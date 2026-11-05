using Jellyfin.Plugin.MetadataRules.Engine;
using System.Text.Json;

var timestamp = DateTimeOffset.Parse("2026-10-31T12:00:00Z");
var evaluator = new RuleEvaluator();
var planner = new ActionPlanner(evaluator);
int passed = 0, failed = 0;
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
ConditionNode P(string field, string op, RuleValue? value = null) => new() { FieldId = field, OperatorId = op, Value = value };
ConditionNode Text(string field, string value, string op = "eq") => P(field, op, RuleValue.TextValue(value));
ConditionNode Number(string field, double value, string op = "gte") => P(field, op, RuleValue.NumberValue(value));
ConditionNode Group(string kind, params ConditionNode[] children) => new() { Kind = kind, Children = [.. children] };
ConditionNode Q(string collection, ConditionNode child, string quantifier = "any") => new() { Kind = "Quantifier", CollectionId = collection, Quantifier = quantifier, Child = child };
MediaFacts Facts(string id = "movie", string kind = "Movie", Dictionary<string, FactValue>? fields = null, Dictionary<string, FactCollection>? collections = null) => new()
{
    ItemId = id, ItemKind = kind, Title = id, LibraryId = "library-a", Timestamp = timestamp,
    Fields = fields ?? new(), Collections = collections ?? new()
};
RuleActions Locale(string language = "no", string country = "NO") => new() { Language = new() { Mode = "set", Value = language }, Country = new() { Mode = "set", Value = country } };
RuleDocument Doc(ConditionNode condition, RuleActions? actions = null) => new() { RuleSets = [new() { Name = "Rule", Branches = [new() { Name = "IF", Condition = condition, Actions = actions ?? Locale() }] }] };
TruthValue Eval(ConditionNode condition, MediaFacts facts) => evaluator.EvaluateCondition(condition, facts, timestamp).Result!.Value;
var original = Text("item.originalLanguage", "norwegian");
var audio = Q("versions", Q("audioTracks", Text("audio.language", "norwegian")));
var preferences = new Dictionary<string, FactValue>
{
    ["metadata.language"] = FactValue.Known(""), ["metadata.country"] = FactValue.Known(""),
    ["metadata.effectiveLanguage"] = FactValue.Known("en"), ["metadata.effectiveCountry"] = FactValue.Known("US"),
    ["metadata.inheritedLanguage"] = FactValue.Known("en"), ["metadata.inheritedCountry"] = FactValue.Known("US")
};
MediaFacts Track(string id, string? language, int channels = 2, bool isDefault = false) => Facts(id, "AudioTrack", new()
{
    ["audio.language"] = language is null ? FactValue.NotReady() : FactValue.Known(language),
    ["audio.channels"] = FactValue.Known(channels), ["audio.default"] = FactValue.Known(isDefault), ["audio.index"] = FactValue.Known(2)
});
MediaFacts Playable(string id, params MediaFacts[] tracks) => Facts(id, "Version", collections: new() { ["audioTracks"] = FactCollection.Known(tracks) });

