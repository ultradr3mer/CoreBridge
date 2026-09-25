using System.Net;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;
using YamlDotNet.RepresentationModel;

namespace CoreBridge.Core.Tests;

public class ImageCloudInitTests
{
    [Fact]
    public void NeedsCloudInit_TrueSobaldGastProvisionierungVorhanden()
    {
        var config = Config(
            ("claude", new ComponentConfig
            {
                Script = "scripts/components/install-claude.sh",
                InstallPath = "/usr/local/sbin/corebridge-install-claude",
                Packages = ["bash"]
            }),
            ("opencode", new ComponentConfig { Snap = "opencode" }));

        Assert.True(ImageCloudInit.NeedsCloudInit(config, ["claude"]));
        Assert.True(ImageCloudInit.NeedsCloudInit(config, ["opencode"]));
        Assert.True(ImageCloudInit.NeedsCloudInit(config, ["claude", "opencode"]));
        Assert.False(ImageCloudInit.NeedsCloudInit(config, []));
    }

    [Fact]
    public void Generate_RendertPackagesUsersScriptUndRuncmd()
    {
        var config = Config(
            ("claude", new ComponentConfig
            {
                Script = "scripts/components/install-claude.sh",
                InstallPath = "/usr/local/sbin/corebridge-install-claude",
                Packages = ["bash", "curl", "bash"],
                PackageUpdate = true,
                Users =
                [
                    new CloudInitUserConfig
                    {
                        Name = "claude",
                        Sudo = "ALL=(ALL) NOPASSWD:ALL",
                        Groups = ["sudo"]
                    }
                ]
            }));

        var yaml = ImageCloudInit.Generate("agent", config, ["claude"]);
        var root = ParseRoot(yaml);

        Assert.Equal("true", Scalar(Required(root, "package_update")));
        Assert.Equal("false", Scalar(Required(root, "package_upgrade")));
        Assert.Equal(["bash", "curl"], Scalars(Required(root, "packages")));

        var users = Sequence(Required(root, "users"));
        Assert.Single(users);
        Assert.Equal("claude", Scalar(Required(Mapping(users[0]), "name")));
        Assert.Equal("ALL=(ALL) NOPASSWD:ALL", Scalar(Required(Mapping(users[0]), "sudo")));
        Assert.Equal(["sudo"], Scalars(Required(Mapping(users[0]), "groups")));

        var files = Sequence(Required(root, "write_files"));
        Assert.Single(files);
        Assert.Equal(
            "/usr/local/sbin/corebridge-install-claude",
            Scalar(Required(Mapping(files[0]), "path")));
        Assert.Contains(
            "curl -fsSL https://claude.ai/install.sh | bash -s stable",
            Scalar(Required(Mapping(files[0]), "content")));

        Assert.Equal(
            ["/usr/local/sbin/corebridge-install-claude", "ubuntu"],
            Scalars(Sequence(Required(root, "runcmd"))[0]));
        Assert.DoesNotContain("exit 0", yaml);
    }

    [Fact]
    public void Generate_RendertSnapAlsArgumentliste()
    {
        var config = Config(
            ("opencode", new ComponentConfig { Snap = "opencode", Classic = true }),
            ("python", new ComponentConfig { Snap = "python3-alt", Classic = true }));

        var yaml = ImageCloudInit.Generate("agent", config, ["opencode", "python"]);
        var root = ParseRoot(yaml);
        var commands = Sequence(Required(Mapping(Required(root, "snap")), "commands"));

        Assert.Equal(["false", "false"], new[]
        {
            Scalar(Required(root, "package_update")),
            Scalar(Required(root, "package_upgrade"))
        });
        Assert.Equal(
            ["snap", "install", "opencode", "--classic"],
            Scalars(commands[0]));
        Assert.Equal(
            ["snap", "install", "python3-alt", "--classic"],
            Scalars(commands[1]));
        Assert.False(TryGet(root, "runcmd", out _));
        Assert.False(TryGet(root, "write_files", out _));
    }

