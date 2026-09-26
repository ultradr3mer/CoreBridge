using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CoreBridge.Cli;
using CoreBridge.Core.Config;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Cli.Tests;

/// <summary>Ergebnis eines CLI-Kommandos; <paramref name="Error"/> ist die fertige Fehlermeldung bei Exit ≠ 0.</summary>
internal sealed record CliRunResult(int ExitCode, IReadOnlyList<string> Lines, string? Error = null);

/// <summary>
/// Gemeinsame Namen, Config und Temp-Store der Lifecycle-Stufen; den
/// Fortschrittszustand (Image, VM) hinterlässt jeweils die vorherige Stufe.
/// CLI-Kommandos laufen ausschließlich über den öffentlichen
/// <see cref="Program.Run"/>-Dispatcher (kein CLI-Kindprozess). Nur die
/// interaktiven Verbindungen (vm-connect, SSH) gehen direkt über die
/// Session-Typen aus CoreBridge.Core, weil `multipass shell` ein TTY braucht.
/// </summary>
public sealed class CliLifecycleFixture : IDisposable
{
    private readonly TempDirectory store;
    private readonly AppConfig config;
    private bool disposed;

    public CliLifecycleFixture()
    {
        store = new TempDirectory();
        config = new AppConfig
        {
            // Ohne interaktive Rückfrage (Antwort Nein) darf ein langer cloud-init-Launch
            // nicht nach dem ersten Abschnitt abgebrochen werden.
            DefaultTimeoutVmCreation = "2400s",
            // Start/Stop/Exec/Transfer warten DefaultTimeout (Standard 10 s) — für echte VMs zu kurz.
            DefaultTimeout = "600s",
            // Jeder echte Multipass-Aufruf wird protokolliert — außerhalb des
            // Temp-Stores, damit die Aufzeichnung den Lauf überlebt und per
            // ReplayMultipassCli wiedergegeben werden kann. Bootstrap bleibt
            // Standard: hier läuft garantiert das echte Multipass.
            MultipassLogPath = "artifacts/mp-log/cli-lifecycle",
            Images = new ImagesConfig
            {
                StoreRoot = store.Path,
                Components = new Dictionary<string, ComponentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["claude"] = new()
                    {
                        Script = "scripts/components/install-claude.sh",
                        InstallPath = "/usr/local/sbin/corebridge-install-claude",
                        Packages = ["bash", "ca-certificates", "curl", "sudo"],
                        PackageUpdate = true,
                        InstallUser = "ubuntu"
                    },
                    ["dotnet"] = new()
                    {
                        Snap = "dotnet-sdk-100",
                        Classic = true,
                        Script = "scripts/components/install-dotnet.sh",
                        InstallPath = "/usr/local/sbin/corebridge-install-dotnet",
                        InstallUser = "ubuntu"
                    }
                }
            }
        };

