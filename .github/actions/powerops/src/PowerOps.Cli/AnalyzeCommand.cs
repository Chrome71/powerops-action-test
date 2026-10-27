using System.CommandLine;
using System.Reflection;
using System.Text;

using PowerOps.Core.Output;

namespace PowerOps.Cli;

public static class AnalyzeCommand
{
    private const int UsageError = 2;
    private const int InternalError = 3;

    public static Command Create(TextWriter output, TextWriter error)
    {
        var paths = new Argument<string[]>("path")
        {
            Description = "Solution folders or zips to analyze; folders are searched recursively.",
            Arity = ArgumentArity.ZeroOrMore,
            DefaultValueFactory = _ => ["."],
        };
        var format = new Option<string>("--format") { Description = "Report format.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json", "sarif", "markdown");
        var outFile = new Option<string?>("--out") { Description = "Write the report to this file instead of stdout." };
        var failOn = new Option<string>("--fail-on") { Description = "Lowest severity that makes the exit code 1.", DefaultValueFactory = _ => "error" };
        failOn.AcceptOnlyFromAmong("error", "warning", "none");
        var baseline = new Option<string?>("--baseline") { Description = "Ignore findings listed in this baseline file (see 'powerops baseline')." };

        var command = new Command("analyze", "Check unpacked or exported solutions for common problems (offline, read-only).")
        {
            paths, format, outFile, failOn, baseline,
        };
        command.SetAction(parse => Execute(
            parse.GetValue(paths)!, parse.GetValue(format)!, parse.GetValue(outFile), parse.GetValue(failOn)!, parse.GetValue(baseline), output, error));
        return command;
    }

    // Same value `--version` prints.
    internal static string ToolVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static int Execute(string[] paths, string format, string? outFile, string failOn, string? baselineFile, TextWriter output, TextWriter error) =>
        Run(paths, baselineFile, error, result =>
        {
            var report = new StringWriter();
            if (format == "json")
            {
                JsonReport.Write(result, report);
            }
            else if (format == "sarif")
            {
                SarifReport.Write(result, report, ToolVersion());
            }
            else if (format == "markdown")
            {
                MarkdownReport.Write(result, report);
            }
            else
            {
                TextReport.Write(result, report);
            }

            if (outFile is null)
            {
                output.Write(report.ToString());
            }
            else
            {
                File.WriteAllText(outFile, report.ToString(), new UTF8Encoding(false));
            }

            var failed = failOn switch
            {
                "error" => result.Errors > 0,
                "warning" => result.Errors + result.Warnings > 0,
                _ => false,
            };
            return failed ? 1 : 0;
        });

    /// <summary>Shared by analyze and baseline: checks the paths and the baseline file, analyzes, drops baselined findings, then hands the result to <paramref name="emit"/> (which returns the exit code). Maps failures to exit codes 2 and 3.</summary>
    internal static int Run(string[] paths, string? baselineFile, TextWriter error, Func<AnalysisResult, int> emit)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p) && !Directory.Exists(p))
            {
                error.WriteLine($"error: path not found: {p}");
                return UsageError;
            }
        }

        if (baselineFile is not null && !File.Exists(baselineFile))
        {
            error.WriteLine($"error: baseline file not found: {baselineFile}");
            return UsageError;
        }

        Baseline? baseline = null;
        if (baselineFile is not null)
        {
            try
            {
                baseline = Baseline.Load(baselineFile);
            }
            catch (InvalidDataException ex)
            {
                error.WriteLine($"error: {ex.Message}");
                return UsageError;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"error: cannot read baseline {baselineFile}: {ex.Message}");
                return UsageError;
            }
        }

        try
        {
            var result = AnalysisResult.Run(paths, Directory.GetCurrentDirectory());
            if (result.Solutions.Count == 0)
            {
                error.WriteLine($"error: no solutions found under {string.Join(", ", paths)}. To analyze a YAML layout, point at the folder that holds solutions/.");
                return UsageError;
            }

            return emit(baseline?.Apply(result) ?? result);
        }
        catch (Exception ex)
        {
            error.WriteLine(Environment.GetEnvironmentVariable("POWEROPS_DEBUG") == "1" ? ex.ToString() : $"error: {ex.Message}");
            return InternalError;
        }
    }
}