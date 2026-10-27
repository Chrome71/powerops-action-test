using PowerOps.Core.Rules;

namespace PowerOps.Core.Output;

public static class TextReport
{
    /// <summary>Writes the human-readable report with "\n" line endings on every OS.</summary>
    public static void Write(AnalysisResult result, TextWriter writer)
    {
        foreach (var s in result.Solutions)
        {
            var sol = s.Solution;
            writer.Write($"{Clean(s.Path)}: {Clean(sol.UniqueName)} {Clean(sol.Version)} (publisher {Clean(sol.Publisher.UniqueName)}, prefix {Clean(sol.Publisher.Prefix)})\n");
            foreach (var required in s.Requires)
            {
                writer.Write($"  requires {Clean(required)}\n");
            }

            foreach (var f in s.Findings)
            {
                var severity = f.Severity == Severity.Error ? "error" : "warning";
                writer.Write($"  {severity} {f.RuleId} {Clean(f.File)}: {Clean(f.Message)}\n");
            }

            if (s.Findings.Count == 0)
            {
                writer.Write("  no findings\n");
            }
        }

        writer.Write(Summary(result) + "\n");
    }

    internal static string Summary(AnalysisResult result) =>
        $"{Count(result.Solutions.Count, "solution")}, {Count(result.Errors, "error")}, {Count(result.Warnings, "warning")}{(result.Baselined > 0 ? $", {result.Baselined} baselined" : "")}";

    // A hostile solution must not forge lines (or "::error::" workflow commands) in the report.
    public static string Clean(string? text) => string.Concat((text ?? "").Select(c => char.IsControl(c) ? ' ' : c));

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}