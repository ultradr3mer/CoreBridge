using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

[Collection("InitStore")]
public class InitProfileResolverTests
{
    [Fact]
    public void ConfiguredNameWirdZuRuntimeImageUndProvisionierungAufgeloest()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);
        var profileDir = InitStore.ProfileDir("agent");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(InitStore.CloudInitFile("agent"), "#cloud-config\n");

        var config = new InitProfileConfig(
            "agent",
            ["claude", "dotnet"],
            ["dotnet-sdk-100"],
            RuntimeImage: "daily:26.10",
            CloudInitFile: "cloud-init.yaml");
        File.WriteAllText(InitStore.ConfigFile("agent"), config.ToJson());

        var profile = InitProfileResolver.Resolve(
            new InitsConfig { DefaultRuntimeImage = "daily:26.10" },
            "agent");

        Assert.NotNull(profile);
        Assert.Equal("daily:26.10", profile!.RuntimeImage);
        Assert.Equal(Path.GetFullPath(InitStore.CloudInitFile("agent")), profile.CloudInitPath);
    }

    [Fact]
    public void ExpliziteCloudInitHatVorrangVorProfil()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);
        var profileDir = InitStore.ProfileDir("agent");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(InitStore.CloudInitFile("agent"), "#cloud-config\n");

        var config = new InitProfileConfig(
            "agent",
            ["dotnet"],
            ["dotnet-sdk-100"],
            RuntimeImage: "daily:26.10",
            CloudInitFile: "cloud-init.yaml");
        File.WriteAllText(InitStore.ConfigFile("agent"), config.ToJson());

        var explicitPath = Path.Combine(store.Path, "explicit.yaml");
        var profile = InitProfileResolver.Resolve(
            new InitsConfig(),
            "agent",
            explicitPath);

        Assert.Equal(explicitPath, profile!.CloudInitPath);
    }

    [Fact]
    public void KatalogUndURLsBleibenUnveraendert()
    {
        var config = new InitsConfig();

        Assert.Null(InitProfileResolver.Resolve(config, "daily:26.10"));
        Assert.Null(InitProfileResolver.Resolve(config, "26.04"));
        Assert.Null(InitProfileResolver.Resolve(config, "https://example.invalid/image.img"));
        Assert.Null(InitProfileResolver.Resolve(config, @"C:\images\agent.img"));
    }

    [Fact]
    public void ProfilAusDerModelAeraWirdNichtStillAkzeptiert()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);
        Directory.CreateDirectory(InitStore.ProfileDir("legacy"));
        // Schema 3 = Profil, das noch ein heruntergeladenes Modell referenzierte.
        File.WriteAllText(
            InitStore.ConfigFile("legacy"),
            new InitProfileConfig(
                "legacy",
                ["dotnet"],
                ["dotnet-sdk-100"],
                RuntimeImage: "core26",
                SchemaVersion: 3).ToJson());

        var exception = Assert.Throws<MultipassException>(
            () => InitProfileResolver.Resolve(new InitsConfig(), "legacy"));

        Assert.Contains("init-delete", exception.Message);
    }

    /// <summary>
    /// <c>vm-create --profile</c> mit Tippfehler: Meldung mit den vorhandenen Profilen
    /// statt eines Katalog-Lookups durch Multipass (Issue #4).
    /// </summary>
    [Fact]
    public void VertipptesProfilNenntDieVorhandenen()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);
        foreach (var name in new[] { "web", "dev" })
        {
            Directory.CreateDirectory(InitStore.ProfileDir(name));
            File.WriteAllText(InitStore.ConfigFile(name), new InitProfileConfig(name, ["git"], []).ToJson());
        }

        var exception = Assert.Throws<MultipassException>(
            () => InitProfileResolver.ResolveRequired(new InitsConfig(), "dve"));

        Assert.Contains("'dve' existiert nicht", exception.Message);
        Assert.Contains("Vorhandene Profile: dev, web.", exception.Message);
        Assert.Equal("dev", InitProfileResolver.ResolveRequired(new InitsConfig(), "dev").Name);
    }

    [Fact]
    public void OhneProfileZeigtDieMeldungInitConfigure()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);

        var exception = Assert.Throws<MultipassException>(
            () => InitProfileResolver.ResolveRequired(new InitsConfig(), "daily:26.10"));

        Assert.Contains("init-configure", exception.Message);
    }
}
