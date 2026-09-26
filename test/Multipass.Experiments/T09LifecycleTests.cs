using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
[Trait("Category", "Integration")]
public class T09LifecycleTests : MultipassExperiment
{
    private const string Vm = "cbx-t03-a";

    [MultipassFact]
    public void StopStartDeleteMisstDauernUndZustaende()
    {
        var before = MultipassVms.List();
        var target = before.FirstOrDefault(v => v.Name == Vm && !v.IsDeleted);
        if (target is null)
        {
            ExperimentLog.Append(
                "T09",
                "Lifecycle — stop/start/delete",
                $"stop/start/delete {Vm}",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, string.Empty, string.Empty, 0),
                $"Übersprungen — VM existiert nicht (oder nur im Papierkorb): {Vm}");
            return;
        }

        var stop = RunExperiment(
            "T09",
            "`stop`",
            new[] { "stop", Vm },
            180_000,
            r => $"Dauer: {r.DurationMs} ms");
        Assert.Equal(0, stop.ExitCode);
        var afterStop = MultipassVms.List().Single(v => v.Name == Vm);
        ExperimentLog.Append(
            "T09",
            "Zustand nach `stop`",
            "list --format json (Auszug)",
            MultipassBinaryLocator.LocateOrNull(),
            new MultipassRunResult(0, $"state: {afterStop.State}", string.Empty, 0),
            $"State: {afterStop.State ?? "unbekannt"}");
        Assert.False(
            string.Equals(afterStop.State, "Running", StringComparison.OrdinalIgnoreCase),
            $"State nach stop ist immer noch Running: {afterStop.State}");

        var start = RunExperiment(
            "T09",
            "`start`",
            new[] { "start", Vm },
            180_000,
            r => $"Dauer: {r.DurationMs} ms");
        Assert.Equal(0, start.ExitCode);
        var afterStart = MultipassVms.List().Single(v => v.Name == Vm);
        ExperimentLog.Append(
            "T09",
            "Zustand nach `start`",
            "list --format json (Auszug)",
            MultipassBinaryLocator.LocateOrNull(),
            new MultipassRunResult(0, $"state: {afterStart.State}", string.Empty, 0),
            $"State: {afterStart.State ?? "unbekannt"}");
        Assert.True(
            string.Equals(afterStart.State, "Running", StringComparison.OrdinalIgnoreCase),
            $"State nach start ist nicht Running: {afterStart.State}");

        var delete = RunExperiment(
            "T09",
            "`delete` — in den Papierkorb (kein purge)",
            new[] { "delete", Vm },
            180_000,
            r => $"Dauer: {r.DurationMs} ms");
        Assert.Equal(0, delete.ExitCode);
        var afterDelete = MultipassVms.List().Where(v => v.Name == Vm).ToList();
        var deleted = afterDelete.All(v => v.IsDeleted);
        ExperimentLog.Append(
            "T09",
            "Zustand nach `delete`",
            "list --format json (Auszug)",
            MultipassBinaryLocator.LocateOrNull(),
            new MultipassRunResult(0, $"state: {afterDelete.FirstOrDefault()?.State ?? "abwesend"}", string.Empty, 0),
            deleted
                ? $"{Vm} im Papierkorb (state Deleted, nicht gepurgt)."
                : $"{Vm} unerwartet noch aktiv: {afterDelete.FirstOrDefault()?.State}");
        Assert.True(deleted, $"State nach delete nicht Deleted: {afterDelete.FirstOrDefault()?.State}");
    }
}
