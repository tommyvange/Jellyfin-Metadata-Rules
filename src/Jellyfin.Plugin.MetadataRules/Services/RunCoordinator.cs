using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataRules.Services;

/// <summary>A single bounded worker serializes every trigger and every item write/refresh.</summary>
public sealed class RunCoordinator(IMediaAdapter media, RunStore store, ILogger<RunCoordinator> logger) : BackgroundService
{
    private readonly object _enqueueGate = new();
    private readonly Channel<RunJob> _queue = Channel.CreateBounded<RunJob>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, byte> _mutating = new();
    private RunJob? _deferredReconciliation;
    public bool IsMutating(string id) => _mutating.ContainsKey(id);
    public RunJob Enqueue(RunRequest request, bool preview, string trigger = "Manual")
        => Enqueue(request, preview, trigger, Plugin.Instance!.Settings);
    internal RunJob Enqueue(RunRequest request, bool preview, string trigger, PluginConfiguration settings)
    {
        lock (_enqueueGate)
        {
            if (!preview && !settings.Enabled) throw new InvalidOperationException("Metadata Rules is disabled. Preview is still available.");
            if (request.ExpectedRevision is not null && request.ExpectedRevision != settings.Revision) throw new InvalidOperationException("Configuration changed. Reload and preview again.");
            RunJob? reviewed = null;
            if (request.PreviewJobId is not null)
            {
                reviewed = store.Get(request.PreviewJobId) ?? throw new ArgumentException("Preview was not found or has expired.");
                if (reviewed.ResultsTruncated) throw new ArgumentException("This preview report was truncated. Narrow the scope and preview again before applying it.");
                if (!reviewed.DryRun || reviewed.State != "Completed") throw new ArgumentException("Only a completed preview can be applied.");
                if (request.ItemIds is not null && request.ItemIds.Except(reviewed.CandidateIds, StringComparer.OrdinalIgnoreCase).Any()) throw new ArgumentException("Cannot broaden a reviewed scope.");
                if (reviewed.Settings.Revision != settings.Revision) throw new InvalidOperationException("Configuration changed after this preview. Preview again.");
            }
            if (request.Force && (reviewed is null || preview)) throw new ArgumentException("Force requires a specific completed preview.");
            var json = reviewed?.RulesJson ?? request.RulesJson ?? settings.RulesJson;
            var document = RuleDocuments.Parse(json, Plugin.Instance?.Localization);
            if (reviewed is not null && request.RulesJson is not null && RuleDocuments.Parse(request.RulesJson, Plugin.Instance?.Localization).Revision != reviewed.Revision) throw new InvalidOperationException("Draft changed after preview. Preview it again.");
            if (trigger != "Manual" && request.ItemIds is null)
            {
                var existing = store.Jobs().FirstOrDefault(j => !j.DryRun && j.State is "Queued" or "Running" && j.FullReconciliation && j.Revision == document.Revision && j.Settings.Revision == settings.Revision);
                if (existing is not null) return existing;
            }
            var enumerateOnStart = trigger != "Manual" && request.ItemIds is null && reviewed is null;
            var ids = request.ItemIds ?? reviewed?.CandidateIds ?? (enumerateOnStart ? [] : media.Candidates(settings, document).ToArray());
            if (ids.Length > 100000) throw new ArgumentException("A run supports at most 100,000 candidates; narrow its library scope.");
            if (ids.Any(x => !Guid.TryParse(x, out _))) throw new ArgumentException("Item IDs must be UUIDs.");
            ids = ids.Select(x => Guid.Parse(x).ToString("N")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var job = new RunJob { Trigger = trigger, DryRun = preview, RulesJson = RulesJson.Serialize(document), Revision = document.Revision, CandidateIds = ids, Total = ids.Length, EnumerateOnStart = enumerateOnStart, FullReconciliation = reviewed?.FullReconciliation ?? request.ItemIds is null, Settings = settings, Force = request.Force, PreviewJobId = reviewed?.Id };
            if (!_queue.Writer.TryWrite(job))
            {
                if (trigger != "Manual" && request.ItemIds is null && _deferredReconciliation is null) _deferredReconciliation = job;
                else throw new InvalidOperationException("The run queue is full. Wait for a current run to finish.");
            }
            store.Save(job); return job;
        }
    }
    public bool Cancel(string id) { var job = store.Get(id); if (job is null || job.FinishedAt is not null) return false; job.Cancellation.Cancel(); return true; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
                try { await Process(job, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { job.State = stoppingToken.IsCancellationRequested ? "Interrupted" : "Cancelled"; }
                catch (Exception ex) { job.State = "Failed"; job.Error = ex.Message; logger.LogError(ex, "Metadata Rules run {Id} failed", job.Id); }
                finally
                {
                    job.FinishedAt = DateTimeOffset.UtcNow; store.Save(job); job.Completion.TrySetResult();
                    lock (_enqueueGate) { if (_deferredReconciliation is { } deferred && _queue.Writer.TryWrite(deferred)) _deferredReconciliation = null; }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_enqueueGate)
            {
                if (_deferredReconciliation is { } deferred) { deferred.State = "Interrupted"; deferred.FinishedAt = DateTimeOffset.UtcNow; store.Save(deferred); deferred.Completion.TrySetResult(); _deferredReconciliation = null; }
            }
            while (_queue.Reader.TryRead(out var queued)) { queued.State = "Interrupted"; queued.FinishedAt = DateTimeOffset.UtcNow; store.Save(queued); queued.Completion.TrySetResult(); }
        }
    }
    private async Task WaitForScan(CancellationToken token)
    {
        while (media.IsScanRunning) await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
    }
    private static bool Excluded(MediaFacts facts, PluginConfiguration settings)
        => settings.ExcludedItemIds.Contains(facts.ItemId, StringComparer.OrdinalIgnoreCase)
        || settings.LibraryIds.Length > 0 && !settings.LibraryIds.Contains(facts.LibraryId, StringComparer.OrdinalIgnoreCase)
        || facts.Get("state.virtual").Value is true || facts.Get("state.extra").Value is true;
    private async Task Process(RunJob job, CancellationToken token)
    {
        var document = RuleDocuments.Parse(job.RulesJson, Plugin.Instance?.Localization);
        var planner = new ActionPlanner();
        if (job.EnumerateOnStart)
        {
            await WaitForScan(token).ConfigureAwait(false);
            job.CandidateIds = media.Candidates(job.Settings, document).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (job.CandidateIds.Length > 100000) throw new InvalidOperationException("More than 100,000 candidates; narrow the library scope.");
            job.Total = job.CandidateIds.Length; job.EnumerateOnStart = false;
        }
        job.CountsByRule = document.RuleSets.Select(r => new RuleRunCounts { RuleSetId = r.Id, RuleSetName = r.Name }).ToList();
        job.State = "Running"; store.Save(job);
        var changed = new HashSet<string>(); var refreshed = new HashSet<string>(); var inherited = new HashSet<string>();
        var alreadyTargeted = new HashSet<string>();
        var reviewedRows = job.PreviewJobId is null ? new Dictionary<string, RunRow>() : store.Rows(job.PreviewJobId).ToDictionary(r => r.ItemId);
        foreach (var id in job.CandidateIds)
        {
            token.ThrowIfCancellationRequested();
            if (!job.DryRun && Plugin.Instance?.Settings.Enabled is false) throw new OperationCanceledException("Metadata Rules was disabled.");
            if (!job.DryRun) await WaitForScan(token).ConfigureAwait(false);
            var row = new RunRow { ItemId = id, Revision = job.Revision, FactsTimestamp = job.StartedAt };
            try
            {
                var facts = ReadFacts(job, id, document);
                if (facts is null) { row.Status = "Skipped"; row.Reason = "Item was removed."; }
                else
                {
                    row.FactsTimestamp = facts.Timestamp; row.Title = facts.Title; row.ItemKind = facts.ItemKind; row.LibraryId = facts.LibraryId;
                    if (Excluded(facts, job.Settings)) { row.Status = "Skipped"; row.Reason = "Excluded, virtual, or outside the selected libraries."; }
                    else
                    {
                        row.Plan = planner.Plan(document, facts, job.StartedAt);
                        row.Status = row.Plan.Evaluation.Status; row.Reason = row.Plan.Evaluation.Reason;
                        if (job.PreviewJobId is not null && (!reviewedRows.TryGetValue(id, out var old) || PlanSignature(old.Plan) != PlanSignature(row.Plan))) { row.PlanChanged = true; row.Status = "PlanChanged"; row.Reason = "Current facts differ from the reviewed preview; no actions were applied. Preview again."; }
                        if (row.Status == "Matched")
                        {
                            job.Counts.Matched++;
                            bool hasPlannedWork = false;
                            bool decisionChecked = false;
                            foreach (var target in row.Plan.Targets.Where(t => !t.SkippedLocked && !job.Settings.ExcludedItemIds.Contains(t.ItemId, StringComparer.OrdinalIgnoreCase)))
                            {
                                var policy = row.Plan.Evaluation.Actions!;
                                var entry = store.Journal(target.ItemId);
                                if (!job.Force && (entry?.Paused is true || entry?.Key == target.DecisionKey && entry.UncertainAttempts > 1))
                                {
                                    row.Reason = entry.Message ?? "Repeated uncertain refresh outcomes require a reviewed manual force before another replacement attempt.";
                                    row.Outcome.Add(new(target.ItemId, "Paused", row.Reason)); continue;
                                }
                                var performRefresh = ShouldRefresh(target, policy, entry, job);
                                hasPlannedWork |= target.PreferenceChanged || performRefresh;
                                if (target.PreferenceChanged) changed.Add(target.ItemId);
                                if (performRefresh) refreshed.Add(target.ItemId);
                                if (!job.DryRun && (target.PreferenceChanged || performRefresh) && alreadyTargeted.Add(target.ItemId))
                                {
                                    var outcomes = await Apply(job, document, row.Plan, target, performRefresh, entry, !decisionChecked, token).ConfigureAwait(false);
                                    decisionChecked = true; row.Outcome.AddRange(outcomes);
                                    if (outcomes.Any(o => o.Stage == "PlanChanged")) { row.Status = "PlanChanged"; row.Reason = "Facts changed before an action; remaining targets were not applied. Review the recorded partial outcomes and preview again."; break; }
                                }
                            }
                            if (row.Outcome.Any(o => o.Stage == "Failed")) { row.Status = "Error"; row.Reason = string.Join("; ", row.Outcome.Where(o => o.Stage == "Failed").Select(o => o.Message)); }
                            foreach (var effect in row.Plan.InheritedEffects) inherited.Add(effect.ItemId);
                            if (!hasPlannedWork && row.Outcome.Any(o => o.Stage == "Paused")) row.Status = "Skipped";
                            if (!hasPlannedWork && row.Status == "Matched") { job.Counts.NoOp++; row.Reason ??= "Preferences already match; repeated replacement is suppressed by the journal."; }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { row.Status = "Cancelled"; row.Reason = "Run cancelled; completed action stages are journaled."; throw; }
            catch (Exception ex) { row.Status = "Error"; row.Reason = ex.Message; logger.LogWarning(ex, "Metadata Rules could not process {ItemId}", id); }
            finally
            {
                job.Processed++; job.Counts.Examined++;
                if (row.Status == "Skipped") job.Counts.Skipped++; if (row.Status == "Deferred") job.Counts.Deferred++; if (row.Status == "Error") job.Counts.Errors++;
                job.Counts.Changed = changed.Count; job.Counts.Refresh = refreshed.Count; job.Counts.Inherited = inherited.Count; job.Counts.Affected = changed.Union(refreshed).Count();
                store.AddRow(job, row);
                // Checkpoint progress at bounded intervals; action stages are persisted synchronously per target.
                if (row.Plan?.Evaluation.RuleSetId is { } ruleId && job.CountsByRule.FirstOrDefault(r => r.RuleSetId == ruleId) is { } ruleCounts)
                {
                    if (row.Plan.Evaluation.Status == "Matched") { ruleCounts.Matched++; ruleCounts.Changed += row.Plan.ChangedCount; ruleCounts.Refresh += row.Plan.RefreshCount; }
                    if (row.Status == "Deferred") ruleCounts.Deferred++; if (row.Status == "Error") ruleCounts.Errors++;
                }
                if (job.Processed % 25 == 0 || job.Processed == job.Total) store.Save(job);
            }
        }
        job.State = "Completed";
    }
    private static bool ShouldRefresh(PlannedTarget target, RuleActions policy, JournalEntry? entry, RunJob job)
    {
        if (!target.Refresh || target.SkippedLocked) return false;
        if (job.Force) return true;
        if (entry?.Paused is true || entry?.Key == target.DecisionKey && entry.UncertainAttempts > 1) return false;
        if (policy.RepeatPolicy == "preferenceChange") return target.PreferenceChanged || entry?.Key == target.DecisionKey && entry.Stage is "Failed" or "Interrupted" or "PreferencesApplied" or "Cancelled";
        if (policy.RepeatPolicy == "everyRun") return entry?.LastRefreshAt is null || DateTimeOffset.UtcNow - entry.LastRefreshAt >= TimeSpan.FromMinutes(Math.Max(policy.MinimumIntervalMinutes, job.Settings.MinimumRefreshIntervalMinutes));
        return entry is null || entry.Key != target.DecisionKey || entry.Stage != "RefreshAttemptCompleted";
    }
    private async Task<IReadOnlyList<ActionOutcome>> Apply(RunJob job, RuleDocument document, ActionPlan plan, PlannedTarget target, bool refresh, JournalEntry? entry, bool recheckDecision, CancellationToken token)
    {
        var outcomes = new List<ActionOutcome>();
        // The global worker is also the per-item gate. No independent writer can run in this plugin.
        await WaitForScan(token).ConfigureAwait(false);
        // Re-evaluate once immediately before this source's first mutation. The chosen branch is then
        // frozen for its exact targets: our own source write/refresh must not change later child decisions.
        if (recheckDecision)
        {
            var currentSource = ReadFacts(job, plan.ItemId, document);
            if (currentSource is null || Excluded(currentSource, job.Settings)) return [new(target.ItemId, "PlanChanged", "Source was removed or left scope.")];
            var currentPlan = new ActionPlanner().Plan(document, currentSource, job.StartedAt);
            if (PlanSignature(currentPlan) != PlanSignature(plan)) return [new(target.ItemId, "PlanChanged", "Source facts changed before persistence.")];
        }
        var currentTarget = ReadFacts(job, target.ItemId, document);
        if (currentTarget is null || Excluded(currentTarget, job.Settings) || currentTarget.Get("state.locked").Value is true)
            return [new(target.ItemId, "Skipped", "Target was removed, excluded, or locked before persistence.")];
        var currentPreferences = ActionPlanner.Preferences(currentTarget);
        if (!Same(currentPreferences.Language, target.Before.Language) || !Same(currentPreferences.Country, target.Before.Country))
            return [new(target.ItemId, "PlanChanged", "The target's explicit preferences changed outside this action plan.")];
        entry ??= new JournalEntry { ItemId = target.ItemId };
        if (entry.Paused && !job.Force) return [new(target.ItemId, "Paused", entry.Message)];
        var locale = (target.After.Language ?? "<inherit>") + "/" + (target.After.Country ?? "<inherit>");
        if ((target.PreferenceChanged || refresh) && entry.Transitions.TakeLast(3).Any(x => x.Locale == locale && x.BranchId == plan.Evaluation.BranchId) && entry.Transitions.LastOrDefault() is { } last && (last.Locale != locale || last.BranchId != plan.Evaluation.BranchId) && !job.Force)
        {
            entry.Paused = true; entry.Message = "A repeating locale or winning-branch cycle was detected. Review the conflicting branches before a manual force."; store.SaveJournal(entry); return [new(target.ItemId, "Paused", entry.Message)];
        }
        if (entry.Key != target.DecisionKey) entry.UncertainAttempts = 0;
        entry.Key = target.DecisionKey; entry.Stage = "Pending"; entry.Message = null; if (job.Force) { entry.Paused = false; entry.UncertainAttempts = 0; }
        store.SaveJournal(entry);
        _mutating[target.ItemId] = 1;
        try
        {
            entry.Stage = "Running"; store.SaveJournal(entry);
            if (target.PreferenceChanged)
            {
                await media.SavePreferences(target.ItemId, target.WriteLanguage, target.After.Language, target.WriteCountry, target.After.Country, token).ConfigureAwait(false);
            }
            if (entry.Transitions.LastOrDefault() is not { } previous || previous.Locale != locale || previous.BranchId != plan.Evaluation.BranchId)
            { entry.Transitions.Add(new(locale, plan.Evaluation.BranchId, DateTimeOffset.UtcNow)); entry.Transitions = entry.Transitions.TakeLast(6).ToList(); }
            entry.Stage = "PreferencesApplied"; store.SaveJournal(entry); outcomes.Add(new(target.ItemId, entry.Stage));
            if (refresh)
            {
                token.ThrowIfCancellationRequested(); await WaitForScan(token).ConfigureAwait(false);
                await media.Refresh(target.ItemId, target.RefreshMode, job.Trigger != "Manual", token).ConfigureAwait(false);
                entry.LastRefreshAt = DateTimeOffset.UtcNow; entry.Stage = "RefreshAttemptCompleted";
                outcomes.Add(new(target.ItemId, entry.Stage, "The single-item refresh attempt returned. Provider success and localized content are not guaranteed."));
            }
            if (!refresh) { entry.Stage = "Completed"; outcomes.Add(new(target.ItemId, "Completed", "Preference-only action completed.")); }
            var after = ReadFacts(job, target.ItemId, document);
            if (after is not null && ((target.WriteLanguage || refresh) && (!Same(after.Get("metadata.language").Value, target.After.Language) || !Same(after.Get("metadata.effectiveLanguage").Value, target.After.EffectiveLanguage)) || (target.WriteCountry || refresh) && (!Same(after.Get("metadata.country").Value, target.After.Country) || !Same(after.Get("metadata.effectiveCountry").Value, target.After.EffectiveCountry))))
            { entry.Paused = true; entry.Message = "Stored locale conflicts with the requested preferences after persistence/refresh. Check local NFO metadata and provider configuration."; outcomes.Add(new(target.ItemId, "LocaleConflict", entry.Message)); }
            store.SaveJournal(entry);
        }
        catch (OperationCanceledException) { entry.Stage = "Cancelled"; entry.Message = "Action was cancelled; refresh outcome may be partial."; store.SaveJournal(entry); throw; }
        catch (Exception ex) { entry.Stage = "Failed"; entry.Message = ex.Message; store.SaveJournal(entry); outcomes.Add(new(target.ItemId, "Failed", ex.Message)); }
        finally { _mutating.TryRemove(target.ItemId, out _); }
        return outcomes;
    }
    private MediaFacts? ReadFacts(RunJob job, string id, RuleDocument document)
    {
        var facts = media.Read(id, document, DateTimeOffset.UtcNow, true);
        MediaFacts Pin(MediaFacts value) => value with
        {
            PathCaseSensitive = job.Settings.PathCaseSensitive,
            Collections = value.Collections.ToDictionary(pair => pair.Key, pair => pair.Value with { Elements = pair.Value.Elements.Select(Pin).ToArray() }),
            Descendants = value.Descendants.Select(Pin).ToArray()
        };
        return facts is null ? null : Pin(facts);
    }
    private static string PlanSignature(ActionPlan? plan) => plan is null ? "<none>" : System.Text.Json.JsonSerializer.Serialize(new { plan.DecisionKey, Targets = plan.Targets.OrderBy(t => t.ItemId), Effects = plan.InheritedEffects.OrderBy(e => e.ItemId) }, RunStore.JsonOptions);
    private static bool Same(object? actual, string? expected) => string.Equals(Convert.ToString(actual) ?? "", expected ?? "", StringComparison.OrdinalIgnoreCase);
}
