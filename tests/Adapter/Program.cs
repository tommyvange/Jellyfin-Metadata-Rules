using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
using Jellyfin.Plugin.MetadataRules.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine("PASS " + name); }
ConditionNode Predicate(string field, string op, RuleValue? value = null) => new() { FieldId = field, OperatorId = op, Value = value };
ConditionNode Any(string collection, ConditionNode child) => new() { Kind = "Quantifier", CollectionId = collection, Child = child };
RuleDocument Rules(ConditionNode condition, RuleActions? actions = null, params string[] kinds) => new()
{
    Revision = "adapter-test", RuleSets = [new() { Scope = new() { ItemKinds = kinds.Length == 0 ? ["Movie"] : [.. kinds] },
        Branches = [new() { Condition = condition, Actions = actions ?? new() }] }]
};

await Test("Preview is read-only and distinguishes current version from alternate version audio", () =>
{
    var f = new Fixture(); var movie = f.Movie(); movie.Container = "matroska";
    var alternateId = Guid.NewGuid();
    f.Versions[movie.Id] = [Fixture.Source(movie.Id, "/movies/Primary.mkv", 100), Fixture.Source(alternateId, "/movies/Alternate.mp4", 200)];
    f.Streams[movie.Id] = [new() { Type = MediaStreamType.Audio, Index = 1, Language = "eng", Channels = 6 }, new() { Type = MediaStreamType.Audio, Index = 2, Language = "nob", Channels = 2 }];
    f.Streams[alternateId] = [new() { Type = MediaStreamType.Audio, Index = 3, Language = "nor", Channels = 6 }];
    var norwegianSurround = new ConditionNode { Kind = "All", Children = [Predicate("audio.language", "eq", RuleValue.TextValue("norwegian")), Predicate("audio.channels", "gte", RuleValue.NumberValue(6))] };
    var document = Rules(Any("versions", Any("audioTracks", norwegianSurround)));
    var facts = f.Adapter.Read(movie.Id.ToString("N"), document, f.Now)!;
    var evaluator = new RuleEvaluator();
    Check(evaluator.Evaluate(document, facts, f.Now).Status == "Matched", "Alternate version should match.");
    Check(evaluator.EvaluateCondition(Any("audioTracks", norwegianSurround), facts, f.Now).Result == TruthValue.False, "Correlated predicates must match one track on the current version.");
    var alternate = facts.Collections["versions"].Elements.Single(x => x.ItemId == alternateId.ToString("N"));
    Check((long)alternate.Get("file.sizeBytes").Value! == 200 && (string)alternate.Get("file.name").Value! == "Alternate.mp4", "Version file facts must describe the alternate.");
    Check((string)facts.Get("file.container").Value! == "matroska" && (string)alternate.Get("file.container").Value! == "mp4", "Container must be its own field and describe the selected version.");
    Check((string)alternate.Get("item.kind").Value! == "Version", "Version identity must not inherit Movie kind.");
    Check(f.Writes == 0 && f.Refreshes.Count == 0, "Preview may not write or refresh.");
    Check(f.Queries.All(q => q.DtoOptions.EnableImages && q.DtoOptions.EnableUserData), "Read entities must retain owned rows.");
    var trace = evaluator.Evaluate(document, facts, f.Now).Trace.Single().Condition!;
    Check(JsonSerializer.Serialize(trace, RulesJson.Options).Contains("stream 3"), "Track evidence must identify its stream.");
    return Task.CompletedTask;
});

await Test("Version count loads static versions without reading tracks and excludes remote sources", () =>
{
    var f = new Fixture(); var movie = f.Movie();
    var remote = Fixture.Source(Guid.NewGuid(), "https://example.invalid/movie", 1); remote.Protocol = MediaProtocol.Http; remote.IsRemote = true;
    f.Versions[movie.Id] = [Fixture.Source(movie.Id, movie.Path, 123), remote];
    var facts = f.Adapter.Read(movie.Id.ToString("N"), Rules(Predicate("file.versionCount", "eq", RuleValue.NumberValue(1))), f.Now)!;
    Check((int)facts.Get("file.versionCount").Value! == 1, "Only local playable sources count.");
    Check(f.StreamReads == 0, "Version-count rules must not load streams.");
    return Task.CompletedTask;
});

