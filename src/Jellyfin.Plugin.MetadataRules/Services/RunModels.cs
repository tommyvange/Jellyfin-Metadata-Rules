using System.Text.Json.Serialization;
using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
namespace Jellyfin.Plugin.MetadataRules.Services;

public sealed class RunRequest
{
    public string? RulesJson { get; set; }
    public string[]? ItemIds { get; set; }
    public string? PreviewJobId { get; set; }
    public string? ExpectedRevision { get; set; }
    public bool Force { get; set; }
}
public sealed class RunCounts
{
    public int Examined { get; set; }
    public int Matched { get; set; }
    public int Changed { get; set; }
    public int Refresh { get; set; }
    public int Inherited { get; set; }
    public int NoOp { get; set; }
    public int Skipped { get; set; }
    public int Deferred { get; set; }
    public int Errors { get; set; }
    public int Affected { get; set; }
}
public sealed class RunJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string State { get; set; } = "Queued";
    public string Trigger { get; set; } = "Manual";
    public bool DryRun { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Revision { get; set; } = "";
    public int Total { get; set; }
    public int Processed { get; set; }
    public RunCounts Counts { get; set; } = new();
    public List<RuleRunCounts> CountsByRule { get; set; } = [];
    public string? Error { get; set; }
    public bool ResultsTruncated { get; set; }
    public string RulesJson { get; set; } = "";
    public string[] CandidateIds { get; set; } = [];
    public bool EnumerateOnStart { get; set; }
    public bool FullReconciliation { get; set; }
    public PluginConfiguration Settings { get; set; } = new();
    public bool Force { get; set; }
    public string? PreviewJobId { get; set; }
    [JsonIgnore] public CancellationTokenSource Cancellation { get; } = new();
    [JsonIgnore] public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
public sealed class RunRow
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ItemKind { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public ActionPlan? Plan { get; set; }
    public List<ActionOutcome> Outcome { get; set; } = [];
    public DateTimeOffset FactsTimestamp { get; set; }
    public string Revision { get; set; } = "";
    public bool PlanChanged { get; set; }
}
public sealed record ActionOutcome(string ItemId, string Stage, string? Message = null);
public sealed class JournalEntry
{
    public string ItemId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Stage { get; set; } = "Pending";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastRefreshAt { get; set; }
    public string? Message { get; set; }
    public List<LocaleTransition> Transitions { get; set; } = [];
    public bool Paused { get; set; }
    public int UncertainAttempts { get; set; }
}
public sealed record LocaleTransition(string Locale, string? BranchId, DateTimeOffset At);

public sealed class RuleRunCounts
{
    public string RuleSetId { get; set; } = "";
    public string RuleSetName { get; set; } = "";
    public int Matched { get; set; }
    public int Changed { get; set; }
    public int Refresh { get; set; }
    public int Deferred { get; set; }
    public int Errors { get; set; }
}
