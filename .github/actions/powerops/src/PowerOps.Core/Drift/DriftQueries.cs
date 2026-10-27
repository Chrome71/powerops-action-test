using System.Text.RegularExpressions;

namespace PowerOps.Core.Drift;

/// <summary>The FetchXML of each Web API read, copied from tests/fixtures/pac/drift/queries (a test compares them).</summary>
public static partial class DriftQueries
{
    private const string SolutionText = """<fetch><entity name="solution"><attribute name="solutionid" /><attribute name="version" /><attribute name="ismanaged" /><filter><condition attribute="uniquename" operator="eq" value="PowerOpsSeed" /></filter></entity></fetch>""";
    private const string ComponentsText = """<fetch><entity name="solutioncomponent"><attribute name="componenttype" /><attribute name="objectid" /><attribute name="rootsolutioncomponentid" /><filter><condition attribute="solutionid" operator="eq" value="{solutionId}" /></filter></entity></fetch>""";
    private const string FlowsText = """<fetch><entity name="workflow"><attribute name="workflowid" /><attribute name="name" /><attribute name="statecode" /><filter><condition attribute="category" operator="eq" value="5" /></filter><link-entity name="solutioncomponent" from="objectid" to="workflowid" link-type="inner"><filter><condition attribute="solutionid" operator="eq" value="{solutionId}" /></filter></link-entity></entity></fetch>""";
    private const string VariablesText = """<fetch><entity name="environmentvariabledefinition"><attribute name="schemaname" /><link-entity name="environmentvariablevalue" from="environmentvariabledefinitionid" to="environmentvariabledefinitionid" link-type="outer" alias="v"><attribute name="value" /></link-entity><link-entity name="solutioncomponent" from="objectid" to="environmentvariabledefinitionid" link-type="inner"><filter><condition attribute="solutionid" operator="eq" value="{solutionId}" /></filter></link-entity></entity></fetch>""";
    private const string ReferencesText = """<fetch><entity name="connectionreference"><attribute name="connectionreferenceid" /><attribute name="connectionreferencelogicalname" /><attribute name="connectorid" /><attribute name="connectionid" /><link-entity name="solutioncomponent" from="objectid" to="connectionreferenceid" link-type="inner"><filter><condition attribute="solutionid" operator="eq" value="{solutionId}" /></filter></link-entity></entity></fetch>""";
    private const string LayersText = """<fetch><entity name="msdyn_componentlayer"><attribute name="msdyn_componentid" /><attribute name="msdyn_solutionname" /><attribute name="msdyn_order" /><filter><condition attribute="msdyn_componentid" operator="eq" value="{componentIds}" /><condition attribute="msdyn_solutioncomponentname" operator="eq" value="{componentName}" /></filter></entity></fetch>""";

    /// <summary>The layer table's component name for connection references, whatever their <c>componenttype</c> number.</summary>
    public const string ReferenceLayerName = "connectionreference";

    // From the Step 1 capture (2026-10-22): the names the layer table accepts, case-sensitive. A component of any other type is not read.
    private static readonly Dictionary<int, string> LayerNames = new()
    {
        [1] = "Entity",
        [29] = "Workflow",
        [300] = "CanvasApp",
        [380] = "EnvironmentVariableDefinition",
    };

    [GeneratedRegex(@"\A[A-Za-z0-9_]+\z")]
    private static partial Regex NamePattern();

    /// <summary>True for a solution unique name: letters, digits and underscores only (no trailing line break).</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <exception cref="ArgumentException"><paramref name="name"/> fails <see cref="IsValidName"/>.</exception>
    public static string Solution(string name) =>
        IsValidName(name) ? SolutionText.Replace("PowerOpsSeed", name) : throw new ArgumentException("Not a solution unique name.", nameof(name));

    public static string Components(Guid solutionId) => Fill(ComponentsText, solutionId);

    public static string Flows(Guid solutionId) => Fill(FlowsText, solutionId);

    public static string Variables(Guid solutionId) => Fill(VariablesText, solutionId);

    public static string References(Guid solutionId) => Fill(ReferencesText, solutionId);

    /// <summary>Read 7, for one component: the layer table accepts one id and its component name per query (the batched and the nameless queries return no rows).</summary>
    /// <exception cref="ArgumentException"><paramref name="componentName"/> fails <see cref="IsValidName"/>.</exception>
    public static string Layers(Guid componentId, string componentName) =>
        IsValidName(componentName)
            ? LayersText.Replace("{componentIds}", componentId.ToString("D")).Replace("{componentName}", componentName)
            : throw new ArgumentException("Not a component name.", nameof(componentName));

    /// <summary>The layer table's component name for a <c>componenttype</c> number, or null when the type is not read.</summary>
    public static string? LayerName(int type) => LayerNames.GetValueOrDefault(type);

    private static string Fill(string text, Guid id) => text.Replace("{solutionId}", id.ToString("D"));
}