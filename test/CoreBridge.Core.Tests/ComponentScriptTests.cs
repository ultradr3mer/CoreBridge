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
    public void KomponenteBettetSkriptEinUndRuftEsAuf(string component, string installPath, string scriptMarker)
    {
        var config = LoadShippedImagesConfig();

        var yaml = InitCloudInit.Generate("agent", config, [component]);

        Assert.Contains($"path: '{installPath}'", yaml);
        Assert.Contains(scriptMarker, yaml);
        Assert.Contains($"- ['{installPath}', 'ubuntu']", yaml);
        Assert.Contains($"touch /var/lib/corebridge/{component}.ready", yaml);
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
