using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

using PowerOps.Core.Model;

namespace PowerOps.Core.Diff;

public enum ChangeKind { Added, Removed, Changed }

/// <summary>One difference. <paramref name="Key"/> is the identity used to match components (never a file path); <paramref name="Name"/> is a display name when the key is an id.</summary>
public sealed record Change(ChangeKind Kind, string Component, string Key, string? Name, string? Detail, IReadOnlyList<string> Risks);

public sealed record DiffResult(Solution Old, Solution New, IReadOnlyList<Change> Changes);

/// <summary>Stable ids of the risk labels on a <see cref="Change"/>.</summary>
public static class Risk
{
    public const string RemovedComponent = "removed-component";
    public const string FlowState = "flow-state";
    public const string ConnectionReference = "connection-reference";
    public const string EnvironmentVariable = "environment-variable";

    public static string Describe(string id) => id switch
    {
        RemovedComponent => "A managed upgrade deletes components that are no longer in the new version.",
        FlowState => "A flow was turned on or off.",
        ConnectionReference => "A connection reference was added or removed.",
        EnvironmentVariable => "An environment variable was added or removed.",
        _ => id,
    };
}

/// <summary>Compares two <see cref="Solution"/> models. Every component kind is one block in <see cref="Compare"/>; to diff a new kind, add a block there.</summary>
public static class SolutionDiff
{
    private const string Arrow = " → ";

    public static DiffResult Compare(Solution old, Solution @new)
    {
        var changes = new List<Change>();

        changes.AddRange(Fields("solution", "uniqueName", old.UniqueName, @new.UniqueName));
        changes.AddRange(Fields("solution", "version", old.Version, @new.Version));
        changes.AddRange(Fields("solution", "managed", old.Managed ? "managed" : "unmanaged", @new.Managed ? "managed" : "unmanaged"));
        changes.AddRange(Fields("solution", "publisher", Pub(old.Publisher), Pub(@new.Publisher)));

        changes.AddRange(Keyed("rootComponent", old.RootComponents, @new.RootComponents,
            r => $"{r.Type}:{r.SchemaName ?? r.Id}", _ => null, null, (_, _) => null));

        changes.AddRange(Keyed("missingDependency", old.MissingDependencies, @new.MissingDependencies,
            d => $"{Node(d.Required)} required by {Node(d.Dependent)}", _ => null, null, (_, _) => null,
            removedDeletes: false)); // a missing dependency going away is an improvement, not a deletion

        changes.AddRange(Keyed("connectionReference", old.ConnectionReferences, @new.ConnectionReferences,
            c => c.LogicalName, _ => null, Risk.ConnectionReference,
            (a, b) => Differences(("connectorId", a.ConnectorId, b.ConnectorId))));

        changes.AddRange(Keyed("environmentVariable", old.EnvironmentVariables, @new.EnvironmentVariables,
            v => v.SchemaName, _ => null, Risk.EnvironmentVariable,
            (a, b) => Join(
                Differences(
                    ("type", a.Type, b.Type),
                    ("required", a.IsRequired.ToString().ToLowerInvariant(), b.IsRequired.ToString().ToLowerInvariant()),
                    ("defaultValue", a.DefaultValue, b.DefaultValue)),
                // Current values are environment-specific and may be sensitive, so they are never printed.
                a.CurrentValue == b.CurrentValue ? null : a.CurrentValue is null ? "currentValue added" : b.CurrentValue is null ? "currentValue removed" : "currentValue changed")));

        changes.AddRange(Keyed("workflow", old.Workflows, @new.Workflows,
            w => w.Id.Length > 0 ? w.Id : w.Name, w => w.Name, null,
            (a, b) => Join(
                Differences(("name", a.Name, b.Name), ("category", a.Category.ToString(), b.Category.ToString()), ("state", State(a.StateCode), State(b.StateCode))),
                SameJson(a.ClientData, b.ClientData) ? null : "definition changed"),
            (a, b) => a.StateCode != b.StateCode && (a.Category == 5 || b.Category == 5) ? Risk.FlowState : null));

        // Canvas apps have no model field for a name, so the name is read from the meta XML. An edit changes the .msapp and
        // AppVersion but not necessarily anything else in the meta, so AppVersion is reported (as a republish), not ignored.
        changes.AddRange(Keyed("canvasApp", old.CanvasAppTexts.Select(CanvasApp), @new.CanvasAppTexts.Select(CanvasApp),
            a => a.Name, _ => null, null,
            (a, b) => Join(
                a.Text == b.Text ? null : "definition changed",
                a.Version == b.Version ? null : $"republished (AppVersion {Show(a.Version)}{Arrow}{Show(b.Version)})")));

        return new DiffResult(old, @new, changes);
    }

