using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

public static partial class Program
{
  private static async Task RunVmList(CommandContext context)
  {
    var events = context.Events;
    var list = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
    if (list.Count == 0)
    {
      await events.Emit(events.Std("No VMs — create one with `cb vm-create -n <name>`."));
      return;
    }

    await events.Emit(events.Std($"{"Name",-30} {"State",-10} IPv4"));
    foreach (var vm in list)
    {
      await events.Emit(events.Std($"{vm.Name,-30} {vm.State ?? "?",-10} {string.Join(", ", vm.Ipv4)}"));
    }
  }

  /// <summary>CPU/Memory/Disk laut <c>multipass get</c>; was nicht lesbar ist, bleibt <c>null</c>.</summary>
  private static VmResources ReadResources(CommandContext context, string name)
  {
    string? Get(string key)
    {
      try
      {
        return context.Vms.GetSetting(name, key, context.DefaultTimeoutMs) is { Length: > 0 } value ? value : null;
      }
      catch (MultipassException)
      {
        return null;
      }
    }

    return new VmResources(int.TryParse(Get("cpus"), out var cpus) ? cpus : null, Get("memory"), Get("disk"));
  }

  /// <summary>CPU/Memory/Disk-Angaben prüfen, bevor etwas an einer VM passiert.</summary>
  private static VmResources ParseResources(int? cpus, string? memory, string? disk)
  {
    try
    {
      return VmResources.Parse(cpus, memory, disk);
    }
    catch (MultipassException exception)
    {
      throw new ProgramExecutionException(1, exception.Message);
    }
  }

