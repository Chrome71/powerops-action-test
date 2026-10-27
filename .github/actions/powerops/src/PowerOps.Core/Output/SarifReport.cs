using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using PowerOps.Core.Rules;

namespace PowerOps.Core.Output;

public static class SarifReport
{
    internal const string Repo = "https://github.com/Chrome71/PowerOps";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes a SARIF 2.1.0 log (stable property order, "\n" line endings, trailing newline).</summary>
    public static void Write(AnalysisResult result, TextWriter writer, string toolVersion)
    {
        var rules = Analyzer.Rules;
        var ids = rules.Select(r => r.Id).ToList();
        var results = new List<Res>();
        foreach (var s in result.Solutions)
        {
            var zip = s.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            foreach (var f in s.Findings)
            {
                var uri = EscapePath(zip ? s.Path : s.Path == "." ? f.File : s.Path + "/" + f.File);
                var key = string.Join("|", s.Solution.UniqueName, f.RuleId, f.Component, f.Message);
                results.Add(new Res(
                    f.RuleId,
                    ids.IndexOf(f.RuleId),
                    Level(f.Severity),
                    new Msg(f.Message),
                    [new Loc(new Phys(new Art(uri), new Region(1)), zip ? [new Logical(f.File)] : null)],
                    new Dictionary<string, string> { ["poFinding/v1"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) }));
            }
        }

        var driver = new Driver(
            "PowerOps",
            Repo,
            toolVersion,
            rules.Select(r => new Rule(
                r.Id,
                r.GetType().Name[5..],
                new Msg(r.Title),
                Analyzer.HelpUri(r.Id),
                new Msg(r.Fix),
                new Config(Level(r.Severity)))).ToList());
        var log = new Log("https://json.schemastore.org/sarif-2.1.0.json", "2.1.0", [new Run(new Tool(driver), results)]);
        writer.Write(JsonSerializer.Serialize(log, Options) + "\n");
    }

    private static string Level(Severity severity) => severity == Severity.Error ? "error" : "warning";

    // Solution paths and file names come from the solution; percent-encode each segment.
    private static string EscapePath(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private sealed record Log([property: JsonPropertyName("$schema")] string Schema, string Version, IReadOnlyList<Run> Runs);

    private sealed record Run(Tool Tool, IReadOnlyList<Res> Results);

    private sealed record Tool(Driver Driver);

    private sealed record Driver(string Name, string InformationUri, string Version, IReadOnlyList<Rule> Rules);

    private sealed record Rule(string Id, string Name, Msg ShortDescription, string HelpUri, Msg Help, Config DefaultConfiguration);

    private sealed record Config(string Level);

    private sealed record Res(string RuleId, int RuleIndex, string Level, Msg Message, IReadOnlyList<Loc> Locations, IReadOnlyDictionary<string, string> PartialFingerprints);

    private sealed record Msg(string Text);

    private sealed record Loc(Phys PhysicalLocation, IReadOnlyList<Logical>? LogicalLocations);

    private sealed record Phys(Art ArtifactLocation, Region Region);

    private sealed record Art(string Uri);

    private sealed record Region(int StartLine);

    private sealed record Logical(string FullyQualifiedName);
}