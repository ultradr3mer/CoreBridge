using System.Text.RegularExpressions;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Simulation;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Der Simulator soll freies Durchspielen tragen (beliebige Namen, Zustand über
/// Prozesse) und sich dabei so verhalten wie das echte Multipass — geprüft gegen
/// die Aufnahme in <c>Recordings/multipass-1.16.3</c>.
/// </summary>
public class SimulatedMultipassCliTests
{
    private static readonly string[] ListArgs = ["list", "--format", "json"];

    private static readonly Regex ResultLine = new(
        @"(Launched: \S+|Cloned from \S+ to \S+?\.|Snapshot taken: \S+|Snapshot restored: \S+)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void LebenszyklusMitSnapshotKlonUndRestore()
    {
        using var temp = new TempDirectory();
        var service = new VmService(Simulator(temp));

        Assert.Equal(0, service.Create("sim-a", "daily:26.10").ExitCode);
        Assert.Equal("Running", service.Find("sim-a")!.State);
        Assert.NotEmpty(service.Find("sim-a")!.Ipv4);

        // Wie Multipass: Snapshot und Klon nur gestoppt.
        Assert.Equal(2, service.Snapshot("sim-a").ExitCode);
        Assert.Equal(2, service.Clone("sim-a").ExitCode);
        service.Stop("sim-a");
        Assert.Empty(service.Find("sim-a")!.Ipv4);

        Assert.Contains("Snapshot taken: sim-a.base", service.Snapshot("sim-a", "base", "frisch").StdOut);
        Assert.Contains("Snapshot taken: sim-a.snapshot2", service.Snapshot("sim-a").StdOut);
        Assert.Equal(2, service.Snapshot("sim-a", "base").ExitCode);

        Assert.Contains("Cloned from sim-a to sim-b.", service.Clone("sim-a", "sim-b").StdOut);
        Assert.Contains("Cloned from sim-a to sim-a-clone2.", service.Clone("sim-a").StdOut);
        Assert.Equal(new[] { "sim-a", "sim-a-clone2", "sim-b" }, service.List().Select(vm => vm.Name));
        Assert.Empty(service.ListSnapshots("sim-b"));

        Assert.Contains("Snapshot restored: sim-a.base", service.Restore("sim-a", "base").StdOut);
        var backup = service.Snapshot("sim-a");
        Assert.Contains("sim-a.snapshot3", backup.StdOut);
        Assert.Equal("base", service.ListSnapshots("sim-a").Single(s => s.Name == "snapshot3").Parent);
        Assert.Equal(2, service.Restore("sim-a", "nope").ExitCode);

        service.Delete("sim-b");
        Assert.Equal("Deleted", service.Find("sim-b")!.State);
        service.Delete("sim-b", purge: true);
        Assert.Null(service.Find("sim-b"));
    }

    [Fact]
    public void DiskWaechstNurGestopptUndNieZurueck()
    {
        using var temp = new TempDirectory();
        var service = new VmService(Simulator(temp));
        service.Create("sim-a", "daily:26.10");
        service.Create("sim-b", "daily:26.10", disk: "10G");

        Assert.Equal("5.0GiB", service.GetDisk("sim-a"));
        Assert.Equal("10.0GiB", service.GetDisk("sim-b"));
        Assert.Contains("Instance must be stopped for modification", service.SetDisk("sim-a", "20G").StdErr);

        service.Stop("sim-a");
        Assert.Contains("Disk can only be expanded", service.SetDisk("sim-a", "1G").StdErr);
        Assert.Contains("abc is not a valid memory size", service.SetDisk("sim-a", "abc").StdErr);
        Assert.Equal(0, service.SetDisk("sim-a", "20G").ExitCode);
        Assert.Equal("20.0GiB", service.GetDisk("sim-a"));

        service.Clone("sim-a", "sim-c");
        Assert.Equal("20.0GiB", service.GetDisk("sim-c"));
        Assert.Throws<MultipassException>(() => service.GetDisk("sim-nix"));
    }

    [Fact]
    public void UnbekanntesImageScheitertWieMultipass()
    {
        using var temp = new TempDirectory();
        var cli = Simulator(temp);

        var typo = cli.Run(["launch", "dve", "--name", "sim-a"]);

        Assert.Equal(2, typo.ExitCode);
        Assert.Equal("launch failed: Unable to find an image matching \"dve\" in remote \"\".\n", typo.StdErr);
        Assert.Equal(0, cli.Run(["launch", "noble", "--name", "sim-b"]).ExitCode);
        Assert.Equal(0, cli.Run(["launch", "24.04", "--name", "sim-c"]).ExitCode);
    }

    [Fact]
    public void ZustandUeberlebtNeueInstanz()
    {
        using var temp = new TempDirectory();
        new VmService(Simulator(temp)).Create("sim-a", "daily:26.10");

        var second = new VmService(Simulator(temp));

        Assert.Equal("Running", second.Find("sim-a")!.State);
    }

    [Fact]
    public void FehlendeVmMeldetWieMultipass()
    {
        using var temp = new TempDirectory();
        var cli = Simulator(temp);

        var start = cli.Run(["start", "cbx-nix"]);
        var stop = cli.Run(["stop", "cbx-nix"]);
        var clone = cli.Run(["clone", "cbx-nix"]);

        Assert.Equal(2, start.ExitCode);
        Assert.Contains("Instance 'cbx-nix' does not exist.", start.StdErr);
        Assert.Contains("instance \"cbx-nix\" does not exist", stop.StdErr);
        Assert.Equal("clone failed: instance \"cbx-nix\" does not exist\n", clone.StdErr);
    }

    [Fact]
    public void KomponentenAusCloudInitBeantwortenIhreSondenAuchImKlon()
    {
        using var temp = new TempDirectory();
        var cloudInit = Path.Combine(temp.Path, "cloud-init.yaml");
        File.WriteAllText(cloudInit, """
            runcmd:
              - ["/usr/local/sbin/corebridge-install-claude", "ubuntu"]
            """);
        var service = new VmService(Simulator(temp));
        service.Create("sim-a", "daily:26.10", cloudInitPath: cloudInit);

        Assert.Equal("2.0.14 (Claude Code)\n", service.Exec("sim-a", "claude --version").StdOut);
        Assert.Equal(0, service.Exec("sim-a", "test -f /var/lib/corebridge/claude.ready").ExitCode);
        Assert.Equal(127, service.Exec("sim-a", "dotnet --version").ExitCode);
        Assert.Equal(1, service.Exec("sim-a", "test -f /var/lib/corebridge/dotnet.ready").ExitCode);

        service.Stop("sim-a");
        service.Clone("sim-a", "sim-b");
        service.Start("sim-b");
        Assert.Equal("sim-b\n", service.Exec("sim-b", "hostname").StdOut);
        Assert.Equal(0, service.Exec("sim-b", "claude --version").ExitCode);
    }

    /// <summary>Die exec-Kommandos des CLI-Lifecycles (Stage05/05b) — ausgewertet, nicht nur durchgewinkt.</summary>
    [Theory]
    [InlineData("echo cbx-exec-$((20+22))", 0, "cbx-exec-42\n")]
    [InlineData(". /etc/os-release && test \"$VERSION_ID\" = \"26.10\"", 0, "")]
    [InlineData(". /etc/os-release && test \"$VERSION_ID\" = \"24.04\"", 1, "")]
    [InlineData("command -v claude >/dev/null && claude --version", 0, "2.0.14 (Claude Code)\n")]
    [InlineData("command -v dotnet >/dev/null && dotnet --version", 1, "")]
    [InlineData("echo user=$(id -un) root=$(sudo id -un)", 0, "user=ubuntu root=root\n")]
    [InlineData("x=$((6*7)); echo \"${x}\" '$x' ${UNSET:-leer}", 0, "42 $x leer\n")]
    [InlineData("false || echo ok; true && false; echo rc=$?", 0, "ok\nrc=1\n")]
    [InlineData("cd /nirgends", 2, "")]
    [InlineData("cloud-init status --wait >/dev/null 2>&1 || true", 0, "")]
    [InlineData("grep VERSION_ID /etc/os-release | head -n 1", 0, "VERSION_ID=\"26.10\"\n")]
    public void ExecLaeuftDurchDieSimulierteShell(string command, int exitCode, string stdOut)
    {
        using var temp = new TempDirectory();
        var service = new VmService(Simulator(temp));
        service.Create("sim-a", "daily:26.10", cloudInitPath: ClaudeCloudInit(temp));

        var result = service.Exec("sim-a", command);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(stdOut, result.StdOut);
    }

    [Fact]
    public void DateienUndVerzeichnisseBleibenZwischenExecsErhalten()
    {
        using var temp = new TempDirectory();
        var service = new VmService(Simulator(temp));
        service.Create("sim-a", "daily:26.10");

        Assert.Equal(0, service.Exec("sim-a", "mkdir -p ~/work/demo && cd ~/work/demo && echo hallo > a.txt && echo welt >> a.txt").ExitCode);
        Assert.Equal("hallo\nwelt\n", service.Exec("sim-a", "cat /home/ubuntu/work/demo/a.txt").StdOut);
        Assert.Equal("demo\n", service.Exec("sim-a", "ls work").StdOut);
        Assert.Equal(1, service.Exec("sim-a", "test -f nix.txt").ExitCode);
        Assert.Contains("No such file", service.Exec("sim-a", "cat nix.txt").StdErr);

        // Marker sind Dateien wie andere auch.
        Assert.Equal(0, service.Exec("sim-a", "sudo touch /var/lib/corebridge/demo.ready").ExitCode);
        Assert.Equal("demo.ready\n", service.Exec("sim-a", "ls /var/lib/corebridge").StdOut);
    }

    /// <summary>vm-connect (Stage06): jedes exec ist ein eigenes Skript, das Arbeitsverzeichnis wandert über die Marker-Zeile mit.</summary>
    [Fact]
    public void ConsoleSessionTraegtDasArbeitsverzeichnisWeiter()
    {
        using var temp = new TempDirectory();
        var service = new VmService(Simulator(temp));
        service.Create("sim-a", "daily:26.10");
        var timeout = TimeSpan.FromSeconds(5);

        using var session = service.OpenConsole("sim-a", timeoutMs: 5_000);
        session.SendLine("cd /tmp");
        session.SendCommand("echo cwd=$(pwd)", "cwd=/tmp", timeout);
        session.SendCommand("echo cbx-connect-$((20+22))", "cbx-connect-42", timeout);
        session.SendCommand("echo user=$(id -un)", "user=ubuntu", timeout);

        Assert.Equal("/tmp", session.WorkingDirectory);
    }

    /// <summary><c>multipass-sim.exe</c>: Kommandozeile und Ausgaben wie <c>multipass.exe</c>.</summary>
    [Fact]
    public void KommandozeileGibtAusWieMultipass()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state.json");

        Assert.Equal((0, "No instances found.\n", ""), CommandLine(state, "list"));
        Assert.Equal((0, "Launched: demo\n", ""), CommandLine(state, "launch", "26.10", "--name", "demo"));
        CommandLine(state, "launch", "--name", "demo2");
        CommandLine(state, "stop", "demo");
        Assert.Equal((0, "Snapshot taken: demo.base\n", ""), CommandLine(state, "snapshot", "demo", "--name", "base", "--comment", "frisch"));

        Assert.Equal(
            "Name                    State             IPv4             Image\n"
            + "demo                    Stopped           --               Ubuntu 26.10\n"
            + "demo2                   Running           172.30.99.3      Ubuntu 26.10\n",
            CommandLine(state, "list").Out);
        Assert.Equal(
            "Instance   Snapshot   Parent   Comment\n"
            + "demo       base       --       frisch\n",
            CommandLine(state, "list", "--snapshots").Out);
        Assert.Contains("State:          Stopped\n", CommandLine(state, "info", "demo").Out);
        Assert.Equal(2, MultipassVmList.Parse(CommandLine(state, "list", "--format", "json").Out).Count);

        var missing = CommandLine(state, "exec", "nope", "--", "true");
        Assert.Equal(2, missing.Code);
        Assert.Contains("instance \"nope\" does not exist", missing.Err);
    }

    [Fact]
    public void KommandozeilenShellStartetDieVmUndTraegtDasVerzeichnis()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state.json");
        CommandLine(state, "launch", "--name", "demo");
        CommandLine(state, "stop", "demo");

        var shell = CommandLineWithInput(state, "cd /tmp\npwd\nmkdir -p work && cd work\npwd\nexit\necho nie", "shell", "demo");

        Assert.Equal((0, "Starting demo\n/tmp\n/tmp/work\n", ""), shell);
        Assert.Equal("Running", new VmService(SimulatedMultipassCli.At(state, TimeSpan.Zero)).Find("demo")!.State);
    }

    private static (int Code, string Out, string Err) CommandLine(string state, params string[] args) =>
        CommandLineWithInput(state, string.Empty, args);

    private static (int Code, string Out, string Err) CommandLineWithInput(string state, string input, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = MultipassSimCommandLine.Run(args, new StringReader(input), output, error, interactive: false, state, TimeSpan.Zero);
        return (code, output.ToString(), error.ToString());
    }

    private static string ClaudeCloudInit(TempDirectory temp)
    {
        var cloudInit = Path.Combine(temp.Path, "cloud-init.yaml");
        File.WriteAllText(cloudInit, """
            runcmd:
              - ["/usr/local/sbin/corebridge-install-claude", "ubuntu"]
            """);
        return cloudInit;
    }

    [Fact]
    public void TransferHinUndZurueck()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "hin.txt");
        var target = Path.Combine(temp.Path, "zurueck", "her.txt");
        File.WriteAllText(source, "hallo");
        var service = new VmService(Simulator(temp));
        service.Create("sim-a", "daily:26.10");

        Assert.Equal(0, service.Put(source, "sim-a", "rec.txt").ExitCode);
        Assert.Equal("hallo", service.Exec("sim-a", "cat rec.txt").StdOut);
        Assert.Equal(0, service.Get("sim-a", "rec.txt", target).ExitCode);
        Assert.Equal("hallo", File.ReadAllText(target));
    }

    [Fact]
    public void LangeVerbenWartenInAbschnittenUndFragenWeiterZuWarten()
    {
        using var temp = new TempDirectory();
        var cli = Simulator(temp, TimeSpan.FromMilliseconds(300));
        var asked = 0;

        var result = cli.RunWithExtensions(
            ["launch", "daily:26.10", "--name", "sim-a"],
            TimeSpan.FromMilliseconds(100),
            () =>
            {
                asked++;
                return true;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, asked);
        Assert.Equal(300, result.DurationMs);
        Assert.Equal(0, cli.Run(ListArgs).DurationMs);
    }

    [Fact]
    public void NeinZumWeiterWartenWirftAberDerZustandIstSchonGeaendert()
    {
        using var temp = new TempDirectory();
        var cli = Simulator(temp, TimeSpan.FromMilliseconds(300));

        Assert.Throws<MultipassTimeoutException>(() => cli.RunWithExtensions(
            ["launch", "daily:26.10", "--name", "sim-a"],
            TimeSpan.FromMilliseconds(100),
            () => false));

        // Wie beim echten Daemon: der Launch läuft im Hintergrund zu Ende.
        Assert.Equal("Running", new VmService(cli).Find("sim-a")!.State);
    }

    [Fact]
    public void BootstrapWaehltDenSimulatorUndProtokolliertIhnNicht()
    {
        using var temp = new TempDirectory();
        var config = new AppConfig
        {
            MultipassLogPath = Path.Combine(temp.Path, "log"),
            MultipassSimulationDelay = "0ms",
            Inits = new InitsConfig { StoreRoot = temp.Path },
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = typeof(SimulatedMultipassCli).FullName! }
        };

        var cli = MultipassCliFactory.Create(CoreBridgeContainer.Create(config));

        var simulator = Assert.IsType<SimulatedMultipassCli>(cli);
        Assert.Equal(Path.Combine(temp.Path, SimulatedMultipassCli.FolderName, SimulatedMultipassCli.StateFileName), simulator.StatePath);
        cli.Run(ListArgs);
        Assert.False(Directory.Exists(config.MultipassLogPath));
    }

    /// <summary>
    /// Spielt die echte Aufnahme Aufruf für Aufruf gegen den Simulator: gleiche
    /// Exit-Codes, gleiche Ergebniszeilen, gleiche exec-Ausgaben, gleiche VM- und
    /// Snapshot-Listen. Transfers bleiben außen vor (Host-Pfade der Aufnahme).
    /// </summary>
    [Fact]
    public void SimulatorEntsprichtDerEchtenAufnahme()
    {
        var recording = Path.Combine(AppContext.BaseDirectory, "Recordings", "multipass-1.16.3");
        var entries = MultipassCallLog.Read(recording);
        Assert.True(entries.Count > 20, $"Aufnahme fehlt oder ist zu kurz: {recording}");

        using var temp = new TempDirectory();
        var simulator = Simulator(temp);
        var compared = 0;
        foreach (var recorded in entries.Where(e => e.Verb is not ("transfer" or "version")))
        {
            var simulated = simulator.Run(recorded.Args);
            var call = string.Join(' ', recorded.Args);
            Assert.True(recorded.ExitCode == simulated.ExitCode, $"Exit-Code für `{call}`: echt {recorded.ExitCode}, simuliert {simulated.ExitCode} ({simulated.StdErr})");

            switch (recorded.Verb)
            {
                case "list" when recorded.Args.Contains("--snapshots"):
                    Assert.Equal(
                        MultipassSnapshotList.Parse(recorded.StdOut).Where(s => s.Instance.StartsWith("cbx-rec", StringComparison.Ordinal)),
                        MultipassSnapshotList.Parse(simulated.StdOut));
                    break;
                case "list":
                    Assert.Equal(
                        Vms(recorded.StdOut).Where(vm => vm.Name.StartsWith("cbx-rec", StringComparison.Ordinal)),
                        Vms(simulated.StdOut));
                    break;
                case "exec":
                    Assert.Equal(recorded.StdOut, simulated.StdOut);
                    break;
                default:
                    Assert.True(
                        Results(recorded.StdOut).SequenceEqual(Results(simulated.StdOut)),
                        $"Ergebniszeilen für `{call}`: echt [{string.Join(", ", Results(recorded.StdOut))}], simuliert [{string.Join(", ", Results(simulated.StdOut))}]");
                    break;
            }

            compared++;
        }

        Assert.True(compared > 20);
    }

    /// <summary>
    /// Nach Namen sortiert: Multipass 1.16 listet in keiner festen Reihenfolge
    /// (einmal <c>ai-ubuntu, ai-ubuntu-python, ai-ubuntu-dotnet</c>), verglichen
    /// wird also nur, welche VMs in welchem Zustand da sind.
    /// </summary>
    private static IEnumerable<(string Name, string? State)> Vms(string json)
    {
        return MultipassVmList.Parse(json).Select(vm => (vm.Name, vm.State)).OrderBy(vm => vm.Name, StringComparer.Ordinal);
    }

    private static IEnumerable<string> Results(string stdOut)
    {
        return ResultLine.Matches(stdOut).Select(m => m.Value);
    }

    private static SimulatedMultipassCli Simulator(TempDirectory temp, TimeSpan? delay = null)
    {
        return SimulatedMultipassCli.At(Path.Combine(temp.Path, "sim", "state.json"), delay ?? TimeSpan.Zero);
    }
}
