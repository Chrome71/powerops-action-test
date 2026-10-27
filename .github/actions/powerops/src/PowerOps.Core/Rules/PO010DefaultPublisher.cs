using System.Text.RegularExpressions;

using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed partial class PO010DefaultPublisher : IRule
{
    public string Id => "PO010";

    public Severity Severity => Severity.Warning;

    public string Title => "Solution uses a default or generated publisher prefix";

    public string Fix => "Create a publisher with a meaningful prefix and build the solution under it.";

    [GeneratedRegex("^cr[0-9a-f]{3,4}$", RegexOptions.IgnoreCase)]
    private static partial Regex Generated();

    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        var p = solution.Publisher;
        if (string.Equals(p.Prefix, "new", StringComparison.OrdinalIgnoreCase)
            || Generated().IsMatch(p.Prefix)
            || p.UniqueName.StartsWith("DefaultPublisher", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Finding(Id, Severity, $"Publisher '{p.UniqueName}' (prefix '{p.Prefix}') is a default or generated publisher.", solution.ManifestFile, p.UniqueName);
        }
    }
}