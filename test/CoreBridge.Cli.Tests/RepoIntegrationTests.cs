using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Tests;
using CoreBridge.Core.Vm;
using CoreBridge.Simulation;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// repo-receive, wenn main seit dem Task-Start weitergegangen ist: Integration in der VM
/// (main schicken, mergen, Stellvertreter-Agent löst Konflikte, Ergebnisdatei) und danach
/// Vorspulen — oder ohne Zustimmung gar nichts.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
public sealed class RepoIntegrationTests : IDisposable
{
    private readonly TempDirectory _store = new();
    private readonly TempDirectory _repo = new();
    private readonly AppConfig _config;

    public RepoIntegrationTests()
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
        File.WriteAllText(Path.Combine(_repo.Path, "a.txt"), "base\n");
        Commit(_repo.Path, "init");
    }

    public void Dispose()
    {
        _store.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task KonfliktLoestDerAgentInDerVm()
    {
        var vm = await TaskWithHostChange("a.txt");

        var result = await CliHarness.RunAsync(_config, new RepoReceiveOptions { Name = vm, Id = "t1", Repo = _repo.Path, Integrate = true, Yes = true });

        Assert.True(result.ExitCode == 0, result.All);
        Assert.Contains(result.Lines, line => line.Contains("conflicts in 1 file(s): a.txt"));
        Assert.Contains(result.Lines, line => line.Contains("Konflikt gelöst: a.txt"));
        Assert.Contains(result.Lines, line => line.StartsWith("main fast-forwarded to"));
        var merged = File.ReadAllText(Path.Combine(_repo.Path, "a.txt"));
        Assert.Contains("vm", merged);
        Assert.Contains("host", merged);
    }

    [Fact]
    public async Task OhneKonfliktKeinAgent()
    {
        var vm = await TaskWithHostChange("b.txt");

        var result = await CliHarness.RunAsync(_config, new RepoReceiveOptions { Name = vm, Id = "t1", Repo = _repo.Path, Integrate = true, Yes = true });

        Assert.True(result.ExitCode == 0, result.All);
        Assert.Contains(result.Lines, line => line == "merged without conflicts.");
        Assert.DoesNotContain(result.Lines, line => line.Contains("resolves the conflicts"));
        Assert.Equal("vm", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(_repo.Path, "b.txt")));
    }

    [Fact]
    public async Task OhneZustimmungBleibtAllesUnveraendert()
    {
        var vm = await TaskWithHostChange("a.txt");
        var before = Head(_repo.Path);

        var result = await CliHarness.RunAsync(_config, new RepoReceiveOptions { Name = vm, Id = "t1", Repo = _repo.Path, Yes = true });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Lines, line => line.Contains("Integrate main into claude/t1"));
        Assert.Contains("--integrate", result.Error);
        Assert.Equal(before, Head(_repo.Path));
    }

    /// <summary>
    /// <c>cb work</c> legt VM und Arbeitskopie an; danach entsteht in der VM <c>claude/t1</c>
    /// (a.txt → „vm“) und auf dem Host ein neuer Commit auf main, der <paramref name="hostFile"/> ändert.
    /// </summary>
    private async Task<string> TaskWithHostChange(string hostFile)
    {
        var work = await CliHarness.RunAsync(_config, new WorkOptions { Repo = _repo.Path, Yes = true });
        Assert.True(work.ExitCode == 0, work.All);
        var vm = VmInventory.Load(Path.Combine(_store.Path, VmInventory.FileName)).Vms.Single().Key;

        var home = Path.Combine(_store.Path, "multipass-sim", "disks", vm, "home", "ubuntu");
        var workspace = Path.Combine(home, "work", Path.GetFileName(_repo.Path));
        // receive-pack sieht kein -c des Aufrufers; die tiefen Simulatorpfade brauchen longpaths im Bare-Repo selbst.
        InventoryCliTests.Git(Path.Combine(home, "repos", Path.GetFileName(_repo.Path) + ".git"), "config", "core.longpaths", "true");
        InventoryCliTests.Git(workspace, "fetch", "-q", "origin");
        InventoryCliTests.Git(workspace, "checkout", "-q", "-b", "claude/t1", "origin/main");
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "vm\n");
        Commit(workspace, "task");
        InventoryCliTests.Git(workspace, "push", "-q", "origin", "claude/t1");

        File.WriteAllText(Path.Combine(_repo.Path, hostFile), "host\n");
        Commit(_repo.Path, "host");
        return vm;
    }

    private static void Commit(string directory, string message)
    {
        InventoryCliTests.Git(directory, "add", ".");
        InventoryCliTests.Git(directory, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", message);
    }

    private static string Head(string directory) =>
        File.ReadAllText(Path.Combine(directory, ".git", "refs", "heads", "main")).Trim();
}
