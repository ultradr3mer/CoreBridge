using System.Diagnostics;
using System.Text;

namespace CoreBridge.Core.Vm;

/// <summary>
/// Emulierte Konsole über `multipass shell <vm>`: langlaufender Prozess mit
/// umgeleiteten stdin/stdout/stderr. Befehle werden per <see cref="SendLine"/>
/// geschrieben, Ausgaben über <see cref="WaitFor(string, TimeSpan)"/> geprüft —
/// damit sind Shell-Inhalte und -Befehle testbar, ohne ein echtes Terminal.
/// Hinweis: ohne TTY echoed die VM-Shell Eingaben nicht zurück — Assertions
/// immer auf die Ausgabe des Befehls beziehen, nicht auf den Befehlstext.
/// </summary>
public sealed class VmConsoleSession : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly object _lock = new();
    private readonly Task _stdOutTask;
    private readonly Task _stdErrTask;

    private VmConsoleSession(Process process)
    {
        _process = process;
        _stdOutTask = Task.Run(() => Pump(process.StandardOutput, _output));
        _stdErrTask = Task.Run(() => Pump(process.StandardError, _output));
    }

    /// <summary>Öffnet eine Shell-Sitzung auf der VM (multipass shell).</summary>
    public static VmConsoleSession Open(string vmName, string? exePath = null)
    {
        var binary = exePath ?? MultipassBinaryLocator.LocateOrNull() ?? throw new MultipassNotFoundException();
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("shell");
        startInfo.ArgumentList.Add(vmName);

        return Start(startInfo);
    }

    /// <summary>Startet die Session über einen beliebigen Prozess (Test-Hook).</summary>
    public static VmConsoleSession Start(ProcessStartInfo startInfo)
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
            started ?? throw new MultipassException($"Prozess konnte nicht gestartet werden: {startInfo.FileName}"));
    }

    /// <summary>Roh-Eingabe in die Shell (kein Zeilenumbruch).</summary>
    public void Send(string input)
    {
        _process.StandardInput.Write(input);
        _process.StandardInput.Flush();
    }

    /// <summary>Befehl senden (inkl. Zeilenumbruch).</summary>
    public void SendLine(string command)
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
    /// auftaucht, und liefert alles bisher Gepumpte zurück (auch stderr).
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

            if (_process.HasExited)
            {
                throw new MultipassException(
                    $"Shell-Prozess ist vorher beendet (exit {_process.ExitCode}) — gesucht wurde: {expected}");
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

    /// <summary>Sitzung auf der VM sauber beenden (exit) und Prozessausgang abwarten.</summary>
    public int Exit()
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

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            TryKill();
        }

        _process.Dispose();
    }

    private void Pump(StreamReader reader, StringBuilder target)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                lock (_lock)
                {
                    target.Append(buffer, 0, read);
                }
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
