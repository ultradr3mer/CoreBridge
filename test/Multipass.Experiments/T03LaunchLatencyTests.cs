using System.Text.Json;
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

        var names = ListVmNames();
        Assert.Contains(ColdVmName, names);
        Assert.Contains(WarmVmName, names);
    }

    private void MeasureLaunch(string vmName, string phase)
    {
        var existing = ListVmNames();
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

    private static List<string> ListVmNames()
    {
        var result = new MultipassCli().Run(new[] { "list", "--format", "json" }, 60_000);
        Assert.True(result.ExitCode == 0, $"multipass list scheiterte: {result.StdErr.Trim()} / {result.StdOut.Trim()}");

        var names = new List<string>();
        using var document = JsonDocument.Parse(result.StdOut);
        var list = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.TryGetProperty("list", out var property) && property.ValueKind == JsonValueKind.Array
                ? property
                : default;

        if (list.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var vm in list.EnumerateArray())
        {
            if (vm.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                names.Add(name.GetString()!);
            }
        }

        return names;
    }
}