await Test("Clearing an override previews the item's exact library fallback rather than its parent's options", () =>
{
    var f = new Fixture();
    var parent = new Folder { Id = Guid.NewGuid(), Name = "Parent", Path = "/movies" }; f.Items[parent.Id] = parent;
    var movie = f.Movie(); movie.ParentId = parent.Id; movie.PreferredMetadataLanguage = "nb";
    f.Options[parent.Id] = new() { PreferredMetadataLanguage = "fr", MetadataCountryCode = "FR" };
    f.Options[movie.Id] = new() { PreferredMetadataLanguage = "de", MetadataCountryCode = "DE" };
    var doc = Rules(Predicate("item.kind", "eq", RuleValue.TextValue("Movie")), new() { Language = new() { Mode = "clear" } });
    var facts = f.Adapter.Read(movie.Id.ToString("N"), doc, f.Now)!;
    Check((string)facts.Get("metadata.inheritedLanguage").Value! == "de", "Current library options must win over unrelated parent library options.");
    Check((string)facts.Get("metadata.effectiveLanguage").Value! == "nb", "Stored override must remain distinct from fallback.");
    var plan = new ActionPlanner().Plan(doc, facts, f.Now);
    Check(plan.Targets.Single().After.EffectiveLanguage == "de", "Clear-override plan must predict inheritance.");
    Check(movie.PreferredMetadataLanguage == "nb" && f.Writes == 0, "Planning cannot temporarily mutate the server entity.");
    return Task.CompletedTask;
});

await Test("Season scopes include actual episodes and preserve detached ancestry and field applicability", () =>
{
    var f = new Fixture();
    var series = new Series { Id = Guid.NewGuid(), Name = "Show", OriginalLanguage = "nno", Status = SeriesStatus.Continuing, DateLastRefreshed = f.Now.UtcDateTime };
    var season = new Season { Id = Guid.NewGuid(), Name = "Season 3", ParentId = series.Id, SeriesId = series.Id, IndexNumber = 3, DateLastRefreshed = f.Now.UtcDateTime };
    var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode", ParentId = season.Id, SeriesId = series.Id, SeasonId = season.Id, IndexNumber = 1, ParentIndexNumber = 3, Path = "/shows/one.mkv", DateLastRefreshed = f.Now.UtcDateTime };
    var missing = new Episode { Id = Guid.NewGuid(), Name = "Missing", ParentId = season.Id, IsVirtualItem = true };
    var extra = new Episode { Id = Guid.NewGuid(), Name = "Trailer", ParentId = season.Id, ExtraType = ExtraType.Trailer };
    foreach (var item in new BaseItem[] { series, season, episode, missing, extra }) f.Items[item.Id] = item;
    var doc = Rules(Predicate("item.kind", "eq", RuleValue.TextValue("Season")), new() { Language = new() { Mode = "set", Value = "no" } }, "Season");
    var facts = f.Adapter.Read(season.Id.ToString("N"), doc, f.Now)!;
    Check(facts.Descendants.Count == 1 && facts.Descendants[0].ItemId == episode.Id.ToString("N"), "Only actual episodes belong in ordinary descendant scope.");
    Check(facts.Descendants[0].ParentId == season.Id.ToString("N"), "Planner needs the explicit parent edge.");
    Check((int)facts.Get("episode.seasonNumber").Value! == 3, "Season number uses Season.IndexNumber.");
    Check((string)facts.Get("series.originalLanguage").Value! == "nn", "Season inherits original language from its series.");
    Check(facts.Get("item.runtimeTicks").State == FactState.Unsupported, "Season runtime is not a playable file duration.");
    var episodeFacts = f.Adapter.Read(episode.Id.ToString("N"), Rules(Predicate("series.status", "eq", RuleValue.TextValue("Continuing")), null, "Episode"), f.Now)!;
    Check((string)episodeFacts.Get("series.status").Value! == "Continuing", "Episode series status must come from the series.");
    Check(episodeFacts.Get("state.extra").Value is false, "Ordinary episode must not be marked as an extra.");
    return Task.CompletedTask;
});

