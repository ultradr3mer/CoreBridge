using CoreBridge.Cli;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;

namespace CoreBridge.Cli.Tests;

internal sealed record CliLifecycleCleanupReport(bool Success, string Diagnostics)
{
    public static CliLifecycleCleanupReport Ok() => new(true, "Keine Lifecycle-Ressourcen offen.");
}

/// <summary>
/// Gemeinsamer, serialisierter Zustand für die nummerierten Integrationstufen.
/// Der Fixture ruft ausschließlich den öffentlichen <see cref="Program.Run"/>-
/// Dispatcher auf; es startet keinen CLI-Kindprozess und capturt keinen Output.
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

    internal ProgramExecution Run(IOptions options)
    {
        return Program.Run(config, options);
    }

    internal void RecordStatus(ProgramExecution result)
    {
        MultipassAvailable = result.Success;
        SkipReason = MultipassAvailable ? null : "CLI-Integration übersprungen — multipass-status fehlgeschlagen.";
    }

    internal bool ShouldSkip()
    {
        return !MultipassAvailable;
    }

    internal CliLifecycleCleanupReport Cleanup()
    {
        var failures = new List<string>();

        if (VmCleanupRequired)
        {
            var delete = Run(new VmDeleteOptions { Name = VmName });
            if (!delete.Success)
            {
                failures.Add($"vm-delete '{VmName}' fehlgeschlagen (Exit {delete.ExitCode}).");
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
                var delete = Run(new ImageDeleteOptions { Name = ImageName });
                if (!delete.Success)
                {
                    failures.Add($"image-delete '{ImageName}' fehlgeschlagen (Exit {delete.ExitCode}).");
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
        CliLifecycleCleanupReport report;
        try
        {
            report = Cleanup();
        }
        finally
        {
            store.Dispose();
        }

        if (!report.Success)
        {
            throw new InvalidOperationException("Lifecycle-Cleanup fehlgeschlagen:" + Environment.NewLine + report.Diagnostics);
        }
    }
}
