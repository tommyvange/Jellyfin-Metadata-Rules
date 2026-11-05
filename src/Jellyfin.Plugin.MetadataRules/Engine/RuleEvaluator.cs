using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.MetadataRules.Engine;

public sealed class RuleEvaluator
{
    private readonly FieldRegistry _registry;
    public RuleEvaluator(FieldRegistry? registry = null) => _registry = registry ?? new();

    public EvaluationResult Evaluate(RuleDocument document, MediaFacts facts, DateTimeOffset runTimestamp)
    {
        var traces = new List<BranchTrace>();
        foreach (var rule in document.RuleSets.OrderBy(x => x.Order))
        {
            if (!rule.Enabled || !InScope(rule.Scope, facts)) continue;
            for (int i = 0; i < rule.Branches.Count; i++)
            {
                var branch = rule.Branches[i];
                var trace = branch.IsElse ? null : EvaluateCondition(branch.Condition!, facts, runTimestamp);
                var result = branch.IsElse ? TruthValue.True : trace!.Result!.Value;
                traces.Add(new(rule.Id, branch.Id, result, true, trace, branch.IsElse ? "All preceding branches were definitively false." : null));
                if (result == TruthValue.False) continue;
                foreach (var later in rule.Branches.Skip(i + 1)) traces.Add(new(rule.Id, later.Id, null, false, later.Condition is null ? null : NotEvaluated(later.Condition), "A higher priority branch stopped evaluation."));
                if (result == TruthValue.Unknown)
                    return new() { Status = ContainsReadError(trace!) ? "Error" : "Deferred", RuleSetId = rule.Id, BranchId = branch.Id, RuleSetName = rule.Name, BranchName = branch.Name, Trace = traces, Reason = "A higher priority eligible branch has unknown required facts; lower branches and rulesets were not evaluated." };
                return new() { Status = "Matched", RuleSetId = rule.Id, BranchId = branch.Id, RuleSetName = rule.Name, BranchName = branch.Name, Actions = branch.Actions, Trace = traces };
            }
        }
        return new() { Status = "NoMatch", Trace = traces, Reason = "No eligible branch matched. The item is unchanged." };
    }