await Test("Unfinished empty metadata and unprobed tracks defer; observed empty tracks remain known", () =>
{
    var f = new Fixture(); var movie = f.Movie(); movie.DateLastRefreshed = default;
    var doc = Rules(new() { Kind = "All", Children = [Predicate("item.genres", "isEmpty"), Any("subtitleTracks", Predicate("subtitle.forced", "isTrue"))] });
    f.Versions[movie.Id] = [Fixture.Source(movie.Id, movie.Path, 100)];
    f.Streams[movie.Id] = [];
    var facts = f.Adapter.Read(movie.Id.ToString("N"), doc, f.Now)!;
    Check(facts.Get("item.genres").State == FactState.NotReady, "Unfinished empty metadata must not be a negative match.");
    Check(facts.Collections["subtitleTracks"].State == FactState.NotReady, "No stored probe results must stay unknown.");
    movie.DateLastRefreshed = f.Now.UtcDateTime;
    f.Streams[movie.Id] = [new() { Type = MediaStreamType.Video, Index = 0, Width = 1920 }];
    facts = f.Adapter.Read(movie.Id.ToString("N"), doc, f.Now)!;
    Check(facts.Get("item.genres").State == FactState.Known, "Completed empty metadata is an observed empty set.");
    Check(facts.Collections["subtitleTracks"].State == FactState.Known && facts.Collections["subtitleTracks"].Elements.Count == 0, "Probed file without subtitles is a known empty collection.");
    return Task.CompletedTask;
});

await Test("Preference persistence rehydrates all owned data and preserves unrelated metadata", async () =>
{
    var f = new Fixture(); var movie = f.Movie();
    movie.ProviderIds["Tmdb"] = "123";
    movie.ImageInfos = [new() { Path = "/images/one.jpg", Type = ImageType.Primary }];
    movie.LockedFields = [MetadataField.Overview]; movie.Overview = "Retain overview";
    await f.Adapter.SavePreferences(movie.Id.ToString("N"), true, "no", true, "NO", CancellationToken.None);
    Check(f.Writes == 1 && movie.PreferredMetadataLanguage == "no" && movie.PreferredMetadataCountryCode == "NO", "Selected preferences must persist.");
    Check(movie.ProviderIds["Tmdb"] == "123" && movie.ImageInfos.Length == 1 && movie.LockedFields.SequenceEqual(new[] { MetadataField.Overview }) && movie.Overview == "Retain overview", "Owned and unrelated rows must survive.");
    Check(f.Queries.Single().DtoOptions.EnableImages && f.Queries.Single().DtoOptions.EnableUserData && f.Queries.Single().DtoOptions.Fields.Count > 0, "Write path must never save a stored-columns-only entity.");
});

await Test("Refresh awaits exactly one item with the requested replacement flags and respects locks", async () =>
{
    var f = new Fixture(); var series = new Series { Id = Guid.NewGuid(), Name = "Series" }; f.Items[series.Id] = series;
    await f.Adapter.Refresh(series.Id.ToString("N"), "metadata", true, CancellationToken.None);
    var options = f.Refreshes.Single().Options;
    Check(options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh && options.ReplaceAllMetadata && options.ForceSave && options.RemoveOldMetadata, "Metadata replacement flags must match Jellyfin.");
    Check(options.ImageRefreshMode == MetadataRefreshMode.None && !options.ReplaceAllImages, "Metadata-only action must preserve remote images.");
    Check(options.IsAutomated && !options.RegenerateTrickplay && !options.EnableRemoteContentProbe, "Background refresh must avoid unrelated regeneration/probing.");
    Check(f.Refreshes.Single().Item.Id == series.Id, "Single-item refresh must not queue recursive descendants.");
    await f.Adapter.Refresh(series.Id.ToString("N"), "metadataAndImages", false, CancellationToken.None);
    options = f.Refreshes.Last().Options;
    Check(options.ImageRefreshMode == MetadataRefreshMode.FullRefresh && options.ReplaceAllImages && !options.IsAutomated, "Image replacement is explicit.");
    series.IsLocked = true;
    try { await f.Adapter.SavePreferences(series.Id.ToString("N"), true, "no", false, null, CancellationToken.None); throw new Exception("Expected lock rejection."); } catch (InvalidOperationException) { }
    try { await f.Adapter.Refresh(series.Id.ToString("N"), "metadata", false, CancellationToken.None); throw new Exception("Expected lock rejection."); } catch (InvalidOperationException) { }
    Check(f.Writes == 0 && f.Refreshes.Count == 2, "Locks must prevent every action.");
});

