using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerOps.Core.Flows;

/// <summary>A mocked action result in the static-result shape: status Succeeded or Failed, optional outputs and error.</summary>
public sealed record FlowMock(string Status, JsonNode? Outputs, JsonNode? Error);

/// <summary>What a run gets from the test. A null value means the test gave none: reading it makes the run unmocked.</summary>
public sealed record FlowRunInputs(
    JsonNode? TriggerOutputs,
    IReadOnlyDictionary<string, IReadOnlyList<FlowMock>> Mocks,
    IReadOnlyDictionary<string, JsonNode?> Parameters,
    string? UtcNow,
    string? Guid,
    JsonNode? Workflow);

public enum FlowRunOutcome { Finished, Unmocked, Unsupported, Invalid }

/// <summary>One call of an action: its status, its evaluated inputs, outputs and error (null when absent).</summary>
public sealed record ActionCall(string Status, JsonNode? Inputs, JsonNode? Outputs, JsonNode? Error);

/// <summary>The result of a run. Status is the run's status when Outcome is Finished; Message says why otherwise.</summary>
public sealed record FlowRun(
    FlowRunOutcome Outcome,
    string? Message,
    string? Status,
    IReadOnlyDictionary<string, IReadOnlyList<ActionCall>> Calls,
    IReadOnlyDictionary<string, JsonNode?> Variables,
    JsonNode? Response);

/// <summary>Runs a cloud flow definition offline, from Microsoft Learn's Workflow Definition Language pages: connector actions never run, the test's mocks answer for them.</summary>
public static class FlowSimulator
{
    /// <summary>Never throws for a flow or a test the simulator can't run: Outcome says why. Neither the definition nor the inputs are changed.</summary>
    public static FlowRun Run(JsonObject definition, FlowRunInputs inputs) => new Simulation(definition, inputs).Execute();

    private sealed class RunStop(FlowRunOutcome outcome, string message) : Exception(message)
    {
        public FlowRunOutcome Outcome { get; } = outcome;
    }

    private sealed record ActionInfo(string Name, string Type, JsonObject Definition, IReadOnlyList<string> Loops);

    /// <summary>One level of the running loops: a Foreach (with its item), an Until, or a Query's current item (Loop is null).</summary>
    private sealed record Frame(string? Loop, JsonNode? Item, bool HasItem);

    private sealed class Var(string type)
    {
        public string Type { get; } = type;

        public JsonNode? Value { get; set; }
    }

    private sealed class Simulation(JsonObject definition, FlowRunInputs inputs) : IExpressionContext
    {
        private const int MaxCalls = 100_000;
        private const int MaxDepth = 64;
        private static readonly string[] Statuses = ["Succeeded", "Failed", "Skipped", "TimedOut"];
        private static readonly string[] ConnectorTypes = ["OpenApiConnection", "OpenApiConnectionWebhook", "Http", "Workflow"];
        private static readonly string[] VariableTypes = ["string", "integer", "float", "boolean", "array", "object"];

        // Only these properties of the inputs are evaluated; the rest (a Response's schema) is not.
        private static readonly Dictionary<string, string[]> EvaluatedInputs = new()
        {
            ["Response"] = ["statusCode", "headers", "body"],
            ["ParseJson"] = ["content"],
        };

        private readonly Dictionary<string, ActionInfo> _actions = [];
        private readonly Dictionary<string, List<ActionCall>> _calls = [];
        private readonly List<string> _order = [];
        private readonly Dictionary<string, int> _mockCalls = [];
        private readonly Dictionary<string, Var> _vars = [];
        private readonly List<Frame> _frames = [];
        private int _count;
        private long _bytes;
        private bool _ended;
        private string? _runStatus;
        private JsonNode? _response;

        public FlowRun Execute()
        {
            try
            {
                var actions = Field<JsonObject>(definition, "actions", "The flow");
                Index(actions, []);
                CheckMocks();
                if (TriggerSkipsRun())
                {
                    return Finish("Skipped");
                }

                var status = RunBlock(actions);
                return Finish(_runStatus ?? status);
            }
            catch (RunStop stop)
            {
                return Finish(null, stop.Outcome, stop.Message);
            }
        }

        // IExpressionContext

