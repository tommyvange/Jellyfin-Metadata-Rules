using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.MetadataRules.Controllers;
using Jellyfin.Plugin.MetadataRules.Engine;
using Jellyfin.Plugin.MetadataRules.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;

int assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++; Console.WriteLine("PASS " + message);
}
var fixture = new RatingFixture(1030);
var choices = RatingChoiceCatalog.Read(fixture.Library);
Check(choices.Keys.Order().SequenceEqual(new[] { "rating.custom", "rating.official" }), "catalog exposes separate stable official and custom rating field IDs");
Check(fixture.Snapshots.Count == 1 && fixture.BatchQueries.Count == 3 && fixture.BatchQueries.Select(q => q.ItemIds.Length).SequenceEqual(new[] { 512, 512, 6 }), "catalog freezes 1030 IDs once and reads bounded batches of 512");
Check(fixture.Snapshots.Single().IncludeOwnedItems && fixture.Snapshots.Single().IncludeAlternateVersions && fixture.Snapshots.Single().Recursive, "frozen scope includes owned items and alternate versions recursively");
Check(fixture.Snapshots.Concat(fixture.BatchQueries).All(q => q.AncestorIds.Length == 0 && q.IncludeItemTypes.Length == 0), "catalog scans all media kinds across libraries without selected-library filters");
Check(fixture.BatchQueries.All(q => q.DtoOptions.Fields.Count == 0 && !q.DtoOptions.EnableImages && !q.DtoOptions.EnableUserData), "catalog loads only stored columns without image or user data expansion");
Check(fixture.BatchQueries.SelectMany(q => q.ItemIds).Distinct().Count() == fixture.Items.Count, "every frozen media ID is read exactly once across batch boundaries");
var official = choices["rating.official"].Select(c => c.Value).ToArray();
var custom = choices["rating.custom"].Select(c => c.Value).ToArray();
Check(official.Contains("ALL-LIB-AUDIO") && official.Contains("OWNED-EXTRA") && official.Contains("ALT-ONLY"), "choices include audio in another library, owned extras, and alternate editions");
Check(custom.Contains("CUSTOM-ONLY") && !official.Contains("CUSTOM-ONLY"), "custom-only stored values appear only in the custom rating choices");
Check(official.Count(v => string.Equals(v, "PG-13", StringComparison.OrdinalIgnoreCase)) == 1 && custom.Count(v => string.Equals(v, "Family & <All>", StringComparison.OrdinalIgnoreCase)) == 1, "each rating field deduplicates with ordinal case-insensitive membership");
Check(official.All(v => !string.IsNullOrWhiteSpace(v)) && custom.All(v => !string.IsNullOrWhiteSpace(v)), "null, empty, and whitespace-only stored ratings are omitted");
Check(official.Contains("  Preserved Age  ") && custom.Contains("  Custom preserved  ") && choices.Values.SelectMany(v => v).All(c => c.Label == c.Value), "nonblank stored rating strings and their labels retain exact whitespace and content");
Check(official.SequenceEqual(official.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) && custom.SequenceEqual(custom.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)), "official and custom choices are sorted independently using ordinal case-insensitive text order");
Check(Array.IndexOf(official, "12") < Array.IndexOf(official, "6"), "rating strings use text ordering rather than an invented numerical age hierarchy");

fixture.ResetQueries();
var controller = new MetadataRulesController(null!, null!, fixture.Library, fixture.Localization, null!);
var response = controller.Schema() as JsonResult ?? throw new InvalidOperationException("Schema must return JSON.");
var json = JsonSerializer.SerializeToElement(response.Value, RunStore.JsonOptions);
var fields = json.GetProperty("Fields").EnumerateArray().ToDictionary(f => f.GetProperty("Id").GetString()!);
var officialField = fields["rating.official"]; var customField = fields["rating.custom"];
Check(officialField.GetProperty("DisplayName").GetString() == "Age rating" && customField.GetProperty("DisplayName").GetString() == "Custom age rating", "schema exposes the requested Age rating and Custom age rating labels");
Check(officialField.GetProperty("Choices").EnumerateArray().Any(c => c.GetProperty("Value").GetString() == "ALL-LIB-AUDIO") && customField.GetProperty("Choices").EnumerateArray().Any(c => c.GetProperty("Value").GetString() == "CUSTOM-ONLY"), "PascalCase schema fields carry independent stored rating choice lists");
var textOperators = new[] { "eq", "neq", "contains", "notContains", "startsWith", "endsWith" };
Check(officialField.GetProperty("Type").GetString() == "text" && customField.GetProperty("Type").GetString() == "text" && new[] { officialField, customField }.All(f => textOperators.All(op => f.GetProperty("Operators").EnumerateArray().Any(e => e.GetString() == op))), "existing rating IDs keep text types and backwards-compatible comparison operators");
Check(fixture.UnexpectedOperations.Count == 0, "catalog and schema never invoke provider, probe, persistence, stream, or refresh operations");

