using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class ModelDescriptorTests
{
    private const string ClassicModel = """
        type: model
        model: ubuntu-classic-2610-amd64-dangerous
        base: core24
        classic: true
        distribution: ubuntu
        grade: dangerous
        snaps:
          - name: core24
        """;

    [Fact]
    public void ClassicModelWirdAkzeptiertObwohlBaseCore24Ist()
    {
        var descriptor = ModelDescriptorParser.Parse(ClassicModel);

        Assert.Equal("ubuntu-classic-2610-amd64-dangerous", descriptor.Model);
        Assert.Equal("core24", descriptor.Base);
        Assert.True(descriptor.Classic);
        Assert.Equal("ubuntu", descriptor.Distribution);
    }

    [Fact]
    public void UbuntuCoreModelWirdAbgewiesen()
    {
        var model = ClassicModel.Replace(
            "ubuntu-classic-2610-amd64-dangerous",
            "ubuntu-core-26-amd64-dangerous");

        var exception = Assert.Throws<InvalidDataException>(
            () => ModelDescriptorParser.ValidateClassic(model));

        Assert.Contains("ubuntu-classic", exception.Message);
    }

    [Fact]
    public void FehlendesClassicFlagWirdAbgewiesen()
    {
        var model = ClassicModel.Replace("classic: true", "classic: false");

        Assert.Throws<InvalidDataException>(() => ModelDescriptorParser.ValidateClassic(model));
    }
}

[Collection("ImageStore")]
public class ImageProfileResolverTests
{
    [Fact]
    public void ConfiguredNameWirdZuRuntimeImageUndProvisionierungAufgeloest()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);
        var modelDir = ImageStore.ModelDir("agent");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(ImageStore.CloudInitFile("agent"), "#cloud-config\n");

        var config = new ImageProfileConfig(
            "agent",
            "https://example.invalid/ubuntu-classic-2610-amd64-dangerous.model",
            ["claude", "dotnet"],
            ["dotnet-sdk-100"],
            RuntimeImage: "daily:26.10",
            CloudInitFile: "cloud-init.yaml",
            ModelId: "ubuntu-classic-2610-amd64-dangerous",
            ModelSha256: "abc123");
        File.WriteAllText(ImageStore.ConfigFile("agent"), config.ToJson());

        var profile = ImageProfileResolver.Resolve(
            new ImagesConfig { DefaultRuntimeImage = "daily:26.10" },
            "agent");

        Assert.NotNull(profile);
        Assert.Equal("daily:26.10", profile!.RuntimeImage);
        Assert.Equal(Path.GetFullPath(ImageStore.CloudInitFile("agent")), profile.CloudInitPath);
    }

    [Fact]
    public void ExpliziteCloudInitHatVorrangVorProfil()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);
        var modelDir = ImageStore.ModelDir("agent");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(ImageStore.CloudInitFile("agent"), "#cloud-config\n");

        var config = new ImageProfileConfig(
            "agent",
            "https://example.invalid/model",
            ["dotnet"],
            ["dotnet-sdk-100"],
            RuntimeImage: "daily:26.10",
            CloudInitFile: "cloud-init.yaml",
            ModelId: "ubuntu-classic-2610-amd64-dangerous",
            ModelSha256: "abc123");
        File.WriteAllText(ImageStore.ConfigFile("agent"), config.ToJson());

        var explicitPath = Path.Combine(store.Path, "explicit.yaml");
        var profile = ImageProfileResolver.Resolve(
            new ImagesConfig(),
            "agent",
            explicitPath);

        Assert.Equal(explicitPath, profile!.CloudInitPath);
    }

    [Fact]
    public void KatalogUndURLsBleibenUnveraendert()
    {
        var config = new ImagesConfig();

        Assert.Null(ImageProfileResolver.Resolve(config, "daily:26.10"));
        Assert.Null(ImageProfileResolver.Resolve(config, "26.04"));
        Assert.Null(ImageProfileResolver.Resolve(config, "https://example.invalid/image.img"));
        Assert.Null(ImageProfileResolver.Resolve(config, @"C:\images\agent.img"));
    }

    [Fact]
    public void AltesProfilWirdNichtStillAufClassicUmgestellt()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);
        Directory.CreateDirectory(ImageStore.ModelDir("legacy"));
        File.WriteAllText(
            ImageStore.ConfigFile("legacy"),
            new ImageProfileConfig(
                "legacy",
                "https://example.invalid/core.model",
                ["dotnet"],
                ["dotnet-sdk-100"],
                RuntimeImage: "core26").ToJson());

        var exception = Assert.Throws<MultipassException>(
            () => ImageProfileResolver.Resolve(new ImagesConfig(), "legacy"));

        Assert.Contains("image-delete", exception.Message);
    }
}
