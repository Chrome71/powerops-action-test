using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using PowerOps.Core.Model;
using PowerOps.Core.Readers;

namespace PowerOps.Core.Flows;

/// <summary>One assertion of a test file. <paramref name="Path"/> holds strings (properties) and integers (array items).</summary>
public sealed record FlowAssertion(string Kind, JsonNode? Expected, string? Action = null, int? Call = null, string? Name = null, IReadOnlyList<object>? Path = null);

/// <summary>A <c>*.flowtest.json</c> file: which flow to run, what the test gives it, and what must hold afterwards. <paramref name="Trigger"/> is what <c>triggerOutputs()</c> returns (null when the test gave none).</summary>
public sealed record FlowTestFile(
    string Flow,
    JsonObject? Trigger,
    IReadOnlyDictionary<string, IReadOnlyList<FlowMock>> Mocks,
    IReadOnlyDictionary<string, JsonNode?> EnvironmentVariables,
    string? UtcNow,
    string? Guid,
    JsonObject? Workflow,
    IReadOnlyList<FlowAssertion> Assertions)
{
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly string[] MockStatuses = ["Succeeded", "Failed"];
    private static readonly string[] ActionStatuses = ["Succeeded", "Failed", "Skipped"];
    private static readonly string[] RunStatuses = ["Succeeded", "Failed", "Cancelled", "Skipped"];

    private static readonly Dictionary<string, string[]> AssertionProperties = new()
    {
        ["runStatus"] = ["kind", "equals"],
        ["actionStatus"] = ["kind", "equals", "action", "call"],
        ["inputs"] = ["kind", "equals", "action", "call", "path"],
        ["outputs"] = ["kind", "equals", "action", "call", "path"],
        ["variable"] = ["kind", "equals", "name", "path"],
        ["response"] = ["kind", "equals", "path"],
    };

    /// <exception cref="InvalidDataException">The file is over 4 MB, or <see cref="Parse"/> rejects it; the message starts with the path.</exception>
    public static FlowTestFile Load(string path)
    {
        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new InvalidDataException($"{path}: the file is larger than 4 MB.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Reads and checks a test file. Any unknown property, wrong type or rule broken throws; <c>docs/schemas/flow-test.schema.json</c> describes the same format.</summary>
    /// <exception cref="InvalidDataException">The message starts with <paramref name="path"/>.</exception>
    public static FlowTestFile Parse(string json, string path)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new InvalidDataException($"{path}: the file is larger than 4 MB.");
        }

        JsonNode? root;
        try
        {
            root = ExpressionFunctions.ParseJson(json.TrimStart('﻿'));
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"{path}: not valid JSON: {e.Message}", e);
        }

        return new Reader(path).Read(root);
    }

    private sealed class Reader(string path)
    {
        private InvalidDataException Bad(string where, string problem) => new($"{path}: {(where.Length == 0 ? "" : where + ": ")}{problem}");

        public FlowTestFile Read(JsonNode? root)
        {
            var o = root as JsonObject ?? throw Bad("", "the file must be a JSON object.");
            Known(o, "", "$schema", "flow", "trigger", "mocks", "environmentVariables", "utcNow", "guid", "workflow", "assertions");
            Text(o, "$schema", "", false);
            var flow = Text(o, "flow", "", true)!;

            JsonObject? trigger = null;
            if (Obj(o, "trigger", "") is { } t)
            {
                Known(t, "trigger", "headers", "body");
                Obj(t, "headers", "trigger");
                trigger = [];
                foreach (var (key, value) in t)
                {
                    trigger[key] = value?.DeepClone();
                }
            }

            var mocks = new Dictionary<string, IReadOnlyList<FlowMock>>();
            foreach (var (name, node) in Obj(o, "mocks", "") ?? [])
            {
                var where = $"mocks['{name}']";
                mocks[name] = node switch
                {
                    JsonObject one => [Mock(one, where)],
                    JsonArray list => list.Select((m, i) => m is JsonObject mock ? Mock(mock, $"{where}[{i}]") : throw Bad($"{where}[{i}]", "a mock must be an object.")).ToList(),
                    _ => throw Bad(where, "must be a mock or a list of mocks."),
                };
            }

            var variables = new Dictionary<string, JsonNode?>();
            foreach (var (name, value) in Obj(o, "environmentVariables", "") ?? [])
            {
                variables[name] = value?.DeepClone();
            }

            var utcNow = Text(o, "utcNow", "", false);
            var guid = Text(o, "guid", "", false);
            var workflow = Obj(o, "workflow", "")?.DeepClone() as JsonObject;

            if (!o.TryGetPropertyValue("assertions", out var list2))
            {
                throw Bad("", "'assertions' is required.");
            }

            if (list2 is not JsonArray { Count: > 0 } assertions)
            {
                throw Bad("", "'assertions' must be a list with at least one assertion.");
            }

            return new FlowTestFile(flow, trigger, mocks, variables, utcNow, guid, workflow, assertions.Select((a, i) => Assertion(a, i)).ToList());
        }

        private FlowMock Mock(JsonObject m, string where)
        {
            Known(m, where, "status", "outputs", "error");
            var status = Text(m, "status", where, true)!;
            if (!MockStatuses.Contains(status))
            {
                throw Bad(where, "'status' must be 'Succeeded' or 'Failed'.");
            }

            var error = Obj(m, "error", where);
            if (error is not null)
            {
                Known(error, where + ".error", "code", "message");
                Text(error, "code", where + ".error", false);
                Text(error, "message", where + ".error", false);
                if (status == "Succeeded")
                {
                    throw Bad(where, "'error' is only for a mock with status 'Failed'.");
                }
            }

            return new FlowMock(status, Obj(m, "outputs", where)?.DeepClone(), error?.DeepClone());
        }

        private FlowAssertion Assertion(JsonNode? node, int index)
        {
            var where = $"assertions[{index}]";
            var a = node as JsonObject ?? throw Bad(where, "an assertion must be an object.");
            var kind = Text(a, "kind", where, true)!;
            if (!AssertionProperties.TryGetValue(kind, out var allowed))
            {
                throw Bad(where, $"unknown kind '{kind}'; use {string.Join(", ", AssertionProperties.Keys)}.");
            }

            foreach (var (key, _) in a)
            {
                if (!allowed.Contains(key))
                {
                    throw Bad(where, AssertionProperties.Values.Any(v => v.Contains(key)) ? $"kind '{kind}' takes no '{key}'." : $"unknown property '{key}'.");
                }
            }

            if (!a.TryGetPropertyValue("equals", out var expected))
            {
                throw Bad(where, "'equals' is required.");
            }

            if (kind is "runStatus" or "actionStatus")
            {
                var statuses = kind == "runStatus" ? RunStatuses : ActionStatuses;
                if (expected is not JsonValue v || !v.TryGetValue(out string? s) || !statuses.Contains(s))
                {
                    throw Bad(where, $"'equals' must be one of {string.Join(", ", statuses)}.");
                }
            }

            int? call = null;
            if (a.TryGetPropertyValue("call", out var callNode))
            {
                call = Index(callNode) ?? throw Bad(where, "'call' must be a non-negative integer.");
            }

            IReadOnlyList<object>? segments = null;
            if (a.TryGetPropertyValue("path", out var pathNode))
            {
                segments = (pathNode as JsonArray ?? throw Bad(where, "'path' must be a list.")).Select(s => Segment(s, where)).ToList();
            }

            return new FlowAssertion(
                kind,
                expected?.DeepClone(),
                kind is "actionStatus" or "inputs" or "outputs" ? Text(a, "action", where, true) : null,
                call,
                kind == "variable" ? Text(a, "name", where, true) : null,
                segments);
        }

        private object Segment(JsonNode? node, string where) =>
            node is JsonValue v && v.TryGetValue(out string? text)
                ? text
                : Index(node) ?? throw Bad(where, "a 'path' segment must be a string or a non-negative integer.");

        private static int? Index(JsonNode? node) =>
            node is JsonValue v && v.TryGetValue(out decimal d) && d >= 0 && d <= int.MaxValue && d == decimal.Truncate(d) ? (int)d : null;

        private void Known(JsonObject o, string where, params string[] allowed)
        {
            foreach (var (key, _) in o)
            {
                if (!allowed.Contains(key))
                {
                    throw Bad(where, $"unknown property '{key}'.");
                }
            }
        }

        private string? Text(JsonObject o, string key, string where, bool required)
        {
            if (!o.TryGetPropertyValue(key, out var node))
            {
                return required ? throw Bad(where, $"'{key}' is required.") : null;
            }

            return node is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : throw Bad(where, $"'{key}' must be a non-empty string.");
        }

        private JsonObject? Obj(JsonObject o, string key, string where) =>
            !o.TryGetPropertyValue(key, out var node) ? null : node as JsonObject ?? throw Bad(where, $"'{key}' must be an object.");
    }
}

