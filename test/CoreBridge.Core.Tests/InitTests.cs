using System.Net;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;
using YamlDotNet.RepresentationModel;

namespace CoreBridge.Core.Tests;

public class InitCloudInitTests
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

        Assert.True(InitCloudInit.NeedsCloudInit(config, ["claude"]));
        Assert.True(InitCloudInit.NeedsCloudInit(config, ["opencode"]));
        Assert.True(InitCloudInit.NeedsCloudInit(config, ["claude", "opencode"]));
        Assert.False(InitCloudInit.NeedsCloudInit(config, []));
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

        var yaml = InitCloudInit.Generate("agent", config, ["claude"]);
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

        var yaml = InitCloudInit.Generate("agent", config, ["opencode", "python"]);
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

        var yaml = InitCloudInit.Generate("agent", config, ["one", "two"]);
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
            () => InitCloudInit.Generate("agent", config, ["one", "two"]));

        Assert.Contains("unterschiedlich", exception.Message);
    }

    [Fact]
    public void Generate_LehntFehlendenInstallPathAb()
    {
        var config = Config(
            ("broken", new ComponentConfig { Script = "scripts/components/install-claude.sh" }));

        var exception = Assert.Throws<MultipassException>(
            () => InitCloudInit.Generate("agent", config, ["broken"]));

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

        var yaml = InitCloudInit.Generate("agent", config, ["zeta", "alpha"]);
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
            () => InitCloudInit.Generate("agent", config, ["broken"]));

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
            () => InitCloudInit.Generate("agent", config, ["broken"]));

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

        var yaml = InitCloudInit.Generate("agent", config, ["absolute"]);

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

    private static InitsConfig Config(params (string Name, ComponentConfig Component)[] components)
    {
        return new InitsConfig
        {
            Components = components.ToDictionary(
                component => component.Name,
                component => component.Component,
                StringComparer.OrdinalIgnoreCase)
        };
    }
}

public class InitComponentsTests
{
    [Fact]
    public void ResolveSnaps_MapptNurKomponentenMitStoreSnap()
    {
        var config = Config(
            ("claude", new ComponentConfig { Script = "scripts/components/install-claude.sh" }),
            ("dotnet", new ComponentConfig { Snap = "dotnet-sdk-100" }),
            ("python", new ComponentConfig { Snap = "python3-alt" }));

        var snaps = InitComponents.ResolveSnaps(config, ["claude", "dotnet", "python"]);

        Assert.Equal(["dotnet-sdk-100", "python3-alt"], snaps);
    }

    [Fact]
    public void ResolveSnaps_IgnoriertGrossKleinUndLeerzeichen()
    {
        var config = Config(
            ("claude", new ComponentConfig()),
            ("dotnet", new ComponentConfig { Snap = "dotnet-sdk-100" }));

        var snaps = InitComponents.ResolveSnaps(config, [" Claude ", "DOTNET"]);

        Assert.Equal(["dotnet-sdk-100"], snaps);
    }

    [Fact]
    public void ResolveSnaps_UnbekannteKomponenteWirftMultipassException()
    {
        var config = Config(("claude", new ComponentConfig()));

        Assert.Throws<MultipassException>(
            () => InitComponents.ResolveSnaps(config, ["rust"]));
    }

    private static InitsConfig Config(params (string Name, ComponentConfig Component)[] components)
    {
        return new InitsConfig
        {
            Components = components.ToDictionary(
                component => component.Name,
                component => component.Component,
                StringComparer.OrdinalIgnoreCase)
        };
    }
}

