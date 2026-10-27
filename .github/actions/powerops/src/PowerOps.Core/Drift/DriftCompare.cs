namespace PowerOps.Core.Drift;

/// <summary>One difference or notice found by <see cref="DriftCompare"/>. <paramref name="Position"/> is the environment's 1-based place in the input list.</summary>
public sealed record DriftItem(int Position, string Environment, string Kind, string Subject, bool Drift, string? Expected, string? Actual);

public static class DriftKind
{
    public const string SolutionMissing = "solution-missing";
    public const string Version = "version";
    public const string Managed = "managed";
    public const string ComponentMissing = "component-missing";
    public const string ComponentExtra = "component-extra";
    public const string UnmanagedLayer = "unmanaged-layer";
    public const string LayersNotRead = "layers-not-read";
    public const string VariableMissing = "variable-missing";
    public const string VariableValue = "variable-value";
    public const string ReferenceMissing = "reference-missing";
    public const string ReferenceConnector = "reference-connector";
    public const string ReferenceUnbound = "reference-unbound";
    public const string FlowMissing = "flow-missing";
    public const string FlowState = "flow-state";

    /// <summary>All kinds in report order.</summary>
    public static readonly string[] Order =
    [
        SolutionMissing, Version, Managed, ComponentMissing, ComponentExtra, UnmanagedLayer, LayersNotRead,
        VariableMissing, VariableValue, ReferenceMissing, ReferenceConnector, ReferenceUnbound, FlowMissing, FlowState,
    ];

    /// <summary>Information kinds are shown but are not drift.</summary>
    public static bool IsDrift(string kind) => kind is not (Managed or LayersNotRead);
}

public static class DriftCompare
{
    private const int WorkflowType = 29;

    /// <summary>Compares every snapshot after the first with the first, plus the checks of each single environment.</summary>
    public static IReadOnlyList<DriftItem> Compare(IReadOnlyList<Snapshot> snapshots)
    {
        var items = new List<DriftItem>();
        for (var i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            void Add(string kind, string subject, string? expected = null, string? actual = null) =>
                items.Add(new DriftItem(i + 1, s.Environment, kind, subject, DriftKind.IsDrift(kind), expected, actual));

            if (s.Installed)
            {
                var flowNames = s.Flows.ToDictionary(f => f.Id, f => f.Name);
                foreach (var c in s.Components.Where(c => c.UnmanagedLayer == true && s.Managed == true))
                {
                    Add(DriftKind.UnmanagedLayer, flowNames.TryGetValue(c.Id, out var n) ? n : Component(c.Type, c.Id));
                }

                foreach (var r in s.References.Where(r => !r.Bound))
                {
                    Add(DriftKind.ReferenceUnbound, r.LogicalName);
                }

                if (s.Managed == true && !s.LayersRead)
                {
                    Add(DriftKind.LayersNotRead, s.Solution);
                }
            }

            if (i > 0)
            {
                ComparePair(snapshots[0], s, Add);
            }
        }

        return items
            .OrderBy(x => x.Position)
            .ThenBy(x => Array.IndexOf(DriftKind.Order, x.Kind))
            .ThenBy(x => x.Subject, StringComparer.Ordinal)
            .ToList();
    }

    private static void ComparePair(Snapshot a, Snapshot b, Action<string, string, string?, string?> add)
    {
        if (a.Installed != b.Installed)
        {
            add(DriftKind.SolutionMissing, a.Solution, null, null);
            return;
        }

        if (!a.Installed)
        {
            return;
        }

        if (a.Version != b.Version)
        {
            add(DriftKind.Version, a.Solution, a.Version, b.Version);
        }

        if (a.Managed != b.Managed)
        {
            add(DriftKind.Managed, a.Solution, Managed(a.Managed), Managed(b.Managed));
        }

        var flowIds = a.Flows.Concat(b.Flows).Select(f => f.Id).ToHashSet();
        // Connection references are compared as references; Learn gives them no fixed componenttype, so they are left out by id.
        var referenceIds = a.References.Concat(b.References).Select(r => r.Id).ToHashSet();
        // Step 1 capture (2026-10-22): after an import, tables (1), canvas apps (300) and environment variables (380, 381) get new ids, so a Dev/Test comparison by id would always differ; they are left out (plan Assumption 2's fallback).
        // M12 manual run (2026-10-24): a model-driven app (80), its sitemap (62) and its app settings (10087) get new ids too, so they are left out the same way.
        // shortcut: 10087 was AppSetting in both of that run's environments, but codes above 10000 come from solutioncomponentdefinitions and may differ per org; match on its primaryentityname "appsetting" if one does.
        IEnumerable<(int Type, Guid Id)> Keys(Snapshot s) => s.Components
            .Where(c => !(c.Type == WorkflowType && flowIds.Contains(c.Id)) && !referenceIds.Contains(c.Id) && c.Type is not (1 or 62 or 80 or 300 or 380 or 381 or 10087))
            .Select(c => (c.Type, c.Id));
        var (ka, kb) = (Keys(a).ToHashSet(), Keys(b).ToHashSet());
        foreach (var c in ka.Except(kb))
        {
            add(DriftKind.ComponentMissing, Component(c.Type, c.Id), null, null);
        }

        foreach (var c in kb.Except(ka))
        {
            add(DriftKind.ComponentExtra, Component(c.Type, c.Id), null, null);
        }

        Match(a.Variables, b.Variables, v => v.SchemaName, StringComparer.OrdinalIgnoreCase, DriftKind.VariableMissing,
            (x, y) => x.ValueHash != y.ValueHash ? (DriftKind.VariableValue, null, null) : null, add);
        Match(a.References, b.References, r => r.LogicalName, StringComparer.OrdinalIgnoreCase, DriftKind.ReferenceMissing,
            (x, y) => x.ConnectorId != y.ConnectorId ? (DriftKind.ReferenceConnector, x.ConnectorId, y.ConnectorId) : null, add,
            r => r.LogicalName);
        Match(a.Flows, b.Flows, f => f.Id, EqualityComparer<Guid>.Default, DriftKind.FlowMissing,
            (x, y) => x.State != y.State ? (DriftKind.FlowState, x.State, y.State) : null, add, f => f.Name);
    }

    // Reports a missing item for keys on one side only, and whatever <paramref name="diff"/> says for keys on both.
    private static void Match<T, TKey>(
        IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, TKey> key, IEqualityComparer<TKey> comparer, string missingKind,
        Func<T, T, (string Kind, string? Expected, string? Actual)?> diff, Action<string, string, string?, string?> add, Func<T, string>? name = null)
        where TKey : notnull
    {
        name ??= x => key(x)!.ToString()!;
        var (da, db) = (a.ToDictionary(key, comparer), b.ToDictionary(key, comparer));
        foreach (var x in a)
        {
            if (db.TryGetValue(key(x), out var y))
            {
                if (diff(x, y) is { } d)
                {
                    add(d.Kind, name(x), d.Expected, d.Actual);
                }
            }
            else
            {
                add(missingKind, name(x), null, null);
            }
        }

        foreach (var y in b.Where(y => !da.ContainsKey(key(y))))
        {
            add(missingKind, name(y), null, null);
        }
    }

    private static string Component(int type, Guid id) => $"{type} {id:D}";

    private static string Managed(bool? managed) => managed == true ? "managed" : "unmanaged";
}