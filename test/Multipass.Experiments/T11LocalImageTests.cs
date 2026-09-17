using CoreBridge.Core.Vm;

namespace Multipass.Experiments;

[Collection("MultipassExperiments")]
public class T11LocalImageTests : MultipassExperiment
{
    private const string VmName = "cbx-t11-local";

    private static string ImagePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CoreBridge", "images", "cbx-core26.img");

    private static string? ImageUrl => Environment.GetEnvironmentVariable("CORE_BRIDGE_T11_IMAGE_URL");

    [MultipassFact]
    public void LaunchLokalGebautesCore26Image()
    {
        var names = MultipassVms.List().Where(v => !v.IsDeleted).Select(v => v.Name).ToList();
        if (names.Contains(VmName))
        {
            ExperimentLog.Append(
                "T11",
                "file://-Launch des lokal gebauten core26-Images",
                $"launch {ImageUrl} --name {VmName}",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, string.Empty, string.Empty, 0),
                $"Launch übersprungen — VM existierte bereits: {VmName}");
            return;
        }

        if (ImageUrl is null)
        {
            ExperimentLog.Append(
                "T11",
                "Custom-Image-Launch des lokal gebauten core26-Images",
                $"launch <image-url> --name {VmName}",
                MultipassBinaryLocator.LocateOrNull(),
                new MultipassRunResult(0, string.Empty, "Keine Image-URL", 0),
                "Übersprungen — CORE_BRIDGE_T11_IMAGE_URL nicht gesetzt. Hinweis: Windows-file:// ist in "
                + "multipass 1.16.3 unbrauchbar (Client mangled Laufwerkspfad, Daemon verwirft host != leer) — "
                + "Image daher per http:// (z. B. `python -m http.server`) servieren.");
            return;
        }

        var result = RunExperiment(
            "T11",
            "Custom-Image-Launch des lokal gebauten core26-Images (dangerous-Modell, Canonical-signiert)",
            new[] { "launch", ImageUrl, "--name", VmName },
            2_400_000,
            r => $"Dauer: {r.DurationMs} ms — URL: {ImageUrl} (lokal: {ImagePath})");
        Assert.Equal(0, result.ExitCode);

        var vm = MultipassVms.List().SingleOrDefault(v => v.Name == VmName);
        Assert.NotNull(vm);
        ExperimentLog.Append(
            "T11",
            "Zustand nach Launch",
            "list --format json (Auszug)",
            MultipassBinaryLocator.LocateOrNull(),
            new MultipassRunResult(0, $"state: {vm.State}; ipv4: {string.Join(", ", vm.Ipv4)}", string.Empty, 0),
            $"State: {vm.State ?? "unbekannt"}, IPv4: [{string.Join(", ", vm.Ipv4)}]");
        Assert.True(
            string.Equals(vm.State, "Running", StringComparison.OrdinalIgnoreCase),
            $"VM nicht Running: {vm.State}");

        var probe = new MultipassCli().Run(
            new[] { "exec", VmName, "--", "/bin/sh", "-c", "echo t11-ok; snap list 2>/dev/null | head -8" },
            120_000);
        ExperimentLog.Append(
            "T11",
            "Exec-Probe in der lokalen Image-VM",
            $"exec {VmName} -- /bin/sh -c 'echo t11-ok; snap list'",
            MultipassBinaryLocator.LocateOrNull(),
            probe,
            $"Dauer: {probe.DurationMs} ms");
        Assert.Equal(0, probe.ExitCode);
        Assert.Contains("t11-ok", probe.StdOut);
    }
}