/// <summary>Finds the test files under a folder.</summary>
public static class FlowTestFiles
{
    /// <summary><c>*.flowtest.json</c> files under <paramref name="folder"/>, recursively, skipping <c>bin</c>, <c>obj</c>, <c>.git</c> and <c>node_modules</c> and not following links, sorted ordinally by their forward-slash path below the folder.</summary>
    public static IReadOnlyList<string> Find(string folder)
    {
        var found = new List<string>();
        Walk(folder, found);
        found.Sort((a, b) => string.CompareOrdinal(Relative(folder, a), Relative(folder, b)));
        return found;
    }

    private static string Relative(string folder, string file) => Path.GetRelativePath(folder, file).Replace('\\', '/');

    private static void Walk(string dir, List<string> found)
    {
        found.AddRange(Directory.EnumerateFiles(dir, "*.flowtest.json", SolutionDiscovery.Options));
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SolutionDiscovery.Options).Where(d => !SolutionDiscovery.Skipped.Contains(Path.GetFileName(d))))
        {
            Walk(sub, found);
        }
    }
}

public enum FlowTestOutcome { Passed, Failed, Unmocked, Unsupported }

/// <summary>The result of one assertion. <paramref name="Index"/> counts from 0. <paramref name="HasActual"/> tells an actual value of <c>null</c> from none (the action did not run, the path was not found).</summary>
public sealed record FlowAssertionResult(int Index, string Description, bool Passed, JsonNode? Expected, bool HasActual, JsonNode? Actual, string? Message);

