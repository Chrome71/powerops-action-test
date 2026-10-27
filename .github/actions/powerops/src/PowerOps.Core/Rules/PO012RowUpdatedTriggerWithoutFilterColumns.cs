using PowerOps.Core.Model;

namespace PowerOps.Core.Rules;

public sealed class PO012RowUpdatedTriggerWithoutFilterColumns : IRule
{
    // Dataverse trigger messages that include Update: 3 Update, 4 Create+Update, 6 Update+Delete, 7 all.
    private static readonly int[] UpdateMessages = [3, 4, 6, 7];

    public string Id => "PO012";

    public Severity Severity => Severity.Warning;

    public string Title => "Dataverse row-updated trigger has no filter columns";

    public string Fix => "Set the trigger's filter columns to the columns whose update should start the flow.";

    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        foreach (var (w, flow) in RuleHelpers.CloudFlows(solution))
        {
            foreach (var t in flow.Triggers)
            {
                if (t.OperationId == "SubscribeWebhookTrigger"
                    && t.Message is { } m && UpdateMessages.Contains(m)
                    && string.IsNullOrWhiteSpace(t.FilteringAttributes))
                {
                    yield return new Finding(Id, Severity, $"Flow '{w.Name}' runs on every update of '{t.Entity}'; set filter columns.", w.File, t.Name);
                }
            }
        }
    }
}