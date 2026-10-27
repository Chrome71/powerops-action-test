using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO001UndefinedConnectionReference : IRule
{
    public string Id => "PO001";

    public Severity Severity => Severity.Error;

    public string Title => "Flow uses a connection reference that is not defined";

    public string Fix => "Add the connection reference to the solution, or add the solution that owns it as a dependency.";

    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        var defined = solution.ConnectionReferences.Select(c => c.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (w, flow) in RuleHelpers.CloudFlows(solution))
        {
            foreach (var name in flow.ConnectionReferences.Select(c => c.LogicalName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!defined.Contains(name!) && !RuleHelpers.IsDeclared(solution, name!))
                {
                    yield return new Finding(Id, Severity, $"Connection reference '{name}' is not defined in the solution or its dependencies.", w.File, name);
                }
            }
        }
    }
}