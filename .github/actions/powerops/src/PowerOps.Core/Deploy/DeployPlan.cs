using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using PowerOps.Core.Drift;
using PowerOps.Core.Model;
using PowerOps.Core.Settings;

namespace PowerOps.Core.Deploy;

/// <summary>What the target read gave: its friendly name, environment id, one snapshot per zip solution (matched by unique name, ignoring case) and the installed version (null when absent) of each other dependency.</summary>
public sealed record DeployTarget(string Name, Guid Id, IReadOnlyList<Snapshot> Solutions, IReadOnlyDictionary<string, string?> Dependencies);

/// <summary>One zip of the folder: its file name (no path), the SHA-256 hex of its bytes, its model and its settings file (name, SHA-256 hex of its bytes, text) or nulls.</summary>
public sealed record DeployZip(string File, string Sha256, Solution Solution, string? SettingsFile, string? SettingsSha256, string? SettingsText);

/// <summary>One zip in the plan file's <c>inputs</c>.</summary>
public sealed record ZipInput(string Zip, string ZipSha256, string? Settings, string? SettingsSha256, string SnapshotSha256);

/// <summary>One dependency in the plan file's <c>inputs</c>: the lowercase unique name and the installed version, null when not installed.</summary>
public sealed record DependencyInput(string Name, string? Version);

/// <summary>What the plan hash covers, in hash order.</summary>
public sealed record PlanInputs(IReadOnlyList<ZipInput> Zips, IReadOnlyList<DependencyInput> Dependencies);

/// <summary>The solution as the target holds it.</summary>
public sealed record InstalledState(string Version, bool Managed, bool LayersRead);

/// <summary>The predicted state of one cloud flow after the import (mapped from <see cref="ApplyFlow"/>, without the after state).</summary>
public sealed record PlannedFlow(string Name, string ExportState, bool Exists, string? StateBefore, string ExpectedState);

public sealed record PlannedSolution(
    int Order, string Solution, string Zip, string? Settings, string Version, bool Managed, string Action,
    InstalledState? Installed, IReadOnlyList<string> DependsOn, IReadOnlyList<PlannedFlow> Flows);

public sealed record PlanItem(string Solution, string Kind, string Severity, string Subject, string Message);

/// <summary>The plan. Holds no environment id or URL; the CLI adds the tool version when it writes JSON.</summary>
public sealed record DeployPlanResult(
    string Target, string Hash, bool Blocked, PlanInputs Inputs, IReadOnlyList<PlannedSolution> Solutions, IReadOnlyList<PlanItem> Items);

public static class PlanAction
{
    public const string Install = "install";
    public const string Upgrade = "upgrade";
    public const string Skip = "skip";
}

public static class PlanSeverity
{
    public const string Block = "block";
    public const string Warning = "warning";
    public const string Info = "info";
}

public static class PlanKind
{
    public const string Cycle = "cycle";
    public const string MissingDependency = "missing-dependency";
    public const string DependencyNotChecked = "dependency-not-checked";
    public const string ManagedConflict = "managed-conflict";
    public const string Settings = "settings";
    public const string Deletes = "deletes";
    public const string UnmanagedLayer = "unmanaged-layer";
    public const string LayersNotRead = "layers-not-read";
    public const string ComponentsNotChecked = "components-not-checked";

    /// <summary>All kinds in report order.</summary>
    public static readonly string[] Order =
    [
        Cycle, MissingDependency, DependencyNotChecked, ManagedConflict, Settings, Deletes, UnmanagedLayer, LayersNotRead, ComponentsNotChecked,
    ];

    internal static string SeverityOf(string kind) => kind switch
    {
        Cycle or MissingDependency or ManagedConflict or Settings => PlanSeverity.Block,
        DependencyNotChecked or Deletes or UnmanagedLayer => PlanSeverity.Warning,
        _ => PlanSeverity.Info,
    };
}

/// <summary>Plans what importing a folder of solution zips into a target would do: order, actions, items and the hash over the inputs.</summary>
public static class DeployPlan
{
    private const int WorkflowType = 29;
    private const int TableType = 1;
    private const int CanvasAppType = 300;

    private sealed record Entry(DeployZip Zip, Snapshot Snap, string Action, (string[] Names, string[] NotChecked) Deps, IReadOnlyList<(Workflow Flow, Guid Id)> Flows);

