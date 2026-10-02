using System.Text.Json;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;
using CoreBridge.Simulation;

namespace CoreBridge.Core.Tests;

/// <summary>
/// <c>vm-install</c> soll in eine laufende VM dasselbe bringen wie cloud-init beim
/// Anlegen — geprüft an den ausgelieferten Komponenten und am Simulator.
/// </summary>
public class InitInstallTests
{
    [Fact]
    public void PlanFolgtDerCloudInitReihenfolge()
    {
        var steps = InitInstall.Plan(LoadShippedImagesConfig(), ["python", "dotnet"]);
        var titles = steps.Select(s => s.Title).ToArray();

        Assert.StartsWith("warten, bis cloud-init", titles[0]);
        Assert.Equal("Paketquellen aktualisieren", titles[1]);
        Assert.StartsWith("Pakete installieren (bash, ca-certificates, curl, sudo)", titles[2]);
        Assert.Equal("Snap dotnet-sdk-100 installieren", titles[3]);
        Assert.Contains("sudo snap install dotnet-sdk-100 --classic", steps[3].Command);

        // Je Skript-Komponente: hochladen, installieren, ausführen, Marker prüfen.
        Assert.Equal(
            new[]
            {
                "python: Skript hochladen",
                "python: Skript nach /usr/local/sbin/corebridge-install-python installieren",
                "python: Skript ausführen (als ubuntu)",
                "python: Marker /var/lib/corebridge/python.ready prüfen",
                "dotnet: Skript hochladen"
            },
            titles[4..9]);
        Assert.True(steps[4].IsUpload);
        Assert.Equal("/tmp/corebridge-install-python.sh", steps[4].RemotePath);
        Assert.Equal("sudo /usr/local/sbin/corebridge-install-python ubuntu", steps[6].Command);
        Assert.Equal("/var/lib/corebridge/python.ready", steps[7].Marker);
    }

    /// <summary>
    /// vm-create prüft nach cloud-init genau diese Marker: einer je Komponente mit
    /// Skript, in Profilreihenfolge; reine Snap-Komponenten (opencode) setzen keinen.
    /// </summary>
    [Fact]
    public void MarkerNurFuerKomponentenMitSkript()
    {
        var markers = InitInstall.Markers(LoadShippedImagesConfig(), ["claude", "opencode", "dotnet"]);

        Assert.Equal(
            new[]
            {
                ("claude", "/var/lib/corebridge/claude.ready"),
                ("dotnet", "/var/lib/corebridge/dotnet.ready")
            },
            markers);
        Assert.Equal("test -f /var/lib/corebridge/claude.ready", InitInstall.MarkerProbe(markers[0].Marker));
    }

    [Fact]
    public void UnbekannteKomponenteNenntDieBekannten()
    {
        var exception = Assert.Throws<MultipassException>(() => InitInstall.Plan(LoadShippedImagesConfig(), ["pyton"]));

        Assert.Contains("pyton", exception.Message);
        Assert.Contains("python", exception.Message);
    }

    [Fact]
    public void SkriptWirdMitLfHochgeladen()
    {
        using var temp = new TempDirectory();
        var script = Path.Combine(temp.Path, "x.sh");
        File.WriteAllText(script, "#!/bin/bash\r\necho hi\r\n");

        Assert.Equal("#!/bin/bash\necho hi\n", InitInstall.ReadScript(script));
    }

    /// <summary>
    /// Spielt den Plan über <see cref="VmService"/> gegen den Simulator: jeder
    /// Schritt gelingt, der letzte ist die Marker-Prüfung.
    /// </summary>
    [Theory]
    [InlineData("python")]
    [InlineData("dotnet")]
    public void PlanImSimulatorEndetMitMarker(string component)
    {
        using var temp = new TempDirectory();
        var service = new VmService(SimulatedMultipassCli.At(Path.Combine(temp.Path, "state.json"), TimeSpan.Zero));
        service.Create("sim-a", "daily:26.10");

        MultipassRunResult? last = null;
        foreach (var step in InitInstall.Plan(LoadShippedImagesConfig(), [component]))
        {
            last = step.IsUpload
                ? service.Put(step.ScriptSource!, "sim-a", step.RemotePath)
                : service.Exec("sim-a", step.Command!);
            Assert.True(last.ExitCode == 0, $"Schritt `{step.Title}` scheitert (Exit {last.ExitCode}).");
        }

        Assert.NotNull(last);
    }

    private static ImagesConfig LoadShippedImagesConfig()
    {
        var path = InitStore.ResolvePath("src/CoreBridge.Cli/appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var images = document.RootElement.GetProperty("Images").Deserialize<ImagesConfig>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return images with
        {
            Components = new Dictionary<string, ComponentConfig>(images.Components, StringComparer.OrdinalIgnoreCase)
        };
    }
}
