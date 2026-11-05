using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
using Jellyfin.Plugin.MetadataRules.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Jellyfin.Plugin.MetadataRules.Controllers;
using MediaBrowser.Model.Tasks;

var directory = Path.Combine(Path.GetTempPath(), "metadata-rules-integration-" + Guid.NewGuid().ToString("N"));
var adapter = new FakeMedia(); var store = new RunStore(directory);
var coordinator = new RunCoordinator(adapter, store, NullLogger<RunCoordinator>.Instance);
await coordinator.StartAsync(default);
var doc = Document(); var settings = new PluginConfiguration { RulesJson = RulesJson.Serialize(doc), Revision = "settings-1" };
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
async Task<RunJob> Run(bool preview = false, RunRequest? request = null)
{
    var job = coordinator.Enqueue(request ?? new(), preview, "Manual", settings);
    await job.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    Check(job.State == "Completed", "job completes: " + job.State + " " + job.Error);
    return job;
}
try
{
    var preview = await Run(true);
    Check(adapter.Writes == 0 && adapter.Refreshes == 0, "preview performs no writes or refreshes");
    Check(preview.Counts.Matched == 1 && preview.Counts.Changed == 1 && preview.Counts.Refresh == 1 && preview.Counts.Affected == 1, "preview counts union of preference and refresh targets");
    var applied = await Run(false, new() { PreviewJobId = preview.Id, ExpectedRevision = settings.Revision });
    Check(adapter.Writes == 1 && adapter.Refreshes == 1, "reviewed preview applies locale then refresh once");
    Check(adapter.Sequence.SequenceEqual(new[] { "write", "refresh" }), "preferences persist before refresh");
    Check(store.Rows(applied.Id)[0].Plan?.DecisionKey == store.Rows(preview.Id)[0].Plan?.DecisionKey, "preview and execution share the same plan");
    await Run();
    Check(adapter.Writes == 1 && adapter.Refreshes == 1, "identical later run does not replace metadata again");
    var changedPreview = await Run(true);
    adapter.Original = "en";
    var changed = await Run(false, new() { PreviewJobId = changedPreview.Id });
    Check(store.Rows(changed.Id)[0].PlanChanged && adapter.Refreshes == 1, "applying stale preview reports changed plan without mutation");
    adapter.Original = "no";
    doc.RuleSets[0].Branches[0].Actions.RefreshMode = "metadataAndImages";
    settings.RulesJson = RulesJson.Serialize(doc);
    adapter.FailRefresh = true;
    var failed = await Run();
    Check(store.Rows(failed.Id)[0].Outcome.Any(x => x.Stage == "Failed") && failed.Counts.Errors == 1, "refresh failure is recorded without aborting the worker");
    adapter.FailRefresh = false;
    await Run();
    var retry = store.Jobs().First();
    Check(adapter.Refreshes == 3 && adapter.Writes == 1, "failed refresh remains retryable when preferences already match");
    Check(retry.Counts.NoOp == 0, "a successful refresh-only action is not counted as a no-op");
    adapter.Locked = true;
    await Run();
    Check(adapter.Refreshes == 3 && adapter.Writes == 1, "locked source has no mutation or refresh");
    adapter.Locked = false;
    var entry = store.Journal(FakeMedia.Id)!;
    Check(entry.Stage == "RefreshAttemptCompleted", "journal records completed refresh attempt separately");
    entry.Stage = "Running"; store.SaveJournal(entry);
    var recovered = new RunStore(directory);
    Check(recovered.Journal(FakeMedia.Id) is { Stage: "Interrupted", UncertainAttempts: 1 }, "restart marks uncertain action interrupted");
    var nightly = new NightlyTask(coordinator).GetDefaultTriggers().Single();
    Check(nightly.Type == TaskTriggerInfoType.DailyTrigger && nightly.TimeOfDayTicks == TimeSpan.FromHours(3).Ticks, "nightly defaults to Jellyfin daily 03:00 trigger");
    var authorization = typeof(MetadataRulesController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
    Check(authorization.Policy == "RequiresElevation", "all controller routes require Jellyfin administrator elevation");
    bool rejected = false;
    try { coordinator.Enqueue(new() { PreviewJobId = preview.Id, ItemIds = [Guid.NewGuid().ToString("N")] }, false, "Manual", settings); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "reviewed candidate scope cannot be broadened");
    var truncated = await Run(true); truncated.ResultsTruncated = true; store.Save(truncated);
    bool truncatedRejected = false;
    try { coordinator.Enqueue(new() { PreviewJobId = truncated.Id }, false, "Manual", settings); } catch (ArgumentException) { truncatedRejected = true; }
    Check(truncatedRejected, "truncated preview cannot be applied");
    var missingReport = await Run(true);
    File.Delete(Path.Combine(directory, "jobs", missingReport.Id + ".jsonl"));
    var missingApplied = await Run(false, new() { PreviewJobId = missingReport.Id });
    Check(store.Rows(missingApplied.Id).Single().PlanChanged, "missing reviewed result never silently authorizes actions");
    doc.RuleSets[0].Branches[0].Actions.WriteScope = "all";
    settings.RulesJson = RulesJson.Serialize(doc); adapter.ItemKind = "Series";
    var beforeNewEpisode = await Run(true); var refreshesBeforeNewEpisode = adapter.Refreshes;
    adapter.ChildCount = 1;
    var scopeChanged = await Run(false, new() { PreviewJobId = beforeNewEpisode.Id });
    Check(store.Rows(scopeChanged.Id).Single().PlanChanged && adapter.Refreshes == refreshesBeforeNewEpisode, "new descendant cannot broaden reviewed refresh/write targets");
    adapter.ChildCount = 0; adapter.ItemKind = "Movie";
    doc.RuleSets[0].Branches[0].Actions.WriteScope = "item";
    doc.RuleSets[0].Branches[0].Actions.RefreshMode = "metadata";
    settings.RulesJson = RulesJson.Serialize(doc); adapter.DelayRefresh = true;
    var cancellable = coordinator.Enqueue(new(), false, "Manual", settings);
    await adapter.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(coordinator.Cancel(cancellable.Id), "running job accepts cancellation");
    await cancellable.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(cancellable.State == "Cancelled" && store.Journal(FakeMedia.Id)?.Stage == "Cancelled", "cancellation preserves partial journal stage and job outcome");
    adapter.DelayRefresh = false;
    await Run();
    Check(store.Journal(FakeMedia.Id)?.Stage == "RefreshAttemptCompleted", "cancelled refresh can be retried");
    var xml = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
    using var writer = new StringWriter(); xml.Serialize(writer, settings);
    var restored = (PluginConfiguration)xml.Deserialize(new StringReader(writer.ToString()))!;
    Check(restored.RulesJson == settings.RulesJson && restored.NightlyEnabled && !restored.InstantEnabled, "XML persistence round-trips nested RulesJson and independent trigger switches");
    var hierarchyMedia = new HierarchyMedia(); var hierarchyPath = Path.Combine(directory, "hierarchy");
    var hierarchyStore = new RunStore(hierarchyPath);
    using var hierarchyWorker = new RunCoordinator(hierarchyMedia, hierarchyStore, NullLogger<RunCoordinator>.Instance);
    await hierarchyWorker.StartAsync(default);
    try
    {
        var hierarchyDoc = Document(); var branch = hierarchyDoc.RuleSets[0].Branches[0];
        branch.Condition = new() { Id = "unset-language", FieldId = "metadata.language", OperatorId = "neq", Value = RuleValue.TextValue("no") };
        branch.Actions.WriteScope = "all"; branch.Actions.RefreshMode = "none";
        var hierarchySettings = new PluginConfiguration { RulesJson = RulesJson.Serialize(hierarchyDoc) };
        var hierarchyJob = hierarchyWorker.Enqueue(new(), false, "Manual", hierarchySettings);
        await hierarchyJob.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(hierarchyJob.State == "Completed" && hierarchyMedia.Written.Count == 3, "source writes do not invalidate the frozen winning branch for descendant writes");
        Check(hierarchyMedia.Written.Distinct().Count() == 3, "expanded source/season/episode preferences write once each");
        Check(hierarchyStore.Journal(HierarchyMedia.SourceId)?.Stage == "Completed", "preference-only journal reaches a terminal completed stage");
        Check(new RunStore(hierarchyPath).Journal(HierarchyMedia.SourceId)?.Stage == "Completed", "restart does not mislabel completed preference-only work as uncertain");
    }
    finally { await hierarchyWorker.StopAsync(default); }
    doc.RuleSets[0].Branches[0].Actions.RefreshMode = "metadataAndImages";
    settings.RulesJson = RulesJson.Serialize(doc); adapter.LocaleConflictOnRefresh = true;
    var conflict = await Run();
    Check(store.Journal(FakeMedia.Id)?.Paused == true && store.Rows(conflict.Id).Single().Outcome.Any(o => o.Stage == "LocaleConflict"), "post-refresh locale conflict is recorded and pauses reconciliation");
    var writesBeforePause = adapter.Writes; var refreshesBeforePause = adapter.Refreshes;
    adapter.LocaleConflictOnRefresh = false;
    var paused = await Run();
    Check(adapter.Writes == writesBeforePause && adapter.Refreshes == refreshesBeforePause && paused.Counts.Skipped == 1, "paused locale conflict does not fight local metadata on later runs");
    var forcePreview = await Run(true);
    await Run(false, new() { PreviewJobId = forcePreview.Id, Force = true });
    Check(store.Journal(FakeMedia.Id)?.Paused == false && adapter.Language == "no", "manual force applies only a reviewed preview and clears the reviewed pause");
    var coalesceStore = new RunStore(Path.Combine(directory, "coalesce")); var coalesceMedia = new FakeMedia();
    using var coalesceWorker = new RunCoordinator(coalesceMedia, coalesceStore, NullLogger<RunCoordinator>.Instance);
    var subset = coalesceWorker.Enqueue(new() { ItemIds = [FakeMedia.Id] }, false, "Manual", settings);
    var full = coalesceWorker.Enqueue(new(), false, "Nightly", settings);
    var duplicateFull = coalesceWorker.Enqueue(new(), false, "FullScan", settings);
    Check(full.Id != subset.Id && duplicateFull.Id == full.Id, "full reconciliation coalesces duplicate full work without being lost behind a subset run");
    await coalesceWorker.StartAsync(default);
    try { await Task.WhenAll(subset.Completion.Task, full.Completion.Task).WaitAsync(TimeSpan.FromSeconds(10)); }
    finally { await coalesceWorker.StopAsync(default); }
    var pressureStore = new RunStore(Path.Combine(directory, "backpressure"));
    using var pressureWorker = new RunCoordinator(new FakeMedia(), pressureStore, NullLogger<RunCoordinator>.Instance);
    var queuedPreviews = Enumerable.Range(0, 16).Select(_ => pressureWorker.Enqueue(new() { ItemIds = [FakeMedia.Id] }, true, "Manual", settings)).ToArray();
    var deferredFull = pressureWorker.Enqueue(new(), false, "FullScan", settings);
    Check(deferredFull.State == "Queued", "a full-scan reconciliation remains queued when the bounded channel is full");
    await pressureWorker.StartAsync(default);
    try
    {
        await Task.WhenAll(queuedPreviews.Select(j => j.Completion.Task).Append(deferredFull.Completion.Task)).WaitAsync(TimeSpan.FromSeconds(15));
        Check(deferredFull.State == "Completed", "deferred full reconciliation executes after queue pressure clears");
    }
    finally { await pressureWorker.StopAsync(default); }
    var missingLanguageMedia = new FakeMedia { OriginalFact = FactValue.Absent() };
    var missingLanguageStore = new RunStore(Path.Combine(directory, "missing-original-language"));
    using var missingLanguageWorker = new RunCoordinator(missingLanguageMedia, missingLanguageStore, NullLogger<RunCoordinator>.Instance);
    var missingLanguageRules = Document();
    missingLanguageRules.RuleSets.Add(new() { Id = "fallback", Name = "Other movies", Order = 1, Branches = [new() { Id = "fallback-movie", Condition = new() { Id = "fallback-type", FieldId = "item.kind", OperatorId = "eq", Value = RuleValue.TextValue("Movie") }, Actions = new() { Language = new() { Mode = "set", Value = "en" }, Country = new() { Mode = "set", Value = "US" }, RefreshMode = "metadata" } }] });
    var missingLanguageSettings = new PluginConfiguration { RulesJson = RulesJson.Serialize(missingLanguageRules), Revision = "missing-original-language" };
    await missingLanguageWorker.StartAsync(default);
    try
    {
        var missingPreview = missingLanguageWorker.Enqueue(new(), true, "Manual", missingLanguageSettings);
        await missingPreview.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var previewRow = missingLanguageStore.Rows(missingPreview.Id).Single();
        Check(missingPreview.State == "Completed" && missingPreview.Counts.Deferred == 0 && previewRow.Plan?.Evaluation.RuleSetId == "fallback", "confirmed missing original language falls through to a later ruleset in preview");
        Check(previewRow.Plan!.Evaluation.Trace.First().Condition is { Result: TruthValue.False, Availability: FactState.Absent }, "missing original-language trace records False and Absent rather than Unknown");
        Check(missingLanguageMedia.Writes == 0 && missingLanguageMedia.Refreshes == 0, "missing-language fallthrough preview remains read-only");
        var missingOriginalApplied = missingLanguageWorker.Enqueue(new() { PreviewJobId = missingPreview.Id, ExpectedRevision = missingLanguageSettings.Revision }, false, "Manual", missingLanguageSettings);
        await missingOriginalApplied.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(missingOriginalApplied.State == "Completed" && missingLanguageMedia.Language == "en" && missingLanguageMedia.Country == "US" && missingLanguageMedia.Writes == 1 && missingLanguageMedia.Refreshes == 1, "applying the preview executes the later rule when original language is confirmed absent");
        missingLanguageMedia.OriginalFact = FactValue.NotReady();
        var unfinishedPreview = missingLanguageWorker.Enqueue(new(), true, "Manual", missingLanguageSettings);
        await unfinishedPreview.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(unfinishedPreview.Counts.Deferred == 1 && missingLanguageStore.Rows(unfinishedPreview.Id).Single().Plan?.Evaluation.Trace.All(t => t.RuleSetId != "fallback") == true, "unfinished original language still defers instead of applying a lower-priority rule");
    }
    finally { await missingLanguageWorker.StopAsync(default); }
    Console.WriteLine($"{checks} integration assertions passed.");
}
finally { await coordinator.StopAsync(default); coordinator.Dispose(); Directory.Delete(directory, true); }

static RuleDocument Document() => new() { RuleSets = [new() { Id = "norwegian", Name = "Norwegian original", Branches = [new() { Id = "original", Condition = new() { Id = "original-test", FieldId = "item.originalLanguage", OperatorId = "eq", Value = RuleValue.TextValue("norwegian") }, Actions = new() { Language = new() { Mode = "set", Value = "no" }, Country = new() { Mode = "set", Value = "NO" }, RefreshMode = "metadata" } }] }] };
sealed class FakeMedia : IMediaAdapter
{
    public const string Id = "00000000000000000000000000000001";
    public bool IsScanRunning => false;
    public int Writes, Refreshes; public bool FailRefresh, Locked, DelayRefresh, LocaleConflictOnRefresh;
    public string ItemKind = "Movie"; public int ChildCount;
    public TaskCompletionSource RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Original = "no"; public FactValue? OriginalFact; public string? Language, Country;
    public List<string> Sequence = [];
    public IReadOnlyList<string> Candidates(PluginConfiguration config, RuleDocument rules) => [Id];
    public MediaFacts? Read(string id, RuleDocument rules, DateTimeOffset timestamp, bool fresh = false)
    {
        var facts = new MediaFacts()
    {
        ItemId = Id, Title = "Example Film", ItemKind = ItemKind, LibraryId = "00000000000000000000000000000002", Timestamp = timestamp,
        Fields = new Dictionary<string, FactValue> { ["item.originalLanguage"] = OriginalFact ?? FactValue.Known(Original), ["metadata.language"] = FactValue.Known(Language ?? ""), ["metadata.country"] = FactValue.Known(Country ?? ""), ["metadata.effectiveLanguage"] = FactValue.Known(Language ?? "en"), ["metadata.effectiveCountry"] = FactValue.Known(Country ?? "US"), ["metadata.inheritedLanguage"] = FactValue.Known("en"), ["metadata.inheritedCountry"] = FactValue.Known("US"), ["state.locked"] = FactValue.Known(Locked), ["state.virtual"] = FactValue.Known(false) }
    };
        return facts with { Descendants = Enumerable.Range(0, ChildCount).Select(i => facts with { ItemId = "000000000000000000000000000000" + (i+10).ToString("00"), Title = "Episode " + i, ItemKind = "Episode", ParentId = Id, Descendants = [] }).ToArray() };
    }
    public Task SavePreferences(string id, bool language, string? languageValue, bool country, string? countryValue, CancellationToken token)
    { Writes++; Sequence.Add("write"); if (language) Language = languageValue; if (country) Country = countryValue; return Task.CompletedTask; }
    public async Task Refresh(string id, string mode, bool automated, CancellationToken token)
    { Refreshes++; Sequence.Add("refresh"); if (FailRefresh) throw new InvalidOperationException("Synthetic provider failure"); if (DelayRefresh) { RefreshStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } if (LocaleConflictOnRefresh) Language = "sv"; }
}

sealed class HierarchyMedia : IMediaAdapter
{
    public const string SourceId = "00000000000000000000000000000005";
    private const string SeasonId = "00000000000000000000000000000006", EpisodeId = "00000000000000000000000000000007";
    private readonly Dictionary<string, string?> _languages = new() { [SourceId] = "en", [SeasonId] = null, [EpisodeId] = null };
    private readonly Dictionary<string, string?> _countries = new() { [SourceId] = "US", [SeasonId] = null, [EpisodeId] = null };
    public List<string> Written = [];
    public bool IsScanRunning => false;
    public IReadOnlyList<string> Candidates(PluginConfiguration config, RuleDocument rules) => [SourceId];
    public MediaFacts Read(string id, RuleDocument rules, DateTimeOffset timestamp, bool fresh = false)
    {
        var parent = id == SourceId ? null : id == SeasonId ? SourceId : SeasonId;
        var facts = new MediaFacts { ItemId = id, Title = id == SourceId ? "Show" : id == SeasonId ? "Season" : "Episode", ItemKind = id == SourceId ? "Series" : id == SeasonId ? "Season" : "Episode", ParentId = parent, Timestamp = timestamp,
            Fields = new Dictionary<string, FactValue> { ["metadata.language"] = FactValue.Known(_languages[id] ?? ""), ["metadata.country"] = FactValue.Known(_countries[id] ?? ""), ["metadata.effectiveLanguage"] = FactValue.Known(_languages[id] ?? _languages[SourceId]), ["metadata.effectiveCountry"] = FactValue.Known(_countries[id] ?? _countries[SourceId]), ["metadata.inheritedLanguage"] = FactValue.Known(parent is null ? "en" : _languages[parent] ?? _languages[SourceId]), ["metadata.inheritedCountry"] = FactValue.Known("US"), ["state.locked"] = FactValue.Known(false), ["state.virtual"] = FactValue.Known(false) } };
        return id == SourceId ? facts with { Descendants = [Read(SeasonId, rules, timestamp), Read(EpisodeId, rules, timestamp)] } : facts;
    }
    public Task SavePreferences(string id, bool language, string? languageValue, bool country, string? countryValue, CancellationToken token)
    { if (language) _languages[id] = languageValue; if (country) _countries[id] = countryValue; Written.Add(id); return Task.CompletedTask; }
    public Task Refresh(string id, string mode, bool automated, CancellationToken token) => throw new Exception("Preference-only rules must not refresh.");
}
