using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Ergebnis einer Signaturprüfung für eine Datei. <see cref="Reason"/> ist bei
/// Erfolg die Kurzbeschreibung des Signierers, sonst der Grund der Ablehnung —
/// fertig zum Anzeigen. <see cref="SignerUserId"/> ist die erste User-ID des
/// Primärschlüssels (z. B. <c>CoreBridge &lt;core@brid.ge&gt;</c>), sobald der
/// Schlüssel bekannt ist. <see cref="ContentMismatch"/> ist nur gesetzt, wenn
/// Schlüssel und Signatur in Ordnung sind, der Inhalt aber nicht passt.
/// </summary>
public sealed record SignatureCheck(
    string File,
    bool Valid,
    string Reason,
    string? Fingerprint = null,
    DateTime? SignedAt = null,
    string? SignerUserId = null,
    bool ContentMismatch = false);

/// <summary>
/// Prüft abgetrennte OpenPGP-Signaturen (<c>&lt;datei&gt;.asc</c>, armored oder
/// binär) gegen ein vertrautes Schlüssel-Bundle. CoreBridge prüft nur — signiert
/// wird außerhalb des Produkts (<c>scripts/sign-settings.ps1</c>,
/// <c>tools/CoreBridge.Signer</c>). Erwartet wird Typ CanonicalTextDocument
/// (<c>gpg --textmode</c>), damit ein Checkout mit CRLF statt LF die Signatur
/// nicht bricht; BinaryDocument wird ebenfalls akzeptiert.
/// </summary>
public static class PgpFileVerifier
{
    /// <summary>Dateiendung der abgetrennten Signatur neben der signierten Datei.</summary>
    public const string SignatureExtension = ".asc";

    /// <summary>Toleranz für Uhrabweichung zwischen Signier- und Prüfrechner.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Prüft <paramref name="path"/> gegen <c>&lt;path&gt;.asc</c>. Eine fehlende
    /// Datei oder Signatur ist ungültig, keine Ausnahme.
    /// </summary>
    public static SignatureCheck VerifyFile(string path, PgpPublicKeyRingBundle trusted, DateTime utcNow)
    {
        var signaturePath = path + SignatureExtension;
        if (!File.Exists(path))
        {
            return new SignatureCheck(path, false, "Datei fehlt.");
        }

        if (!File.Exists(signaturePath))
        {
            return new SignatureCheck(path, false, $"Signatur fehlt ({Path.GetFileName(signaturePath)}).");
        }

        using var data = File.OpenRead(path);
        using var signature = File.OpenRead(signaturePath);
        return Verify(path, data, signature, trusted, utcNow);
    }

    /// <summary>
    /// Prüft die Bytes aus <paramref name="data"/> gegen die abgetrennte Signatur.
    /// Gültig, sobald eine enthaltene Signatur alle Bedingungen erfüllt: bekannter,
    /// nicht widerrufener, nicht abgelaufener Schlüssel; Dokument-Signaturtyp;
    /// kein MD5/SHA-1/RIPEMD-160; Inhalt passt.
    /// </summary>
    public static SignatureCheck Verify(
        string file,
        Stream data,
        Stream signature,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(trusted);

        // Klein genug für den Speicher; so kann jede enthaltene Signatur dieselben
        // Bytes prüfen.
        using var buffer = new MemoryStream();
        data.CopyTo(buffer);
        return VerifyAny(file, buffer.ToArray(), signature, trusted, utcNow, textOnly: false);
    }

    /// <summary>
    /// Prüft eine Clearsign-Datei (<see cref="ClearsignedMessage"/>) mit denselben
    /// Bedingungen wie <see cref="Verify"/>, gegen den kanonischen Text. Clearsign
    /// ist immer eine Text-Signatur, daher gilt nur CanonicalTextDocument.
    /// </summary>
    public static SignatureCheck VerifyClearsigned(
        string file,
        ClearsignedMessage message,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(trusted);

        using var signature = new MemoryStream(message.Signature);
        return VerifyAny(file, message.CanonicalText, signature, trusted, utcNow, textOnly: true);
    }

