using System.Diagnostics;
using System.Text.RegularExpressions;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Inits;

/// <summary>
/// Der SSH-Schlüssel, mit dem der Host (git über <c>ssh://ubuntu@&lt;vm&gt;.mshome.net</c>)
/// die VMs erreicht. Liegt je Benutzer — nicht je Checkout — unter
/// <c>%LOCALAPPDATA%\CoreBridge\ssh</c>, damit alle Worktrees denselben Schlüssel nutzen
/// und Windows-OpenSSH die Rechte der privaten Datei akzeptiert (Profilordner: nur
/// Benutzer, SYSTEM, Administratoren). Erzeugt wird er einmalig per <c>ssh-keygen</c>;
/// die eigenen Schlüssel in <c>~\.ssh</c> bleiben unberührt.
/// </summary>
public static partial class HostSshKey
{
    /// <summary>Ablage des öffentlichen Schlüssels im Gast; das git-Skript liest ihn dort.</summary>
    public const string GuestKeyFile = "/etc/corebridge/host_authorized_keys";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CoreBridge",
        "ssh");

    public static string PrivateKeyPath(string? directory = null) => Path.Combine(directory ?? DefaultDirectory, "id_ed25519");

    public static string PublicKeyPath(string? directory = null) => PrivateKeyPath(directory) + ".pub";

    /// <summary>Eigene known_hosts der VMs — neu angelegte VMs ändern ihren Host-Key, das soll <c>~\.ssh</c> nicht berühren.</summary>
    public static string KnownHostsPath(string? directory = null) => Path.Combine(directory ?? DefaultDirectory, "known_hosts");

    /// <summary>Ob eine der Komponenten den Host-Schlüssel im Gast braucht.</summary>
    public static bool RequiredBy(ImagesConfig config, IEnumerable<string> components)
    {
        return InitComponents.Resolve(config, components).Any(c => c.AuthorizeHostKey);
    }

    /// <summary>
    /// Pfad des öffentlichen Schlüssels; fehlt das Paar, wird es per
    /// <c>ssh-keygen -t ed25519</c> ohne Passphrase erzeugt.
    /// </summary>
    public static string EnsurePublicKeyPath(string? directory = null)
    {
        var publicKey = PublicKeyPath(directory);
        if (File.Exists(publicKey) && File.Exists(PrivateKeyPath(directory)))
        {
            return publicKey;
        }

        Directory.CreateDirectory(directory ?? DefaultDirectory);
        var startInfo = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", string.Empty, "-C", $"corebridge@{Environment.MachineName}", "-f", PrivateKeyPath(directory) })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new MultipassException("ssh-keygen konnte nicht gestartet werden (Windows-OpenSSH-Client installiert?).");
        var stdErr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(publicKey))
        {
            throw new MultipassException($"ssh-keygen ist fehlgeschlagen (Exit {process.ExitCode}): {stdErr.Result.Trim()}");
        }

        return publicKey;
    }

    /// <summary>Liest einen öffentlichen Schlüssel und prüft, dass es genau eine OpenSSH-Zeile ist.</summary>
    public static string ReadPublicKey(string path)
    {
        var key = File.ReadAllText(path).Trim();
        if (!PublicKeyLine().IsMatch(key))
        {
            throw new MultipassException($"Kein gültiger öffentlicher SSH-Schlüssel: {path}");
        }

        return key;
    }

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+) [A-Za-z0-9+/=]+( [^\r\n]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicKeyLine();
}
