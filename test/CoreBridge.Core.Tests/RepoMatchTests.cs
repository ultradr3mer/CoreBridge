using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class RepoMatchTests
{
    private static readonly InitsConfig Inits = new()
    {
        Components = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = new(),
            ["node"] = new(),
            ["python"] = new(),
            ["git"] = new(),
            ["claude"] = new(),
            ["docker"] = new() { Detect = ["Dockerfile", "compose.y*ml"] }
        }
    };

    [Fact]
    public void ErkenntRegelnUndDetectUndImmerGit()
    {
        using var repo = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(repo.Path, "src", "App"));
        File.WriteAllText(Path.Combine(repo.Path, "src", "App", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(repo.Path, "compose.yaml"), "services: {}");
        Directory.CreateDirectory(Path.Combine(repo.Path, "node_modules", "x"));
        File.WriteAllText(Path.Combine(repo.Path, "node_modules", "x", "package.json"), "{}");

        var requirements = RepoRequirements.Detect(repo.Path, Inits);

        Assert.Equal(["docker", "dotnet", "git"], requirements.Select(r => r.Component));
        Assert.Equal("src/App/App.csproj", requirements.Single(r => r.Component == "dotnet").Reason);
    }

    [Fact]
    public void RegelOhneKomponenteZaehltNicht()
    {
        using var repo = new TempDirectory();
        File.WriteAllText(Path.Combine(repo.Path, "Cargo.toml"), "[package]");

        Assert.Equal(["git"], RepoRequirements.Detect(repo.Path, Inits).Select(r => r.Component));
    }

    private static VmInventory Inventory(params (string Vm, string[] Components)[] vms)
    {
        var inventory = new VmInventory();
        foreach (var (vm, components) in vms)
        {
            inventory.Vms[vm] = new InventoryVm { Components = [.. components] };
        }

        return inventory;
    }

    private static VmInfo Vm(string name, string state = "Stopped") => new(name, state, []);

    [Fact]
    public void VolleAbdeckungZuerstDannWenigsteFehlendeDannKleinsterUeberschuss()
    {
        var inventory = Inventory(("big", ["dotnet", "git", "node", "python"]), ("exact", ["dotnet", "git"]), ("half", ["git"]));

        var ranked = RepoMatcher.Rank(["dotnet", "git"], inventory, [Vm("big"), Vm("exact"), Vm("half"), Vm("fremd")], []);

        Assert.Equal(["exact", "big", "half", "fremd"], ranked.Select(c => c.Label));
        Assert.True(ranked[0].Covers);
        Assert.Equal(["node", "python"], ranked[1].Surplus);
        Assert.False(ranked[3].Known);

        var plan = RepoMatcher.Plan(["dotnet", "git"], ranked, "CoreBridge", ["big", "exact", "half", "fremd"]);
        Assert.Equal(MigrationKind.UseVm, plan.Kind);
        Assert.Equal("exact", plan.Vm);
    }

    [Fact]
    public void SnapshotAusKommentarPasst()
    {
        var inventory = Inventory(("dev", ["git"]));
        var snapshots = new[] { new VmSnapshotInfo("dev", "base", null, SnapshotComment.Format("frisch", ["dotnet", "git"], "dev")) };

        var ranked = RepoMatcher.Rank(["dotnet", "git"], inventory, [Vm("dev")], snapshots);
        var plan = RepoMatcher.Plan(["dotnet", "git"], ranked, "x", ["dev"]);

        Assert.Equal("dev.base", ranked[0].Label);
        Assert.Equal(MigrationKind.RestoreSnapshot, plan.Kind);
        Assert.Equal("cb vm-restore -n dev -s base", plan.Commands()[0]);
    }

    [Fact]
    public void TeilweiseKlonenUndErgaenzen()
    {
        var inventory = Inventory(("dotnet-vm", ["dotnet", "git", "claude"]));

        var ranked = RepoMatcher.Rank(["git", "node"], inventory, [Vm("dotnet-vm")], []);
        var plan = RepoMatcher.Plan(["git", "node"], ranked, "web-app", ["dotnet-vm"]);

        Assert.Equal(MigrationKind.CloneAndInstall, plan.Kind);
        Assert.Equal(["node"], plan.Missing);
        Assert.Equal(
            ["cb vm-clone -s dotnet-vm -n web-app", "cb vm-install -n web-app -c node --snapshot base", "cb repo-send -n web-app"],
            plan.Commands());
    }

    [Fact]
    public void OhneBrauchbareVmNeuAusProfil()
    {
        var plan = RepoMatcher.Plan(["git", "node"], RepoMatcher.Rank(["git", "node"], new VmInventory(), [Vm("fremd")], []), "My_Repo", ["my-repo"]);

        Assert.Equal(MigrationKind.CreateFromProfile, plan.Kind);
        Assert.Equal("my-repo-2", plan.Vm);
        Assert.Equal("cb init-configure -n my-repo-2 -c git,node", plan.Commands()[0]);
    }

    [Fact]
    public void KommentarHinUndZurueck()
    {
        var comment = SnapshotComment.Format("after configuration (dev)", ["claude", "dotnet", "git"], "dev");

        Assert.Equal("after configuration (dev) — corebridge: components=claude,dotnet,git; profile=dev", comment);
        var parsed = SnapshotComment.Parse(comment)!.Value;
        Assert.Equal(["claude", "dotnet", "git"], parsed.Components);
        Assert.Equal("dev", parsed.Profile);
        Assert.Null(SnapshotComment.Parse("Backup"));
        Assert.Equal([], SnapshotComment.Parse(SnapshotComment.Format(null, [], null))!.Value.Components);
    }

    [Fact]
    public void InventarUeberlebtSpeichern()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, VmInventory.FileName);

        VmInventory.Update(path, inventory =>
        {
            inventory.Vm("Dev").Components = VmInventory.Merge(["git"], ["dotnet", "git"]);
            inventory.Repos["/r"] = "dev";
        });

        var loaded = VmInventory.Load(path);
        Assert.Equal(["dotnet", "git"], loaded.Vms["dev"].Components);
        Assert.False(loaded.Vms["DEV"].Known);
        Assert.Equal("dev", loaded.Repos["/r"]);
    }
}
