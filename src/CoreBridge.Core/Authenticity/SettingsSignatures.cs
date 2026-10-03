using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Was eine Datei ohne Signatur bedeutet. Eine vorhandene, aber ungültige
/// Signatur bricht bei jeder Stufe außer <see cref="Ignore"/> ab.
/// </summary>
public enum SignaturePolicy
{
    /// <summary>Nichts erzwingen, nur den Klartext auspacken (<c>settings-verify</c> meldet selbst).</summary>
    Ignore,

    /// <summary>Unsigniert ist in Ordnung und bleibt still (Benutzereinstellungen); signiert muss gültig sein.</summary>
    Optional,

    /// <summary>Unsigniert lädt mit Warnung (mitgelieferte appsettings); signiert muss gültig sein.</summary>
    Warn,

    /// <summary>Ohne gültige Signatur wird die Datei nicht geladen.</summary>
    Required,
}

/// <summary>Zu ladender Inhalt einer Datei und das Ergebnis ihrer Prüfung.</summary>
public sealed record SignedContent(byte[] Content, SignatureCheck Check);

/// <summary>
/// Signaturen der Konfiguration. Eine Datei gilt als signiert, wenn sie selbst
/// eine Clearsign-Datei ist, wenn daneben <c>&lt;datei&gt;.clearsigned.asc</c>
/// (<c>gpg --clearsign</c>, <c>scripts/sign-settings.ps1 -ClearSign</c>) liegt
/// oder eine abgetrennte <c>&lt;datei&gt;.asc</c>. Die mitgelieferten
/// <c>appsettings.json</c> und <c>appsettings.{Environment}.json</c> warnen ohne
/// Signatur (<see cref="SignaturePolicy.Warn"/>); <c>appsettings.local.json</c>
/// und die Benutzerdateien dürfen unsigniert sein, werden aber geprüft, sobald
/// sie signiert sind (<see cref="SignaturePolicy.Optional"/>). Env-Variablen
/// bleiben ungeprüft.
/// </summary>
public static class SettingsSignatures
{
    /// <summary>Endung der Klartextsignatur neben der signierten Datei.</summary>
    public const string ClearsignedExtension = ".clearsigned.asc";

    /// <summary>Die mitgelieferten Dateien in <paramref name="directory"/>, in Ladereihenfolge.</summary>
    public static IReadOnlyList<string> Files(string directory, string environment)
    {
        var files = new List<string> { Path.Combine(directory, "appsettings.json") };
        var environmentFile = Path.Combine(directory, $"appsettings.{environment}.json");
        if (File.Exists(environmentFile))
        {
            files.Add(environmentFile);
        }

        return files;
    }

