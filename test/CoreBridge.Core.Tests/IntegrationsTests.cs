using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;
using Xunit.Abstractions;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Geteilter Zustand des VM-/Image-Zyklus. WICHTIG: xUnit ordnet nur
/// INNERHALB einer Klasse (AlphabeticalOrderer über _0_.._n); zwischen
/// Klassen ist die Reihenfolge ungarantiert — deshalb der ganze Zyklus
/// in EINER Klasse, aber weiterhin feingranulare Tests mit eigener
/// Pass/Fail-Meldung und ITestOutputHelper-Live-Ausgabe.
/// </summary>
public static class VmCycleContext
{
  public const string VmName = "cbx-cycle-a";
  public const string ImageName = "cbx-cycle-img";

  public static bool MultipassAvailable => MultipassBinaryLocator.IsInstalled();

  /// <summary>
  /// Store muss über alle Tests des Zyklus leben (cloud-init wird beim vm-create
  /// erst in _2 gebraucht) — deshalb statisch, Dispose passiert im letzten Test.
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
[Collection("ImageStore")] // ImageStore ist statisch: serialisiert gegen alle Image-Tests
public class VmCycleTests
{
  private readonly ITestOutputHelper output;

  public VmCycleTests(ITestOutputHelper output)
  {
    this.output = output;
  }

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

    // image-list: neues Image taucht auf (noch ohne .img).
    var entry = images.List().Single(e => e.Name == VmCycleContext.ImageName);
    Assert.False(entry.HasImage);
    this.output.WriteLine($"image-list: {entry.Name} (Komponenten: {string.Join(",", entry.Components)})");

    VmCycleContext.ConfigureResult = configure;
  }

  [Fact]
  public void _2_vmCreate()
  {
    if (VmCycleContext.SkipWithoutMultipass()) return;
    var configure = VmCycleContext.ConfigureResult;
    if (configure?.CloudInitPath is null)
    {
      this.output.WriteLine("übersprungen — _1 hat keine Konfiguration hinterlegt.");
      return;
    }

    var vms = new VmService();
    var create = vms.Create(VmCycleContext.VmName, "core26", null, cloudInitPath: configure.CloudInitPath);
    Assert.Equal(0, create.ExitCode);
    this.output.WriteLine($"launch: {create.DurationMs / 1000.0:F1} s");
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
  public void _4_vmStart()
  {
    if (VmCycleContext.SkipWithoutMultipass()) return;
    var vms = new VmService();

    // Echter Start-Fall: erst stoppen (launch lässt die VM bereits laufen),
    // dann starten. Start VOR dem Connect — Shell geht nur auf laufender VM.
    // (multipass start auf bereits laufender VM stört nachweisbar die
    // SSH-Authentifizierung — deshalb stop/start statt idempotentem Start.)
    Assert.Equal(0, vms.Stop(VmCycleContext.VmName).ExitCode);
    var start = vms.Start(VmCycleContext.VmName);
    Assert.Equal(0, start.ExitCode);
    var started = vms.List().Single(v => v.Name == VmCycleContext.VmName);
    Assert.Equal("Running", started.State, ignoreCase: true);
  }

  [Fact]
  public void _5_vmExec()
  {
    if (VmCycleContext.SkipWithoutMultipass()) return;
    var vms = new VmService();

    // Frisch gestartete VM: SSH kann für wenige Sekunden "disconnected" liefern
    // (siehe EXPERIMENT_LOG T06 — native SSH ~10 s nach Launch stabil). Retry.
    MultipassRunResult exec = new(2, string.Empty, string.Empty, 0);
    for (var attempt = 1; attempt <= 5; attempt++)
    {
      exec = vms.Exec(VmCycleContext.VmName, "echo cycle-ok");
      if (exec.ExitCode == 0)
      {
        break;
      }

      this.output.WriteLine($"exec Versuch {attempt} fehlgeschlagen (exit {exec.ExitCode}) — warte 5 s ...");
      Thread.Sleep(5_000);
    }

    Assert.Equal(0, exec.ExitCode);
    Assert.Contains("cycle-ok", exec.StdOut);
  }

  [Fact]
  public void _6_vmConnect()
  {
    if (VmCycleContext.SkipWithoutMultipass()) return;
    var vms = new VmService();

    // vm-connect (emuliert über Pipes). OHNE Prompt-Wait: ohne TTY gibt bash
    // keinen Prompt aus (nur die Banner) — direkt Befehl senden und auf seine
    // Ausgabe warten. Shell-Ausgabe läuft live via output.WriteLine mit.
    using var console = vms.OpenConsole(VmCycleContext.VmName, this.output.WriteLine);
    try
    {
      var output = console.SendCommand("echo console-ok", "console-ok", TimeSpan.FromSeconds(30));
      Assert.Contains("console-ok", output);
    }
    catch (MultipassTimeoutException exception)
    {
      // Diagnose: was hat die Shell bisher von sich gegeben?
      this.output.WriteLine($"--- Timeout, bisherige Shell-Ausgabe ---{Environment.NewLine}{exception.StdOut}");
      throw;
    }

    console.Exit();
  }

  /// <summary>
  /// vm-connect über natives SSH (SSH.NET, PTY) — die ECHTE interaktive Shell
  /// mit gemeinsamem Status. Test-eigener Key: Keypair in TempDir erzeugen
  /// (ssh-keygen), Public Key über den (bewiesen funktionierenden) exec-Kanal
  /// in authorized_keys injizieren, dann SSH-Session als ubuntu@vm.
  /// Verbindung nur Host→VM (Plan §6).
  /// </summary>
  [Fact]
  public void _7_vmConnectSsh()
  {
    if (VmCycleContext.SkipWithoutMultipass()) return;
    var vms = new VmService();

    using var keyDir = new TempDirectory();
    var keyPath = System.IO.Path.Combine(keyDir, "id_ed25519");
    var sshKeygen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
      FileName = "ssh-keygen",
      Arguments = $"-t ed25519 -N \"\" -f \"{keyPath}\" -q",
      UseShellExecute = false,
      CreateNoWindow = true
    });
    sshKeygen!.WaitForExit(15_000);
    Assert.Equal(0, sshKeygen.ExitCode);
    var publicKey = File.ReadAllText(keyPath + ".pub").Trim();