        public JsonNode? Outputs(string action)
        {
            var info = Find(action);
            CheckLoops(info, $"outputs('{action}')");
            return _calls.TryGetValue(action, out var calls) && calls[^1].Status != "Skipped"
                ? calls[^1].Outputs
                : throw new ExpressionException($"Action '{action}' has not run or was skipped, so it has no outputs.");
        }

        public JsonNode? Result(string scope)
        {
            var info = Find(scope);
            if (info.Type is "Foreach" or "Until")
            {
                throw new UnsupportedException($"result('{scope}') of a {info.Type} is not supported.");
            }

            if (info.Type != "Scope")
            {
                throw new ExpressionException($"result() needs a Scope, but '{scope}' is a {info.Type}.");
            }

            CheckLoops(info, $"result('{scope}')");
            var children = (info.Definition["actions"] as JsonObject)?.Select(p => p.Key).ToHashSet() ?? [];
            var result = new JsonArray();
            foreach (var name in _order.AsEnumerable().Reverse().Where(children.Contains).Distinct().Reverse())
            {
                var call = _calls[name][^1];
                var entry = new JsonObject { ["name"] = name, ["inputs"] = call.Inputs?.DeepClone(), ["outputs"] = call.Outputs?.DeepClone(), ["status"] = call.Status };
                if (call.Error is not null)
                {
                    entry["error"] = call.Error.DeepClone();
                }

                result.Add(entry);
            }

            return result;
        }

        public JsonNode? Items(string loop) =>
            (_frames.LastOrDefault(f => f.Loop == loop && f.HasItem) ?? throw new ExpressionException($"'{loop}' is not a Foreach that contains this action.")).Item;

        public JsonNode? Item() =>
            (_frames.LastOrDefault(f => f.HasItem) ?? throw new ExpressionException("item() is used outside a Foreach or a Query.")).Item;

        public JsonNode? TriggerOutputs() => inputs.TriggerOutputs?.DeepClone() ?? throw new UnmockedException("triggerOutputs() needs a value: set 'trigger' in the test.");

        public string UtcNow() => inputs.UtcNow ?? throw new UnmockedException("utcNow() needs a value: set 'utcNow' in the test.");

        public string Guid() => inputs.Guid ?? throw new UnmockedException("guid() needs a value: set 'guid' in the test.");

        public JsonNode? Workflow() => inputs.Workflow?.DeepClone() ?? throw new UnmockedException("workflow() needs a value: set 'workflow' in the test.");

        public JsonNode? Variable(string name) =>
            _vars.TryGetValue(name, out var v) ? v.Value : throw new ExpressionException($"Variable '{name}' is not initialized.");

        public JsonNode? Parameter(string name)
        {
            var parameter = (definition["parameters"] as JsonObject)?[name] as JsonObject ?? throw new ExpressionException($"The flow has no parameter '{name}'.");
            if (inputs.Parameters.TryGetValue(name, out var given))
            {
                return given?.DeepClone();
            }

            return parameter.TryGetPropertyValue("defaultValue", out var fallback)
                ? fallback?.DeepClone()
                : throw new UnmockedException($"parameter '{name}' has no value: set it in the test or give the parameter a defaultValue.");
        }

        // Setup

        private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        private static bool? AsBool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

        private static JsonObject Error(string message) => new() { ["code"] = "InvalidTemplate", ["message"] = message };

        /// <summary>Every block of actions inside an action: <c>actions</c>, <c>else.actions</c>, <c>cases.*.actions</c> and <c>default.actions</c>.</summary>
        private static IEnumerable<JsonObject> Blocks(JsonObject action, string owner = "An action")
        {
            var holders = new[] { action, action["else"], action["default"] }.Concat(action["cases"] is JsonObject cases ? cases.Select(c => c.Value) : []);
            return holders.OfType<JsonObject>().Select(h => Field<JsonObject>(h, "actions", owner)).OfType<JsonObject>();
        }

        /// <summary>A property that may be absent but, when there, must be a <typeparamref name="T"/> (an object or an array): a malformed definition must not pass by running nothing.</summary>
        private static T? Field<T>(JsonObject holder, string key, string owner)
            where T : JsonNode =>
            holder.TryGetPropertyValue(key, out var node)
                ? node as T ?? throw new RunStop(FlowRunOutcome.Unsupported, $"{owner} has '{key}' that is not {(typeof(T) == typeof(JsonObject) ? "an object" : "an array")}.")
                : null;