    private static string Pub(Publisher p) => $"{p.UniqueName} ({p.Prefix})";

    private static string Node(DependencyNode n) => $"{n.Type}:{n.SchemaName ?? n.Id}";

    private static string State(int? code) => code switch { 1 => "on", 0 => "off", null => "unknown", _ => code.ToString()! };

    private static IEnumerable<Change> Fields(string component, string key, string? old, string? @new) =>
        old == @new ? [] : [new Change(ChangeKind.Changed, component, key, null, Show(old) + Arrow + Show(@new), [])];

    /// <summary>"field: old → new" for each field that differs, joined with "; "; null when none differ.</summary>
    private static string? Differences(params (string Field, string? Old, string? New)[] fields)
    {
        var parts = fields.Where(f => f.Old != f.New).Select(f => $"{f.Field}: {Show(f.Old)}{Arrow}{Show(f.New)}").ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string Show(string? value) =>
        value is null ? "(none)" : Truncate(value, 100);

    /// <summary>Cuts to <paramref name="max"/> chars plus "…" without splitting a surrogate pair.</summary>
    internal static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)] + "…";
    }

    private static IEnumerable<Change> Keyed<T>(
        string component,
        IEnumerable<T> before,
        IEnumerable<T> after,
        Func<T, string> key,
        Func<T, string?> name,
        string? addRemoveRisk,
        Func<T, T, string?> compare,
        Func<T, T, string?>? changeRisk = null,
        bool removedDeletes = true)
    {
        // Identity is the key only. Duplicates keep the first; keys are matched case-insensitively.
        var a = before.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var b = after.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var addRemove = addRemoveRisk is null ? [] : new[] { addRemoveRisk };

        foreach (var k in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var inOld = a.TryGetValue(k, out var x);
            var inNew = b.TryGetValue(k, out var y);
            if (!inNew)
            {
                yield return new Change(ChangeKind.Removed, component, key(x!), name(x!), null, removedDeletes ? [Risk.RemovedComponent, .. addRemove] : addRemove);
            }
            else if (!inOld)
            {
                yield return new Change(ChangeKind.Added, component, key(y!), name(y!), null, addRemove);
            }
            else if (compare(x!, y!) is { } detail)
            {
                yield return new Change(ChangeKind.Changed, component, key(y!), name(y!), detail, changeRisk?.Invoke(x!, y!) is { } risk ? [risk] : []);
            }
        }
    }

    private static string? Join(params string?[] parts) => parts.Where(p => p is not null).ToList() is { Count: > 0 } l ? string.Join("; ", l) : null;

    private static bool SameJson(string? a, string? b)
    {
        if (a == b)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        try
        {
            // DeepEquals ignores property order and whitespace; array order still counts. operationMetadataId is a designer-generated
            // GUID that can change on a re-save without a real edit, so it is dropped first (the analyzer's flow parser skips it too).
            return JsonNode.DeepEquals(WithoutOperationMetadataIds(JsonNode.Parse(a)), WithoutOperationMetadataIds(JsonNode.Parse(b)));
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return false; // not JSON (or duplicate keys): the raw text differs
        }
    }

    private static JsonNode? WithoutOperationMetadataIds(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove("operationMetadataId");
                foreach (var child in o.Select(p => p.Value).ToList())
                {
                    WithoutOperationMetadataIds(child);
                }

                break;
            case JsonArray arr:
                foreach (var child in arr.ToList())
                {
                    WithoutOperationMetadataIds(child);
                }

                break;
        }

        return node;
    }

    /// <summary>The app name, the AppVersion timestamp, and the meta XML without it (parsed, so insignificant whitespace does not count).</summary>
    private static (string Name, string? Version, string Text) CanvasApp(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
            var root = XDocument.Load(reader).Root!;
            var name = root.Element("Name")?.Value.Trim() ?? "";
            var version = root.Element("AppVersion")?.Value.Trim();
            root.Element("AppVersion")?.Remove();
            // Where xmlns:xsi is declared differs between a meta file and customizations.xml; the prefixes are regenerated the same way on both sides.
            root.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
            return (name.Length > 0 ? name : "(unnamed)", version, root.ToString(SaveOptions.DisableFormatting));
        }
        catch (XmlException)
        {
            return ("(unnamed)", null, text);
        }
    }
}