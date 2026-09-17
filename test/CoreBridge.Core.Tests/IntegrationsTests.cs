using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Geteilter Zustand des VM-/Image-Zyklus zwischen Create- und Delete-Klasse.
/// Beide Klassen laufen in derselben Collection (serialisiert), innerhalb der
/// Klassen alphabetisch (AlphabeticalOrderer, _0_.._n) — so ist der Ablauf
/// deterministisch, ohne alles in einen Multi-Minuten-Test zu zwingen.
/// </summary>
[Collection("VmCycle")]
public class VmCycleContext
{
    public const string VmName = "cbx-cycle-a";
    public const string ImageName = "cbx-cycle-img";

    public static bool MultipassAvailable => MultipassBinaryLocator.IsInstalled();

    /// <summary>
    /// Store muss über alle Tests des Zyklus leben (cloud-init wird beim vm-create
    /// in _2 erst gebraucht) — deshalb statisch, Dispose passiert im letzten Test.
    /// </summary>
    internal static TempDirectory? Store { get; set; }

    public static ImageConfigureResult? ConfigureResult { get; set; }

    /// <summary>
    /// Early-Return-Pattern: `if (SkipWithoutMultipass()) return;` — jedes
    /// Zyklus-Test startet damit (analog MultipassFact in den Experiments).
    /// </summary>
    public static bool SkipWithoutMultipass() => !MultipassAvailable;
}

[TestCaseOrderer(
    ordererTypeName: "CoreBridge.Core.Tests.AlphabeticalOrderer",
    ordererAssemblyName: "CoreBridge.Core.Tests")]
[Collection("VmCycle")]
public class CreateCycleTest
{
    [Fact]
    public void _0_multipassStatus()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;

        var report = Doctor.Check();
        Assert.True(report.Found, "Multipass-Binary nicht gefunden.");
        Assert.NotNull(report.ClientVersion);
        Assert.NotNull(report.DaemonVersion);
    }

    [Fact]
    public void _1_imageConfigure()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        VmCycleContext.Store = new TempDirectory();
        ImageStore.Initialize(VmCycleContext.Store);
        var images = new ImageService(TestConfigs.Images);
        var vms = new VmService();

        // Pre-Cleanup: Reste aus abgebrochenen Läufen (Name belegt -> launch exit 2).
        if (vms.List().Any(v => v.Name == VmCycleContext.VmName))
        {
            vms.Delete(VmCycleContext.VmName, purge: true);
        }

        var configure = images.Configure(VmCycleContext.ImageName, ["claude", "dotnet"], force: true);
        Assert.True(File.Exists(configure.ConfigPath));
        Assert.NotNull(configure.CloudInitPath);
        Assert.True(File.Exists(configure.CloudInitPath));

        // Guard: ohne --force würde ein zweiter Configure-Lauf fehlschlagen.
        Assert.Throws<MultipassException>(() => images.Configure(VmCycleContext.ImageName, ["claude"]));

        VmCycleContext.ConfigureResult = configure;
    }

    [Fact]
    public void _2_vmCreate()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        var configure = VmCycleContext.ConfigureResult;
        if (configure?.CloudInitPath is null)
        {
            return; // _1 fehlgeschlagen/übersprungen — ohne cloud-init kein sinnvoller Launch.
        }

        var vms = new VmService();
        var create = vms.Create(VmCycleContext.VmName, "core26", null, cloudInitPath: configure.CloudInitPath);
        Assert.Equal(0, create.ExitCode);
    }

    [Fact]
    public void _3_vmList()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        var vms = new VmService();

        var listed = vms.List().SingleOrDefault(v => v.Name == VmCycleContext.VmName);
        Assert.NotNull(listed);
        Assert.False(listed.IsDeleted);
        Assert.Equal("Running", listed.State, ignoreCase: true);
    }

    [Fact]
    public void _4_vmExec()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        var vms = new VmService();

        var exec = vms.Exec(VmCycleContext.VmName, "echo cycle-ok");
        Assert.Equal(0, exec.ExitCode);
        Assert.Contains("cycle-ok", exec.StdOut);
    }

    [Fact]
    public void _5_vmConnect()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        var vms = new VmService();

        // vm-connect (emuliert über Pipes): Befehl senden, Ausgabe prüfen, beenden.
        using var console = vms.OpenConsole(VmCycleContext.VmName);
        var output = console.SendCommand("echo console-ok", "console-ok", TimeSpan.FromSeconds(60));
        Assert.Contains("console-ok", output);
        console.Exit();
    }
}

[TestCaseOrderer(
    ordererTypeName: "CoreBridge.Core.Tests.AlphabeticalOrderer",
    ordererAssemblyName: "CoreBridge.Core.Tests")]
[Collection("VmCycle")]
public class DeleteCycleTest
{
    [Fact]
    public void _0_vmStop()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        var vms = new VmService();

        var stop = vms.Stop(VmCycleContext.VmName);
        Assert.Equal(0, stop.ExitCode);
        var stopped = vms.List().Single(v => v.Name == VmCycleContext.VmName);
        Assert.False(
            string.Equals(stopped.State, "Running", StringComparison.OrdinalIgnoreCase),
            $"State nach stop ist immer noch Running: {stopped.State}");
    }

    [Fact]
    public void _1_vmDelete()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        var vms = new VmService();

        // MIT purge (Regel: purge nur über Tests, nur cbx-VMs).
        var delete = vms.Delete(VmCycleContext.VmName, purge: true);
        Assert.Equal(0, delete.ExitCode);
        var after = vms.List().Where(v => v.Name == VmCycleContext.VmName).ToList();
        Assert.Empty(after); // purge: VM muss WIRKLICH weg sein, nicht nur Papierkorb.
    }

    [Fact]
    public void _2_imageDelete()
    {
        if (VmCycleContext.SkipWithoutMultipass()) return;
        var images = new ImageService(TestConfigs.Images);

        var result = images.Delete(VmCycleContext.ImageName);
        Assert.Equal(ImageStore.ModelDir(VmCycleContext.ImageName), result.ModelDir);
        Assert.False(Directory.Exists(ImageStore.ModelDir(VmCycleContext.ImageName)));
        VmCycleContext.ConfigureResult = null;
        VmCycleContext.Store?.Dispose();
    }
}

