using System.Text.RegularExpressions;
using CoreBridge.Interaction;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>Das Host-Inventar im Store (<c>&lt;StoreRoot&gt;/inventory.json</c>).</summary>
  private static string InventoryPath => Path.Combine(InitStore.Root, VmInventory.FileName);

  private static VmInventory LoadInventory() => VmInventory.Load(InventoryPath);

  private static void UpdateInventory(Action<VmInventory> change) => VmInventory.Update(InventoryPath, change);

  /// <summary>
  /// Was im laufenden Gast installiert ist: Komponenten mit Skript laut ihrem <c>.ready</c>-Marker,
  /// reine Snap-Komponenten (ohne Skript, also ohne Marker) laut <c>snap list</c>. <c>null</c>,
  /// wenn die Marker nicht lesbar waren.
  /// </summary>
  private static async Task<IReadOnlyList<string>?> InstalledComponents(CommandContext context, string vm)
  {
    var markers = await Task.Run(() => context.Vms.Exec(vm, $"ls {InitInstall.MarkerDirectory} 2>/dev/null || true", context.DefaultTimeoutMs), context.CancellationToken);
    if (markers.ExitCode != 0)
    {
      return null;
    }

    var found = Regex.Matches(markers.StdOut, @"^([A-Za-z0-9_-]+)\.ready\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
    var snapOnly = context.Config.Inits.Components.Where(pair => !pair.Value.HasScript && !string.IsNullOrWhiteSpace(pair.Value.Snap)).ToList();
    if (snapOnly.Count > 0)
    {
      var list = await Task.Run(() => context.Vms.Exec(vm, "snap list 2>/dev/null || true", context.DefaultTimeoutMs), context.CancellationToken);
      var snaps = Lines(list.StdOut).Skip(1).Select(line => line.Split(' ', 2)[0]).ToHashSet(StringComparer.Ordinal);
      found.AddRange(snapOnly.Where(pair => snaps.Contains(pair.Value.Snap!)).Select(pair => pair.Key));
    }

    return found;
  }

  /// <summary>Komponenten der VM laut Inventar (leer, wenn unbekannt).</summary>
  private static IReadOnlyList<string> InventoryComponents(string vm) =>
      LoadInventory().Vms.GetValueOrDefault(vm)?.Components ?? [];

  /// <summary>Snapshot-Kommentar mit dem maschinenlesbaren Teil für <paramref name="vm"/>.</summary>
  private static string SnapshotCommentFor(string vm, string? text)
  {
    var entry = LoadInventory().Vms.GetValueOrDefault(vm);
    return SnapshotComment.Format(text, entry?.Components ?? [], entry?.Profile);
  }

  /// <summary>Hält den Stand der VM als Snapshot im Inventar fest; Name aus „Snapshot taken: vm.name“.</summary>
  private static void RecordSnapshot(string vm, string? snapshot, MultipassRunResult result)
  {
    var name = snapshot ?? Regex.Match(result.StdOut, @"Snapshot taken: [^.\s]+\.(\S+)").Groups[1].Value;
    if (name.Length == 0)
    {
      return;
    }

    UpdateInventory(inventory =>
    {
      var entry = inventory.Vm(vm);
      entry.Snapshots[name] = new InventorySnapshot { Components = [.. entry.Components], Profile = entry.Profile, Taken = DateTime.UtcNow };
    });
  }

  /// <summary>
  /// Zeigt VMs, Snapshots, Komponenten und Ressourcen. Mit <c>--refresh</c> gleicht es mit
  /// Multipass ab: Ressourcen per <c>get</c>, Komponenten laufender VMs aus ihren
  /// <c>.ready</c>-Markern (die Wahrheit; reine Snap-Komponenten ohne Marker per <c>snap list</c>), Snapshots aus <c>list --snapshots</c> samt Kommentar;
  /// VMs, die es nicht mehr gibt, fallen heraus.
  /// </summary>
  private static async Task RunVmInventory(CommandContext context, VmInventoryOptions options)
  {
    var events = context.Events;
    var vms = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
    var snapshots = await Task.Run(() => context.Vms.ListSnapshots(null, context.DefaultTimeoutMs), context.CancellationToken);
    var inventory = LoadInventory();
    var notes = new List<string>();
    if (options.Refresh)
    {
      foreach (var vm in vms.Where(vm => !vm.IsDeleted))
      {
        var entry = inventory.Vm(vm.Name);
        string? Setting(string key)
        {
          try
          {
            return context.Vms.GetSetting(vm.Name, key, context.DefaultTimeoutMs);
          }
          catch (MultipassException)
          {
            return null;
          }
        }

        await Task.Run(() =>
        {
          entry.Cpus = int.TryParse(Setting("cpus"), out var cpus) ? cpus : entry.Cpus;
          entry.Memory = Setting("memory") ?? entry.Memory;
          entry.Disk = Setting("disk") ?? entry.Disk;
        }, context.CancellationToken);

        if (vm.IsRunning)
        {
          if (await InstalledComponents(context, vm.Name) is { } installed)
          {
            entry.Components = VmInventory.Merge([], installed);
            entry.Known = true;
            entry.Verified = DateTime.UtcNow;
          }
        }
        else if (!entry.Known)
        {
          notes.Add($"{vm.Name}: {vm.State} — Marker nicht geprüft (starten und erneut `cb vm-inventory --refresh`).");
        }

        foreach (var snapshot in snapshots.Where(s => string.Equals(s.Instance, vm.Name, StringComparison.OrdinalIgnoreCase)))
        {
          if (!entry.Snapshots.ContainsKey(snapshot.Name) && SnapshotComment.Parse(snapshot.Comment) is { } parsed)
          {
            entry.Snapshots[snapshot.Name] = new InventorySnapshot { Components = parsed.Components, Profile = parsed.Profile };
          }
        }

        var existing = snapshots.Where(s => string.Equals(s.Instance, vm.Name, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in entry.Snapshots.Keys.Where(name => !existing.Contains(name)).ToList())
        {
          entry.Snapshots.Remove(gone);
        }
      }

      var names = vms.Select(vm => vm.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (var gone in inventory.Vms.Keys.Where(name => !names.Contains(name)).ToList())
      {
        inventory.Vms.Remove(gone);
      }

      inventory.Save(InventoryPath);
    }

    var shown = vms.Where(vm => !vm.IsDeleted && (options.Name is null || string.Equals(vm.Name, options.Name, StringComparison.OrdinalIgnoreCase))).ToList();
    if (shown.Count == 0)
    {
      await events.Emit(events.Std(options.Name is null ? "No VMs — create one with `cb vm-create -n <name>`." : $"VM `{options.Name}` does not exist."));
      return;
    }

    await events.Emit(events.Std($"{"VM / Snapshot",-28} {"State",-9} {"Profil",-10} {"CPUs",-5} {"Memory",-8} {"Disk",-9} Komponenten"));
    foreach (var vm in shown)
    {
      var entry = inventory.Vms.GetValueOrDefault(vm.Name);
      var components = entry is null || !entry.Known ? "? (unbekannt — `cb vm-inventory --refresh`)" : Join(entry.Components);
      await events.Emit(events.Std(
          $"{vm.Name,-28} {vm.State,-9} {entry?.Profile ?? "-",-10} {entry?.Cpus?.ToString() ?? "-",-5} {entry?.Memory ?? "-",-8} {entry?.Disk ?? "-",-9} {components}"));
      foreach (var snapshot in snapshots.Where(s => string.Equals(s.Instance, vm.Name, StringComparison.OrdinalIgnoreCase)))
      {
        var recorded = entry?.Snapshots.GetValueOrDefault(snapshot.Name)?.Components ?? SnapshotComment.Parse(snapshot.Comment)?.Components;
        await events.Emit(events.Std($"  └ {snapshot.Name,-24} {string.Empty,-9} {string.Empty,-10} {string.Empty,-5} {string.Empty,-8} {string.Empty,-9} {(recorded is null ? "?" : Join(recorded))}"));
      }
    }

    foreach (var note in notes)
    {
      await events.Emit(events.Std(note));
    }

    if (!options.Refresh)
    {
      await events.Emit(events.Std($"Inventar: {InventoryPath} — mit den Markern im Gast abgleichen: `cb vm-inventory --refresh`."));
    }
  }

  private static string Join(IReadOnlyCollection<string> components) =>
      components.Count == 0 ? "-" : string.Join(",", components);

  /// <summary>Was das Repository braucht, welche VM/welcher Snapshot passt und der Weg dorthin.</summary>
  private sealed record RepoMatchReport(
      string Root,
      string Repo,
      IReadOnlyList<Requirement> Requirements,
      IReadOnlyList<MatchCandidate> Ranked,
      MigrationPlan Plan);

  private static async Task<RepoMatchReport> MatchRepo(CommandContext context, string? path, IEnumerable<string>? extra = null)
  {
    var (root, repo) = await ResolveRepo(context, path);
    var requirements = RepoRequirements.Detect(root, context.Config.Inits).ToList();
    foreach (var component in extra ?? [])
    {
      if (!requirements.Any(r => string.Equals(r.Component, component, StringComparison.OrdinalIgnoreCase)))
      {
        requirements.Add(new Requirement(component, "Agent"));
      }
    }

    var required = requirements.Select(r => r.Component).ToList();
    var vms = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
    var snapshots = await Task.Run(() => context.Vms.ListSnapshots(null, context.DefaultTimeoutMs), context.CancellationToken);
    var ranked = RepoMatcher.Rank(required, LoadInventory(), vms, snapshots);
    var plan = RepoMatcher.Plan(required, ranked, repo, vms.Select(vm => vm.Name).Concat(InitProfileResolver.ExistingProfiles()));
    return new RepoMatchReport(root, repo, requirements, ranked, plan);
  }

  private static async Task RunRepoMatch(CommandContext context, RepoMatchOptions options)
  {
    var report = await MatchRepo(context, options.Repo);
    await EmitMatchReport(context, report);
  }

  private static async Task EmitMatchReport(CommandContext context, RepoMatchReport report)
  {
    var events = context.Events;
    await events.Emit(events.Std($"Repository `{report.Repo}` ({report.Root}) braucht:"));
    foreach (var requirement in report.Requirements)
    {
      await events.Emit(events.Std($"  {requirement.Component,-12} {requirement.Reason}"));
    }

    if (report.Ranked.Count > 0)
    {
      await events.Emit(events.Std(string.Empty));
      await events.Emit(events.Std($"{"Kandidat",-32} {"fehlt",-24} überzählig"));
      foreach (var candidate in report.Ranked.Take(8))
      {
        var missing = candidate.Known ? Join(candidate.Missing) : "? (unbekannt)";
        await events.Emit(events.Std($"{(candidate.Covers ? "✓ " : "  ") + candidate.Label,-32} {missing,-24} {Join(candidate.Surplus)}"));
      }
    }

    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std(report.Plan.Kind switch
    {
      MigrationKind.UseVm => $"Passt: VM `{report.Plan.Vm}`. Weiter:",
      MigrationKind.RestoreSnapshot => $"Passt: Snapshot `{report.Plan.Source!.Label}` (vm-restore sichert den aktuellen Stand vorher). Weiter:",
      MigrationKind.CloneAndInstall => $"Keine VM passt ganz. Migration von `{report.Plan.Source!.Vm}` (fehlt: {Join(report.Plan.Missing)}):",
      _ => "Nichts Brauchbares vorhanden. Neu aus einem Profil:"
    }));
    foreach (var command in report.Plan.Commands())
    {
      await events.Emit(events.Std($"  {command}"));
    }

    if (report.Ranked.Any(c => !c.Known && c.Snapshot is null))
    {
      await events.Emit(events.Std("Hinweis: VMs mit unbekannten Komponenten — `cb vm-inventory --refresh` liest ihre Marker."));
    }
  }
}
