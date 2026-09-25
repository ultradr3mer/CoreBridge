using System.Diagnostics;
using System.Text;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Vm;

/// <summary>
/// Basisklasse für testbare Konsolen-Sessions auf einer VM: Befehle senden,
/// Ausgaben sammeln, via <see cref="WaitFor"/> prüfen, optional live mitloggen
/// (z. B. ITestOutputHelper.WriteLine im Test). Zwei Implementierungen:
/// <see cref="VmConsoleSession"/> (ein Prozess, Pipes) und
/// <see cref="VmExecSession"/> (ein exec pro Befehl — der verlässliche Weg,
/// da der Multipass-Windows-Client gepipetes stdin nicht forwardet).
/// </summary>
public abstract class VmSession : IDisposable
{
  private readonly StringBuilder _output = new();
  private readonly object _lock = new();
  private readonly Action<string>? _log;

  protected VmSession(Action<string>? log)
  {
    _log = log;
  }

  /// <summary>Roh-Eingabe (kein Zeilenumbruch) — Bedeutung je nach Implementierung.</summary>
  public abstract void Send(string input);

  /// <summary>Befehl senden (inkl. Zeilenumbruch).</summary>
  public virtual void SendLine(string command)
  {
    Send(command + "\n");
  }

  /// <summary>Befehl senden und auf seine Ausgabe warten.</summary>
  public string SendCommand(string command, string expected, TimeSpan timeout)
  {
    SendLine(command);
    return WaitFor(expected, timeout);
  }

  /// <summary>
  /// Wartet, bis <paramref name="expected"/> in der gesammelten Ausgabe
  /// auftaucht, und liefert alles bisher Gesammelte zurück (auch stderr).
  /// </summary>
  public string WaitFor(string expected, TimeSpan timeout)
  {
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
      string snapshot;
      lock (_lock)
      {
        snapshot = _output.ToString();
      }

      if (snapshot.Contains(expected, StringComparison.Ordinal))
      {
        return snapshot;
      }

      if (HasExited)
      {
        throw new MultipassException(
            $"Shell ist vorher beendet (exit {ExitCode}) — gesucht wurde: {expected}");
      }

      Thread.Sleep(100);
    }

    string soFar;
    lock (_lock)
    {
      soFar = _output.ToString();
    }

    throw new MultipassTimeoutException(
        $"wait-for {expected}",
        (int)timeout.TotalMilliseconds,
        soFar,
        string.Empty);
  }

  /// <summary>Sitzung beenden (Default: nichts zu tun).</summary>
  public virtual int Exit()
  {
    return 0;
  }

  public abstract void Dispose();

  protected abstract bool HasExited { get; }

  protected abstract int ExitCode { get; }

  /// <summary>Angesammelte Ausgabe erweitern (implementierungsseitig aufzurufen).</summary>
  protected void AppendOutput(string chunk)
  {
    lock (_lock)
    {
      _output.Append(chunk);
    }

    // Live-Mitschnitt außerhalb des Locks — langsames Loggen soll nichts blockieren.
    _log?.Invoke(chunk);
  }
}

/// <summary>
/// Prozessbasierte Session (z. B. Test-Hook mit pwsh): langlaufender Prozess,
/// stdin wird geschrieben, stdout/stderr gepummt. Für echte VM-Shells nicht
/// geeignet (Multipass-Windows-Client forwardet gepipetes stdin nicht).
/// </summary>
public sealed class VmConsoleSession : VmSession
{
  private readonly Process _process;
  private readonly Task _stdOutTask;
  private readonly Task _stdErrTask;

  private VmConsoleSession(Process process, Action<string>? log)
      : base(log)
  {
    _process = process;
    _stdOutTask = Task.Run(() => Pump(process.StandardOutput));
    _stdErrTask = Task.Run(() => Pump(process.StandardError));
  }

  /// <summary>Startet die Session über einen beliebigen Prozess (Test-Hook).</summary>
  public static VmConsoleSession Start(ProcessStartInfo startInfo, Action<string>? log = null)
  {
    Process? started;
    try
    {
      started = Process.Start(startInfo);
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
      throw new MultipassException($"Prozess konnte nicht gestartet werden: {startInfo.FileName}", exception);
    }

    return new VmConsoleSession(
        started ?? throw new MultipassException($"Prozess konnte nicht gestartet werden: {startInfo.FileName}"),
        log);
  }

  public override void Send(string input)
  {
    _process.StandardInput.Write(input);
    _process.StandardInput.Flush();
  }

  /// <summary>Sitzung sauber beenden (exit) und Prozessausgang abwarten.</summary>
  public override int Exit()
  {
    try
    {
      SendLine("exit");
      if (!_process.WaitForExit(10_000))
      {
        TryKill();
      }
    }
    catch (ObjectDisposedException)
    {
    }

    return _process.HasExited ? _process.ExitCode : -1;
  }

  public override void Dispose()
  {
    if (!_process.HasExited)
    {
      TryKill();
    }

    _process.Dispose();
  }

  protected override bool HasExited => _process.HasExited;

  protected override int ExitCode => _process.HasExited ? _process.ExitCode : -1;

  private void Pump(StreamReader reader)
  {
    var buffer = new char[4096];
    try
    {
      int read;
      while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
      {
        AppendOutput(new string(buffer, 0, read));
      }
    }
    catch (Exception)
    {
      // Stream endet mit dem Prozess — kein Fehler.
    }
  }

  private void TryKill()
  {
    try
    {
      _process.Kill(entireProcessTree: true);
      _process.WaitForExit(5_000);
    }
    catch (InvalidOperationException)
    {
    }
    catch (System.ComponentModel.Win32Exception)
    {
    }
  }
}

/// <summary>
/// VM-Session über den exec-Kanal: jeder Befehl ist ein eigenes
/// `multipass exec <vm> -- /bin/sh -c "<cmd>"` (der unter Windows verlässliche
/// Weg). Ausgaben aller Befehle sammeln sich in einem gemeinsamen Verlauf —
/// damit sind Inhalte und Befehle testbar. Kein gemeinsamer Shell-Status
/// (cwd/Umgebungsvariablen) zwischen Befehlen.
/// </summary>
public sealed class VmExecSession : VmSession
{
  private readonly MultipassCli _cli;
  private readonly string _vmName;
  private readonly int _timeoutMs;

  public VmExecSession(string vmName, Action<string>? log = null, int timeoutMs = 600_000, MultipassCli? cli = null)
      : base(log)
  {
    _vmName = vmName;
    _cli = cli ?? new MultipassCli();
    _timeoutMs = timeoutMs;
  }

  public override void Send(string input)
  {
    // Einzelne Zeile aus dem Input ziehen (Roh-Input ohne \n wird ignoriert).
    var command = input.TrimEnd('\n', '\r');
    if (command.Length == 0)
    {
      return;
    }

    var result = _cli.Run(
        new[] { "exec", _vmName, "--", "/bin/sh", "-c", command },
        _timeoutMs);

    AppendOutput(result.StdOut);
    if (!string.IsNullOrWhiteSpace(result.StdErr))
    {
      AppendOutput(result.StdErr);
    }
  }

  public override void Dispose()
  {
  }

  protected override bool HasExited => false; // Session endet nie von selbst.

  protected override int ExitCode => -1;
}
