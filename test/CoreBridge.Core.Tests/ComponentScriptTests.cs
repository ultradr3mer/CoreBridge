using System.Text.Json;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Prüft die ausgelieferten Komponenten aus der echten CLI-<c>appsettings.json</c>:
/// Skript vorhanden, in cloud-init eingebettet und per runcmd aufgerufen.
/// </summary>
public class ComponentScriptTests
{
    [Theory]
    [InlineData("python", "/usr/local/sbin/corebridge-install-python", "python3.14")]
    [InlineData("cline", "/usr/local/sbin/corebridge-install-cline", "npm install -g --allow-scripts=cline,protobufjs cline")]
    [InlineData("git", "/usr/local/sbin/corebridge-install-git", "git --version")]
    // Auch ohne Login-Shell im PATH (multipass exec), wie /usr/local/bin/claude.
    [InlineData("dotnet", "/usr/local/sbin/corebridge-install-dotnet", "ln -sfn \"$dotnet_source\" /usr/local/bin/dotnet")]
    public void KomponenteBettetSkriptEinUndRuftEsAuf(string component, string installPath, string scriptMarker)
    {
        var config = LoadShippedInitsConfig();

        var yaml = InitCloudInit.Generate("agent", config, [component]);

        Assert.Contains($"path: '{installPath}'", yaml);
        Assert.Contains(scriptMarker, yaml);
        Assert.Contains($"- ['{installPath}', 'ubuntu']", yaml);
        Assert.Contains($"touch /var/lib/corebridge/{component}.ready", yaml);
    }

    /// <summary>
    /// vm-create und vm-install halten eine Komponente erst mit ihrem
    /// <c>.ready</c>-Marker für installiert — ein Skript ohne ihn (wie lange
    /// install-dotnet.sh) läuft durch und gilt trotzdem als gescheitert.
    /// </summary>
    [Fact]
    public void JedesKomponentenSkriptSetztSeinenMarker()
    {
        var config = LoadShippedInitsConfig();

        var withScript = config.Components.Where(c => !string.IsNullOrWhiteSpace(c.Value.Script)).ToList();
        Assert.NotEmpty(withScript);
        foreach (var (name, _) in withScript)
        {
            var yaml = InitCloudInit.Generate("agent", config, [name]);
            Assert.True(
                yaml.Contains($"touch /var/lib/corebridge/{name}.ready", StringComparison.Ordinal),
                $"Skript der Komponente '{name}' setzt keinen Marker /var/lib/corebridge/{name}.ready.");
        }
    }

    [Fact]
    public void AlleAusgeliefertenKomponentenSindKombinierbar()
    {
        var config = LoadShippedInitsConfig();

        var yaml = InitCloudInit.Generate("all", config, config.Components.Keys);

        foreach (var component in config.Components.Values.Where(c => c.InstallPath is not null))
        {
            Assert.Contains($"path: '{component.InstallPath}'", yaml);
        }
    }

    [Fact]
    public void GitInstalliertNurGit()
    {
        var yaml = InitCloudInit.Generate("agent", LoadShippedInitsConfig(), ["git"]);

        Assert.Contains("- 'git'", yaml);
        Assert.DoesNotContain("openssh-server", yaml);
        Assert.DoesNotContain("authorized_keys", yaml);
    }

    public static TheoryData<string> ShippedBundles()
    {
        var data = new TheoryData<string>();
        foreach (var file in ComponentCatalog.Bundles(InitStore.ResolvePath("src/CoreBridge.Cli/components")))
        {
            data.Add(ComponentCatalog.BundleName(file));
        }

        return data;
    }

    /// <summary>
    /// Jedes mitgelieferte Bundle: genau seine Komponente, Skript inline mit
    /// <c>set -Eeuo pipefail</c>, Installationsbenutzer als <c>$1</c>, eigener Marker,
    /// gültige cloud-init — und der Simulator erkennt den Marker nach dem Launch.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedBundles))]
    public void BundleRendertUndSetztSeinenMarker(string name)
    {
        var config = WithBundles(LoadShippedInitsConfig());
        var component = config.Components[name];

        Assert.Null(component.Script);
        Assert.StartsWith("#!/bin/bash\nset -Eeuo pipefail\n", component.ScriptContent);
        Assert.Contains("install_user=\"${1:-ubuntu}\"", component.ScriptContent);
        Assert.Equal($"/usr/local/sbin/corebridge-install-{name}", component.InstallPath);

        var yaml = InitCloudInit.Generate("agent", config, [name]);
        Assert.Contains($"- ['{component.InstallPath}', 'ubuntu']", yaml);
        Assert.Contains($"touch /var/lib/corebridge/{name}.ready", yaml);
        Assert.Equal([(name, $"/var/lib/corebridge/{name}.ready")], InitInstall.Markers(config, [name]));

        using var temp = new TempDirectory();
        var cloudInit = Path.Combine(temp.Path, "cloud-init.yaml");
        File.WriteAllText(cloudInit, yaml);
        var vms = new CoreBridge.Core.Vm.VmService(CoreBridge.Simulation.SimulatedMultipassCli.At(Path.Combine(temp.Path, "sim", "state.json"), TimeSpan.Zero));
        vms.Create("sim-a", "daily:26.10", cloudInitPath: cloudInit);
        Assert.Equal(0, vms.Exec("sim-a", InitInstall.MarkerProbe($"/var/lib/corebridge/{name}.ready")).ExitCode);
    }

    [Fact]
    public void AlleKomponentenMitBundlesSindKombinierbar()
    {
        var config = WithBundles(LoadShippedInitsConfig());

        var yaml = InitCloudInit.Generate("all", config, config.Components.Keys);

        Assert.True(config.Components.Count >= 16, $"nur {config.Components.Count} Komponenten");
    }

    private static InitsConfig WithBundles(InitsConfig config)
    {
        var components = new Dictionary<string, ComponentConfig>(config.Components, StringComparer.OrdinalIgnoreCase);
        foreach (var file in ComponentCatalog.Bundles(InitStore.ResolvePath("src/CoreBridge.Cli/components")))
        {
            var entries = ComponentCatalog.ParseComponents(File.ReadAllBytes(file), file);
            ComponentCatalog.RequireBundleShape(file, entries);
            foreach (var (name, component) in entries)
            {
                components.Add(name, component);
            }
        }

        return config with { Components = components };
    }

    private static InitsConfig LoadShippedInitsConfig()
    {
        var path = InitStore.ResolvePath("src/CoreBridge.Cli/appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var inits = document.RootElement.GetProperty("Inits").Deserialize<InitsConfig>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return inits with
        {
            Components = new Dictionary<string, ComponentConfig>(inits.Components, StringComparer.OrdinalIgnoreCase)
        };
    }
}
