using PowerOps.Core.Drift;
using PowerOps.Core.Settings;

namespace PowerOps.Core.Deploy;

/// <summary>What <c>deploy apply</c> did with one planned solution. <paramref name="PacExitCode"/> is set only when <c>pac</c> failed.</summary>
public sealed record AppliedSolution(int Order, string Solution, string Zip, string Action, string Import, int? PacExitCode);

/// <summary>One planned cloud flow and its state after the imports (null in a dry run or when the second read failed).</summary>
public sealed record AppliedFlow(string Solution, string Name, string ExpectedState, string? StateAfter);

/// <summary>The apply report. Holds no environment id or URL, folder path, connection id, setting value or <c>pac</c> text.</summary>
public sealed record DeployApplyResult(
    string Tool, bool Applied, string Target, string Hash, string Status, string? Message,
    IReadOnlyList<AppliedSolution> Solutions, IReadOnlyList<AppliedFlow> Flows);

public static class ApplyStatus
{
    public const string Refused = "refused";
    public const string Planned = "planned";
    public const string Imported = "imported";
    public const string Failed = "failed";
}

public static class ImportStatus
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string NotRun = "not-run";
    public const string Planned = "planned";
    public const string Skipped = "skipped";
}

/// <summary>One <c>pac solution import</c> to run: the plan position, the zip and settings file names (no paths) and whether it takes <c>--stage-and-upgrade</c>.</summary>
public sealed record ImportStep(int Order, string Solution, string Zip, string? Settings, bool StageAndUpgrade);

/// <summary>A planned flow paired with the id of its cloud flow in the zip.</summary>
public sealed record FlowRef(string Solution, PlannedFlow Flow, Guid Id);

/// <summary>The flows with their states after the imports, and whether every one is in its expected state.</summary>
public sealed record FlowsAfterResult(IReadOnlyList<AppliedFlow> Flows, bool AsExpected);

/// <summary>The decisions of <c>deploy apply</c> that need no I/O.</summary>
public static class DeployApply
{
    /// <summary>The message for plans whose hashes differ although every input is equal.</summary>
    public const string InputsEqualMessage = "the target environment, or the plan file's hash, differs";

    /// <summary>One plain sentence naming the first input that differs, in this order: a zip added, removed or changed; a settings file; the target's state of a solution; a dependency's version. Null when all are equal. Names only, no hash or value.</summary>
    public static string? ChangedInput(PlanInputs reviewed, PlanInputs now)
    {
        var then = reviewed.Zips.ToDictionary(z => z.Zip, StringComparer.Ordinal);
        var current = now.Zips.ToDictionary(z => z.Zip, StringComparer.Ordinal);
        var names = then.Keys.Union(current.Keys).Order(StringComparer.Ordinal).ToList();
        foreach (var n in names)
        {
            if (!then.TryGetValue(n, out var was))
            {
                return $"the zip {n} is not in the reviewed plan";
            }

            if (!current.TryGetValue(n, out var has))
            {
                return $"the zip {n} of the reviewed plan is gone";
            }

            if (was.ZipSha256 != has.ZipSha256)
            {
                return $"the zip {n} changed since the plan";
            }
        }

        foreach (var n in names)
        {
            var (was, has) = (then[n], current[n]);
            if (was.Settings is null != has.Settings is null)
            {
                return $"the settings file of {n} was {(was.Settings is null ? "added" : "removed")} since the plan";
            }

            if (was.Settings != has.Settings || was.SettingsSha256 != has.SettingsSha256)
            {
                return $"the settings file of {n} changed since the plan";
            }
        }

        if (names.FirstOrDefault(n => then[n].SnapshotSha256 != current[n].SnapshotSha256) is { } changed)
        {
            return $"the target's state of the solution in {changed} changed since the plan";
        }

        var oldDeps = reviewed.Dependencies.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var newDeps = now.Dependencies.ToDictionary(d => d.Name, StringComparer.Ordinal);
        foreach (var n in oldDeps.Keys.Union(newDeps.Keys).Order(StringComparer.Ordinal))
        {
            if (!oldDeps.TryGetValue(n, out var was))
            {
                return $"the dependency {n} is not in the reviewed plan";
            }

            if (!newDeps.TryGetValue(n, out var has))
            {
                return $"the dependency {n} of the reviewed plan is no longer read";
            }

            if (was.Version != has.Version)
            {
                return $"the installed version of the dependency {n} changed since the plan";
            }
        }

        return null;
    }

    /// <summary>The imports to run, in plan order: an <c>install</c> or <c>upgrade</c> each, a <c>skip</c> none. Only an upgrade of a managed zip stages and upgrades.</summary>
    /// <exception cref="InvalidDataException">A planned solution's zip is not among <paramref name="zips"/>.</exception>
    public static IReadOnlyList<ImportStep> Imports(DeployPlanResult plan, IReadOnlyList<DeployZip> zips) =>
        plan.Solutions
            .Where(s => s.Action != PlanAction.Skip)
            .Select(s =>
            {
                var zip = ZipOf(s, zips);
                return new ImportStep(s.Order, s.Solution, zip.File, zip.SettingsFile, s.Action == PlanAction.Upgrade && zip.Solution.Managed);
            })
            .ToList();

    /// <summary>Pairs each planned flow with its cloud flow id: the n-th planned flow of a solution is the n-th of <see cref="FlowState.CloudFlows"/> (as <see cref="DeployPlan.Build"/> made them). Never matches by name.</summary>
    /// <exception cref="InvalidDataException">A zip is missing, or holds another number of cloud flows than the plan lists.</exception>
    public static IReadOnlyList<FlowRef> FlowIds(DeployPlanResult plan, IReadOnlyList<DeployZip> zips)
    {
        var refs = new List<FlowRef>();
        foreach (var s in plan.Solutions.Where(s => s.Flows.Count > 0))
        {
            var zip = ZipOf(s, zips);
            var cloud = FlowState.CloudFlows(zip.Solution);
            if (cloud.Count != s.Flows.Count || cloud.Any(f => !Guid.TryParse(f.Id, out _)))
            {
                throw new InvalidDataException($"{zip.File}: the zip's cloud flows are not the plan's");
            }

            refs.AddRange(s.Flows.Select((f, i) => new FlowRef(s.Solution, f, Guid.Parse(cloud[i].Id))));
        }

        return refs;
    }

    /// <summary>Each flow's state after the imports, read by id from the second read's snapshot of its solution (<see cref="FlowState.Missing"/> when absent), and whether all equal their expected state.</summary>
    public static FlowsAfterResult FlowsAfter(IReadOnlyList<FlowRef> flows, IReadOnlyList<Snapshot> after)
    {
        var applied = flows.Select(f =>
        {
            var snap = after.FirstOrDefault(s => string.Equals(s.Solution, f.Solution, StringComparison.OrdinalIgnoreCase));
            var state = snap?.Flows.FirstOrDefault(x => x.Id == f.Id)?.State ?? FlowState.Missing;
            return new AppliedFlow(f.Solution, f.Flow.Name, f.Flow.ExpectedState, state);
        }).ToList();
        return new FlowsAfterResult(applied, applied.All(f => f.StateAfter == f.ExpectedState));
    }

    private static DeployZip ZipOf(PlannedSolution s, IReadOnlyList<DeployZip> zips) =>
        zips.FirstOrDefault(z => z.File == s.Zip) ?? throw new InvalidDataException($"{s.Zip}: the zip is not among the loaded zips");
}