    /// <summary>"Name (1.0.2.77)" becomes "Name"; null for no value or a blank one.</summary>
    public static string? RequiredSolution(string? attribute)
    {
        var s = attribute?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        var open = s.LastIndexOf(" (", StringComparison.Ordinal);
        return s.EndsWith(')') && open > 0 ? s[..open].TrimEnd() : s;
    }

    /// <summary>The solutions the zips depend on that the target read must look up: valid names that are not a zip's own, distinct ignoring case, sorted.</summary>
    public static IReadOnlyList<string> TargetDependencies(IEnumerable<Solution> zips)
    {
        var all = zips.ToList();
        var own = all.Select(z => z.UniqueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.SelectMany(z => Dependencies(z).Names)
            .Where(n => !own.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The text the plan hash is taken of.</summary>
    /// <exception cref="InvalidDataException">A zip has no snapshot in the target read.</exception>
    public static string HashText(IReadOnlyList<DeployZip> zips, DeployTarget target) => Render(target.Id, Inputs(zips, target));

    /// <exception cref="InvalidDataException">A zip's unique name or version, the target's version of it or a cloud flow id is unusable; the message names the zip file and never a value.</exception>
    public static DeployPlanResult Build(IReadOnlyList<DeployZip> zips, DeployTarget target)
    {
        var entries = zips.OrderBy(z => z.File, StringComparer.Ordinal).Select(z => Prepare(z, target)).ToList();
        var inputs = Inputs(zips, target);
        var ordered = Order(entries);
        var left = ordered.Left.Select(e => e.Zip.Solution.UniqueName).ToList();
        var byName = entries.ToDictionary(e => e.Zip.Solution.UniqueName, StringComparer.OrdinalIgnoreCase);
        var read = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version) in target.Dependencies)
        {
            read.TryAdd(name, version);
        }

        var solutions = new List<PlannedSolution>();
        var items = new List<(int Order, PlanItem Item)>();
        var earlier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordered.All.Count; i++)
        {
            var e = ordered.All[i];
            var (zip, snap) = (e.Zip, e.Snap);
            var name = zip.Solution.UniqueName;

            void Add(string kind, string subject, string message) =>
                items.Add((i, new PlanItem(name, kind, PlanKind.SeverityOf(kind), subject, message)));

            if (ordered.Left.Contains(e))
            {
                Add(PlanKind.Cycle, name, "It is in a dependency cycle among the zips or depends on one; left over: " + string.Join(", ", left) + ".");
            }

            if (snap.Installed && zip.Solution.Managed != snap.Managed)
            {
                Add(PlanKind.ManagedConflict, name, zip.Solution.Managed
                    ? "The zip is managed but the solution is installed unmanaged in the target."
                    : "The zip is unmanaged but the solution is installed managed in the target.");
            }

            var flows = new List<PlannedFlow>();
            if (e.Action != PlanAction.Skip)
            {
                foreach (var dep in e.Deps.Names)
                {
                    var installed = byName.TryGetValue(dep, out var other) ? other.Snap.Installed : read.GetValueOrDefault(dep) is not null;

                    // A zip left over in a cycle is reported by its own `cycle` item.
                    if (!installed && !earlier.Contains(dep) && !(other is not null && ordered.Left.Contains(other)))
                    {
                        Add(PlanKind.MissingDependency, dep, "The solution it depends on is not installed in the target and no earlier zip provides it.");
                    }
                }

                foreach (var subject in e.Deps.NotChecked)
                {
                    Add(PlanKind.DependencyNotChecked, subject, "The required component names no solution that can be checked, so that dependency is not read.");
                }

                AddSettings(e, Add);
                flows.AddRange(e.Flows.Select(f => FlowState.Plan(f.Flow, snap.Flows.FirstOrDefault(x => x.Id == f.Id)?.State ?? FlowState.Missing))
                    .Select(f => new PlannedFlow(f.Name, f.ExportState, f.Exists, f.StateBefore, f.ExpectedState)));
            }

            if (e.Action == PlanAction.Upgrade)
            {
                AddUpgrade(e, Add);
            }

            solutions.Add(new PlannedSolution(
                i + 1, name, zip.File, zip.SettingsFile, zip.Solution.Version, zip.Solution.Managed, e.Action,
                snap.Installed ? new InstalledState(snap.Version!, snap.Managed == true, snap.LayersRead) : null,
                e.Deps.Names.Order(StringComparer.OrdinalIgnoreCase).ToList(), flows));
            earlier.Add(name);
        }

        var sorted = items
            .OrderBy(x => x.Order)
            .ThenBy(x => Array.IndexOf(PlanKind.Order, x.Item.Kind))
            .ThenBy(x => x.Item.Subject, StringComparer.Ordinal)
            .Select(x => x.Item)
            .ToList();
        return new DeployPlanResult(target.Name, Sha256Hex(Encoding.UTF8.GetBytes(Render(target.Id, inputs))), sorted.Any(x => x.Severity == PlanSeverity.Block), inputs, solutions, sorted);
    }

