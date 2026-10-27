using System.Text.RegularExpressions;

using PowerOps.Core.Flows;
using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

internal static partial class RuleHelpers
{
    /// <summary>The Fix sentence shared by PO006 and PO014.</summary>
    public const string HardCodedUrlFix = "Remove the URL where the platform can use the current environment, otherwise read it from an environment variable whose value is set per environment at deployment.";

    // The SharePoint alternative keeps the site, so two sites on one tenant are two findings.
    [GeneratedRegex(@"https?://(?!globaldisco\.)[a-z0-9-]+\.crm\d*\.dynamics\.com|https?://[a-z0-9-]+\.sharepoint\.com/(?:sites|teams)/[^/""'\s?#]+", RegexOptions.IgnoreCase)]
    public static partial Regex EnvironmentUrl();

    /// <summary>Modern cloud flows (category 5) with clientdata, parsed once per call. Invalid JSON throws InvalidDataException naming the flow file.</summary>
    public static IEnumerable<(Workflow Workflow, FlowDefinition Flow)> CloudFlows(Solution s) =>
        s.Workflows
            .Where(w => w.Category == 5 && w.ClientData is not null)
            .Select(w =>
            {
                try
                {
                    return (w, FlowDefinition.Parse(w.ClientData!));
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"{w.File}: {e.Message}", e);
                }
            });

    /// <summary>True when a MissingDependency names this schema name or id as required: another solution provides it.</summary>
    public static bool IsDeclared(Solution s, string value) =>
        s.MissingDependencies.Any(d =>
            string.Equals(d.Required.SchemaName, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.Required.Id, value, StringComparison.OrdinalIgnoreCase));
}