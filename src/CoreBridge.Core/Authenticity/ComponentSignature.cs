namespace CoreBridge.Core.Authenticity;

/// <summary>Signaturstatus einer Komponente, vom besten zum schlechtesten.</summary>
public enum ComponentTrust
{
    /// <summary>Gültig signiert mit dem eingebetteten CoreBridge-Schlüssel.</summary>
    CoreBridge,

    /// <summary>Gültig signiert mit einem Schlüssel aus dem Vertrauensordner des Benutzers.</summary>
    UserTrusted,

    /// <summary>Keine Signatur (Definition oder Skript).</summary>
    Unsigned,

    /// <summary>Signiert, aber mit einem Schlüssel, dem niemand vertraut.</summary>
    UnknownKey,

    /// <summary>Signatur kaputt, Schlüssel abgelaufen/widerrufen oder Inhalt nach dem Signieren geändert.</summary>
    Invalid,
}

/// <summary>
/// Wer eine Komponente signiert hat, fertig zum Anzeigen. <see cref="File"/> ist die
/// Datei, deren Prüfung den Status bestimmt (Definition oder Skript).
/// </summary>
public sealed record ComponentSignature(
    ComponentTrust Trust,
    string File,
    string Reason,
    string? Fingerprint = null,
    string? Signer = null,
    DateTime? SignedAt = null)
{
    /// <summary>Unsigniert oder unbekannter Schlüssel: geladen wird, aber laut gewarnt.</summary>
    public bool NeedsWarning => Trust is ComponentTrust.Unsigned or ComponentTrust.UnknownKey;

    /// <summary>
    /// Ordnet eine Prüfung ein: gültig mit dem CoreBridge-Fingerprint → CoreBridge, gültig
    /// mit einem anderen (der nur über den Vertrauensordner ins Bundle kommt) → Benutzer-vertraut.
    /// </summary>
    public static ComponentSignature From(SignatureCheck check)
    {
        var trust = check switch
        {
            { Valid: true, Fingerprint: TrustedKeys.CoreBridgeFingerprint } => ComponentTrust.CoreBridge,
            { Valid: true } => ComponentTrust.UserTrusted,
            { Unsigned: true } => ComponentTrust.Unsigned,
            { UnknownKey: true } => ComponentTrust.UnknownKey,
            _ => ComponentTrust.Invalid
        };
        return new ComponentSignature(trust, check.File, check.Reason, check.Fingerprint, check.SignerUserId, check.SignedAt);
    }

    /// <summary>Der schlechtere von beiden — eine Komponente ist nur so vertrauenswürdig wie ihr schwächster Teil.</summary>
    public static ComponentSignature Worst(ComponentSignature first, ComponentSignature second) =>
        second.Trust > first.Trust ? second : first;

    /// <summary>Kurzform für Tabellen (<c>component-list</c>).</summary>
    public string Label => Trust switch
    {
        ComponentTrust.CoreBridge => "CoreBridge",
        ComponentTrust.UserTrusted => $"vertraut: {ShortSigner}",
        ComponentTrust.Unsigned => "UNSIGNIERT",
        ComponentTrust.UnknownKey => "UNBEKANNT",
        _ => "UNGÜLTIG"
    };

    /// <summary>Langform für Meldungen, mit Fingerprint und Datum.</summary>
    public string Describe() => Trust switch
    {
        ComponentTrust.CoreBridge => $"signiert von CoreBridge (Fingerprint {Fingerprint}, {SignedAt:yyyy-MM-dd})",
        ComponentTrust.UserTrusted => $"signiert von {Signer ?? "?"} (Benutzer-vertraut, Fingerprint {Fingerprint}, {SignedAt:yyyy-MM-dd})",
        ComponentTrust.Unsigned => $"unsigniert ({System.IO.Path.GetFileName(File)})",
        ComponentTrust.UnknownKey => $"unbekannter Schlüssel {Fingerprint} ({System.IO.Path.GetFileName(File)}) — vertrauen: cb key-trust -f <öffentlicher Schlüssel>",
        _ => $"ungültig: {Reason} ({File})"
    };

    /// <summary><c>claude-vm &lt;vm@cla.de&gt;</c> → <c>claude-vm</c>.</summary>
    private string ShortSigner =>
        Signer is { } signer && signer.IndexOf('<') is > 0 and var open ? signer[..open].Trim() : Signer ?? Fingerprint ?? "?";
}
