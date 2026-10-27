using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO006HardCodedEnvironmentUrl : IRule
{
    public string Id => "PO006";

    public Severity Severity => Severity.Warning;

    public string Title => "Flow contains a hard-coded environment or SharePoint site URL";

    public string Fix => RuleHelpers.HardCodedUrlFix;

    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        foreach (var (w, flow) in RuleHelpers.CloudFlows(solution))
        {
            // Literals under definition.parameters are env-var defaults mirrored into the flow; PO007 owns those.
            var urls = flow.StringLiterals
                .Where(l => !l.JsonPath.StartsWith("$.parameters", StringComparison.Ordinal))
                .SelectMany(l => RuleHelpers.EnvironmentUrl().Matches(l.Value).Select(m => m.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var url in urls)
            {
                yield return new Finding(Id, Severity, $"Flow '{w.Name}' hard-codes the URL '{url}'.", w.File, url);
            }
        }
    }
}