using System.Text.Json.Nodes;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

public class InitComponentStoreTests
{
    private static ImagesConfig BuiltIns() => new()
    {
        Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["opencode"] = new() { Snap = "opencode", Classic = true }
        }
    };

    [Fact]
    public void Parse_JsonUndYamlErgebenDieselbeDefinition()
    {
        var json = InitComponentStore.Parse(
            """{ "snap": "htop", "Classic": true, "Packages": ["curl"], "PackageUpdate": true }""",
            ComponentFormat.Json);
        var yaml = InitComponentStore.Parse(
            """
            Snap: htop
            Classic: true
            Packages:
              - curl
            PackageUpdate: true
            """,
            ComponentFormat.Yaml);

        Assert.Equal("htop", json.Snap);
        Assert.True(json.Classic);
        Assert.Equal(json.Snap, yaml.Snap);
        Assert.Equal(json.Classic, yaml.Classic);
        Assert.Equal(json.Packages, yaml.Packages);
        Assert.Equal(json.PackageUpdate, yaml.PackageUpdate);
    }

    [Fact]
    public void Parse_GequoteterYamlWertBleibtText()
    {
        var component = InitComponentStore.Parse("Snap: 'true'", ComponentFormat.Yaml);

        Assert.Equal("true", component.Snap);
    }

    [Theory]
    [InlineData("""{ "Snapp": "htop" }""", ComponentFormat.Json)]
    [InlineData("Snapp: htop", ComponentFormat.Yaml)]
    [InlineData("{ nicht json", ComponentFormat.Json)]
    [InlineData("Snap: [offen", ComponentFormat.Yaml)]
    public void Parse_UnbekannterSchluesselOderKaputteEingabeIstFehler(string text, ComponentFormat format)
    {
        Assert.Throws<MultipassException>(() => InitComponentStore.Parse(text, format));
    }

    [Fact]
    public void ParseArgument_LiestDateiOderMeldetFehlendeDatei()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir, "htop.yaml");
        File.WriteAllText(file, "Snap: htop\n");

        Assert.Equal("htop", InitComponentStore.ParseArgument(file, ComponentFormat.Yaml).Snap);
        var error = Assert.Throws<MultipassException>(
            () => InitComponentStore.ParseArgument(Path.Combine(dir, "fehlt.json"), ComponentFormat.Json));
        Assert.Contains("nicht gefunden", error.Message);
    }

    [Fact]
    public void Add_LegtDateiAnUndBehaeltAndereSchluessel()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "sub", "appsettings.user.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, """{ "Eigenes": 1, "Images": { "Other": "x" } }""");

        InitComponentStore.Add(BuiltIns(), userFile, "htop", new ComponentConfig { Snap = "htop" }, replace: false);

        var root = JsonNode.Parse(File.ReadAllText(userFile))!;
        Assert.Equal(1, root["Eigenes"]!.GetValue<int>());
        Assert.Equal("x", root["Images"]!["Other"]!.GetValue<string>());
        Assert.Equal("htop", root["Images"]!["Components"]!["htop"]!["Snap"]!.GetValue<string>());
        Assert.Null(root["Images"]!["Components"]!["htop"]!["NeedsCloudInit"]);
        Assert.Equal("htop", InitComponentStore.ReadUser(userFile)["htop"].Snap);
    }

    [Fact]
    public void Add_ErsetzenNurMitReplace()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "appsettings.user.json");
        InitComponentStore.Add(BuiltIns(), userFile, "htop", new ComponentConfig { Snap = "htop" }, replace: false);

        Assert.Throws<MultipassException>(
            () => InitComponentStore.Add(BuiltIns(), userFile, "HTOP", new ComponentConfig { Snap = "btop" }, replace: false));

        InitComponentStore.Add(BuiltIns(), userFile, "htop", new ComponentConfig { Snap = "btop" }, replace: true);
        var user = InitComponentStore.ReadUser(userFile);
        Assert.Single(user);
        Assert.Equal("btop", user["htop"].Snap);
    }

    [Fact]
    public void AddUndRemove_MitgelieferteKomponenteIstTabu()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "appsettings.user.json");

        Assert.Throws<MultipassException>(
            () => InitComponentStore.Add(BuiltIns(), userFile, "opencode", new ComponentConfig { Snap = "x" }, replace: true));
        var error = Assert.Throws<MultipassException>(() => InitComponentStore.Remove(BuiltIns(), userFile, "opencode"));
        Assert.Contains("mitgeliefert", error.Message);
        Assert.False(File.Exists(userFile));
    }

    [Theory]
    [InlineData("Htop")]
    [InlineData("1tool")]
    [InlineData("tool-")]
    [InlineData("to ol")]
    public void Add_UngueltigerNameIstFehler(string name)
    {
        using var dir = new TempDirectory();

        Assert.Throws<MultipassException>(() => InitComponentStore.Add(
            BuiltIns(), Path.Combine(dir, "u.json"), name, new ComponentConfig { Snap = "htop" }, replace: false));
    }

    [Fact]
    public void Add_LeereDefinitionUndScriptOhneInstallPathSindFehler()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "u.json");
        var script = Path.Combine(dir, "install.sh");
        File.WriteAllText(script, "#!/bin/sh\n");

        Assert.Throws<MultipassException>(
            () => InitComponentStore.Add(BuiltIns(), userFile, "leer", new ComponentConfig(), replace: false));
        Assert.Throws<MultipassException>(
            () => InitComponentStore.Add(BuiltIns(), userFile, "tool", new ComponentConfig { Script = script }, replace: false));
        Assert.Throws<MultipassException>(() => InitComponentStore.Add(
            BuiltIns(), userFile, "tool", new ComponentConfig { Script = Path.Combine(dir, "fehlt.sh"), InstallPath = "/usr/local/sbin/x" }, replace: false));
    }

    [Fact]
    public void Add_RelativesScriptWirdAbsolutGespeichertUndGerendert()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "u.json");
        File.WriteAllText(Path.Combine(dir, "install.sh"), "#!/bin/sh\ntouch /var/lib/corebridge/tool.ready\n");
        var previous = Environment.CurrentDirectory;
        ComponentConfig saved;
        try
        {
            Environment.CurrentDirectory = dir;
            saved = InitComponentStore.Add(BuiltIns(), userFile, "tool",
                new ComponentConfig { Script = "install.sh", InstallPath = "/usr/local/sbin/corebridge-install-tool" }, replace: false);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "install.sh")), saved.Script);
        var merged = InitComponentStore.Merge(new AppConfig { Images = BuiltIns() }, userFile);
        Assert.Contains("tool.ready", InitCloudInit.Generate("p", merged.Images, ["tool"]));
    }

    [Fact]
    public void Remove_EntferntNurDieBenutzerkomponente()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "u.json");
        InitComponentStore.Add(BuiltIns(), userFile, "htop", new ComponentConfig { Snap = "htop" }, replace: false);
        InitComponentStore.Add(BuiltIns(), userFile, "btop", new ComponentConfig { Snap = "btop" }, replace: false);

        InitComponentStore.Remove(BuiltIns(), userFile, "HTOP");

        Assert.Equal(["btop"], InitComponentStore.ReadUser(userFile).Keys);
        Assert.Throws<MultipassException>(() => InitComponentStore.Remove(BuiltIns(), userFile, "htop"));
    }

    [Fact]
    public void Merge_ErgaenztKomponentenUndListeZeigtHerkunft()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "u.json");
        var config = new AppConfig { Images = BuiltIns() };
        Assert.Same(config, InitComponentStore.Merge(config, userFile));

        InitComponentStore.Add(config.Images, userFile, "htop", new ComponentConfig { Snap = "htop" }, replace: false);
        var merged = InitComponentStore.Merge(config, userFile);

        Assert.Equal(["htop"], InitComponents.ResolveSnaps(merged.Images, ["HTOP"]));
        var list = InitComponentStore.List(merged.Images, userFile);
        Assert.Equal(["htop", "opencode"], list.Select(e => e.Name));
        Assert.Equal([ComponentSource.User, ComponentSource.BuiltIn], list.Select(e => e.Source));
        Assert.Single(config.Images.Components);
    }

    [Fact]
    public void Merge_HandeditierterMitgelieferterNameIstFehler()
    {
        using var dir = new TempDirectory();
        var userFile = Path.Combine(dir, "u.json");
        File.WriteAllText(userFile, """{ "Images": { "Components": { "OpenCode": { "Snap": "x" } } } }""");

        var error = Assert.Throws<MultipassException>(
            () => InitComponentStore.Merge(new AppConfig { Images = BuiltIns() }, userFile));
        Assert.Contains("mitgeliefert", error.Message);
    }
}
