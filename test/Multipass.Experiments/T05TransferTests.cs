using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
[Trait("Category", "Integration")]
public class T05TransferTests : MultipassExperiment
{
    private const string Vm = "cbx-t03-b";
    private const string RemotePath = "/tmp/cbx-t05-payload.txt";

    [MultipassFact]
    public void TransferRoundtripHaeltInhalt()
    {
        var names = MultipassVms.List().Select(v => v.Name).ToList();
        Assert.Contains(Vm, names);

        var payload = "cbx-t05-payload-" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "cbx-t05-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var localIn = Path.Combine(directory, "payload.txt");
            File.WriteAllText(localIn, payload);

            var intoVm = RunExperiment(
                "T05",
                "`transfer` — Host -> VM",
                new[] { "transfer", localIn, $"{Vm}:{RemotePath}" },
                120_000,
                r => $"Dauer: {r.DurationMs} ms");
            Assert.Equal(0, intoVm.ExitCode);

            var cli = new MultipassCli();
            var cat = cli.Run(
                new[] { "exec", Vm, "--", "/bin/sh", "-c", $"cat {RemotePath}" },
                60_000);
            ExperimentLog.Append(
                "T05",
                "`exec cat` — Inhalt in der VM prüfen",
                $"exec {Vm} -- /bin/sh -c 'cat {RemotePath}'",
                MultipassBinaryLocator.LocateOrNull(),
                cat,
                $"Dauer: {cat.DurationMs} ms");
            Assert.Equal(0, cat.ExitCode);
            Assert.Contains(payload, cat.StdOut);

            var localOut = Path.Combine(directory, "payload-out.txt");
            var outOfVm = RunExperiment(
                "T05",
                "`transfer` — VM -> Host",
                new[] { "transfer", $"{Vm}:{RemotePath}", localOut },
                120_000,
                r => $"Dauer: {r.DurationMs} ms");
            var fileOk = File.Exists(localOut) && File.ReadAllText(localOut).Trim() == payload;
            ExperimentLog.Append(
                "T05",
                "`transfer` VM -> Host — Bewertung",
                $"transfer {Vm}:{RemotePath} <lokal>",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(outOfVm.ExitCode, fileOk ? "Datei korrekt angekommen" : "Datei fehlt oder abweichend", outOfVm.StdErr.Trim(), outOfVm.DurationMs),
                fileOk
                    ? $"Exit-Code {outOfVm.ExitCode} (sftp-Chmod-Fehler auf Windows), Inhalt aber korrekt — Sync-Weg nutzbar."
                    : $"Exit-Code {outOfVm.ExitCode}, Datei nicht korrekt — VM -> HostTransfer unbrauchbar.");
            Assert.True(fileOk, $"VM -> Host fehlgeschlagen: exit {outOfVm.ExitCode}, stderr: {outOfVm.StdErr.Trim()}");

            var cleanup = cli.Run(
                new[] { "exec", Vm, "--", "/bin/sh", "-c", $"rm -f {RemotePath}" },
                60_000);
            ExperimentLog.Append(
                "T05",
                "`exec rm` — VM-seitig aufräumen",
                $"exec {Vm} -- /bin/sh -c 'rm -f {RemotePath}'",
                MultipassBinaryLocator.LocateOrNull(),
                cleanup,
                $"Dauer: {cleanup.DurationMs} ms");
            Assert.Equal(0, cleanup.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