    public static bool InScope(RuleScope scope, MediaFacts facts) => scope.ItemKinds.Contains(facts.ItemKind, StringComparer.OrdinalIgnoreCase)
        && (scope.LibraryIds.Count == 0 || scope.LibraryIds.Any(id => Guid.TryParse(id, out var expected) && Guid.TryParse(facts.LibraryId, out var actual)
            ? expected == actual : string.Equals(id, facts.LibraryId, StringComparison.OrdinalIgnoreCase)));
    private static bool ContainsReadError(ConditionTrace trace) => trace.Availability == FactState.ReadError || trace.Children.Any(ContainsReadError);
    public ConditionTrace EvaluateCondition(ConditionNode node, MediaFacts facts, DateTimeOffset runTimestamp)
    {
        ConditionTrace Base(TruthValue value, string? reason = null, IReadOnlyList<ConditionTrace>? children = null) => new()
        { NodeId = node.Id, Kind = node.Kind, Result = value, Reason = reason, Children = children ?? [], EvidenceItemId = facts.ItemId, EvidenceTitle = facts.Title, StreamIndex = StreamIndex(facts) };
        if (node.Kind is "All" or "Any")
        {
            var children = new List<ConditionTrace>(); bool unknown = false; TruthValue? decisive = null;
            foreach (var child in node.Children)
            {
                if (decisive is not null) { children.Add(NotEvaluated(child)); continue; }
                var evaluated = EvaluateCondition(child, facts, runTimestamp); children.Add(evaluated);
                if (evaluated.Result == TruthValue.Unknown) unknown = true;
                if (node.Kind == "All" && evaluated.Result == TruthValue.False) decisive = TruthValue.False;
                if (node.Kind == "Any" && evaluated.Result == TruthValue.True) decisive = TruthValue.True;
            }
            return Base(decisive ?? (unknown ? TruthValue.Unknown : node.Kind == "All" ? TruthValue.True : TruthValue.False), children: children);
        }
        if (node.Kind == "Not")
        {
            var child = EvaluateCondition(node.Child!, facts, runTimestamp);
            return Base(child.Result == TruthValue.Unknown ? TruthValue.Unknown : child.Result == TruthValue.True ? TruthValue.False : TruthValue.True, children: [child]);
        }
        if (node.Kind == "Quantifier") return EvaluateCollection(node, facts, runTimestamp);
        if (node.Kind != "Predicate") return Base(TruthValue.Unknown, "Unsupported condition node.");
        var fact = facts.Get(node.FieldId ?? string.Empty); var field = _registry.Find(node.FieldId);
        var actual = FactConversion.Unwrap(fact.Value);
        var result = Compare(fact, field, node.OperatorId ?? string.Empty, node.Value, facts.PathCaseSensitive, runTimestamp, out var reason);
        return new() { NodeId = node.Id, Kind = node.Kind, FieldId = node.FieldId, OperatorId = node.OperatorId, Actual = actual, Expected = node.Value, Result = result, Reason = reason, Source = fact.Source, Availability = fact.State, EvidenceItemId = facts.ItemId, EvidenceTitle = facts.Title, StreamIndex = StreamIndex(facts) };
    }
    private static int? StreamIndex(MediaFacts facts)
    {
        foreach (var field in new[] { "audio.index", "subtitle.index", "video.index" })
            if (FactConversion.Number(facts.Get(field).Value) is { } number) return (int)number;
        return null;
    }
    private static ConditionTrace NotEvaluated(ConditionNode node) => new()
    {
        NodeId = node.Id, Kind = node.Kind, FieldId = node.FieldId, OperatorId = node.OperatorId, Expected = node.Value,
        Evaluated = false, Result = null, Reason = "Not evaluated because a preceding condition or branch established the result.",
        Children = node.Children.Select(NotEvaluated).Concat(node.Child is null ? [] : new[] { NotEvaluated(node.Child) }).ToArray()
    };
    private ConditionTrace EvaluateCollection(ConditionNode node, MediaFacts facts, DateTimeOffset runTimestamp)
    {
        var collection = facts.Collections.TryGetValue(node.CollectionId ?? string.Empty, out var c) ? c : new(FactState.Unsupported, [], "The collection is unsupported or was not loaded.");
        var children = collection.Elements.Select(x => EvaluateCondition(node.Child!, x, runTimestamp)).ToArray();
        int matches = children.Count(x => x.Result == TruthValue.True), unknown = children.Count(x => x.Result == TruthValue.Unknown), total = children.Length;
        bool complete = collection.State is FactState.Known or FactState.Absent;
        var result = node.Quantifier switch
        {
            "any" => matches > 0 ? TruthValue.True : unknown > 0 || !complete ? TruthValue.Unknown : TruthValue.False,
            "none" => matches > 0 ? TruthValue.False : unknown > 0 || !complete ? TruthValue.Unknown : TruthValue.True,
            "all" => children.Any(x => x.Result == TruthValue.False) ? TruthValue.False : unknown > 0 || !complete ? TruthValue.Unknown : total > 0 ? TruthValue.True : TruthValue.False,
            "count" => CompareCountInterval(matches, complete ? matches + unknown : double.PositiveInfinity, node.CountOperator, node.CountValue),
            _ => TruthValue.Unknown
        };
        return new() { NodeId = node.Id, Kind = node.Kind, FieldId = node.CollectionId, OperatorId = node.Quantifier, Expected = node.CountValue,
            Actual = matches, Result = result, Availability = collection.State, Reason = result == TruthValue.Unknown ? collection.Reason ?? "Unknown members prevent a definite collection result." : "Known matching elements: " + matches + "; unknown elements: " + unknown + "; observed elements: " + total + ".",
            KnownCount = total - unknown, UnknownCount = unknown, TotalCount = total, EvidenceItemId = facts.ItemId, EvidenceTitle = facts.Title, Children = children };
    }
    private static TruthValue CompareCountInterval(double lower, double upper, string op, RuleValue? value)
    {
        if (value?.Number is not { } n) return TruthValue.Unknown;
        bool? result = op switch
        {
            "eq" => lower == upper && lower == n ? true : n < lower || n > upper ? false : null,
            "neq" => lower == upper && lower == n ? false : n < lower || n > upper ? true : null,
            "gt" => lower > n ? true : upper <= n ? false : null,
            "gte" => lower >= n ? true : upper < n ? false : null,
            "lt" => upper < n ? true : lower >= n ? false : null,
            "lte" => upper <= n ? true : lower > n ? false : null,
            "range" when value.UpperNumber is { } hi => lower >= n && upper <= hi ? true : upper < n || lower > hi ? false : null,
            _ => null
        };
        return result is null ? TruthValue.Unknown : result.Value ? TruthValue.True : TruthValue.False;
    }
    private static TruthValue Compare(FactValue fact, FieldDefinition? field, string op, RuleValue? value, bool pathCaseSensitive, DateTimeOffset runTimestamp, out string? reason)
    {
        reason = fact.Reason;
        if (field is null) { reason = "Unknown field ID."; return TruthValue.Unknown; }
        if (!field.Operators.Contains(op)) { reason = "The operator is unavailable for this field."; return TruthValue.Unknown; }
        if (op is "isKnown" or "isMissing")
        {
            if (fact.State is FactState.NotReady or FactState.ReadError) return TruthValue.Unknown;
            return (op == "isKnown" ? fact.State == FactState.Known : fact.State == FactState.Absent) ? TruthValue.True : TruthValue.False;
        }
        if (fact.State == FactState.Absent
            && (field.Id is "item.originalLanguage" or "series.originalLanguage")
            && (op is "eq" or "neq" or "in" or "notIn"))
        {
            reason = "Original language is confirmed missing; this comparison does not match. Evaluation can continue to later branches or rulesets. Use 'Is missing' to match absence explicitly.";
            return TruthValue.False;
        }
        if (fact.State != FactState.Known) { reason ??= "Comparison requires a known value (" + fact.State + ")."; return TruthValue.Unknown; }
        var actual = FactConversion.Unwrap(fact.Value);
        if (op is "isEmpty" or "isNotEmpty")
        {
            bool empty = actual is null || actual is string s && s.Length == 0 || actual is IEnumerable<string> values && !values.Any() || actual is Array a && a.Length == 0;
            return (op == "isEmpty" ? empty : !empty) ? TruthValue.True : TruthValue.False;
        }
        if (actual is null) { reason = "A null value cannot establish this comparison."; return TruthValue.Unknown; }
        if (field.Type == "language" && (string.IsNullOrWhiteSpace(FactConversion.Text(actual)) || LanguageNormalizer.Normalize(FactConversion.Text(actual)) is "und" or "unknown" or "unk" or "zxx" or "mul" or "mis"))
        { reason = "The track or provider did not identify a language."; return TruthValue.Unknown; }
        bool? compared = null;
        var comparison = field.Comparison == "filesystem" && pathCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool Equal(string a, string b) => field.Type == "language" ? LanguageNormalizer.Matches(a, b) : string.Equals(a, b, comparison);
        if (op == "isTrue" || op == "isFalse")
        {
            if (FactConversion.Boolean(actual) is { } flag) compared = op == "isTrue" ? flag : !flag;
        }
        else if (field.Type == "number")
        {
            if (FactConversion.Number(actual) is { } n && value?.Number is { } expected) compared = Numeric(n, expected, value.UpperNumber, op);
        }
        else if (field.Type == "date")
        {
            if (FactConversion.Date(actual) is { } date)
            {
                compared = op switch
                {
                    "before" when value?.Date is { } d => date < d,
                    "after" when value?.Date is { } d => date > d,
                    "range" when value?.Date is { } lo && value.UpperDate is { } hi => date >= lo && date <= hi,
                    "ageLessThanDays" when value?.Number is { } days => (runTimestamp - date).TotalDays < days,
                    "ageGreaterThanDays" when value?.Number is { } days => (runTimestamp - date).TotalDays > days,
                    _ => null
                };
            }
        }
        else if (field.Type == "set")
        {
            var set = FactConversion.Strings(actual);
            if (set is not null) compared = op switch
            {
                "containsValue" when value?.Text is { } expected => set.Any(x => Equal(x, expected)),
                "containsAny" when value is not null => value.Values.Any(expected => set.Any(x => Equal(x, expected))),
                "containsAll" when value is not null => value.Values.All(expected => set.Any(x => Equal(x, expected))),
                "containsNone" when value is not null => !value.Values.Any(expected => set.Any(x => Equal(x, expected))),
                _ => null
            };
        }
        else if (FactConversion.Text(actual) is { } text)
        {
            compared = op switch
            {
                "eq" when value?.Text is { } expected => Equal(text, expected),
                "neq" when value?.Text is { } expected => !Equal(text, expected),
                "contains" when value?.Text is { } expected => text.Contains(expected, comparison),
                "notContains" when value?.Text is { } expected => !text.Contains(expected, comparison),
                "startsWith" when value?.Text is { } expected => text.StartsWith(expected, comparison),
                "endsWith" when value?.Text is { } expected => text.EndsWith(expected, comparison),
                "in" when value is not null => value.Values.Any(expected => Equal(text, expected)),
                "notIn" when value is not null => !value.Values.Any(expected => Equal(text, expected)),
                _ => null
            };
        }
        if (compared is null) reason = "The stored value cannot be compared using the required type.";
        else if (field.Type == "language") reason ??= "Language '" + FactConversion.Text(actual) + "' normalizes to '" + LanguageNormalizer.Normalize(FactConversion.Text(actual)) + "'. Norwegian, any variant means no, nb, or nn; exact-language choices remain distinct.";
        return compared is null ? TruthValue.Unknown : compared.Value ? TruthValue.True : TruthValue.False;
    }
    private static bool? Numeric(double n, double expected, double? upper, string op) => op switch
    {
        "eq" => n == expected, "neq" => n != expected, "lt" => n < expected, "lte" => n <= expected, "gt" => n > expected, "gte" => n >= expected,
        "range" when upper is { } hi => n >= expected && n <= hi, _ => null
    };
}

internal static class FactConversion
{
    public static object? Unwrap(object? value) => value is JsonElement e ? e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(UnwrapElement).ToArray(), _ => null
    } : value;
    private static object? UnwrapElement(JsonElement value) => Unwrap(value);
    public static string? Text(object? value) => Unwrap(value) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    public static double? Number(object? value)
    {
        var v = Unwrap(value);
        return v is not null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    }
    public static bool? Boolean(object? value) => Unwrap(value) is bool b ? b : null;
    public static DateTimeOffset? Date(object? value) => Unwrap(value) switch
    {
        DateTimeOffset date => date, DateTime date => new DateTimeOffset(date),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) => date, _ => null
    };
    public static IReadOnlyList<string>? Strings(object? value) => Unwrap(value) switch
    {
        IEnumerable<string> values => values.ToArray(), IEnumerable<object?> values => values.Select(Text).Where(x => x is not null).Cast<string>().ToArray(), _ => null
    };
}