    [Fact]
    public void Generate_MergtIdentischeUserDefinitionen()
    {
        var user = new CloudInitUserConfig
        {
            Name = "agent",
            Groups = ["users", "users"]
        };
        var config = Config(
            ("one", new ComponentConfig { Users = [user] }),
            ("two", new ComponentConfig { Users = [user with { Groups = ["users"] }] }));

        var yaml = ImageCloudInit.Generate("agent", config, ["one", "two"]);
        var users = Sequence(Required(ParseRoot(yaml), "users"));

        Assert.Single(users);
        Assert.Equal("agent", Scalar(Required(Mapping(users[0]), "name")));
        Assert.Equal(["users"], Scalars(Required(Mapping(users[0]), "groups")));
    }

    [Fact]
    public void Generate_LehntKonflikthafteUserDefinitionenAb()
    {
        var config = Config(
            ("one", new ComponentConfig
            {
                Users = [new CloudInitUserConfig { Name = "agent" }]
            }),
            ("two", new ComponentConfig
            {
                Users = [new CloudInitUserConfig { Name = "agent", Shell = "/bin/sh" }]
            }));

        var exception = Assert.Throws<MultipassException>(
            () => ImageCloudInit.Generate("agent", config, ["one", "two"]));

        Assert.Contains("unterschiedlich", exception.Message);
    }

    [Fact]
    public void Generate_LehntFehlendenInstallPathAb()
    {
        var config = Config(
            ("broken", new ComponentConfig { Script = "scripts/components/install-claude.sh" }));

        var exception = Assert.Throws<MultipassException>(
            () => ImageCloudInit.Generate("agent", config, ["broken"]));

        Assert.Contains("InstallPath", exception.Message);
    }

    [Fact]
    public void Generate_BewaahrtAusgewahlteScriptUndRuncmdReihenfolge()
    {
        var config = Config(
            ("zeta", new ComponentConfig
            {
                Script = "scripts/components/install-dotnet.sh",
                InstallPath = "/opt/corebridge/zeta",
                InstallUser = "ubuntu"
            }),
            ("alpha", new ComponentConfig
            {
                Script = "scripts/components/install-claude.sh",
                InstallPath = "/opt/corebridge/alpha",
                InstallUser = "ubuntu"
            }));

        var yaml = ImageCloudInit.Generate("agent", config, ["zeta", "alpha"]);
        var root = ParseRoot(yaml);
        var files = Sequence(Required(root, "write_files"));
        var commands = Sequence(Required(root, "runcmd"));

        Assert.Equal(
            ["/opt/corebridge/zeta", "/opt/corebridge/alpha"],
            files.Select(file => Scalar(Required(Mapping(file), "path"))));
        Assert.Equal(
            [["/opt/corebridge/zeta", "ubuntu"], ["/opt/corebridge/alpha", "ubuntu"]],
            commands.Select(command => Scalars(command)));
    }

    [Theory]
    [InlineData("usr/local/sbin/corebridge-install-claude")]
    [InlineData("/usr/local/../sbin/corebridge-install-claude")]
    public void Generate_LehntNichtNormalisiertenInstallPathAb(string installPath)
    {
        var config = Config(
            ("broken", new ComponentConfig
            {
                Script = "scripts/components/install-claude.sh",
                InstallPath = installPath
            }));

        var exception = Assert.Throws<MultipassException>(
            () => ImageCloudInit.Generate("agent", config, ["broken"]));

        Assert.Contains("normalisierter absoluter Unix-Pfad", exception.Message);
    }

    [Fact]
    public void Generate_LehntNichtNormalisiertenRelativenScriptPfadAb()
    {
        var config = Config(
            ("broken", new ComponentConfig
            {
                Script = "../outside.sh",
                InstallPath = "/usr/local/sbin/corebridge-install-broken"
            }));

        var exception = Assert.Throws<MultipassException>(
            () => ImageCloudInit.Generate("agent", config, ["broken"]));

        Assert.Contains("normalisierter relativer oder absoluter Pfad", exception.Message);
    }

