using System.CommandLine;
using System.Text;

using PowerOps.Core.Model;
using PowerOps.Core.Output;
using PowerOps.Core.Readers;
using PowerOps.Core.Settings;

namespace PowerOps.Cli;

public static class SettingsCommand
{
    private const int UsageError = 2;
    private const int InternalError = 3;

    internal const string EnvironmentPlaceholder = "<environment id>";
    internal const string ImportTimedOut = "The import timed out after 60 minutes. It may still be running in the environment: check the solution's import history there before running again.";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(60);
    private static readonly string[] StepNames = ["validate", "target", "plan", "import", "flows"];

    public static Command Create(TextWriter output, TextWriter error, Func<IReadOnlyList<string>, TimeSpan, PacResult> pac)
    {
        var command = new Command("settings", "Generate and check pac deployment settings files, and import a solution with one.");
        command.Subcommands.Add(Generate(output, error));
        command.Subcommands.Add(Validate(output, error));
        command.Subcommands.Add(Apply(output, error, pac));
        return command;
    }

    private static Command Generate(TextWriter output, TextWriter error)
    {
        var solution = new Argument<string>("solution") { Description = "An unpacked solution folder or an exported zip." };
        var outFile = new Option<string?>("--out") { Description = "Write the settings file here instead of stdout." };
        var command = new Command("generate", "Print a deployment settings file with empty values to fill in.") { solution, outFile };
        command.SetAction(parse => Guard(error, () =>
        {
            var path = parse.GetValue(solution)!;
            if (NotFound(path, error))
            {
                return UsageError;
            }

            var json = DeploymentSettings.Generate(SolutionLoader.Load(path));
            if (parse.GetValue(outFile) is { } file)
            {
                File.WriteAllText(file, json, new UTF8Encoding(false));
            }
            else
            {
                output.Write(json);
            }

            return 0;
        }));
        return command;
    }

