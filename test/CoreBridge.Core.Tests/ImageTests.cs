using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class TestConfigs
{
    /// <summary>Konfig-Äquivalent zu src/CoreBridge.Cli/appsettings.json.</summary>
    public static ImagesConfig Images { get; } = new()
    {
        DefaultModelUrl = "https://raw.githubusercontent.com/canonical/models/main/ubuntu-classic-2610-amd64-dangerous.model",
        DefaultRuntimeImage = "daily:26.10",
        Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new()
            {
                CloudInitScript = "scripts/components/install-claude.sh",
                CloudInitInstallPath = "/usr/local/sbin/corebridge-install-claude",
                CloudInitPackages = ["bash", "curl", "ca-certificates"]
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
            ["opencode"] = new() { Snap = "opencode", Classic = true },
            ["python"] = new()
            {
                Snap = "python3-alt",
                Classic = true,
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
    public void NeedsCloudInit_TrueSobaldGastProvisionierungVorhanden()
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
        Assert.Contains("chown -h ubuntu:ubuntu /home/ubuntu/.local/bin/dotnet", yaml);
        Assert.DoesNotContain("chown -R ubuntu:ubuntu /home/ubuntu\n", yaml);
    }

    [Fact]
    public void GenerateClassicSnaps_BekommenClassicFlag()
    {
        var yaml = ImageCloudInit.Generate("agent", TestConfigs.Images, new[] { "opencode", "python" });

        Assert.Contains("- snap install opencode --classic", yaml);
        Assert.Contains("- snap install python3-alt --classic", yaml);
        Assert.DoesNotContain("snap install claude-code --classic", yaml);
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
    public void GenerateEnthaeltClaudeInCloudInit()
    {
        var yaml = ImageCloudInit.Generate("agent", TestConfigs.Images, new[] { "claude", "opencode" });

        Assert.Contains("package_update: true", yaml);
        Assert.Contains("  - curl", yaml);
        Assert.Contains("path: /usr/local/sbin/corebridge-install-claude", yaml);
        Assert.Contains("curl -fsSL https://claude.ai/install.sh | bash -s stable", yaml);
        Assert.Contains("runcmd:", yaml);
        Assert.Contains("  - [/usr/local/sbin/corebridge-install-claude]", yaml);
        Assert.Contains("snap:", yaml);
        Assert.Contains("- snap install opencode", yaml);
        Assert.DoesNotContain("install-claude.ps1", yaml);
        Assert.DoesNotContain("Host-Install", yaml);
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
    public void ResolveSnaps_MapptNurKomponentenMitStoreSnap()
    {
        var snaps = ImageComponents.ResolveSnaps(TestConfigs.Images, new[] { "claude", "dotnet", "python" });

        Assert.Equal(new[] { "dotnet-sdk-100", "python3-alt" }, snaps);
    }

    [Fact]
    public void ResolveSnaps_IgnoriertGrossKleinUndLeerzeichen()
    {
        var snaps = ImageComponents.ResolveSnaps(TestConfigs.Images, new[] { " Claude ", "DOTNET" });

        Assert.Equal(new[] { "dotnet-sdk-100" }, snaps);
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
                Content = new StringContent(
                    "type: model\n" +
                    "model: ubuntu-classic-2610-amd64-dangerous\n" +
                    "base: core24\n" +
                    "classic: true\n" +
                    "distribution: ubuntu\n" +
                    "grade: dangerous\n")
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
        Assert.Equal(new[] { "dotnet-sdk-100" }, result.Snaps);
        Assert.Equal("daily:26.10", result.RuntimeImage);
        Assert.DoesNotContain("HostInstall", File.ReadAllText(result.ConfigPath));
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
        Assert.NotNull(result.CloudInitPath);
        Assert.Empty(result.Snaps);
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
        SeedStore(store, "alpha", ["claude"], [], withImage: false);
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
        SeedStore(store, "agent", ["claude"], [], withImage: true);
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
            new[] { "dotnet-sdk-100" },
            RuntimeImage: "daily:26.10",
            CloudInitFile: "cloud-init.yaml",
            ModelId: "ubuntu-classic-2610-amd64-dangerous",
            ModelSha256: "abc123");

        var roundtrip = ImageBuildConfig.FromJson(config.ToJson());

        Assert.Equal(config.Name, roundtrip.Name);
        Assert.Equal(config.BaseModelUrl, roundtrip.BaseModelUrl);
        Assert.Equal(config.Components, roundtrip.Components);
        Assert.Equal(config.Snaps, roundtrip.Snaps);
        Assert.Equal(config.RuntimeImage, roundtrip.RuntimeImage);
        Assert.Equal(config.CloudInitFile, roundtrip.CloudInitFile);
        Assert.Equal(config.ModelId, roundtrip.ModelId);
        Assert.Equal(config.ModelSha256, roundtrip.ModelSha256);
        Assert.Equal(3, roundtrip.SchemaVersion);
    }
}