    // Validates the zip and picks its action.
    private static Entry Prepare(DeployZip zip, DeployTarget target)
    {
        var solution = zip.Solution;
        if (!DriftQueries.IsValidName(solution.UniqueName))
        {
            throw Bad(zip, "the unique name may hold only letters, digits and underscores");
        }

        var version = ParseVersion(solution.Version) ?? throw Bad(zip, "the version is not four numeric parts");
        var snap = SnapshotOf(zip, target);
        var action = PlanAction.Install;
        if (snap.Installed)
        {
            var installed = ParseVersion(snap.Version) ?? throw Bad(zip, "the version installed in the target is not four numeric parts");
            action = Compare(version, installed) > 0 ? PlanAction.Upgrade : PlanAction.Skip;
        }

        var flows = FlowState.CloudFlows(solution)
            .Select(f => (Flow: f, Id: Guid.TryParse(f.Id, out var id) ? id : throw Bad(zip, "a cloud flow's id is not a GUID")))
            .ToList();
        return new Entry(zip, snap, action, Dependencies(solution), flows);
    }

    private static void AddSettings(Entry e, Action<string, string, string> add)
    {
        IReadOnlyList<SettingsProblem> problems;
        try
        {
            problems = DeploymentSettings.Validate(e.Zip.Solution, e.Zip.SettingsText ?? "{}");
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{e.Zip.SettingsFile}: {ex.Message}", ex);
        }

        foreach (var p in problems)
        {
            add(PlanKind.Settings, p.Name, $"{p.ComponentType} {p.Kind}: {p.Message}");
        }
    }

    private static void AddUpgrade(Entry e, Action<string, string, string> add)
    {
        var (solution, snap) = (e.Zip.Solution, e.Snap);
        var flowNames = snap.Flows.ToDictionary(f => f.Id, f => f.Name);

        if (snap.Managed == true)
        {
            foreach (var c in snap.Components.Where(c => c.UnmanagedLayer == true))
            {
                add(PlanKind.UnmanagedLayer, flowNames.TryGetValue(c.Id, out var n) ? n : Component(c.Type, c.Id), "An unmanaged layer stays on top of the imported change.");
            }

            if (!snap.LayersRead)
            {
                add(PlanKind.LayersNotRead, solution.UniqueName, "The target's layers were not read, so unmanaged layers are not checked.");
            }
        }

        if (!solution.Managed)
        {
            return;
        }

        var zipFlows = solution.Workflows.Select(w => Guid.TryParse(w.Id, out var id) ? id : (Guid?)null).Where(id => id is not null).ToHashSet();
        foreach (var c in snap.Components.Where(c => c.Type == WorkflowType && !zipFlows.Contains(c.Id)))
        {
            add(PlanKind.Deletes, flowNames.TryGetValue(c.Id, out var n) ? n : Component(c.Type, c.Id), "The upgrade deletes this component because the zip no longer has it.");
        }

        foreach (var v in snap.Variables.Where(v => !solution.EnvironmentVariables.Any(x => Same(x.SchemaName, v.SchemaName))))
        {
            add(PlanKind.Deletes, v.SchemaName, "The upgrade deletes this environment variable because the zip no longer has it.");
        }

        foreach (var r in snap.References.Where(r => !solution.ConnectionReferences.Any(x => Same(x.LogicalName, r.LogicalName))))
        {
            add(PlanKind.Deletes, r.LogicalName, "The upgrade deletes this connection reference because the zip no longer has it.");
        }

        // The zip names tables and canvas apps by schema name and the target by id, so only the counts compare (a lower bound).
        foreach (var (type, noun, effect) in new[]
        {
            (TableType, "table(s)", "a deleted table loses its data"),
            (CanvasAppType, "canvas app(s)", "the managed upgrade removes canvas apps the zip no longer has"),
        })
        {
            var gone = snap.Components.Count(c => c.Type == type) - solution.RootComponents.Count(c => c.Type == type.ToString(CultureInfo.InvariantCulture));
            if (gone > 0)
            {
                add(PlanKind.Deletes, $"{gone} {noun}", $"The upgrade deletes at least {gone} of them; {effect}.");
            }
        }

        var referenceIds = snap.References.Select(r => r.Id).ToHashSet();
        var rest = snap.Components
            .Where(c => c.Type is not (WorkflowType or 380 or 381 or TableType or CanvasAppType) && !referenceIds.Contains(c.Id))
            .ToList();
        if (rest.Count > 0)
        {
            add(PlanKind.ComponentsNotChecked, rest.Count.ToString(CultureInfo.InvariantCulture),
                $"The target solution holds components of type {string.Join(", ", rest.Select(c => c.Type).Distinct().Order())}; the upgrade may delete them and that is not checked.");
        }
    }

