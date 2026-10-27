using System.CommandLine;
using System.Text;

using PowerOps.Core.Flows;
using PowerOps.Core.Output;
using PowerOps.Core.Readers;

namespace PowerOps.Cli;

public static class TestCommand
{
    public static Command Create(TextWriter output, TextWriter error)
    {
        var path = new Argument<string>("path")
        {
            Description = "The solution folder or zip to test against; folders are searched recursively.",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => ".",
        };
        var tests = new Option<string?>("--tests") { Description = "The folder to search for *.flowtest.json files (default: the path; needed when the path is a zip)." };
        var format = new Option<string>("--format") { Description = "Report format.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json", "markdown");
        var outFile = new Option<string?>("--out") { Description = "Write the report to this file instead of stdout." };

        var command = new Command(
            "test",
            "Run offline tests (*.flowtest.json) against the cloud flows of the solutions under a path, with mocked connector actions (no tenant, read-only). " +
            "The simulator is written from Microsoft Learn and has not been compared with a real run yet. Exit 1 when a test fails, is unmocked or uses something unsupported.")
        {
            path, tests, format, outFile,
        };
        command.SetAction(parse => Execute(parse.GetValue(path)!, parse.GetValue(tests), parse.GetValue(format)!, parse.GetValue(outFile), output, error));
        return command;
    }

    private static int Execute(string path, string? testsFolder, string format, string? outFile, TextWriter output, TextWriter error)
    {
        var tests = testsFolder ?? path;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            error.WriteLine($"error: path not found: {path}");
            return 2;
        }

        if (!Directory.Exists(tests))
        {
            error.WriteLine(File.Exists(tests)
                ? $"error: the tests location is a file: {tests}. Pass a folder with --tests (a zip path needs it)."
                : $"error: tests folder not found: {tests}");
            return 2;
        }

        var testFiles = FlowTestFiles.Find(tests);
        if (testFiles.Count == 0)
        {
            error.WriteLine($"error: no *.flowtest.json files found under {tests}");
            return 2;
        }

        var solutionPaths = SolutionDiscovery.Find(path);
        if (solutionPaths.Count == 0)
        {
            error.WriteLine($"error: no solutions found under {path}. To test a YAML layout, point at the folder that holds solutions/.");
            return 2;
        }

        try
        {
            // Everything is read and checked before the first test runs, so one bad file or solution blocks them all.
            var files = testFiles.Select(f => (Name: Path.GetRelativePath(tests, f).Replace('\\', '/'), Test: FlowTestFile.Load(f))).ToList();
            var context = new LoadContext();
            var solutions = solutionPaths.Select(p => SolutionLoader.Load(p, context)).ToList();
            var locations = solutionPaths.Select(p => Path.GetRelativePath(path, p).Replace('\\', '/')).ToList();
            var results = files.Select(f => FlowTestRunner.Run(f.Name, f.Test, solutions, locations)).ToList();

            var report = new StringWriter();
            if (format == "json")
            {
                TestReport.WriteJson(results, report);
            }
            else if (format == "markdown")
            {
                TestReport.WriteMarkdown(results, report);
            }
            else
            {
                TestReport.WriteText(results, report);
            }

            if (outFile is null)
            {
                output.Write(report.ToString());
            }
            else
            {
                File.WriteAllText(outFile, report.ToString(), new UTF8Encoding(false));
            }

            return results.All(r => r.Outcome == FlowTestOutcome.Passed) ? 0 : 1;
        }
        catch (Exception ex)
        {
            error.WriteLine(Environment.GetEnvironmentVariable("POWEROPS_DEBUG") == "1" ? ex.ToString() : $"error: {ex.Message}");
            return 3;
        }
    }
}