  /// <summary>Multipass-Namensregel: Buchstaben, Ziffern, Bindestriche; Beginn mit Buchstabe, Ende ohne Bindestrich.</summary>
  private static readonly System.Text.RegularExpressions.Regex VmNamePattern =
      new("^[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

  /// <summary>
  /// Legt die VM an (eine vorhandene gleichnamige VM nur nach Rückfrage ersetzt)
  /// und wartet in Abschnitten von <c>DefaultTimeoutVmCreation</c>;
  /// nach jedem Abschnitt wird gefragt, ob weiter gewartet werden soll. Bei Nein
  /// läuft der Launch im Multipass-Daemon weiter, und der Benutzer kann ein
  /// Folgekommando eingeben, das wie ein normaler CLI-Aufruf ausgeführt wird.
  /// </summary>
  private static async Task RunVmCreate(CommandContext context, VmCreateOptions options)
  {
    var events = context.Events;
    var (config, vms, cancellationToken) = (context.Config, context.Vms, context.CancellationToken);
    var waitSlice = config.DefaultTimeoutVmCreation.ReadTime();
    var image = string.IsNullOrWhiteSpace(options.Image) ? null : options.Image.Trim();
    var profileName = string.IsNullOrWhiteSpace(options.Profile) ? null : options.Profile.Trim();
    InitProfile? profile;
    if (profileName is not null)
    {
      if (image is not null)
      {
        throw new ProgramExecutionException(
            1,
            $"Use either --profile or --image — profile `{profileName}` brings its own image (change it with `cb init-configure -n {profileName} --runtime-image <image> -f`).");
      }

      profile = InitProfileResolver.ResolveRequired(config.Inits, profileName, options.CloudInit);
    }
    else
    {
      // Früher war --image auch der Profilname; das geht weiter, mit Hinweis.
      profile = image is null ? null : InitProfileResolver.Resolve(config.Inits, image, options.CloudInit);
      if (profile is not null)
      {
        await events.Emit(events.Err($"Hinweis: `{image}` ist ein Profil — künftig `--profile {image}` statt `--image` verwenden."));
      }
    }

    var launchImage = profile?.RuntimeImage ?? image ?? config.Inits.DefaultRuntimeImage;
    var cloudInit = options.CloudInit ?? profile?.CloudInitPath;
    // Angaben an vm-create schlagen die Vorgaben des Profils.
    var resources = ParseResources(options.Cpus, options.Memory, options.Disk).Over(profile?.Resources ?? VmResources.None);
    if (!VmNamePattern.IsMatch(options.Name))
    {
      throw new ProgramExecutionException(
          1,
          $"Invalid VM name `{options.Name}` — use letters, digits and hyphens, starting with a letter and not ending with a hyphen (e.g. dev-01).");
    }

    if (cloudInit is not null && !File.Exists(cloudInit))
    {
      throw new ProgramExecutionException(1, $"cloud-init file not found: {cloudInit}");
    }

    if (profile is not null)
    {
      // Die cloud-init ist beim init-configure gerendert; gezeigt wird, wie es heute um die Komponenten steht.
      await ReportComponentTrust(context, profile.Components);
    }

    await ReplaceExistingVm(context, options.Name);

    await events.Emit(events.Std($"Lege VM `{options.Name}` an ("
        + (profile is null ? $"Image: {launchImage}" : $"Profil: {profile.Name} → Image: {launchImage}")
        + (resources.IsEmpty ? string.Empty : $", {resources}")
        + $"{(cloudInit is null ? string.Empty : ", cloud-init")}) ..."));

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    MultipassRunResult? result;
    try
    {
      result = await WithProgress(
          context,
          () => vms.Create(
              options.Name,
              launchImage,
              resources.Disk,
              cloudInit,
              waitSlice,
              () => context.Events.Ask(context.Events.Choice<YesNoOptions>(
                  $"VM `{options.Name}` is not ready after {stopwatch.Elapsed.TotalSeconds:F0} s — keep waiting?")) == YesNoOptions.Yes,
              resources.Cpus,
              resources.Memory));
    }
    catch (MultipassTimeoutException)
    {
      result = null;
    }

    if (result is null)
    {
      await events.Emit(events.Std("Stopped waiting — Multipass keeps creating the VM in the background (check with `cb vm-list`)."));
      var followUp = await context.PromptAsync("Follow-up command (empty = quit): ");
      if (string.IsNullOrWhiteSpace(followUp))
      {
        return;
      }

      if (!TryParseOptions(SplitCommandLine(followUp), out var followUpOptions, out _))
      {
        throw new ProgramExecutionException(1, $"Invalid follow-up command: {followUp}");
      }

      var followUpContext = context with { Config = ApplyTimeoutOverride(context.Config, followUpOptions) };
      await Dispatch(followUpContext, followUpOptions);
      return;
    }

    if (result.ExitCode != 0)
    {
      foreach (var line in Lines(result.StdOut))
      {
        await events.Emit(events.Std(line));
      }

      throw Failure(result);
    }

    if (profile is not null)
    {
      await events.Emit(events.Std($"Profil: {profile.Name} → Image: {profile.RuntimeImage}"));
      await events.Emit(events.Std("Gast-Provisionierung: cloud-init"));
    }

    await events.Emit(events.Std(await Completed(vms, options.Name, $"create {options.Name}", result, cancellationToken)));
    // Ressourcen so, wie Multipass sie meldet (5.0GiB statt 5G), auch die Standardwerte.
    var actual = await Task.Run(() => ReadResources(context, options.Name), cancellationToken);
    UpdateInventory(inventory => inventory.Vms[options.Name] = new InventoryVm
    {
      Profile = profile?.Name,
      Cpus = actual.Cpus ?? resources.Cpus,
      Memory = actual.Memory ?? resources.Memory,
      Disk = actual.Disk ?? resources.Disk,
      Created = DateTime.UtcNow
    });

    if (cloudInit is not null)
    {
      // Erst wenn die Provisionierung nachweislich fertig ist, gibt es den
      // Snapshot — sonst hielte er eine halbe Installation fest, und weil
      // runcmd nur einmal je Instanz läuft, holt kein späterer Start sie nach.
      await VerifyProvisioning(context, options.Name, profile);
      if (profile is not null)
      {
        UpdateInventory(inventory => inventory.Vm(options.Name).Components = VmInventory.Merge([], profile.Components));
      }

      await OfferSnapshotAfterConfiguration(context, options.Name, profile?.Name ?? "provisioned", options.Snapshot);
    }
  }

  /// <summary>
  /// Wartet nach dem Launch, bis cloud-init im Gast fertig ist, und verlangt
  /// dann die <c>.ready</c>-Marker aller Profil-Komponenten mit Skript. Ein
  /// fehlender Marker nach beendetem cloud-init heißt: diese Komponente ist
  /// gescheitert — Exit 1, die VM läuft zur Diagnose weiter, kein Snapshot.
  /// Ohne Profil (nur <c>--cloud-init</c>) sind keine Komponenten bekannt; dann
  /// wird nur auf cloud-init gewartet.
  /// </summary>
  private static async Task VerifyProvisioning(CommandContext context, string name, InitProfile? profile)
  {
    var events = context.Events;
    var waitMs = (int)context.Config.DefaultTimeoutVmCreation.ReadTime().TotalMilliseconds;
    const string waitLabel = "cloud-init: warten, bis die Gast-Provisionierung fertig ist";
    MultipassRunResult waited;
    try
    {
      waited = await RunStep(
          context,
          waitLabel,
          () => context.Vms.Exec(name, InitInstall.WaitForCloudInitCommand, waitMs, KeepWaiting(context, waitLabel)));
    }
    catch (MultipassTimeoutException)
    {
      throw new ProgramExecutionException(
          1,
          $"Stopped waiting — cloud-init keeps provisioning `{name}` in the background; no snapshot was taken. Check later with `cb vm-exec -n {name} -c \"cloud-init status\"`.");
    }

    if (waited.ExitCode != 0)
    {
      throw new ProgramExecutionException(
          waited.ExitCode,
          $"vm-create {name}: waiting for cloud-init failed (exit {waited.ExitCode}). The VM stays running for diagnosis; no snapshot was taken.");
    }

    if (profile is null)
    {
      await events.Emit(events.Std("Provisionierung: cloud-init fertig (ohne Profil keine Komponenten zu prüfen)."));
      return;
    }

    var markers = InitInstall.Markers(context.Config.Inits, profile.Components);
    var missing = new List<(string Component, string Marker)>();
    foreach (var (component, marker) in markers)
    {
      var probe = await Task.Run(
          () => context.Vms.Exec(name, InitInstall.MarkerProbe(marker), context.DefaultTimeoutMs),
          context.CancellationToken);
      if (probe.ExitCode != 0)
      {
        missing.Add((component, marker));
      }
    }

    if (missing.Count > 0)
    {
      throw new ProgramExecutionException(
          1,
          $"vm-create {name}: cloud-init finished, but {string.Join(", ", missing.Select(m => $"`{m.Component}`"))} did not set "
          + $"{(missing.Count == 1 ? "its marker" : "their markers")} ({string.Join(", ", missing.Select(m => m.Marker))}) — the provisioning is incomplete. "
          + $"The VM stays running for diagnosis (`cb vm-exec -n {name} -c \"sudo tail -n 50 /var/log/cloud-init-output.log\"`); no snapshot was taken.");
    }

    await events.Emit(events.Std(markers.Count == 0
        ? "Provisionierung: cloud-init fertig (keine Komponente mit Marker)."
        : $"Provisionierung: OK ({string.Join(", ", markers.Select(m => m.Component))})"));
  }

  private static async Task RunVmExec(CommandContext context, VmExecOptions options)
  {
    await EnsureRunning(context, options.Name);

    var result = await WithProgress(
        context,
        () => context.Vms.Exec(options.Name, options.Command, context.DefaultTimeoutMs));
    await Report(context.Events, result);
  }

  private static async Task RunVmConnect(CommandContext context, VmConnectOptions options)
  {
    await EnsureRunning(context, options.Name);

    var exitCode = await Task.Run(() => context.Vms.Connect(options.Name), context.CancellationToken);
    if (exitCode != 0)
    {
      throw new ProgramExecutionException(exitCode, $"vm-connect fehlgeschlagen (Exit {exitCode}).");
    }
  }

  private static async Task RunVmStart(CommandContext context, VmStartOptions options)
  {
    var name = options.Name;
    var vm = await RequireVm(context, name);
    if (vm.IsDeleted)
    {
      throw InTrash(name);
    }

    if (vm.IsRunning)
    {
      await context.Events.Emit(context.Events.Std($"start {name}: OK (lief bereits) — State: Running"));
      return;
    }

    await StartVm(context, name);
  }

  private static async Task RunVmStop(CommandContext context, VmStopOptions options)
  {
    var name = options.Name;
    var vm = await RequireVm(context, name);
    if (vm.IsDeleted)
    {
      throw InTrash(name);
    }

    if (vm.IsStopped)
    {
      await context.Events.Emit(context.Events.Std($"stop {name}: OK (was already stopped) — State: {vm.State}"));
      return;
    }

    if (vm.IsTransitional
        && !await context.ConfirmAsync($"VM `{name}` is {vm.State} — Multipass can only power it off (--force). Power it off?"))
    {
      throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` was not stopped (State: {vm.State}).");
    }

    await StopVm(context, vm);
  }

  /// <summary>
  /// Startet die VM und wartet in Abschnitten von <c>DefaultTimeoutVmStart</c> — ein Start
  /// dauert unter Hyper-V oft länger als <c>DefaultTimeout</c> (Issue #5).
  /// </summary>
  private static Task StartVm(CommandContext context, string name)
  {
    return RunVmStateCommand(
        context,
        name,
        $"start {name}",
        () => context.Vms.Start(name, context.VmStartTimeoutMs, KeepWaiting(context, $"start {name}")));
  }

  /// <summary>
  /// Stoppt die VM; in Übergangszuständen (<see cref="VmInfo.IsTransitional"/>)
  /// mit <c>--force</c>, weil Multipass den normalen Stop dort ablehnt. Die
  /// Rückfrage dazu stellt der Aufrufer.
  /// </summary>
  private static Task StopVm(CommandContext context, VmInfo vm)
  {
    var force = vm.IsTransitional;
    var label = force ? $"stop --force {vm.Name}" : $"stop {vm.Name}";
    return RunVmStateCommand(
        context,
        vm.Name,
        label,
        () => context.Vms.Stop(vm.Name, context.DefaultTimeoutMs, KeepWaiting(context, label), force));
  }

  /// <summary>
  /// Löscht die VM (in den Papierkorb oder mit <c>--purge</c> endgültig). Vor dem
  /// Löschen einer laufenden VM wird gefragt; eine VM im Papierkorb ohne
  /// <c>--purge</c> ist bereits gelöscht.
  /// </summary>
  private static async Task RunVmDelete(CommandContext context, VmDeleteOptions options)
  {
    var name = options.Name;
    var vm = await RequireVm(context, name);
    if (vm.IsDeleted && !options.Purge)
    {
      await context.Events.Emit(context.Events.Std($"delete {name}: OK (already in the trash; --purge removes it for good) — State: Deleted"));
      return;
    }

    if (!vm.IsDeleted
        && !vm.IsStopped
        && !await context.ConfirmAsync($"VM `{name}` is {vm.State} — stop and delete it?"))
    {
      throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` was not deleted (State: {vm.State}).");
    }

    if (!vm.IsDeleted && !vm.IsStopped)
    {
      await StopVm(context, vm);
    }

    var label = $"delete {name}{(options.Purge ? " --purge" : string.Empty)}";
    await RunVmStateCommand(
        context,
        name,
        label,
        () => context.Vms.Delete(name, options.Purge, context.DefaultTimeoutMs, KeepWaiting(context, label)));
    if (options.Purge)
    {
      UpdateInventory(inventory => inventory.Vms.Remove(name));
    }
  }

  /// <summary>
  /// Gibt es die VM schon (auch im Papierkorb), wird gefragt, ob sie gestoppt,
  /// endgültig gelöscht und neu angelegt werden darf; bei Nein bricht vm-create ab.
  /// </summary>
  private static async Task ReplaceExistingVm(CommandContext context, string name)
  {
    var existing = await Task.Run(() => context.Vms.Find(name), context.CancellationToken);
    if (existing is null)
    {
      return;
    }

    var question = existing.IsDeleted
        ? $"VM `{name}` is still in the trash (Deleted) — purge it and create it again?"
        : $"VM `{name}` already exists ({existing.State}) — {(existing.IsStopped ? "delete" : "stop and delete")} it and create it again?";
    if (!await context.ConfirmAsync(question))
    {
      throw new ProgramExecutionException(
          1,
          $"Cancelled — VM `{name}` already exists ({existing.State}). Pick another name or remove it with `cb vm-delete -n {name} --purge`.");
    }

    if (!existing.IsDeleted && !existing.IsStopped)
    {
      await StopVm(context, existing);
    }

    await RunVmStateCommand(
        context,
        name,
        $"delete {name} --purge",
        () => context.Vms.Delete(name, purge: true, context.DefaultTimeoutMs, KeepWaiting(context, $"delete {name}")));
    UpdateInventory(inventory => inventory.Vms.Remove(name));
  }

  /// <summary>
  /// Stellt sicher, dass die VM läuft: fehlt sie oder liegt sie im Papierkorb, gibt
  /// es eine fertige Meldung; ist sie gestoppt, wird nach Rückfrage gestartet.
  /// Übergangszustände (Starting, Restarting …) wartet Multipass selbst ab.
  /// </summary>
  /// <param name="ask">
  /// <c>false</c>: ohne Rückfrage starten — für Befehle, die eine laufende VM
  /// ausdrücklich voraussetzen (<c>vm-install</c>).
  /// </param>
  private static async Task EnsureRunning(CommandContext context, string name, bool ask = true)
  {
    var events = context.Events;
    var vm = await RequireVm(context, name);
    if (vm.IsDeleted)
    {
      throw InTrash(name);
    }

    if (vm.IsTransitional)
    {
      await events.Emit(events.Std($"VM `{name}` is {vm.State} — waiting for it to come up (if it hangs: `cb vm-stop -n {name}` powers it off) ..."));
    }

    if (!vm.IsStopped)
    {
      return;
    }

    if (ask && !await context.ConfirmAsync($"VM `{name}` is {vm.State} — start it now?"))
    {
      throw new ProgramExecutionException(1, $"Cancelled — VM `{name}` is {vm.State}; start it with `cb vm-start -n {name}`.");
    }

    await StartVm(context, name);
  }

  /// <summary>VM laut vm-list; fehlt sie, gibt es eine fertige Meldung mit den vorhandenen Namen.</summary>
  private static async Task<VmInfo> RequireVm(CommandContext context, string name)
  {
    var list = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
    return list.FirstOrDefault(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ProgramExecutionException(
            1,
            $"VM `{name}` does not exist. "
            + (list.Count == 0
                ? "There are no VMs yet — create one with `cb vm-create -n <name>`."
                : $"Existing VMs: {string.Join(", ", list.Select(vm => vm.Name))}."));
  }

  private static ProgramExecutionException InTrash(string name)
  {
    return new ProgramExecutionException(
        1,
        $"VM `{name}` is in the trash (Deleted) — restore it with `multipass recover {name}` or remove it with `cb vm-delete -n {name} --purge`.");
  }

  /// <summary>
  /// Führt ein zustandsänderndes VM-Kommando aus und meldet zum Schluss
  /// Erfolg, Dauer und den neuen State laut vm-list.
  /// </summary>
  private static async Task RunVmStateCommand(
      CommandContext context,
      string name,
      string label,
      Func<MultipassRunResult> action)
  {
    var events = context.Events;
    await events.Emit(events.Std($"{label} ..."));
    MultipassRunResult result;
    try
    {
      result = await WithProgress(context, action);
    }
    catch (MultipassTimeoutException)
    {
      // Hier endet ein Timeout nur, wenn der Benutzer "weiter warten?" verneint
      // hat — der Daemon arbeitet weiter, der Dienst hängt nicht. Exit 1, weil
      // ein folgender Schritt (Löschen nach Stopp, exec nach Start) nicht auf
      // einem unfertigen Zustand aufsetzen darf.
      throw new ProgramExecutionException(
          1,
          $"Stopped waiting — Multipass keeps running `{label}` in the background (check with `cb vm-list`).");
    }

    if (result.ExitCode != 0)
    {
      await Report(events, result);
    }

    await events.Emit(events.Std(await Completed(context.Vms, name, label, result, context.CancellationToken)));
  }

  /// <summary>Abschlusszeile: <c>&lt;label&gt;: OK (&lt;Dauer&gt;) — State: &lt;State&gt;</c>.</summary>
  private static async Task<string> Completed(
      VmService vms,
      string name,
      string label,
      MultipassRunResult result,
      CancellationToken cancellationToken)
  {
    var state = await QueryVmState(vms, name, cancellationToken) ?? "nicht vorhanden";
    return $"{label}: OK ({result.DurationMs / 1000.0:F1} s) — State: {state}";
  }

  /// <summary>State der VM laut vm-list, <c>null</c> wenn sie nicht existiert.</summary>
  private static async Task<string?> QueryVmState(VmService vms, string name, CancellationToken cancellationToken)
  {
    var list = await Task.Run(() => vms.List(), cancellationToken);
    return list
        .FirstOrDefault(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase))
        ?.State;
  }

  private static async Task RunVmPut(CommandContext context, VmPutOptions options)
  {
    var events = context.Events;
    if (!File.Exists(options.Source))
    {
      throw new ProgramExecutionException(1, $"Quelldatei fehlt: {options.Source}");
    }

    await EnsureRunning(context, options.Name);

    await events.Emit(events.Std($"transfer {options.Source} → {options.Name} ..."));
    var result = await WithProgress(
        context,
        () => context.Vms.Put(options.Source, options.Name, options.Destination, context.DefaultTimeoutMs));
    if (result.ExitCode != 0)
    {
      await Report(events, result);
    }

    await events.Emit(events.Std($"transfer: OK ({result.DurationMs / 1000.0:F1} s)"));
  }

  private static async Task RunVmGet(CommandContext context, VmGetOptions options)
  {
    var events = context.Events;
    await EnsureRunning(context, options.Name);

    var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Destination));
    if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
    {
      Directory.CreateDirectory(targetDirectory);
    }

