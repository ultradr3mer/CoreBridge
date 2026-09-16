using System.Text.Json;

namespace CoreBridge.Core.Vm;

public sealed record DoctorReport(
    bool Found,
    string? BinaryPath,
    string? ClientVersion,
    string? DaemonVersion,
    string? RawVersionOutput,
    string? Error,
    IReadOnlyList<string> Hints);

public static class Doctor
{
    public static readonly IReadOnlyList<string> InstallationHints = new[]
    {
        "Doku: https://canonical.com/multipass/docs/stable/how-to-guides/install-multipass/#how-to-guides-install-multipass",
        "Direktdownload: https://canonical.com/multipass/download/windows",
        "winget install Canonical.Multipass (von Canonical nicht offiziell unterstützt)",
        "Binary an anderem Ort über die Env-Variable CORE_BRIDGE_MULTIPASS_EXE bekannt geben"
    };

    public static DoctorReport Check(Func<string?>? locate = null)
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
            var cli = new MultipassCli(locate);
            var result = cli.Run(new[] { "version", "--format", "json" }, 60_000, binary);
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
            return new DoctorReport(
                true,
                binary,
                client,
                daemon,
                result.StdOut,
                daemon is null ? "Daemon hat keine Version gemeldet (läuft der multipassd-Dienst?)." : null,
                Array.Empty<string>());
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
}
