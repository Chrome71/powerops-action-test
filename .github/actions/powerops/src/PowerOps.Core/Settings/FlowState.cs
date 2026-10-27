using System.Text.Json;

using PowerOps.Core.Model;

namespace PowerOps.Core.Settings;

/// <summary>One cloud flow in a <c>settings apply</c> run: its state in the zip, whether it exists in the target and in which state, the state the import should leave it in, and its state after the import (<c>null</c> until then).</summary>
public sealed record ApplyFlow(string Name, string ExportState, bool Exists, string? StateBefore, string ExpectedState, string? StateAfter);

/// <summary>Cloud flow states, read from <c>pac power-automate list-cloud-flows --workflow-id &lt;id&gt; --json</c>, and what an import does to them.</summary>
public static class FlowState
{
    public const string On = "on";
    public const string Off = "off";
    public const string Suspended = "suspended";
    public const string Missing = "missing";

    /// <summary>The solution's cloud flows (category 5), sorted by name.</summary>
    public static IReadOnlyList<Workflow> CloudFlows(Solution solution) =>
        solution.Workflows.Where(w => w.Category == 5).OrderBy(w => w.Name, StringComparer.Ordinal).ThenBy(w => w.Id, StringComparer.Ordinal).ToList();

    /// <summary>The state of flow <paramref name="workflowId"/> in one list-cloud-flows result. pac prints <c>null</c> for an id that does not exist, else <c>{"value":[...]}</c> whose entries have <c>workflowId</c> and a <c>stateCode</c> of <c>Draft</c>, <c>Published</c> or <c>Suspended</c> (samples in tests/fixtures/pac).</summary>
    /// <exception cref="InvalidDataException">The output has another shape or an unknown state; the message never quotes the output.</exception>
    public static string Read(string pacJson, string workflowId)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(pacJson);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"pac printed no JSON (line {e.LineNumber}, position {e.BytePositionInLine})", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return Missing;
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("pac printed JSON without a value array");
            }

            var id = Guid.Parse(workflowId);
            foreach (var flow in value.EnumerateArray())
            {
                if (flow.ValueKind == JsonValueKind.Object && flow.TryGetProperty("workflowId", out var w) && w.ValueKind == JsonValueKind.String
                    && Guid.TryParse(w.GetString(), out var found) && found == id)
                {
                    var state = flow.TryGetProperty("stateCode", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    return state switch
                    {
                        "Published" => On,
                        "Draft" => Off,
                        "Suspended" => Suspended,
                        _ => throw new InvalidDataException("pac printed a flow without a known stateCode"),
                    };
                }
            }

            return Missing;
        }
    }

    /// <summary>The flow's state in the exported zip (statecode 0 off, 1 on, 2 suspended).</summary>
    public static string Export(int? stateCode) => stateCode switch { 1 => On, 2 => Suspended, _ => Off };

    /// <summary>The plan for one flow given its state in the target (<see cref="Missing"/> when it is not there): an existing flow keeps its state; a new one comes back on only if it was on at export.</summary>
    public static ApplyFlow Plan(Workflow flow, string before) =>
        before == Missing
            ? new(flow.Name, Export(flow.StateCode), false, null, flow.StateCode == 1 ? On : Off, null)
            : new(flow.Name, Export(flow.StateCode), true, before, before, null);
}