var movie = fixture.Items.Values.OfType<Movie>().First();
movie.OfficialRating = "PG-13"; movie.CustomRating = "Family custom";
var facts = fixture.ReadRatingFacts(movie);
var evaluator = new RuleEvaluator();
TruthValue Evaluate(string field, string expected) => evaluator.EvaluateCondition(new() { FieldId = field, OperatorId = "eq", Value = RuleValue.TextValue(expected) }, facts, fixture.Now).Result!.Value;
Check(Evaluate("rating.official", "PG-13") == TruthValue.True && Evaluate("rating.custom", "Family custom") == TruthValue.True, "real facts adapter and evaluator read each actual stored rating field independently");
Check(Evaluate("rating.official", "Family custom") == TruthValue.False && Evaluate("rating.custom", "PG-13") == TruthValue.False, "custom ratings do not replace or fall back into official rating predicates");
movie.CustomRating = null;
facts = fixture.ReadRatingFacts(movie);
Check(facts.Get("rating.custom").State == FactState.Absent && Evaluate("rating.custom", "PG-13") == TruthValue.Unknown && Evaluate("rating.official", "PG-13") == TruthValue.True, "missing custom rating stays unavailable instead of inheriting the official rating");
movie.OfficialRating = null; movie.CustomRating = "Custom only";
facts = fixture.ReadRatingFacts(movie);
Check(facts.Get("rating.official").State == FactState.Absent && Evaluate("rating.official", "Custom only") == TruthValue.Unknown && Evaluate("rating.custom", "Custom only") == TruthValue.True, "missing official rating stays unavailable instead of inheriting the custom rating");
Check(fixture.UnexpectedOperations.Count == 0, "rating facts and evaluator remain read-only without probing or refresh");

