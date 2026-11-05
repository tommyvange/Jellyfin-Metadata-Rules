using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetadataRules.Engine;

public sealed class RuleDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Revision { get; set; } = string.Empty;
    public List<RuleSet> RuleSets { get; set; } = [];
}

public sealed class RuleSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New ruleset";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public RuleScope Scope { get; set; } = new();
    public List<RuleBranch> Branches { get; set; } = [];
}

public sealed class RuleScope
{
    public List<string> LibraryIds { get; set; } = [];
    public List<string> ItemKinds { get; set; } = ["Movie", "Series"];
}

public sealed class RuleBranch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "IF";
    public bool IsElse { get; set; }
    public ConditionNode? Condition { get; set; }
    public RuleActions Actions { get; set; } = new();
}

/// <summary>A concrete JSON/XML-boundary DTO; Kind selects one of the five typed forms.</summary>
public sealed class ConditionNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Predicate";
    public List<ConditionNode> Children { get; set; } = [];
    public ConditionNode? Child { get; set; }
    public string? FieldId { get; set; }
    public string? OperatorId { get; set; }
    public RuleValue? Value { get; set; }
    public string? CollectionId { get; set; }
    public string Quantifier { get; set; } = "any";
    public string CountOperator { get; set; } = "gte";
    public RuleValue? CountValue { get; set; }
}

public sealed class RuleValue
{
    public string Kind { get; set; } = "text";
    public string? Text { get; set; }
    public double? Number { get; set; }
    public DateTimeOffset? Date { get; set; }
    public bool? Boolean { get; set; }
    public List<string> Values { get; set; } = [];
    public double? UpperNumber { get; set; }
    public DateTimeOffset? UpperDate { get; set; }
    public static RuleValue TextValue(string value) => new() { Kind = "text", Text = value };
    public static RuleValue NumberValue(double value) => new() { Kind = "number", Number = value };
    public static RuleValue SetValue(params string[] values) => new() { Kind = "values", Values = [.. values] };
}

public sealed class PreferenceAction
{
    public string Mode { get; set; } = "leave";
    public string? Value { get; set; }
}

public sealed class RuleActions
{
    public PreferenceAction Language { get; set; } = new();
    public PreferenceAction Country { get; set; } = new();
    public string WriteScope { get; set; } = "item";
    public string RefreshScope { get; set; } = "item";
    public bool OverwriteDescendantOverrides { get; set; }
    public string RefreshMode { get; set; } = "none";
    public string RepeatPolicy { get; set; } = "newDecision";
    public int MinimumIntervalMinutes { get; set; } = 60;
}

[JsonConverter(typeof(JsonStringEnumConverter<FactState>))]
public enum FactState { Known, Absent, NotReady, Unsupported, ReadError }

/// <summary>Known null/empty is deliberately distinct from unavailable, unfinished, or absent.</summary>
public sealed record FactValue(FactState State, object? Value = null, string? Reason = null, string? Source = null)
{
    public static FactValue Known(object? value, string? source = null) => new(FactState.Known, value, Source: source);
    public static FactValue Absent(string? reason = null) => new(FactState.Absent, Reason: reason ?? "The source confirms this value is absent.");
    public static FactValue NotReady(string? reason = null) => new(FactState.NotReady, Reason: reason ?? "Required metadata is not ready.");
    public static FactValue Unsupported(string? reason = null) => new(FactState.Unsupported, Reason: reason ?? "This field is unsupported for the item or server.");
    public static FactValue ReadError(string reason) => new(FactState.ReadError, Reason: reason);
}

public sealed record FactCollection(FactState State, IReadOnlyList<MediaFacts> Elements, string? Reason = null)
{
    public static FactCollection Known(params MediaFacts[] elements) => new(FactState.Known, Array.AsReadOnly(elements));
    public static FactCollection NotReady(string? reason = null) => new(FactState.NotReady, [], reason ?? "The collection is not ready.");
}