    [Fact]
    public void Generate_AkzeptiertAbsolutenTestScriptPfad()
    {
        using var temp = new TempDirectory();
        var scriptPath = Path.Combine(temp.Path, "install.sh");
        File.WriteAllText(scriptPath, "#!/bin/sh\necho absolute-script\n");
        var config = Config(
            ("absolute", new ComponentConfig
            {
                Script = scriptPath,
                InstallPath = "/usr/local/sbin/corebridge-install-absolute"
            }));

        var yaml = ImageCloudInit.Generate("agent", config, ["absolute"]);

        Assert.Contains("/usr/local/sbin/corebridge-install-absolute", yaml);
    }

    private static YamlMappingNode ParseRoot(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var document = Assert.Single(stream.Documents);
        return Assert.IsType<YamlMappingNode>(document.RootNode);
    }

    private static YamlNode Required(YamlMappingNode mapping, string key)
    {
        Assert.True(TryGet(mapping, key, out var node), $"YAML-Schlüssel '{key}' fehlt.");
        return node!;
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? node)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out node);
    }

    private static YamlMappingNode Mapping(YamlNode node)
    {
        return Assert.IsType<YamlMappingNode>(node);
    }

    private static YamlSequenceNode Sequence(YamlNode node)
    {
        return Assert.IsType<YamlSequenceNode>(node);
    }

    private static string Scalar(YamlNode node)
    {
        return Assert.IsType<YamlScalarNode>(node).Value ?? string.Empty;
    }

    private static string[] Scalars(YamlNode node)
    {
        return Sequence(node).Children.Select(Scalar).ToArray();
    }

    private static ImagesConfig Config(params (string Name, ComponentConfig Component)[] components)
    {
        return new ImagesConfig
        {
            Components = components.ToDictionary(
                component => component.Name,
                component => component.Component,
                StringComparer.OrdinalIgnoreCase)
        };
    }
}

public class ImageComponentsTests
{
    [Fact]
    public void ResolveSnaps_MapptNurKomponentenMitStoreSnap()
    {
        var config = Config(
            ("claude", new ComponentConfig { Script = "scripts/components/install-claude.sh" }),
            ("dotnet", new ComponentConfig { Snap = "dotnet-sdk-100" }),
            ("python", new ComponentConfig { Snap = "python3-alt" }));

        var snaps = ImageComponents.ResolveSnaps(config, ["claude", "dotnet", "python"]);

        Assert.Equal(["dotnet-sdk-100", "python3-alt"], snaps);
    }

    [Fact]
    public void ResolveSnaps_IgnoriertGrossKleinUndLeerzeichen()
    {
        var config = Config(
            ("claude", new ComponentConfig()),
            ("dotnet", new ComponentConfig { Snap = "dotnet-sdk-100" }));

        var snaps = ImageComponents.ResolveSnaps(config, [" Claude ", "DOTNET"]);

        Assert.Equal(["dotnet-sdk-100"], snaps);
    }

    [Fact]
    public void ResolveSnaps_UnbekannteKomponenteWirftMultipassException()
    {
        var config = Config(("claude", new ComponentConfig()));

        Assert.Throws<MultipassException>(
            () => ImageComponents.ResolveSnaps(config, ["rust"]));
    }

    private static ImagesConfig Config(params (string Name, ComponentConfig Component)[] components)
    {
        return new ImagesConfig
        {
            Components = components.ToDictionary(
                component => component.Name,
                component => component.Component,
                StringComparer.OrdinalIgnoreCase)
        };
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
        var config = Config();

        var result = new ImageService(config, http: new HttpClient(new StubHttpHandler()))
            .Configure("agent", ["claude", "dotnet"]);

        Assert.Equal("agent", result.Name);
        Assert.True(File.Exists(result.ModelPath));
        Assert.True(File.Exists(result.ConfigPath));
        Assert.NotNull(result.CloudInitPath);
        Assert.True(File.Exists(result.CloudInitPath));
        Assert.Equal(["dotnet-sdk-100"], result.Snaps);
        Assert.Equal("daily:26.10", result.RuntimeImage);
        Assert.DoesNotContain("HostInstall", File.ReadAllText(result.ConfigPath));
    }