/// <summary>The result of one test file. <paramref name="Flow"/> is the flow's display name when it was found, else what the test named. <paramref name="Message"/> is set when the test did not reach its assertions (or has none to blame).</summary>
public sealed record FlowTestResult(string File, string Flow, string? Solution, FlowTestOutcome Outcome, string? RunStatus, string? Message, IReadOnlyList<FlowAssertionResult> Assertions);

/// <summary>Runs a test file against the cloud flows of already loaded solutions.</summary>
public static class FlowTestRunner
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="locations">Where each solution is, below the path the command was given (forward slashes), to tell apart solutions with one name; null when not known.</param>
    public static FlowTestResult Run(string file, FlowTestFile test, IReadOnlyList<Solution> solutions, IReadOnlyList<string>? locations = null)
    {
        FlowTestResult Stop(FlowTestOutcome outcome, string message, string? flow = null, string? solution = null) =>
            new(file, flow ?? test.Flow, solution, outcome, null, message, []);

        var matches = solutions
            .SelectMany((s, i) => s.Workflows.Where(w => w.Category == 5 && IsFlow(w, test.Flow)).Select(w => (Index: i, Flow: w)))
            .ToList();
        if (matches.Count != 1)
        {
            var named = matches.Count == 0 ? Enumerable.Range(0, solutions.Count) : matches.Select(m => m.Index);
            var list = solutions.Count == 0 ? "no solution" : string.Join(", ", named.Select(i => Named(solutions[i], locations?[i])));
            return Stop(
                FlowTestOutcome.Failed,
                matches.Count == 0
                    ? $"No cloud flow has the name or id '{test.Flow}' in {list}."
                    : $"The flow '{test.Flow}' is ambiguous: {matches.Count} flows match, in {list}. Point the path at one solution.");
        }

        var (solution, workflow) = (solutions[matches[0].Index], matches[0].Flow);
        if (Definition(workflow) is not { } definition)
        {
            return Stop(FlowTestOutcome.Failed, $"The flow '{workflow.Name}' has no readable definition (clientdata with properties.definition).", workflow.Name, solution.UniqueName);
        }

        var parameters = new Dictionary<string, JsonNode?>();
        var used = new HashSet<string>();
        foreach (var (name, node) in definition["parameters"] as JsonObject ?? new JsonObject())
        {
            var parameter = node as JsonObject;
            if (FlowDefinition.EnvironmentVariableSchema(name, Str((parameter?["metadata"] as JsonObject)?["schemaName"])) is not { } schema)
            {
                continue;
            }

            used.Add(schema);

            if (test.EnvironmentVariables.TryGetValue(schema, out var given))
            {
                parameters[name] = given?.DeepClone();
                continue;
            }

            var variable = solution.EnvironmentVariables.FirstOrDefault(v => v.SchemaName == schema);
            if ((variable?.CurrentValue ?? variable?.DefaultValue) is not { } text)
            {
                continue;
            }

            var type = Str(parameter?["type"]);
            if (!TryConvert(text, type, out var value))
            {
                // The value is not echoed: it can be a secret.
                return Stop(FlowTestOutcome.Unsupported, $"Environment variable '{schema}' (parameter '{name}'): its value in the solution cannot be converted to the parameter type '{type}'.", workflow.Name, solution.UniqueName);
            }

            parameters[name] = value;
        }

        // a typo would otherwise run against the solution's value
        if (test.EnvironmentVariables.Keys.FirstOrDefault(k => !used.Contains(k)) is { } unused)
        {
            return Stop(FlowTestOutcome.Failed, $"environment variable '{unused}' is not a parameter of this flow", workflow.Name, solution.UniqueName);
        }

        var run = FlowSimulator.Run(definition, new FlowRunInputs(test.Trigger, test.Mocks, parameters, test.UtcNow, test.Guid, test.Workflow));
        if (run.Outcome != FlowRunOutcome.Finished)
        {
            var outcome = run.Outcome switch
            {
                FlowRunOutcome.Unmocked => FlowTestOutcome.Unmocked,
                FlowRunOutcome.Unsupported => FlowTestOutcome.Unsupported,
                _ => FlowTestOutcome.Failed,
            };
            return Stop(outcome, run.Message ?? "The run did not finish.", workflow.Name, solution.UniqueName);
        }

        var actions = new HashSet<string>();
        ActionNames(definition, actions);
        var results = test.Assertions.Select((a, i) => Check(i, a, run, actions)).ToList();
        return new FlowTestResult(file, workflow.Name, solution.UniqueName, results.All(r => r.Passed) ? FlowTestOutcome.Passed : FlowTestOutcome.Failed, run.Status, null, results);
    }

    private static string Named(Solution s, string? location) =>
        location is { Length: > 0 } and not "." ? $"{location} ({s.UniqueName} {s.Version})" : $"{s.UniqueName} {s.Version}";

    private static bool IsFlow(Workflow w, string reference) =>
        w.Name == reference || (System.Guid.TryParse(reference, out var id) && System.Guid.TryParse(w.Id, out var own) && id == own);

    private static JsonObject? Definition(Workflow w)
    {
        if (w.ClientData is null)
        {
            return null;
        }

        try
        {
            return ((ExpressionFunctions.ParseJson(w.ClientData.TrimStart('﻿')) as JsonObject)?["properties"] as JsonObject)?["definition"] as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // Environment variable text from the solution, by the parameter's type; Dataverse and the flow both write it in the invariant form.
    private static bool TryConvert(string text, string? type, out JsonNode? value)
    {
        value = null;
        switch (type?.ToLowerInvariant())
        {
            case "string" or "securestring":
                value = JsonValue.Create(text);
                return true;
            case "bool":
                if (!bool.TryParse(text, out var flag))
                {
                    return false;
                }

                value = JsonValue.Create(flag);
                return true;
            case "int" or "float" or "object" or "array":
                try
                {
                    value = ExpressionFunctions.ParseJson(text);
                }
                catch (JsonException)
                {
                    return false;
                }

                return type!.ToLowerInvariant() switch
                {
                    "int" => value is JsonValue n && n.TryGetValue(out long _),
                    "float" => value is JsonValue f && f.GetValueKind() == JsonValueKind.Number,
                    "object" => value is JsonObject,
                    _ => value is JsonArray,
                };
            default:
                return false;
        }
    }

    private static void ActionNames(JsonNode? node, HashSet<string> names)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key == "actions" && value is JsonObject block)
                    {
                        names.UnionWith(block.Select(p => p.Key));
                    }

                    ActionNames(value, names);
                }

                break;
            case JsonArray a:
                foreach (var item in a)
                {
                    ActionNames(item, names);
                }

                break;
        }
    }

    private static string Describe(FlowAssertion a)
    {
        var at = a.Path is { Count: > 0 } path ? " at " + JsonSerializer.Serialize(path, Compact) : "";
        var call = a.Call is { } c ? $" call {c}" : "";
        return a.Kind switch
        {
            "runStatus" => "run status",
            "actionStatus" => $"status of {a.Action}{call}",
            "variable" => $"variable {a.Name}{at}",
            "response" => "response" + at,
            _ => $"{a.Kind} of {a.Action}{call}{at}",
        };
    }

    private static FlowAssertionResult Check(int index, FlowAssertion a, FlowRun run, HashSet<string> actions)
    {
        var description = Describe(a);
        FlowAssertionResult Fail(string message) => new(index, description, false, a.Expected?.DeepClone(), false, null, message);

        // A skipped call has no inputs or outputs.
        (string? Label, JsonNode? Value, bool Ran) Candidate(string? label, ActionCall call) => (label, Pick(call), a.Kind == "actionStatus" || call.Status != "Skipped");

        JsonNode? Pick(ActionCall call) => a.Kind switch
        {
            "actionStatus" => JsonValue.Create(call.Status),
            "inputs" => call.Inputs,
            _ => call.Outputs,
        };

        List<(string? Label, JsonNode? Value, bool Ran)> candidates;
        switch (a.Kind)
        {
            case "runStatus":
                candidates = [(null, JsonValue.Create(run.Status), true)];
                break;
            case "variable":
                if (!run.Variables.TryGetValue(a.Name!, out var variable))
                {
                    return Fail($"no variable named '{a.Name}'");
                }

                candidates = [(null, variable, true)];
                break;
            case "response":
                if (run.Response is null)
                {
                    return Fail("no Response action ran");
                }

                candidates = [(null, run.Response, true)];
                break;
            default:
                if (!run.Calls.TryGetValue(a.Action!, out var calls) || calls.Count == 0)
                {
                    return Fail(actions.Contains(a.Action!) ? "did not run" : $"action '{a.Action}' is not in the flow");
                }

                if (a.Call is { } c)
                {
                    if (c >= calls.Count)
                    {
                        return Fail($"call {c} of {calls.Count}: the action made {calls.Count} call{(calls.Count == 1 ? "" : "s")}");
                    }

                    candidates = [Candidate(null, calls[c])];
                }
                else
                {
                    candidates = calls.Select((call, i) => Candidate(calls.Count > 1 ? $"call {i} of {calls.Count}" : null, call)).ToList();
                }

                break;
        }

        foreach (var (label, value, ran) in candidates)
        {
            if (!ran)
            {
                return Fail(label is null ? "did not run" : $"{label}: did not run");
            }

            var target = value;
            if (a.Path is { Count: > 0 } path && !TryWalk(value, path, out target))
            {
                return Fail(label is null ? "path not found" : $"{label}: path not found");
            }

            if (!Same(a.Expected, target))
            {
                return new FlowAssertionResult(index, description, false, a.Expected?.DeepClone(), true, target?.DeepClone(), label);
            }
        }

        return new FlowAssertionResult(index, description, true, a.Expected?.DeepClone(), false, null, null);
    }

    private static bool TryWalk(JsonNode? node, IReadOnlyList<object> path, out JsonNode? found)
    {
        found = node;
        foreach (var segment in path)
        {
            switch (segment)
            {
                case string key when found is JsonObject o && o.TryGetPropertyValue(key, out var next):
                    found = next;
                    break;
                case int i when found is JsonArray list && i < list.Count:
                    found = list[i];
                    break;
                default:
                    found = null;
                    return false;
            }
        }

        return true;
    }

    private static bool Same(JsonNode? a, JsonNode? b) => a is null || b is null ? a is null && b is null : JsonNode.DeepEquals(a, b);
}