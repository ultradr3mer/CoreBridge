using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class VmResourcesTests
{
    [Fact]
    public void GueltigeAngabenWerdenGetrimmtUebernommen()
    {
        var resources = VmResources.Parse(2, " 4G ", "20G");

        Assert.Equal(new VmResources(2, "4G", "20G"), resources);
        Assert.Equal("CPUs: 2, Memory: 4G, Disk: 20G", resources.ToString());
        Assert.Equal(["--cpus", "2", "--memory", "4G", "--disk", "20G"], resources.LaunchArguments());
        Assert.True(VmResources.Parse(null, " ", null).IsEmpty);
    }

    [Theory]
    [InlineData(0, null, null, "CPU count")]
    [InlineData(null, "viel", null, "memory size")]
    [InlineData(null, "64M", null, "too small")]
    [InlineData(null, null, "100M", "too small")]
    [InlineData(null, null, "1.5", "disk size")]
    public void UngueltigeAngabenWerdenVorherAbgelehnt(int? cpus, string? memory, string? disk, string expected)
    {
        var exception = Assert.Throws<MultipassException>(() => VmResources.Parse(cpus, memory, disk));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void CliSchlaegtProfil()
    {
        var profile = new VmResources(2, "4G", "20G");

        Assert.Equal(new VmResources(4, "4G", "20G"), new VmResources(Cpus: 4).Over(profile));
        Assert.Equal(profile, VmResources.None.Over(profile));
    }

    [Fact]
    public void CreateUebergibtCpusUndMemory()
    {
        var cli = new FakeCli();

        new VmService(cli).Create("cbx-test", "26.04", "20G", cpus: 2, memory: "4G");

        Assert.Equal(
            ["launch", "26.04", "--name", "cbx-test", "--timeout", "2400", "--cpus", "2", "--memory", "4G", "--disk", "20G"],
            cli.Calls[0]);
    }

    [Fact]
    public void SimulatorKenntCpusUndMemory()
    {
        using var temp = new TempDirectory();
        var service = new VmService(CoreBridge.Simulation.SimulatedMultipassCli.At(Path.Combine(temp.Path, "sim", "state.json"), TimeSpan.Zero));

        service.Create("sim-a", "daily:26.10", cpus: 2, memory: "4G");
        Assert.Equal("2", service.GetSetting("sim-a", "cpus"));
        Assert.Equal("4.0GiB", service.GetSetting("sim-a", "memory"));
        Assert.Contains("CPU(s):         2", service.Cli.Run(["info", "sim-a"]).StdOut);
        Assert.Contains("Instance must be stopped for modification", service.SetSetting("sim-a", "cpus", "4").StdErr);

        service.Stop("sim-a");
        Assert.Contains("positive integer", service.SetSetting("sim-a", "cpus", "0").StdErr);
        Assert.Contains("less than 128M", service.SetSetting("sim-a", "memory", "64M").StdErr);
        Assert.Equal(0, service.SetSetting("sim-a", "cpus", "4").ExitCode);
        Assert.Equal(0, service.SetSetting("sim-a", "memory", "2G").ExitCode);
        Assert.Equal("4", service.GetSetting("sim-a", "cpus"));
        Assert.Equal("2.0GiB", service.GetSetting("sim-a", "memory"));

        service.Clone("sim-a", "sim-b");
        Assert.Equal("4", service.GetSetting("sim-b", "cpus"));
    }

    [Collection("InitStore")]
    public class Profil
    {
        [Fact]
        public void VorgabenLandenImProfilUndKommenBeimAufloesenZurueck()
        {
            using var store = new TempDirectory();
            InitStore.Initialize(store);
            var config = new InitsConfig
            {
                Components = new(StringComparer.OrdinalIgnoreCase) { ["leer"] = new ComponentConfig() }
            };

            new InitService(config).Configure("web", ["leer"], resources: new VmResources(2, "4G"));

            var profile = InitProfileResolver.ResolveRequired(config, "web");
            Assert.Equal(new VmResources(2, "4G"), profile.Resources);
            Assert.DoesNotContain("Disk", File.ReadAllText(InitStore.ConfigFile("web")));
            Assert.Equal(new VmResources(2, "4G"), new InitService(config).List().Single().Resources);
        }
    }

    private sealed class FakeCli() : MultipassCli(() => null)
    {
        public List<string[]> Calls { get; } = [];

        public override MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30_000, string? exePath = null)
        {
            Calls.Add([.. args]);
            return new MultipassRunResult(0, string.Empty, string.Empty, 0);
        }

        public override MultipassRunResult RunWithExtensions(IReadOnlyList<string> args, TimeSpan slice, Func<bool> keepWaiting, string? exePath = null)
        {
            Calls.Add([.. args]);
            return new MultipassRunResult(0, string.Empty, string.Empty, 0);
        }
    }
}
