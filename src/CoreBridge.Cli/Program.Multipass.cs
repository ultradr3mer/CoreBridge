using System.Text;
using CoreBridge.Interaction;
using CoreBridge.Core.Common;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

public static partial class Program
{
  private static async Task RunMultipassStatus(CommandContext context)
  {
    var events = context.Events;

    // Bei Wiedergabe/Simulation gibt es weder Binary noch Daemon: den
    // Pseudo-Pfad melden, damit der Erfolgspfad (Parsing der Version) durchläuft.
    var cli = context.Vms.Cli;
    var report = await Task.Run(
        () => cli is IMultipassDouble multipassDouble
            ? Doctor.Check(() => multipassDouble.BinaryLabel, cli, context.DefaultTimeoutMs)
            : Doctor.Check(cli: cli, timeoutMs: context.DefaultTimeoutMs),
        context.CancellationToken);
    if (!report.Found)
    {
      var message = new StringBuilder("Multipass nicht gefunden.\n");
      foreach (var hint in report.Hints)
      {
        message.Append($"\n  - {hint}");
      }

      throw new ProgramExecutionException(1, message.ToString());
    }

    await events.Emit(events.Std($"Binary:  {report.BinaryPath}"));
    await events.Emit(events.Std($"CLI:     {report.ClientVersion ?? "unbekannt"}"));
    await events.Emit(events.Std($"Daemon:  {report.DaemonVersion ?? "keine Antwort"}"));
    if (report.Error is not null)
    {
      // Ein Daemon, der nicht antwortet, ist kein gesundes Multipass: Exit 1,
      // damit Skripte und die Lifecycle-Stage00 nicht auf einem toten Daemon weiterbauen.
      foreach (var line in Lines(report.RawVersionOutput ?? string.Empty))
      {
        await events.Emit(events.Std(line));
      }

      // Ein abgelehnter Client wird durch einen Neustart nicht authentifiziert.
      throw new ProgramExecutionException(
          1,
          report.NotAuthenticated ? report.Error : $"{report.Error} — Dienst neu starten mit `cb multipass-restart`.");
    }
  }

  private static async Task RunMultipassRestart(CommandContext context, MultipassRestartOptions options)
  {
    var events = context.Events;
    var cancellationToken = context.CancellationToken;
    var timeout = context.Config.DefaultTimeout.ReadTime();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var service = new MultipassService(commandTimeout: timeout);

    // Ein hängender Stop antwortet erfahrungsgemäß nicht mehr: während des Wartens
    // beendet Enter den Daemon sofort, statt den ganzen DefaultTimeout abzusitzen.
    var offer = new StopOffer(events);

    // Erst ohne Erhöhung: ob der Benutzer den Dienst steuern darf, entscheidet
    // dessen ACL, nicht die Gruppe. Erhöht wird nur, wenn es wirklich nicht geht —
    // vorab (keine Rechte am Dienst) oder mittendrin (z. B. multipassd hart
    // beenden, der als SYSTEM läuft); das erhöhte Fenster setzt dann am
    // vorgefundenen Zustand fort (StopPending → warten → beenden).
    if (service.IsElevated() || service.CanControlService())
    {
      try
      {
        await EmitRestart(
            events,
            offer,
            service.Restart(timeout, ConfirmInCore(events), offer.Open, options.KillNow, cancellationToken: cancellationToken));
        await events.Emit(events.Std($"multipass-restart: OK ({stopwatch.Elapsed.TotalSeconds:F1} s) — State: {service.QueryState()}"));
        return;
      }
      catch (MultipassAccessDeniedException exception) when (!service.IsElevated())
      {
        await events.Emit(events.Std($"Warnung: {exception.Message}"));
      }
    }
    else
    {
      await events.Emit(events.Std($"Warnung: Der Benutzer darf den Dienst {MultipassService.ServiceName} nicht stoppen und starten — der Neustart benötigt Administratorrechte."));
    }

    if (!await context.AcknowledgeAsync("Mit Administratorrechten in einem neuen Fenster fortfahren?"))
    {
      throw new ProgramExecutionException(1, "Abgebrochen — ohne Administratorrechte kein Neustart.");
    }

    await events.Emit(events.Std("Starte erhöht (UAC-Abfrage) — Ausgabe und Rückfragen folgen im Administratorfenster ..."));
    var exitCode = await ElevatedRelaunch.RunAsync(
        typeof(Program).Assembly.Location,
        ["multipass-restart", "--pause-on-exit", "--timeout", context.Config.DefaultTimeout, .. (offer.KillChosen || options.KillNow ? new[] { "--kill-now" } : [])],
        cancellationToken);
    if (exitCode == ElevatedRelaunch.ControlCExit)
    {
      // Fenster geschlossen oder Strg+C statt einer Taste: über das Ergebnis
      // des Neustarts sagt der Exit-Code dann nichts.
      throw new ProgramExecutionException(
          1,
          $"Das Administratorfenster wurde geschlossen, bevor multipass-restart fertig war (Strg+C oder Fenster zu) — Zustand des Dienstes: {service.QueryState()}; prüfen mit `cb multipass-status`.");
    }

    if (exitCode != 0)
    {
      throw new ProgramExecutionException(exitCode, $"multipass-restart im Administratorfenster fehlgeschlagen (Exit {exitCode}).");
    }

    await events.Emit(events.Std($"multipass-restart: OK ({stopwatch.Elapsed.TotalSeconds:F1} s, erhöht) — State: {service.QueryState()}"));
  }

