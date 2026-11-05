using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.MetadataRules;
using Jellyfin.Plugin.MetadataRules.Configuration;
using Jellyfin.Plugin.MetadataRules.Engine;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Serialization;

var root = Path.Combine(Path.GetTempPath(), "metadata-rules-configuration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int assertions = 0, writes = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++; Console.WriteLine("PASS " + message);
}
PluginConfiguration Copy(PluginConfiguration config) => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(config))!;
var paths = InterfaceProxy<IApplicationPaths>.Create((method, _) => method.Name.StartsWith("get_", StringComparison.Ordinal)
    ? Path.Combine(root, method.Name[4..]) : throw new InvalidOperationException("Unexpected path operation: " + method.Name));
var serializer = InterfaceProxy<IXmlSerializer>.Create((method, args) =>
{
    switch (method.Name)
    {
        case "SerializeToFile":
            var target = (string)args[1]!; Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var stream = File.Create(target)) new XmlSerializer(args[0]!.GetType()).Serialize(stream, args[0]);
            writes++; return null;
        case "DeserializeFromFile":
            using (var stream = File.OpenRead((string)args[1]!)) return new XmlSerializer((Type)args[0]!).Deserialize(stream);
        case "SerializeToStream": new XmlSerializer(args[0]!.GetType()).Serialize((Stream)args[1]!, args[0]); return null;
        case "DeserializeFromStream": return new XmlSerializer((Type)args[0]!).Deserialize((Stream)args[1]!);
        case "DeserializeFromBytes": using (var stream = new MemoryStream((byte[])args[1]!)) return new XmlSerializer((Type)args[0]!).Deserialize(stream);
        default: throw new InvalidOperationException("Unexpected serializer operation: " + method.Name);
    }
});
var localization = InterfaceProxy<ILocalizationManager>.Create((method, _) => method.Name switch
{
    "GetCultures" => new[] { new CultureDto("no", "Norwegian", "no", ["nor"]), new CultureDto("nb", "Bokmål", "nb", ["nob"]), new CultureDto("en", "English", "en", ["eng"]) },
    "GetCountries" => new[] { new CountryInfo { Name = "Norway", TwoLetterISORegionName = "NO", ThreeLetterISORegionName = "NOR" }, new CountryInfo { Name = "United States", TwoLetterISORegionName = "US", ThreeLetterISORegionName = "USA" } },
    _ => throw new InvalidOperationException("Unexpected localization operation: " + method.Name)
});
try
{
    var plugin = new Plugin(paths, serializer, localization);
    Check(plugin.Configuration.Revision.Length == 64 && plugin.Configuration.Revision == plugin.Settings.Revision, "initial native configuration revision matches settings revision");
    Check(File.Exists(plugin.ConfigurationFilePath), "native BasePlugin creates an XML configuration file");
    var document = new RuleDocument { RuleSets = [new() { Name = "Norsk & <metadata>", Branches = [new() { Condition = new() { FieldId = "item.originalLanguage", OperatorId = "eq", Value = RuleValue.TextValue("norwegian") }, Actions = new() { Language = new() { Mode = "set", Value = "no" }, Country = new() { Mode = "set", Value = "NO" } } }] }] };
    var valid = plugin.Settings; valid.RulesJson = RulesJson.Serialize(document);
    valid.RunAfterScan = true; valid.LibraryIds = [Guid.NewGuid().ToString("D")];
    var previousRevision = valid.Revision;
    plugin.UpdateConfiguration(valid);
    Check(plugin.Settings.Revision != previousRevision && plugin.Configuration.Revision == plugin.Settings.Revision, "valid native update advances the optimistic revision");
    var xml = XDocument.Load(plugin.ConfigurationFilePath);
    Check(xml.Root!.Element("RulesJson")?.Value == valid.RulesJson, "nested rule document persists as an exact XML string value");
    Check(plugin.Configuration.RulesJson == valid.RulesJson && plugin.Settings.RunAfterScan, "valid rule update becomes the active configuration");
    Check(plugin.Settings.LibraryIds.Single().Length == 32, "valid native configuration normalizes Jellyfin library IDs");
    var baseline = File.ReadAllBytes(plugin.ConfigurationFilePath); var baselineActive = JsonSerializer.Serialize(plugin.Settings); var baselineWrites = writes;
    void Reject(PluginConfiguration invalid, string name, Type exception)
    {
        Exception? caught = null; try { plugin.UpdateConfiguration(invalid); } catch (Exception e) { caught = e; }
        Check(caught is not null && exception.IsAssignableFrom(caught.GetType()), name + " is rejected through native UpdateConfiguration");
        Check(JsonSerializer.Serialize(plugin.Settings) == baselineActive && File.ReadAllBytes(plugin.ConfigurationFilePath).SequenceEqual(baseline) && writes == baselineWrites, name + " preserves active and persisted settings");
    }
    var badSchema = plugin.Settings; var invalidDocument = RulesJson.Parse(badSchema.RulesJson); invalidDocument.SchemaVersion = 99; badSchema.RulesJson = RulesJson.Serialize(invalidDocument);
    Reject(badSchema, "unsupported rule schema", typeof(ArgumentException));
    var badOperator = plugin.Settings; invalidDocument = RulesJson.Parse(badOperator.RulesJson); invalidDocument.RuleSets[0].Branches[0].Condition!.OperatorId = "executeCSharp"; badOperator.RulesJson = RulesJson.Serialize(invalidDocument);
    Reject(badOperator, "unsupported rule operator", typeof(ArgumentException));
    var firstEditor = plugin.Settings; var secondEditor = Copy(firstEditor); firstEditor.NightlyEnabled = false; plugin.UpdateConfiguration(firstEditor);
    baseline = File.ReadAllBytes(plugin.ConfigurationFilePath); baselineActive = JsonSerializer.Serialize(plugin.Settings); baselineWrites = writes;
    secondEditor.InstantEnabled = true;
    Reject(secondEditor, "stale second editor revision", typeof(InvalidOperationException));
    var reloaded = new Plugin(paths, serializer, localization);
    Check(reloaded.Settings.RulesJson == plugin.Settings.RulesJson && !reloaded.Settings.NightlyEnabled && reloaded.Configuration.Revision == reloaded.Settings.Revision, "persisted XML rules and ordinary settings reload with a consistent native revision");
    Console.WriteLine($"{assertions} configuration assertions passed.");
}
finally { Directory.Delete(root, true); }

public class InterfaceProxy<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[], object?> _handler = null!;
    public static T Create(Func<MethodInfo, object?[], object?> handler)
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy<T>>();
        ((InterfaceProxy<T>)(object)proxy)._handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args ?? []);
}
