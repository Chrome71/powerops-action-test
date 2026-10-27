using System.CommandLine;

using PowerOps.Core.Output;

namespace PowerOps.Cli;

public static class RulesCommand
{
    public static Command Create(TextWriter output)
    {
        var json = new Option<bool>("--json") { Description = "Print the rules as JSON." };
        var command = new Command("rules", "List every analysis rule with its severity, title, fix and docs link.") { json };
        command.SetAction(parse =>
        {
            if (parse.GetValue(json))
            {
                RulesReport.WriteJson(output);
            }
            else
            {
                RulesReport.WriteText(output);
            }

            return 0;
        });
        return command;
    }
}