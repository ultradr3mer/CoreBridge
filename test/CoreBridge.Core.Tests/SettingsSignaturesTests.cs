using System.Text;
using System.Text.Json;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Inits;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Clearsign-Prüfung gegen eine mit echtem <c>gpg --clearsign</c> erzeugte Datei
/// (<c>Clearsign/</c>): Sie enthält eine Zeile mit <c>-</c> am Anfang
/// (Dash-Escaping) und Leerzeichen am Zeilenende (kanonisch entfernt). Der
/// Schlüssel läuft nicht ab; beiliegend ist nur der öffentliche Teil. Dieselbe
/// Datei dient als <c>&lt;datei&gt;.clearsigned.asc</c> neben einer unsignierten
/// Datei mit dem Klartext.
/// </summary>
public class SettingsSignaturesTests
{
    private const string SignedFile = "appsettings.clearsigned.json";
    private const string ExpectedCleartext = "{\n  \"DefaultTimeout\": \"11s\",   \n  \"Numbers\": [\n-1\n  ]\n}";

    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Clearsign");

    private static PgpPublicKeyRingBundle TestKey()
    {
        using var keys = File.OpenRead(Path.Combine(Directory, "test-key.pub.asc"));
        return TrustedKeys.Read(keys);
    }

    private static string Signed() => File.ReadAllText(Path.Combine(Directory, SignedFile));

    private static byte[] Unwrap(string content, PgpPublicKeyRingBundle trusted, bool requireSignature = true)
    {
        var policy = requireSignature ? SignaturePolicy.Required : SignaturePolicy.Optional;
        return SettingsSignatures.Unwrap(SignedFile, Encoding.UTF8.GetBytes(content), policy, trusted, DateTime.UtcNow);
    }

    /// <summary>Legt <c>appsettings.json</c> mit <paramref name="plain"/> und die Clearsign-Datei daneben an.</summary>
    private static string WithCompanion(TempDirectory directory, string plain)
    {
        var path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(path, plain);
        File.WriteAllText(path + SettingsSignatures.ClearsignedExtension, Signed());
        return path;
    }

    private static byte[] UnwrapFile(string path, SignaturePolicy policy, ICollection<SignatureCheck>? warnings = null)
    {
        return SettingsSignatures.Unwrap(path, File.ReadAllBytes(path), policy, TestKey(), DateTime.UtcNow, warnings);
    }

    private static string Normalize(byte[] bytes) => Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");

    [Fact]
    public void Unwrap_GpgClearsignLiefertDenKlartext()
    {
        var cleartext = Unwrap(Signed(), TestKey());

        // Zeilenenden folgen dem Checkout (core.autocrlf); Dash-Escaping ist entfernt.
        Assert.Equal(ExpectedCleartext, Normalize(cleartext));
        using var json = JsonDocument.Parse(cleartext);
        Assert.Equal(-1, json.RootElement.GetProperty("Numbers")[0].GetInt32());
    }

    [Fact]
    public void Unwrap_AndereZeilenendenBleibenGueltig()
    {
        var signed = Signed().Replace("\r\n", "\n");

        Assert.Equal(ExpectedCleartext, Normalize(Unwrap(signed.Replace("\n", "\r\n"), TestKey())));
        Assert.Equal(ExpectedCleartext, Normalize(Unwrap(signed, TestKey())));
    }

    [Fact]
    public void Unwrap_LeerzeichenAmZeilenendeSindNichtSigniert()
    {
        // RFC 4880 §7.1: Leerraum am Zeilenende gehört nicht zum kanonischen Text.
        var signed = Signed().Replace("\"11s\",   ", "\"11s\",");

        Unwrap(signed, TestKey());
    }

    [Fact]
    public void Unwrap_GeaenderterInhaltWirftMitGrund()
    {
        var failure = Assert.Throws<SettingsSignatureException>(() => Unwrap(Signed().Replace("11s", "99s"), TestKey()));

        Assert.Contains(SignedFile, failure.Message);
        Assert.Contains("Inhalt passt nicht", failure.Message);
    }

    [Fact]
    public void Unwrap_FehlendesDashEscapingErgibtDenselbenText()
    {
        // Wie gpg: "- -1" und ein unescaptes "-1" sind derselbe signierte Text.
        var signed = Signed().Replace("- -1", "-1");

        Assert.Equal(ExpectedCleartext, Normalize(Unwrap(signed, TestKey())));
    }

    [Fact]
    public void Unwrap_FremderSchluesselWirft()
    {
        var failure = Assert.Throws<SettingsSignatureException>(() => Unwrap(Signed(), TrustedKeys.CoreBridge));

        Assert.Contains("unbekanntem Schlüssel", failure.Message);
    }

    [Fact]
    public void Unwrap_UngueltigeSignaturWirftAuchOhneSignaturpflicht()
    {
        Assert.Throws<SettingsSignatureException>(() => Unwrap(Signed(), TrustedKeys.CoreBridge, requireSignature: false));
    }

    [Fact]
    public void Unwrap_UnsigniertOhneSignaturpflichtBleibtUnveraendert()
    {
        const string plain = "{ \"DefaultTimeout\": \"11s\" }";

        Assert.Equal(plain, Encoding.UTF8.GetString(Unwrap(plain, TestKey(), requireSignature: false)));
    }

    [Fact]
    public void Unwrap_UnsigniertMitSignaturpflichtWirft()
    {
        var failure = Assert.Throws<SettingsSignatureException>(() => Unwrap("{}", TestKey()));

        Assert.Contains("nicht signiert", failure.Message);
    }