/// <summary>Snapshots contain detached values only. Adapters must never store mutable Jellyfin entities here.</summary>
public sealed record MediaFacts
{
    public string ItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ItemKind { get; init; } = "Movie";
    public string LibraryId { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool PathCaseSensitive { get; init; }
    public IReadOnlyDictionary<string, FactValue> Fields { get; init; } = new Dictionary<string, FactValue>();
    public IReadOnlyDictionary<string, FactCollection> Collections { get; init; } = new Dictionary<string, FactCollection>();
    public IReadOnlyList<MediaFacts> Descendants { get; init; } = [];
    public FactValue Get(string fieldId) => Fields.TryGetValue(fieldId, out var value) ? value : fieldId switch
    {
        "item.id" => FactValue.Known(ItemId), "item.title" => FactValue.Known(Title),
        "item.kind" => FactValue.Known(ItemKind), "library.id" => FactValue.Known(LibraryId),
        _ => FactValue.Unsupported("The facts adapter did not supply this field.")
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<TruthValue>))]
public enum TruthValue { False, True, Unknown }

public sealed record ConditionTrace
{
    public string NodeId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? FieldId { get; init; }
    public string? OperatorId { get; init; }
    public object? Actual { get; init; }
    public RuleValue? Expected { get; init; }
    public TruthValue? Result { get; init; }
    public bool Evaluated { get; init; } = true;
    public string? Reason { get; init; }
    public string? Source { get; init; }
    public FactState? Availability { get; init; }
    public string? EvidenceItemId { get; init; }
    public string? EvidenceTitle { get; init; }
    public int? StreamIndex { get; init; }
    public int? KnownCount { get; init; }
    public int? UnknownCount { get; init; }
    public int? TotalCount { get; init; }
    public IReadOnlyList<ConditionTrace> Children { get; init; } = [];
}

public sealed record BranchTrace(string RuleSetId, string BranchId, TruthValue? Result, bool Evaluated, ConditionTrace? Condition, string? Reason = null);
public sealed record EvaluationResult
{
    public string Status { get; init; } = "NoMatch";
    public string? RuleSetId { get; init; }
    public string? BranchId { get; init; }
    public string? RuleSetName { get; init; }
    public string? BranchName { get; init; }
    public RuleActions? Actions { get; init; }
    public IReadOnlyList<BranchTrace> Trace { get; init; } = [];
    public string? Reason { get; init; }
}

public sealed record PreferenceSnapshot(string? Language, string? Country, string? EffectiveLanguage, string? EffectiveCountry);
public sealed record PlannedTarget
{
    public string ItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ItemKind { get; init; } = string.Empty;
    public bool IsSource { get; init; }
    public PreferenceSnapshot Before { get; init; } = new(null, null, null, null);
    public PreferenceSnapshot After { get; init; } = new(null, null, null, null);
    public bool WriteLanguage { get; init; }
    public bool WriteCountry { get; init; }
    public bool PreferenceChanged => WriteLanguage || WriteCountry;
    public string RefreshMode { get; init; } = "none";
    public bool Refresh => RefreshMode != "none";
    public bool SkippedLocked { get; init; }
    public string? Reason { get; init; }
    public string DecisionKey { get; init; } = string.Empty;
}
public sealed record InheritedEffect(string ItemId, string Title, PreferenceSnapshot Before, PreferenceSnapshot After);
public sealed record ActionPlan
{
    public string ItemId { get; init; } = string.Empty;
    public string RuleRevision { get; init; } = string.Empty;
    public DateTimeOffset FactsTimestamp { get; init; }
    public EvaluationResult Evaluation { get; init; } = new();
    public IReadOnlyList<PlannedTarget> Targets { get; init; } = [];
    public IReadOnlyList<InheritedEffect> InheritedEffects { get; init; } = [];
    public string DecisionKey { get; init; } = string.Empty;
    public int ChangedCount => Targets.Count(x => x.PreferenceChanged && !x.SkippedLocked);
    public int RefreshCount => Targets.Count(x => x.Refresh && !x.SkippedLocked);
    public int AffectedCount => Targets.Count(x => (x.PreferenceChanged || x.Refresh) && !x.SkippedLocked);
}

public static class RulesJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static RuleDocument Parse(string json) => JsonSerializer.Deserialize<RuleDocument>(json, Options) ?? throw new JsonException("A rules document is required.");
    public static string Serialize(RuleDocument document) => JsonSerializer.Serialize(document, Options);
}
