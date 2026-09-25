using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
public class T03LaunchLatencyTests : MultipassExperiment
{
    private const string Image = "core26";
    private const string ColdVmName = "cbx-t03-a";
    private const string WarmVmName = "cbx-t03-b";
    private const int LaunchTimeoutMs = 2_400_000;

    [MultipassFact]
    public void LaunchMisstKaltUndWarmeLatenz()
    {
        MeasureLaunch(ColdVmName, "kalt (erster Launch des Images, inkl. Download)");
        MeasureLaunch(WarmVmName, "warm (Image aus multipassd-Cache)");

        var names = MultipassVms.List().Select(v => v.Name).ToList();
        Assert.Contains(ColdVmName, names);
        Assert.Contains(WarmVmName, names);
    }

    private void MeasureLaunch(string vmName, string phase)
    {
        var existing = MultipassVms.List().Where(v => !v.IsDeleted).Select(v => v.Name).ToList();
        var existingNote = existing.Count == 0 ? "keine" : string.Join(", ", existing);
        if (existing.Contains(vmName))
        {
            ExperimentLog.Append(
                "T03",
                $"`launch` — {phase}",
                $"launch {Image} --name {vmName}",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, string.Empty, string.Empty, 0),
                $"Launch übersprungen — VM existierte bereits: {vmName}");
            return;
        }

        var result = RunExperiment(
            "T03",
            $"`launch` — {phase}",
            new[] { "launch", Image, "--name", vmName },
            LaunchTimeoutMs,
            r => $"Dauer: {r.DurationMs} ms — VMs vor Launch: [{existingNote}]");
        Assert.Equal(0, result.ExitCode);
    }
}
