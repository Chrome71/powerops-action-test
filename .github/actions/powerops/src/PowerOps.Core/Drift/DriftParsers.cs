using System.Text.Json;
using System.Text.RegularExpressions;

using PowerOps.Core.Settings;

namespace PowerOps.Core.Drift;

/// <summary>Parses the Dataverse Web API's OData JSON (<c>{"value":[...]}</c>) of the drift reads 2 to 7. Properties not needed (<c>@odata.*</c>, primary keys, lookups) are ignored. Every rejection is an <see cref="InvalidDataException"/> whose message never quotes the input.</summary>
public static partial class DriftParsers
{
    [GeneratedRegex(@"\Ahttps://[A-Za-z0-9.-]+/?\z", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>The environment URL from <c>pac org who</c>'s <c>Org URL:</c> line, as <c>https://host</c> (use <c>GetLeftPart(UriPartial.Authority)</c> for the text).</summary>
    /// <exception cref="InvalidDataException">Not an absolute https URL of a host with only letters, digits, '-' and '.', with no user info, port, path, query or fragment.</exception>
    public static Uri EnvironmentUrl(string? orgUrl) =>
        orgUrl is not null && UrlPattern().IsMatch(orgUrl) && Uri.TryCreate(orgUrl, UriKind.Absolute, out var uri) && uri.HostNameType == UriHostNameType.Dns
            ? uri
            : throw new InvalidDataException("the environment URL is not an https URL of a host name with no user info, port, path, query or fragment");

    // Documented Dataverse host suffixes, from Learn "Datacenter regions" (new-datacenter-regions) and "Dynamics 365 US Government" (microsoft-dynamics-365-government), verified 2026-10-21.
    private static readonly string[] DataverseSuffixes = [".dynamics.com", ".crm.microsoftdynamics.us", ".crm.appsplatform.us", ".crm.dynamics.cn"];

    /// <summary>Whether the host ends with a documented Dataverse suffix on a label boundary with at least one label before it. The token is sent only to such hosts.</summary>
    public static bool IsDataverseHost(Uri url) =>
        DataverseSuffixes.Any(suffix => url.Host.Length > suffix.Length && url.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read 2: the solution's id, version and managed flag, or null when no row (not installed).</summary>
    public static (Guid Id, string Version, bool Managed)? Solution(string json)
    {
        var rows = Rows(json);
        return rows.Count switch
        {
            0 => null,
            1 => (Id(rows[0], "solutionid"), Text(rows[0], "version"), Flag(rows[0], "ismanaged")),
            _ => throw Bad("more than one solution row"),
        };
    }

    /// <summary>Read 3: the solution's components. <c>rootsolutioncomponentid</c> is ignored.</summary>
    public static IReadOnlyList<(int Type, Guid Id)> Components(string json) =>
        Unique(Rows(json).Select(r => (Type: Int(r, "componenttype"), Id: Id(r, "objectid"))).ToList(), c => $"{c.Type}/{c.Id}", StringComparer.Ordinal);

    /// <summary>Read 4: the solution's cloud flows; <c>statecode</c> 0, 1 and 2 are off, on and suspended.</summary>
    public static IReadOnlyList<SnapshotFlow> Flows(string json) =>
        Unique(Rows(json).Select(r => new SnapshotFlow(Id(r, "workflowid"), Text(r, "name"), Int(r, "statecode") switch
        {
            0 => FlowState.Off,
            1 => FlowState.On,
            2 => FlowState.Suspended,
            _ => throw Bad("a flow has an unknown statecode"),
        })).ToList(), f => f.Id.ToString(), StringComparer.Ordinal);

    /// <summary>Read 5: the solution's environment variables with the hash of the current value (<c>v.value</c>, the outer-linked column).</summary>
    public static IReadOnlyList<SnapshotVariable> Variables(string json) =>
        Unique(Rows(json).Select(r => new SnapshotVariable(Text(r, "schemaname"), Snapshot.Hash(OptionalText(r, "v.value")))).ToList(), v => v.SchemaName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read 6: the solution's connection references. The id is <c>connectionreferenceid</c>; only whether <c>connectionid</c> is set is kept.</summary>
    public static IReadOnlyList<SnapshotReference> References(string json) =>
        Unique(Rows(json).Select(r => new SnapshotReference(Id(r, "connectionreferenceid"), Text(r, "connectionreferencelogicalname"), Text(r, "connectorid"), !string.IsNullOrEmpty(OptionalText(r, "connectionid")))).ToList(),
            r => r.LogicalName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read 7: whether the component has an unmanaged layer (a row whose <c>msdyn_solutionname</c> is "Active"). Every component has at least one layer, so no rows is unreadable. <c>msdyn_changes</c> and <c>msdyn_componentjson</c> hold component data and are never read.</summary>
    public static bool Layers(string json)
    {
        var rows = Rows(json);
        return rows.Count > 0 ? rows.Any(r => OptionalText(r, "msdyn_solutionname") == "Active") : throw Bad("no layer rows");
    }

    /// <summary>The <c>error.message</c> of the documented error body <c>{"error":{"code","message"}}</c>, or null for anything else. Never throws.</summary>
    public static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static InvalidDataException Bad(string why) => new($"unexpected Web API output: {why}");

    // The rows of one read. The elements are cloned so they outlive the document.
    private static List<JsonElement> Rows(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                throw Bad("not an object with a value array");
            }

            if (root.TryGetProperty("@Microsoft.Dynamics.CRM.morerecords", out var more) && more.ValueKind == JsonValueKind.True)
            {
                throw Bad("more than one page of results is not supported");
            }

            return value.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Object ? r.Clone() : throw Bad("a row is not an object")).ToList();
        }
        catch (JsonException)
        {
            throw Bad("not JSON");
        }
    }

    private static List<T> Unique<T>(List<T> items, Func<T, string> key, StringComparer comparer) =>
        items.Select(key).All(new HashSet<string>(comparer).Add) ? items : throw Bad("a read has a duplicate row");

    // A property that is absent or null is null; any other type than the expected one is rejected.
    private static JsonElement? Prop(JsonElement row, string name, JsonValueKind kind) =>
        !row.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null ? null
        : p.ValueKind == kind ? p
        : throw Bad("a property has the wrong type");

    private static string? OptionalText(JsonElement row, string name) => Prop(row, name, JsonValueKind.String)?.GetString();

    private static string Text(JsonElement row, string name) => OptionalText(row, name) ?? throw Bad("a required property is missing");

    private static int Int(JsonElement row, string name) =>
        Prop(row, name, JsonValueKind.Number) is { } n && n.TryGetInt32(out var i) ? i : throw Bad("a required number is missing or not an integer");

    private static bool Flag(JsonElement row, string name) =>
        row.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : throw Bad("a required flag is missing or not a boolean");

    private static Guid Id(JsonElement row, string name) =>
        Guid.TryParse(Text(row, name), out var id) ? id : throw Bad("an id is not a GUID");
}