  /// <summary>
  /// Zeigt oder ändert den Speicherort der Multipass-Daten (<c>MULTIPASS_STORAGE</c>).
  /// Ändern: Rückfrage, laufende VMs nach Rückfrage stoppen, dann — erhöht, notfalls
  /// per UAC in einem neuen Fenster — Variable setzen und den Dienst neu starten. Es
  /// wird nichts kopiert: der neue Ort ist leer, und der Dienst kennt diesen Client
  /// dort noch nicht. Deshalb prüft das ursprüngliche Fenster zum Schluss die
  /// Authentifizierung und zeigt, wie der Client einmalig vertraut wird.
  /// </summary>
  private static async Task RunMultipassStore(CommandContext context, MultipassStoreOptions options)
  {
    var events = context.Events;
    var storage = new MultipassStorage();
    var current = storage.Current();
    await events.Emit(events.Std($"Speicherort: {current.Path} ({DescribeSource(current.Source)})"));
    if (options.Path is null && !options.Reset)
    {
      await ReportClientTrust(context, passphrase: null);
      return;
    }

    if (options.Path is not null && options.Reset)
    {
      throw new ProgramExecutionException(1, "Entweder -p/--path oder --reset angeben, nicht beides.");
    }

    var target = options.Reset ? null : Path.GetFullPath(options.Path!.Trim());
    var effective = target ?? MultipassStorage.DefaultPath;
    if (File.Exists(effective))
    {
      throw new ProgramExecutionException(1, $"`{effective}` ist eine Datei — der Speicherort muss ein Ordner sein.");
    }

    // Steht der Ort nur als System-Variable fest, erreicht er den Dienst erst nach einem
    // Neustart des Rechners; dann lohnt das Umstellen trotzdem (Dienst-Umgebung).
    if (MultipassStorage.SamePath(current.Path, effective) && current.Source != MultipassStorageSource.System)
    {
      await events.Emit(events.Std($"multipass-store: unverändert ({effective})."));
      return;
    }

    var service = new MultipassService(commandTimeout: context.Config.DefaultTimeout.ReadTime());
    var elevated = service.IsElevated();
    if (!options.Yes
        && !await context.ConfirmAsync(
            $"Multipass legt seine Daten danach unter {effective} ab. Vorhandene VMs und Images bleiben in {current.Path} "
            + "und sind dann nicht mehr sichtbar — sie werden weder kopiert noch gelöscht; mitgenommen werden nur die Einstellungen "
            + "(multipassd.conf, damit die Passphrase gilt), wo das Ziel keine eigenen hat. Der Dienst wird dafür neu gestartet"
            + (elevated ? string.Empty : " (braucht Administratorrechte, UAC-Abfrage)") + ". Umstellen?"))
    {
      throw new ProgramExecutionException(1, $"Abgebrochen — der Speicherort bleibt {current.Path}.");
    }

    // Die Passphrase setzt nur ein vertrauter Client — also jetzt, solange der Dienst diesen
    // Client noch kennt; nach dem Umstellen nimmt `multipass authenticate` ihn damit wieder auf.
    string? passphrase = null;
    if (!options.PauseOnExit)
    {
      await StopRunningVmsForStore(context);
      passphrase = await PreparePassphrase(context);
    }

    if (elevated)
    {
      await SwitchStorage(context, storage, service, target);
    }
    else
    {
      await events.Emit(events.Std("Starte erhöht (UAC-Abfrage) — Ausgabe und Rückfragen folgen im Administratorfenster ..."));
      var exitCode = await ElevatedRelaunch.RunAsync(
          typeof(Program).Assembly.Location,
          ["multipass-store", .. (target is null ? new[] { "--reset" } : ["--path", target]), "--yes", "--pause-on-exit", "--timeout", context.Config.DefaultTimeout],
          context.CancellationToken);
      if (exitCode == ElevatedRelaunch.ControlCExit)
      {
        throw new ProgramExecutionException(
            1,
            $"Das Administratorfenster wurde geschlossen, bevor multipass-store fertig war — Speicherort jetzt: {storage.Current().Path}; prüfen mit `cb multipass-store`.");
      }

      if (exitCode != 0)
      {
        throw new ProgramExecutionException(exitCode, $"multipass-store im Administratorfenster fehlgeschlagen (Exit {exitCode}).");
      }
    }

    if (options.PauseOnExit)
    {
      // Das Admin-Fenster prüft nicht: authentifiziert werden muss der Client des Benutzers.
      await events.Emit(events.Std("Fertig — die Prüfung des Clients folgt im ursprünglichen Fenster."));
      return;
    }

    await events.Emit(events.Std($"multipass-store: OK — Speicherort {storage.Current().Path}"));
    await ReportClientTrust(context, passphrase);
  }