    private static Command Validate(TextWriter output, TextWriter error)
    {
        var solution = new Argument<string>("solution") { Description = "An unpacked solution folder or an exported zip." };
        var settings = new Option<string>("--settings") { Description = "The deployment settings file to check.", Required = true };
        var format = new Option<string>("--format") { Description = "Report format.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        var command = new Command("validate", "Check a deployment settings file against a solution: missing or extra entries, empty connection ids and values.") { solution, settings, format };
        command.SetAction(parse => Guard(error, () =>
        {
            var path = parse.GetValue(solution)!;
            var file = parse.GetValue(settings)!;
            if (NotFound(path, error))
            {
                return UsageError;
            }

            if (!File.Exists(file))
            {
                error.WriteLine($"error: settings file not found: {file}");
                return UsageError;
            }

            var loaded = SolutionLoader.Load(path);
            IReadOnlyList<SettingsProblem> problems;
            try
            {
                problems = DeploymentSettings.Validate(loaded, File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"error: {file}: {ex.Message}");
                return InternalError;
            }

            if (parse.GetValue(format) == "json")
            {
                SettingsReport.WriteJson(loaded.UniqueName, file, problems, output);
            }
            else
            {
                SettingsReport.WriteText(problems, output);
            }

            return problems.Count > 0 ? 1 : 0;
        }));
        return command;
    }

    private static Command Apply(TextWriter output, TextWriter error, Func<IReadOnlyList<string>, TimeSpan, PacResult> pac)
    {
        var solution = new Argument<string>("solution") { Description = "An exported solution zip." };
        var settings = new Option<string>("--settings") { Description = "The deployment settings file to import with.", Required = true };
        var apply = new Option<bool>("--apply") { Description = "Import the solution. Without it the command is a dry run that writes nothing." };
        var target = new Option<string?>("--target") { Description = "With --apply: the name of the environment the import was approved for; it must equal pac's current environment." };
        var format = new Option<string>("--format") { Description = "Report format.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");
        var command = new Command("apply", "Validate a settings file, then import the solution zip with it through pac into pac's current environment (only with --apply) and report the state of each cloud flow.")
        {
            solution, settings, apply, target, format,
        };
        command.SetAction(parse => Guard(error, () =>
        {
            var path = parse.GetValue(solution)!;
            var file = parse.GetValue(settings)!;
            var applying = parse.GetValue(apply);
            var name = parse.GetValue(target);
            var json = parse.GetValue(format) == "json";
            if (NotFound(path, error))
            {
                return UsageError;
            }

            if (Directory.Exists(path))
            {
                error.WriteLine($"error: settings apply takes an exported solution zip, not a folder: {path}");
                return UsageError;
            }

            if (!File.Exists(file))
            {
                error.WriteLine($"error: settings file not found: {file}");
                return UsageError;
            }

            if (applying && name is null)
            {
                error.WriteLine("error: --apply needs --target <environment name>");
                return UsageError;
            }

            if (!applying && name is not null)
            {
                error.WriteLine("error: --target is only used with --apply");
                return UsageError;
            }

            var loaded = SolutionLoader.Load(path);
            IReadOnlyList<SettingsProblem> problems;
            try
            {
                problems = DeploymentSettings.Validate(loaded, File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"error: {file}: {ex.Message}");
                return InternalError;
            }

            var steps = new List<ApplyStep>();
            var headerPrinted = false;
            string? environment = null;
            IReadOnlyList<ApplyFlow> flows = [];

            // Writes the report: steps not reached are planned after a finished dry run, else skipped.
            int Finish(int code, string status, string? message = null, int? pacExitCode = null)
            {
                steps.Add(new(StepNames[steps.Count], status));
                var rest = code == 0 && !applying ? "planned" : "skipped";
                steps.AddRange(StepNames.Skip(steps.Count).Select(n => new ApplyStep(n, rest)));
                var result = new ApplyResult(applying, environment, loaded.Managed, ImportArguments(EnvironmentPlaceholder, path, file), steps, problems, pacExitCode, message, flows);
                if (json)
                {
                    SettingsApplyReport.WriteJson(result, output);
                }
                else
                {
                    SettingsApplyReport.WriteText(result, output, header: !headerPrinted);
                }

                return code;
            }

            if (problems.Count > 0)
            {
                return Finish(1, "failed");
            }

            steps.Add(new("validate", "ok"));
            var who = pac(["org", "who"], ReadTimeout);
            if (Failed(who, "pac org who", ReadTimeout) is { } whoFailure)
            {
                return Finish(InternalError, "failed", who.Started ? $"{whoFailure} {PlanFacts.SignInFix}" : whoFailure);
            }

            environment = Field(who.Stdout, "Friendly Name:");
            var id = Field(who.Stdout, "Environment ID:");
            if (environment is null || id is null)
            {
                environment = null;
                return Finish(InternalError, "failed", $"`pac org who` printed no environment name or id. {PlanFacts.SignInFix}");
            }

            if (applying && !string.Equals(name, environment, StringComparison.Ordinal))
            {
                return Finish(UsageError, "failed", $"pac's current environment is \"{environment}\", not the --target \"{name}\"; nothing was imported.");
            }

            steps.Add(new("target", "ok"));
            var cloudFlows = FlowState.CloudFlows(loaded);
            var before = new List<ApplyFlow>();
            foreach (var flow in cloudFlows)
            {
                if (ReadFlow(pac, id, flow) is not { } state)
                {
                    return Finish(InternalError, "failed", ReadFailure);
                }

                before.Add(FlowState.Plan(flow, state));
            }

            flows = before;
            if (!applying)
            {
                return Finish(0, "ok");
            }

            steps.Add(new("plan", "ok"));
            if (json)
            {
                error.Write(SettingsApplyReport.Header(environment));
            }
            else
            {
                output.Write(SettingsApplyReport.Header(environment));
                headerPrinted = true;
            }

            output.Flush();
            error.Flush();
            var import = pac(ImportArguments(id, path, file), ImportTimeout);
            if (!import.Started)
            {
                return Finish(InternalError, "failed", "pac was not found on PATH; nothing was imported.");
            }

            if (import.TimedOut)
            {
                return Finish(InternalError, "failed", ImportTimedOut);
            }

            if (import.ExitCode != 0)
            {
                error.Write(import.Stdout);
                error.Write(import.Stderr);
                return Finish(InternalError, "failed", $"The import failed (pac exit code {import.ExitCode}); pac's output is on stderr. There is no rollback: fix the cause and run again.", import.ExitCode);
            }

            steps.Add(new("import", "ok"));
            var after = new List<ApplyFlow>();
            foreach (var (flow, planned) in cloudFlows.Zip(before))
            {
                if (ReadFlow(pac, id, flow) is not { } state)
                {
                    return Finish(InternalError, "failed", "The import succeeded. " + ReadFailure);
                }

                after.Add(planned with { StateAfter = state });
            }

            flows = after;
            return after.All(f => f.StateAfter == FlowState.On) ? Finish(0, "ok") : Finish(1, "failed");
        }));
        return command;
    }

    private const string ReadFailure = "Reading a cloud flow's state with `pac power-automate list-cloud-flows` failed, timed out or printed output powerops cannot read.";

    internal static string[] ImportArguments(string environmentId, string zip, string? settingsFile, bool stageAndUpgrade = false) =>
        ["solution", "import", "--environment", environmentId, "--path", zip, .. settingsFile is null ? (string[])[] : ["--settings-file", settingsFile], .. stageAndUpgrade ? (string[])["--stage-and-upgrade"] : []];

    // The flow's state in the target, or null when pac fails or prints something unreadable.
    private static string? ReadFlow(Func<IReadOnlyList<string>, TimeSpan, PacResult> pac, string environmentId, Workflow flow)
    {
        var id = Guid.Parse(flow.Id).ToString("D");
        var result = pac(["power-automate", "list-cloud-flows", "--environment", environmentId, "--workflow-id", id, "--json"], ReadTimeout);
        if (Failed(result, "pac power-automate list-cloud-flows", ReadTimeout) is not null)
        {
            return null;
        }

        try
        {
            return FlowState.Read(result.Stdout, id);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string? Failed(PacResult result, string command, TimeSpan timeout) =>
        !result.Started ? "pac was not found on PATH. Install pac (dotnet tool install --global Microsoft.PowerApps.CLI.Tool) and add the dotnet tools folder to PATH."
        : result.TimedOut ? $"`{command}` timed out after {timeout.TotalSeconds:0} s."
        : result.ExitCode != 0 ? $"`{command}` failed (exit code {result.ExitCode})."
        : null;

    // The value after a "Label:" line of pac's text output, or null.
    internal static string? Field(string text, string label) =>
        text.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith(label, StringComparison.Ordinal))
            .Select(l => l[label.Length..].Trim())
            .FirstOrDefault(v => v.Length > 0);

    private static bool NotFound(string path, TextWriter error)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }

        error.WriteLine($"error: path not found: {path}");
        return true;
    }

    // Same mapping as AnalyzeCommand.Run: any failure is exit 3 (POWEROPS_DEBUG=1 for the stack trace).
    internal static int Guard(TextWriter error, Func<int> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex)
        {
            error.WriteLine(Environment.GetEnvironmentVariable("POWEROPS_DEBUG") == "1" ? ex.ToString() : $"error: {ex.Message}");
            return InternalError;
        }
    }
}