    await events.Emit(events.Std($"transfer {options.Name}:{options.Remote} → {options.Destination} ..."));
    var result = await WithProgress(
        context,
        () => context.Vms.Get(options.Name, options.Remote, options.Destination, context.DefaultTimeoutMs));
    if (!File.Exists(options.Destination))
    {
      await Report(events, result);
      return;
    }

    var megaBytes = new FileInfo(options.Destination).Length / (1024.0 * 1024.0);
    await events.Emit(events.Std($"transfer: OK — {megaBytes:N0} MB ({result.DurationMs / 1000.0:F1} s)"));
    if (result.ExitCode != 0)
    {
      await events.Emit(events.Std($"Hinweis: Exit-Code {result.ExitCode} — {result.StdErr.Trim()}"));
    }
  }

  /// <summary>
  /// Clone, Snapshot und Restore gehen nur auf gestoppten VMs: eine laufende
  /// wird nach Rückfrage gestoppt, eine im Papierkorb abgewiesen.
  /// </summary>
  private static async Task EnsureStopped(CommandContext context, VmInfo vm, string purpose)
  {
    if (vm.IsDeleted)
    {
      throw InTrash(vm.Name);
    }

    if (vm.IsStopped)
    {
      return;
    }

    if (!await context.ConfirmAsync($"VM `{vm.Name}` is {vm.State} — stop it for {purpose}?"))
    {
      throw new ProgramExecutionException(1, $"Cancelled — {purpose} needs a stopped VM; `{vm.Name}` is {vm.State}.");
    }

    await StopVm(context, vm);
  }
}