    [Fact]
    public void Unwrap_KopfOhneSignaturblockWirft()
    {
        const string broken = "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n{}\n";

        var failure = Assert.Throws<SettingsSignatureException>(() => Unwrap(broken, TestKey(), requireSignature: false));
        Assert.Contains("Clearsign-Datei unlesbar", failure.Message);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Begleitdatei_PrueftDieDateiDaneben(string newline)
    {
        using var directory = new TempDirectory();
        var plain = ExpectedCleartext.Replace("\n", newline) + newline;
        var path = WithCompanion(directory, plain);

        var check = SettingsSignatures.Read(path, File.ReadAllBytes(path), TestKey(), DateTime.UtcNow).Check;

        Assert.True(check.Valid, check.Reason);
        Assert.Equal(plain, Encoding.UTF8.GetString(UnwrapFile(path, SignaturePolicy.Required)));
    }

    [Theory]
    [InlineData(SignaturePolicy.Optional)]
    [InlineData(SignaturePolicy.Warn)]
    public void Begleitdatei_GeaenderteDateiWirftAuchOhneSignaturpflicht(SignaturePolicy policy)
    {
        using var directory = new TempDirectory();
        var path = WithCompanion(directory, ExpectedCleartext.Replace("11s", "99s"));

        var failure = Assert.Throws<SettingsSignatureException>(() => UnwrapFile(path, policy));

        Assert.Contains("Inhalt passt nicht zu appsettings.json.clearsigned.asc", failure.Message);
        Assert.True(failure.Failed.Single().ContentMismatch);
    }

    [Fact]
    public void Begleitdatei_IgnoreLaedtTrotzUngueltigerSignatur()
    {
        using var directory = new TempDirectory();
        var path = WithCompanion(directory, "{}");

        Assert.Equal("{}", Encoding.UTF8.GetString(UnwrapFile(path, SignaturePolicy.Ignore)));
    }

    [Fact]
    public void Unsigniert_WarnSammeltWarnungOptionalBleibtStill()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(path, "{}");

        var warnings = new List<SignatureCheck>();
        UnwrapFile(path, SignaturePolicy.Warn, warnings);
        var warning = Assert.Single(warnings);
        Assert.True(warning.Unsigned);
        Assert.Contains("appsettings.json.clearsigned.asc", SettingsSignatures.WarningMessage(warning));

        warnings.Clear();
        UnwrapFile(path, SignaturePolicy.Optional, warnings);
        Assert.Empty(warnings);
        Assert.False(SettingsSignatures.IsSigned(path));
    }

    [Fact]
    public void PolicyFor_MitgelieferteWarnenLokaleNicht()
    {
        Assert.Equal(SignaturePolicy.Warn, SettingsSignatures.PolicyFor("appsettings.json", "Development"));
        Assert.Equal(SignaturePolicy.Warn, SettingsSignatures.PolicyFor("appsettings.Development.json", "Development"));
        Assert.Equal(SignaturePolicy.Optional, SettingsSignatures.PolicyFor("appsettings.Simulation.json", "Development"));
        Assert.Equal(SignaturePolicy.Optional, SettingsSignatures.PolicyFor("appsettings.local.json", "Development"));
    }

    [Fact]
    public void Benutzerdatei_FremdeSignaturWirft()
    {
        using var directory = new TempDirectory();
        var userFile = WithCompanion(directory, ExpectedCleartext);

        var failure = Assert.Throws<SettingsSignatureException>(() => InitComponentStore.ReadUser(userFile));
        Assert.Contains("unbekanntem Schlüssel", failure.Message);
    }

    [Fact]
    public void Benutzerdatei_SigniertWirdGelesenAberNichtUeberschrieben()
    {
        // Die mitgelieferte appsettings.json samt Signatur als gültig signierte Benutzerdatei.
        using var directory = new TempDirectory();
        var shipped = InitStore.ResolvePath("src/CoreBridge.Cli/appsettings.json");
        var userFile = Path.Combine(directory, "appsettings.user.json");
        File.Copy(shipped, userFile);
        File.Copy(shipped + SettingsSignatures.ClearsignedExtension, userFile + SettingsSignatures.ClearsignedExtension);

        Assert.True(SettingsSignatures.IsSigned(userFile));
        Assert.Contains("git", InitComponentStore.ReadUser(userFile).Keys);
        var before = File.ReadAllText(userFile);

        var failure = Assert.Throws<CoreBridge.Core.Multipass.MultipassException>(
            () => InitComponentStore.Remove(new CoreBridge.Core.Config.InitsConfig(), userFile, "git"));

        Assert.Contains("ist signiert", failure.Message);
        Assert.Equal(before, File.ReadAllText(userFile));
    }

    [Fact]
    public void MitgelieferteAppsettings_SindMitDemCoreBridgeSchluesselSigniert()
    {
        var directory = Path.GetDirectoryName(InitStore.ResolvePath("src/CoreBridge.Cli/appsettings.json"))!;
        var files = System.IO.Directory.GetFiles(directory, "appsettings*.json")
            .Where(file => File.Exists(file + SettingsSignatures.ClearsignedExtension))
            .ToList();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var check = SettingsSignatures.VerifyFile(file, TrustedKeys.CoreBridge, DateTime.UtcNow);
            Assert.True(check.Valid, $"{Path.GetFileName(file)}: {check.Reason}");
        }
    }
}