var empty = new RatingFixture(0);
var emptyChoices = RatingChoiceCatalog.Read(empty.Library);
Check(emptyChoices.Count == 2 && emptyChoices.Values.All(c => c.Count == 0) && empty.BatchQueries.Count == 0, "an empty library yields both empty lists without querying item batches");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel(); bool cancelled = false;
    try { RatingChoiceCatalog.Read(fixture.Library, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "catalog honors cancellation before reading ratings");
}
var cancellable = new RatingFixture(1030);
using (var cancellation = new CancellationTokenSource())
{
    cancellable.AfterBatch = () => cancellation.Cancel(); bool cancelled = false;
    try { RatingChoiceCatalog.Read(cancellable.Library, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && cancellable.BatchQueries.Count == 1, "catalog stops between batches when cancellation is requested");
}
Console.WriteLine($"{assertions} rating acceptance assertions passed.");

public class InterfaceProxy<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    public static T Create(Func<MethodInfo, object?[], object?> handler)
    {
        var result = DispatchProxy.Create<T, InterfaceProxy<T>>(); ((InterfaceProxy<T>)(object)result).Handler = handler; return result;
    }
}
sealed class RatingFixture
{
    public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
    public Dictionary<Guid, BaseItem> Items { get; } = [];
    public List<InternalItemsQuery> Snapshots { get; } = [];
    public List<InternalItemsQuery> BatchQueries { get; } = [];
    public List<string> UnexpectedOperations { get; } = [];
    public Action? AfterBatch { get; set; }
    public ILibraryManager Library { get; }
    public ILocalizationManager Localization { get; }
    private readonly Folder _library = new() { Id = Guid.NewGuid(), Name = "Selected movies" };
    private readonly Folder _otherLibrary = new() { Id = Guid.NewGuid(), Name = "Other music library" };
    public RatingFixture(int count)
    {
        for (var i = 0; i < count; i++)
        {
            BaseItem item = i switch { 1024 => new Audio(), 1025 => new Folder { ExtraType = ExtraType.Trailer }, 1027 => new Episode(), 1028 => new Series(), _ => new Movie() };
            item.Id = Guid.NewGuid(); item.Name = "Media " + i; item.Path = "/media/" + i + ".mkv"; item.DateLastRefreshed = Now.UtcDateTime;
            item.ParentId = i >= 1024 ? _otherLibrary.Id : _library.Id;
            item.OfficialRating = (i % 4) switch { 0 => "PG-13", 1 => "pg-13", 2 => " \t", _ => null };
            item.CustomRating = (i % 4) switch { 0 => "Family & <All>", 1 => "family & <all>", 2 => "", _ => null };
            if (i == 2) item.OfficialRating = "12";
            if (i == 3) item.OfficialRating = "6";
            if (i == 1024) { item.OfficialRating = "ALL-LIB-AUDIO"; item.CustomRating = "AUDIO-CUSTOM"; }
            if (i == 1025) { item.OfficialRating = "OWNED-EXTRA"; item.CustomRating = "Owned-Custom"; }
            if (i == 1026) { ((Movie)item).PrimaryVersionId = Items.Keys.First(); item.OfficialRating = "ALT-ONLY"; item.CustomRating = "Alternate-Custom"; }
            if (i == 1027) { item.OfficialRating = "TV-14"; item.CustomRating = null; }
            if (i == 1028) { item.OfficialRating = null; item.CustomRating = "CUSTOM-ONLY"; }
            if (i == 1029) { item.OfficialRating = "  Preserved Age  "; item.CustomRating = "  Custom preserved  "; }
            Items.Add(item.Id, item);
        }
        Library = InterfaceProxy<ILibraryManager>.Create((method, args) => method.Name switch
        {
            "GetItemIds" => Snapshot((InternalItemsQuery)args[0]!),
            "GetItemList" => Batch((InternalItemsQuery)args[0]!),
            "GetVirtualFolders" => new List<VirtualFolderInfo> { new VirtualFolderInfo { ItemId = _library.Id.ToString("N"), Name = _library.Name }, new VirtualFolderInfo { ItemId = _otherLibrary.Id.ToString("N"), Name = _otherLibrary.Name } },
            "GetItemById" => (Guid)args[0]! == _library.Id ? _library : (Guid)args[0]! == _otherLibrary.Id ? _otherLibrary : Items.GetValueOrDefault((Guid)args[0]!),
            "GetCollectionFolders" => new List<Folder> { ((BaseItem)args[0]!).ParentId == _otherLibrary.Id ? _otherLibrary : _library },
            "GetLibraryOptions" => new LibraryOptions(),
            _ => Unexpected(method.Name)
        });
        Localization = InterfaceProxy<ILocalizationManager>.Create((method, _) => method.Name switch { "GetCultures" => Array.Empty<CultureDto>(), "GetCountries" => Array.Empty<CountryInfo>(), "FindLanguageInfo" => null, _ => Unexpected("localization." + method.Name) });
    }
    public void ResetQueries() { Snapshots.Clear(); BatchQueries.Clear(); }
    private IReadOnlyList<Guid> Snapshot(InternalItemsQuery query) { Snapshots.Add(query); return Items.Keys.ToArray(); }
    private IReadOnlyList<BaseItem> Batch(InternalItemsQuery query)
    {
        BatchQueries.Add(query); var result = query.ItemIds.Select(id => Items.GetValueOrDefault(id)).Where(item => item is not null).Cast<BaseItem>().ToArray(); AfterBatch?.Invoke(); return result;
    }
    private object? Unexpected(string name) { UnexpectedOperations.Add(name); throw new InvalidOperationException("Unexpected operation: " + name); }
    public MediaFacts ReadRatingFacts(Movie movie)
    {
        var server = new ServerConfiguration { PreferredMetadataLanguage = "en", MetadataCountryCode = "US" };
        var configuration = InterfaceProxy<IServerConfigurationManager>.Create((method, _) => method.Name == "get_Configuration" ? server : Unexpected("configuration." + method.Name));
        var sources = InterfaceProxy<IMediaSourceManager>.Create((method, _) => Unexpected("sources." + method.Name));
        var providers = InterfaceProxy<IProviderManager>.Create((method, _) => Unexpected("providers." + method.Name));
        var fileSystem = InterfaceProxy<IFileSystem>.Create((method, _) => method.Name == "IsPathFile" ? true : Unexpected("filesystem." + method.Name));
        BaseItem.LibraryManager = Library; BaseItem.ConfigurationManager = configuration; BaseItem.LocalizationManager = Localization; BaseItem.FileSystem = fileSystem;
        Video.RecordingsManager = InterfaceProxy<IRecordingsManager>.Create((method, _) => method.Name == "GetActiveRecordingInfo" ? null : Unexpected("recordings." + method.Name));
        var adapter = new JellyfinMediaAdapter(Library, sources, providers, fileSystem, Localization, configuration);
        var rules = new RuleDocument { RuleSets = [new() { Branches = [new() { Condition = new() { Kind = "All", Children = [new() { FieldId = "rating.official", OperatorId = "isKnown" }, new() { FieldId = "rating.custom", OperatorId = "isKnown" }] } }] }] };
        return adapter.Read(movie.Id.ToString("N"), rules, Now)!;
    }
}