/// <summary>
/// InitStore ist statisch — alle Tests, die Initialize aufrufen, müssen serialisiert
/// laufen (xUnit-Collection), sonst verbiegen parallel laufende Klassen den Root.
/// </summary>
[Collection("InitStore")]
public class InitServiceConfigureGuardTests
{
    [Fact]
    public void Configure_NeuesProfil_SchreibtConfigUndCloudInit()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);
        var config = Config();

        var result = new InitService(config).Configure("agent", ["claude", "dotnet"]);

        Assert.Equal("agent", result.Name);
        Assert.True(File.Exists(result.ConfigPath));
        Assert.NotNull(result.CloudInitPath);
        Assert.True(File.Exists(result.CloudInitPath));
        Assert.Equal(["dotnet-sdk-100"], result.Snaps);
        Assert.Equal("daily:26.10", result.RuntimeImage);
        Assert.DoesNotContain("HostInstall", File.ReadAllText(result.ConfigPath));
    }

    [Fact]
    public void Configure_LaedtKeinModellUndLegtKeineModelldateiAn()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);

        new InitService(Config()).Configure("agent", ["claude"]);

        // Das Profil besteht ausschließlich aus Profilkonfiguration + cloud-init;
        // ein Image wird weder heruntergeladen noch verwaltet.
        var files = Directory.GetFiles(InitStore.ProfileDir("agent")).Select(Path.GetFileName).ToArray();
        Assert.Equal(["agent.profile.json", "cloud-init.yaml"], files.Order(StringComparer.Ordinal));
        Assert.Equal(
            """
            {
              "Name": "agent",
              "Components": [
                "claude"
              ],
              "Snaps": [],
              "RuntimeImage": "daily:26.10",
              "CloudInitFile": "cloud-init.yaml",
              "SchemaVersion": 4
            }
            """.ReplaceLineEndings(),
            File.ReadAllText(InitStore.ConfigFile("agent")));
    }

    [Fact]
    public void Configure_BestehendesProfil_OhneForceWirftMultipassException()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);
        var config = Config();
        var dir = InitStore.ProfileDir("agent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(InitStore.ConfigFile("agent"), "x");

        var exception = Assert.Throws<MultipassException>(
            () => new InitService(config).Configure("agent", ["claude"]));

        Assert.Contains("init-delete", exception.Message);
    }

    [Fact]
    public void Configure_BestehendesProfil_MitForceLaeuftWeiter()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);
        var config = Config();
        var dir = InitStore.ProfileDir("agent");
        Directory.CreateDirectory(dir);
        File.WriteAllText(InitStore.ConfigFile("agent"), "x");

        var result = new InitService(config).Configure("agent", ["claude"], force: true);

        Assert.True(File.Exists(result.ConfigPath));
        Assert.NotNull(result.CloudInitPath);
        Assert.Empty(result.Snaps);
    }

    private static InitsConfig Config()
    {
        return new InitsConfig
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

[Collection("InitStore")]
public class InitServiceListDeleteTests
{
    private static void SeedStore(TempDirectory store, string name, string[] components, string[] snaps)
    {
        InitStore.Initialize(store);
        var dir = InitStore.ProfileDir(name);
        Directory.CreateDirectory(dir);
        var profileConfig = new InitProfileConfig(
            name,
            components,
            snaps);
        File.WriteAllText(InitStore.ConfigFile(name), profileConfig.ToJson());
    }

    [Fact]
    public void List_LiefertKonfigurierteProfile()
    {
        using var store = new TempDirectory();
        SeedStore(store, "alpha", ["claude"], []);
        SeedStore(store, "beta", ["dotnet"], ["dotnet-sdk-100"]);

        var entries = new InitService(new InitsConfig()).List();

        Assert.Equal(2, entries.Count);
        Assert.Equal("alpha", entries[0].Name);
        Assert.Equal(["claude"], entries[0].Components);
        Assert.Empty(entries[0].Snaps);
        Assert.Equal("beta", entries[1].Name);
        Assert.Equal(["dotnet-sdk-100"], entries[1].Snaps);
    }

    [Fact]
    public void List_OhneProfilesRoot_LiefertLeereListe()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);

        Assert.Empty(new InitService(new InitsConfig()).List());
    }

    [Fact]
    public void Delete_EntferntProfilkonfiguration()
    {
        using var store = new TempDirectory();
        SeedStore(store, "agent", ["claude"], []);

        var result = new InitService(new InitsConfig()).Delete("agent");

        Assert.Equal(InitStore.ProfileDir("agent"), result.ProfileDir);
        Assert.False(Directory.Exists(result.ProfileDir));
    }

    [Fact]
    public void Delete_UnbekannterName_WirftMultipassException()
    {
        using var store = new TempDirectory();
        InitStore.Initialize(store);

        Assert.Throws<MultipassException>(() => new InitService(new InitsConfig()).Delete("gibtsnicht"));
    }
}

public class InitProfileConfigTests
{
    [Fact]
    public void Json_RoundtripErhaeltAlleFelder()
    {
        var config = new InitProfileConfig(
            "agent",
            ["claude", "dotnet"],
            ["dotnet-sdk-100"],
            RuntimeImage: "daily:26.10",
            CloudInitFile: "cloud-init.yaml");

        var roundtrip = InitProfileConfig.FromJson(config.ToJson());

        Assert.Equal(config.Name, roundtrip.Name);
        Assert.Equal(config.Components, roundtrip.Components);
        Assert.Equal(config.Snaps, roundtrip.Snaps);
        Assert.Equal(config.RuntimeImage, roundtrip.RuntimeImage);
        Assert.Equal(config.CloudInitFile, roundtrip.CloudInitFile);
        Assert.Equal(4, roundtrip.SchemaVersion);
    }
}
