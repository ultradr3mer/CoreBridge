using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
public class T04ExecTests : MultipassExperiment
{
    private const string Vm = "cbx-t03-a";

    [MultipassFact]
    public void ExecFuehrtKommandosAusUndReichtExitCodeWeiter()
    {
        var names = MultipassVms.List().Select(v => v.Name).ToList();
        Assert.Contains(Vm, names);

        var cli = new MultipassCli();
        var shell = cli.Run(new[] { "exec", Vm, "--", "/bin/sh", "-c", "echo shell-ok" }, 60_000);
        ExperimentLog.Append(
            "T04",
            "`exec` — /bin/sh Verfügbarkeit",
            $"exec {Vm} -- /bin/sh -c 'echo shell-ok'",
            MultipassBinaryLocator.LocateOrNull(),
            shell,
            $"Dauer: {shell.DurationMs} ms");
        if (shell.ExitCode != 0)
        {
            Assert.Fail(
                $"Kein Shell-Zugriff auf {Vm} (exit {shell.ExitCode}): {shell.StdOut.Trim()} / {shell.StdErr.Trim()}");
        }

        var stdoutResult = RunExperiment(
            "T04",
            "`exec` — stdout/stderr getrennt",
            new[] { "exec", Vm, "--", "/bin/sh", "-c", "echo corebridge-t04-stdout; echo corebridge-t04-stderr 1>&2" },
            60_000,
            r => $"Dauer: {r.DurationMs} ms");
        Assert.Equal(0, stdoutResult.ExitCode);
        Assert.Contains("corebridge-t04-stdout", stdoutResult.StdOut);
        Assert.Contains("corebridge-t04-stderr", stdoutResult.StdErr);

        var exitResult = RunExperiment(
            "T04",
            "`exec` — Exit-Code-Weitergabe",
            new[] { "exec", Vm, "--", "/bin/sh", "-c", "exit 42" },
            60_000,
            r => $"Dauer: {r.DurationMs} ms");
        Assert.Equal(42, exitResult.ExitCode);

        RunExperiment(
            "T04",
            "`exec` — Umgebung (Kernel, Default-User)",
            new[] { "exec", Vm, "--", "/bin/sh", "-c", "uname -a; id -un 2>/dev/null || echo user-unbekannt" },
            60_000,
            r => $"Dauer: {r.DurationMs} ms");
    }
}
