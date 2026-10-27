using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using PowerOps.Core.Deploy;

namespace PowerOps.Core.Output;

/// <summary>The text, JSON and Markdown reports of a <see cref="DeployPlanResult"/>. The result holds no environment id or URL, no folder path, no connection id and no variable value, so none can appear here.</summary>
public static class DeployPlanReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Target and hash, one line per solution with its flows and items indented below it, then a summary; "\n" line endings.</summary>
    public static void WriteText(DeployPlanResult result, TextWriter writer)
    {
        var text = new StringBuilder($"Target: {TextReport.Clean(result.Target)}\nPlan hash: {result.Hash}\n");
        foreach (var s in result.Solutions)
        {
            var installed = s.Installed is null ? "not installed" : TextReport.Clean(s.Installed.Version);
            var file = s.Settings is null ? "" : $", settings {TextReport.Clean(s.Settings)}";
            text.Append($"{s.Order} {TextReport.Clean(s.Solution)} ({TextReport.Clean(s.Zip)}): {s.Action} {installed} -> {TextReport.Clean(s.Version)}, {Managed(s.Managed)}{file}\n");
            foreach (var f in s.Flows)
            {
                text.Append($"  flow {TextReport.Clean(f.Name)}: {TextReport.Clean(Before(f))} -> {TextReport.Clean(f.ExpectedState)}\n");
            }

            foreach (var i in result.Items.Where(i => i.Solution == s.Solution))
            {
                text.Append($"  {i.Severity} {i.Kind} {TextReport.Clean(i.Subject)}: {TextReport.Clean(i.Message)}\n");
            }
        }

        text.Append(Summary(result)).Append('\n');
        writer.Write(text.ToString());
    }

    /// <summary>Writes the JSON report, which is also the plan file (stable property order, nulls written, "\n" line endings, trailing newline).</summary>
    public static void WriteJson(DeployPlanResult result, string tool, TextWriter writer)
    {
        var doc = new Doc(JsonOutput.SchemaVersion, tool, result.Target, result.Hash, result.Blocked, result.Inputs, result.Solutions, result.Items);
        writer.Write(JsonSerializer.Serialize(doc, Options) + "\n");
    }

    /// <summary>Writes a GitHub-flavoured Markdown report with "\n" line endings. Stops at a whole row before <paramref name="maxChars"/> and says how many rows were left out.</summary>
    public static void WriteMarkdown(DeployPlanResult result, TextWriter writer, int maxChars = DiffReport.MarkdownLimit)
    {
        static string E(string? s) => MarkdownReport.Escape(s);
        var text = new StringBuilder($"## PowerOps deploy plan\n\n**{E(Summary(result))}**\n\nTarget: {E(result.Target)}\n\nPlan hash: `{result.Hash}`\n");
        var tables = new List<(string Header, List<string> Rows)>
        {
            ("| Order | Solution | Zip | Action | In target | Zip version | Managed | Settings |\n|---|---|---|---|---|---|---|---|",
                [.. result.Solutions.Select(s =>
                    $"| {s.Order} | {E(s.Solution)} | {E(s.Zip)} | {s.Action} | {(s.Installed is null ? "not installed" : E(s.Installed.Version))} | {E(s.Version)} | {Managed(s.Managed)} | {E(s.Settings)} |")]),
            ("| Solution | Severity | Kind | Subject | Message |\n|---|---|---|---|---|",
                [.. result.Items.Select(i => $"| {E(i.Solution)} | {i.Severity} | {i.Kind} | {E(i.Subject)} | {E(i.Message)} |")]),
            ("| Solution | Flow | Before | Expected |\n|---|---|---|---|",
                [.. result.Solutions.SelectMany(s => s.Flows.Select(f => $"| {E(s.Solution)} | {E(f.Name)} | {E(Before(f))} | {E(f.ExpectedState)} |"))]),
        };

        // Reserve room for the note that is appended when rows are left out.
        const int Reserve = 300;
        var (shown, total) = (0, tables.Sum(t => t.Rows.Count));
        var full = true;
        foreach (var (header, rows) in tables)
        {
            var started = false;
            foreach (var row in rows)
            {
                var line = row + "\n";
                var head = started ? "" : "\n" + header + "\n";
                if (!full || text.Length + head.Length + line.Length + Reserve > maxChars)
                {
                    full = false;
                    break;
                }

                text.Append(head).Append(line);
                started = true;
                shown++;
            }
        }

        if (shown < total)
        {
            text.Append($"\n_{(total - shown).ToString(CultureInfo.InvariantCulture)} more rows not shown, to fit in a pull request comment. Use `--format json` for the full plan._\n");
        }

        writer.Write(text.ToString());
    }

    private static string Summary(DeployPlanResult r)
    {
        var n = r.Items.Count(i => i.Severity == PlanSeverity.Block);
        return r.Blocked ? $"plan blocked: {n} blocking item{(n == 1 ? "" : "s")}" : "plan ok";
    }

    private static string Managed(bool managed) => managed ? "managed" : "unmanaged";

    private static string Before(PlannedFlow f) => f.StateBefore ?? "new";

    private sealed record Doc(
        int SchemaVersion, string Tool, string Target, string Hash, bool Blocked,
        PlanInputs Inputs, IReadOnlyList<PlannedSolution> Solutions, IReadOnlyList<PlanItem> Items);
}