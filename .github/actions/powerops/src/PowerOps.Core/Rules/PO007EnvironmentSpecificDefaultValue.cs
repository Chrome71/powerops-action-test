using System.Text.RegularExpressions;

using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed partial class PO007EnvironmentSpecificDefaultValue : IRule
{
    public string Id => "PO007";

    public Severity Severity => Severity.Warning;

    public string Title => "Environment variable default value looks environment-specific";

    public string Fix => "Leave the default empty and set the value per environment at deployment time, or use a default that is valid everywhere.";

    [GeneratedRegex(@"https?://[^\s/]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();

    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        foreach (var v in solution.EnvironmentVariables)
        {
            var value = v.DefaultValue?.Trim();
            var kind = string.IsNullOrEmpty(value) ? null
                : Url().IsMatch(value) ? "a URL"
                : Guid().IsMatch(value) ? "a GUID"
                : Email().IsMatch(value) ? "an email address"
                : null;
            if (kind is not null)
            {
                yield return new Finding(Id, Severity, $"Environment variable '{v.SchemaName}' has a default value that looks like {kind}.", v.File, v.SchemaName);
            }
        }
    }
}