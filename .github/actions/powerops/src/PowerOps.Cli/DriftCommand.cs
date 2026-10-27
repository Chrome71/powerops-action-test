using System.CommandLine;
using System.Text;

using PowerOps.Core.Drift;
using PowerOps.Core.Output;

namespace PowerOps.Cli;

public static class DriftCommand
{
    private const int UsageError = 2;
    private const int InternalError = 3;

    /// <param name="live">Returns the snapshot of one live environment (<c>solution</c>, <c>--env</c> value), or throws <see cref="DriftReadException"/>.</param>
    public static Command Create(TextWriter output, TextWriter error, Func<string, string, Snapshot> live)
    {
        var solution = new Option<string?>("--solution") { Description = "The solution's unique name (required)." };
        var env = new Option<string[]>("--env") { Description = "An environment, or a snapshot file (.json). Give at least two.", AllowMultipleArgumentsPerToken = false };
        var save = new Option<string?>("--save") { Description = "Write one snapshot file per live environment into this folder." };
        var format = new Option<string>("--format") { Description = "Report format.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json", "markdown");
        var outFile = new Option<string?>("--out") { Description = "Write the report here instead of stdout." };
        var command = new Command("drift", "Compare one solution across environments (or saved snapshot files): version, components, layers, variables, references and flows.")
        {
            solution, env, save, format, outFile,
        };
        command.SetAction(parse => SettingsCommand.Guard(error, () =>
            Run(parse.GetValue(solution), parse.GetValue(env) ?? [], parse.GetValue(save), parse.GetValue(format)!, parse.GetValue(outFile), output, error, live)));
        return command;
    }

    private static bool IsSnapshotPath(string value) => value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || File.Exists(value);

    private static int Run(string? solution, string[] envs, string? saveDir, string format, string? outFile, TextWriter output, TextWriter error, Func<string, string, Snapshot> live)
    {
        // --env values are never printed (they may be URLs): messages name them by position.
        var problems = new List<string>();
        if (solution is null || !DriftQueries.IsValidName(solution))
        {
            problems.Add("--solution is required and must be a solution unique name (letters, digits and underscores)");
        }

        if (envs.Length < 2)
        {
            problems.Add("give at least two --env");
        }

        for (var i = 0; i < envs.Length; i++)
        {
            if (envs[i].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !File.Exists(envs[i]))
            {
                problems.Add($"--env {i + 1}: no snapshot file at that path");
            }
        }

        if (saveDir is not null && File.Exists(saveDir))
        {
            problems.Add("--save names an existing file, not a folder");
        }

        if (saveDir is not null && envs.All(IsSnapshotPath))
        {
            problems.Add("--save needs at least one live --env; snapshot files are not saved again");
        }

        if (problems.Count > 0)
        {
            problems.ForEach(p => error.WriteLine($"error: {p}"));
            return UsageError;
        }

        var snapshots = new Snapshot[envs.Length];
        var liveAt = new List<int>();
        for (var i = 0; i < envs.Length; i++)
        {
            if (!IsSnapshotPath(envs[i]))
            {
                liveAt.Add(i);
                continue;
            }

            try
            {
                snapshots[i] = SnapshotFile.Load(envs[i]);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Only InvalidDataException's text is safe: IO messages quote the path.
                error.WriteLine($"error: --env {i + 1}: {(ex is InvalidDataException ? ex.Message : "the snapshot file could not be read")}");
                return InternalError;
            }

            if (!string.Equals(snapshots[i].Solution, solution, StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine($"error: --env {i + 1}: the snapshot is for another solution");
                return UsageError;
            }
        }

        foreach (var i in liveAt)
        {
            try
            {
                snapshots[i] = live(solution!, envs[i]);
            }
            catch (DriftReadException ex)
            {
                error.WriteLine($"error: --env {i + 1}: {ex.Message}");
                if (!string.IsNullOrEmpty(ex.Forwarded))
                {
                    error.Write(ex.Forwarded.EndsWith('\n') ? ex.Forwarded : ex.Forwarded + "\n");
                }

                return ex.ExitCode;
            }
        }

        if (snapshots.All(s => !s.Installed))
        {
            error.WriteLine($"error: solution `{solution}` is not installed in any environment");
            return UsageError;
        }

        if (saveDir is not null && Save(saveDir, solution!, liveAt, snapshots, error) is { } failed)
        {
            return failed;
        }

        var sources = envs.Select(e => IsSnapshotPath(e) ? "snapshot" : "live").ToList();
        var items = DriftCompare.Compare(snapshots);
        var report = new StringWriter();
        switch (format)
        {
            case "json":
                DriftReport.WriteJson(solution!, snapshots, sources, items, report);
                break;
            case "markdown":
                DriftReport.WriteMarkdown(solution!, snapshots, sources, items, report);
                break;
            default:
                DriftReport.WriteText(solution!, snapshots, sources, items, report);
                break;
        }

        if (outFile is not null)
        {
            File.WriteAllText(outFile, report.ToString(), new UTF8Encoding(false));
        }
        else
        {
            output.Write(report.ToString());
        }

        return DriftReport.HasDrift(items) ? 1 : 0;
    }

    // Returns an exit code when nothing was saved, else null.
    private static int? Save(string dir, string solution, List<int> liveAt, Snapshot[] snapshots, TextWriter error)
    {
        var names = liveAt.ToDictionary(i => i, i => FileName(solution, snapshots[i].Environment));
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (i, name) in names)
        {
            if (!seen.TryAdd(name, i))
            {
                error.WriteLine($"error: --env {seen[name] + 1} and --env {i + 1} would be saved to the same file name; nothing was saved");
                return UsageError;
            }
        }

        Directory.CreateDirectory(dir);
        foreach (var (i, name) in names)
        {
            File.WriteAllText(Path.Combine(dir, name), SnapshotFile.Write(snapshots[i]), new UTF8Encoding(false));
        }

        return null;
    }

    private static string FileName(string solution, string environment) =>
        $"{solution}.{new string(environment.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '-').ToArray())}.json";
}