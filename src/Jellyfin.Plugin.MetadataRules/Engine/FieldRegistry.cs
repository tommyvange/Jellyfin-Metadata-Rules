using System.Globalization;

namespace Jellyfin.Plugin.MetadataRules.Engine;

public sealed record ValueChoice(string Value, string Label);
public sealed record FieldDefinition(string Id, string DisplayName, string Type, IReadOnlyList<string> ItemKinds, IReadOnlyList<string> Operators, IReadOnlyList<ValueChoice> Choices, IReadOnlyList<string> Dependencies, string Availability, string Comparison, string HelpText);
public sealed record CollectionDefinition(string Id, string DisplayName, IReadOnlyList<string> ItemKinds, IReadOnlyList<string> ElementKinds, string Dependency, string HelpText);
public sealed record OperatorDefinition(string Id, string DisplayName, string ValueKind);

/// <summary>The single source of editor/evaluator field and operator capabilities.</summary>
public sealed class FieldRegistry
{
    public static readonly IReadOnlyList<string> ItemKinds = ["Movie", "Series", "Season", "Episode"];
    private static readonly string[] AllKinds = ["Movie", "Series", "Season", "Episode", "Version", "AudioTrack", "SubtitleTrack", "VideoTrack"];
    private static readonly string[] MediaKinds = ["Movie", "Series", "Season", "Episode"];
    private static readonly string[] PlayableKinds = ["Movie", "Episode", "Version"];
    private static readonly string[] AvailabilityOperators = ["isKnown", "isMissing", "isEmpty", "isNotEmpty"];
    public IReadOnlyList<FieldDefinition> Fields { get; }
    public IReadOnlyList<CollectionDefinition> Collections { get; }
    public IReadOnlyList<OperatorDefinition> Operators { get; } = [
        new("eq", "Equals", "scalar"), new("neq", "Does not equal", "scalar"),
        new("contains", "Contains", "text"), new("notContains", "Does not contain", "text"),
        new("startsWith", "Starts with", "text"), new("endsWith", "Ends with", "text"),
        new("lt", "Less than", "number"), new("lte", "Less than or equal", "number"),
        new("gt", "Greater than", "number"), new("gte", "Greater than or equal", "number"),
        new("range", "Inclusive range", "range"), new("before", "Before", "date"), new("after", "After", "date"),
        new("ageLessThanDays", "Age in days is less than", "number"), new("ageGreaterThanDays", "Age in days is greater than", "number"),
        new("in", "Is one of", "values"), new("notIn", "Is none of", "values"),
        new("containsValue", "Contains value", "text"), new("containsAny", "Contains any", "values"),
        new("containsAll", "Contains all", "values"), new("containsNone", "Contains none", "values"),
        new("isTrue", "Is true", "none"), new("isFalse", "Is false", "none"),
        new("isKnown", "Is known", "none"), new("isMissing", "Is missing", "none"),
        new("isEmpty", "Is empty", "none"), new("isNotEmpty", "Is not empty", "none")
    ];
    public FieldRegistry(IReadOnlyDictionary<string, IReadOnlyList<ValueChoice>>? storedChoices = null)
    {
        var fields = new List<FieldDefinition>();
        void Add(string id, string label, string type, string[] kinds, string dependency = "metadata", string? help = null, string comparison = "ordinalIgnoreCase", IReadOnlyList<ValueChoice>? choices = null)
        {
            string[] ops = type switch
            {
                "text" => ["eq", "neq", "contains", "notContains", "startsWith", "endsWith"],
                "number" => ["eq", "neq", "lt", "lte", "gt", "gte", "range"],
                "date" => ["before", "after", "range", "ageLessThanDays", "ageGreaterThanDays"],
                "enum" or "language" => ["eq", "neq", "in", "notIn"],
                "set" => ["containsValue", "containsAny", "containsAll", "containsNone"],
                "boolean" => ["isTrue", "isFalse"], _ => []
            };
            fields.Add(new(id, label, type, kinds, [.. ops, .. AvailabilityOperators], choices ?? storedChoices?.GetValueOrDefault(id) ?? [], [dependency],
                id is "item.originalLanguage" or "series.originalLanguage"
                    ? "Confirmed missing original language does not match language comparisons; later branches remain eligible. Is missing matches confirmed absence. Unfinished, unsupported, or unreadable facts remain unknown."
                    : "Unknown comparisons defer. Is missing matches confirmed absence only; unfinished facts remain unknown.", comparison,
                help ?? "Stored source value; unavailable data is shown with an explicit reason."));
        }
        Add("item.kind", "Item kind", "enum", AllKinds, "identity", choices: AllKinds.Select(x => new ValueChoice(x, x)).ToArray());
        Add("library.id", "Library", "enum", MediaKinds, "identity");
        Add("item.id", "Item ID", "text", AllKinds, "identity");
        Add("item.title", "Title", "text", AllKinds);
        Add("item.originalTitle", "Original title", "text", MediaKinds);
        Add("item.originalLanguage", "Original language (metadata)", "language", MediaKinds, help: "One provider supplied original language; independent of spoken audio tracks. A confirmed missing language does not match comparisons and allows later rules to run. Use Is missing to match absence explicitly.", choices: LanguageNormalizer.NorwegianChoices);
        Add("series.originalLanguage", "Series original language (metadata)", "language", ["Episode", "Season"], "series", help: "The series provider’s stored original language. Confirmed absence does not match comparisons and allows later rules to run; unfinished facts still defer.", choices: LanguageNormalizer.NorwegianChoices);
        Add("metadata.language", "Explicit metadata language", "language", MediaKinds, "preferences");
        Add("metadata.country", "Explicit metadata country", "enum", MediaKinds, "preferences", comparison: "countryCode");
        Add("metadata.effectiveLanguage", "Effective metadata language", "language", MediaKinds, "preferences");
        Add("metadata.effectiveCountry", "Effective metadata country", "enum", MediaKinds, "preferences", comparison: "countryCode");
        Add("metadata.inheritedLanguage", "Inherited metadata language before item override", "language", MediaKinds, "preferences");
        Add("metadata.inheritedCountry", "Inherited metadata country before item override", "enum", MediaKinds, "preferences", comparison: "countryCode");
        Add("metadata.hasLanguageOverride", "Has language override", "boolean", MediaKinds, "preferences");
        Add("metadata.hasCountryOverride", "Has country override", "boolean", MediaKinds, "preferences");
        foreach (var (id, label) in new[] { ("item.genres", "Genres"), ("item.tags", "Tags"), ("item.studios", "Studios"), ("item.productionLocations", "Production locations") })
            Add(id, label, "set", MediaKinds, help: id.EndsWith("productionLocations", StringComparison.Ordinal) ? "Provider strings, not ISO country codes." : null);
        Add("item.productionYear", "Production year", "number", MediaKinds);
        Add("item.premiereDate", "Premiere date", "date", MediaKinds);
        Add("item.endDate", "End date", "date", ["Series"]);
        Add("item.runtimeTicks", "Runtime (100 ns ticks)", "number", PlayableKinds, help: "Source model runtime in ticks; a series container runtime is not an episode duration.");
        Add("item.dateCreated", "Added date", "date", MediaKinds);
        Add("item.dateLastRefreshed", "Last refreshed date", "date", MediaKinds);
        Add("rating.official", "Age rating", "text", MediaKinds, help: "The media item’s stored age rating (Jellyfin OfficialRating). Suggestions include values used on any media. Rating labels are text, not universal numerical ages.");
        Add("rating.custom", "Custom age rating", "text", MediaKinds, help: "The media item’s stored custom age rating (Jellyfin CustomRating), matched independently of its age rating. Suggestions include custom values used on any media.");
        Add("rating.community", "Community rating", "number", MediaKinds);
        Add("rating.critic", "Critic rating", "number", MediaKinds);
        Add("provider.ids", "Provider IDs (provider=value)", "set", MediaKinds, "providers", help: "Exact provider=value membership. Example: Tmdb=123.");
        Add("provider.names", "Available provider IDs", "set", MediaKinds, "providers", help: "Exact provider name membership tests whether a provider ID exists.");
        foreach (var role in new[] { "actors", "directors", "writers", "other" }) Add("people." + role, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(role), "set", MediaKinds, "people");
        Add("file.path", "Path", "text", ["Movie", "Episode", "Season", "Series", "Version"], "file", comparison: "filesystem");
        Add("file.name", "Filename", "text", PlayableKinds, "file", comparison: "filesystem");
        Add("file.extension", "File extension", "text", PlayableKinds, "file");
        Add("file.container", "Container", "text", PlayableKinds, "file");
        Add("file.sizeBytes", "File size (bytes)", "number", PlayableKinds, "file", help: "Size of this file/version; not a series container total.");
        Add("file.versionCount", "Local playable version count", "number", ["Movie", "Episode"], "versions");
        foreach (var track in new[] { "audio", "subtitle", "video" })
        {
            string[] kinds = [track == "audio" ? "AudioTrack" : track == "subtitle" ? "SubtitleTrack" : "VideoTrack"];
            if (track != "video") Add(track + ".language", track == "audio" ? "Audio language" : "Subtitle language", "language", kinds, "streams", choices: LanguageNormalizer.NorwegianChoices);
            Add(track + ".codec", "Codec", "text", kinds, "streams");
            Add(track + ".index", "Stream index", "number", kinds, "streams");
            if (track != "video")
            {
                foreach (var flag in new[] { "default", "external", "hearingImpaired" }) Add(track + "." + flag, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(flag), "boolean", kinds, "streams");
            }
        }
        Add("audio.channels", "Audio channels", "number", ["AudioTrack"], "streams");
        Add("audio.title", "Audio track title", "text", ["AudioTrack"], "streams");
        Add("audio.bitrate", "Audio bitrate (bits/s)", "number", ["AudioTrack"], "streams");
        Add("audio.original", "Original audio flag", "boolean", ["AudioTrack"], "streams");
        Add("subtitle.forced", "Forced subtitle", "boolean", ["SubtitleTrack"], "streams");
        foreach (var (id, label) in new[] { ("width", "Width (pixels)"), ("height", "Height (pixels)"), ("frameRate", "Frame rate (frames/s)"), ("bitDepth", "Bit depth") }) Add("video." + id, label, "number", ["VideoTrack"], "streams");
        Add("video.range", "Video range", "enum", ["VideoTrack"], "streams");
        Add("video.hdr", "HDR properties", "set", ["VideoTrack"], "streams");
        Add("series.status", "Series status", "enum", ["Series", "Episode"], "series");
        Add("series.airDays", "Air days", "set", ["Series"], "series");
        Add("series.episodeCount", "Available episode count", "number", ["Series", "Season"], "episodes", help: "Available episodes excluding virtual missing episodes and extras.");
        Add("episode.seriesId", "Series ID", "text", ["Episode", "Season"], "series");
        Add("episode.seriesName", "Series name", "text", ["Episode", "Season"], "series");
        Add("episode.seasonId", "Season ID", "text", ["Episode"], "series");
        Add("episode.seasonNumber", "Season number", "number", ["Episode", "Season"]);
        Add("episode.number", "Episode number", "number", ["Episode"]);
        Add("episode.endNumber", "Episode range end", "number", ["Episode"]);
        Add("state.locked", "Metadata locked", "boolean", MediaKinds, "identity");
        Add("state.virtual", "Virtual item", "boolean", MediaKinds, "identity");
        Add("state.locationType", "Location type", "enum", MediaKinds, "identity");
        Add("state.sourceType", "Source type", "enum", ["Movie", "Episode", "Version"], "file");
        Fields = fields.AsReadOnly();
        Collections = [
            new("versions", "Local playable versions", ["Movie", "Episode"], ["Version"], "versions", "Alternate local playable versions only; extras excluded."),
            new("audioTracks", "Audio tracks", ["Movie", "Episode", "Version"], ["AudioTrack"], "streams", "Stored tracks; predicates inside this quantifier refer to the same track."),
            new("subtitleTracks", "Subtitle tracks", ["Movie", "Episode", "Version"], ["SubtitleTrack"], "streams", "Stored subtitle streams; no probing."),
            new("videoTracks", "Video tracks", ["Movie", "Episode", "Version"], ["VideoTrack"], "streams", "Stored video streams; no probing."),
            new("episodes", "Available episodes", ["Series", "Season"], ["Episode"], "episodes", "Excludes virtual missing episodes and extras. ALL requires at least one element.")
        ];
    }
    public FieldDefinition? Find(string? id) => Fields.FirstOrDefault(x => x.Id == id);
    public CollectionDefinition? FindCollection(string? id) => Collections.FirstOrDefault(x => x.Id == id);
    public IReadOnlySet<string> Dependencies(RuleDocument document)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Visit(ConditionNode? node)
        {
            if (node is null) return;
            if (Find(node.FieldId) is { } field) result.UnionWith(field.Dependencies);
            if (FindCollection(node.CollectionId) is { } collection) result.Add(collection.Dependency);
            foreach (var child in node.Children) Visit(child);
            Visit(node.Child);
        }
        foreach (var rule in document.RuleSets.Where(x => x.Enabled)) foreach (var branch in rule.Branches) Visit(branch.Condition);
        return result;
    }
}

