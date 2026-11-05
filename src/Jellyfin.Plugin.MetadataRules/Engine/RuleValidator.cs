using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetadataRules.Engine;

public sealed record ValidationIssue(string Path, string Message);
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Errors, IReadOnlyList<ValidationIssue> Warnings, string Revision)
{
    public bool IsValid => Errors.Count == 0;
}

public static class RuleRevision
{
    public static string Compute(RuleDocument document)
    {
        var json = JsonSerializer.SerializeToNode(document, RulesJson.Options)!.AsObject();
        json.Remove("revision");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = false })))).ToLowerInvariant();
    }
}

public sealed partial class RuleValidator
{
    public const int MaximumDepth = 24;
    public const int MaximumNodes = 2048;
    public const int MaximumRuleSets = 128;
    public const int MaximumBranches = 128;
    private readonly FieldRegistry _registry;
    private readonly HashSet<string> _languages;
    private readonly HashSet<string> _countries;
    public RuleValidator(FieldRegistry? registry = null, IEnumerable<string>? languageCodes = null, IEnumerable<string>? countryCodes = null)
    {
        _registry = registry ?? new();
        _languages = new(languageCodes ?? CultureInfo.GetCultures(CultureTypes.NeutralCultures).Select(x => x.TwoLetterISOLanguageName), StringComparer.OrdinalIgnoreCase);
        _languages.UnionWith(["no", "nb", "nn"]);
        _countries = new(countryCodes ?? CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(x => new RegionInfo(x.Name).TwoLetterISORegionName), StringComparer.OrdinalIgnoreCase);
    }
    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodePattern();

