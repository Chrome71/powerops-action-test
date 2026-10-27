using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using PowerOps.Core.Diff;
using PowerOps.Core.Model;

namespace PowerOps.Core.Output;

public static class DiffReport
{
    /// <summary>GitHub rejects PR comments over 65,536 characters; stay well below.</summary>
    public const int MarkdownLimit = 60_000;

    private const int MaxCell = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void WriteText(DiffResult result, TextWriter writer)
    {
        writer.Write(Header(result, TextReport.Clean) + "\n");
        foreach (var c in result.Changes)
        {
            var risk = c.Risks.Count == 0 ? "" : $" [risk: {string.Join(", ", c.Risks)}]";
            writer.Write($"  {Verb(c.Kind)} {c.Component} {TextReport.Clean(Label(c))}{(c.Detail is null ? "" : ": " + TextReport.Clean(c.Detail))}{risk}\n");
        }

        writer.Write(Summary(result) + "\n");
    }

    /// <summary>Writes the JSON report (stable property order, "\n" line endings, trailing newline).</summary>
    public static void WriteJson(DiffResult result, TextWriter writer)
    {
        var doc = new Doc(
            JsonOutput.SchemaVersion,
            new Sol(result.Old.UniqueName, result.Old.Version),
            new Sol(result.New.UniqueName, result.New.Version),
            result.Changes.Select(c => new Item(c.Kind, c.Component, c.Key, c.Name, c.Detail, c.Risks)).ToList(),
            new Counts(Count(result, ChangeKind.Added), Count(result, ChangeKind.Removed), Count(result, ChangeKind.Changed)));
        writer.Write(JsonSerializer.Serialize(doc, Options) + "\n");
    }

    /// <summary>Writes a GitHub-flavoured Markdown report with "\n" line endings. Stops at a whole row before <paramref name="maxChars"/> and says how many changes were left out.</summary>
    public static void WriteMarkdown(DiffResult result, TextWriter writer, int maxChars = MarkdownLimit)
    {
        var text = new StringBuilder($"## PowerOps diff\n\n**{Escape(Header(result, s => s))}**\n\n{Escape(Summary(result))}\n");
        if (result.Changes.Count > 0)
        {
            foreach (var id in result.Changes.SelectMany(c => c.Risks).Distinct().Order(StringComparer.Ordinal))
            {
                text.Append($"\n- `{id}`: {Risk.Describe(id)}");
            }

            text.Append("\n\n| Change | Component | Name | Detail | Risk |\n|---|---|---|---|---|\n");
        }

        // Reserve room for the note that is appended when rows are left out.
        const int Reserve = 300;
        var shown = 0;
        foreach (var c in result.Changes)
        {
            var row = $"| {Verb(c.Kind)} | {c.Component} | {Escape(Label(c))} | {Escape(c.Detail)} | {string.Join(" ", c.Risks.Select(r => $"`{r}`"))} |\n";
            if (text.Length + row.Length + Reserve > maxChars)
            {
                break;
            }

            text.Append(row);
            shown++;
        }

        if (shown < result.Changes.Count)
        {
            text.Append($"\n_{result.Changes.Count - shown} more changes not shown, to fit in a pull request comment. Use `--format json` for the full list._\n");
        }

        writer.Write(text.ToString());
    }

    private static string Header(DiffResult r, Func<string, string> clean) =>
        $"{clean(r.Old.UniqueName)} {clean(r.Old.Version)} → {clean(r.New.UniqueName)} {clean(r.New.Version)}";

    private static string Summary(DiffResult r) =>
        r.Changes.Count == 0
            ? "no changes"
            : $"{r.Changes.Count} change{(r.Changes.Count == 1 ? "" : "s")} ({Count(r, ChangeKind.Added)} added, {Count(r, ChangeKind.Removed)} removed, {Count(r, ChangeKind.Changed)} changed)";

    private static int Count(DiffResult r, ChangeKind kind) => r.Changes.Count(c => c.Kind == kind);

    private static string Verb(ChangeKind kind) => kind.ToString().ToLowerInvariant();

    private static string Label(Change c) => c.Name is null ? c.Key : $"{c.Name} ({c.Key})";

    // Escapes first, then caps the escaped length; a surrogate pair or an escape sequence is never split.
    private static string Escape(string? text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < (text?.Length ?? 0); i++)
        {
            var unit = char.IsHighSurrogate(text![i]) && i + 1 < text.Length ? text.Substring(i, 2) : text[i].ToString();
            var escaped = MarkdownReport.Escape(unit);
            if (result.Length + escaped.Length > MaxCell)
            {
                return result.Append('…').ToString();
            }

            result.Append(escaped);
            i += unit.Length - 1;
        }

        return result.ToString();
    }

    private sealed record Doc(int SchemaVersion, Sol Old, Sol New, IReadOnlyList<Item> Changes, Counts Summary);

    private sealed record Sol(string UniqueName, string Version);

    private sealed record Item(ChangeKind Kind, string Component, string Key, string? Name, string? Detail, IReadOnlyList<string> Risks);

    private sealed record Counts(int Added, int Removed, int Changed);
}