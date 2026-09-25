using System.Security.Cryptography;
using System.Text;
using CoreBridge.Cli;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;

namespace CoreBridge.Cli.Tests;

internal sealed record CliLifecycleCleanupReport(bool Success, string Diagnostics)
{
    public static CliLifecycleCleanupReport Ok() => new(true, "Keine Lifecycle-Ressourcen offen.");
}

internal sealed record CliRunResult(int ExitCode, IReadOnlyList<string> Lines);

/// <summary>
/// Gemeinsamer, serialisierter Zustand für die nummerierten Integrationstufen.
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

    internal string ModelPath => Path.Combine(store.Path, "models", ImageName, ImageName + ".model");

    internal string ProfileConfigPath => Path.Combine(store.Path, "models", ImageName, ImageName + ".profile.json");

    internal bool MultipassAvailable { get; private set; }

    internal string? SkipReason { get; private set; }

    internal bool ImageConfigured { get; set; }

    internal bool VmCreated { get; set; }

    internal bool ImageCleanupRequired { get; set; }

    internal bool VmCleanupRequired { get; set; }

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
            await foreach (var line in Program.Run(config, options))
            {
                log(line);
                lines.Add(line);
            }

            return new CliRunResult(0, lines);
        }
        catch (ProgramExecutionException exception)
        {
            log($"Fehler (Exit {exception.ExitCode}): {exception.Message}");
            return new CliRunResult(exception.ExitCode, lines);
        }
    }

    internal void RecordStatus(int exitCode)
    {
        MultipassAvailable = exitCode == 0;
        SkipReason = MultipassAvailable ? null : "CLI-Integration übersprungen — multipass-status fehlgeschlagen.";
    }

    internal bool ShouldSkip()
    {
        return !MultipassAvailable;
    }

    internal async Task<CliLifecycleCleanupReport> CleanupAsync(Action<string> log)
    {
        var failures = new List<string>();

        if (VmCleanupRequired)
        {
            var exitCode = (await RunAsync(new VmDeleteOptions { Name = VmName, Purge = true }, log)).ExitCode;
            if (exitCode != 0)
            {
                failures.Add($"vm-delete '{VmName}' fehlgeschlagen (Exit {exitCode}).");
            }
            else
            {
                VmCleanupRequired = false;
            }
        }

        if (ImageCleanupRequired)
        {
            var modelDirectory = Path.Combine(store.Path, "models", ImageName);
            if (!Directory.Exists(modelDirectory))
            {
                ImageCleanupRequired = false;
            }
            else
            {
                var exitCode = (await RunAsync(new ImageDeleteOptions { Name = ImageName }, log)).ExitCode;
                if (exitCode != 0)
                {
                    failures.Add($"image-delete '{ImageName}' fehlgeschlagen (Exit {exitCode}).");
                }
                else if (Directory.Exists(modelDirectory))
                {
                    failures.Add($"Image-Profil '{ImageName}' existiert nach image-delete weiterhin.");
                }
                else
                {
                    ImageCleanupRequired = false;
                }
            }
        }

        return failures.Count == 0
            ? CliLifecycleCleanupReport.Ok()
            : new CliLifecycleCleanupReport(false, string.Join(Environment.NewLine, failures));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // Kein ITestOutputHelper beim Dispose — die Meldungen gehen in die Diagnose.
        var log = new List<string>();
        CliLifecycleCleanupReport report;
        try
        {
            report = CleanupAsync(log.Add).GetAwaiter().GetResult();
        }
        finally
        {
            store.Dispose();
        }

        if (!report.Success)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                ["Lifecycle-Cleanup fehlgeschlagen:", report.Diagnostics, "Log:", .. log]));
        }
    }
}
