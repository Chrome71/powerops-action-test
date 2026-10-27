using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO011FlowOff : IRule
{
    public string Id => "PO011";

    public Severity Severity => Severity.Warning;

    public string Title => "Cloud flow is turned off";

    public string Fix => "Turn the flow on before export, or remove it from the solution if it is no longer needed.";

    public IEnumerable<Finding> Evaluate(Solution solution) =>
        solution.Workflows
            .Where(w => w.Category == 5 && w.StateCode == 0)
            .Select(w => new Finding(Id, Severity, $"Flow '{w.Name}' is turned off.", w.File, w.Name));
}