using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.MetadataRules.Engine;

/// <summary>Pure action expansion. It neither reads Jellyfin nor invokes persistence/refresh APIs.</summary>
public sealed class ActionPlanner
{
    private readonly RuleEvaluator _evaluator;
    public ActionPlanner(RuleEvaluator? evaluator = null) => _evaluator = evaluator ?? new();
    public ActionPlan Plan(RuleDocument document, MediaFacts facts, DateTimeOffset runTimestamp)
    {
        var revision = RuleRevision.Compute(document);
        var evaluation = _evaluator.Evaluate(document, facts, runTimestamp);
        var action = evaluation.Actions;
        var winningRule = document.RuleSets.FirstOrDefault(x => x.Id == evaluation.RuleSetId);
        var winningBranch = winningRule?.Branches.FirstOrDefault(x => x.Id == evaluation.BranchId);
        // Review revisions cover the whole document. Durable action decisions cover the winning
        // rule/branch and evaluated priority evidence, so unrelated edits never replace media again.
        var decisionKey = Hash(new
        {
            facts.ItemId, evaluation.RuleSetId, evaluation.BranchId, Scope = winningRule?.Scope,
            Condition = winningBranch?.Condition, IsElse = winningBranch?.IsElse,
            Inputs = InputEvidence(evaluation.Trace), Action = action
        });
        if (evaluation.Status != "Matched" || action is null) return new() { ItemId = facts.ItemId, RuleRevision = revision, FactsTimestamp = facts.Timestamp, Evaluation = evaluation, DecisionKey = decisionKey };
        if (Locked(facts)) return new()
        {
            ItemId = facts.ItemId, RuleRevision = revision, FactsTimestamp = facts.Timestamp,
            Evaluation = evaluation with { Status = "Skipped", Reason = "The matched item is locked; no preferences or refresh actions are planned." }, DecisionKey = decisionKey,
            Targets = [new() { ItemId = facts.ItemId, Title = facts.Title, ItemKind = facts.ItemKind, IsSource = true, Before = Preferences(facts), After = Preferences(facts), SkippedLocked = true, Reason = "Metadata is locked.", DecisionKey = decisionKey }]
        };
        var all = new[] { facts }.Concat(Flatten(facts.Descendants)).DistinctBy(x => x.ItemId).ToArray();
        var before = all.ToDictionary(x => x.ItemId, Preferences, StringComparer.Ordinal);
        var after = new Dictionary<string, PreferenceSnapshot>(before, StringComparer.Ordinal);
        var targets = new List<PlannedTarget>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in all)
        {
            bool source = item.ItemId == facts.ItemId;
            bool write = source || Includes(action.WriteScope, item.ItemKind);
            bool refresh = action.RefreshMode != "none" && (source || Includes(action.RefreshScope, item.ItemKind));
            if (!write && !refresh) continue;
            selected.Add(item.ItemId);
            var old = before[item.ItemId]; var proposed = old;
            bool locked = Locked(item);
            if (!locked && write)
            {
                var language = Apply(old.Language, action.Language, source || action.OverwriteDescendantOverrides, true);
                var country = Apply(old.Country, action.Country, source || action.OverwriteDescendantOverrides, false);
                proposed = old with { Language = language, Country = country };
                after[item.ItemId] = proposed;
            }
            targets.Add(new()
            {
                ItemId = item.ItemId, Title = item.Title, ItemKind = item.ItemKind, IsSource = source, Before = old, After = proposed,
                WriteLanguage = !locked && !Same(old.Language, proposed.Language), WriteCountry = !locked && !Same(old.Country, proposed.Country),
                RefreshMode = !locked && refresh ? action.RefreshMode : "none", SkippedLocked = locked,
                Reason = locked ? "Metadata is locked; preferences and refresh are skipped." : !source && !action.OverwriteDescendantOverrides && (old.Language is not null || old.Country is not null) ? "Existing descendant overrides are preserved independently for language and country." : null,
                DecisionKey = Hash(new { SourceDecision = decisionKey, Target = item.ItemId, DesiredLanguage = proposed.Language, DesiredCountry = proposed.Country, action.RefreshMode, action.WriteScope, action.RefreshScope })
            });
        }
        // Resolve effective locales through the complete detached parent tree, including unmodified intermediary seasons.
        var byId = all.ToDictionary(x => x.ItemId, StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        PreferenceSnapshot Resolve(MediaFacts item)
        {
            if (resolved.Contains(item.ItemId)) return after[item.ItemId];
            if (!resolving.Add(item.ItemId)) return before[item.ItemId];
            var current = after[item.ItemId];
            PreferenceSnapshot? parent = item.ParentId is not null && byId.TryGetValue(item.ParentId, out var parentFacts) ? Resolve(parentFacts) : null;
            var inheritedLanguage = parent?.EffectiveLanguage ?? Text(item, "metadata.inheritedLanguage") ?? (before[item.ItemId].Language is null ? before[item.ItemId].EffectiveLanguage : null);
            var inheritedCountry = parent?.EffectiveCountry ?? Text(item, "metadata.inheritedCountry") ?? (before[item.ItemId].Country is null ? before[item.ItemId].EffectiveCountry : null);
            var result = current with { EffectiveLanguage = current.Language ?? inheritedLanguage, EffectiveCountry = current.Country ?? inheritedCountry };
            after[item.ItemId] = result; resolving.Remove(item.ItemId); resolved.Add(item.ItemId); return result;
        }
        foreach (var item in all) Resolve(item);
        targets = targets.Select(x => x with { After = after[x.ItemId] }).ToList();
        var inherited = all.Where(x => x.ItemId != facts.ItemId).Where(x =>
            !Same(before[x.ItemId].EffectiveLanguage, after[x.ItemId].EffectiveLanguage) || !Same(before[x.ItemId].EffectiveCountry, after[x.ItemId].EffectiveCountry))
            .Where(x => targets.All(t => t.ItemId != x.ItemId || !t.PreferenceChanged))
            .Select(x => new InheritedEffect(x.ItemId, x.Title, before[x.ItemId], after[x.ItemId])).ToArray();
        return new() { ItemId = facts.ItemId, RuleRevision = revision, FactsTimestamp = facts.Timestamp, Evaluation = evaluation, Targets = targets, InheritedEffects = inherited, DecisionKey = decisionKey };
    }
    private static bool Includes(string scope, string kind) => scope == "all" || scope == "seasons" && kind == "Season" || scope == "episodes" && kind == "Episode";
    private static IEnumerable<MediaFacts> Flatten(IEnumerable<MediaFacts> descendants)
    {
        foreach (var item in descendants)
        {
            yield return item;
            foreach (var child in Flatten(item.Descendants)) yield return child;
        }
    }
    private static bool Locked(MediaFacts facts) => FactConversion.Boolean(facts.Get("state.locked").Value) == true;
    private static string? Text(MediaFacts facts, string field)
    {
        var fact = facts.Get(field); return fact.State == FactState.Known ? EmptyToNull(FactConversion.Text(fact.Value)) : null;
    }
    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private static bool Same(string? a, string? b) => string.Equals(EmptyToNull(a), EmptyToNull(b), StringComparison.OrdinalIgnoreCase);
    public static PreferenceSnapshot Preferences(MediaFacts facts) => new(Text(facts, "metadata.language"), Text(facts, "metadata.country"), Text(facts, "metadata.effectiveLanguage"), Text(facts, "metadata.effectiveCountry"));
    private static string? Apply(string? old, PreferenceAction action, bool overwrite, bool language)
    {
        if (action.Mode == "leave" || !overwrite && old is not null) return old;
        if (action.Mode == "clear") return null;
        return action.Mode == "set" ? language ? LanguageNormalizer.Normalize(action.Value) : action.Value?.ToUpperInvariant() : old;
    }
    private static object[] InputEvidence(IReadOnlyList<BranchTrace> branches)
    {
        var list = new List<object>();
        void Trace(ConditionTrace trace)
        {
            if (!trace.Evaluated) return;
            // Only evaluated predicates/collection coverage affect a decision; exclude snapshot and execution timestamps.
            if (trace.Kind == "Predicate") list.Add(new { trace.NodeId, trace.FieldId, trace.OperatorId, trace.Expected, Actual = CanonicalActual(trace.Actual), trace.Availability, trace.EvidenceItemId, trace.StreamIndex });
            if (trace.Kind == "Quantifier") list.Add(new { trace.NodeId, trace.FieldId, trace.Actual, trace.Availability, trace.TotalCount, trace.UnknownCount, trace.EvidenceItemId });
            var children = trace.Kind == "Quantifier"
                ? trace.Children.OrderBy(x => x.EvidenceItemId, StringComparer.Ordinal).ThenBy(x => x.StreamIndex).ThenBy(x => x.NodeId, StringComparer.Ordinal)
                : trace.Children.AsEnumerable();
            foreach (var child in children) Trace(child);
        }
        foreach (var branch in branches.Where(x => x.Evaluated))
        {
            list.Add(new { branch.RuleSetId, branch.BranchId, branch.Result });
            if (branch.Condition is not null) Trace(branch.Condition);
        }
        return list.ToArray();
    }
    private static object? CanonicalActual(object? actual)
    {
        // Source model sets have membership semantics; repository row order is not a new input.
        // Actual arrays are set fields in the registry. Scalar numbers retain their native value.
        if (actual is Array or IEnumerable<string> && FactConversion.Strings(actual) is { } values)
            return values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal).ToArray();
        return actual;
    }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, RulesJson.Options)))).ToLowerInvariant();
}
