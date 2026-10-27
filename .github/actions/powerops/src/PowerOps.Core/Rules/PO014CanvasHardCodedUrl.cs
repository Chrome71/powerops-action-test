using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO014CanvasHardCodedUrl : IRule
{
    public string Id => "PO014";

    public Severity Severity => Severity.Warning;

    public string Title => "Canvas app formula contains a hard-coded environment or SharePoint site URL";

    public string Fix => RuleHelpers.HardCodedUrlFix;

    public IEnumerable<Finding> Evaluate(Solution solution) =>
        solution.CanvasFormulas
            .Where(f => f.Formula.StartsWith('='))
            .SelectMany(f => RuleHelpers.EnvironmentUrl().Matches(f.Formula).Select(m => (Formula: f, Url: m.Value)))
            // Keyed on the app's folder (canvasapps/<name>), because display names need not be unique.
            .DistinctBy(x => (string.Join('/', x.Formula.File.Split('/').Take(2)), x.Url.ToUpperInvariant()))
            .Select(x => new Finding(Id, Severity, $"Canvas app '{x.Formula.App}' hard-codes the URL '{x.Url}' in {x.Formula.Control}.{x.Formula.Property}.", x.Formula.File, x.Url));
}