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
        var config = LoadShippedImagesConfig();

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
        var config = LoadShippedImagesConfig();

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
        var config = LoadShippedImagesConfig();

        var yaml = InitCloudInit.Generate("all", config, config.Components.Keys);

        foreach (var component in config.Components.Values.Where(c => c.InstallPath is not null))
        {
            Assert.Contains($"path: '{component.InstallPath}'", yaml);
        }
    }

    [Fact]
    public void GitInstalliertNurGit()
    {
        var yaml = InitCloudInit.Generate("agent", LoadShippedImagesConfig(), ["git"]);

        Assert.Contains("- 'git'", yaml);
        Assert.DoesNotContain("openssh-server", yaml);
        Assert.DoesNotContain("authorized_keys", yaml);
    }

    private static ImagesConfig LoadShippedImagesConfig()
    {
        var path = InitStore.ResolvePath("src/CoreBridge.Cli/appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var images = document.RootElement.GetProperty("Images").Deserialize<ImagesConfig>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return images with
        {
            Components = new Dictionary<string, ComponentConfig>(images.Components, StringComparer.OrdinalIgnoreCase)
        };
    }
}