    /// <summary>
    /// Stufe für eine Datei aus dem Konfigurationsordner: die mitgelieferten
    /// warnen ohne Signatur, alle anderen (<c>appsettings.local.json</c>) nicht.
    /// </summary>
    public static SignaturePolicy PolicyFor(string fileName, string environment)
    {
        return string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, $"appsettings.{environment}.json", StringComparison.OrdinalIgnoreCase)
                ? SignaturePolicy.Warn
                : SignaturePolicy.Optional;
    }

    /// <summary>Prüft jede mitgelieferte Datei, ohne abzubrechen.</summary>
    public static IReadOnlyList<SignatureCheck> Check(
        string directory,
        string environment,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow)
    {
        return Files(directory, environment)
            .Select(file => VerifyFile(file, trusted, utcNow))
            .ToList();
    }

    /// <summary>Prüft <paramref name="path"/> wie <see cref="Read"/>; eine fehlende Datei ist ungültig.</summary>
    public static SignatureCheck VerifyFile(string path, PgpPublicKeyRingBundle trusted, DateTime utcNow)
    {
        return File.Exists(path)
            ? Read(path, File.ReadAllBytes(path), trusted, utcNow).Check
            : new SignatureCheck(path, false, "Datei fehlt.");
    }

    /// <summary>
    /// Prüft <paramref name="bytes"/> (Inhalt von <paramref name="path"/>), ohne zu
    /// werfen. Reihenfolge: die Datei ist selbst clearsigned (geladen wird ihr
    /// Klartext) → <c>&lt;path&gt;.clearsigned.asc</c> → <c>&lt;path&gt;.asc</c>
    /// → unsigniert (<see cref="SignatureCheck.Unsigned"/>). Bei einer
    /// Signaturdatei daneben wird die Datei selbst geladen und gegen deren
    /// Signatur geprüft — der Klartext in der Signaturdatei wird nicht gelesen,
    /// eine Änderung nach dem Signieren fällt also auf.
    /// </summary>
    public static SignedContent Read(string path, byte[] bytes, PgpPublicKeyRingBundle trusted, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(trusted);

        ClearsignedMessage? inline;
        try
        {
            ClearsignedMessage.TryParse(bytes, out inline);
        }
        catch (InvalidDataException exception)
        {
            return new SignedContent(bytes, new SignatureCheck(path, false, $"Clearsign-Datei unlesbar: {exception.Message}"));
        }

        if (inline is not null)
        {
            return new SignedContent(inline.Cleartext, PgpFileVerifier.VerifyClearsigned(path, inline, trusted, utcNow));
        }

        var clearsigned = path + ClearsignedExtension;
        if (File.Exists(clearsigned))
        {
            return new SignedContent(bytes, VerifyClearsignedCompanion(path, bytes, clearsigned, trusted, utcNow));
        }

        var detached = path + PgpFileVerifier.SignatureExtension;
        if (File.Exists(detached))
        {
            using var data = new MemoryStream(bytes, writable: false);
            using var signature = File.OpenRead(detached);
            return new SignedContent(bytes, PgpFileVerifier.Verify(path, data, signature, trusted, utcNow));
        }

        return new SignedContent(
            bytes,
            new SignatureCheck(path, false, $"nicht signiert (keine {Path.GetFileName(clearsigned)}).", Unsigned: true));
    }

    /// <summary>Ob <paramref name="path"/> existiert und eine Signatur hat (gültig oder nicht).</summary>
    public static bool IsSigned(string path)
    {
        return File.Exists(path)
            && !Read(path, File.ReadAllBytes(path), TrustedKeys.CoreBridge, DateTime.UtcNow).Check.Unsigned;
    }

    /// <summary>
    /// Liest wie <see cref="Read"/> und wendet <paramref name="policy"/> an.
    /// Liefert den zu ladenden Inhalt; eine unsignierte Datei unter
    /// <see cref="SignaturePolicy.Warn"/> landet in <paramref name="warnings"/>.
    /// Wirft <see cref="SettingsSignatureException"/> bei ungültiger Signatur
    /// (außer unter <see cref="SignaturePolicy.Ignore"/>) und bei fehlender unter
    /// <see cref="SignaturePolicy.Required"/>.
    /// </summary>
    public static byte[] Unwrap(
        string path,
        byte[] bytes,
        SignaturePolicy policy,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow,
        ICollection<SignatureCheck>? warnings = null)
    {
        var read = Read(path, bytes, trusted, utcNow);
        var check = read.Check;
        if (check.Valid || policy == SignaturePolicy.Ignore)
        {
            return read.Content;
        }

        if (!check.Unsigned || policy == SignaturePolicy.Required)
        {
            throw new SettingsSignatureException([check]);
        }

        if (policy == SignaturePolicy.Warn)
        {
            warnings?.Add(check);
        }

        return read.Content;
    }

    /// <summary>
    /// Wirft <see cref="SettingsSignatureException"/>, sobald eine Signatur
    /// ungültig ist; unsignierte Dateien sind nur eine Warnung.
    /// </summary>
    public static void RequireValid(IReadOnlyList<SignatureCheck> checks)
    {
        var failed = checks.Where(check => !check.Valid && !check.Unsigned).ToList();
        if (failed.Count > 0)
        {
            throw new SettingsSignatureException(failed);
        }
    }

    /// <summary>Warnzeile für eine unsignierte mitgelieferte Datei, fertig zum Anzeigen.</summary>
    public static string WarningMessage(SignatureCheck check)
    {
        return $"Warnung: {Path.GetFileName(check.File)} ist {check.Reason} "
            + "Signieren: .\\scripts\\sign-settings.ps1 -ClearSign (prüfen: cb settings-verify).";
    }

    private static SignatureCheck VerifyClearsignedCompanion(
        string path,
        byte[] bytes,
        string clearsigned,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow)
    {
        var name = Path.GetFileName(clearsigned);
        ClearsignedMessage? message;
        try
        {
            if (!ClearsignedMessage.TryParse(File.ReadAllBytes(clearsigned), out message))
            {
                return new SignatureCheck(path, false, $"{name} ist keine Clearsign-Datei.");
            }
        }
        catch (InvalidDataException exception)
        {
            return new SignatureCheck(path, false, $"{name} unlesbar: {exception.Message}");
        }

        // Signiert gilt, was geladen wird: die Datei daneben, kanonisch wie gpg sie signiert.
        var check = PgpFileVerifier.VerifyClearsigned(
            path, message! with { CanonicalText = ClearsignedMessage.Canonicalize(bytes) }, trusted, utcNow);
        return check.ContentMismatch
            ? check with { Reason = $"Inhalt passt nicht zu {name} (nach dem Signieren geändert?)." }
            : check;
    }
}

/// <summary>
/// Eine oder mehrere Dateien sind nicht gültig signiert. Die Meldung nennt
/// jede Datei mit Grund und ist fertig zum Anzeigen.
/// </summary>
public sealed class SettingsSignatureException(IReadOnlyList<SignatureCheck> failed)
    : Exception(BuildMessage(failed))
{
    public IReadOnlyList<SignatureCheck> Failed { get; } = failed;

    private static string BuildMessage(IReadOnlyList<SignatureCheck> failed)
    {
        var lines = failed.Select(check => $"  {Path.GetFileName(check.File)}: {check.Reason}");
        return "Konfiguration nicht vertrauenswürdig signiert:" + Environment.NewLine
            + string.Join(Environment.NewLine, lines);
    }
}
