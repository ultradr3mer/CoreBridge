using System.Diagnostics;
using CoreBridge.Core.Config;
using CoreBridge.Core.Common;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace CoreBridge.Core.Authenticity;

/// <summary>
/// Signiert eine Datei mit dem Schlüssel des Benutzers als Klartextsignatur daneben
/// (<c>&lt;datei&gt;.clearsigned.asc</c>) — das Format, das <see cref="SettingsSignatures"/> prüft.
/// </summary>
public interface IFileSigner
{
    /// <summary>Womit signiert wird, zum Anzeigen (Schlüsseldatei oder gpg-Schlüssel).</summary>
    string Label { get; }

    void ClearSign(string file);
}

/// <summary>
/// Signieren mit dem konfigurierten Schlüssel (<see cref="SigningConfig"/>). Eine alte Signatur
/// gehört nie zu neuem Inhalt: wer eine signierbare Datei neu schreibt, ruft
/// <see cref="RemoveSignatures"/>.
/// </summary>
public static class FileSigning
{
    /// <summary>Passphrase eines geschützten <see cref="SigningConfig.KeyFile"/>.</summary>
    public const string PasswordVariable = "CORE_BRIDGE_SIGNING_PASSWORD";

    /// <summary>Der Signierer laut Config; <c>null</c>, wenn kein Schlüssel eingetragen ist.</summary>
    public static IFileSigner? Create(SigningConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.KeyFile))
        {
            return new KeyFileSigner(RepoPaths.Resolve(config.KeyFile), config.KeyId);
        }

        return string.IsNullOrWhiteSpace(config.GpgKey) ? null : new GpgSigner(config.GpgKey.Trim());
    }

    /// <summary>Entfernt Signaturen neben <paramref name="file"/> (abgetrennt und Klartext).</summary>
    public static void RemoveSignatures(string file)
    {
        File.Delete(file + SettingsSignatures.ClearsignedExtension);
        File.Delete(file + PgpFileVerifier.SignatureExtension);
    }
}

/// <summary>Signiert mit BouncyCastle aus einem exportierten geheimen Schlüssel — ohne gpg.</summary>
public sealed class KeyFileSigner(string keyFile, string? keyId) : IFileSigner
{
    private SigningKey? _key;

    public string Label => keyId is null ? keyFile : $"{keyFile} ({keyId})";

    public void ClearSign(string file)
    {
        var key = _key ??= ReadKey();
        var data = File.ReadAllBytes(file);
        using var output = File.Create(file + SettingsSignatures.ClearsignedExtension);
        PgpFileSigner.ClearSign(data, output, key);
    }

    private SigningKey ReadKey()
    {
        if (!File.Exists(keyFile))
        {
            throw new SigningException($"Signierschlüssel nicht gefunden: {keyFile} (Signing:KeyFile).");
        }

        var passphrase = (Environment.GetEnvironmentVariable(FileSigning.PasswordVariable) ?? string.Empty).ToCharArray();
        try
        {
            using var stream = File.OpenRead(keyFile);
            return PgpFileSigner.ReadSigningKey(stream, passphrase, keyId);
        }
        catch (PgpException exception)
        {
            throw new SigningException(
                passphrase.Length == 0
                    ? $"Signierschlüssel {keyFile} lässt sich nicht entsperren — Passphrase in {FileSigning.PasswordVariable} setzen ({exception.Message})."
                    : $"Signierschlüssel {keyFile} lässt sich nicht lesen oder entsperren: {exception.Message}",
                exception);
        }
        finally
        {
            Array.Clear(passphrase);
        }
    }
}

/// <summary>
/// Signiert über <c>gpg --clearsign</c> mit einem Schlüssel aus dem gpg-Schlüsselbund; die
/// Passphrase fragt gpg-agent/pinentry. Wie <c>scripts/sign-settings.ps1</c> wird ein gpg
/// außerhalb von Git for Windows bevorzugt (dessen Schlüsselbund ist meist leer).
/// </summary>
public sealed class GpgSigner(string key) : IFileSigner
{
    public string Label => $"gpg {key}";

    public void ClearSign(string file)
    {
        var start = new ProcessStartInfo(LocateGpg())
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in new[] { "--yes", "--local-user", key, "--output", file + SettingsSignatures.ClearsignedExtension, "--clearsign", file })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new SigningException("gpg ließ sich nicht starten.");
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new SigningException($"gpg --clearsign {file} fehlgeschlagen (Exit {process.ExitCode}): {error.Result.Trim()}");
        }
    }

    private static string LocateGpg()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "gpg.exe" } : ["gpg"];
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name)))
            .Where(File.Exists)
            .OrderBy(path => path.Contains($"{Path.DirectorySeparatorChar}Git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();
        return candidates.FirstOrDefault()
            ?? throw new SigningException("gpg nicht gefunden — Gpg4win installieren oder stattdessen Signing:KeyFile verwenden (ohne gpg).");
    }
}

/// <summary>Signieren ging nicht: Schlüssel fehlt, lässt sich nicht entsperren, gpg scheitert.</summary>
public sealed class SigningException(string message, Exception? inner = null) : Exception(message, inner);