Test("Norwegian metadata aliases and distinct exact variants", () =>
{
    foreach (var code in new[] { "nor", "no", "nob", "nb", "nno", "nn", " NOB " }) Check(Eval(original, Facts(fields: new() { ["item.originalLanguage"] = FactValue.Known(code) })) == TruthValue.True, code);
    Check(Eval(Text("item.originalLanguage", "nb"), Facts(fields: new() { ["item.originalLanguage"] = FactValue.Known("nno") })) == TruthValue.False);
    Check(Eval(Text("item.originalLanguage", "nb"), Facts(fields: new() { ["item.originalLanguage"] = FactValue.Known("nob") })) == TruthValue.True);
});
Test("Original language example creates deterministic locale plan", () =>
{
    var fields = new Dictionary<string, FactValue>(preferences) { ["item.originalLanguage"] = FactValue.Known("nor") };
    var facts = Facts(fields: fields); var doc = Doc(original); var plan = planner.Plan(doc, facts, timestamp);
    Check(plan.Evaluation.Status == "Matched"); Check(plan.Targets.Single().After.Language == "no"); Check(plan.Targets.Single().After.Country == "NO");
    Check(plan.ChangedCount == 1 && plan.AffectedCount == 1 && plan.RefreshCount == 0);
    Check(plan.DecisionKey == planner.Plan(doc, facts, timestamp.AddDays(1)).DecisionKey);
});
Test("English original movie with nondefault Norwegian dub selects second branch", () =>
{
    var facts = Facts(fields: new() { ["item.originalLanguage"] = FactValue.Known("en") }, collections: new() { ["versions"] = FactCollection.Known(Playable("A", Track("english", "eng", isDefault: true), Track("norwegian", "nob"))) });
    var doc = Doc(original); doc.RuleSets[0].Branches.Add(new() { Name = "ELSE IF", Condition = audio, Actions = Locale() });
    var result = evaluator.Evaluate(doc, facts, timestamp);
    Check(result.Status == "Matched" && result.BranchName == "ELSE IF"); Check((string)facts.Get("item.originalLanguage").Value! == "en");
    var trace = result.Trace.Last().Condition!; Check(trace.Children[0].Children[1].StreamIndex == 2);
});
Test("Track conditions correlate on one track", () =>
{
    var condition = Q("audioTracks", Group("All", Text("audio.language", "norwegian"), Number("audio.channels", 6)));
    Check(Eval(condition, Playable("A", Track("no-stereo", "nb", 2), Track("en-surround", "en", 6))) == TruthValue.False);
    Check(Eval(condition, Playable("A", Track("no-surround", "nb", 6))) == TruthValue.True);
});
Test("Missing original language blocks lower branch and ELSE", () =>
{
    var facts = Facts(fields: new() { ["item.originalLanguage"] = FactValue.NotReady() }, collections: new() { ["versions"] = FactCollection.Known(Playable("A", Track("norwegian", "nb"))) });
    var doc = Doc(original); doc.RuleSets[0].Branches.Add(new() { Condition = audio }); doc.RuleSets[0].Branches.Add(new() { IsElse = true });
    var result = evaluator.Evaluate(doc, facts, timestamp); Check(result.Status == "Deferred"); Check(result.Trace.Skip(1).All(x => !x.Evaluated && x.Result is null));
});
Test("Unknown precedence also blocks later rulesets", () =>
{
    var doc = Doc(original); doc.RuleSets.Add(new() { Order = 1, Branches = [new() { IsElse = true }] });
    Check(evaluator.Evaluate(doc, Facts(), timestamp).Status == "Deferred");
});
Test("Combined OR can establish positive despite unknown other source", () =>
{
    var facts = Facts(collections: new() { ["versions"] = FactCollection.Known(Playable("A", Track("dub", "no"))) });
    Check(Eval(Group("Any", original, audio), facts) == TruthValue.True);
});
Test("Three-valued AND OR NOT truth tables", () =>
{
    var facts = Facts(fields: new() { ["item.title"] = FactValue.Known("yes"), ["item.originalLanguage"] = FactValue.NotReady() });
    var yes = Text("item.title", "yes"); var no = Text("item.title", "no"); var unknown = original;
    Check(Eval(Group("All", unknown, no), facts) == TruthValue.False); Check(Eval(Group("Any", unknown, yes), facts) == TruthValue.True);
    Check(Eval(Group("All", unknown, yes), facts) == TruthValue.Unknown); Check(Eval(Group("Any", unknown, no), facts) == TruthValue.Unknown);
    Check(Eval(new() { Kind = "Not", Child = unknown }, facts) == TruthValue.Unknown);
    Check(Eval(Group("All", Group("Any", no, yes), new() { Kind = "Not", Child = no }), facts) == TruthValue.True);
});
Test("Short-circuited conditions are not evaluated", () =>
{
    var result = evaluator.EvaluateCondition(Group("Any", Text("item.title", "movie"), original), Facts(), timestamp);
    Check(result.Children[1].Evaluated == false && result.Children[1].Result is null);
});
Test("Negative language conditions preserve unknown", () =>
{
    foreach (var fact in new[] { FactValue.Absent(), FactValue.NotReady(), FactValue.Unsupported(), FactValue.ReadError("error"), FactValue.Known("und"), FactValue.Known("mul"), FactValue.Known("mis"), FactValue.Known("unk"), FactValue.Known("") })
        Check(Eval(Text("item.originalLanguage", "no", "neq"), Facts(fields: new() { ["item.originalLanguage"] = fact })) == TruthValue.Unknown);
});
Test("Availability distinguishes unfinished from confirmed missing", () =>
{
    Check(Eval(P("item.originalLanguage", "isMissing"), Facts(fields: new() { ["item.originalLanguage"] = FactValue.Absent() })) == TruthValue.True);
    Check(Eval(P("item.originalLanguage", "isMissing"), Facts(fields: new() { ["item.originalLanguage"] = FactValue.NotReady() })) == TruthValue.Unknown);
    Check(Eval(P("item.originalLanguage", "isKnown"), Facts(fields: new() { ["item.originalLanguage"] = FactValue.Unsupported() })) == TruthValue.False);
});
Test("Known empty collections have explicit ANY NONE ALL semantics", () =>
{
    var facts = Playable("A");
    Check(Eval(Q("audioTracks", Text("audio.language", "no")), facts) == TruthValue.False);
    Check(Eval(Q("audioTracks", Text("audio.language", "no"), "none"), facts) == TruthValue.True);
    Check(Eval(Q("audioTracks", Text("audio.language", "no"), "all"), facts) == TruthValue.False);
    Check(Eval(Q("audioTracks", Text("audio.language", "no")), Facts("A", "Version", collections: new() { ["audioTracks"] = FactCollection.NotReady() })) == TruthValue.Unknown);
});
Test("Episode aggregates preserve supporting episode and unknown coverage", () =>
{
    var episode1 = Facts("episode1", "Episode", collections: new() { ["audioTracks"] = FactCollection.Known(Track("track1", "nob")) });
    var episode2 = Facts("episode2", "Episode", collections: new() { ["audioTracks"] = FactCollection.NotReady() });
    var series = Facts("show", "Series", collections: new() { ["episodes"] = FactCollection.Known(episode1, episode2) });
    var child = Q("audioTracks", Text("audio.language", "norwegian"));
    var any = evaluator.EvaluateCondition(Q("episodes", child), series, timestamp);
    Check(any.Result == TruthValue.True && any.UnknownCount == 1 && any.TotalCount == 2); Check(any.Children[0].EvidenceItemId == "episode1");
    Check(Eval(Q("episodes", child, "all"), series) == TruthValue.Unknown); Check(Eval(Q("episodes", child, "none"), series) == TruthValue.False);
});
Test("Count comparisons use lower and upper bounds", () =>
{
    var facts = Playable("A", Track("known", "nb"), Track("unknown", null));
    var count = Q("audioTracks", Text("audio.language", "norwegian"), "count"); count.CountValue = RuleValue.NumberValue(1);
    Check(Eval(count, facts) == TruthValue.True); count.CountOperator = "eq"; Check(Eval(count, facts) == TruthValue.Unknown);
    count.CountValue = RuleValue.NumberValue(3); Check(Eval(count, facts) == TruthValue.False);
});
Test("Text comparison is ordinal case insensitive with explicit filesystem behavior", () =>
{
    var facts = Facts(fields: new() { ["file.path"] = FactValue.Known("/Media/Norway/Film.mkv") });
    Check(Eval(Text("file.path", "/media/norway", "startsWith"), facts) == TruthValue.True);
    Check(Eval(Text("file.path", "/media/norway", "startsWith"), facts with { PathCaseSensitive = true }) == TruthValue.False);
    foreach (var (op, expected) in new[] { ("eq", "MOVIE"), ("neq", "other"), ("contains", "OV"), ("notContains", "xx"), ("startsWith", "mo"), ("endsWith", "IE") }) Check(Eval(Text("item.title", expected, op), Facts()) == TruthValue.True, op);
});
Test("Set membership does not become substring search", () =>
{
    var facts = Facts(fields: new() { ["item.tags"] = FactValue.Known(new[] { "Norwegian", "Drama" }) });
    Check(Eval(Text("item.tags", "Nor", "containsValue"), facts) == TruthValue.False);
    foreach (var (op, values) in new[] { ("containsAny", new[] { "Drama", "Comedy" }), ("containsAll", new[] { "DRAMA", "Norwegian" }), ("containsNone", new[] { "Comedy" }) }) Check(Eval(P("item.tags", op, RuleValue.SetValue(values)), facts) == TruthValue.True, op);
});
Test("All scalar numeric date boolean enum operators", () =>
{
    var facts = Facts(fields: new() { ["item.productionYear"] = FactValue.Known(2026), ["item.dateCreated"] = FactValue.Known(timestamp.AddDays(-10)), ["state.locked"] = FactValue.Known(false) });
    foreach (var (op, n) in new[] { ("eq", 2026d), ("neq", 2025d), ("lt", 2027d), ("lte", 2026d), ("gt", 2025d), ("gte", 2026d) }) Check(Eval(Number("item.productionYear", n, op), facts) == TruthValue.True, op);
    Check(Eval(P("item.productionYear", "range", new() { Kind = "number", Number = 2020, UpperNumber = 2026 }), facts) == TruthValue.True);
    Check(Eval(P("item.dateCreated", "before", new() { Kind = "date", Date = timestamp }), facts) == TruthValue.True);
    Check(Eval(P("item.dateCreated", "after", new() { Kind = "date", Date = timestamp.AddDays(-20) }), facts) == TruthValue.True);
    Check(Eval(P("item.dateCreated", "range", new() { Kind = "date", Date = timestamp.AddDays(-20), UpperDate = timestamp }), facts) == TruthValue.True);
    Check(Eval(Number("item.dateCreated", 9, "ageGreaterThanDays"), facts) == TruthValue.True);
    Check(Eval(Number("item.dateCreated", 11, "ageLessThanDays"), facts) == TruthValue.True);
    Check(Eval(P("state.locked", "isFalse"), facts) == TruthValue.True);
    Check(Eval(P("item.kind", "in", RuleValue.SetValue("Movie", "Series")), facts) == TruthValue.True);
    Check(Eval(P("item.kind", "notIn", RuleValue.SetValue("Episode")), facts) == TruthValue.True);
});
Test("Clear overrides restores inherited locale", () =>
{
    var fields = new Dictionary<string, FactValue>(preferences) { ["metadata.language"] = FactValue.Known("nb"), ["metadata.effectiveLanguage"] = FactValue.Known("nb") };
    var action = new RuleActions { Language = new() { Mode = "clear" } };
    var plan = planner.Plan(Doc(Text("item.kind", "Movie"), action), Facts(fields: fields), timestamp);
    Check(plan.Targets.Single().After.Language is null && plan.Targets.Single().After.EffectiveLanguage == "en");
});
Test("Descendant override preservation and inherited effects are separate", () =>
{
    var plain = Facts("plain", "Episode", new(preferences)) with { ParentId = "show" };
    var overrideFields = new Dictionary<string, FactValue>(preferences) { ["metadata.language"] = FactValue.Known("fr"), ["metadata.effectiveLanguage"] = FactValue.Known("fr") };
    var overridden = Facts("override", "Episode", overrideFields) with { ParentId = "show" };
    var show = Facts("show", "Series", new(preferences)) with { Descendants = new[] { plain, overridden } };
    var doc = Doc(Text("item.kind", "Series")); var plan = planner.Plan(doc, show, timestamp);
    Check(plan.Targets.Count == 1); Check(plan.InheritedEffects.Count == 2); Check(plan.InheritedEffects.Single(x => x.ItemId == "override").After.EffectiveLanguage == "fr");
    doc.RuleSets[0].Branches[0].Actions.WriteScope = "episodes"; plan = planner.Plan(doc, show, timestamp);
    Check(plan.Targets.Single(x => x.ItemId == "override").After.Language == "fr");
    doc.RuleSets[0].Branches[0].Actions.OverwriteDescendantOverrides = true; plan = planner.Plan(doc, show, timestamp);
    Check(plan.Targets.Single(x => x.ItemId == "override").After.Language == "no");
});
Test("Locked source skips all writes and refresh", () =>
{
    var fields = new Dictionary<string, FactValue>(preferences) { ["state.locked"] = FactValue.Known(true) }; var action = Locale(); action.RefreshMode = "metadataAndImages";
    var plan = planner.Plan(Doc(Text("item.kind", "Movie"), action), Facts(fields: fields), timestamp);
    Check(plan.Evaluation.Status == "Skipped" && plan.AffectedCount == 0 && plan.Targets.Single().SkippedLocked);
});
Test("Refresh-only plans and unique affected count", () =>
{
    var doc = Doc(Text("item.kind", "Movie"), new() { RefreshMode = "metadata" });
    var plan = planner.Plan(doc, Facts(fields: new(preferences)), timestamp);
    Check(plan.ChangedCount == 0 && plan.RefreshCount == 1 && plan.AffectedCount == 1);
    doc.RuleSets[0].Branches[0].Actions = Locale(); doc.RuleSets[0].Branches[0].Actions.RefreshMode = "metadataAndImages";
    plan = planner.Plan(doc, Facts(fields: new(preferences)), timestamp); Check(plan.ChangedCount == 1 && plan.RefreshCount == 1 && plan.AffectedCount == 1);
});
Test("Decision keys survive output writes and irrelevant timestamp changes", () =>
{
    var fields = new Dictionary<string, FactValue>(preferences) { ["item.originalLanguage"] = FactValue.Known("nob"), ["item.dateLastRefreshed"] = FactValue.Known(timestamp.AddDays(-1)) };
    var doc = Doc(original); doc.RuleSets[0].Branches[0].Actions.RefreshMode = "metadata";
    var first = planner.Plan(doc, Facts(fields: fields), timestamp);
    fields["metadata.language"] = FactValue.Known("no"); fields["metadata.country"] = FactValue.Known("NO"); fields["metadata.effectiveLanguage"] = FactValue.Known("no"); fields["metadata.effectiveCountry"] = FactValue.Known("NO"); fields["item.dateLastRefreshed"] = FactValue.Known(timestamp);
    var second = planner.Plan(doc, Facts(fields: fields) with { Timestamp = timestamp.AddDays(1) }, timestamp.AddDays(1));
    Check(first.DecisionKey == second.DecisionKey && first.Targets[0].DecisionKey == second.Targets[0].DecisionKey);
    fields["item.originalLanguage"] = FactValue.Known("nn"); Check(planner.Plan(doc, Facts(fields: fields), timestamp).DecisionKey != second.DecisionKey);
});
Test("Cosmetic and unrelated rule edits do not repeat a winning action", () =>
{
    var facts = Facts(fields: new Dictionary<string, FactValue>(preferences) { ["item.originalLanguage"] = FactValue.Known("no") });
    var doc = Doc(Text("item.originalLanguage", "norwegian")); doc.RuleSets[0].Branches[0].Actions.RefreshMode = "metadata";
    var first = planner.Plan(doc, facts, timestamp);
    doc.RuleSets[0].Name = "Renamed ruleset"; doc.RuleSets[0].Branches[0].Name = "Renamed branch";
    doc.RuleSets.Add(new() { Name = "Unrelated later rule", Order = 10, Branches = [new() { Condition = Text("item.title", "other"), Actions = Locale("nb") }] });
    var cosmetic = planner.Plan(doc, facts, timestamp);
    Check(first.RuleRevision != cosmetic.RuleRevision && first.DecisionKey == cosmetic.DecisionKey);
    Check(first.Targets[0].DecisionKey == cosmetic.Targets[0].DecisionKey);
    doc.RuleSets[0].Branches[0].Actions.Language.Value = "nb";
    var actionEdit = planner.Plan(doc, facts, timestamp); Check(actionEdit.DecisionKey != cosmetic.DecisionKey);
    doc.RuleSets[0].Branches[0].Actions.Language.Value = "no";
    doc.RuleSets[0].Branches[0].Condition!.Value!.Text = "no";
    Check(planner.Plan(doc, facts, timestamp).DecisionKey != cosmetic.DecisionKey, "winning condition revision changes the action decision");
});
Test("Preceding branch comparison edits remain relevant decision inputs", () =>
{
    var facts = Facts(fields: new Dictionary<string, FactValue>(preferences) { ["item.originalLanguage"] = FactValue.Known("no") });
    var doc = Doc(Text("item.title", "other"));
    doc.RuleSets[0].Branches.Add(new() { Condition = Text("item.originalLanguage", "norwegian"), Actions = Locale() });
    var first = planner.Plan(doc, facts, timestamp);
    doc.RuleSets[0].Branches[0].Condition!.Value!.Text = "another";
    var changed = planner.Plan(doc, facts, timestamp);
    Check(first.Evaluation.BranchId == changed.Evaluation.BranchId && first.DecisionKey != changed.DecisionKey);
});
Test("Unordered set facts and quantifier evidence retain durable keys", () =>
{
    var condition = Group("All", P("item.tags", "containsAny", RuleValue.SetValue("Drama")), Q("versions", Q("audioTracks", Text("audio.language", "norwegian"))));
    var doc = Doc(condition); doc.RuleSets[0].Branches[0].Actions.RefreshMode = "metadata";
    var fields = new Dictionary<string, FactValue>(preferences) { ["item.tags"] = FactValue.Known(new[] { "Drama", "Norwegian" }) };
    var firstVersion = Playable("A", Track("a", "nb"), Track("b", "en"));
    var secondVersion = Playable("B", Track("c", "nn"));
    var first = planner.Plan(doc, Facts(fields: fields, collections: new() { ["versions"] = FactCollection.Known(firstVersion, secondVersion) }), timestamp);
    fields["item.tags"] = FactValue.Known(new[] { "Norwegian", "Drama" });
    var reversedTracks = firstVersion with { Collections = new Dictionary<string, FactCollection> { ["audioTracks"] = FactCollection.Known(firstVersion.Collections["audioTracks"].Elements.Reverse().ToArray()) } };
    var second = planner.Plan(doc, Facts(fields: fields, collections: new() { ["versions"] = FactCollection.Known(secondVersion, reversedTracks) }), timestamp);
    Check(first.DecisionKey == second.DecisionKey && first.Targets[0].DecisionKey == second.Targets[0].DecisionKey);
});
Test("Equivalent GUID library scope formats identify the same library", () =>
{
    var libraryId = Guid.NewGuid(); var doc = Doc(Text("item.kind", "Movie"));
    doc.RuleSets[0].Scope.LibraryIds = [libraryId.ToString("D")];
    Check(evaluator.Evaluate(doc, Facts() with { LibraryId = libraryId.ToString("N") }, timestamp).Status == "Matched");
});
Test("Draft serialization roundtrip preserves validation revision and plan", () =>
{
    var doc = Doc(original); var json = RulesJson.Serialize(doc); var parsed = RulesJson.Parse(json); var facts = Facts(fields: new() { ["item.originalLanguage"] = FactValue.Known("no") });
    Check(new RuleValidator().Validate(parsed).IsValid); Check(RuleRevision.Compute(doc) == RuleRevision.Compute(parsed));
    Check(planner.Plan(doc, facts, timestamp).DecisionKey == planner.Plan(parsed, facts, timestamp).DecisionKey);
    parsed.Revision = "untrusted-client-revision"; Check(RuleRevision.Compute(parsed) == RuleRevision.Compute(doc));
});
Test("Validation rejects schema fields operators values branches and output codes", () =>
{
    var validator = new RuleValidator();
    foreach (var bad in new[] { Doc(Text("arbitrary", "no")), Doc(Text("item.originalLanguage", "no", "contains")), Doc(P("item.productionYear", "gt", RuleValue.TextValue("wrong"))), Doc(Text("item.originalLanguage", "zz")), Doc(original, Locale("norwegian")), Doc(original, Locale(country: "ZZ")) }) Check(!validator.Validate(bad).IsValid);
    var doc = Doc(original); doc.SchemaVersion = 2; Check(!validator.Validate(doc).IsValid);
    doc = Doc(original); doc.RuleSets[0].Branches.Insert(0, new() { IsElse = true }); Check(!validator.Validate(doc).IsValid);
    doc = Doc(original); doc.RuleSets[0].Branches[0].Id = doc.RuleSets[0].Id; Check(!validator.Validate(doc).IsValid);
});
Test("Depth and node limits are enforced", () =>
{
    ConditionNode node = Text("item.title", "movie"); for (int i = 0; i < 30; i++) node = new() { Kind = "Not", Child = node };
    Check(!new RuleValidator().Validate(Doc(node)).IsValid);
    Check(!new RuleValidator().Validate(Doc(Group("All", Enumerable.Range(0, 2050).Select(_ => Text("item.title", "movie")).ToArray()))).IsValid);
});
Test("Registry covers required groups and loading dependencies", () =>
{
    var registry = new FieldRegistry(); Check(registry.Fields.Count >= 70); Check(registry.Fields.Select(x => x.Id).Distinct().Count() == registry.Fields.Count);
    var dependencies = registry.Dependencies(Doc(audio)); Check(dependencies.Contains("streams") && dependencies.Contains("versions"));
});
Test("Read errors are visible as errors rather than negative matches", () =>
{
    var result = evaluator.Evaluate(Doc(original), Facts(fields: new() { ["item.originalLanguage"] = FactValue.ReadError("fixture read failure") }), timestamp);
    Check(result.Status == "Error" && result.Trace[0].Condition!.Availability == FactState.ReadError);
});
Test("No match and explicit no-op stop preserve all preferences", () =>
{
    var facts = Facts(fields: new(preferences)); var doc = Doc(Text("item.kind", "Series")); Check(planner.Plan(doc, facts, timestamp).Targets.Count == 0);
    doc = Doc(Text("item.kind", "Movie"), new()); doc.RuleSets.Add(new() { Order = 1, Branches = [new() { IsElse = true, Actions = Locale() }] });
    var plan = planner.Plan(doc, facts, timestamp); Check(plan.Evaluation.Status == "Matched" && plan.AffectedCount == 0 && plan.Targets.Single().After.Language is null);
});
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
