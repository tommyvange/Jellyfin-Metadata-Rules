using MediaBrowser.Model.Plugins;
namespace Jellyfin.Plugin.MetadataRules.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool NightlyEnabled { get; set; } = true;
    public bool RunAfterScan { get; set; }
    public bool InstantEnabled { get; set; }
    public bool PathCaseSensitive { get; set; } = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
    public string[] LibraryIds { get; set; } = [];
    public string[] ExcludedItemIds { get; set; } = [];
    public string RulesJson { get; set; } = "{\"SchemaVersion\":1,\"RuleSets\":[]}";
    public string Revision { get; set; } = "";
    public int HistoryLimit { get; set; } = 50;
    public int ResultsLimit { get; set; } = 10000;
    public int MinimumRefreshIntervalMinutes { get; set; } = 1440;
}
