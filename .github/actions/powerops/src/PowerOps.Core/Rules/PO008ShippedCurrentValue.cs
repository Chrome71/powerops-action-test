using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO008ShippedCurrentValue : IRule
{
    public string Id => "PO008";

    public Severity Severity => Severity.Warning;

    public string Title => "Environment variable ships a current value";

    public string Fix => "Remove the current value before export, and supply per-environment values with deployment settings.";

    public IEnumerable<Finding> Evaluate(Solution solution) =>
        solution.EnvironmentVariables
            .Where(v => !string.IsNullOrEmpty(v.CurrentValue))
            .Select(v => new Finding(Id, Severity, $"Environment variable '{v.SchemaName}' ships a current value in the solution.", v.ValuesFile ?? v.File, v.SchemaName));
}