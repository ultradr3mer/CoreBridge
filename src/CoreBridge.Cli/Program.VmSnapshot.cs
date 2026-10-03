using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>
  /// Klont eine gestoppte VM (<c>multipass clone</c>); der Klon bringt alles mit,
  /// was cloud-init in der Quelle installiert hat. Ein vorhandener Klon-Name wird
  /// wie bei vm-create nur nach Rückfrage ersetzt.
  /// </summary>
  private static async Task RunVmClone(CommandContext context, VmCloneOptions options)
  {
    var source = options.Source;
    var destination = string.IsNullOrWhiteSpace(options.Name) ? null : options.Name.Trim();
    if (destination is not null)
    {
      RequireValidName(destination);
      if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
      {
        throw new ProgramExecutionException(1, $"The clone needs a name other than its source `{source}`.");
      }
    }

    var vm = await RequireVm(context, source);
    await EnsureStopped(context, vm, "cloning");
    if (destination is not null)
    {
      await ReplaceExistingVm(context, destination);
    }

    // Ein Klon kopiert die ganze Disk — dafür ist DefaultTimeout zu knapp; wie
    // vm-create wird in Abschnitten von DefaultTimeoutVmCreation gewartet.
    var label = destination is null ? $"clone {source}" : $"clone {source} → {destination}";
    var waitMs = (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
    MultipassRunResult result;
    try
    {
      result = await RunReported(
          context,
          label,
          () => context.Vms.Clone(source, destination, waitMs, KeepWaiting(context, label)));
    }
    catch (MultipassTimeoutException)
    {
      await context.Events.Emit(context.Events.Std(
          "Stopped waiting — Multipass keeps cloning in the background (check with `cb vm-list`)."));
      return;
    }

    var cloned = destination ?? ParseCloneName(result.StdOut);
    await context.Events.Emit(context.Events.Std(cloned is null
        ? $"{label}: OK ({result.DurationMs / 1000.0:F1} s)"
        : await Completed(context.Vms, cloned, destination is null ? $"{label} → {cloned}" : label, result, context.CancellationToken)));
    await context.Events.Emit(context.Events.Std($"Source `{source}` stays stopped — start it with `cb vm-start -n {source}`."));
  }

  /// <summary>Klon-Name aus „Cloned from &lt;source&gt; to &lt;name&gt;.“; <c>null</c>, wenn die Zeile fehlt.</summary>
  private static string? ParseCloneName(string stdOut)
  {
    var match = System.Text.RegularExpressions.Regex.Match(stdOut, @"\bto\s+""?([A-Za-z][A-Za-z0-9-]*)");
    return match.Success ? match.Groups[1].Value : null;
  }

  /// <summary>Sichert den Stand einer gestoppten VM (<c>multipass snapshot</c>).</summary>
  private static async Task RunVmSnapshot(CommandContext context, VmSnapshotOptions options)
  {
    var name = options.Name;
    var snapshot = string.IsNullOrWhiteSpace(options.Snapshot) ? null : options.Snapshot.Trim();
    if (snapshot is not null)
    {
      RequireValidName(snapshot);
    }

    var vm = await RequireVm(context, name);
    if (snapshot is not null)
    {
      var existing = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
      if (existing.Any(s => string.Equals(s.Name, snapshot, StringComparison.OrdinalIgnoreCase)))
      {
        throw new ProgramExecutionException(
            1,
            $"VM `{name}` already has a snapshot `{snapshot}` — pick another name (existing: {string.Join(", ", existing.Select(s => s.Name))}).");
      }
    }

    await EnsureStopped(context, vm, "the snapshot");

    var label = snapshot is null ? $"snapshot {name}" : $"snapshot {name}.{snapshot}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.Snapshot(name, snapshot, options.Comment, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    await context.Events.Emit(context.Events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
  }

  private static async Task RunVmSnapshots(CommandContext context, VmSnapshotsOptions options)
  {
    var events = context.Events;
    var name = string.IsNullOrWhiteSpace(options.Name) ? null : options.Name.Trim();
    if (name is not null)
    {
      await RequireVm(context, name);
    }

    var snapshots = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
    if (snapshots.Count == 0)
    {
      await events.Emit(events.Std(name is null
          ? "No snapshots — take one with `cb vm-snapshot -n <vm>`."
          : $"VM `{name}` has no snapshots — take one with `cb vm-snapshot -n {name}`."));
      return;
    }

    await events.Emit(events.Std($"{"VM",-30} {"Snapshot",-20} {"Parent",-20} Comment"));
    foreach (var snapshot in snapshots)
    {
      await events.Emit(events.Std($"{snapshot.Instance,-30} {snapshot.Name,-20} {(string.IsNullOrEmpty(snapshot.Parent) ? "--" : snapshot.Parent),-20} {snapshot.Comment}"));
    }
  }

  /// <summary>
  /// Setzt eine gestoppte VM auf einen Snapshot zurück. Der aktuelle Stand wird
  /// vorher als Snapshot gesichert, außer der Benutzer verwirft ihn ausdrücklich —
  /// ohne interaktive Eingabe (Antwort Nein) wird also immer gesichert.
  /// </summary>
  private static async Task RunVmRestore(CommandContext context, VmRestoreOptions options)
  {
    var events = context.Events;
    var (name, snapshot) = (options.Name, options.Snapshot);
    var vm = await RequireVm(context, name);
    var snapshots = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
    if (!snapshots.Any(s => string.Equals(s.Name, snapshot, StringComparison.OrdinalIgnoreCase)))
    {
      throw new ProgramExecutionException(
          1,
          $"VM `{name}` has no snapshot `{snapshot}`. "
          + (snapshots.Count == 0
              ? $"Take one with `cb vm-snapshot -n {name}`."
              : $"Existing snapshots: {string.Join(", ", snapshots.Select(s => s.Name))}."));
    }

    await EnsureStopped(context, vm, "the restore");

    if (!await context.ConfirmAsync($"Discard the current state of `{name}` without a backup snapshot?"))
    {
      var backupLabel = $"snapshot {name} (backup before restore)";
      var backup = await RunReported(
          context,
          backupLabel,
          () => context.Vms.Snapshot(
              name,
              comment: $"Backup before restore to {snapshot}",
              timeoutMs: context.DefaultTimeoutMs,
              keepWaiting: KeepWaiting(context, backupLabel)));
      await events.Emit(events.Std($"{backupLabel}: OK ({backup.DurationMs / 1000.0:F1} s)"));
    }

    var label = $"restore {name}.{snapshot}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.Restore(name, snapshot, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    await events.Emit(events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
  }

  /// <summary>
  /// Vergrößert die Disk einer VM (<c>multipass set local.&lt;vm&gt;.disk</c>). Multipass
  /// ändert nur gestoppte VMs und verkleinert nie — beides wird geprüft, bevor eine
  /// laufende VM (nach Rückfrage) gestoppt wird; sie startet danach wieder, und der
  /// Gast zieht seine Root-Partition beim Boot per cloud-init <c>growpart</c> nach.
  /// </summary>
  private static async Task RunVmResize(CommandContext context, VmResizeOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var size = options.Disk.Trim();
    if (!MultipassSize.TryParse(size, out var requested))
    {
      throw new ProgramExecutionException(
          1,
          $"Invalid disk size `{size}` — use a number followed by K, M or G, e.g. 20G or 0.5G.");
    }

    var vm = await RequireVm(context, name);
    if (vm.IsDeleted)
    {
      throw InTrash(name);
    }

    var current = await Task.Run(() => context.Vms.GetDisk(name, context.DefaultTimeoutMs), context.CancellationToken);
    if (MultipassSize.TryParse(current, out var currentBytes))
    {
      if (requested < currentBytes)
      {
        throw new ProgramExecutionException(
            1,
            $"VM `{name}` has a {current} disk — Multipass can only grow it, not shrink it to {size}.");
      }

      if (requested == currentBytes)
      {
        await events.Emit(events.Std($"resize {name}: OK (disk is already {current}) — State: {vm.State}"));
        return;
      }
    }

    var wasRunning = !vm.IsStopped;
    await EnsureStopped(context, vm, "the resize");

    var label = $"resize {name} disk {current} -> {size}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.SetDisk(name, size, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    var resized = await Task.Run(() => context.Vms.GetDisk(name, context.DefaultTimeoutMs), context.CancellationToken);
    await events.Emit(events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s) — disk now {resized}"));

    if (!wasRunning)
    {
      await events.Emit(events.Std($"VM `{name}` stays stopped — the guest grows its root partition to the new size on the next start (`cb vm-start -n {name}`)."));
      return;
    }

    await StartVm(context, name);
    await events.Emit(events.Std($"cloud-init grows the root partition while booting — check with `cb vm-exec -n {name} -c \"df -h /\"`."));
  }
}
