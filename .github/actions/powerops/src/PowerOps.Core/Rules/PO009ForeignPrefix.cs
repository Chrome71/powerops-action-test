using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO009ForeignPrefix : IRule
{
    public string Id => "PO009";

    public Severity Severity => Severity.Warning;

    public string Title => "Component name does not use the publisher prefix";

    public string Fix => "Recreate the component under the solution's publisher, or keep it in a solution of the publisher that owns it and add that solution as a dependency.";

    // Checks only env vars, connection references and entity root components (type 1); other types are not always named by the publisher.
    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        var names = solution.EnvironmentVariables.Select(v => (Name: v.SchemaName, v.File))
            .Concat(solution.ConnectionReferences.Select(c => (Name: c.LogicalName, c.File)))
            .Concat(solution.RootComponents.Where(r => r.Type == "1" && r.SchemaName is not null).Select(r => (Name: r.SchemaName!, File: solution.ManifestFile)));

        foreach (var (name, file) in names)
        {
            var i = name.IndexOf('_');
            if (i > 0 && !string.Equals(name[..i], solution.Publisher.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return new Finding(Id, Severity, $"Component '{name}' does not use the publisher prefix '{solution.Publisher.Prefix}'.", file, name);
            }
        }
    }
}