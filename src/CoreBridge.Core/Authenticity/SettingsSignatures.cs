using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Signaturpflicht für die mitgelieferte Konfiguration: <c>appsettings.json</c>
/// muss existieren und gültig signiert sein, <c>appsettings.{Environment}.json</c>
/// nur, wenn es die Datei gibt. <c>appsettings.local.json</c> und
/// Env-Variablen bleiben bewusst ungeprüft (lokale Überschreibungen).
/// </summary>
public static class SettingsSignatures
{
    /// <summary>Die signaturpflichtigen Dateien in <paramref name="directory"/>, in Ladereihenfolge.</summary>
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

    /// <summary>Prüft jede signaturpflichtige Datei, ohne abzubrechen.</summary>
    public static IReadOnlyList<SignatureCheck> Check(
        string directory,
        string environment,
        PgpPublicKeyRingBundle trusted,
        DateTime utcNow)
    {
        return Files(directory, environment)
            .Select(file => PgpFileVerifier.VerifyFile(file, trusted, utcNow))
            .ToList();
    }

    /// <summary>Wirft <see cref="SettingsSignatureException"/>, sobald eine Prüfung fehlgeschlagen ist.</summary>
    public static void RequireValid(IReadOnlyList<SignatureCheck> checks)
    {
        var failed = checks.Where(check => !check.Valid).ToList();
        if (failed.Count > 0)
        {
            throw new SettingsSignatureException(failed);
        }
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
