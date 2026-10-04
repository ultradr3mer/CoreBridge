using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Tests;
using CoreBridge.Core.Vm;
using CoreBridge.Simulation;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// <c>cb work</c> gegen den Simulator, durch den Dispatcher: leeres Inventar → Migration
/// (Profil + VM) → Start → repo-send → Task-Branch → Stellvertreter-Agent → repo-receive
/// spult main vor; der zweite Aufruf nimmt die gemerkte VM ohne Rückfrage.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
public sealed class WorkFlowTests : IDisposable
{
    private readonly TempDirectory _store = new();
    private readonly TempDirectory _repo = new();
    private readonly AppConfig _config;

    public WorkFlowTests()
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
                    ["git"] = new() { Script = "scripts/components/install-git.sh", InstallPath = "/usr/local/sbin/corebridge-install-git", Packages = ["git"] },
                    ["claude"] = new() { Script = "scripts/components/install-claude.sh", InstallPath = "/usr/local/sbin/corebridge-install-claude" }
                }
            }
        };

        InventoryCliTests.Git(_repo.Path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_repo.Path, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_repo.Path, "CLAUDE.md"), "# Test\n");
        InventoryCliTests.Git(_repo.Path, "add", ".");
        InventoryCliTests.Git(_repo.Path, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
    }

    public void Dispose()
    {
        _store.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task VomLeerenInventarBisZumVorgespultenMain()
    {
        var first = await CliHarness.RunAsync(_config, new WorkOptions { Repo = _repo.Path, Yes = true });

        Assert.True(first.ExitCode == 0, first.All);
        Assert.Contains(first.Lines, line => line.Contains("Nichts Brauchbares vorhanden"));
        Assert.Contains(first.Lines, line => line.StartsWith("main fast-forwarded to"));
        Assert.True(File.Exists(Path.Combine(_repo.Path, "corebridge-agent.txt")));
        var inventory = VmInventory.Load(Path.Combine(_store.Path, VmInventory.FileName));
        Assert.Equal(["claude", "git", "node"], inventory.Vms.Single().Value.Components);
        Assert.Equal(inventory.Vms.Single().Key, inventory.Repos[Path.GetFullPath(_repo.Path)]);

        // Zweiter Aufruf: gemerkte VM, keine Rückfrage, keine Migration — wieder ein Commit.
        var second = await CliHarness.RunAsync(_config, new WorkOptions { Repo = _repo.Path, Yes = true });
        Assert.True(second.ExitCode == 0, second.All);
        Assert.Contains(second.Lines, line => line.Contains("(gemerkt für dieses Repository)"));
        Assert.DoesNotContain(second.Lines, line => line.Contains("Migration") || line.Contains("Nichts Brauchbares"));
        Assert.DoesNotContain(second.Lines, line => line.Contains("?"));
        Assert.Contains(second.Lines, line => line.StartsWith("main fast-forwarded to"));

        // Das Repo braucht inzwischen mehr: auch die gemerkte VM bekommt es nachinstalliert.
        File.WriteAllText(Path.Combine(_repo.Path, "go.mod"), "module x\n");
        InventoryCliTests.Git(_repo.Path, "add", ".");
        InventoryCliTests.Git(_repo.Path, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "go");
        var third = await CliHarness.RunAsync(_config, new WorkOptions { Repo = _repo.Path, Yes = true });
        Assert.True(third.ExitCode == 0, third.All);
        Assert.Contains(third.Lines, line => line.Contains("(gemerkt für dieses Repository)"));
        Assert.Contains(third.Lines, line => line.Contains("fehlt: go"));
    }

    [Fact]
    public async Task OhneJaBleibtAllesUnveraendert()
    {
        var refused = await CliHarness.RunAsync(_config, new WorkOptions { Repo = _repo.Path });

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("nichts geändert", refused.Error);
        Assert.False(File.Exists(Path.Combine(_store.Path, VmInventory.FileName)) && VmInventory.Load(Path.Combine(_store.Path, VmInventory.FileName)).Vms.Count > 0);
    }

    [Fact]
    public async Task UnbekannterAgentWirdAbgelehnt()
    {
        var result = await CliHarness.RunAsync(_config, new WorkOptions { Repo = _repo.Path, Agent = "copilot" });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("claude or opencode", result.Error);
    }
}