await Test("Candidate IDs are frozen using selected libraries and top-level version filters", () =>
{
    var f = new Fixture(); var movie = f.Movie(); var libraryId = Guid.NewGuid();
    var ids = f.Adapter.Candidates(new() { LibraryIds = [libraryId.ToString("N")] }, Rules(Predicate("item.kind", "eq", RuleValue.TextValue("Movie"))));
    var query = f.CandidateQuery!;
    Check(f.Adapter.Candidates(new(), Rules(Predicate("item.kind", "eq", RuleValue.TextValue("Movie")))).Count == 0, "No selected libraries must never expand to the whole server.");
    Check(ids.Single() == movie.Id.ToString("N"), "Candidate snapshot must contain stable IDs.");
    Check(query.AncestorIds.Single() == libraryId && !query.IncludeOwnedItems && !query.IncludeAlternateVersions && query.IsVirtualItem == false, "Enumeration scope must exclude owned extras, versions, and virtual items.");
    return Task.CompletedTask;
});

await Test("Single-item refresh remains pending until Jellyfin completes the awaited attempt", async () =>
{
    var f = new Fixture(); var movie = f.Movie();
    f.PendingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = f.Adapter.Refresh(movie.Id.ToString("N"), "metadata", false, CancellationToken.None);
    Check(!pending.IsCompleted && f.Refreshes.Count == 1, "Adapter must await the provider task rather than queueing and reporting completion.");
    f.PendingRefresh.SetResult(ItemUpdateType.None);
    await pending;
});

await Test("Fresh parent preference persistence replaces Jellyfin's cached inheritance source", async () =>
{
    var f = new Fixture();
    var series = new Series { Id = Guid.NewGuid(), Name = "Show", PreferredMetadataLanguage = "en" };
    var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode", ParentId = series.Id, SeriesId = series.Id, Path = "/shows/episode.mkv" };
    f.Items[series.Id] = series; f.Items[episode.Id] = episode;
    f.CachedItems[series.Id] = new Series { Id = series.Id, Name = series.Name, PreferredMetadataLanguage = "en" };
    await f.Adapter.SavePreferences(series.Id.ToString("N"), true, "no", false, null, CancellationToken.None);
    var facts = f.Adapter.Read(episode.Id.ToString("N"), Rules(Predicate("metadata.effectiveLanguage", "eq", RuleValue.TextValue("no")), null, "Episode"), f.Now)!;
    Check((string)facts.Get("metadata.effectiveLanguage").Value! == "no", "Inherited locale must observe the freshly saved parent rather than a stale cached instance.");
    Check(ReferenceEquals(f.CachedItems[series.Id], series), "Only the fully hydrated persisted instance belongs in Jellyfin's cache.");
});

await Test("Incomplete local version enumeration stays unknown while remote-only emptiness remains observed", () =>
{
    var f = new Fixture(); var movie = f.Movie(); movie.DateLastRefreshed = default;
    var document = Rules(Any("versions", Any("audioTracks", Predicate("audio.language", "eq", RuleValue.TextValue("norwegian")))));
    var facts = f.Adapter.Read(movie.Id.ToString("N"), document, f.Now)!;
    Check(facts.Collections["versions"].State == FactState.NotReady && facts.Get("file.versionCount").State == FactState.NotReady, "A local item with no stored source cannot establish a negative decision.");
    Check(new RuleEvaluator().Evaluate(document, facts, f.Now).Status == "Deferred", "Missing source facts must defer the branch.");
    var pending = Fixture.Source(Guid.NewGuid(), "/movies/Pending.mkv", 0); pending.Type = MediaSourceType.Placeholder;
    f.Versions[movie.Id] = [pending];
    facts = f.Adapter.Read(movie.Id.ToString("N"), document, f.Now)!;
    Check(facts.Collections["versions"].State == FactState.NotReady, "Placeholder sources cannot become a known empty local collection.");
    f.Versions[movie.Id] = [Fixture.Source(movie.Id, movie.Path, 100), pending];
    f.Streams[movie.Id] = [new() { Type = MediaStreamType.Audio, Index = 1, Language = "nob" }];
    facts = f.Adapter.Read(movie.Id.ToString("N"), document, f.Now)!;
    Check(facts.Collections["versions"].State == FactState.NotReady && new RuleEvaluator().Evaluate(document, facts, f.Now).Status == "Matched", "Known positive evidence may establish ANY even while another version is pending.");
    var remote = Fixture.Source(Guid.NewGuid(), "https://example.invalid/movie", 100); remote.Protocol = MediaProtocol.Http; remote.IsRemote = true;
    f.Versions[movie.Id] = [remote];
    facts = f.Adapter.Read(movie.Id.ToString("N"), document, f.Now)!;
    Check(facts.Collections["versions"].State == FactState.Known && facts.Collections["versions"].Elements.Count == 0 && (int)facts.Get("file.versionCount").Value! == 0, "An observed remote-only source set genuinely contains no local versions.");
    return Task.CompletedTask;
});

