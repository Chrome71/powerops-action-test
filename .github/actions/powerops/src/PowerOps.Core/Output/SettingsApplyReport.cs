using System.Text.Encodings.Web;
using System.Text.Json;

using PowerOps.Core.Settings;

namespace PowerOps.Core.Output;

/// <summary>One step of <c>settings apply</c> (validate, target, plan, import, flows) and its status: ok, failed, skipped or planned.</summary>
public sealed record ApplyStep(string Name, string Status);

/// <summary>The outcome of <c>settings apply</c>. <c>Environment</c> is the target's name (never its id); <c>PacExitCode</c> is set when the import failed; <c>Message</c> says why a run stopped.</summary>
public sealed record ApplyResult(
    bool Applied,
    string? Environment,
    bool Managed,
    IReadOnlyList<string> ImportArguments,
    IReadOnlyList<ApplyStep> Steps,
    IReadOnlyList<SettingsProblem> Problems,
    int? PacExitCode,
    string? Message,
    IReadOnlyList<ApplyFlow> Flows);

public static class SettingsApplyReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The target line, which an <c>--apply</c> run prints before the import starts.</summary>
    public static string Header(string environment) => $"environment: {environment}\n";

    /// <summary>The target line (unless <paramref name="header"/> is false or there is no target), managed, the import command, the validate problems, one line per step and per flow, and a summary line; "\n" line endings.</summary>
    public static void WriteText(ApplyResult r, TextWriter writer, bool header = true)
    {
        if (header && r.Environment is { } environment)
        {
            writer.Write(Header(environment));
        }

        writer.Write($"managed: {(r.Managed ? "true" : "false")}\n");
        writer.Write($"command: pac {string.Join(" ", r.ImportArguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}\n");
        foreach (var p in r.Problems)
        {
            writer.Write(SettingsReport.Line(p));
        }

        foreach (var s in r.Steps)
        {
            writer.Write($"{s.Name}: {s.Status}\n");
        }

        foreach (var f in r.Flows)
        {
            writer.Write($"flow {f.Name}: {f.StateAfter ?? (f.Exists ? $"exists, {f.StateBefore}" : $"new, expected {f.ExpectedState}")}\n");
        }

        writer.Write(Summary(r) + "\n");
    }

    /// <summary>Writes the result as JSON (schemaVersion first, stable property order, "\n" line endings, trailing newline).</summary>
    public static void WriteJson(ApplyResult r, TextWriter writer) =>
        writer.Write(JsonSerializer.Serialize(
            new
            {
                SchemaVersion = JsonOutput.SchemaVersion,
                r.Applied,
                r.Environment,
                r.Managed,
                r.ImportArguments,
                r.Steps,
                r.Problems,
                r.PacExitCode,
                r.Message,
                r.Flows,
            },
            Options) + "\n");

    private static string Summary(ApplyResult r)
    {
        if (r.Message is { } message)
        {
            return message;
        }

        if (r.Problems.Count > 0)
        {
            return SettingsReport.Count(r.Problems.Count);
        }

        if (!r.Applied)
        {
            return "dry run: nothing imported";
        }

        var notOn = r.Flows.Count(f => f.StateAfter != FlowState.On);
        return r.Flows.Count == 0 ? "imported, no cloud flows"
            : notOn == 0 ? "imported, all flows on"
            : $"imported, {notOn} of {r.Flows.Count} flows not on";
    }
}