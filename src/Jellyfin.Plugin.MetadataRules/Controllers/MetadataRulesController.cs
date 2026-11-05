using System.Text.Json;
using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
using Jellyfin.Plugin.MetadataRules.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MetadataRules.Controllers;

[ApiController]
[Route("MetadataRules")]
[Authorize(Policy = "RequiresElevation")]
[RequestSizeLimit(524288)]
public sealed class MetadataRulesController(RunCoordinator coordinator, RunStore store, ILibraryManager library, ILocalizationManager localization, IMediaAdapter media) : ControllerBase
{
    private static JsonResult Json(object value, int status = 200) => new(value, RunStore.JsonOptions) { StatusCode = status };
    private static Plugin PluginInstance => Plugin.Instance ?? throw new InvalidOperationException("Plugin unavailable.");
    [HttpGet("Schema")]
    public IActionResult Schema()
    {
        var registry = new FieldRegistry(RatingChoiceCatalog.Read(library, HttpContext?.RequestAborted ?? CancellationToken.None));
        return Json(new
        {
            registry.Fields, registry.Collections, registry.Operators,
            Languages = localization.GetCultures().Select(x => new ValueChoice(x.TwoLetterISOLanguageName, x.DisplayName + " (" + x.TwoLetterISOLanguageName + ")")).DistinctBy(x => x.Value).OrderBy(x => x.Label),
            Countries = localization.GetCountries().Select(x => new ValueChoice(x.TwoLetterISORegionName, (x.DisplayName ?? x.Name) + " / " + x.TwoLetterISORegionName)).OrderBy(x => x.Label),
            Libraries = library.GetVirtualFolders().Select(x => new { Id = Guid.TryParse(x.ItemId, out var id) ? id.ToString("N") : x.ItemId, x.Name }),
            Capabilities = new { JellyfinBaseline = "12.4.0", OriginalLanguage = true, StoredTracks = true, SingleItemRefresh = true, MaxAstDepth = RuleValidator.MaximumDepth, MaxRuleBytes = 262144, MaxCandidates = 100000, AutomaticRefreshConcurrency = 1 },
            ServerTimeZone = TimeZoneInfo.Local.Id,
            Schedule = "Daily at 03:00 server local time by default. Edit Metadata Rules under Dashboard → Scheduled Tasks; rule saves preserve your schedule."
        });
    }
    [HttpGet("Facts/{id}")]
    public IActionResult Facts(string id)
    {
        if (!Guid.TryParse(id, out _)) return BadRequest();
        // Request every stored dependency for a single-item inspector, without evaluating this synthetic tree.
        var registry = new FieldRegistry();
        var inspection = new RuleDocument { RuleSets = [new() { Branches = [new() { Condition = new() { Kind = "All", Children = [.. registry.Fields.Select(f => new ConditionNode { FieldId = f.Id }), .. registry.Collections.Select(c => new ConditionNode { CollectionId = c.Id })] } }] }] };
        try { return media.Read(id, inspection, DateTimeOffset.UtcNow) is { } facts ? Json(FactsSummary(facts, 0)) : NotFound(); }
        catch (Exception ex) { return Json(new { Message = ex.Message }, 400); }
    }
    private static object FactsSummary(MediaFacts facts, int depth) => new
    {
        facts.ItemId, facts.Title, facts.ItemKind, facts.LibraryId, facts.Timestamp, facts.Fields,
        Collections = facts.Collections.Select(pair => new
        {
            Id = pair.Key, pair.Value.State, pair.Value.Reason, Count = pair.Value.Elements.Count,
            Elements = depth >= 4 ? Array.Empty<object>() : pair.Value.Elements.Take(10).Select(f => FactsSummary(f, depth + 1)).ToArray(),
            SamplesTruncated = pair.Value.Elements.Count > 10
        })
    };
    [HttpGet("Configuration")]
    public IActionResult Configuration() => Json(PluginInstance.Settings);
    [HttpPut("Configuration")]
    public IActionResult SaveConfiguration([FromBody] PluginConfiguration config) => Guard(() => { PluginInstance.UpdateConfiguration(config); return PluginInstance.Settings; });
    public sealed class ValidationRequest { public string RulesJson { get; set; } = ""; }
    [HttpPost("Validate")]
    public IActionResult Validate([FromBody] ValidationRequest request)
    {
        try
        {
            if (request.RulesJson.Length > 262144) return Json(new { IsValid = false, Errors = new[] { "Rules exceed 256 KiB." }, Warnings = Array.Empty<string>() }, 400);
            var doc = RulesJson.Parse(request.RulesJson);
            var validation = new RuleValidator(languageCodes: localization.GetCultures().Select(x => x.TwoLetterISOLanguageName), countryCodes: localization.GetCountries().Select(x => x.TwoLetterISORegionName)).Validate(doc);
            return Json(validation);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NullReferenceException) { return Json(new { IsValid = false, Errors = new[] { ex.Message }, Warnings = Array.Empty<string>() }); }
    }
    [HttpPost("Preview")]
    public IActionResult Preview([FromBody] RunRequest request) => Guard(() => Summary(coordinator.Enqueue(request, true)));
    [HttpPost("Run")]
    public IActionResult Run([FromBody] RunRequest request) => Guard(() => Summary(coordinator.Enqueue(request, false)));
    [HttpGet("Jobs")]
    public IActionResult Jobs() => Json(store.Jobs().Select(Summary));
    [HttpGet("Jobs/{id}")]
    public IActionResult Job(string id) => store.Get(id) is { } job ? Json(Summary(job)) : NotFound();
    [HttpGet("Jobs/{id}/Items")]
    public IActionResult Items(string id, [FromQuery] int startIndex = 0, [FromQuery] int limit = 50, [FromQuery] string? status = null, [FromQuery] string? search = null)
    {
        if (!Guid.TryParse(id, out _) || store.Get(id) is null) return NotFound();
        var items = store.Rows(id).Where(x => (string.IsNullOrEmpty(status) || MatchesStatus(x, status)) && (string.IsNullOrEmpty(search) || (x.Title + " " + x.ItemId + " " + x.Reason).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        return Json(new { Items = items.Skip(Math.Max(0, startIndex)).Take(Math.Clamp(limit, 1, 200)), TotalRecordCount = items.Length });
    }
    [HttpGet("Jobs/{id}/Items/{itemId}")]
    public IActionResult Item(string id, string itemId) => Guid.TryParse(id, out _) && store.Get(id) is not null && store.Rows(id).FirstOrDefault(x => x.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase)) is { } row ? Json(row) : NotFound();
    [HttpPost("Jobs/{id}/Cancel")]
    public IActionResult Cancel(string id) => Json(new { Cancelled = coordinator.Cancel(id) });
    private static bool MatchesStatus(RunRow row, string status) => status.ToLowerInvariant() switch
    {
        "changed" => row.Plan?.ChangedCount > 0,
        "refreshed" => row.Outcome.Any(o => o.Stage == "RefreshAttemptCompleted"),
        "noop" => row.Plan is { Evaluation.Status: "Matched", AffectedCount: 0 } || row.Reason?.StartsWith("Preferences already match", StringComparison.Ordinal) == true,
        _ => row.Status.Equals(status, StringComparison.OrdinalIgnoreCase)
    };
    private static object Summary(RunJob j) => new { j.Id, j.State, j.Trigger, j.DryRun, j.StartedAt, j.FinishedAt, j.Revision, j.Total, j.Processed, j.Counts, j.CountsByRule, j.Error, j.ResultsTruncated };
    private static IActionResult Guard(Func<object> action)
    {
        try { return Json(action()); }
        catch (InvalidOperationException ex) { return Json(new { Message = ex.Message }, 409); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NullReferenceException) { return Json(new { Message = ex.Message }, 400); }
    }
}