await Test("Original-language placeholders report explicit unavailability instead of a usable language", () =>
{
    var f = new Fixture(); var movie = f.Movie();
    var document = Rules(Predicate("item.originalLanguage", "neq", RuleValue.TextValue("no")));
    foreach (var marker in new[] { "und", "zxx", "mul", "mis", "unk", "unknown" })
    {
        movie.OriginalLanguage = marker; movie.DateLastRefreshed = default;
        var facts = f.Adapter.Read(movie.Id.ToString("N"), document, f.Now)!;
        Check(facts.Get("item.originalLanguage").State == FactState.NotReady && !string.IsNullOrEmpty(facts.Get("item.originalLanguage").Reason), "Unfinished placeholder needs a readiness reason: " + marker);
        movie.DateLastRefreshed = f.Now.UtcDateTime;
        facts = f.Adapter.Read(movie.Id.ToString("N"), document, f.Now)!;
        Check(facts.Get("item.originalLanguage").State == FactState.Absent && new RuleEvaluator().Evaluate(document, facts, f.Now).Status == "Deferred", "Confirmed unidentified language cannot match a negative comparison: " + marker);
    }
    return Task.CompletedTask;
});

Console.WriteLine($"Adapter acceptance: {passed} passed.");

public class InterfaceProxy<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    public static T Create(Func<MethodInfo, object?[], object?> handler)
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy<T>>();
        ((InterfaceProxy<T>)(object)proxy).Handler = handler;
        return proxy;
    }
}

