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
            new ImagesConfig { DefaultRuntimeImage = "daily:26.10" },
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
            new ImagesConfig(),
            "agent",
            explicitPath);

        Assert.Equal(explicitPath, profile!.CloudInitPath);
    }

    [Fact]
    public void KatalogUndURLsBleibenUnveraendert()
    {
        var config = new ImagesConfig();

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
            () => InitProfileResolver.Resolve(new ImagesConfig(), "legacy"));

        Assert.Contains("init-delete", exception.Message);
    }
}
