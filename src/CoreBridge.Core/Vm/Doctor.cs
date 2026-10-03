using System.Text.Json;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Vm;

public sealed record DoctorReport(
    bool Found,
    string? BinaryPath,
    string? ClientVersion,
    string? DaemonVersion,
    string? RawVersionOutput,
    string? Error,
    IReadOnlyList<string> Hints,
    bool NotAuthenticated = false);

public static class Doctor
{
    public static readonly IReadOnlyList<string> InstallationHints = new[]
    {
        "Doku: https://canonical.com/multipass/docs/stable/how-to-guides/install-multipass/#how-to-guides-install-multipass",
        "Direktdownload: https://canonical.com/multipass/download/windows",
        "winget install Canonical.Multipass (von Canonical nicht offiziell unterstützt)",
        "Binary an anderem Ort über die Env-Variable CORE_BRIDGE_MULTIPASS_EXE bekannt geben"
    };

    /// <param name="locate">Binary-Auflösung; <c>null</c> = Standard.</param>
    /// <param name="cli">
    /// Client für die Versionsabfrage. Standard ist <see cref="MultipassCli"/>;
    /// die CLI übergibt den Client aus <see cref="MultipassCliFactory"/>.
    /// </param>
    /// <param name="timeoutMs">Wartezeit auf <c>multipass version</c>; die CLI übergibt <c>DefaultTimeout</c>.</param>
    public static DoctorReport Check(Func<string?>? locate = null, IMultipassCli? cli = null, int timeoutMs = 60_000)
    {
        if (locate is null)
        {
            locate = () => MultipassBinaryLocator.LocateOrNull();
        }

        var binary = locate();
        if (binary is null)
        {
            return new DoctorReport(false, null, null, null, null, null, InstallationHints);
        }

        try
        {
            var multipass = cli ?? new MultipassCli(locate);
            var result = multipass.Run(new[] { "version", "--format", "json" }, timeoutMs, binary);
            if (result.ExitCode != 0)
            {
                return new DoctorReport(
                    true,
                    binary,
                    null,
                    null,
                    result.StdOut + result.StdErr,
                    $"multipass version endete mit Exit-Code {result.ExitCode}.",
                    InstallationHints);
            }

            var (client, daemon) = MultipassVersion.Parse(result.StdOut);
            var notAuthenticated = daemon is null && IsNotAuthenticated(multipass, binary, timeoutMs);
            return new DoctorReport(
                true,
                binary,
                client,
                daemon,
                result.StdOut,
                notAuthenticated
                    ? NotAuthenticatedMessage
                    : daemon is null ? "Daemon hat keine Version gemeldet (läuft der multipassd-Dienst?)." : null,
                Array.Empty<string>(),
                notAuthenticated);
        }
        catch (JsonException)
        {
            return new DoctorReport(
                true,
                binary,
                null,
                null,
                null,
                "Antwort von multipass version war kein gültiges JSON.",
                InstallationHints);
        }
        catch (MultipassTimeoutException exception)
        {
            return new DoctorReport(
                true,
                binary,
                null,
                null,
                exception.StdOut,
                $"multipass version: {exception.Message}",
                InstallationHints);
        }
    }

    /// <summary>
    /// Meldung, wenn der Dienst läuft, den Client aber ablehnt. Warten hilft dann nicht:
    /// der Dienst kennt das Client-Zertifikat nicht (typisch nach einem neuen Speicherort
    /// über <c>MULTIPASS_STORAGE</c>, dessen Daten die bisherigen Zertifikate nicht enthalten).
    /// </summary>
    public const string NotAuthenticatedMessage =
        "Der Multipass-Dienst läuft, lehnt diesen Client aber ab (nicht authentifiziert) — Warten hilft nicht. "
        + "Typisch nach einem neuen Speicherort (MULTIPASS_STORAGE): der Dienst kennt das Client-Zertifikat nicht mehr. "
        + "Abhilfe: als Administrator eine Passphrase setzen (`multipass set local.passphrase`), "
        + "danach als normaler Benutzer `multipass authenticate` mit derselben Passphrase.";

    /// <summary>
    /// <c>multipass version</c> endet auch dann mit Exit 0, wenn der Dienst den Client
    /// ablehnt — es fehlt nur die Daemon-Version. Erst ein Befehl, der den Dienst braucht,
    /// verrät den Grund: „The client is not authenticated with the Multipass service.“
    /// Scheitert die Probe selbst (Timeout, unbekannter Aufruf einer Attrappe), gilt das
    /// nicht als Ablehnung.
    /// </summary>
    private static bool IsNotAuthenticated(IMultipassCli multipass, string binary, int timeoutMs)
    {
        try
        {
            var probe = multipass.Run(new[] { "list", "--format", "json" }, timeoutMs, binary);
            return (probe.StdOut + probe.StdErr).Contains("not authenticated", StringComparison.OrdinalIgnoreCase);
        }
        catch (MultipassException)
        {
            return false;
        }
    }
}
