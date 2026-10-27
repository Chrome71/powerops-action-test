using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using PowerOps.Core.Rules;

namespace PowerOps.Core.Output;

public static class RulesReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Writes every rule as JSON (stable property order, "\n" line endings, trailing newline).</summary>
    public static void WriteJson(TextWriter writer) =>
        writer.Write(JsonSerializer.Serialize(
            new Doc(JsonOutput.SchemaVersion, Analyzer.Rules.Select(r => new Item(r.Id, r.Severity, r.Title, r.Fix, Analyzer.HelpUri(r.Id))).ToList()),
            Options) + "\n");

    /// <summary>Writes one block per rule with "\n" line endings.</summary>
    public static void WriteText(TextWriter writer)
    {
        foreach (var r in Analyzer.Rules)
        {
            writer.Write($"{r.Id} {(r.Severity == Severity.Error ? "error " : "warning")} {r.Title}\n  fix: {r.Fix}\n  {Analyzer.HelpUri(r.Id)}\n");
        }
    }

    private sealed record Doc(int SchemaVersion, IReadOnlyList<Item> Rules);

    private sealed record Item(string Id, Severity Severity, string Title, string Fix, string HelpUri);
}