using System.CommandLine;

using PowerOps.Core.Plans;

namespace PowerOps.Cli;

public static class PlanCommand
{
    private const int UsageError = 2;
    private const int InternalError = 3;

    public static Command Create(TextWriter output, TextWriter error)
    {
        var command = new Command("plan", "Check and sign off planning documents in docs/plans/ (offline).");
        command.Subcommands.Add(Status(output, error));
        command.Subcommands.Add(SignOff(output, error));
        command.Subcommands.Add(Facts(output, error));
        return command;
    }

    private static Command Status(TextWriter output, TextWriter error)
    {
        var path = new Argument<string>("path") { Description = "A repo folder, a plan folder or a plan.md.", DefaultValueFactory = _ => "." };
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var command = new Command("status", "Check every docs/plans/*/plan.md: header, sign-off hash, review and tenant data.") { path, json };
        command.SetAction(parse => Guard(error, () =>
        {
            var root = parse.GetValue(path)!;
            if (!File.Exists(root) && !Directory.Exists(root))
            {
                error.WriteLine($"error: path not found: {root}");
                return UsageError;
            }

            if (NotPlanFile(root))
            {
                error.WriteLine($"error: not a plan file: {root}");
                return UsageError;
            }

            var baseDir = Directory.GetCurrentDirectory();
            var plans = PlanChecker.Discover(root).Select(f => PlanChecker.Check(f, baseDir)).ToList();
            if (parse.GetValue(json))
            {
                PlanStatusReport.WriteJson(plans, output);
            }
            else if (plans.Count == 0)
            {
                output.WriteLine($"no plans found under {root}");
            }
            else
            {
                PlanStatusReport.WriteText(plans, output);
            }

            return plans.Any(p => p.Malformed) ? UsageError : plans.Any(p => p.Violations.Count > 0) ? 1 : 0;
        }));
        return command;
    }

    private static Command SignOff(TextWriter output, TextWriter error)
    {
        var plan = new Argument<string>("plan") { Description = "The plan folder or its plan.md." };
        var command = new Command("sign-off", "Record status, date and body hash in the plan header. Refuses unless status is approved and the review is done or skipped.") { plan };
        command.SetAction(parse => Guard(error, () =>
        {
            if (NotPlanFile(parse.GetValue(plan)!))
            {
                error.WriteLine($"error: not a plan file: {parse.GetValue(plan)}");
                return UsageError;
            }

            var result = PlanSignOff.Run(parse.GetValue(plan)!, Directory.GetCurrentDirectory(), DateOnly.FromDateTime(DateTime.Now));
            result.Out.ToList().ForEach(output.WriteLine);
            result.Err.ToList().ForEach(error.WriteLine);
            return result.ExitCode;
        }));
        return command;
    }

    private static Command Facts(TextWriter output, TextWriter error)
    {
        var command = new Command("facts", "Print the environment name and solution names from pac as JSON (read-only; needs pac signed in).");
        command.SetAction(_ => Guard(error, () =>
        {
            output.Write(PlanFacts.Run(new PacRunner().Run));
            return 0;
        }));
        return command;
    }

    // A file argument must be a plan.md, otherwise its folder would be scanned as a plan folder.
    private static bool NotPlanFile(string path) => File.Exists(path) && Path.GetFileName(path) != "plan.md";

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