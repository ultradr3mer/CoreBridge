using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace CoreBridge.Core.Vm;

/// <summary>Zustand eines Windows-Dienstes laut <c>sc.exe queryex</c>.</summary>
public enum MultipassServiceState
{
    Unknown,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused
}

/// <summary>
/// Steuert den Windows-Dienst <c>Multipass</c> (Prozess <c>multipassd.exe</c>)
/// über <c>sc.exe</c>. Alle Eingriffe sind <c>virtual</c>, damit Tests den
/// Ablauf von <see cref="Restart"/> ohne echten Dienst prüfen können.
/// </summary>
public class MultipassService
{
    public const string ServiceName = "Multipass";

    public const string DaemonProcessName = "multipassd";

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _killTimeout;

    /// <param name="pollInterval">Abstand der Zustandsabfragen (Standard 500 ms).</param>
    /// <param name="killTimeout">Wartezeit auf <c>Stopped</c> nach dem harten Beenden (Standard 15 s).</param>
    public MultipassService(TimeSpan? pollInterval = null, TimeSpan? killTimeout = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _killTimeout = killTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Startet den Dienst neu wie <c>Restart-Service Multipass -Force</c>: Stop
    /// anfordern und bis <paramref name="stopTimeout"/> warten. Reagiert der
    /// Dienst nicht, wird über <paramref name="confirm"/> gefragt, ob der
    /// Daemon-Prozess (PID laut Dienst, wie <c>Win32_Service.ProcessId</c>) hart
    /// beendet werden soll (<c>Stop-Process -Id &lt;PID&gt; -Force</c>). Danach
    /// starten und warten, bis der Daemon wieder antwortet. Liefert
    /// Fortschrittsmeldungen, sobald sie entstehen; wirft
    /// <see cref="MultipassException"/>, wenn ein Schritt scheitert oder das
    /// Beenden abgelehnt wird.
    /// </summary>
    /// <param name="confirm">Ja/Nein-Rückfrage an den Benutzer; <c>true</c> = Ja.</param>
    public async IAsyncEnumerable<string> Restart(
        TimeSpan stopTimeout,
        TimeSpan startTimeout,
        Func<string, bool> confirm,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
        {
            throw new MultipassException(
                $"Neustart des Dienstes {ServiceName} benötigt Administratorrechte — PowerShell als Administrator starten.");
        }

        var state = QueryState();
        yield return $"Service {ServiceName}: {state}";

        if (state != MultipassServiceState.Stopped)
        {
            if (state != MultipassServiceState.StopPending)
            {
                yield return "Stoppe Service ...";
                RequestStop();
            }

            if (!await WaitForStateAsync(MultipassServiceState.Stopped, stopTimeout, cancellationToken))
            {
                var processId = QueryProcessId();
                if (processId == 0)
                {
                    throw new MultipassException(
                        $"Service {ServiceName} reagiert nicht ({QueryState()}), meldet aber keinen Prozess — Rechner neu starten.");
                }

                yield return $"Service reagiert nach {stopTimeout.TotalSeconds:F0} s nicht ({QueryState()}).";
                if (!confirm($"Daemon-Prozess {DaemonProcessName} (PID {processId}) hart beenden?"))
                {
                    throw new MultipassException(
                        $"Abgebrochen — {DaemonProcessName} (PID {processId}) wurde nicht beendet, Service bleibt in {QueryState()}.");
                }

                KillProcess(processId);
                yield return $"Beendet: PID {processId}";

                if (!await WaitForStateAsync(MultipassServiceState.Stopped, _killTimeout, cancellationToken))
                {
                    throw new MultipassException(
                        $"Service {ServiceName} bleibt in {QueryState()}, auch nach dem Beenden von {DaemonProcessName}. "
                        + "Hängende Hyper-V-VMs mit `Stop-VM -TurnOff` ausschalten oder den Rechner neu starten.");
                }
            }

            yield return "Service gestoppt.";
        }

        yield return "Starte Service ...";
        RequestStart();
        if (!await WaitForStateAsync(MultipassServiceState.Running, startTimeout, cancellationToken))
        {
            throw new MultipassException(
                $"Service {ServiceName} ist nach {startTimeout.TotalSeconds:F0} s nicht Running ({QueryState()}).");
        }

        yield return "Warte auf Daemon ...";
        if (!await WaitUntilAsync(IsDaemonResponding, startTimeout, cancellationToken))
        {
            throw new MultipassException(
                $"Service {ServiceName} läuft, aber der Daemon antwortet nach {startTimeout.TotalSeconds:F0} s nicht auf `multipass version`.");
        }
    }

    public virtual bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public virtual MultipassServiceState QueryState()
    {
        var (exitCode, output) = RunSc("queryex", ServiceName);
        if (exitCode != 0)
        {
            throw new MultipassException($"sc queryex {ServiceName} beendet mit Exit {exitCode}: {output.Trim()}");
        }

        return ParseState(output);
    }

    /// <summary>Fordert den Stop an; „nicht gestartet“ (1062) und „nimmt keine Steuerbefehle an“ (1061) sind kein Fehler.</summary>
    public virtual void RequestStop()
    {
        var (exitCode, output) = RunSc("stop", ServiceName);
        if (exitCode is not (0 or 1061 or 1062))
        {
            throw new MultipassException($"sc stop {ServiceName} beendet mit Exit {exitCode}: {output.Trim()}");
        }
    }

    /// <summary>Startet den Dienst; „läuft bereits“ (1056) ist kein Fehler.</summary>
    public virtual void RequestStart()
    {
        var (exitCode, output) = RunSc("start", ServiceName);
        if (exitCode is not (0 or 1056))
        {
            throw new MultipassException($"sc start {ServiceName} beendet mit Exit {exitCode}: {output.Trim()}");
        }
    }

    /// <summary>PID des Dienstprozesses laut <c>sc queryex</c> (entspricht <c>Win32_Service.ProcessId</c>), 0 wenn keiner läuft.</summary>
    public virtual int QueryProcessId()
    {
        var (exitCode, output) = RunSc("queryex", ServiceName);
        if (exitCode != 0)
        {
            throw new MultipassException($"sc queryex {ServiceName} beendet mit Exit {exitCode}: {output.Trim()}");
        }

        return ParseProcessId(output);
    }

    /// <summary>Beendet den Prozess hart (wie <c>Stop-Process -Id &lt;PID&gt; -Force</c>).</summary>
    public virtual void KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (ArgumentException)
        {
            // Prozess war bereits beendet.
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MultipassException(
                $"{DaemonProcessName} (PID {processId}) konnte nicht beendet werden: {exception.Message}",
                exception);
        }
    }

