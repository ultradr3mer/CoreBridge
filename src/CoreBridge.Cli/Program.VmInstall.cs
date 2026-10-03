using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>
  /// Installiert Komponenten in eine vorhandene VM — dieselben Schritte wie
  /// cloud-init beim Anlegen (<see cref="InitInstall.Plan"/>). Der erste
  /// fehlgeschlagene Schritt beendet den Befehl; die VM läuft dann zur Diagnose
  /// weiter. Nach Erfolg wird ein Snapshot empfohlen.
  /// </summary>
  private static async Task RunVmInstall(CommandContext context, VmInstallOptions options)
  {
    var events = context.Events;
    var name = options.Name;
    var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (components.Length == 0)
    {
      throw new ProgramExecutionException(1, "No components given — e.g. `-c dotnet,python`.");
    }

    if (options.Snapshot is not null)
    {
      RequireValidName(options.Snapshot);
    }

    var steps = InitInstall.Plan(context.Config.Inits, components);
    await EnsureRunning(context, name, ask: false);

    // apt und snap brauchen Minuten: gewartet wird in Abschnitten von
    // DefaultTimeoutVmCreation, danach fragt die CLI „weiter warten?“.
    var waitMs = (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < steps.Count; i++)
    {
      var step = steps[i];
      var label = $"[{i + 1}/{steps.Count}] {step.Title}";
      MultipassRunResult result;
      string? upload = null;
      try
      {
        if (step.IsUpload)
        {
          upload = Path.Combine(Path.GetTempPath(), $"corebridge-{Guid.NewGuid():N}.sh");
          await File.WriteAllTextAsync(upload, InitInstall.ReadScript(step.ScriptSource!), context.CancellationToken);
          result = await RunStep(context, label, () => context.Vms.Put(upload, name, step.RemotePath, waitMs));
        }
        else
        {
          result = await RunStep(context, label, () => context.Vms.Exec(name, step.Command!, waitMs, KeepWaiting(context, label)));
        }
      }
      catch (MultipassTimeoutException)
      {
        throw new ProgramExecutionException(
            1,
            $"Stopped waiting — `{step.Title}` keeps running inside `{name}`. The VM stays running; check with `cb vm-exec -n {name} -c ...`.");
      }
      finally
      {
        if (upload is not null)
        {
          File.Delete(upload);
        }
      }

      if (result.ExitCode != 0)
      {
        var detail = step.Marker is not null
            ? $"the script ran but did not set its marker `{step.Marker}` — the installation counts as incomplete"
            : $"exit {result.ExitCode}";
        throw new ProgramExecutionException(
            result.ExitCode,
            $"vm-install {name}: step `{step.Title}` failed ({detail}). The VM stays running for diagnosis (`cb vm-exec -n {name} -c ...`); no snapshot was taken.");
      }
    }

    await events.Emit(events.Std($"install {string.Join(", ", components)} → {name}: OK ({stopwatch.Elapsed.TotalSeconds:F1} s)"));
    await OfferSnapshotAfterConfiguration(context, name, string.Join("-", components), options.Snapshot);
  }

  /// <summary>
  /// Ein Installationsschritt: ankündigen, mit Spinner ausführen, stdout und
  /// stderr zeigen. Anders als <see cref="RunReported"/> wirft er bei Exit ≠ 0
  /// nicht, damit <see cref="RunVmInstall"/> den Schritt in der Meldung nennen kann.
  /// </summary>
  private static async Task<MultipassRunResult> RunStep(CommandContext context, string label, Func<MultipassRunResult> action)
  {
    var events = context.Events;
    await events.Emit(events.Std($"{label} ..."));
    var result = await WithProgress(context, action);
    foreach (var line in Lines(result.StdOut).Concat(Lines(MultipassStdErr.Clean(result.StdErr))))
    {
      await events.Emit(events.Std($"    {line}"));
    }

    return result;
  }

  /// <summary>
  /// Empfehlung nach jeder Konfiguration: einen Snapshot anlegen, solange der
  /// Stand frisch ist und noch niemand verbunden hat. Mit
  /// <paramref name="explicitName"/> (<c>--snapshot</c>) ohne Rückfrage; sonst wird
  /// gefragt — ohne Konsole gilt Nein. Die VM wird dafür gestoppt und bleibt gestoppt.
  /// </summary>
  private static async Task OfferSnapshotAfterConfiguration(
      CommandContext context,
      string name,
      string suggestedName,
      string? explicitName)
  {
    var events = context.Events;
    if (explicitName is null
        && !await context.ConfirmAsync(
            $"Recommended: take a snapshot of `{name}` now that it is configured — a stable state before anyone connects (the VM gets stopped). Take one now?"))
    {
      await events.Emit(events.Std($"No snapshot — take one later with `cb vm-snapshot -n {name}`."));
      return;
    }

    var existing = await Task.Run(() => context.Vms.ListSnapshots(name, context.DefaultTimeoutMs), context.CancellationToken);
    string? snapshot = explicitName ?? (VmNamePattern.IsMatch(suggestedName) ? suggestedName : null);
    if (snapshot is not null && existing.Any(s => string.Equals(s.Name, snapshot, StringComparison.OrdinalIgnoreCase)))
    {
      if (explicitName is not null)
      {
        throw new ProgramExecutionException(
            1,
            $"VM `{name}` already has a snapshot `{snapshot}` — the installation is done, take a snapshot with another name via `cb vm-snapshot -n {name} -s <name>`.");
      }

      // Vorschlag vergeben: Multipass wählt dann snapshotN.
      snapshot = null;
    }

    var vm = await RequireVm(context, name);
    if (!vm.IsStopped)
    {
      await StopVm(context, vm);
    }

    var label = snapshot is null ? $"snapshot {name}" : $"snapshot {name}.{snapshot}";
    var result = await RunReported(
        context,
        label,
        () => context.Vms.Snapshot(name, snapshot, $"after configuration ({suggestedName})", context.DefaultTimeoutMs, KeepWaiting(context, label)));
    await events.Emit(events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
    await events.Emit(events.Std($"VM `{name}` stays stopped — start it with `cb vm-start -n {name}`."));
  }
}
