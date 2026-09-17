using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class TestConfigs
{
    /// <summary>Konfig-Äquivalent zu src/CoreBridge.Cli/appsettings.json.</summary>
    public static ImagesConfig Images { get; } = new()
    {
        Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = new() { Snap = "claude-code" },
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
    public void NeedsCloudInit_FalseBeiNurSnapKomponenten()
    {
        Assert.False(ImageCloudInit.NeedsCloudInit(TestConfigs.Images, new[] { "claude", "opencode" }));
    }

    [Fact]
    public void NeedsCloudInit_TrueBeiDotnetOderPython()
    {
        Assert.True(ImageCloudInit.NeedsCloudInit(TestConfigs.Images, new[] { "claude", "dotnet" }));
        Assert.True(ImageCloudInit.NeedsCloudInit(TestConfigs.Images, new[] { "python" }));
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
        Assert.Contains("chown -R ubuntu:ubuntu /home/ubuntu/.local", yaml);
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
    public void GenerateOhneRest_HatKeineRuncmdSektion()
    {
        var yaml = ImageCloudInit.Generate("agent", TestConfigs.Images, new[] { "claude", "opencode" });

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

public class ImageBuildConfigTests
{
    [Fact]
    public void Json_RoundtripErhaeltAlleFelder()
    {
        var config = new ImageBuildConfig(
            "agent",
            ImageStore.DefaultModelUrl,
            new[] { "claude", "dotnet" },
            new[] { "claude-code", "dotnet-sdk-100" });

        var roundtrip = ImageBuildConfig.FromJson(config.ToJson());

        Assert.Equal(config.Name, roundtrip.Name);
        Assert.Equal(config.BaseModelUrl, roundtrip.BaseModelUrl);
        Assert.Equal(config.Components, roundtrip.Components);
        Assert.Equal(config.Snaps, roundtrip.Snaps);
    }
}
