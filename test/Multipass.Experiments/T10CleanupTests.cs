using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
public class T10CleanupTests : MultipassExperiment
{
    private const string Prefix = "cbx-";

    [MultipassFact]
    public void CleanupLoeschtNurCbxVms()
    {
        var before = MultipassVms.List();
        var others = before.Where(v => !v.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).Select(v => v.Name).ToList();
        var trashed = before
            .Where(v => v.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && v.IsDeleted)
            .Select(v => v.Name)
            .ToList();
        var cbxVms = before
            .Where(v => v.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && !v.IsDeleted)
            .Select(v => v.Name)
            .ToList();

        if (cbxVms.Count == 0)
        {
            ExperimentLog.Append(
                "T10",
                "Cleanup — löscht nur cbx-*",
                "delete <cbx-*> (keine aktiv vorhanden)",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, string.Empty, string.Empty, 0),
                cbxVms.Count == 0 && trashed.Count > 0
                    ? $"Nichts zu löschen — bereits im Papierkorb: {string.Join(", ", trashed)}."
                    : "Nichts zu tun — keine cbx-VMs vorhanden.");
            return;
        }

        foreach (var vm in cbxVms)
        {
            var delete = RunExperiment(
                "T10",
                $"`delete` — {vm}",
                new[] { "delete", vm },
                180_000,
                r => $"Dauer: {r.DurationMs} ms");
            Assert.Equal(0, delete.ExitCode);
        }

        var after = MultipassVms.List();
        var activeCbx = after
            .Where(v => v.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && !v.IsDeleted)
            .Select(v => v.Name)
            .ToList();
        ExperimentLog.Append(
            "T10",
            "Cleanup — Ergebnis",
            "list --format json (Auszug)",
            MultipassBinaryLocator.LocateOrNull(),
            new MultipassRunResult(0, $"aktiv übrig: {(activeCbx.Count == 0 ? "keine" : string.Join(", ", activeCbx))}", string.Empty, 0),
            $"Gelöscht: {string.Join(", ", cbxVms)}. Nicht-cbx-VMs unangetastet: {(others.Count == 0 ? "keine vorhanden" : string.Join(", ", others))}. "
            + (trashed.Count > 0 ? $"Bereits im Papierkorb (bleiben dort): {string.Join(", ", trashed)}. " : string.Empty)
            + "Papierkorb nicht gepurgt (Regel: kein blindes purge).");
        Assert.Empty(activeCbx);
    }
}
