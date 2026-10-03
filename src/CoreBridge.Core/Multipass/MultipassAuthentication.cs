using System.Security.Cryptography;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Vertraut einen Client beim Multipass-Dienst an: <c>multipass set local.passphrase</c>
/// setzt die Passphrase, <c>multipass authenticate</c> nimmt damit einen Client auf.
/// Setzen kann nur ein bereits vertrauter Client; die vertrauten Zertifikate liegen im
/// Speicherort (<c>MULTIPASS_STORAGE</c>). Deshalb setzt <c>multipass-store</c> die
/// Passphrase vor dem Umstellen und authentifiziert danach.
/// </summary>
/// <remarks>
/// Läuft absichtlich über einen eigenen <see cref="MultipassCli"/> statt über den Client
/// der CLI: der kann ein <c>LoggingMultipassCli</c> sein, und die Passphrase gehört in
/// kein Log. Sie steht nur kurz auf der Kommandozeile von <c>multipass.exe</c>.
/// </remarks>
public class MultipassAuthentication(IMultipassCli? cli = null)
{
    private readonly IMultipassCli cli = cli ?? new MultipassCli();

    /// <summary>Zufällige Passphrase für genau einen Vorgang; niemand muss sie sich merken.</summary>
    public static string NewPassphrase() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary><c>multipass set local.passphrase=…</c>; braucht einen bereits vertrauten Client.</summary>
    public MultipassRunResult SetPassphrase(string passphrase, int timeoutMs) =>
        this.cli.Run(["set", $"local.passphrase={passphrase}"], timeoutMs);

    /// <summary><c>multipass authenticate …</c>: nimmt diesen Client mit der Passphrase auf.</summary>
    public MultipassRunResult Authenticate(string passphrase, int timeoutMs) =>
        this.cli.Run(["authenticate", passphrase], timeoutMs);
}