    public ValidationResult Validate(RuleDocument document)
    {
        var errors = new List<ValidationIssue>(); var warnings = new List<ValidationIssue>();
        void Error(string path, string message) => errors.Add(new(path, message));
        var ids = new HashSet<string>(StringComparer.Ordinal); int nodeCount = 0;
        void Id(string id, string path)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) Error(path, "A stable ID of 1–128 characters is required.");
            else if (!ids.Add(id)) Error(path, "IDs must be unique across rulesets, branches, and conditions.");
        }
        void Value(ConditionNode node, FieldDefinition field, string path)
        {
            var op = node.OperatorId;
            if (op is "isKnown" or "isMissing" or "isEmpty" or "isNotEmpty" or "isTrue" or "isFalse") return;
            var value = node.Value;
            if (value is null) { Error(path, "The operator requires a typed value."); return; }
            if (value.Text?.Length > 2048 || value.Values.Count > 256 || value.Values.Any(x => x.Length > 2048)) Error(path, "Comparison values exceed the supported size limit.");
            if (op is "in" or "notIn" or "containsAny" or "containsAll" or "containsNone")
            {
                if (value.Kind != "values" || value.Values.Count == 0) Error(path, "This operator requires a nonempty list of values.");
            }
            else if (field.Type == "number" || op is "ageLessThanDays" or "ageGreaterThanDays")
            {
                if (value.Kind != "number" || value.Number is null || !double.IsFinite(value.Number.Value)) Error(path, "This operator requires a finite number.");
                if (op == "range" && (value.UpperNumber is null || value.UpperNumber < value.Number || !double.IsFinite(value.UpperNumber.Value))) Error(path, "A numeric range requires an upper bound at least as large as the lower bound.");
                if (op is "ageLessThanDays" or "ageGreaterThanDays" && value.Number < 0) Error(path, "Age in days cannot be negative.");
            }
            else if (field.Type == "date")
            {
                if (value.Kind != "date" || value.Date is null) Error(path, "This operator requires a date with timezone.");
                if (op == "range" && (value.UpperDate is null || value.UpperDate < value.Date)) Error(path, "A date range requires an ordered upper date.");
            }
            else if (value.Kind != "text" || string.IsNullOrWhiteSpace(value.Text)) Error(path, "This operator requires a text value.");
            var values = value.Kind == "values" ? value.Values : value.Text is null ? [] : new List<string> { value.Text };
            if (field.Type == "language") foreach (var code in values)
            {
                var normalized = LanguageNormalizer.Normalize(code);
                if (normalized != "norwegian" && (!LanguageCodePattern().IsMatch(normalized) || !_languages.Contains(normalized))) Error(path, "Unknown language code: " + code);
            }
            if (field.Comparison == "countryCode") foreach (var code in values) if (!_countries.Contains(code)) Error(path, "Unknown country code: " + code);
        }
        void Node(ConditionNode node, string path, int depth, IReadOnlyList<string> kinds)
        {
            Id(node.Id, path + ".id");
            if (++nodeCount > MaximumNodes) { if (nodeCount == MaximumNodes + 1) Error(path, "Too many condition nodes."); return; }
            if (depth > MaximumDepth) { Error(path, "Condition nesting exceeds " + MaximumDepth + "."); return; }
            switch (node.Kind)
            {
                case "All": case "Any":
                    if (node.Children.Count == 0) Error(path, "Boolean groups require at least one child.");
                    if (node.Child is not null || node.FieldId is not null || node.CollectionId is not null) Error(path, "Boolean groups may contain children only.");
                    for (int i = 0; i < node.Children.Count; i++) Node(node.Children[i], path + ".children[" + i + "]", depth + 1, kinds);
                    break;
                case "Not":
                    if (node.Child is null) Error(path, "NOT requires one child.");
                    else Node(node.Child, path + ".child", depth + 1, kinds);
                    if (node.Children.Count > 0 || node.FieldId is not null || node.CollectionId is not null) Error(path, "NOT accepts one child only.");
                    break;
                case "Predicate":
                    var field = _registry.Find(node.FieldId);
                    if (field is null) Error(path + ".fieldId", "Unknown field ID.");
                    else
                    {
                        if (!field.Operators.Contains(node.OperatorId ?? string.Empty)) Error(path + ".operatorId", "This operator is not supported for the selected field.");
                        else Value(node, field, path + ".value");
                        if (!field.ItemKinds.Intersect(kinds).Any()) Error(path, "This field does not apply in the current item/collection scope.");
                    }
                    if (node.Children.Count > 0 || node.Child is not null || node.CollectionId is not null) Error(path, "Predicates cannot contain nested conditions.");
                    break;
                case "Quantifier":
                    var collection = _registry.FindCollection(node.CollectionId);
                    if (collection is null) Error(path + ".collectionId", "Unknown collection ID.");
                    else if (!collection.ItemKinds.Intersect(kinds).Any()) Error(path, "This collection is not available in the current scope.");
                    if (node.Quantifier is not ("any" or "all" or "none" or "count")) Error(path + ".quantifier", "Unknown quantifier.");
                    if (node.Quantifier == "count")
                    {
                        if (node.CountOperator is not ("eq" or "neq" or "lt" or "lte" or "gt" or "gte" or "range")) Error(path + ".countOperator", "Unsupported count operator.");
                        if (node.CountValue?.Kind != "number" || node.CountValue.Number is null || node.CountValue.Number < 0 || node.CountValue.Number != Math.Floor(node.CountValue.Number.Value) || !double.IsFinite(node.CountValue.Number.Value)) Error(path + ".countValue", "Count requires a nonnegative integer.");
                        if (node.CountOperator == "range" && (node.CountValue?.UpperNumber is null || node.CountValue.UpperNumber < node.CountValue.Number || node.CountValue.UpperNumber != Math.Floor(node.CountValue.UpperNumber.Value))) Error(path + ".countValue", "Count range requires an ordered integer upper bound.");
                    }
                    if (node.Child is null) Error(path, "Collection conditions require a child predicate/group.");
                    else Node(node.Child, path + ".child", depth + 1, collection?.ElementKinds ?? kinds);
                    if (node.Children.Count > 0 || node.FieldId is not null) Error(path, "A quantifier accepts one child only.");
                    break;
                default: Error(path + ".kind", "Unknown condition kind."); break;
            }
        }
        if (document.SchemaVersion != 1) Error("schemaVersion", "Only rules schema version 1 is supported.");
        if (document.RuleSets.Count > MaximumRuleSets) Error("ruleSets", "Too many rulesets.");
        for (int r = 0; r < document.RuleSets.Count; r++)
        {
            var rule = document.RuleSets[r]; var path = "ruleSets[" + r + "]"; Id(rule.Id, path + ".id");
            if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 256) Error(path + ".name", "A ruleset name of 1–256 characters is required.");
            if (rule.Scope.ItemKinds.Count == 0 || rule.Scope.ItemKinds.Except(FieldRegistry.ItemKinds).Any()) Error(path + ".scope.itemKinds", "Select valid media item kinds.");
            if (rule.Scope.LibraryIds.Count > 256 || rule.Scope.LibraryIds.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128)) Error(path + ".scope.libraryIds", "Library scopes require stable library IDs.");
            if (rule.Branches.Count == 0 || rule.Branches.Count > MaximumBranches) Error(path + ".branches", "A ruleset requires 1–128 branches.");
            for (int b = 0; b < rule.Branches.Count; b++)
            {
                var branch = rule.Branches[b]; var bp = path + ".branches[" + b + "]"; Id(branch.Id, bp + ".id");
                if (branch.IsElse)
                {
                    if (b != rule.Branches.Count - 1 || branch.Condition is not null) Error(bp, "ELSE must be terminal and must not have a condition.");
                    if (r < document.RuleSets.Count - 1) warnings.Add(new(bp, "A broad ELSE may shadow later overlapping rulesets."));
                }
                else if (branch.Condition is null) Error(bp, "IF / ELSE IF requires a condition.");
                else Node(branch.Condition, bp + ".condition", 1, rule.Scope.ItemKinds);
                var actions = branch.Actions;
                void Preference(PreferenceAction action, string name, HashSet<string> catalog)
                {
                    if (action.Mode is not ("leave" or "set" or "clear")) Error(bp + ".actions." + name, "Unknown preference action.");
                    if (action.Mode == "set" && (string.IsNullOrWhiteSpace(action.Value) || !catalog.Contains(name == "language" ? LanguageNormalizer.Normalize(action.Value) : action.Value))) Error(bp + ".actions." + name, "Select a supported catalog value.");
                    if (action.Mode != "set" && !string.IsNullOrEmpty(action.Value)) Error(bp + ".actions." + name, "Leave and clear actions must not include an output value.");
                }
                Preference(actions.Language, "language", _languages); Preference(actions.Country, "country", _countries);
                if (actions.WriteScope is not ("item" or "seasons" or "episodes" or "all") || actions.RefreshScope is not ("item" or "seasons" or "episodes" or "all")) Error(bp + ".actions", "Unsupported action scope.");
                if (actions.RefreshMode is not ("none" or "metadata" or "metadataAndImages")) Error(bp + ".actions.refreshMode", "Unsupported refresh mode.");
                if (actions.RepeatPolicy is not ("newDecision" or "preferenceChange" or "everyRun")) Error(bp + ".actions.repeatPolicy", "Unsupported repeat policy.");
                if (actions.MinimumIntervalMinutes is < 1 or > 525600) Error(bp + ".actions.minimumIntervalMinutes", "Minimum interval must be 1–525600 minutes.");
            }
        }
        for (int i = 0; i < document.RuleSets.Count; i++) for (int j = i + 1; j < document.RuleSets.Count; j++)
        {
            var a = document.RuleSets[i]; var b = document.RuleSets[j];
            if (a.Enabled && b.Enabled && a.Scope.ItemKinds.Intersect(b.Scope.ItemKinds).Any() && (a.Scope.LibraryIds.Count == 0 || b.Scope.LibraryIds.Count == 0 || a.Scope.LibraryIds.Intersect(b.Scope.LibraryIds).Any())) warnings.Add(new("ruleSets[" + j + "]", "Scope overlaps '" + a.Name + "'; the first matching branch wins."));
        }
        return new(errors, warnings, RuleRevision.Compute(document));
    }
}