        var suffix = $"{Environment.ProcessId}-{Guid.NewGuid():N}"[..20];
        ImageName = $"cbx-cli-img-{suffix}";
        VmName = $"cbx-cli-vm-{suffix}";
    }

    public string ImageName { get; }

    public string VmName { get; }

    internal string ProfileConfigPath => Path.Combine(store.Path, "profiles", ImageName, ImageName + ".profile.json");

    internal string CloudInitPath => Path.Combine(store.Path, "profiles", ImageName, "cloud-init.yaml");

    internal string SshPrivateKeyPath => Path.Combine(store.Path, "ssh", "id_rsa");

    /// <summary>
    /// Erzeugt einmalig ein RSA-Schlüsselpaar im Temp-Store (ohne ssh-keygen):
    /// Private Key als PKCS#1-PEM für SSH.NET, Public Key im OpenSSH-Format für
    /// ~/.ssh/authorized_keys der VM.
    /// </summary>
    internal string EnsureSshKey()
    {
        var publicKeyPath = SshPrivateKeyPath + ".pub";
        if (File.Exists(publicKeyPath))
        {
            return File.ReadAllText(publicKeyPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SshPrivateKeyPath)!);
        using var rsa = RSA.Create(3072);
        File.WriteAllText(SshPrivateKeyPath, rsa.ExportRSAPrivateKeyPem());

        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        using var blob = new MemoryStream();
        WriteSshString(blob, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteSshString(blob, ToMpint(parameters.Exponent!));
        WriteSshString(blob, ToMpint(parameters.Modulus!));
        var publicKey = $"ssh-rsa {Convert.ToBase64String(blob.ToArray())} cbx-cli-lifecycle";
        File.WriteAllText(publicKeyPath, publicKey);
        return publicKey;
    }

    private static byte[] ToMpint(byte[] unsignedBigEndian)
    {
        return (unsignedBigEndian[0] & 0x80) != 0 ? [0, .. unsignedBigEndian] : unsignedBigEndian;
    }

    private static void WriteSshString(Stream stream, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
    }

    /// <summary>
    /// Konsumiert den Ausgabestrom vollständig, schreibt jede Meldung sofort ins
    /// Log und bildet <see cref="ProgramExecutionException"/> auf ihren Exit-Code
    /// ab (0 bei Erfolg); die Fehlermeldung landet ebenfalls im Log. Andere
    /// Exceptions werden nicht abgefangen.
    /// </summary>
    internal async Task<CliRunResult> RunAsync(IOptions options, Action<string> log)
    {
        var lines = new List<string>();
        try
        {
            // Der Test ist der Konsument des Ereignisstroms — dieselbe Rolle wie
            // die ConsoleAdapter in der echten CLI: Ausgabe sammeln und
            // Rückfragen beantworten.
            await foreach (var cliEvent in Program.Run(config, options))
            {
                switch (cliEvent)
                {
                    // Reihenfolge zählt: InteractionEvent erbt von StdEvent.
                    case ChoiceEvent<YesNoOptions> yesNo:
                        log($"{yesNo.Printable} [y/n] y (Test)");
                        yesNo.SetResult(YesNoOptions.Yes);
                        break;
                    case TextEvent text:
                        log($"{text.Printable}(Test: keine Eingabe)");
                        text.SetResult(null);
                        break;
                    case ProgressEvent:
                        break;
                    case StdEvent std:
                        log(std.Printable);
                        lines.Add(std.Printable);
                        break;
                    case ErrEvent err:
                        log(err.Printable);
                        break;
                    default:
                        log(cliEvent.Printable);
                        lines.Add(cliEvent.Printable);
                        break;
                }
            }

            return new CliRunResult(0, lines);
        }
        catch (ProgramExecutionException exception)
        {
            log($"Fehler (Exit {exception.ExitCode}): {exception.Message}");
            return new CliRunResult(exception.ExitCode, lines, exception.Message);
        }
    }

    /// <summary>Wie <see cref="RunAsync"/>, wirft aber bei Exit ≠ 0.</summary>
    internal async Task<CliRunResult> RunCheckedAsync(IOptions options, Action<string> log)
    {
        var result = await RunAsync(options, log);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{options.GetType().Name} fehlgeschlagen (Exit {result.ExitCode}): {result.Error}");
        }

        return result;
    }

    /// <summary>State der Test-VM laut vm-list, <c>null</c> wenn sie nicht existiert.</summary>
    internal async Task<string?> QueryVmStateAsync(Action<string> log)
    {
        var list = await RunCheckedAsync(new VmListOptions(), log);
        return FindVmState(list, VmName);
    }

    /// <summary>Scheitert bei Exit ≠ 0 mit der Fehlermeldung des Kommandos statt „Expected 0, Actual 1“.</summary>
    internal static void AssertSucceeded(CliRunResult result)
    {
        if (result.ExitCode != 0)
        {
            Assert.Fail($"Exit {result.ExitCode}: {result.Error}");
        }
    }

    /// <summary>
    /// Prüft Exit 0 und die Abschlusszeile zustandsändernder Kommandos:
    /// <c>&lt;label&gt;: OK (&lt;Dauer&gt;) — State: &lt;state&gt;</c>.
    /// </summary>
    internal static void AssertCompleted(CliRunResult result, string label, string state)
    {
        AssertSucceeded(result);
        Assert.NotEmpty(result.Lines);
        Assert.Matches(
            $@"^{Regex.Escape(label)}: OK \([^)]*\) — State: {Regex.Escape(state)}$",
            result.Lines[^1]);
    }

    internal static string? FindVmState(CliRunResult list, string vmName)
    {
        return list.Lines
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(columns => columns.Length >= 2 && columns[0] == vmName)
            .Select(columns => columns[1])
            .FirstOrDefault();
    }

    /// <summary>
    /// Räumt anhand des echten Zustands auf, egal an welcher Stufe der Lauf
    /// abgebrochen ist: VM laut vm-list (endgültig, auch aus dem Papierkorb),
    /// Image laut Modellverzeichnis.
    /// </summary>
    internal async Task CleanupAsync(Action<string> log)
    {
        if (await QueryVmStateAsync(log) is not null)
        {
            await RunCheckedAsync(new VmDeleteOptions { Name = VmName, Purge = true }, log);
        }

        var profileDirectory = Path.Combine(store.Path, "profiles", ImageName);
        if (Directory.Exists(profileDirectory))
        {
            await RunCheckedAsync(new InitDeleteOptions { Name = ImageName }, log);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // Kein ITestOutputHelper beim Dispose — die Meldungen gehen in die Exception.
        var log = new List<string>();
        try
        {
            CleanupAsync(log.Add).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                ["Lifecycle-Cleanup fehlgeschlagen:", exception.Message, "Log:", .. log]),
                exception);
        }
        finally
        {
            store.Dispose();
        }
    }
}
