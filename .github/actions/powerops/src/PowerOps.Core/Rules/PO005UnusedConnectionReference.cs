using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO005UnusedConnectionReference : IRule
{
    public string Id => "PO005";

    public Severity Severity => Severity.Warning;

    public string Title => "Connection reference is not used by any flow or app";

    public string Fix => "Remove the connection reference from the solution, or use it in the flow or app that needs it.";

    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        var used = RuleHelpers.CloudFlows(solution)
            .SelectMany(f => f.Flow.ConnectionReferences.Select(c => c.LogicalName))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var c in solution.ConnectionReferences)
        {
            // shortcut: substring match on raw canvas app metadata, by logical name or by connector api id
            // (apps list connectors, not connection references); prefers a missed warning to a false one. Upgrade when canvas apps are parsed.
            var apiId = c.ConnectorId?[(c.ConnectorId.LastIndexOf('/') + 1)..];
            if (!used.Contains(c.LogicalName)
                && !solution.CanvasAppTexts.Any(t => t.Contains(c.LogicalName, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(apiId) && t.Contains(apiId, StringComparison.OrdinalIgnoreCase))))
            {
                yield return new Finding(Id, Severity, $"Connection reference '{c.LogicalName}' is not used by any flow or app.", c.File, c.LogicalName);
            }
        }
    }
}