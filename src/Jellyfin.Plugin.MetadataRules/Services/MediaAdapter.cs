using System.Collections.ObjectModel;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.MetadataRules.Services;

public interface IMediaAdapter
{
    bool IsScanRunning { get; }
    IReadOnlyList<string> Candidates(PluginConfiguration config, RuleDocument rules);
    MediaFacts? Read(string id, RuleDocument rules, DateTimeOffset timestamp, bool fresh = false);
    Task SavePreferences(string id, bool language, string? languageValue, bool country, string? countryValue, CancellationToken token);
    Task Refresh(string id, string mode, bool automated, CancellationToken token);
}

/// <summary>All server-version-specific access lives here. Stored reads never probe or call a provider.</summary>
public sealed class JellyfinMediaAdapter(ILibraryManager library, IMediaSourceManager sources,
    IProviderManager providers, IFileSystem fileSystem, ILocalizationManager localization,
    IServerConfigurationManager serverConfiguration) : IMediaAdapter
{
    private static readonly FieldRegistry Registry = new();
    private static readonly string[] TrackCollections = ["audioTracks", "subtitleTracks", "videoTracks"];
    public bool IsScanRunning => library.IsScanRunning;

    public IReadOnlyList<string> Candidates(PluginConfiguration config, RuleDocument rules)
    {
        if (config.LibraryIds.Length == 0) return [];
        var kinds = rules.RuleSets.Where(r => r.Enabled).SelectMany(r => r.Scope.ItemKinds).Distinct().ToArray();
        if (kinds.Length == 0) return [];
        var query = new InternalItemsQuery
        {
            Recursive = true, IncludeItemTypes = kinds.Select(Enum.Parse<BaseItemKind>).ToArray(),
            IncludeOwnedItems = false, IncludeAlternateVersions = false, IsVirtualItem = false,
            AncestorIds = config.LibraryIds.Select(Guid.Parse).ToArray(), DtoOptions = DtoOptions.StoredColumnsOnly
        };
        // Freeze IDs before a write can change titles or ordering. This query is never saved.
        return library.GetItemIds(query).Select(x => x.ToString("N")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // GetItemList reads the repository rather than GetItemById's shared cache. Saving a stored-columns
    // preview instance would erase owned rows; all mutation reads deliberately request every field.
    private BaseItem? Full(string id) => Guid.TryParse(id, out var guid) && guid != Guid.Empty
        ? library.GetItemList(new InternalItemsQuery
        {
            ItemIds = [guid], DtoOptions = new DtoOptions(true), IncludeOwnedItems = true,
            IncludeAlternateVersions = true, GroupByPresentationUniqueKey = false
        }).FirstOrDefault()
        : null;

    public MediaFacts? Read(string id, RuleDocument rules, DateTimeOffset timestamp, bool fresh = false)
    {
        var item = Full(id);
        if (item is null) return null;
        var pathCaseSensitive = Plugin.Instance?.Settings.PathCaseSensitive ?? (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS());
        var dependencies = Nodes(rules).ToArray();
        var needTracks = dependencies.Any(x => TrackCollections.Contains(x.CollectionId) ||
            x.FieldId?.StartsWith("audio.", StringComparison.Ordinal) == true ||
            x.FieldId?.StartsWith("subtitle.", StringComparison.Ordinal) == true ||
            x.FieldId?.StartsWith("video.", StringComparison.Ordinal) == true);
        var needVersions = needTracks || dependencies.Any(x => x.CollectionId == "versions" || x.FieldId == "file.versionCount");
        var needEpisodes = dependencies.Any(x => x.CollectionId == "episodes" || x.FieldId == "series.episodeCount");
        var needDescendants = item is Series or Season && rules.RuleSets.Where(r => r.Enabled).Any(r => r.Branches.Any(b =>
            b.Actions.WriteScope != "item" || b.Actions.RefreshScope != "item" ||
            b.Actions.Language.Mode != "leave" || b.Actions.Country.Mode != "leave"));
        var needPeople = dependencies.Any(x => x.FieldId?.StartsWith("people.", StringComparison.Ordinal) == true);
        var cache = new Dictionary<Guid, MediaFacts>();

        MediaFacts Build(BaseItem current, bool children)
        {
            if (cache.TryGetValue(current.Id, out var prior)) return prior;
            var kind = Kind(current);
            var fields = new Dictionary<string, FactValue>(StringComparer.Ordinal);
            void Put(string key, object? value, bool knownEmpty = false)
            {
                if (value is DateTime date && date == default) value = null;
                if (value is Enum ev) value = ev.ToString();
                if (value is Guid gv) value = gv == Guid.Empty ? null : gv.ToString("N");
                if (value is IEnumerable<DayOfWeek> days) value = days.Select(x => x.ToString()).ToArray();
                if (value is DateTime d) value = new DateTimeOffset(d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc));
                bool empty = value is null || value is string s && string.IsNullOrWhiteSpace(s);
                bool emptySet = value is IEnumerable<string> set && !set.Any();
                fields[key] = empty
                    ? knownEmpty ? FactValue.Known("") : current.DateLastRefreshed == default ? FactValue.NotReady() : FactValue.Absent()
                    : emptySet && !knownEmpty && current.DateLastRefreshed == default
                        ? FactValue.NotReady("The metadata collection has not been populated yet.")
                        : FactValue.Known(value, "Jellyfin stored metadata");
            }
            void Property(string key, string property, bool supported = true)
            {
                if (!supported) { fields[key] = FactValue.Unsupported(); return; }
                try
                {
                    var p = current.GetType().GetProperty(property);
                    if (p is null) fields[key] = FactValue.Unsupported(); else Put(key, p.GetValue(current));
                }
                catch (Exception ex) { fields[key] = FactValue.ReadError(ex.GetBaseException().Message); }
            }
            void OriginalLanguage(string key, string? value, BaseItem? metadataSource = null)
            {
                if (key == "series.originalLanguage" && metadataSource is null)
                {
                    fields[key] = FactValue.NotReady("The series metadata is not available yet.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(value) || UnknownLanguage(value))
                {
                    var reason = string.IsNullOrWhiteSpace(value)
                        ? "Stored metadata has no original language."
                        : "Stored original-language metadata does not identify one language (" + value + ").";
                    fields[key] = (metadataSource ?? current).DateLastRefreshed == default ? FactValue.NotReady(reason) : FactValue.Absent(reason);
                }
                else Put(key, Normalize(value));
            }
            var folders = library.GetCollectionFolders(current);
            var folder = folders.FirstOrDefault();
            var parents = current.GetParents().ToArray();
            var parent = parents.FirstOrDefault();
            var series = current as Series ?? parents.OfType<Series>().FirstOrDefault();
            var libraryOptions = library.GetLibraryOptions(current);
            var inheritedLanguage = FirstLocale(parents.Select(x => x.PreferredMetadataLanguage)
                .Concat(folders.Select(x => x.PreferredMetadataLanguage))
                .Append(libraryOptions.PreferredMetadataLanguage).Append(serverConfiguration.Configuration.PreferredMetadataLanguage));
            var inheritedCountry = FirstLocale(parents.Select(x => x.PreferredMetadataCountryCode)
                .Concat(folders.Select(x => x.PreferredMetadataCountryCode))
                .Append(libraryOptions.MetadataCountryCode).Append(serverConfiguration.Configuration.MetadataCountryCode));
            Put("item.id", current.Id.ToString("N")); Put("item.kind", kind); Put("library.id", folder?.Id.ToString("N"));
            Put("item.title", current.Name); Put("item.originalTitle", current.OriginalTitle);
            OriginalLanguage("item.originalLanguage", current.OriginalLanguage);
            Put("metadata.language", current.PreferredMetadataLanguage, true); Put("metadata.country", current.PreferredMetadataCountryCode, true);
            Put("metadata.effectiveLanguage", FirstLocale([current.PreferredMetadataLanguage, inheritedLanguage]), true);
            Put("metadata.effectiveCountry", FirstLocale([current.PreferredMetadataCountryCode, inheritedCountry]), true);
            Put("metadata.inheritedLanguage", inheritedLanguage, true); Put("metadata.inheritedCountry", inheritedCountry, true);
            Put("metadata.hasLanguageOverride", !string.IsNullOrEmpty(current.PreferredMetadataLanguage)); Put("metadata.hasCountryOverride", !string.IsNullOrEmpty(current.PreferredMetadataCountryCode));
            Put("item.genres", current.Genres.ToArray()); Put("item.tags", current.Tags.ToArray()); Put("item.studios", current.Studios.ToArray()); Put("item.productionLocations", current.ProductionLocations.ToArray());
            Property("item.productionYear", "ProductionYear"); Property("item.premiereDate", "PremiereDate"); Property("item.endDate", "EndDate");
            Property("item.runtimeTicks", "RunTimeTicks"); Property("item.dateCreated", "DateCreated"); Property("item.dateLastRefreshed", "DateLastRefreshed");
            Property("rating.official", "OfficialRating"); Property("rating.custom", "CustomRating"); Property("rating.community", "CommunityRating"); Property("rating.critic", "CriticRating");
            Put("provider.ids", current.ProviderIds.Select(p => p.Key + "=" + p.Value).ToArray()); Put("provider.names", current.ProviderIds.Keys.ToArray());
            Put("file.path", current.Path); Put("file.name", Path.GetFileName(current.Path)); Put("file.extension", Path.GetExtension(current.Path));
            Property("file.container", "Container"); Property("file.sizeBytes", "Size");
            Property("state.locked", "IsLocked"); Property("state.virtual", "IsVirtualItem"); Property("state.locationType", "LocationType"); Property("state.sourceType", "SourceType");
            Put("state.extra", current.ExtraType is not null);
            if (current is Episode or Season) OriginalLanguage("series.originalLanguage", series?.OriginalLanguage, series);
            if (current is Series or Episode) Put("series.status", series?.Status?.ToString());
            Property("series.airDays", "AirDays", current is Series);
            if (current is Episode or Season)
            {
                Property("episode.seriesId", "SeriesId");
                Put("episode.seriesName", series?.Name ?? (current as Episode)?.SeriesName ?? (current as Season)?.SeriesName);
                Property("episode.seasonNumber", current is Season ? "IndexNumber" : "ParentIndexNumber");
            }
            Property("episode.seasonId", "SeasonId", current is Episode);
            Property("episode.number", "IndexNumber", current is Episode); Property("episode.endNumber", "IndexNumberEnd", current is Episode);
            if (needPeople)
            {
                try
                {
                    var people = library.GetPeople(current);
                    foreach (var (key, role) in new[] { ("actors", "Actor"), ("directors", "Director"), ("writers", "Writer") })
                        Put("people." + key, people.Where(p => string.Equals(p.Type.ToString(), role, StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).ToArray());
                    Put("people.other", people.Where(p => p.Type.ToString() is not "Actor" and not "Director" and not "Writer").Select(p => p.Name).ToArray());
                }
                catch (Exception ex) { foreach (var role in new[] { "actors", "directors", "writers", "other" }) fields["people." + role] = FactValue.ReadError(ex.Message); }
            }
            // Applicability comes from the same registry used by the editor and evaluator.
            foreach (var field in Registry.Fields.Where(f => !f.ItemKinds.Contains(kind))) fields[field.Id] = FactValue.Unsupported();
            var collections = new Dictionary<string, FactCollection>();
            var facts = new MediaFacts
            {
                ItemId = current.Id.ToString("N"), Title = current.Name, ItemKind = kind,
                LibraryId = folder?.Id.ToString("N") ?? "", ParentId = parent?.Id.ToString("N"), Timestamp = timestamp,
                PathCaseSensitive = pathCaseSensitive,
                Fields = new ReadOnlyDictionary<string, FactValue>(fields)
            };
            cache[current.Id] = facts;
            if (needVersions && current is Movie or Episode)
            {
                try
                {
                    // Static source enumeration uses stored alternate versions, without owned extras,
                    // playback resolution, stream opening, remote providers, or a media probe.
                    var eligible = current.SourceType == SourceType.Library && !current.IsVirtualItem && current.ExtraType is null;
                    var storedSources = eligible ? sources.GetStaticMediaSources(current, false).ToArray() : [];
                    bool Local(MediaSourceInfo source) => source.Protocol == MediaProtocol.File && !source.IsRemote;
                    bool Ready(MediaSourceInfo source) => source.Type != MediaSourceType.Placeholder && !string.IsNullOrEmpty(source.Path) && Guid.TryParse(source.Id, out _);
                    var pendingVersions = eligible && (storedSources.Length == 0 && !string.IsNullOrEmpty(current.Path) && fileSystem.IsPathFile(current.Path) || storedSources.Any(v => Local(v) && !Ready(v)));
                    var versions = storedSources.Where(v => Local(v) && Ready(v)).DistinctBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ToArray();
                    var versionFacts = new List<MediaFacts>();
                    foreach (var version in versions)
                    {
                        var versionId = Guid.Parse(version.Id);
                        var versionFields = new Dictionary<string, FactValue>(fields)
                        {
                            ["item.id"] = FactValue.Known(versionId.ToString("N")), ["item.kind"] = FactValue.Known("Version"),
                            ["file.path"] = FactValue.Known(version.Path), ["file.name"] = FactValue.Known(Path.GetFileName(version.Path)),
                            ["file.extension"] = FactValue.Known(Path.GetExtension(version.Path)),
                            ["file.container"] = !string.IsNullOrWhiteSpace(version.Container) ? FactValue.Known(version.Container) : FactValue.Absent("Stored version has no identified container."),
                            ["file.sizeBytes"] = version.Size is { } size ? FactValue.Known(size) : FactValue.Absent(),
                            ["item.runtimeTicks"] = version.RunTimeTicks is { } runtime ? FactValue.Known(runtime) : FactValue.Absent()
                        };
                        var trackCollections = new Dictionary<string, FactCollection>();
                        if (needTracks)
                        {
                            try
                            {
                                var streamList = sources.GetMediaStreams(versionId);
                                foreach (var (key, type) in new[] { ("audioTracks", MediaStreamType.Audio), ("subtitleTracks", MediaStreamType.Subtitle), ("videoTracks", MediaStreamType.Video) })
                                    trackCollections[key] = streamList.Count == 0
                                        ? FactCollection.NotReady("No stored media probe results yet.")
                                        : FactCollection.Known(streamList.Where(s => s.Type == type).Select(s => Track(facts, versionId, version.Name, s)).ToArray());
                            }
                            catch (Exception ex) { foreach (var key in TrackCollections) trackCollections[key] = new(FactState.ReadError, [], ex.Message); }
                        }
                        foreach (var field in Registry.Fields.Where(f => !f.ItemKinds.Contains("Version"))) versionFields[field.Id] = FactValue.Unsupported();
                        versionFacts.Add(facts with { ItemId = versionId.ToString("N"), Title = version.Name ?? current.Name, ItemKind = "Version", Fields = new ReadOnlyDictionary<string, FactValue>(versionFields), Collections = new ReadOnlyDictionary<string, FactCollection>(trackCollections) });
                    }
                    collections["versions"] = pendingVersions
                        ? new(FactState.NotReady, versionFacts.ToArray(), "Local version enumeration is incomplete; pending sources are not negative evidence.")
                        : FactCollection.Known(versionFacts.ToArray());
                    if (needTracks)
                    {
                        var thisVersion = versionFacts.FirstOrDefault(v => v.ItemId == facts.ItemId);
                        foreach (var key in TrackCollections)
                            collections[key] = thisVersion?.Collections[key] ?? (pendingVersions
                                ? FactCollection.NotReady("This item's local playable source is not ready.")
                                : new FactCollection(FactState.Unsupported, [], "This item has no selected local playable version."));
                    }
                    if (pendingVersions) fields["file.versionCount"] = FactValue.NotReady("The exact local playable version count is not ready.");
                    else Put("file.versionCount", versions.Length, true);
                }
                catch (Exception ex)
                {
                    collections["versions"] = new(FactState.ReadError, [], ex.Message);
                    if (needTracks) foreach (var key in TrackCollections) collections[key] = new(FactState.ReadError, [], ex.Message);
                    fields["file.versionCount"] = FactValue.ReadError(ex.Message);
                }
            }
            IReadOnlyList<MediaFacts> descendants = [];
            if (children && current is Series or Season)
            {
                var descendantsItems = library.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = [current.Id], Recursive = true, IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
                    IncludeOwnedItems = false, IncludeAlternateVersions = false, IsVirtualItem = false,
                    GroupByPresentationUniqueKey = false, DtoOptions = new DtoOptions(true)
                });
                descendants = descendantsItems.Where(x => !x.IsVirtualItem && x.ExtraType is null && (x is not Episode || x.LocationType != LocationType.Virtual)).Select(x => Build(x, false)).ToArray();
                collections["episodes"] = FactCollection.Known(descendants.Where(x => x.ItemKind == "Episode").ToArray());
                Put("series.episodeCount", descendants.Count(x => x.ItemKind == "Episode"), true);
            }
            facts = facts with { Collections = new ReadOnlyDictionary<string, FactCollection>(collections), Descendants = descendants };
            cache[current.Id] = facts;
            return facts;
        }
        return Build(item, needEpisodes || needDescendants);
    }

    private static string Kind(BaseItem item) => item switch { Movie => "Movie", Series => "Series", Season => "Season", Episode => "Episode", _ => item.GetType().Name };
    private static string? FirstLocale(IEnumerable<string?> values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    private static bool UnknownLanguage(string? value) => LanguageNormalizer.Normalize(value) is "und" or "zxx" or "mul" or "mis" or "unk" or "unknown";
    private string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? value : localization.FindLanguageInfo(value)?.TwoLetterISOLanguageName ?? LanguageNormalizer.Normalize(value);

    private MediaFacts Track(MediaFacts parent, Guid versionId, string? name, MediaStream stream)
    {
        var prefix = stream.Type == MediaStreamType.Audio ? "audio." : stream.Type == MediaStreamType.Subtitle ? "subtitle." : "video.";
        var kind = stream.Type == MediaStreamType.Audio ? "AudioTrack" : stream.Type == MediaStreamType.Subtitle ? "SubtitleTrack" : "VideoTrack";
        var map = new Dictionary<string, FactValue>();
        foreach (var field in Registry.Fields.Where(f => f.ItemKinds.Contains(kind)))
        {
            if (!field.Id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var property = field.Id[prefix.Length..] switch
            {
                "codec" => "Codec", "channels" => "Channels", "title" => "Title", "bitrate" => "BitRate", "default" => "IsDefault",
                "original" => "IsOriginal", "external" => "IsExternal", "hearingImpaired" => "IsHearingImpaired", "forced" => "IsForced",
                "width" => "Width", "height" => "Height", "frameRate" => "RealFrameRate", "bitDepth" => "BitDepth", "range" => "VideoRange", "index" => "Index", _ => null
            };
            if (property is null) continue;
            var p = stream.GetType().GetProperty(property); var value = p?.GetValue(stream);
            map[field.Id] = p is null ? FactValue.Unsupported() : value is null ? FactValue.Absent() : FactValue.Known(value is Enum e ? e.ToString() : value, $"Version {versionId:N}, stream {stream.Index}");
        }
        if (stream.Type is MediaStreamType.Audio or MediaStreamType.Subtitle)
            map[prefix + "language"] = string.IsNullOrWhiteSpace(stream.Language) || UnknownLanguage(stream.Language)
                ? FactValue.Absent("Stored track has no single identified language.")
                : FactValue.Known(Normalize(stream.Language), $"Raw language {stream.Language}; version {versionId:N}, stream {stream.Index}");
        if (stream.Type == MediaStreamType.Video) map["video.hdr"] = FactValue.Known(new[] { stream.VideoRangeType.ToString() });
        map["track.versionId"] = FactValue.Known(versionId.ToString("N"));
        return parent with
        {
            ItemId = versionId.ToString("N"), Title = $"{name ?? parent.Title} · stream {stream.Index}", ItemKind = kind,
            Fields = new ReadOnlyDictionary<string, FactValue>(map), Collections = new Dictionary<string, FactCollection>(), Descendants = []
        };
    }

    public async Task SavePreferences(string id, bool language, string? languageValue, bool country, string? countryValue, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var item = Full(id) ?? throw new InvalidOperationException("Item was removed.");
        if (item.IsLocked) throw new InvalidOperationException("Item became locked before persistence.");
        if (language) item.PreferredMetadataLanguage = languageValue;
        if (country) item.PreferredMetadataCountryCode = countryValue;
        // Video.UpdateToRepositoryAsync also copies unrelated metadata onto local alternate versions.
        // Persist only this complete item through the library service, retaining normal metadata savers.
        await library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, token).ConfigureAwait(false);
        // UpdateItemAsync assumes its caller already owns the cached instance. Our fresh repository
        // read must replace that cache entry so descendant/provider inheritance observes the save.
        library.RegisterItem(item);
    }

    public async Task Refresh(string id, string mode, bool automated, CancellationToken token)
    {
        if (mode == "none") return;
        if (mode is not "metadata" and not "metadataAndImages") throw new ArgumentException("Unknown refresh mode.", nameof(mode));
        token.ThrowIfCancellationRequested();
        var item = Full(id) ?? throw new InvalidOperationException("Item was removed.");
        if (item.IsLocked) throw new InvalidOperationException("Item became locked before refresh.");
        var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh, ReplaceAllMetadata = true,
            ImageRefreshMode = mode == "metadataAndImages" ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
            ReplaceAllImages = mode == "metadataAndImages", ForceSave = true, RemoveOldMetadata = true,
            IsAutomated = automated, RegenerateTrickplay = false, EnableRemoteContentProbe = false
        };
        await providers.RefreshSingleItem(item, options, token).ConfigureAwait(false);
    }

    public static IEnumerable<ConditionNode> Nodes(RuleDocument doc)
    {
        IEnumerable<ConditionNode> Walk(ConditionNode n)
        {
            yield return n;
            foreach (var c in n.Children.Concat(n.Child is null ? [] : new[] { n.Child })) foreach (var x in Walk(c)) yield return x;
        }
        return doc.RuleSets.Where(r => r.Enabled).SelectMany(r => r.Branches).Where(b => b.Condition is not null).SelectMany(b => Walk(b.Condition!));
    }
}
