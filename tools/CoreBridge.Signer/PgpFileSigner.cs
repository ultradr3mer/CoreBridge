using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Signer;

/// <summary>Entsperrter Signierschlüssel samt öffentlichem Teil (für den Issuer-Fingerprint).</summary>
public sealed record SigningKey(PgpPrivateKey PrivateKey, PgpPublicKey PublicKey)
{
    public string Fingerprint => Convert.ToHexString(PublicKey.GetFingerprint());
}

/// <summary>
/// Erzeugt abgetrennte, armored OpenPGP-Signaturen im Format, das
/// <c>CoreBridge.Core.Authenticity.PgpFileVerifier</c> erwartet: Typ
/// CanonicalTextDocument (wie <c>gpg --detach-sign --armor --textmode</c>), SHA-256.
/// </summary>
public static class PgpFileSigner
{
    /// <summary>
    /// Liest den geheimen Schlüssel aus einem Export (<c>gpg --export-secret-keys --armor</c>)
    /// und entsperrt ihn. Ohne <paramref name="keyId"/> (Fingerprint oder
    /// 16-stellige Key-ID, hex) gilt der erste signierfähige Schlüssel.
    /// </summary>
    public static SigningKey ReadSigningKey(Stream secretKeys, char[] passphrase, string? keyId = null)
    {
        var bundle = new PgpSecretKeyRingBundle(PgpUtilities.GetDecoderStream(secretKeys));
        var wanted = keyId?.Replace(" ", string.Empty).ToUpperInvariant();

        foreach (var ring in bundle.GetKeyRings())
        {
            foreach (var secret in ring.GetSecretKeys())
            {
                var matches = wanted is null
                    || Convert.ToHexString(secret.GetFingerprint()) == wanted
                    || secret.KeyId.ToString("X16") == wanted;
                if (matches && secret.IsSigningKey && !secret.IsPrivateKeyEmpty)
                {
                    return new SigningKey(secret.ExtractPrivateKey(passphrase), secret.PublicKey);
                }
            }
        }

        throw new PgpException(wanted is null
            ? "Kein signierfähiger geheimer Schlüssel im Export."
            : $"Kein signierfähiger geheimer Schlüssel {wanted} im Export.");
    }

    /// <summary>Signiert die Bytes aus <paramref name="data"/> und schreibt die armored Signatur nach <paramref name="output"/>.</summary>
    public static void Sign(Stream data, Stream output, SigningKey key)
    {
        var generator = Generator(key);
        var buffer = new byte[8192];
        int read;
        while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            generator.Update(buffer, 0, read);
        }

        using var armored = new ArmoredOutputStream(output);
        generator.Generate().Encode(armored);
    }

    /// <summary>
    /// Klartextsignatur wie <c>gpg --clearsign</c>: Kopf, <c>Hash: SHA256</c>, der Text
    /// (Zeilen mit <c>-</c> am Anfang dash-escaped, LF), dann der Signaturblock. Signiert
    /// wird der kanonische Text (<c>ClearsignedMessage.Canonicalize</c>: Leerraum am
    /// Zeilenende weg, CRLF dazwischen, ohne letztes Zeilenende) — so prüft CoreBridge die
    /// Datei daneben (<c>&lt;datei&gt;.clearsigned.asc</c>) wie die Clearsign-Datei selbst.
    /// </summary>
    public static void ClearSign(byte[] data, Stream output, SigningKey key)
    {
        var text = new System.Text.UTF8Encoding(false).GetString(data).TrimStart('\uFEFF');
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var canonical = System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", lines.Select(line => line.TrimEnd(' ', '\t'))));
        var generator = Generator(key);
        generator.Update(canonical, 0, canonical.Length);
        var signature = generator.Generate();

        var body = new System.Text.StringBuilder()
            .Append("-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n");
        foreach (var line in lines)
        {
            body.Append(line.StartsWith('-') ? "- " + line : line).Append('\n');
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToString());
        output.Write(bytes, 0, bytes.Length);
        using var armored = new ArmoredOutputStream(output);
        signature.Encode(armored);
    }

    private static PgpSignatureGenerator Generator(SigningKey key)
    {
        var generator = new PgpSignatureGenerator(key.PublicKey.Algorithm, HashAlgorithmTag.Sha256);
        generator.InitSign(PgpSignature.CanonicalTextDocument, key.PrivateKey);

        // Erstellzeit und Issuer-Key-ID ergänzt der Generator selbst.
        var hashed = new PgpSignatureSubpacketGenerator();
        hashed.SetIssuerFingerprint(false, key.PublicKey);
        generator.SetHashedSubpackets(hashed.Generate());
        return generator;
    }
}
