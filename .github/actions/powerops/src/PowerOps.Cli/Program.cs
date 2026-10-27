using System.CommandLine;
using System.Text;

using PowerOps.Core.Deploy;
using PowerOps.Core.Drift;

namespace PowerOps.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        return Run(args, Console.Out, Console.Error);
    }

    // Output and error writers are injectable so tests can capture them.
    public static int Run(string[] args, TextWriter output, TextWriter error) =>
        Run(args, output, error, (pacArgs, timeout) => new PacRunner(timeout: timeout).Run(pacArgs));

    // The command tree; tests read it too (HandoffDocTests checks the commands a page names).
    internal static RootCommand Root(TextWriter output, TextWriter error, Func<IReadOnlyList<string>, TimeSpan, PacResult> pac, Func<string, string, Snapshot>? driftLive = null,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, DeployTarget>? deployTarget = null)
    {
        // The live reader (az runner, HttpClient) is built only when a run has a live --env.
        var reader = new Lazy<DriftLiveReader>(() => new DriftLiveReader(
            pac,
            (azArgs, timeout) => new PacRunner(DriftLiveReader.AzExecutable(OperatingSystem.IsWindows()), timeout).Run(azArgs),
            DriftLiveReader.Web(DriftLiveReader.Handler())));
        var root = new RootCommand(
            "A free, open-source, cross-platform toolkit that brings software-engineering discipline " +
            "(analysis, diffing, testing, repeatable deployment) to Microsoft Power Platform.");

        root.Subcommands.Add(AnalyzeCommand.Create(output, error));
        root.Subcommands.Add(BaselineCommand.Create(error));
        root.Subcommands.Add(DiffCommand.Create(output, error));
        root.Subcommands.Add(PlanCommand.Create(output, error));
        root.Subcommands.Add(TestCommand.Create(output, error));
        root.Subcommands.Add(SkillsCommand.Create(output, error));
        root.Subcommands.Add(RulesCommand.Create(output));
        root.Subcommands.Add(SettingsCommand.Create(output, error, pac));
        root.Subcommands.Add(DriftCommand.Create(output, error, driftLive ?? ((solution, env) => reader.Value.Read(solution, env))));

        root.Subcommands.Add(DeployCommand.Create(output, error, pac, deployTarget ?? ((target, solutions, dependencies) => reader.Value.ReadTarget(target, solutions, dependencies))));

        return root;
    }

    // The pac runner is injectable so tests of `settings apply` never call a real pac; so are the live reads of `drift` and `deploy plan`.
    public static int Run(string[] args, TextWriter output, TextWriter error, Func<IReadOnlyList<string>, TimeSpan, PacResult> pac, Func<string, string, Snapshot>? driftLive = null,
        Func<string, IReadOnlyList<string>, IReadOnlyList<string>, DeployTarget>? deployTarget = null)
    {
        var parse = Root(output, error, pac, driftLive, deployTarget).Parse(args);
        if (parse.Errors.Count > 0)
        {
            // System.CommandLine returns 1 for parse errors; usage errors are 2 here.
            foreach (var e in parse.Errors)
            {
                error.WriteLine($"error: {e.Message}");
            }

            return 2;
        }

        return parse.Invoke(new InvocationConfiguration { Output = output, Error = error });
    }
}