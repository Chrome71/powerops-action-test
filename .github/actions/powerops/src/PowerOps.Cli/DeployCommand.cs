using System.CommandLine;
using System.Text;

using PowerOps.Core.Deploy;
using PowerOps.Core.Drift;
using PowerOps.Core.Model;
using PowerOps.Core.Output;
using PowerOps.Core.Readers;

namespace PowerOps.Cli;

public static class DeployCommand
{
    private const int UsageError = 2;
    private const int InternalError = 3;
    private const long MaxSettingsBytes = 1024 * 1024;
    private const string SettingsSuffix = ".settings.json";

    /// <param name="pac">Runs pac; <c>deploy apply</c> uses it for the imports only.</param>
    /// <param name="target">Reads the target: the <c>--target</c> value, the solution names of the folder and the dependency names not in it. Throws <see cref="DriftReadException"/>.</param>
    public static Command Create(
        TextWriter output, TextWriter error, Func<IReadOnlyList<string>, TimeSpan, PacResult> pac, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, DeployTarget> target)
    {
        var folder = new Argument<string>("folder") { Description = "A folder of exported solution zips, each with an optional <zip name>.settings.json beside it." };
        var targetOption = new Option<string?>("--target") { Description = "The environment to plan against (an id or URL, as pac --environment). Never printed." };
        var save = new Option<string?>("--save") { Description = "Also write the JSON report (the plan file) here, whatever --format is." };
        var format = new Option<string>("--format") { Description = "Report format.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json", "markdown");
        var outFile = new Option<string?>("--out") { Description = "Write the report here instead of stdout." };
        var plan = new Command("plan", "Say what importing the solution zips in a folder into the target would do. Reads the target only.")
        {
            folder, targetOption, save, format, outFile,
        };
        plan.SetAction(parse => SettingsCommand.Guard(error, () =>
            Run(parse.GetValue(folder)!, parse.GetValue(targetOption), parse.GetValue(save), parse.GetValue(format)!, parse.GetValue(outFile), output, error, target)));
        return new Command("deploy", "Plan a deployment of solution zips, and import a reviewed plan.") { plan, Apply(output, error, pac, target) };
    }

    private static Command Apply(
        TextWriter output, TextWriter error, Func<IReadOnlyList<string>, TimeSpan, PacResult> pac, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, DeployTarget> target)
    {
        var planFile = new Argument<string>("plan-file") { Description = "A plan file written by deploy plan --save. The zips and settings files are read from its folder." };
        var targetOption = new Option<string?>("--target") { Description = "The environment the plan was made for (an id or URL, as pac --environment). Never printed." };
        var apply = new Option<bool>("--apply") { Description = "Import the solutions. Without it the command is a dry run that writes nothing." };
        var format = new Option<string>("--format") { Description = "Report format.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        var outFile = new Option<string?>("--out") { Description = "Write the report here instead of stdout." };
        var command = new Command("apply", "Plan again against the target, refuse when anything changed since the reviewed plan, then import the zips in plan order through pac (only with --apply).")
        {
            planFile, targetOption, apply, format, outFile,
        };
        command.SetAction(parse => SettingsCommand.Guard(error, () =>
            Apply(parse.GetValue(planFile)!, parse.GetValue(targetOption), parse.GetValue(apply), parse.GetValue(format)!, parse.GetValue(outFile), output, error, pac, target)));
        return command;
    }

    private static string Clean(string? text) => TextReport.Clean(text);

    private sealed record Prepared(DeployPlanResult Plan, IReadOnlyList<DeployZip> Zips, DeployTarget Target);

    private static int Run(
        string folder, string? target, string? save, string format, string? outFile, TextWriter output, TextWriter error,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, DeployTarget> read)
    {
        // The --target value and the folder's full path are never printed.
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(target))
        {
            problems.Add("--target is required");
        }

        foreach (var (name, value) in new[] { ("--save", save), ("--out", outFile) })
        {
            if (value is not null && Directory.Exists(value))
            {
                problems.Add($"{name} names an existing folder, not a file");
            }
        }

        if (Prepare(folder, target, problems, error, read, out var code) is not { } prepared)
        {
            return code;
        }

        var result = prepared.Plan;

        var report = new StringWriter();
        switch (format)
        {
            case "json":
                DeployPlanReport.WriteJson(result, AnalyzeCommand.ToolVersion(), report);
                break;
            case "markdown":
                DeployPlanReport.WriteMarkdown(result, report);
                break;
            default:
                DeployPlanReport.WriteText(result, report);
                break;
        }

        if (save is not null)
        {
            var json = new StringWriter();
            DeployPlanReport.WriteJson(result, AnalyzeCommand.ToolVersion(), json);
            File.WriteAllText(save, json.ToString(), new UTF8Encoding(false));
        }

        if (outFile is not null)
        {
            File.WriteAllText(outFile, report.ToString(), new UTF8Encoding(false));
        }
        else
        {
            output.Write(report.ToString());
        }

        return result.Blocked ? 1 : 0;
    }