        private static bool StaticResultOn(JsonObject action) =>
            action["runtimeConfiguration"] is JsonObject rc && rc["staticResult"] is JsonObject sr && Str(sr["staticResultOptions"]) == "Enabled";

        private static bool TakesMock(ActionInfo action) => ConnectorTypes.Contains(action.Type) || StaticResultOn(action.Definition);

        private void Index(JsonObject? block, IReadOnlyList<string> loops)
        {
            foreach (var (name, node) in block ?? new JsonObject())
            {
                if (node is not JsonObject action)
                {
                    throw new RunStop(FlowRunOutcome.Unsupported, $"Action '{name}' is not an object.");
                }

                var type = Str(action["type"]) ?? "";
                if (!_actions.TryAdd(name, new ActionInfo(name, type, action, loops)))
                {
                    throw new RunStop(FlowRunOutcome.Unsupported, $"The flow has two actions named '{name}'.");
                }

                foreach (var inner in Blocks(action, $"Action '{name}'"))
                {
                    Index(inner, type is "Foreach" or "Until" ? [.. loops, name] : loops);
                }
            }
        }

        private void CheckMocks()
        {
            foreach (var (name, list) in inputs.Mocks)
            {
                if (!_actions.TryGetValue(name, out var action))
                {
                    throw new RunStop(FlowRunOutcome.Invalid, $"Mock '{name}' names no action in the flow.");
                }

                if (!TakesMock(action))
                {
                    throw new RunStop(FlowRunOutcome.Invalid, $"Mock '{name}' is on a {action.Type} action; only OpenApiConnection, OpenApiConnectionWebhook, Http and Workflow actions, and actions with a static result enabled, take a mock.");
                }

                if (list.FirstOrDefault(m => m.Status is not ("Succeeded" or "Failed")) is { } bad)
                {
                    throw new RunStop(FlowRunOutcome.Invalid, $"Mock '{name}' has status '{bad.Status}'; use Succeeded or Failed.");
                }
            }
        }

        private bool TriggerSkipsRun()
        {
            if (definition["triggers"] is not JsonObject { Count: > 0 } triggers)
            {
                return false;
            }

            if (triggers.Count > 1)
            {
                throw new RunStop(FlowRunOutcome.Unsupported, "The flow has more than one trigger.");
            }

            if (triggers.Single().Value is not JsonObject trigger)
            {
                return false;
            }

            if (trigger.ContainsKey("splitOn"))
            {
                throw new RunStop(FlowRunOutcome.Unsupported, "The trigger has splitOn, which is not supported.");
            }

            foreach (var condition in Field<JsonArray>(trigger, "conditions", "The trigger") ?? [])
            {
                try
                {
                    var holds = AsBool(Expression.EvaluateCondition((condition as JsonObject)?["expression"], this))
                        ?? throw new ExpressionException("it does not give a boolean");
                    if (!holds)
                    {
                        return true;
                    }
                }
                catch (ExpressionException e)
                {
                    throw new RunStop(FlowRunOutcome.Unsupported, $"A trigger condition could not be evaluated: {e.Message}");
                }
                catch (UnmockedException e)
                {
                    throw new RunStop(FlowRunOutcome.Unmocked, $"A trigger condition: {e.Message}");
                }
                catch (UnsupportedException e)
                {
                    throw new RunStop(FlowRunOutcome.Unsupported, $"A trigger condition: {e.Message}");
                }
            }

            return false;
        }

        private FlowRun Finish(string? status, FlowRunOutcome outcome = FlowRunOutcome.Finished, string? message = null) => new(
            outcome,
            message,
            status,
            _calls.ToDictionary(p => p.Key, p => (IReadOnlyList<ActionCall>)p.Value),
            _vars.ToDictionary(p => p.Key, p => p.Value.Value?.DeepClone()),
            _response?.DeepClone());

        // Running

        // Every call, loop iteration and item counts, so a loop with an empty body still ends.
        private void Tick()
        {
            if (++_count > MaxCalls)
            {
                throw new RunStop(FlowRunOutcome.Unsupported, $"The run made more than {MaxCalls} action calls; the simulator stops there.");
            }
        }