    // Public Key injizieren (bootstrap-agent-vm-Muster, als ubuntu via exec).
    var inject = vms.Exec(
        VmCycleContext.VmName,
        $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '{publicKey}' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys");
    Assert.Equal(0, inject.ExitCode);

    using var session = vms.OpenSshSession(VmCycleContext.VmName, this.output.WriteLine, keyPath);
    try
    {
      // Prompt abwarten (PTY liefert ihn — anders als der exec-Kanal), dann
      // zwei Befehle: gemeinsamer Shell-Status muss sichtbar sein (cd/whoami).
      session.WaitFor("$", TimeSpan.FromSeconds(20));
      var output = session.SendCommand("echo ssh-ok", "ssh-ok", TimeSpan.FromSeconds(10));
      Assert.Contains("ssh-ok", output);

      // Shell-Status: cwd über zwei Befehle hinweg erhalten (nur mit echter Shell).
      var pwd = session.SendCommand("cd /tmp && echo marker-$PWD", "marker-/tmp", TimeSpan.FromSeconds(10));
      Assert.Contains("marker-/tmp", pwd);
    }
    catch (MultipassTimeoutException exception)
    {
      this.output.WriteLine($"--- Timeout, bisherige SSH-Ausgabe ---{Environment.NewLine}{exception.StdOut}");
      throw;
    }

    session.Exit();
  }

  [Fact]
  public void _8_vmStop()
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
  public void _9_vmDelete()
  {
    if (VmCycleContext.SkipWithoutMultipass()) return;
    var vms = new VmService();

    // MIT purge (Regel: purge nur über Tests, nur cbx-VMs).
    var delete = vms.Delete(VmCycleContext.VmName, purge: true);
    Assert.Equal(0, delete.ExitCode);
    var after = vms.List().Where(v => v.Name == VmCycleContext.VmName).ToList();
    Assert.Empty(after); // purge: VM muss WIRKLICH weg sein, nicht nur Papierkorb.
  }

  //[Fact]
  //public void _A_imageDelete()
  //{
  //  if (VmCycleContext.SkipWithoutMultipass()) return;
  //  var images = new ImageService(TestConfigs.Images);

  //  var result = images.Delete(VmCycleContext.ImageName);
  //  Assert.Equal(ImageStore.ModelDir(VmCycleContext.ImageName), result.ModelDir);
  //  Assert.False(Directory.Exists(ImageStore.ModelDir(VmCycleContext.ImageName)));

  //  // image-list: Eintrag ist weg.
  //  Assert.DoesNotContain(VmCycleContext.ImageName, images.List().Select(e => e.Name));

  //  VmCycleContext.ConfigureResult = null;
  //  VmCycleContext.Store?.Dispose();
  //}
}
