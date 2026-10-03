using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

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
    if (cloned is not null)
    {
      // Der Klon bringt mit, was in der Quelle installiert ist — nicht ihre Snapshots.
      UpdateInventory(inventory =>
      {
        var origin = inventory.Vms.GetValueOrDefault(source);
        inventory.Vms[cloned] = new InventoryVm
        {
          Profile = origin?.Profile,
          Components = [.. origin?.Components ?? []],
          Known = origin?.Known ?? false,
          Cpus = origin?.Cpus,
          Memory = origin?.Memory,
          Disk = origin?.Disk,
          Created = DateTime.UtcNow,
          Verified = origin?.Verified
        };
      });
    }

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
        () => context.Vms.Snapshot(name, snapshot, SnapshotCommentFor(name, options.Comment), context.DefaultTimeoutMs, KeepWaiting(context, label)));
    RecordSnapshot(name, snapshot, result);
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
              comment: SnapshotCommentFor(name, $"Backup before restore to {snapshot}"),
              timeoutMs: context.DefaultTimeoutMs,
              keepWaiting: KeepWaiting(context, backupLabel)));
      RecordSnapshot(name, null, backup);
      await events.Emit(events.Std($"{backupLabel}: OK ({backup.DurationMs / 1000.0:F1} s)"));
    }

    var label = $"restore {name}.{snapshot}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.Restore(name, snapshot, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    // Die VM hat jetzt den Stand des Snapshots — laut Inventar oder seinem Kommentar.
    var restored = snapshots.First(s => string.Equals(s.Name, snapshot, StringComparison.OrdinalIgnoreCase));
    UpdateInventory(inventory =>
    {
      var entry = inventory.Vm(name);
      var recorded = entry.Snapshots.GetValueOrDefault(restored.Name);
      var parsed = SnapshotComment.Parse(restored.Comment);
      if (recorded is not null || parsed is not null)
      {
        entry.Components = [.. recorded?.Components ?? parsed!.Value.Components];
        entry.Profile = recorded?.Profile ?? parsed?.Profile ?? entry.Profile;
        entry.Known = true;
      }
      else
      {
        entry.Known = false;
      }
    });
    await events.Emit(events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
  }

  /// <summary>
  /// Ändert CPUs, Arbeitsspeicher und/oder Disk einer VM (<c>multipass set
  /// local.&lt;vm&gt;.cpus|memory|disk</c>). Multipass ändert nur gestoppte VMs und
  /// verkleinert die Disk nie — alle Angaben werden geprüft, bevor eine laufende VM
  /// (nach Rückfrage) gestoppt wird; sie startet danach wieder, und der Gast zieht
  /// seine Root-Partition beim Boot per cloud-init <c>growpart</c> nach.
  /// </summary>
  private static async Task RunVmResize(CommandContext context, VmResizeOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var requested = ParseResources(options.Cpus, options.Memory, options.Disk);
    if (requested.IsEmpty)
    {
      throw new ProgramExecutionException(1, $"Nothing to change — give --cpus, --memory and/or --disk, e.g. `cb vm-resize -n {name} --cpus 2 --memory 4G`.");
    }

    var vm = await RequireVm(context, name);
    if (vm.IsDeleted)
    {
      throw InTrash(name);
    }

    string Get(string key) => context.Vms.GetSetting(name, key, context.DefaultTimeoutMs);
    var changes = new List<(string Key, string From, string To)>();
    if (requested.Cpus is { } cpus)
    {
      var current = await Task.Run(() => Get("cpus"), context.CancellationToken);
      if (current != cpus.ToString(System.Globalization.CultureInfo.InvariantCulture))
      {
        changes.Add(("cpus", current, cpus.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      }
    }

    if (requested.Memory is { } memory)
    {
      var current = await Task.Run(() => Get("memory"), context.CancellationToken);
      if (!SameSize(current, memory))
      {
        changes.Add(("memory", current, memory));
      }
    }

    if (requested.Disk is { } size)
    {
      var current = await Task.Run(() => Get("disk"), context.CancellationToken);
      if (MultipassSize.TryParse(current, out var currentBytes) && MultipassSize.TryParse(size, out var requestedBytes) && requestedBytes < currentBytes)
      {
        throw new ProgramExecutionException(
            1,
            $"VM `{name}` has a {current} disk — Multipass can only grow it, not shrink it to {size}.");
      }

      if (!SameSize(current, size))
      {
        changes.Add(("disk", current, size));
      }
    }

    if (changes.Count == 0)
    {
      await events.Emit(events.Std($"resize {name}: OK (nothing to change: {requested}) — State: {vm.State}"));
      return;
    }

    var wasRunning = !vm.IsStopped;
    await EnsureStopped(context, vm, "the resize");

    foreach (var (key, from, to) in changes)
    {
      var label = $"resize {name} {key} {from} -> {to}";
      var result = await RunReported(
          context,
          label,
          () => context.Vms.SetSetting(name, key, to, context.DefaultTimeoutMs, KeepWaiting(context, label)));
      var now = await Task.Run(() => Get(key), context.CancellationToken);
      await events.Emit(events.Std($"{label}: OK ({result.DurationMs / 1000.0:F1} s) — {key} now {now}"));
      UpdateInventory(inventory =>
      {
        var entry = inventory.Vm(name);
        switch (key)
        {
          case "cpus":
            entry.Cpus = int.TryParse(now, out var cpus) ? cpus : entry.Cpus;
            break;
          case "memory":
            entry.Memory = now;
            break;
          default:
            entry.Disk = now;
            break;
        }
      });
    }

    var diskChanged = changes.Any(change => change.Key == "disk");
    if (!wasRunning)
    {
      await events.Emit(events.Std(diskChanged
          ? $"VM `{name}` stays stopped — the guest grows its root partition to the new size on the next start (`cb vm-start -n {name}`)."
          : $"VM `{name}` stays stopped — the new values apply on the next start (`cb vm-start -n {name}`)."));
      return;
    }

    await StartVm(context, name);
    if (diskChanged)
    {
      await events.Emit(events.Std($"cloud-init grows the root partition while booting — check with `cb vm-exec -n {name} -c \"df -h /\"`."));
    }
  }

  /// <summary>Gleiche Größe, auch wenn Multipass sie anders schreibt (<c>4G</c> = <c>4.0GiB</c>).</summary>
  private static bool SameSize(string current, string requested) =>
      MultipassSize.TryParse(current, out var a) && MultipassSize.TryParse(requested, out var b) && a == b;
}