        private static bool TooDeep(JsonNode? node, int level = 1) => node switch
        {
            JsonObject o => level > MaxDepth || o.Any(p => TooDeep(p.Value, level + 1)),
            JsonArray a => level > MaxDepth || a.Any(i => TooDeep(i, level + 1)),
            _ => false,
        };

        private static void CheckDepth(JsonNode? value)
        {
            if (TooDeep(value))
            {
                throw new UnsupportedException($"a value nested deeper than {MaxDepth} levels is not supported.");
            }
        }

        /// <summary>Adds the size of a recorded value to the run's total: 2 bytes a character plus a constant a node, counted node by node and stopped as soon as the budget is passed.</summary>
        private void Charge(JsonNode? value)
        {
            var pending = new Stack<JsonNode?>();
            pending.Push(value);
            while (pending.TryPop(out var node))
            {
                _bytes += ExpressionFunctions.NodeBytes;
                switch (node)
                {
                    case JsonObject o:
                        foreach (var (key, item) in o)
                        {
                            _bytes += 2L * key.Length;
                            pending.Push(item);
                        }

                        break;
                    case JsonArray a:
                        foreach (var item in a)
                        {
                            pending.Push(item);
                        }

                        break;
                    case JsonValue v when v.TryGetValue(out string? text):
                        _bytes += 2L * text.Length;
                        break;
                }

                if (_bytes > ExpressionFunctions.MaxRunBytes)
                {
                    throw new UnsupportedException("the run's recorded values pass 256 MB.");
                }
            }
        }

        private void Record(string name, ActionCall call)
        {
            Tick();
            try
            {
                CheckDepth(call.Inputs);
                CheckDepth(call.Outputs);
                CheckDepth(call.Error);
                Charge(call.Inputs);
                Charge(call.Outputs);
                Charge(call.Error);
            }
            catch (UnsupportedException e)
            {
                throw new RunStop(FlowRunOutcome.Unsupported, $"Action '{name}': {e.Message}");
            }

            if (!_calls.TryGetValue(name, out var list))
            {
                _calls[name] = list = [];
            }

            list.Add(call);
            _order.Add(name);
        }

        private void SkipAction(string name, JsonObject action)
        {
            Record(name, new ActionCall("Skipped", null, null, null));
            SkipChildren(action);
        }

        private void SkipBlock(JsonObject block)
        {
            foreach (var (name, action) in block)
            {
                SkipAction(name, (JsonObject)action!);
            }
        }

        private void SkipChildren(JsonObject action)
        {
            foreach (var block in Blocks(action))
            {
                SkipBlock(block);
            }
        }

        /// <summary>Runs a block of actions and returns its status.</summary>
        private string RunBlock(JsonObject? block)
        {
            block ??= [];
            var after = block.ToDictionary(p => p.Key, p => ReadRunAfter(block, p.Key, (JsonObject)p.Value!));
            var done = new Dictionary<string, string>();
            while (!_ended)
            {
                var next = block.Select(p => p.Key).FirstOrDefault(n => !done.ContainsKey(n) && after[n].Keys.All(done.ContainsKey));
                if (next is null)
                {
                    break;
                }

                if (after[next].All(p => p.Value.Contains(done[p.Key])))
                {
                    done[next] = RunAction(_actions[next]);
                }
                else
                {
                    SkipAction(next, (JsonObject)block[next]!);
                    done[next] = "Skipped";
                }
            }

            var pending = block.Where(p => !done.ContainsKey(p.Key)).ToList();
            if (!_ended && pending.Count > 0)
            {
                throw new RunStop(FlowRunOutcome.Unsupported, $"The runAfter of {string.Join(", ", pending.Select(p => $"'{p.Key}'"))} forms a cycle.");
            }

            foreach (var (name, action) in pending)
            {
                SkipAction(name, (JsonObject)action!);
            }

            // Failed when an action failed that no action in the block that ran (not Skipped) handles with Failed in its runAfter.
            bool Handled(string failed) => after.Any(h => h.Value.TryGetValue(failed, out var on) && on.Contains("Failed") && done.TryGetValue(h.Key, out var status) && status != "Skipped");
            return done.Where(p => p.Value == "Failed").Any(p => !Handled(p.Key)) ? "Failed" : "Succeeded";
        }

