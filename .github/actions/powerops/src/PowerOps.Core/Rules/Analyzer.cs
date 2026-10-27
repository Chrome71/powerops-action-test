using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public static class Analyzer
{
    public static IReadOnlyList<IRule> Rules { get; } = new IRule[]
    {
        new PO001UndefinedConnectionReference(),
        new PO002RequiredEnvironmentVariableWithoutValue(),
        new PO003DependencyWithoutOwningSolution(),
        new PO004UndefinedChildFlow(),
        new PO005UnusedConnectionReference(),
        new PO006HardCodedEnvironmentUrl(),
        new PO007EnvironmentSpecificDefaultValue(),
        new PO008ShippedCurrentValue(),
        new PO009ForeignPrefix(),
        new PO010DefaultPublisher(),
        new PO011FlowOff(),
        new PO012RowUpdatedTriggerWithoutFilterColumns(),
        new PO013RootComponentWithoutSourceFolder(),
        new PO014CanvasHardCodedUrl(),
    }.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

    public static string HelpUri(string ruleId) => $"{Output.SarifReport.Repo}/blob/main/docs/rules/{ruleId}.md";

    public static IRule? Find(string ruleId) => Rules.FirstOrDefault(r => r.Id == ruleId);

    /// <summary>All findings, sorted by rule id, component, message, file (ordinal). File comes last because it differs between zip and unpacked layouts.</summary>
    public static IReadOnlyList<Finding> Run(Solution solution) =>
        Rules.SelectMany(r => r.Evaluate(solution))
            .OrderBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.Component, StringComparer.Ordinal)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ToList();
}