sealed class Fixture
{
    public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
    public Dictionary<Guid, BaseItem> Items { get; } = [];
    public Dictionary<Guid, BaseItem> CachedItems { get; } = [];
    public Dictionary<Guid, LibraryOptions> Options { get; } = [];
    public Dictionary<Guid, IReadOnlyList<MediaSourceInfo>> Versions { get; } = [];
    public Dictionary<Guid, IReadOnlyList<MediaStream>> Streams { get; } = [];
    public List<InternalItemsQuery> Queries { get; } = [];
    public InternalItemsQuery? CandidateQuery { get; private set; }
    public List<(BaseItem Item, MetadataRefreshOptions Options)> Refreshes { get; } = [];
    public int Writes { get; private set; }
    public int StreamReads { get; private set; }
    public TaskCompletionSource<ItemUpdateType>? PendingRefresh { get; set; }
    public JellyfinMediaAdapter Adapter { get; }
    private readonly Folder _library = new() { Id = Guid.NewGuid(), Name = "Movies" };
    public Fixture()
    {
        var config = new ServerConfiguration { PreferredMetadataLanguage = "en", MetadataCountryCode = "US" };
        var library = InterfaceProxy<ILibraryManager>.Create((method, args) => method.Name switch
        {
            "get_IsScanRunning" => false,
            "GetItemList" => Query((InternalItemsQuery)args[0]!),
            "GetItemIds" => CandidateIds((InternalItemsQuery)args[0]!),
            "GetItemById" => CachedItems.GetValueOrDefault((Guid)args[0]!) ?? Items.GetValueOrDefault((Guid)args[0]!),
            "RegisterItem" => Register((BaseItem)args[0]!),
            "GetCollectionFolders" => new List<Folder> { _library },
            "GetLibraryOptions" => Options.GetValueOrDefault(((BaseItem)args[0]!).Id) ?? new LibraryOptions(),
            "UpdateItemAsync" => Persist(),
            _ => throw new InvalidOperationException("Unexpected library operation: " + method.Name)
        });
        var sourceManager = InterfaceProxy<IMediaSourceManager>.Create((method, args) => method.Name switch
        {
            "GetStaticMediaSources" => Versions.GetValueOrDefault(((BaseItem)args[0]!).Id) ?? Array.Empty<MediaSourceInfo>(),
            "GetMediaStreams" => ReadStreams((Guid)args[0]!),
            _ => throw new InvalidOperationException("Unexpected stream operation (preview may not probe): " + method.Name)
        });
        var providers = InterfaceProxy<IProviderManager>.Create((method, args) => method.Name == "RefreshSingleItem"
            ? Refresh((BaseItem)args[0]!, (MetadataRefreshOptions)args[1]!)
            : throw new InvalidOperationException("Unexpected provider operation: " + method.Name));
        var fileSystem = InterfaceProxy<IFileSystem>.Create((method, args) => method.Name == "IsPathFile" ? !((string)args[0]!).StartsWith("http", StringComparison.OrdinalIgnoreCase) : throw new InvalidOperationException("Unexpected filesystem operation: " + method.Name));
        var localization = InterfaceProxy<ILocalizationManager>.Create((method, _) => method.Name == "FindLanguageInfo" ? null : throw new InvalidOperationException("Unexpected localization operation: " + method.Name));
        var serverConfig = InterfaceProxy<IServerConfigurationManager>.Create((method, _) => method.Name == "get_Configuration" ? config : throw new InvalidOperationException("Unexpected configuration operation: " + method.Name));
        BaseItem.LibraryManager = library; BaseItem.ConfigurationManager = serverConfig; BaseItem.LocalizationManager = localization; BaseItem.FileSystem = fileSystem;
        Video.RecordingsManager = InterfaceProxy<IRecordingsManager>.Create((method, _) => method.Name == "GetActiveRecordingInfo" ? null : throw new InvalidOperationException("Unexpected recordings operation: " + method.Name));
        Adapter = new(library, sourceManager, providers, fileSystem, localization, serverConfig);
    }
    private IReadOnlyList<BaseItem> Query(InternalItemsQuery query)
    {
        Queries.Add(query);
        return query.ItemIds.Length > 0 ? Items.Values.Where(x => query.ItemIds.Contains(x.Id)).ToArray()
            : Items.Values.Where(x => query.AncestorIds.Any(id => IsDescendant(x, id))).ToArray();
    }
    private bool IsDescendant(BaseItem item, Guid ancestor)
    {
        var seen = new HashSet<Guid>();
        while (item.ParentId != Guid.Empty && seen.Add(item.Id))
        {
            if (item.ParentId == ancestor) return true;
            if (!Items.TryGetValue(item.ParentId, out item!)) break;
        }
        return false;
    }
    private IReadOnlyList<Guid> CandidateIds(InternalItemsQuery query) { CandidateQuery = query; return Items.Keys.ToArray(); }
    private object? Register(BaseItem item) { CachedItems[item.Id] = item; return null; }
    private Task Persist() { Writes++; return Task.CompletedTask; }
    private IReadOnlyList<MediaStream> ReadStreams(Guid id) { StreamReads++; return Streams.GetValueOrDefault(id) ?? Array.Empty<MediaStream>(); }
    private Task<ItemUpdateType> Refresh(BaseItem item, MetadataRefreshOptions options) { Refreshes.Add((item, options)); return PendingRefresh?.Task ?? Task.FromResult(ItemUpdateType.None); }
    public Movie Movie()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie", Path = "/movies/Movie.mkv", DateLastRefreshed = Now.UtcDateTime, OriginalLanguage = "eng" };
        Items[movie.Id] = movie; return movie;
    }
    public static MediaSourceInfo Source(Guid id, string path, long size) => new() { Id = id.ToString("N"), Path = path, Name = Path.GetFileName(path), Protocol = MediaProtocol.File, Type = MediaSourceType.Default, Size = size, Container = Path.GetExtension(path).TrimStart('.') };
}
