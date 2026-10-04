using System.Diagnostics;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Tests;
using CoreBridge.Core.Vm;
using CoreBridge.Simulation;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Inventar und repo-match durch den Dispatcher, gegen den Simulator: create (Profil) →
/// Snapshot mit Komponenten im Kommentar → install → clone erbt → delete --purge räumt auf;
/// repo-match findet die passende VM bzw. den Migrationspfad.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
public sealed class InventoryCliTests : IDisposable
{
    private readonly TempDirectory _store = new();
    private readonly AppConfig _config;

    public InventoryCliTests()
    {
        _config = new AppConfig
        {
            MultipassSimulationDelay = "0ms",
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = typeof(SimulatedMultipassCli).AssemblyQualifiedName! },
            Inits = new InitsConfig
            {
                StoreRoot = _store.Path,
                Components = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["git"] = new()
                    {
                        Script = "scripts/components/install-git.sh",
                        InstallPath = "/usr/local/sbin/corebridge-install-git",
                        Packages = ["git"]
                    }
                }
            }
        };
    }

    public void Dispose() => _store.Dispose();

    private VmInventory Inventory => VmInventory.Load(Path.Combine(_store.Path, VmInventory.FileName));

    private Task<HarnessResult> Run(IOptions options, params DialogStep[] dialog) => CliHarness.RunAsync(_config, options, dialog);

    [Fact]
    public async Task InventarFolgtDenVerben()
    {
        Assert.Equal(0, (await Run(new InitConfigureOptions { Name = "base", Components = "git", Cpus = 2, Memory = "2G" })).ExitCode);
        var created = await Run(new VmCreateOptions { Name = "inv-a", Profile = "base", Snapshot = "base" });
        Assert.Equal(0, created.ExitCode);

        var a = Inventory.Vms["inv-a"];
        Assert.Equal(("base", 2, "2.0GiB"), (a.Profile, a.Cpus, a.Memory));
        Assert.Equal(["git"], a.Components);
        Assert.Equal(["git"], a.Snapshots["base"].Components);
        var snapshots = await Run(new VmSnapshotsOptions { Name = "inv-a" });
        Assert.Contains(snapshots.Lines, line => line.Contains("corebridge: components=git; profile=base"));

        Assert.Equal(0, (await Run(new VmInstallOptions { Name = "inv-a", Components = "node" })).ExitCode);
        Assert.Equal(["git", "node"], Inventory.Vms["inv-a"].Components);

        Assert.Equal(0, (await Run(new VmCloneOptions { Source = "inv-a", Name = "inv-b" }, Cli.Asks("stop it for cloning").TestInputs("y"))).ExitCode);
        Assert.Equal(["git", "node"], Inventory.Vms["inv-b"].Components);
        Assert.Empty(Inventory.Vms["inv-b"].Snapshots);

        var list = await Run(new VmInventoryOptions());
        Assert.Contains(list.Lines, line => line.StartsWith("inv-b") && line.EndsWith("git,node"));
        Assert.Contains(list.Lines, line => line.Contains("└ base") && line.EndsWith("git"));

        Assert.Equal(0, (await Run(new VmRestoreOptions { Name = "inv-a", Snapshot = "base" }, Cli.Asks("without a backup").TestInputs("y"))).ExitCode);
        Assert.Equal(["git"], Inventory.Vms["inv-a"].Components);

        Assert.Equal(0, (await Run(new VmDeleteOptions { Name = "inv-b", Purge = true })).ExitCode);
        Assert.False(Inventory.Vms.ContainsKey("inv-b"));
    }

    [Fact]
    public async Task RefreshLiestDieMarker()
    {
        // Eine VM, die nicht über CoreBridge entstand: unbekannt, bis --refresh ihre Marker liest.
        var sim = SimulatedMultipassCli.At(Path.Combine(_store.Path, SimulatedMultipassCli.FolderName, SimulatedMultipassCli.StateFileName), TimeSpan.Zero);
        var vms = new VmService(sim);
        vms.Create("fremd", "daily:26.10");
        vms.Exec("fremd", "sudo touch /var/lib/corebridge/git.ready");

        var before = await Run(new VmInventoryOptions());
        Assert.Contains(before.Lines, line => line.StartsWith("fremd") && line.Contains("unbekannt"));

        var refreshed = await Run(new VmInventoryOptions { Refresh = true });
        Assert.Contains(refreshed.Lines, line => line.StartsWith("fremd") && line.EndsWith("git"));
        Assert.Equal(1, Inventory.Vms["fremd"].Cpus);
    }

    [Fact]
    public async Task RepoMatchFindetVmOderMigration()
    {
        using var repo = new TempDirectory();
        Git(repo.Path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo.Path, "package.json"), "{}");

        Assert.Equal(0, (await Run(new InitConfigureOptions { Name = "base", Components = "git" })).ExitCode);
        Assert.Equal(0, (await Run(new VmCreateOptions { Name = "only-git", Profile = "base" })).ExitCode);

        var partial = await Run(new RepoMatchOptions { Repo = repo.Path });
        Assert.Equal(0, partial.ExitCode);
        Assert.Contains(partial.Lines, line => line.Trim().StartsWith("node") && line.Contains("package.json"));
        Assert.Contains(partial.Lines, line => line.Contains("cb vm-clone -s only-git -n "));
        Assert.Contains(partial.Lines, line => line.Contains("-c node --snapshot base"));

        Assert.Equal(0, (await Run(new VmInstallOptions { Name = "only-git", Components = "node" })).ExitCode);
        var full = await Run(new RepoMatchOptions { Repo = repo.Path });
        Assert.Contains(full.Lines, line => line.Contains("Passt: VM `only-git`"));
    }

    internal static void Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.longpaths=true");
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
    }
}