  /// <summary>
  /// Setzt eine zufällige Passphrase (<c>multipass set local.passphrase</c>), solange dieser
  /// Client noch vertraut ist. Sie lebt nur in diesem Aufruf und wird nirgends gespeichert.
  /// Geht es nicht — etwa weil der Dienst den Client schon jetzt ablehnt —, gibt es einen
  /// Hinweis und <c>null</c>; umgestellt wird trotzdem.
  /// </summary>
  private static async Task<string?> PreparePassphrase(CommandContext context)
  {
    var events = context.Events;
    var passphrase = MultipassAuthentication.NewPassphrase();
    MultipassRunResult result;
    try
    {
      result = await Task.Run(() => new MultipassAuthentication().SetPassphrase(passphrase, context.DefaultTimeoutMs), context.CancellationToken);
    }
    catch (MultipassException exception)
    {
      result = new MultipassRunResult(1, string.Empty, exception.Message, 0);
    }

    if (result.ExitCode == 0)
    {
      await events.Emit(events.Std("Passphrase gesetzt (zufällig, nur für diesen Vorgang) — damit wird dieser Client nach dem Umstellen wieder aufgenommen."));
      return passphrase;
    }

    await events.Emit(events.Std(
        $"Hinweis: die Passphrase ließ sich nicht setzen ({FirstLine(result.StdErr, result.StdOut)}) — der Dienst vertraut diesem Client schon jetzt nicht; "
        + "nach dem Umstellen entscheidet, ob der Ziel-Ordner ihm vertraut."));
    return null;
  }

