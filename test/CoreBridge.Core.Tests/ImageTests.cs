using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class TestConfigs
{
    /// <summary>Konfig-Äquivalent zu src/CoreBridge.Cli/appsettings.json.</summary>
    public static ImagesConfig Images { get; } = new()
    {
        DefaultModelUrl = "https://raw.githubusercontent.com/canonical/models/main/ubuntu-core-26-amd64-dangerous.model",
        Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new()
            {
                Snap = "claude-code",
                Symlinks = ["/snap/bin/claude-code|/home/ubuntu/.local/bin/claude"]
            },
            ["dotnet"] = new()
            {
                Snap = "dotnet-sdk-100",
                Symlinks = ["/snap/dotnet-sdk-100/current/usr/bin/dotnet|/home/ubuntu/.local/bin/dotnet"],
                ProfileExports =
                [
                    "DOTNET_ROOT=/snap/dotnet-sdk-100/current/usr/lib/dotnet",
                    "LD_LIBRARY_PATH=$HOME/.local/lib:$LD_LIBRARY_PATH"
                ],
                VendoredLibs = new VendoredLibsConfig
                {
                    LddTarget = "/snap/dotnet-sdk-100/current/usr/bin/dotnet",
                    Packages = ["libunwind8", "libicu78", "libbrotli1", "libgssapi-krb5-2", "libssl3t64", "zlib1g"],
                    ExtractGlob = "usr/lib/x86_64-linux-gnu/*.so.*",
                    Destination = "/home/ubuntu/.local/lib"
                }
            },
            ["opencode"] = new() { Snap = "opencode" },
            ["python"] = new()
            {
                Snap = "python3-alt",
                Symlinks =
                [
                    "/snap/bin/python3-alt.3-14|/home/ubuntu/.local/bin/python",
                    "/snap/bin/python3-alt.pip3-14|/home/ubuntu/.local/bin/pip"
                ]
            }
        }
    };
}

public class ImageCloudInitTests
{
    [Fact]
    public void NeedsCloudInit_TrueSobaldKomponentenGewaehlt()
    {
        Assert.True(ImageCloudInit.NeedsCloudInit(TestConfigs.Images, new[] { "claude" }));
        Assert.True(ImageCloudInit.NeedsCloudInit(TestConfigs.Images, new[] { "claude", "dotnet" }));
        Assert.False(ImageCloudInit.NeedsCloudInit(TestConfigs.Images, Array.Empty<string>()));
    }

    [Fact]
    public void GenerateDotnet_EnthaeltProfileExportsSymlinkUndVendoring()
    {
        var yaml = ImageCloudInit.Generate("agent", TestConfigs.Images, new[] { "dotnet" });

        Assert.StartsWith("#cloud-config", yaml);
        Assert.Contains("path: /home/ubuntu/.profile", yaml);
        Assert.Contains("append: true", yaml);
        Assert.Contains("export DOTNET_ROOT=/snap/dotnet-sdk-100/current/usr/lib/dotnet", yaml);
        Assert.Contains("export LD_LIBRARY_PATH=$HOME/.local/lib:$LD_LIBRARY_PATH", yaml);
        Assert.Contains("ln -sf /snap/dotnet-sdk-100/current/usr/bin/dotnet /home/ubuntu/.local/bin/dotnet", yaml);
        Assert.Contains("apt-get download", yaml);
        Assert.Contains("dpkg -x", yaml);
        Assert.Contains("libunwind8", yaml);
        Assert.Contains("chown -R ubuntu:ubuntu /home/ubuntu", yaml);
    }

    [Fact]
    public void GeneratePython_EnthaeltPythonUndPipSymlinks()
    {
        var yaml = ImageCloudInit.Generate("agent", TestConfigs.Images, new[] { "python" });

        Assert.Contains("ln -sf /snap/bin/python3-alt.3-14 /home/ubuntu/.local/bin/python", yaml);
        Assert.Contains("ln -sf /snap/bin/python3-alt.pip3-14 /home/ubuntu/.local/bin/pip", yaml);
        Assert.DoesNotContain("DOTNET_ROOT", yaml);
        Assert.DoesNotContain("apt-get download", yaml);
    }

    [Fact]
    public void GenerateEnthaeltSnapInstallationen()
    {
        var yaml = ImageCloudInit.Generate("agent", TestConfigs.Images, new[] { "claude", "opencode" });

        Assert.Contains("snap:", yaml);
        Assert.Contains("- snap install claude-code", yaml);
        Assert.Contains("- snap install opencode", yaml);
    }

    [Fact]
    public void GenerateNurSnapKomponenten_HatKeineRuncmdSektion()
    {
        var yaml = ImageCloudInit.Generate("agent", TestConfigs.Images, new[] { "opencode" });

        Assert.Contains("- snap install opencode", yaml);
        Assert.DoesNotContain("runcmd:", yaml);
        Assert.DoesNotContain("write_files:", yaml);
    }
}

public class ImageComponentsTests
{
    [Fact]
    public void ResolveSnaps_MapptKomponentenAufStoreSnaps()
    {
        var snaps = ImageComponents.ResolveSnaps(TestConfigs.Images, new[] { "claude", "dotnet", "python" });

        Assert.Equal(new[] { "claude-code", "dotnet-sdk-100", "python3-alt" }, snaps);
    }

    [Fact]
    public void ResolveSnaps_IgnoriertGrossKleinUndLeerzeichen()
    {
        var snaps = ImageComponents.ResolveSnaps(TestConfigs.Images, new[] { " Claude ", "DOTNET" });

        Assert.Equal(new[] { "claude-code", "dotnet-sdk-100" }, snaps);
    }

