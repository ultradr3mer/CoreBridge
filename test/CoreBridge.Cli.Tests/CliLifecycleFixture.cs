namespace CoreBridge.Cli.Tests;

internal sealed record CliLifecycleCleanupReport(bool Success, string Diagnostics)
{
    public static CliLifecycleCleanupReport Ok() => new(true, "Keine Lifecycle-Ressourcen offen.");
}

/// <summary>
/// Gemeinsamer, serialisierter Zustand für die nummerierten Integrationstest-Stufen.
/// Das Fixture besitzt genau ein temporäres Profil und höchstens eine VM und
/// räumt beide auch nach einem teilweise fehlgeschlagenen Stage-Lauf auf.
/// </summary>
public sealed class CliLifecycleFixture : IDisposable
{
    private readonly TempDirectory store;
    private readonly CliRunner cli;
    private bool disposed;

    public CliLifecycleFixture()
    {
        store = new TempDirectory();
        cli = new CliRunner(store.Path);
        var suffix = $"{Environment.ProcessId}-{Guid.NewGuid():N}"[..20];
        ImageName = $"cbx-cli-img-{suffix}";
        VmName = $"cbx-cli-vm-{suffix}";
    }

    public string ImageName { get; }

    public string VmName { get; }

    internal CliRunner Cli => cli;

    internal string ModelPath => Path.Combine(cli.StoreRoot, "models", ImageName, ImageName + ".model");

    internal bool MultipassAvailable { get; private set; }

    internal string? SkipReason { get; private set; }

    internal bool ImageConfigured { get; set; }

    internal bool VmCreated { get; set; }

    internal bool ImageCleanupRequired { get; set; }

    internal bool VmCleanupRequired { get; set; }

    internal void RecordStatus(CliProcessResult result)
    {
        MultipassAvailable = result.ExitCode == 0 && !result.TimedOut;
        SkipReason = MultipassAvailable
            ? null
            : "CLI-Integration übersprungen — multipass-status: " + result.StdErr.Trim();
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
            var before = cli.Run(["vm-list"], TimeSpan.FromMinutes(2));
            if (before.TimedOut || before.ExitCode != 0)
            {
                failures.Add("vm-list vor Cleanup fehlgeschlagen: " + Describe(before));
            }
            else
            {
                var state = FindVmState(before.StdOut, VmName);
                if (state is null)
                {
                    VmCleanupRequired = false;
                }
                else if (string.Equals(state, "Deleted", StringComparison.OrdinalIgnoreCase))
                {
                    VmCleanupRequired = false;
                }
                else
                {
                    var delete = cli.Run(["vm-delete", "--name", VmName], TimeSpan.FromMinutes(5));
                    if (delete.TimedOut || delete.ExitCode != 0)
                    {
                        failures.Add("vm-delete fehlgeschlagen: " + Describe(delete));
                    }
                    else
                    {
                        var after = cli.Run(["vm-list"], TimeSpan.FromMinutes(2));
                        var finalState = after.ExitCode == 0 ? FindVmState(after.StdOut, VmName) : null;
                        if (!string.Equals(finalState, "Deleted", StringComparison.OrdinalIgnoreCase))
                        {
                            failures.Add(
                                $"VM '{VmName}' wurde nicht als Deleted verifiziert: {Describe(after)}");
                        }
                        else
                        {
                            VmCleanupRequired = false;
                        }
                    }
                }
            }
        }

        if (ImageCleanupRequired)
        {
            var modelDirectory = Path.Combine(cli.StoreRoot, "models", ImageName);
            if (!Directory.Exists(modelDirectory))
            {
                ImageCleanupRequired = false;
            }
            else
            {
                var delete = cli.Run(["image-delete", "--name", ImageName], TimeSpan.FromMinutes(2));
                if (delete.TimedOut || delete.ExitCode != 0)
                {
                    failures.Add("image-delete fehlgeschlagen: " + Describe(delete));
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
            cli.Dispose();
            store.Dispose();
        }

        if (!report.Success)
        {
            throw new InvalidOperationException("Lifecycle-Cleanup fehlgeschlagen:" + Environment.NewLine + report.Diagnostics);
        }
    }

    private static string? FindVmState(string output, string name)
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && string.Equals(columns[0], name, StringComparison.OrdinalIgnoreCase))
            {
                return columns[1];
            }
        }

        return null;
    }

    private static string Describe(CliProcessResult result)
    {
        return $"exit={result.ExitCode}, timeout={result.TimedOut}, stdout={result.StdOut.Trim()}, stderr={result.StdErr.Trim()}";
    }
}
