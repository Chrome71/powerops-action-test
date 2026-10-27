using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using PowerOps.Core.Flows;

namespace PowerOps.Core.Output;

/// <summary>The report of <c>powerops test</c>. It holds no solution path and no time, so it is stable for goldens.</summary>
public static class TestReport
{
    private const int MaxValue = 300;
    private const int MaxCell = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        MaxDepth = 128, // a value from a test file (parsed to 64 levels) plus the report's own nesting
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 128 };

    public static void WriteText(IReadOnlyList<FlowTestResult> results, TextWriter writer)
    {
        foreach (var r in Sorted(results))
        {
            writer.Write($"{Label(r.Outcome)} {TextReport.Clean(r.File)}: {TextReport.Clean(r.Flow)}\n");
            if (r.Message is not null)
            {
                writer.Write($"  {TextReport.Clean(r.Message)}\n");
            }

            foreach (var a in r.Assertions.Where(a => !a.Passed))
            {
                writer.Write($"  assertion {a.Index}, {TextReport.Clean(a.Description)}: {TextReport.Clean(Detail(a))}\n");
            }
        }

        writer.Write(Summary(results) + "\n");
    }

    /// <summary>Writes the JSON report (stable property order, "\n" line endings, trailing newline).</summary>
    public static void WriteJson(IReadOnlyList<FlowTestResult> results, TextWriter writer)
    {
        var doc = new JsonObject
        {
            ["schemaVersion"] = JsonOutput.SchemaVersion,
            ["tests"] = new JsonArray(Sorted(results).Select(Test).ToArray()),
            ["summary"] = new JsonObject
            {
                ["passed"] = Count(results, FlowTestOutcome.Passed),
                ["failed"] = Count(results, FlowTestOutcome.Failed),
                ["unmocked"] = Count(results, FlowTestOutcome.Unmocked),
                ["unsupported"] = Count(results, FlowTestOutcome.Unsupported),
            },
        };
        writer.Write(doc.ToJsonString(Options) + "\n");
    }

    public static void WriteMarkdown(IReadOnlyList<FlowTestResult> results, TextWriter writer)
    {
        var text = new StringBuilder($"## Flow tests\n\n**{Summary(results)}**\n");
        if (results.Count > 0)
        {
            text.Append("\n| Result | Test file | Flow | Detail |\n|---|---|---|---|\n");
        }

        foreach (var r in Sorted(results))
        {
            var detail = (r.Message is null ? [] : new[] { r.Message })
                .Concat(r.Assertions.Where(a => !a.Passed).Select(a => $"assertion {a.Index}, {a.Description}: {Detail(a)}"));
            text.Append($"| {Word(r.Outcome)} | {Escape(r.File)} | {Escape(r.Flow)} | {string.Join("<br>", detail.Select(Escape))} |\n");
        }

        writer.Write(text.ToString());
    }

    private static IEnumerable<FlowTestResult> Sorted(IReadOnlyList<FlowTestResult> results) => results.OrderBy(r => r.File, StringComparer.Ordinal);

    private static string Label(FlowTestOutcome outcome) => outcome switch
    {
        FlowTestOutcome.Passed => "PASS",
        FlowTestOutcome.Failed => "FAIL",
        _ => outcome.ToString().ToUpperInvariant(),
    };

    private static string Word(FlowTestOutcome outcome) => outcome.ToString().ToLowerInvariant();

    private static int Count(IReadOnlyList<FlowTestResult> results, FlowTestOutcome outcome) => results.Count(r => r.Outcome == outcome);

    private static string Summary(IReadOnlyList<FlowTestResult> results) =>
        $"{results.Count} test{(results.Count == 1 ? "" : "s")}: {Count(results, FlowTestOutcome.Passed)} passed, {Count(results, FlowTestOutcome.Failed)} failed, {Count(results, FlowTestOutcome.Unmocked)} unmocked, {Count(results, FlowTestOutcome.Unsupported)} unsupported";

    private static string Detail(FlowAssertionResult a) =>
        a.HasActual
            ? $"expected {Json(a.Expected)}, got {Json(a.Actual)}{(a.Message is null ? "" : $" ({a.Message})")}"
            : a.Message ?? $"expected {Json(a.Expected)}";

    private static string Json(JsonNode? node) => Cap(node is null ? "null" : node.ToJsonString(Compact), MaxValue);

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // Escapes after capping, so an escape sequence is never split.
    private static string Escape(string text) => MarkdownReport.Escape(Cap(text, MaxCell));

    private static JsonObject Test(FlowTestResult r)
    {
        var test = new JsonObject { ["file"] = r.File, ["flow"] = r.Flow };
        if (r.Solution is not null)
        {
            test["solution"] = r.Solution;
        }

        test["result"] = Word(r.Outcome);
        if (r.RunStatus is not null)
        {
            test["runStatus"] = r.RunStatus;
        }

        if (r.Message is not null)
        {
            test["message"] = r.Message;
        }

        test["assertions"] = new JsonArray(r.Assertions.Select(Assertion).ToArray());
        return test;
    }

    private static JsonObject Assertion(FlowAssertionResult a)
    {
        var item = new JsonObject
        {
            ["index"] = a.Index,
            ["description"] = a.Description,
            ["passed"] = a.Passed,
            ["expected"] = a.Expected?.DeepClone(),
        };
        if (a.HasActual)
        {
            item["actual"] = a.Actual?.DeepClone();
        }

        if (a.Message is not null)
        {
            item["message"] = a.Message;
        }

        return item;
    }
}