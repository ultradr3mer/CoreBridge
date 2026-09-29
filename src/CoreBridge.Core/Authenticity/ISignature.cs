namespace CoreBridge.Core.Authenticity;

/// <summary>Ergebnis der Prüfung einer signierten Datei.</summary>
public enum SignatureStatus
{
    /// <summary>Vertrauter Schlüssel, Signatur gültig, Inhalt unverändert.</summary>
    Ok,

    /// <summary>Schlüssel und Signatur in Ordnung, aber der Inhalt wurde nach dem Signieren geändert.</summary>
    ErrorFileMismatch,

    /// <summary>Signatur unbrauchbar: unbekannter, widerrufener oder abgelaufener Schlüssel, schwacher Hash, unlesbar.</summary>
    ErrorSignature,
}

/// <summary>
/// Signaturdaten einer gelesenen Datei. <see cref="Fingerprint"/>,
/// <see cref="SignerName"/> und <see cref="SignerEmail"/> sind <c>null</c>, wenn
/// der Schlüssel nicht im vertrauten Bundle steht. <see cref="Reason"/> ist fertig
/// zum Anzeigen.
/// </summary>
public sealed record SignatureInfo(
    string? Fingerprint,
    string? SignerName,
    string? SignerEmail,
    SignatureStatus Status,
    string Reason,
    DateTime? SignedAt)
{
    /// <summary>Übersetzt eine <see cref="SignatureCheck"/> und zerlegt die User-ID <c>Name &lt;mail&gt;</c>.</summary>
    public static SignatureInfo From(SignatureCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var status = check.Valid
            ? SignatureStatus.Ok
            : check.ContentMismatch ? SignatureStatus.ErrorFileMismatch : SignatureStatus.ErrorSignature;
        var (name, email) = SplitUserId(check.SignerUserId);
        return new SignatureInfo(check.Fingerprint, name, email, status, check.Reason, check.SignedAt);
    }

    private static (string? Name, string? Email) SplitUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return (null, null);
        }

        var open = userId.LastIndexOf('<');
        var close = userId.LastIndexOf('>');
        if (open < 0 || close < open)
        {
            return (userId.Trim(), null);
        }

        var name = userId[..open].Trim();
        var email = userId[(open + 1)..close].Trim();
        return (name.Length > 0 ? name : null, email.Length > 0 ? email : null);
    }
}

/// <summary>
/// Für Typen, die <see cref="SignedJsonProvider{T}"/> liest: Bei einer signierten
/// Datei wird nach dem Deserialisieren <see cref="OnSignature"/> aufgerufen, bei
/// einer unsignierten nicht.
/// </summary>
public interface ISignature
{
    /// <summary>Meldet die Signaturdaten der Datei, aus der das Objekt stammt.</summary>
    void OnSignature(SignatureInfo signature);
}