    /// <summary>Gültig, sobald eine Signatur passt; sonst der erste Ablehnungsgrund.</summary>
    private static SignatureCheck VerifyAny(
        string file,
        byte[] content,
        Stream signature,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow,
        bool textOnly)
    {
        PgpSignatureList signatures;
        try
        {
            signatures = ReadSignatures(signature);
        }
        catch (Exception exception) when (exception is PgpException or IOException)
        {
            return new SignatureCheck(file, false, $"Signatur unlesbar: {exception.Message}");
        }

        if (signatures.Count == 0)
        {
            return new SignatureCheck(file, false, "Signaturdatei enthält keine Signatur.");
        }

        SignatureCheck? firstFailure = null;
        for (var i = 0; i < signatures.Count; i++)
        {
            var check = textOnly && signatures[i].SignatureType != PgpSignature.CanonicalTextDocument
                ? new SignatureCheck(file, false, $"Signaturtyp 0x{signatures[i].SignatureType:X2} ist keine Text-Signatur.")
                : VerifyOne(file, content, signatures[i], trusted, utcNow);
            if (check.Valid)
            {
                return check;
            }

            firstFailure ??= check;
        }

        return firstFailure!;
    }

    private static PgpSignatureList ReadSignatures(Stream signature)
    {
        var factory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(signature));
        var next = factory.NextPgpObject();
        if (next is PgpCompressedData compressed)
        {
            next = new PgpObjectFactory(compressed.GetDataStream()).NextPgpObject();
        }

        return next as PgpSignatureList
            ?? throw new PgpException($"keine abgetrennte Signatur (gefunden: {next?.GetType().Name ?? "nichts"}).");
    }

    private static SignatureCheck VerifyOne(
        string file,
        byte[] content,
        PgpSignature signature,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow)
    {
        if (signature.SignatureType is not (PgpSignature.BinaryDocument or PgpSignature.CanonicalTextDocument))
        {
            return new SignatureCheck(file, false, $"Signaturtyp 0x{signature.SignatureType:X2} ist keine Dokument-Signatur.");
        }

        if (signature.HashAlgorithm is HashAlgorithmTag.MD5 or HashAlgorithmTag.Sha1 or HashAlgorithmTag.RipeMD160)
        {
            return new SignatureCheck(file, false, $"Hash {signature.HashAlgorithm} ist zu schwach.");
        }

        var key = trusted.GetPublicKey(signature.KeyId);
        if (key is null)
        {
            return new SignatureCheck(file, false, $"Signiert mit unbekanntem Schlüssel {signature.KeyId:X16}.");
        }

        var fingerprint = Convert.ToHexString(key.GetFingerprint());
        var ring = trusted.GetPublicKeyRing(signature.KeyId);
        var master = ring.GetPublicKey();
        var userId = master.GetUserIds().FirstOrDefault();

        if (master.HasRevocation() || key.HasRevocation())
        {
            return new SignatureCheck(file, false, $"Schlüssel {fingerprint} ist widerrufen.", fingerprint, SignerUserId: userId);
        }

        // Ein Subkey gilt höchstens so lange wie sein Primärschlüssel.
        var expiry = ExpiresAt(master, master.GetValidSeconds());
        if (key.KeyId != master.KeyId && ExpiresAt(key, key.GetValidSeconds(master)) is { } subkeyExpiry)
        {
            expiry = expiry is { } masterExpiry && masterExpiry < subkeyExpiry ? masterExpiry : subkeyExpiry;
        }

        if (expiry is { } expiresAt && utcNow >= expiresAt)
        {
            return new SignatureCheck(
                file,
                false,
                $"Schlüssel {fingerprint} ist am {expiresAt:yyyy-MM-dd} abgelaufen — Schlüssel verlängern, neu exportieren und einbetten.",
                fingerprint,
                SignerUserId: userId);
        }

        var signedAt = signature.CreationTime;
        if (signedAt < key.CreationTime || signedAt > utcNow + ClockSkew)
        {
            return new SignatureCheck(file, false, $"Signaturzeitpunkt {signedAt:u} liegt außerhalb der Gültigkeit des Schlüssels.", fingerprint, signedAt, userId);
        }

        signature.InitVerify(key);
        signature.Update(content);
        if (!signature.Verify())
        {
            return new SignatureCheck(file, false, "Inhalt passt nicht zur Signatur (nach dem Signieren geändert?).", fingerprint, signedAt, userId, ContentMismatch: true);
        }

        return new SignatureCheck(file, true, $"signiert von {fingerprint} am {signedAt:yyyy-MM-dd HH:mm} UTC", fingerprint, signedAt, userId);
    }

    private static DateTime? ExpiresAt(PgpPublicKey key, long validSeconds)
    {
        return validSeconds > 0 ? key.CreationTime.AddSeconds(validSeconds) : null;
    }
}
