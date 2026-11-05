using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MetadataRules;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginId = "7960b22c-4e70-4a69-a57a-9b3b68dfdd38";
    public static Plugin? Instance { get; private set; }
    private readonly object _settingsGate = new();
    internal ILocalizationManager Localization { get; }
    public string StoragePath { get; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer, ILocalizationManager localization) : base(paths, serializer)
    {
        Localization = localization;
        StoragePath = Path.Combine(paths.DataPath, "metadata-rules");
        Configuration.Revision = SettingsRevision(Configuration);
        Instance = this;
    }
    public override string Name => "Metadata Rules";
    public override Guid Id => Guid.Parse(PluginId);
    public override string Description => "Explainable rules for metadata language, country, and optional replacement refreshes.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "MetadataRules", EmbeddedResourcePath = "Jellyfin.Plugin.MetadataRules.Web.config.html" }];
    public PluginConfiguration Settings
    {
        get
        {
            lock (_settingsGate)
            {
                var result = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(Configuration))!;
                result.Revision = SettingsRevision(result);
                return result;
            }
        }
    }
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration settings) throw new ArgumentException("Invalid Metadata Rules settings.");
        lock (_settingsGate)
        {
            if (!string.Equals(settings.Revision, Settings.Revision, StringComparison.Ordinal)) throw new InvalidOperationException("Settings changed in another editor. Reload before saving.");
            RuleDocuments.Parse(settings.RulesJson, Localization);
            if (settings.LibraryIds is null || settings.ExcludedItemIds is null || settings.LibraryIds.Any(x => !Guid.TryParse(x, out _)) || settings.ExcludedItemIds.Any(x => !Guid.TryParse(x, out _))) throw new ArgumentException("Library and exclusion IDs must be UUIDs.");
            if (settings.HistoryLimit is < 1 or > 200 || settings.ResultsLimit is < 100 or > 20000 || settings.MinimumRefreshIntervalMinutes is < 1 or > 525600) throw new ArgumentException("Retention and refresh interval are outside the supported limits.");
            settings.LibraryIds = settings.LibraryIds.Select(x => Guid.Parse(x).ToString("N")).Distinct().ToArray();
            settings.ExcludedItemIds = settings.ExcludedItemIds.Select(x => Guid.Parse(x).ToString("N")).Distinct().ToArray();
            settings.Revision = SettingsRevision(settings);
            base.UpdateConfiguration(settings);
        }
    }
    private static string SettingsRevision(PluginConfiguration config)
    {
        var copy = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config))!; copy.Revision = "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy)))).ToLowerInvariant();
    }
}

public static class RuleDocuments
{
    public static RuleDocument Parse(string json, ILocalizationManager? localization = null)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 262144) throw new ArgumentException("Rules must be a JSON document of at most 256 KiB.");
        var doc = RulesJson.Parse(json);
        var validator = new RuleValidator(languageCodes: localization?.GetCultures().Select(x => x.TwoLetterISOLanguageName), countryCodes: localization?.GetCountries().Select(x => x.TwoLetterISORegionName));
        foreach (var rule in doc.RuleSets)
        {
            if (rule.Scope.LibraryIds.Any(x => !Guid.TryParse(x, out _))) throw new ArgumentException("Rule library scopes must contain Jellyfin library UUIDs.");
            rule.Scope.LibraryIds = rule.Scope.LibraryIds.Select(x => Guid.Parse(x).ToString("N")).Distinct().ToList();
        }
        var validation = validator.Validate(doc);
        if (!validation.IsValid) throw new ArgumentException(string.Join("; ", validation.Errors.Select(e => e.Path + ": " + e.Message)));
        doc.Revision = validation.Revision;
        return doc;
    }
}
