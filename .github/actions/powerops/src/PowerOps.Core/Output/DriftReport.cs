using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using PowerOps.Core.Drift;

namespace PowerOps.Core.Output;

public static class DriftReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static bool HasDrift(IReadOnlyList<DriftItem> items) => items.Any(i => i.Drift);

    /// <summary>One line per environment, one per item, then a summary; "\n" line endings. <paramref name="sources"/> is unused here and kept so the three writers share a signature.</summary>
    public static void WriteText(string solution, IReadOnlyList<Snapshot> envs, IReadOnlyList<string> sources, IReadOnlyList<DriftItem> items, TextWriter writer)
    {
        for (var i = 0; i < envs.Count; i++)
        {
            var e = envs[i];
            var state = !e.Installed
                ? "not installed"
                : $"{TextReport.Clean(e.Version)}, {(e.Managed == true ? "managed" : "unmanaged")}{(e.Managed == true && !e.LayersRead ? ", layers not read" : "")}";
            writer.Write($"{i + 1} {TextReport.Clean(e.Environment)}: {state}\n");
        }

        foreach (var x in items)
        {
            var change = x.Expected is null && x.Actual is null ? "" : $": {TextReport.Clean(x.Expected)} -> {TextReport.Clean(x.Actual)}";
            writer.Write($"{x.Position} {TextReport.Clean(x.Environment)}: {x.Kind} {TextReport.Clean(x.Subject)}{change}{(x.Drift ? "" : " (info)")}\n");
        }

        writer.Write(Summary(items) + "\n");
    }

    /// <summary>Writes the JSON report (stable property order, nulls written, "\n" line endings, trailing newline).</summary>
    public static void WriteJson(string solution, IReadOnlyList<Snapshot> envs, IReadOnlyList<string> sources, IReadOnlyList<DriftItem> items, TextWriter writer)
    {
        var doc = new Doc(
            JsonOutput.SchemaVersion,
            solution,
            HasDrift(items),
            envs.Select((e, i) => new Env(i + 1, e.Environment, sources[i], e.Installed, e.Version, e.Managed, e.LayersRead)).ToList(),
            items);
        writer.Write(JsonSerializer.Serialize(doc, Options) + "\n");
    }

    /// <summary>Writes a GitHub-flavoured Markdown report with "\n" line endings.</summary>
    public static void WriteMarkdown(string solution, IReadOnlyList<Snapshot> envs, IReadOnlyList<string> sources, IReadOnlyList<DriftItem> items, TextWriter writer)
    {
        static string E(string? s) => MarkdownReport.Escape(s);
        var text = new StringBuilder($"## PowerOps drift\n\n**{E(solution)}: {E(Summary(items))}**\n\n");
        text.Append("| Position | Environment | Source | Version | Managed | Layers read |\n|---|---|---|---|---|---|\n");
        for (var i = 0; i < envs.Count; i++)
        {
            var e = envs[i];
            text.Append($"| {i + 1} | {E(e.Environment)} | {E(sources[i])} | {E(e.Version)} | {(e.Managed is { } m ? (m ? "managed" : "unmanaged") : "")} | {(e.Installed ? (e.LayersRead ? "yes" : "no") : "")} |\n");
        }

        if (items.Count > 0)
        {
            text.Append("\n| Position | Environment | Kind | Subject | Drift | Expected | Actual |\n|---|---|---|---|---|---|---|\n");
            foreach (var x in items)
            {
                text.Append($"| {x.Position} | {E(x.Environment)} | {x.Kind} | {E(x.Subject)} | {(x.Drift ? "yes" : "no")} | {E(x.Expected)} | {E(x.Actual)} |\n");
            }
        }

        writer.Write(text.ToString());
    }

    private static string Summary(IReadOnlyList<DriftItem> items)
    {
        var n = items.Count(i => i.Drift);
        return n == 0 ? "no drift" : $"{n} drift item{(n == 1 ? "" : "s")}";
    }

    private sealed record Doc(int SchemaVersion, string Solution, bool Drift, IReadOnlyList<Env> Environments, IReadOnlyList<DriftItem> Items);

    private sealed record Env(int Position, string Name, string Source, bool Installed, string? Version, bool? Managed, bool LayersRead);
}