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
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITestKeyOnly corebridge@test";

    [Theory]
    [InlineData("python", "/usr/local/sbin/corebridge-install-python", "python3.14")]
    [InlineData("cline", "/usr/local/sbin/corebridge-install-cline", "npm install -g --allow-scripts=cline,protobufjs cline")]
    [InlineData("git", "/usr/local/sbin/corebridge-install-git", "authorized_keys")]
    public void KomponenteBettetSkriptEinUndRuftEsAuf(string component, string installPath, string scriptMarker)
    {
        var config = LoadShippedImagesConfig();

        var yaml = InitCloudInit.Generate("agent", config, [component], HostKey);

        Assert.Contains($"path: '{installPath}'", yaml);
        Assert.Contains(scriptMarker, yaml);
        Assert.Contains($"- ['{installPath}', 'ubuntu']", yaml);
        Assert.Contains($"touch /var/lib/corebridge/{component}.ready", yaml);
    }

    [Fact]
    public void AlleAusgeliefertenKomponentenSindKombinierbar()
    {
        var config = LoadShippedImagesConfig();

        var yaml = InitCloudInit.Generate("all", config, config.Components.Keys, HostKey);

        foreach (var component in config.Components.Values.Where(c => c.InstallPath is not null))
        {
            Assert.Contains($"path: '{component.InstallPath}'", yaml);
        }
    }

    [Fact]
    public void GitLegtDenHostSchluesselVorDemSkriptAb()
    {
        var yaml = InitCloudInit.Generate("agent", LoadShippedImagesConfig(), ["git"], HostKey);

        Assert.Contains("path: '/etc/corebridge/host_authorized_keys'", yaml);
        Assert.Contains("      " + HostKey, yaml);
        Assert.Contains("- 'openssh-server'", yaml);
        Assert.True(
            yaml.IndexOf("host_authorized_keys'", StringComparison.Ordinal)
                < yaml.IndexOf("path: '/usr/local/sbin/corebridge-install-git'", StringComparison.Ordinal));
    }

    [Fact]
    public void GitOhneHostSchluesselScheitert()
    {
        Assert.Throws<CoreBridge.Core.Multipass.MultipassException>(
            () => InitCloudInit.Generate("agent", LoadShippedImagesConfig(), ["git"]));
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
