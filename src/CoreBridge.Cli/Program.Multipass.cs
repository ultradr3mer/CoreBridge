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

      throw new ProgramExecutionException(
          1,
          $"{report.Error} — Dienst neu starten mit `cb multipass-restart`.");
    }
  }

  private static async Task RunMultipassRestart(CommandContext context)
  {
    var events = context.Events;
    var cancellationToken = context.CancellationToken;
    var timeout = context.Config.DefaultTimeout.ReadTime();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var service = new MultipassService(commandTimeout: timeout);

    // Erst ohne Erhöhung: ob der Benutzer den Dienst steuern darf, entscheidet
    // dessen ACL, nicht die Gruppe. Erhöht wird nur, wenn es wirklich nicht geht —
    // vorab (keine Rechte am Dienst) oder mittendrin (z. B. multipassd hart
    // beenden, der als SYSTEM läuft); das erhöhte Fenster setzt dann am
    // vorgefundenen Zustand fort (StopPending → warten → beenden).
    if (service.IsElevated() || service.CanControlService())
    {
      // MultipassService erwartet Func<string,bool>; die blockiert auf einem
      // Threadpool-Thread, während der Konsument den Kanal bedient.
      bool ConfirmInCore(string question)
          => events.Ask(events.Choice<YesNoOptions>(question)) == YesNoOptions.Yes;

      try
      {
        await foreach (var line in service.Restart(timeout, ConfirmInCore, cancellationToken))
        {
          await events.Emit(line == Progress.ProgressTick ? events.Tick() : events.Std(line));
        }

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

    if (!await context.ConfirmAsync("Mit Administratorrechten in einem neuen Fenster fortfahren?"))
    {
      throw new ProgramExecutionException(1, "Abgebrochen — ohne Administratorrechte kein Neustart.");
    }

    await events.Emit(events.Std("Starte erhöht (UAC-Abfrage) — Ausgabe und Rückfragen folgen im Administratorfenster ..."));
    var exitCode = await RunElevatedAsync(context.Config.DefaultTimeout, cancellationToken);
    if (exitCode == ControlCExit)
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

  /// <summary><c>STATUS_CONTROL_C_EXIT</c> (0xC000013A): Konsolenfenster geschlossen oder Strg+C.</summary>
  private const int ControlCExit = unchecked((int)0xC000013A);

  /// <summary>
  /// Startet dieselbe CLI per UAC (<c>runas</c>) mit <c>multipass-restart
  /// --pause-on-exit --timeout &lt;timeout&gt;</c> (der wirksame <c>DefaultTimeout</c>)
  /// und wartet auf ihr Ende. Läuft die CLI als DLL unter <c>dotnet</c>,
  /// wird die DLL als erstes Argument mitgegeben; läuft sie eingebettet in einem
  /// fremden Host (Testhost), wird die CLI neben der Assembly gestartet.
  /// </summary>
  private static async Task<int> RunElevatedAsync(string timeout, CancellationToken cancellationToken)
  {
    var processPath = Environment.ProcessPath
        ?? throw new ProgramExecutionException(1, "Pfad der laufenden CLI unbekannt — erhöhter Neustart nicht möglich.");
    var cliAssembly = typeof(Program).Assembly.Location;
    var processName = Path.GetFileNameWithoutExtension(processPath);
    var (fileName, passAssembly) = processName switch
    {
      _ when string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) => (processPath, true),
      _ when string.Equals(processName, Path.GetFileNameWithoutExtension(cliAssembly), StringComparison.OrdinalIgnoreCase) => (processPath, false),
      // Eingebettet (z. B. Program.Run im Testhost): nicht den fremden Host
      // neu starten, sondern die CLI neben der Assembly.
      _ when File.Exists(Path.ChangeExtension(cliAssembly, ".exe")) => (Path.ChangeExtension(cliAssembly, ".exe"), false),
      _ => ("dotnet", true)
    };
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
      FileName = fileName,
      UseShellExecute = true,
      Verb = "runas",
      WorkingDirectory = Environment.CurrentDirectory
    };
    if (passAssembly)
    {
      startInfo.ArgumentList.Add(cliAssembly);
    }

    foreach (var argument in new[] { "multipass-restart", "--pause-on-exit" })
    {
      startInfo.ArgumentList.Add(argument);
    }

    startInfo.ArgumentList.Add("--timeout");
    startInfo.ArgumentList.Add(timeout);

    System.Diagnostics.Process? process;
    try
    {
      process = System.Diagnostics.Process.Start(startInfo);
    }
    catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
    {
      throw new ProgramExecutionException(1, "Abgebrochen — Administratorrechte in der UAC-Abfrage abgelehnt.");
    }

    using var elevated = process
        ?? throw new ProgramExecutionException(1, "Erhöhter Prozess konnte nicht gestartet werden.");
    await elevated.WaitForExitAsync(cancellationToken);
    return elevated.ExitCode;
  }
}