public static class LanguageNormalizer
{
    public static IReadOnlyList<ValueChoice> NorwegianChoices { get; } = [new("norwegian", "Norwegian, any variant"), new("no", "Norwegian (no)"), new("nb", "Norwegian Bokmål (nb)"), new("nn", "Norwegian Nynorsk (nn)")];
    private static readonly IReadOnlyDictionary<string, string> Aliases = BuildAliases();
    private static IReadOnlyDictionary<string, string> BuildAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["nor"]="no", ["nob"]="nb", ["nno"]="nn", ["eng"]="en", ["swe"]="sv", ["dan"]="da", ["deu"]="de", ["ger"]="de", ["fra"]="fr", ["fre"]="fr", ["spa"]="es", ["ita"]="it", ["fin"]="fi", ["isl"]="is", ["ice"]="is", ["nld"]="nl", ["dut"]="nl", ["por"]="pt", ["jpn"]="ja", ["kor"]="ko", ["zho"]="zh", ["chi"]="zh", ["rus"]="ru", ["ukr"]="uk", ["pol"]="pl", ["ara"]="ar", ["heb"]="he", ["hin"]="hi", ["ces"]="cs", ["cze"]="cs", ["ell"]="el", ["gre"]="el", ["ron"]="ro", ["rum"]="ro", ["hun"]="hu", ["tur"]="tr", ["tha"]="th", ["vie"]="vi", ["ind"]="id"
        };
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures).Where(x => !string.IsNullOrEmpty(x.Name)))
            aliases.TryAdd(culture.ThreeLetterISOLanguageName, culture.TwoLetterISOLanguageName);
        return aliases;
    }
    public static string Normalize(string? value)
    {
        var code = (value ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();
        return Aliases.TryGetValue(code, out var normalized) ? normalized : code;
    }
    public static bool Matches(string? actual, string? expected)
    {
        var a = Normalize(actual); var e = Normalize(expected);
        return e == "norwegian" ? a is "no" or "nb" or "nn" : string.Equals(a, e, StringComparison.Ordinal);
    }
}
