using System.Text;
using System.Text.Json;
using CoreBridge.Core.Authenticity;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Clearsign-Prüfung gegen eine mit echtem <c>gpg --clearsign</c> erzeugte Datei
/// (<c>Clearsign/</c>): Sie enthält eine Zeile mit <c>-</c> am Anfang
/// (Dash-Escaping) und Leerzeichen am Zeilenende (kanonisch entfernt). Der
/// Schlüssel läuft nicht ab; beiliegend ist nur der öffentliche Teil.
/// </summary>
public class ClearsignedContentTests
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
        return ClearsignedContent.Unwrap(SignedFile, Encoding.UTF8.GetBytes(content), requireSignature, trusted, DateTime.UtcNow);
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
}