    private static int Apply(
        string planFile, string? target, bool apply, string format, string? outFile, TextWriter output, TextWriter error,
        Func<IReadOnlyList<string>, TimeSpan, PacResult> pac, Func<string, IReadOnlyList<string>, IReadOnlyList<string>, DeployTarget> read)
    {
        // The --target value, the environment id and every folder path are never printed.
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(target))
        {
            problems.Add("--target is required");
        }

        if (!File.Exists(planFile))
        {
            problems.Add("the plan file does not exist");
        }

        if (outFile is not null && Directory.Exists(outFile))
        {
            problems.Add("--out names an existing folder, not a file");
        }
        else if (outFile is not null && !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(outFile))))
        {
            problems.Add("the folder of --out does not exist");
        }

        if (problems.Count > 0)
        {
            problems.ForEach(p => error.WriteLine($"error: {p}"));
            return UsageError;
        }

        var tool = AnalyzeCommand.ToolVersion();
        var folder = Path.GetDirectoryName(Path.GetFullPath(planFile))!;
        var progress = format == "json" ? error : output;

        int Finish(int code, string name, string hash, string status, string? message, IReadOnlyList<AppliedSolution> solutions, IReadOnlyList<AppliedFlow> flows)
        {
            var report = new StringWriter();
            var result = new DeployApplyResult(tool, apply, name, hash, status, message, solutions, flows);
            if (format == "json")
            {
                DeployApplyReport.WriteJson(result, report);
            }
            else
            {
                DeployApplyReport.WriteText(result, report);
            }

            if (outFile is not null)
            {
                File.WriteAllText(outFile, report.ToString(), new UTF8Encoding(false));
            }
            else
            {
                output.Write(report.ToString());
            }

            return code;
        }

        var info = new FileInfo(planFile);
        PlanFileRead reviewedRead;
        try
        {
            if (info.LinkTarget is not null || info.Length > MaxSettingsBytes)
            {
                error.WriteLine(info.LinkTarget is not null ? "error: the plan file is a symbolic link; symbolic links are not read" : "error: the plan file is larger than 1 MB");
                return InternalError;
            }

            reviewedRead = DeployPlanFile.Read(File.ReadAllText(planFile), tool);
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine($"error: {Clean(ex.Message)}");
            return InternalError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("error: the plan file could not be read");
            return InternalError;
        }

        // A file of another version has no fields to trust, so its report has no target and no hash.
        if (reviewedRead.Plan is not { } reviewed)
        {
            return Finish(1, "", "", ApplyStatus.Refused, reviewedRead.Refusal, [], []);
        }

        if (reviewed.Blocked)
        {
            return Finish(1, reviewed.Target, reviewed.Hash, ApplyStatus.Refused, "a blocked plan is never applied", [], []);
        }

        if (Prepare(folder, target, [], error, read, out var code, prefixForwarded: true) is not { } now)
        {
            return code;
        }

        var plan = now.Plan;
        if (!string.Equals(plan.Target, reviewed.Target, StringComparison.Ordinal))
        {
            error.WriteLine($"error: the plan was made for {Clean(reviewed.Target)}, --target is {Clean(plan.Target)}; nothing was imported");
            return UsageError;
        }

        List<AppliedSolution> Rows(Func<PlannedSolution, string> import) =>
            [.. plan.Solutions.Select(s => new AppliedSolution(s.Order, s.Solution, s.Zip, s.Action, import(s), null))];

        if (plan.Hash != reviewed.Hash)
        {
            var why = DeployApply.ChangedInput(reviewed.Inputs, plan.Inputs) ?? DeployApply.InputsEqualMessage;
            return Finish(1, plan.Target, reviewed.Hash, ApplyStatus.Refused, $"{why}; nothing was imported. Plan again and review the new plan.", Rows(_ => ImportStatus.NotRun), []);
        }

        if (plan.Blocked)
        {
            return Finish(1, plan.Target, reviewed.Hash, ApplyStatus.Refused, "the plan is blocked now; nothing was imported. Plan again.", Rows(_ => ImportStatus.NotRun), []);
        }

        IReadOnlyList<ImportStep> imports;
        IReadOnlyList<FlowRef> flowRefs;
        try
        {
            imports = DeployApply.Imports(plan, now.Zips);
            flowRefs = DeployApply.FlowIds(plan, now.Zips);
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine($"error: {Clean(ex.Message)}");
            return InternalError;
        }

        var planned = flowRefs.Select(f => new AppliedFlow(f.Solution, f.Flow.Name, f.Flow.ExpectedState, null)).ToList();
        if (!apply)
        {
            foreach (var step in imports)
            {
                progress.WriteLine($"would run: pac {string.Join(' ', SettingsCommand.ImportArguments(SettingsCommand.EnvironmentPlaceholder, step.Zip, step.Settings, step.StageAndUpgrade).Select(Clean))}");
            }

            return Finish(0, plan.Target, reviewed.Hash, ApplyStatus.Planned, null, Rows(s => s.Action == PlanAction.Skip ? ImportStatus.Skipped : ImportStatus.Planned), planned);
        }

        progress.Write(SettingsApplyReport.Header(Clean(plan.Target)));
        var state = new Dictionary<int, string>();
        int? pacExit = null;
        string? failure = null;
        foreach (var step in imports)
        {
            failure = Import(step);
            state[step.Order] = failure is null ? ImportStatus.Ok : ImportStatus.Failed;
            if (failure is not null)
            {
                break;
            }
        }

        List<AppliedSolution> Done() =>
            [.. plan.Solutions.Select(s => new AppliedSolution(
                s.Order, s.Solution, s.Zip, s.Action,
                s.Action == PlanAction.Skip ? ImportStatus.Skipped : state.GetValueOrDefault(s.Order, ImportStatus.NotRun),
                state.GetValueOrDefault(s.Order) == ImportStatus.Failed ? pacExit : null))];

        if (failure is not null)
        {
            var did = imports.Where(i => state.GetValueOrDefault(i.Order) == ImportStatus.Ok).Select(i => i.Solution).ToList();
            var after = did.Count == 0 ? "Nothing was imported." : $"Imported: {string.Join(", ", did)}.";
            return Finish(InternalError, plan.Target, reviewed.Hash, ApplyStatus.Failed, $"{failure} There is no rollback. {after}", Done(), []);
        }

        if (flowRefs.Count == 0)
        {
            return Finish(0, plan.Target, reviewed.Hash, ApplyStatus.Imported, null, Done(), []);
        }

        DeployTarget second;
        try
        {
            second = read(target!, [.. now.Zips.Select(z => z.Solution.UniqueName)], DeployPlan.TargetDependencies(now.Zips.Select(z => z.Solution)));
        }
        catch (Exception ex)
        {
            // Whatever the read throws, the imports stand and the report must still be written; only powerops' own read message is quoted.
            if (ex is DriftReadException drift)
            {
                Forward(error, drift.Forwarded);
            }

            var why = ex is DriftReadException d ? $": {d.Message}" : ".";
            return Finish(InternalError, plan.Target, reviewed.Hash, ApplyStatus.Imported, $"The imports succeeded. Reading the target again failed{why}", Done(), planned);
        }

        var flows = DeployApply.FlowsAfter(flowRefs, second.Solutions);
        return Finish(flows.AsExpected ? 0 : 1, plan.Target, reviewed.Hash, ApplyStatus.Imported, null, Done(), flows.Flows);

        // The failure message of one import, or null when it went through; hashes both files again first.
        string? Import(ImportStep step)
        {
            var input = plan.Inputs.Zips.First(z => z.Zip == step.Zip);
            var zip = Path.Combine(folder, step.Zip);
            var settings = step.Settings is null ? null : Path.Combine(folder, step.Settings);
            var changed = Changed(zip, input.ZipSha256) ? step.Zip : settings is not null && Changed(settings, input.SettingsSha256) ? step.Settings : null;
            if (changed is not null)
            {
                return $"{changed} changed after the re-plan read it, so it was not imported.";
            }

            progress.WriteLine($"importing {step.Order} {Clean(step.Solution)} ({Clean(step.Zip)})");
            progress.Flush();
            var result = pac(SettingsCommand.ImportArguments(now.Target.Id.ToString("D"), zip, settings, step.StageAndUpgrade), SettingsCommand.ImportTimeout);
            if (!result.Started)
            {
                return "pac was not found on PATH.";
            }

            if (result.TimedOut)
            {
                return SettingsCommand.ImportTimedOut;
            }

            if (result.ExitCode != 0)
            {
                Forward(error, result.Stdout);
                Forward(error, result.Stderr);
                pacExit = result.ExitCode;
                return $"The import failed (pac exit code {result.ExitCode}); pac's output is on stderr.";
            }

            progress.WriteLine($"imported {Clean(step.Solution)}");
            return null;
        }
    }

    // True when the file's SHA-256 is not `expected`, or it cannot be read.
    private static bool Changed(string path, string? expected)
    {
        try
        {
            return DeployPlan.Sha256Hex(File.ReadAllBytes(path)) != expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Another tool's text goes to stderr with every line prefixed, so no line can start with "error:" or "::".
    private static void Forward(TextWriter error, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').ToList().ForEach(l => error.WriteLine("  | " + Clean(l)));
        }
    }

    // The folder's usage checks (their problems join `problems`), then the zips and settings files loaded, the target read and the plan built.
    // Returns null with the exit code (2 usage, 3 unreadable input or a failed read) when any of them fails; the caller prints nothing more.
    private static Prepared? Prepare(
        string folder, string? target, List<string> problems, TextWriter error,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, DeployTarget> read, out int code, bool prefixForwarded = false)
    {
        code = 0;
        var zips = new List<(string Name, string Path)>();
        if (!Directory.Exists(folder))
        {
            problems.Add("the folder argument is not an existing folder");
        }
        else
        {
            zips = ListZips(folder, problems);
        }

        if (problems.Count > 0)
        {
            problems.ForEach(p => error.WriteLine($"error: {p}"));
            code = UsageError;
            return null;
        }

        var loaded = new List<DeployZip>();
        foreach (var (name, path) in zips)
        {
            if (Load(name, path, folder, error) is not { } zip)
            {
                code = InternalError;
                return null;
            }

            loaded.Add(zip);
        }

        foreach (var g in loaded.GroupBy(z => z.Solution.UniqueName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            error.WriteLine($"error: {string.Join(" and ", g.Select(z => Clean(z.File)))} hold the same solution ({Clean(g.Key)})");
            code = UsageError;
            return null;
        }

        DeployTarget got;
        try
        {
            // Build checks every zip (unique name, version, flow ids, settings file) before any read, against a target that holds none of them.
            DeployPlan.Build(loaded, new DeployTarget("", Guid.Empty, [.. loaded.Select(z => new Snapshot("", z.Solution.UniqueName, false, null, null, true, [], [], [], []))], new Dictionary<string, string?>()));
            got = read(target!, [.. loaded.Select(z => z.Solution.UniqueName)], DeployPlan.TargetDependencies(loaded.Select(z => z.Solution)));
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine($"error: {Clean(ex.Message)}");
            code = InternalError;
            return null;
        }
        catch (DriftReadException ex)
        {
            error.WriteLine($"error: --target: {ex.Message}");
            if (prefixForwarded)
            {
                Forward(error, ex.Forwarded);
            }
            else if (!string.IsNullOrEmpty(ex.Forwarded))
            {
                error.Write(ex.Forwarded.EndsWith('\n') ? ex.Forwarded : ex.Forwarded + "\n");
            }

            code = ex.ExitCode;
            return null;
        }

        DeployPlanResult result;
        try
        {
            result = DeployPlan.Build(loaded, got);
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine($"error: {Clean(ex.Message)}");
            code = InternalError;
            return null;
        }

        return new Prepared(result, loaded, got);
    }

    // The folder's zips in ordinal order; problems are added for every file name, case or link that breaks the rules.
    private static List<(string Name, string Path)> ListZips(string folder, List<string> problems)
    {
        var files = Directory.EnumerateFiles(folder)
            .Select(f => (Name: System.IO.Path.GetFileName(f), Path: f))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        var zips = files.Where(f => f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        var settings = files.Where(f => f.Name.EndsWith(SettingsSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 0)
        {
            problems.Add("the folder holds no zip file");
        }

        var bad = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in zips.Concat(settings))
        {
            if (!DeployPlanFile.IsSafeName(f.Name))
            {
                problems.Add($"{Clean(f.Name)}: a zip or settings file name may hold only letters, digits, '.', '_' and '-'");
                bad.Add(f.Name);
            }
            else if (new FileInfo(f.Path).LinkTarget is not null)
            {
                problems.Add($"{Clean(f.Name)}: symbolic links are not read");
                bad.Add(f.Name);
            }
        }

        var expected = zips.Where(z => !bad.Contains(z.Name)).Select(z => (Zip: z.Name, Settings: System.IO.Path.GetFileNameWithoutExtension(z.Name) + SettingsSuffix)).ToList();
        foreach (var s in settings.Where(s => !bad.Contains(s.Name) && !expected.Any(e => e.Settings == s.Name)))
        {
            var near = expected.FirstOrDefault(e => string.Equals(e.Settings, s.Name, StringComparison.OrdinalIgnoreCase));
            problems.Add(near.Zip is null
                ? $"{Clean(s.Name)}: no zip file has this settings file name"
                : $"{Clean(s.Name)}: differs only in case from {Clean(near.Settings)}, the settings file name of {Clean(near.Zip)}");
        }

        return [.. zips.Select(z => (z.Name, z.Path))];
    }

    // Returns null (after printing the reason) when the zip or its settings file cannot be read.
    private static DeployZip? Load(string name, string path, string folder, TextWriter error)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var solution = SolutionLoader.Load(path);
            var settingsName = System.IO.Path.GetFileNameWithoutExtension(name) + SettingsSuffix;
            var settingsPath = System.IO.Path.Combine(folder, settingsName);
            if (!File.Exists(settingsPath))
            {
                return new DeployZip(name, DeployPlan.Sha256Hex(bytes), solution, null, null, null);
            }

            if (new FileInfo(settingsPath).Length > MaxSettingsBytes)
            {
                error.WriteLine($"error: {Clean(settingsName)}: the settings file is larger than 1 MB");
                return null;
            }

            var settings = File.ReadAllBytes(settingsPath);
            using var reader = new StreamReader(new MemoryStream(settings));
            return new DeployZip(name, DeployPlan.Sha256Hex(bytes), solution, settingsName, DeployPlan.Sha256Hex(settings), reader.ReadToEnd());
        }
        catch (InvalidDataException ex)
        {
            // The loader's message starts with the path as given; only the file name is printed.
            error.WriteLine($"error: {Clean(ex.Message.Replace(path, name, StringComparison.Ordinal))}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: {Clean(name)}: the file could not be read");
        }

        return null;
    }
}