    // Kahn's algorithm, always taking the ready zip with the smallest name; zips in or behind a cycle come last in name order.
    private static (List<Entry> All, HashSet<Entry> Left) Order(List<Entry> entries)
    {
        var remaining = entries.ToList();
        var ordered = new List<Entry>();
        var names = entries.Select(e => e.Zip.Solution.UniqueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (remaining.Where(e => Ready(e, remaining)).MinBy(e => e.Zip.Solution.UniqueName, StringComparer.OrdinalIgnoreCase) is { } next)
        {
            ordered.Add(next);
            remaining.Remove(next);
        }

        var left = remaining.OrderBy(e => e.Zip.Solution.UniqueName, StringComparer.OrdinalIgnoreCase).ToList();
        return ([.. ordered, .. left], [.. left]);

        bool Ready(Entry e, List<Entry> pending) =>
            !e.Deps.Names.Any(d => names.Contains(d) && pending.Any(p => p != e && Same(p.Zip.Solution.UniqueName, d)));
    }

    // The solutions a zip depends on (valid names other than its own, distinct) and its required components that cannot be checked.
    private static (string[] Names, string[] NotChecked) Dependencies(Solution solution)
    {
        var names = new List<string>();
        var notChecked = new List<string>();
        foreach (var d in solution.MissingDependencies)
        {
            var name = RequiredSolution(d.Required.Solution);
            if (name is null || !DriftQueries.IsValidName(name))
            {
                notChecked.Add($"{d.Required.Type}:{d.Required.SchemaName ?? d.Required.Id}");
            }
            else if (!Same(name, solution.UniqueName))
            {
                names.Add(name);
            }
        }

        return (names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), notChecked.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static PlanInputs Inputs(IReadOnlyList<DeployZip> zips, DeployTarget target) => new(
        zips.OrderBy(z => z.File, StringComparer.Ordinal)
            .Select(z => new ZipInput(z.File, z.Sha256, z.SettingsFile, z.SettingsSha256, Sha256Hex(Encoding.UTF8.GetBytes(SnapshotFile.Write(SnapshotOf(z, target))))))
            .ToList(),
        target.Dependencies
            .GroupBy(d => d.Key.ToLowerInvariant())
            .Select(g => new DependencyInput(g.Key, g.First().Value))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList());

    private static string Render(Guid targetId, PlanInputs inputs)
    {
        var text = new StringBuilder("powerops deploy plan 1\n").Append("target ").Append(targetId.ToString("D")).Append('\n');
        foreach (var z in inputs.Zips)
        {
            text.Append($"zip {z.Zip} {z.ZipSha256}\n")
                .Append(z.Settings is null ? "settings none\n" : $"settings {z.Settings} {z.SettingsSha256}\n")
                .Append($"snapshot {z.SnapshotSha256}\n");
        }

        foreach (var d in inputs.Dependencies)
        {
            text.Append($"dependency {d.Name} {d.Version ?? "none"}\n");
        }

        return text.ToString();
    }

    private static Snapshot SnapshotOf(DeployZip zip, DeployTarget target) =>
        target.Solutions.FirstOrDefault(s => Same(s.Solution, zip.Solution.UniqueName)) ?? throw Bad(zip, "the target read holds no snapshot of the solution");

    private static InvalidDataException Bad(DeployZip zip, string why) => new($"{zip.File}: {why}");

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Component(int type, Guid id) => $"{type} {id:D}";

    private static long[]? ParseVersion(string? version)
    {
        var parts = version?.Split('.');
        if (parts is not { Length: 4 })
        {
            return null;
        }

        var numbers = new long[4];
        return parts.Select((p, i) => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])).All(ok => ok) ? numbers : null;
    }

    private static int Compare(long[] a, long[] b) => a.Zip(b, (x, y) => x.CompareTo(y)).FirstOrDefault(c => c != 0);
}