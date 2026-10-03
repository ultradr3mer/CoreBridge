using CoreBridge.Core.Common;

namespace CoreBridge.Simulation;

/// <summary>
/// Kommandozeile von <c>multipass-sim.exe</c>: dieselben Argumente und Ausgaben wie
/// <c>multipass.exe</c>, ausgeführt von <see cref="SimulatedMultipassCli"/>. So lässt sich
/// der Simulator von Hand prüfen und als Binary hinter das echte <c>MultipassCli</c>
/// stellen (<c>CORE_BRIDGE_MULTIPASS_EXE</c>) — dann läuft auch der Prozesspfad.
/// </summary>
/// <remarks>
/// Zustand standardmäßig in <c>&lt;Repo&gt;/artifacts/multipass-sim/state.json</c> — derselbe
/// wie bei <c>cb-sim</c>, also sehen beide dieselben VMs. <see cref="StateVariable"/> und
/// <see cref="DelayVariable"/> überschreiben Ort und Dauer der langen Verben.
/// </remarks>
public static class MultipassSimCommandLine
{
    public const string StateVariable = "CORE_BRIDGE_SIM_STATE";

    public const string DelayVariable = "CORE_BRIDGE_SIM_DELAY";

    /// <summary>Wie <c>MultipassSimulationDelay</c> in <c>appsettings.Simulation.json</c>.</summary>
    public const string DefaultDelay = "4s";

    /// <summary>Markerzeile, mit der jede Shell-Zeile ihr Arbeitsverzeichnis meldet (wie <c>VmExecSession</c>).</summary>
    private const string CwdMarker = "__corebridge_cwd__=";

    private const string Usage = """
        Usage: multipass-sim <command> [options]
        Multipass-Simulator (CoreBridge) — Kommandozeile wie multipass.exe, keine echten VMs.

        Available commands:
          clone     Clone a stopped instance
          delete    Delete instances and snapshots (--purge)
          exec      Run a command on an instance: exec <vm> -- <cmd> [args]
          get       Get local.<vm>.disk
          info      Display information about instances or snapshots
          launch    Create and start an instance: launch [image] --name <vm> [--disk 10G] [--cloud-init f]
          list      List instances (--snapshots, --format table|json|csv)
          purge     Purge all deleted instances
          recover   Recover deleted instances
          restart   Restart instances
          restore   Restore an instance from a snapshot: restore <vm>.<snapshot>
          set       Set local.<vm>.disk=<size>
          shell     Open a (simulated) shell on an instance
          snapshot  Take a snapshot of a stopped instance (--name, --comment)
          start     Start instances
          stop      Stop instances
          transfer  Transfer files: transfer <src> <vm>:<dst> | <vm>:<src> <dst>
          version   Show version details (--format json)

        Environment:
          CORE_BRIDGE_SIM_STATE  state file (default <repo>/artifacts/multipass-sim/state.json, shared with cb-sim)
          CORE_BRIDGE_SIM_DELAY  duration of launch/start/stop/restart/clone (default 4s, e.g. 0ms)

        """;

    /// <summary>Einstieg von <c>multipass-sim.exe</c>: Zustand und Dauer aus der Umgebung.</summary>
    public static int Main(string[] args)
    {
        var state = Environment.GetEnvironmentVariable(StateVariable);
        var delay = Environment.GetEnvironmentVariable(DelayVariable);
        return Run(
            args,
            Console.In,
            Console.Out,
            Console.Error,
            interactive: !Console.IsInputRedirected,
            string.IsNullOrWhiteSpace(state) ? DefaultStatePath() : Path.GetFullPath(state),
            (string.IsNullOrWhiteSpace(delay) ? DefaultDelay : delay).ReadTime());
    }

    public static string DefaultStatePath() =>
        Path.Combine(RepoPaths.Resolve("artifacts"), SimulatedMultipassCli.FolderName, SimulatedMultipassCli.StateFileName);

    public static int Run(
        IReadOnlyList<string> args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool interactive,
        string statePath,
        TimeSpan delay)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.Write(Usage.Replace("\r\n", "\n"));
            return 0;
        }

        var simulator = SimulatedMultipassCli.At(statePath, delay);
        if (args[0] is "shell" or "sh")
        {
            return Shell(simulator, args.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "primary", input, output, error, interactive);
        }

        // Kein Timeout: der echte Client wartet auch, bis der Daemon fertig ist.
        var result = simulator.Run(args, int.MaxValue);
        output.Write(result.StdOut);
        error.Write(result.StdErr);
        return result.ExitCode;
    }

    /// <summary>
    /// <c>multipass shell</c>: startet eine gestoppte VM wie das Original und liest dann
    /// Zeilen (mit Prompt, wenn interaktiv), jede als eigenes exec; das Arbeitsverzeichnis
    /// wandert mit, Variablen nicht. <c>exit</c>/<c>logout</c> oder Eingabeende beendet.
    /// </summary>
    private static int Shell(SimulatedMultipassCli simulator, string name, TextReader input, TextWriter output, TextWriter error, bool interactive)
    {
        var probe = simulator.Run(["exec", name, "--", "true"]);
        if (probe.ExitCode != 0)
        {
            if (!probe.StdErr.Contains("is not running", StringComparison.Ordinal))
            {
                error.Write(probe.StdErr.Replace("exec failed", "shell failed", StringComparison.Ordinal));
                return probe.ExitCode;
            }

            output.Write($"Starting {name}\n");
            var start = simulator.Run(["start", name], int.MaxValue);
            if (start.ExitCode != 0)
            {
                error.Write(start.StdErr);
                return start.ExitCode;
            }
        }

        string? cwd = null;
        while (true)
        {
            if (interactive)
            {
                output.Write($"ubuntu@{name}:{Display(cwd)}$ ");
                output.Flush();
            }

            var line = input.ReadLine();
            if (line is null || line.Trim() is "exit" or "logout")
            {
                break;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            var cd = cwd is null ? string.Empty : $"cd {CoreBridge.Core.Common.Shell.Quote(cwd)} || exit 1\n";
            var script = $"{cd}{line}\n__rc=$?\nprintf '\\n{CwdMarker}%s\\n' \"$(pwd)\"\nexit $__rc";
            var result = simulator.Run(["exec", name, "--", "/bin/sh", "-c", script]);
            var marker = result.StdOut.LastIndexOf("\n" + CwdMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                var reported = result.StdOut[(marker + 1 + CwdMarker.Length)..].TrimEnd('\r', '\n');
                cwd = reported.Length > 0 ? reported : cwd;
                output.Write(result.StdOut[..marker]);
            }
            else
            {
                output.Write(result.StdOut);
            }

            error.Write(result.StdErr);
        }

        return 0;
    }

    private static string Display(string? cwd) => cwd switch
    {
        null or SimShell.Home => "~",
        _ when cwd.StartsWith(SimShell.Home + "/", StringComparison.Ordinal) => "~" + cwd[SimShell.Home.Length..],
        _ => cwd
    };
}
