using System.Diagnostics;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
public class T06CloudInitTests : MultipassExperiment
{
    private const string Vm = "cbx-t06";
    private const string Image = "core26";
    private const string MarkerPath = "/tmp/cbx-t06-marker";
    private const string Marker = "cbx-t06-cloud-init-ok";
    private const int LaunchTimeoutMs = 2_400_000;

    [MultipassFact]
    public void CloudInitWriteFilesWirdAusgefuehrt()
    {
        var names = MultipassVms.List().Select(v => v.Name).ToList();
        if (names.Contains(Vm))
        {
            ExperimentLog.Append(
                "T06",
                "`launch --cloud-init` — write_files",
                $"launch --name {Vm} --cloud-init <yaml> {Image}",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, string.Empty, string.Empty, 0),
                $"Launch übersprungen — VM existierte bereits: {Vm}");
            return;
        }

        var yamlPath = Path.Combine(Path.GetTempPath(), "cbx-t06-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(
            yamlPath,
            "#cloud-config\n"
            + "write_files:\n"
            + $"  - path: {MarkerPath}\n"
            + "    content: |\n"
            + $"      {Marker}\n");
        try
        {
            var launch = RunExperiment(
                "T06",
                "`launch --cloud-init` — write_files",
                new[] { "launch", "--name", Vm, "--cloud-init", yamlPath, Image },
                LaunchTimeoutMs,
                r => $"Dauer: {r.DurationMs} ms");
            Assert.Equal(0, launch.ExitCode);

            var (found, waitMs, detail) = WaitForMarker();
            ExperimentLog.Append(
                "T06",
                "Marker-Polling — cloud-init async?",
                $"exec {Vm} -- /bin/sh -c 'cat {MarkerPath} 2>/dev/null || echo marker-fehlt' (im 5-s-Takt)",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, detail, string.Empty, waitMs),
                found
                    ? $"write_files auf core26 OK — Marker nach {waitMs} ms sichtbar."
                    : $"write_files auf core26 NICHT sichtbar geworden ({waitMs} ms gewartet).");

            var after = MultipassVms.List().Select(v => v.Name).ToList();
            Assert.Contains(Vm, after);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    private static (bool Found, long WaitMs, string Detail) WaitForMarker()
    {
        var cli = new MultipassCli();
        var stopwatch = Stopwatch.StartNew();
        MultipassRunResult last = new(0, string.Empty, string.Empty, 0);
        for (var attempt = 1; attempt <= 24; attempt++)
        {
            last = cli.Run(
                new[] { "exec", Vm, "--", "/bin/sh", "-c", $"cat {MarkerPath} 2>/dev/null || echo marker-fehlt" },
                60_000);
            if (last.StdOut.Contains(Marker))
            {
                return (true, stopwatch.ElapsedMilliseconds, $"Versuch {attempt}: {last.StdOut.Trim()}");
            }

            Thread.Sleep(5_000);
        }

        return (false, stopwatch.ElapsedMilliseconds, $"Letzter Versuch: {last.StdOut.Trim()} / {last.StdErr.Trim()}");
    }
}
