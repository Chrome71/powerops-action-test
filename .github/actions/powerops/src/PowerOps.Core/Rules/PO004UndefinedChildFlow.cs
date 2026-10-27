using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO004UndefinedChildFlow : IRule
{
    public string Id => "PO004";

    public Severity Severity => Severity.Error;

    public string Title => "Flow calls a child flow that is not defined";

    public string Fix => "Add the child flow to the solution, or add the solution that contains it as a dependency.";

    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        var ids = solution.Workflows.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (w, flow) in RuleHelpers.CloudFlows(solution))
        {
            foreach (var child in flow.ChildFlowIds)
            {
                if (!ids.Contains(child) && !RuleHelpers.IsDeclared(solution, child))
                {
                    yield return new Finding(Id, Severity, $"Child flow '{child}' is not in the solution or its dependencies.", w.File, child);
                }
            }
        }
    }
}