  /// <summary>
  /// Erhöht: Ordner anlegen, die Einstellungen samt Passphrase mitnehmen
  /// (<see cref="MultipassStorage.CopySettings"/>), Variable an beiden Stellen setzen, Dienst
  /// neu starten. Kopiert wird vor dem Neustart — bis dahin arbeitet der Dienst noch im
  /// alten Ordner, der neue ist unbenutzt.
  /// </summary>
  private static async Task SwitchStorage(CommandContext context, MultipassStorage storage, MultipassService service, string? target)
  {
    var events = context.Events;
    var from = storage.Current().Path;
    var to = target ?? MultipassStorage.DefaultPath;
    Directory.CreateDirectory(to);
    static string Describe(string file) => File.Exists(file) ? $"{new FileInfo(file).Length} Bytes" : "fehlt";
    await events.Emit(events.Std(
        $"{MultipassStorage.SettingsFile} vorher: {from} {Describe(Path.Combine(from, MultipassStorage.SettingsFile))}, "
        + $"{to} {Describe(Path.Combine(to, MultipassStorage.SettingsFile))}"));
    await events.Emit(events.Std(MultipassStorage.CopySettings(from, to)
        ? $"{MultipassStorage.SettingsFile} (Einstellungen samt Passphrase) aus {from} mitgenommen."
        : $"{MultipassStorage.SettingsFile}: {to} hat eigene (oder {from} keine) — nicht kopiert."));

    storage.Set(target);
    await events.Emit(events.Std(target is null
        ? $"{MultipassStorage.VariableName} entfernt (System-Variable und Dienst-Umgebung) — Standard {MultipassStorage.DefaultPath}"
        : $"{MultipassStorage.VariableName} = {target} (System-Variable und Dienst-Umgebung)"));

    // Ein leerer Speicherort kennt keinen Client: dass der Dienst ihn ablehnt, ist hier
    // der erwartete Zustand und kein Grund, auf eine Daemon-Version zu warten.
    var offer = new StopOffer(events);
    await EmitRestart(
        events,
        offer,
        service.Restart(
            context.Config.DefaultTimeout.ReadTime(),
            ConfirmInCore(events),
            offer.Open,
            acceptRejectedClient: true,
            cancellationToken: context.CancellationToken));
  }

  /// <summary>
  /// Laufende VMs vor dem Umstellen stoppen (nach Rückfrage). Lässt sich die Liste
  /// nicht abfragen — etwa weil der Dienst diesen Client schon jetzt ablehnt —, gibt
  /// es nur einen Hinweis.
  /// </summary>
  private static async Task StopRunningVmsForStore(CommandContext context)
  {
    var events = context.Events;
    IReadOnlyList<VmInfo> vms;
    try
    {
      vms = await Task.Run(() => context.Vms.List(context.DefaultTimeoutMs), context.CancellationToken);
    }
    catch (MultipassException exception)
    {
      await events.Emit(events.Std($"Hinweis: laufende VMs ließen sich nicht abfragen ({exception.Message.Trim()}) — laufen noch welche, vorher von Hand stoppen."));
      return;
    }

    var running = vms.Where(vm => !vm.IsStopped && !vm.IsDeleted).ToList();
    if (running.Count == 0)
    {
      return;
    }

    if (!await context.ConfirmAsync($"{running.Count} VM(s) laufen noch ({string.Join(", ", running.Select(vm => vm.Name))}) — vor dem Umstellen stoppen?"))
    {
      throw new ProgramExecutionException(1, "Abgebrochen — erst die VMs stoppen, dann den Speicherort umstellen.");
    }

    foreach (var vm in running)
    {
      await StopVm(context, vm);
    }
  }