        private static Dictionary<string, string[]> ReadRunAfter(JsonObject block, string name, JsonObject action)
        {
            var result = new Dictionary<string, string[]>();
            foreach (var (predecessor, statuses) in Field<JsonObject>(action, "runAfter", $"Action '{name}'") ?? [])
            {
                if (!block.ContainsKey(predecessor))
                {
                    throw new RunStop(FlowRunOutcome.Unsupported, $"Action '{name}' runs after '{predecessor}', which is not in the same block.");
                }

                var list = (statuses as JsonArray ?? throw new RunStop(FlowRunOutcome.Unsupported, $"Action '{name}' has 'runAfter' for '{predecessor}' that is not an array of statuses.")).Select(Str).ToArray();
                foreach (var status in list.Where(s => s is null || !Statuses.Contains(s)))
                {
                    throw new RunStop(FlowRunOutcome.Unsupported, $"Action '{name}' has the runAfter status '{status}' for '{predecessor}'; use Succeeded, Failed, Skipped or TimedOut.");
                }

                result[predecessor] = list!;
            }

            return result;
        }

        private string RunAction(ActionInfo action)
        {
            ActionCall call;
            try
            {
                call = Perform(action);
            }
            catch (ExpressionException e)
            {
                call = new ActionCall("Failed", null, null, Error(e.Message));
            }
            catch (UnmockedException e)
            {
                throw new RunStop(FlowRunOutcome.Unmocked, $"Action '{action.Name}': {e.Message}");
            }
            catch (UnsupportedException e)
            {
                throw new RunStop(FlowRunOutcome.Unsupported, $"Action '{action.Name}': {e.Message}");
            }

            Record(action.Name, call);
            return call.Status;
        }

        private ActionCall Perform(ActionInfo action)
        {
            var def = action.Definition;
            if (def.ContainsKey("operationOptions"))
            {
                throw new UnsupportedException("operationOptions is not supported.");
            }

            if (TakesMock(action))
            {
                return Mocked(action);
            }

            switch (action.Type)
            {
                case "Compose":
                    var value = EvaluateInputs(action);
                    return new("Succeeded", value, value?.DeepClone(), null);
                case "InitializeVariable":
                    return InitializeVariable(action);
                case "SetVariable":
                case "AppendToStringVariable":
                case "AppendToArrayVariable":
                    return ChangeVariable(action);
                case "If":
                    var holds = AsBool(Expression.EvaluateCondition(def["expression"], this)) ?? throw new ExpressionException("The condition of If must give a boolean.");
                    return Branch(def, holds ? def["actions"] : (def["else"] as JsonObject)?["actions"]);
                case "Switch":
                    var subject = Expression.EvaluateValue(def["expression"], this);
                    var chosen = (def["cases"] as JsonObject)?.Select(c => c.Value).OfType<JsonObject>().FirstOrDefault(c => JsonNode.DeepEquals(c["case"], subject)) ?? def["default"] as JsonObject;
                    return Branch(def, chosen?["actions"]);
                case "Scope":
                    return Status(RunBlock(def["actions"] as JsonObject));
                case "Foreach":
                    return Foreach(action);
                case "Until":
                    return Until(action);
                case "Terminate":
                    var terminate = EvaluateInputs(action);
                    _runStatus = Str((terminate as JsonObject)?["runStatus"]);
                    if (_runStatus is not ("Succeeded" or "Failed" or "Cancelled"))
                    {
                        throw new UnsupportedException($"Terminate with runStatus '{_runStatus}' is not supported.");
                    }

                    _ended = true;
                    return new("Succeeded", terminate, null, null);
                case "Response":
                    var response = EvaluateInputs(action);
                    _response ??= response?.DeepClone();
                    return new("Succeeded", response, response?.DeepClone(), null);
                case "ParseJson":
                    return ParseJson(action);
                case "Query":
                    return Query(action);
                case "Wait":
                    return new("Succeeded", EvaluateInputs(action), null, null);
                default:
                    throw new UnsupportedException($"the action type '{action.Type}' is not supported.");
            }
        }

