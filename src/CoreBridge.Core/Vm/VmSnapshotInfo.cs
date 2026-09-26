namespace CoreBridge.Core.Vm;

/// <summary>Ein Snapshot laut <c>multipass list --snapshots</c>.</summary>
/// <param name="Instance">Name der VM.</param>
/// <param name="Name">Name des Snapshots (für <c>vm-restore -s</c>).</param>
/// <param name="Parent">Snapshot, auf dem dieser aufsetzt; leer = keiner.</param>
/// <param name="Comment">Freitext aus <c>snapshot --comment</c>.</param>
public sealed record VmSnapshotInfo(string Instance, string Name, string? Parent, string? Comment);