    [Fact]
    public void Configure_BestehendesImage_OhneForceWirftMultipassException()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);
        var config = Config();
        var dir = ImageStore.ModelDir("agent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(ImageStore.ConfigFile("agent"), "x");

        var exception = Assert.Throws<MultipassException>(
            () => new ImageService(config).Configure("agent", ["claude"]));

        Assert.Contains("image-delete", exception.Message);
    }

    [Fact]
    public void Configure_BestehendesImage_MitForceLaeuftWeiter()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);
        var config = Config();
        var dir = ImageStore.ModelDir("agent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(ImageStore.ConfigFile("agent"), "x");

        var result = new ImageService(config, http: new HttpClient(new StubHttpHandler()))
            .Configure("agent", ["claude"], force: true);

        Assert.True(File.Exists(result.ConfigPath));
        Assert.NotNull(result.CloudInitPath);
        Assert.Empty(result.Snaps);
    }

    private static ImagesConfig Config()
    {
        return new ImagesConfig
        {
            Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = new()
                {
                    Script = "scripts/components/install-claude.sh",
                    InstallPath = "/usr/local/sbin/corebridge-install-claude",
                    Packages = ["bash", "curl", "ca-certificates"]
                },
                ["dotnet"] = new()
                {
                    Snap = "dotnet-sdk-100",
                    Script = "scripts/components/install-dotnet.sh",
                    InstallPath = "/usr/local/sbin/corebridge-install-dotnet"
                }
            }
        };
    }
}

[Collection("ImageStore")]
public class ImageServiceListDeleteTests
{
    private static void SeedStore(TempDirectory store, string name, string[] components, string[] snaps)
    {
        ImageStore.Initialize(store);
        var dir = ImageStore.ModelDir(name);
        Directory.CreateDirectory(dir);
        var profileConfig = new ImageProfileConfig(
            name,
            "https://example.invalid/ubuntu-classic-2610-amd64-dangerous.model",
            components,
            snaps);
        File.WriteAllText(ImageStore.ConfigFile(name), profileConfig.ToJson());
    }

    [Fact]
    public void List_LiefertKonfigurierteProfile()
    {
        using var store = new TempDirectory();
        SeedStore(store, "alpha", ["claude"], []);
        SeedStore(store, "beta", ["dotnet"], ["dotnet-sdk-100"]);

        var entries = new ImageService(new ImagesConfig()).List();

        Assert.Equal(2, entries.Count);
        Assert.Equal("alpha", entries[0].Name);
        Assert.Equal(["claude"], entries[0].Components);
        Assert.Empty(entries[0].Snaps);
        Assert.Equal("beta", entries[1].Name);
        Assert.Equal(["dotnet-sdk-100"], entries[1].Snaps);
    }

    [Fact]
    public void List_OhneModelsRoot_LiefertLeereListe()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);

        Assert.Empty(new ImageService(new ImagesConfig()).List());
    }

    [Fact]
    public void Delete_EntferntProfilkonfiguration()
    {
        using var store = new TempDirectory();
        SeedStore(store, "agent", ["claude"], []);

        var result = new ImageService(new ImagesConfig()).Delete("agent");

        Assert.Equal(ImageStore.ModelDir("agent"), result.ModelDir);
        Assert.False(Directory.Exists(result.ModelDir));
    }

    [Fact]
    public void Delete_UnbekannterName_WirftMultipassException()
    {
        using var store = new TempDirectory();
        ImageStore.Initialize(store);

        Assert.Throws<MultipassException>(() => new ImageService(new ImagesConfig()).Delete("gibtsnicht"));
    }
}

public class ImageProfileConfigTests
{
    [Fact]
    public void Json_RoundtripErhaeltAlleFelder()
    {
        var config = new ImageProfileConfig(
            "agent",
            "https://example.invalid/ubuntu-classic-2610-amd64-dangerous.model",
            ["claude", "dotnet"],
            ["dotnet-sdk-100"],
            RuntimeImage: "daily:26.10",
            CloudInitFile: "cloud-init.yaml",
            ModelId: "ubuntu-classic-2610-amd64-dangerous",
            ModelSha256: "abc123");

        var roundtrip = ImageProfileConfig.FromJson(config.ToJson());

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
