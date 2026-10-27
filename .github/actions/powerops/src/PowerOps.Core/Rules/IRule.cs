using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public interface IRule
{
    string Id { get; }

    Severity Severity { get; }

    string Title { get; }

    /// <summary>One imperative sentence on how to fix a finding: plain text, no Markdown, no URLs.</summary>
    string Fix { get; }

    IEnumerable<Finding> Evaluate(Solution solution);
}