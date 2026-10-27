using PowerOps.Core.Rules;

namespace PowerOps.Core.Output;

public static class MarkdownReport
{
    /// <summary>Writes a GitHub-flavoured Markdown report (for job summaries and PR comments) with "\n" line endings.</summary>
    public static void Write(AnalysisResult result, TextWriter writer)
    {
        writer.Write($"## PowerOps analyze\n\n**{TextReport.Summary(result)}**\n");
        foreach (var s in result.Solutions)
        {
            var sol = s.Solution;
            writer.Write($"\n### {Escape(s.Path)}: {Escape(sol.UniqueName)} {Escape(sol.Version)}\n\n");
            if (s.Findings.Count == 0)
            {
                writer.Write("No findings.\n");
                continue;
            }

            writer.Write("| Severity | Rule | File | Message | Fix |\n|---|---|---|---|---|\n");
            foreach (var f in s.Findings)
            {
                var severity = f.Severity == Severity.Error ? "error" : "warning";
                writer.Write($"| {severity} | [{f.RuleId}]({Analyzer.HelpUri(f.RuleId)}) | {Escape(f.File)} | {Escape(f.Message)} | {Escape(Analyzer.Find(f.RuleId)?.Fix)} |\n");
            }
        }
    }

    // Everything from the solution is untrusted: control characters become spaces and Markdown/HTML syntax is backslash-escaped.
    internal static string Escape(string? text) =>
        string.Concat((text ?? "").Select(c => char.IsControl(c) ? " " : "\\|*_[]<>`".Contains(c) ? "\\" + c : c.ToString()));
}