  /// <summary>
  /// Prüft, ob der Dienst diesen Client annimmt. Lehnt er ab und gibt es eine Passphrase
  /// von vor dem Umstellen, wird der Client damit aufgenommen (<c>multipass authenticate</c>)
  /// und erneut geprüft. Bleibt er abgelehnt, endet das Kommando mit Exit 1 und dem Weg,
  /// der wirklich hilft (<see cref="Doctor.NotAuthenticatedMessage"/>).
  /// </summary>
  private static async Task ReportClientTrust(CommandContext context, string? passphrase)
  {
    var events = context.Events;
    var report = await CheckDaemon(context);
    if (report.NotAuthenticated && passphrase is not null)
    {
      MultipassRunResult result;
      try
      {
        result = await Task.Run(() => new MultipassAuthentication().Authenticate(passphrase, context.DefaultTimeoutMs), context.CancellationToken);
      }
      catch (MultipassException exception)
      {
        result = new MultipassRunResult(1, string.Empty, exception.Message, 0);
      }

      await events.Emit(events.Std(result.ExitCode == 0
          ? "multipass authenticate: OK"
          : $"multipass authenticate fehlgeschlagen ({FirstLine(result.StdErr, result.StdOut)}) — der Ziel-Ordner kennt diese Passphrase nicht (er hatte wohl eigene Einstellungen, die nicht überschrieben werden)."));
      report = await CheckDaemon(context);
    }

    if (report.NotAuthenticated)
    {
      throw new ProgramExecutionException(1, Doctor.NotAuthenticatedMessage);
    }

    await events.Emit(report.DaemonVersion is null
        ? events.Std($"Warnung: {report.Error}")
        : events.Std($"Daemon {report.DaemonVersion} — dieser Client ist authentifiziert."));
  }

  private static Task<DoctorReport> CheckDaemon(CommandContext context) =>
      Task.Run(() => Doctor.Check(cli: context.Vms.Cli, timeoutMs: context.DefaultTimeoutMs), context.CancellationToken);

  private static string DescribeSource(MultipassStorageSource source) => source switch
  {
    MultipassStorageSource.Service => "Dienst-Umgebung",
    MultipassStorageSource.System => $"System-Variable {MultipassStorage.VariableName}",
    _ => "Standard"
  };

  /// <summary>
  /// Ja/Nein-Rückfrage für <see cref="MultipassService"/>, das <c>Func&lt;string,bool&gt;</c>
  /// erwartet; sie blockiert auf einem Threadpool-Thread, während der Konsument den Kanal bedient.
  /// </summary>
  private static Func<string, bool> ConfirmInCore(EventFactory events)
  {
    return question => events.Ask(events.Choice<YesNoOptions>(question)) == YesNoOptions.Yes;
  }

  /// <summary>
  /// Meldungen eines Dienst-Neustarts weiterreichen. Solange der Dienst auf den Stop
  /// wartet, ist sein Angebot (<see cref="StopOffer"/>) offen und reist auf den Ticks mit;
  /// beantwortet und geschlossen wird es im Dienst selbst.
  /// </summary>
  private static async Task EmitRestart(EventFactory events, StopOffer offer, IAsyncEnumerable<string> lines)
  {
    await foreach (var line in lines)
    {
      await events.Emit(line != Progress.ProgressTick
          ? events.Std(line)
          : offer.Current is { IsOpen: true } open ? events.Tick(open) : events.Tick());
    }
  }

  /// <summary>
  /// Das Angebot während des Wartens auf den Stop: <see cref="Open"/> ist die Fabrik für
  /// <see cref="MultipassService.Restart"/>, <see cref="Current"/> trägt das Angebot auf die
  /// Ticks, und <see cref="KillChosen"/> sagt hinterher, ob Enter gedrückt wurde — dann
  /// bekommt ein erhöhtes Fenster <c>--kill-now</c>, statt erneut zu warten.
  /// </summary>
  private sealed class StopOffer(EventFactory events)
  {
    public ChoiceEvent<HungStopOptions>? Current { get; private set; }

    public bool KillChosen => this.Current?.TryGetAnswer(out var answer) == true && answer == HungStopOptions.Kill;

    public ChoiceEvent<HungStopOptions> Open(string message) => this.Current = events.Choice<HungStopOptions>(message);
  }
}