    public virtual bool IsDaemonResponding()
    {
        return Doctor.Check().DaemonVersion is not null;
    }

    /// <summary>Liest die PID aus der <c>PID</c>-Zeile von <c>sc.exe queryex</c>, 0 wenn keine.</summary>
    public static int ParseProcessId(string scOutput)
    {
        var match = Regex.Match(scOutput, @"^\s*PID\s*:\s*(\d+)", RegexOptions.Multiline);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>Liest den Zustand aus der <c>STATE</c>-Zeile von <c>sc.exe query[ex]</c>.</summary>
    public static MultipassServiceState ParseState(string scOutput)
    {
        var match = Regex.Match(
            scOutput,
            @"\b\d+\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b");
        return match.Success
            ? match.Groups[1].Value switch
            {
                "STOPPED" => MultipassServiceState.Stopped,
                "START_PENDING" => MultipassServiceState.StartPending,
                "STOP_PENDING" => MultipassServiceState.StopPending,
                "RUNNING" => MultipassServiceState.Running,
                "CONTINUE_PENDING" => MultipassServiceState.ContinuePending,
                "PAUSE_PENDING" => MultipassServiceState.PausePending,
                _ => MultipassServiceState.Paused
            }
            : MultipassServiceState.Unknown;
    }

    private Task<bool> WaitForStateAsync(MultipassServiceState expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return WaitUntilAsync(() => QueryState() == expected, timeout, cancellationToken);
    }

    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (await Task.Run(condition, cancellationToken))
            {
                return true;
            }

            if (stopwatch.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private static (int ExitCode, string Output) RunSc(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new MultipassException("sc.exe konnte nicht gestartet werden.");
        var stdErrTask = process.StandardError.ReadToEndAsync();
        var stdOut = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new MultipassException($"sc {string.Join(' ', args)} hat nicht innerhalb von 30 s geantwortet.");
        }

        return (process.ExitCode, stdOut + stdErrTask.GetAwaiter().GetResult());
    }
}
