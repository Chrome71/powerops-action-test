using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO002RequiredEnvironmentVariableWithoutValue : IRule
{
    public string Id => "PO002";

    public Severity Severity => Severity.Error;

    public string Title => "Required environment variable has no default or current value";

    public string Fix => "Give the variable a default value, or supply the value at deployment time and keep it out of the solution.";

    public IEnumerable<Finding> Evaluate(Solution solution) =>
        solution.EnvironmentVariables
            .Where(v => v.IsRequired && string.IsNullOrEmpty(v.DefaultValue) && string.IsNullOrEmpty(v.CurrentValue))
            .Select(v => new Finding(Id, Severity, $"Environment variable '{v.SchemaName}' is required but has no default or current value.", v.File, v.SchemaName));
}