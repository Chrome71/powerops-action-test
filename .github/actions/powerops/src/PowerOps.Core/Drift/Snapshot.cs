using System.Security.Cryptography;
using System.Text;

namespace PowerOps.Core.Drift;

/// <summary>What <c>powerops drift</c> read of one solution in one environment. Holds no environment id or URL, connection id, value or user name. Lists are sorted by their match key (ordinal).</summary>
/// <param name="Environment">The environment's friendly name.</param>
/// <param name="Solution">The solution's unique name.</param>
/// <param name="Installed">False when the solution is not in the environment; the other fields are then null or empty.</param>
/// <param name="LayersRead">False only when the layer read was dropped for a managed solution; true when layers were read or no read was needed.</param>
public sealed record Snapshot(
    string Environment,
    string Solution,
    bool Installed,
    string? Version,
    bool? Managed,
    bool LayersRead,
    IReadOnlyList<SnapshotComponent> Components,
    IReadOnlyList<SnapshotVariable> Variables,
    IReadOnlyList<SnapshotReference> References,
    IReadOnlyList<SnapshotFlow> Flows)
{
    /// <summary>The SHA-256 of <paramref name="value"/>'s UTF-8 bytes after "\r\n" becomes "\n", in lowercase hex; null for no value or an empty one.</summary>
    public static string? Hash(string? value) =>
        string.IsNullOrEmpty(value) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n"))));
}

/// <summary>A solution component: its <c>componenttype</c> number and <c>objectid</c>. <paramref name="UnmanagedLayer"/> is null when layers were not read or the solution is unmanaged.</summary>
public sealed record SnapshotComponent(int Type, Guid Id, bool? UnmanagedLayer);

/// <summary>An environment variable: its schema name and <see cref="Snapshot.Hash"/> of its current value.</summary>
public sealed record SnapshotVariable(string SchemaName, string? ValueHash);

/// <summary>A connection reference: its <c>connectionreferenceid</c> (a solution component id), logical name, connector and whether a connection is set.</summary>
public sealed record SnapshotReference(Guid Id, string LogicalName, string ConnectorId, bool Bound);

/// <summary>A cloud flow: its workflow id, name and state (<c>on</c>, <c>off</c> or <c>suspended</c>, as in <see cref="Settings.FlowState"/>).</summary>
public sealed record SnapshotFlow(Guid Id, string Name, string State);