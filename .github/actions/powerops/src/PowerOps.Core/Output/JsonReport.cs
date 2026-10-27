using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using PowerOps.Core.Rules;

namespace PowerOps.Core.Output;

public static class JsonReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps 'quotes' in messages readable
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Writes the JSON report (stable property order, "\n" line endings, trailing newline).</summary>
    public static void Write(AnalysisResult result, TextWriter writer)
    {
        var doc = new Doc(
            JsonOutput.SchemaVersion,
            result.Solutions.Select(s => new Sol(
                s.Path,
                s.Solution.UniqueName,
                s.Solution.Version,
                s.Solution.Managed,
                new Pub(s.Solution.Publisher.UniqueName, s.Solution.Publisher.Prefix),
                s.Requires,
                s.Findings.Select(f => new Fin(f.RuleId, f.Severity, f.Message, f.File, f.Component, Analyzer.Find(f.RuleId)?.Fix ?? "", Analyzer.HelpUri(f.RuleId))).ToList())).ToList(),
            new Summary(result.Solutions.Count, result.Errors, result.Warnings, result.Baselined));
        writer.Write(JsonSerializer.Serialize(doc, Options) + "\n");
    }

    private sealed record Doc(int SchemaVersion, IReadOnlyList<Sol> Solutions, Summary Summary);

    private sealed record Sol(string Path, string UniqueName, string Version, bool Managed, Pub Publisher, IReadOnlyList<string> Requires, IReadOnlyList<Fin> Findings);

    private sealed record Pub(string UniqueName, string Prefix);

    private sealed record Fin(string RuleId, Severity Severity, string Message, string File, string? Component, string Fix, string HelpUri);

    private sealed record Summary(int Solutions, int Errors, int Warnings, int Baselined);
}