    [Fact]
    public void ResolveSnaps_UnbekannteKomponenteWirftMultipassException()
    {
        Assert.Throws<MultipassException>(
            () => ImageComponents.ResolveSnaps(TestConfigs.Images, new[] { "rust" }));
    }
}

/// <summary>
/// ImageStore ist statisch — alle Tests, die Initialize aufrufen, müssen serialisiert
/// laufen (xUnit-Collection), sonst verbiegen parallel laufende Klassen den Root.
/// </summary>
[Collection("ImageStore")]
public class ImageServiceConfigureGuardTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("type: model\n...")
            });
        }
    }

    [Fact]
    public void Configure_NeuesImage_SchreibtModellConfigCloudInit()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);

        var result = new ImageService(TestConfigs.Images, http: new HttpClient(new StubHttpHandler()))
            .Configure("agent", ["claude", "dotnet"]);

        Assert.Equal("agent", result.Name);
        Assert.True(File.Exists(result.ModelPath));
        Assert.True(File.Exists(result.ConfigPath));
        Assert.NotNull(result.CloudInitPath);
        Assert.True(File.Exists(result.CloudInitPath));
        Assert.Equal(new[] { "claude-code", "dotnet-sdk-100" }, result.Snaps);
    }
    [Fact]
    public void Configure_BestehendesImage_OhneForceWirftMultipassException()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);
        var dir = ImageStore.ModelDir("agent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(ImageStore.ConfigFile("agent"), "x");

        var exception = Assert.Throws<MultipassException>(
            () => new ImageService(TestConfigs.Images).Configure("agent", ["claude"]));
        Assert.Contains("image-delete", exception.Message);
    }

    [Fact]
    public void Configure_BestehendesImage_MitForceLaeuftWeiter()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);
        var dir = ImageStore.ModelDir("agent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(ImageStore.ConfigFile("agent"), "x");

        // Force überspringt den Guard und konfiguriert neu (Stub-HTTP, offline).
        var result = new ImageService(TestConfigs.Images, http: new HttpClient(new StubHttpHandler()))
            .Configure("agent", ["claude"], force: true);

        Assert.True(File.Exists(result.ConfigPath));
    }
}

[Collection("ImageStore")]
public class ImageServiceListDeleteTests
{
    private static void SeedStore(TempDirectory store, string name, string[] components, string[] snaps, bool withImage)
    {
        ImageStore.Initialize(store);
        var dir = ImageStore.ModelDir(name);
        Directory.CreateDirectory(dir);
        var buildConfig = new ImageBuildConfig(
            name,
            TestConfigs.Images.DefaultModelUrl,
            components,
            snaps,
            withImage ? ImageStore.ImageFile(name) : null);
        File.WriteAllText(ImageStore.ConfigFile(name), buildConfig.ToJson());
        if (withImage)
        {
            Directory.CreateDirectory(ImageStore.ImagesRoot);
            File.WriteAllText(ImageStore.ImageFile(name), new string('x', 1024));
        }
    }

    [Fact]
    public void List_LiefertKonfigurierteImages()
    {
        using var store = new TempDirectory();
        SeedStore(store, "alpha", ["claude"], ["claude-code"], withImage: false);
        SeedStore(store, "beta", ["dotnet"], ["dotnet-sdk-100"], withImage: true);

        var entries = new ImageService(TestConfigs.Images).List();

        Assert.Equal(2, entries.Count);
        Assert.Equal("alpha", entries[0].Name);
        Assert.False(entries[0].HasImage);
        Assert.Equal("beta", entries[1].Name);
        Assert.True(entries[1].HasImage);
        Assert.Equal(1024, entries[1].ImageBytes);
    }

    [Fact]
    public void List_OhneModelsRoot_LiefertLeereListe()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);

        Assert.Empty(new ImageService(TestConfigs.Images).List());
    }

    [Fact]
    public void Delete_EntferntKonfigurationUndImage()
    {
        using var store = new TempDirectory();
        SeedStore(store, "agent", ["claude"], ["claude-code"], withImage: true);
        var modelDir = ImageStore.ModelDir("agent");
        var imageFile = ImageStore.ImageFile("agent");

        var result = new ImageService(TestConfigs.Images).Delete("agent");

        Assert.Equal(modelDir, result.ModelDir);
        Assert.Equal(imageFile, result.ImageFile);
        Assert.False(Directory.Exists(modelDir));
        Assert.False(File.Exists(imageFile));
    }

    [Fact]
    public void Delete_UnbekannterName_WirftMultipassException()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);

        Assert.Throws<MultipassException>(() => new ImageService(TestConfigs.Images).Delete("gibtsnicht"));
    }
}

public class ImageBuildConfigTests
{
    [Fact]
    public void Json_RoundtripErhaeltAlleFelder()
    {
        var config = new ImageBuildConfig(
            "agent",
            TestConfigs.Images.DefaultModelUrl,
            new[] { "claude", "dotnet" },
            new[] { "claude-code", "dotnet-sdk-100" });

        var roundtrip = ImageBuildConfig.FromJson(config.ToJson());

        Assert.Equal(config.Name, roundtrip.Name);
        Assert.Equal(config.BaseModelUrl, roundtrip.BaseModelUrl);
        Assert.Equal(config.Components, roundtrip.Components);
        Assert.Equal(config.Snaps, roundtrip.Snaps);
    }
}