        private static ActionCall Status(string status) => new(status, null, null, null);

        private ActionCall Branch(JsonObject action, JsonNode? taken)
        {
            foreach (var block in Blocks(action).Where(b => !ReferenceEquals(b, taken)))
            {
                SkipBlock(block);
            }

            return Status(taken is JsonObject block2 ? RunBlock(block2) : "Succeeded");
        }

        private JsonNode? EvaluateInputs(ActionInfo action)
        {
            var node = action.Definition["inputs"];
            if (node is JsonObject obj && EvaluatedInputs.TryGetValue(action.Type, out var keys))
            {
                node = new JsonObject(obj.Where(p => keys.Contains(p.Key)).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
            }

            return Expression.EvaluateValue(node, this);
        }

        private ActionCall Mocked(ActionInfo action)
        {
            var evaluated = EvaluateInputs(action);
            var calls = _mockCalls.GetValueOrDefault(action.Name);
            if (!inputs.Mocks.TryGetValue(action.Name, out var list))
            {
                throw new UnmockedException(StaticResultOn(action.Definition)
                    ? "its static result is on, so the test needs a mock for it."
                    : "it never runs in a test, so the test needs a mock for it.");
            }

            if (calls >= list.Count)
            {
                throw new UnmockedException($"call {calls + 1} has no mock; the test gives {list.Count}.");
            }

            _mockCalls[action.Name] = calls + 1;
            SkipChildren(action.Definition);
            var mock = list[calls];
            return new(mock.Status, evaluated, mock.Outputs?.DeepClone(), mock.Error?.DeepClone());
        }

        private static bool Matches(string type, JsonNode? value) => value is null || type switch
        {
            "string" => value is JsonValue s && s.GetValueKind() == JsonValueKind.String,
            "integer" => ExpressionFunctions.TryLong(value, out _),
            "float" => value is JsonValue n && n.GetValueKind() == JsonValueKind.Number,
            "boolean" => value is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "array" => value is JsonArray,
            _ => value is JsonObject,
        };

        private static void CheckType(string name, string type, JsonNode? value)
        {
            if (!Matches(type, value))
            {
                throw new UnsupportedException($"the value of variable '{name}' is not a {type}, which the variable is declared as.");
            }
        }

        private ActionCall InitializeVariable(ActionInfo action)
        {
            var evaluated = EvaluateInputs(action);
            if (evaluated is not JsonObject { } io || io["variables"] is not JsonArray { Count: > 0 } list || list[0] is not JsonObject variable)
            {
                throw new UnsupportedException("it has no variable to initialize.");
            }

            var name = Str(variable["name"]) ?? throw new UnsupportedException("its variable has no name.");
            var type = Str(variable["type"])?.ToLowerInvariant();
            if (type is null || !VariableTypes.Contains(type))
            {
                throw new UnsupportedException($"variable type '{Str(variable["type"])}' is not supported.");
            }

            CheckType(name, type, variable["value"]);
            CheckDepth(variable["value"]);
            Charge(variable["value"]);
            _vars[name] = new Var(type) { Value = variable["value"]?.DeepClone() };
            return new("Succeeded", evaluated, null, null);
        }

        private ActionCall ChangeVariable(ActionInfo action)
        {
            var evaluated = EvaluateInputs(action);
            var name = Str((evaluated as JsonObject)?["name"]) ?? throw new UnsupportedException("it has no variable name.");
            var value = (evaluated as JsonObject)?["value"];
            var variable = _vars.GetValueOrDefault(name) ?? throw new ExpressionException($"Variable '{name}' is not initialized.");
            switch (action.Type)
            {
                case "SetVariable":
                    CheckType(name, variable.Type, value);
                    CheckDepth(value);
                    Charge(value);
                    variable.Value = value?.DeepClone();
                    break;
                case "AppendToStringVariable":
                    if (variable.Type != "string" || Str(value) is not { } text)
                    {
                        throw new UnsupportedException($"appending to '{name}' needs a string variable and a string value.");
                    }

                    ExpressionFunctions.CheckLength((long)(Str(variable.Value)?.Length ?? 0) + text.Length);
                    Charge(value);
                    variable.Value = Str(variable.Value) + text;
                    break;
                default:
                    if (variable.Type != "array")
                    {
                        throw new UnsupportedException($"appending to '{name}' needs an array variable.");
                    }

                    CheckDepth(value);
                    Charge(value);
                    var array = variable.Value as JsonArray ?? new JsonArray();
                    array.Add(value?.DeepClone());
                    variable.Value = array;
                    break;
            }

            return new("Succeeded", evaluated, null, null);
        }

        private ActionCall Foreach(ActionInfo action)
        {
            var items = Expression.EvaluateValue(action.Definition["foreach"], this) as JsonArray ?? throw new ExpressionException("The foreach input must be an array.");
            var failed = false;
            foreach (var item in items)
            {
                if (_ended)
                {
                    break;
                }

                Tick();
                _frames.Add(new Frame(action.Name, item?.DeepClone(), true));
                try
                {
                    failed |= RunBlock(action.Definition["actions"] as JsonObject) == "Failed";
                }
                finally
                {
                    _frames.RemoveAt(_frames.Count - 1);
                }
            }

            return Status(failed ? "Failed" : "Succeeded");
        }

        private ActionCall Until(ActionInfo action)
        {
            var limit = Field<JsonObject>(action.Definition, "limit", $"Action '{action.Name}'") is { } l && l.ContainsKey("count")
                ? ExpressionFunctions.TryLong(l["count"], out var count) ? count : throw new UnsupportedException("its 'limit.count' is not an integer.")
                : 60;
            var status = "Succeeded";
            for (long i = 0; i < limit && !_ended; i++)
            {
                Tick();
                _frames.Add(new Frame(action.Name, null, false));
                try
                {
                    status = RunBlock(action.Definition["actions"] as JsonObject);

                    // Learn: a failed iteration does not stop the loop; the Until is Failed only if its last iteration failed.
                    if (_ended || (AsBool(Expression.EvaluateCondition(action.Definition["expression"], this)) ?? throw new ExpressionException("The condition of Until must give a boolean.")))
                    {
                        break;
                    }
                }
                finally
                {
                    _frames.RemoveAt(_frames.Count - 1);
                }
            }

            return Status(status);
        }

        private ActionCall ParseJson(ActionInfo action)
        {
            var evaluated = EvaluateInputs(action);
            var content = (evaluated as JsonObject)?["content"];
            JsonNode? body;
            if (content is JsonValue v && v.TryGetValue(out string? text))
            {
                try
                {
                    body = ExpressionFunctions.ParseJson(text);
                }
                catch (JsonException e)
                {
                    return new ActionCall("Failed", evaluated, null, Error($"The content is not valid JSON: {e.Message}"));
                }
            }
            else
            {
                body = content?.DeepClone();
            }

            return new("Succeeded", evaluated, new JsonObject { ["body"] = body }, null);
        }

        private ActionCall Query(ActionInfo action)
        {
            var parameters = action.Definition["inputs"] as JsonObject;
            var source = Expression.EvaluateValue(parameters?["from"], this) as JsonArray ?? throw new ExpressionException("The from input of Query must be an array.");
            var where = parameters?["where"];
            var kept = new JsonArray();
            foreach (var item in source)
            {
                _frames.Add(new Frame(null, item?.DeepClone(), true));
                try
                {
                    if (AsBool(Expression.EvaluateCondition(where, this)) ?? throw new ExpressionException("The where input of Query must give a boolean."))
                    {
                        kept.Add(item?.DeepClone());
                    }
                }
                finally
                {
                    _frames.RemoveAt(_frames.Count - 1);
                }
            }

            return new("Succeeded", new JsonObject { ["from"] = source, ["where"] = where?.DeepClone() }, new JsonObject { ["body"] = kept }, null);
        }

        private ActionInfo Find(string name) =>
            _actions.TryGetValue(name, out var info) ? info : throw new ExpressionException($"The flow has no action '{name}'.");

        private void CheckLoops(ActionInfo target, string call)
        {
            if (target.Loops.Any(loop => !_frames.Any(f => f.Loop == loop)))
            {
                throw new UnsupportedException($"{call} reads the action '{target.Name}', which is inside a loop that the reading action is not in; the simulator does not support that.");
            }
        }
    }
}