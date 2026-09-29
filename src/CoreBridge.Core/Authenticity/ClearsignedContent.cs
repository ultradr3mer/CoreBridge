using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Entscheidet, welche Bytes einer gelesenen Datei weitergegeben werden: bei einer
/// Clearsign-Datei der geprüfte Klartext, bei einer unsignierten die Datei selbst —
/// aber nur, wenn keine Signatur verlangt ist. Eine vorhandene, aber ungültige
/// Signatur ist immer ein Fehler, auch ohne Signaturpflicht.
/// </summary>
public static class ClearsignedContent
{
    /// <summary>
    /// Liefert den Inhalt von <paramref name="file"/> ohne Clearsign-Hülle oder wirft
    /// <see cref="SettingsSignatureException"/> (ungültige oder kaputte Signatur,
    /// unsigniert trotz <paramref name="requireSignature"/>).
    /// </summary>
    public static byte[] Unwrap(
        string file,
        byte[] bytes,
        bool requireSignature,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(trusted);

        ClearsignedMessage? message;
        try
        {
            if (!ClearsignedMessage.TryParse(bytes, out message))
            {
                return requireSignature
                    ? throw Fail(new SignatureCheck(file, false, "Datei ist nicht signiert (Clearsign erwartet)."))
                    : bytes;
            }
        }
        catch (InvalidDataException exception)
        {
            throw Fail(new SignatureCheck(file, false, $"Clearsign-Datei unlesbar: {exception.Message}"));
        }

        var check = PgpFileVerifier.VerifyClearsigned(file, message!, trusted, utcNow);
        return check.Valid ? message!.Cleartext : throw Fail(check);
    }

    private static SettingsSignatureException Fail(SignatureCheck check) => new([check]);
}
