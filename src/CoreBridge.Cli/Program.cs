using System.Runtime.CompilerServices;
using System.Text;
using CommandLine;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;
using Microsoft.Extensions.Configuration;

namespace CoreBridge.Cli;

/// <summary>
/// Native asynchronous result of a command dispatched through
/// <see cref="Program.Run"/>. Enumeration yields output lines without touching
/// the application's console streams.
/// </summary>
public sealed class ProgramExecution : IAsyncEnumerable<string>
{
    private readonly string[] lines;

    public ProgramExecution(int exitCode, string output = "", string error = "")
    {
        ExitCode = exitCode;
        Error = error;
        lines = string.IsNullOrEmpty(output)
            ? []
            : output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }

    public int ExitCode { get; }

    public string Error { get; }

    public bool Success => ExitCode == 0;

    public static ProgramExecution Ok(string output = "", string error = "") => new(0, output, error);

    public static ProgramExecution Failure(int exitCode, string output = "", string error = "") =>
        new(exitCode, output, error);

    public IAsyncEnumerator<string> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        return Enumerate(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    private async IAsyncEnumerable<string> Enumerate(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
            await Task.Yield();
        }
    }
}

public static class Program
{
    /// <summary>
    /// Einzige vom Parser verwendete Optionsliste. Tests können über dieses
    /// öffentliche Register sicherstellen, dass kein Verb die Dispatch-Grenze
    /// umgeht.
    /// </summary>
    public static IReadOnlyList<Type> RegisteredOptionTypes { get; } =
    [
        typeof(MultipassStatusOptions),
        typeof(VmListOptions),
        typeof(VmCreateOptions),
        typeof(VmStartOptions),
        typeof(VmStopOptions),
        typeof(VmDeleteOptions),
        typeof(VmExecOptions),
        typeof(VmConnectOptions),
        typeof(VmPutOptions),
        typeof(VmGetOptions),
        typeof(ImageConfigureOptions),
        typeof(ImageListOptions),
        typeof(ImageDeleteOptions)
    ];

    /// <summary>
    /// Leitsatz 6: CLI und TUI lesen dieselbe Config-Quelle. Reihenfolge:
    /// appsettings.json → appsettings.{Environment}.json (Env DOTNET_ENVIRONMENT,
    /// Default Development) → appsettings.local.json (gitignored) → Env-Variablen
    /// (Doppelter Unterstrich, z. B. Images__StoreRoot). Keine Passwörter in der
    /// Config — Secrets laufen über die CORE_BRIDGE_*_PASSWORD-Env-Kette.
    /// </summary>
    private static AppConfig LoadConfig()
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.Get<AppConfig>()
            ?? throw new InvalidOperationException("Konfiguration konnte nicht gebunden werden (appsettings.json).");
    }

    /// <summary>
    /// Parst nur die CLI und führt danach genau einen Dispatcher aus. Parsefehler
    /// und Hilfe-/Versionsfehler werden hier auf Exit-Codes abgebildet.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var config = LoadConfig();
        var result = Parser.Default
            .ParseArguments(args, RegisteredOptionTypes.ToArray())
            .MapResult(
                (object options) => Run(config, (IOptions)options),
                errors => errors.Any(error => error.Tag is ErrorType.HelpRequestedError
                    or ErrorType.VersionRequestedError
                    or ErrorType.HelpVerbRequestedError)
                    ? ProgramExecution.Ok()
                    : ProgramExecution.Failure(1));

        await foreach (var line in result)
        {
            Console.WriteLine(line);
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            Console.Error.WriteLine(result.Error.TrimEnd());
        }

        return result.ExitCode;
    }

    /// <summary>
    /// Einziger Kommando-Dispatcher nach dem Parsen. Services werden aus dem
    /// übergebenen Config erzeugt; es gibt keine statischen Vm-/Config-Zustände.
    /// </summary>
    public static ProgramExecution Run(AppConfig config, IOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        return Guard(() =>
        {
            ImageStore.Initialize(config.Images.StoreRoot);
            var vms = new VmService();
            return Dispatch(config, vms, options);
        });
    }

    private static ProgramExecution Dispatch(AppConfig config, VmService vms, IOptions options)
    {
        return options switch
        {
            MultipassStatusOptions => RunMultipassStatus(),
            VmListOptions => RunVmList(vms),
            VmCreateOptions o => RunVmCreate(config, vms, o),
            VmStartOptions o => RunVmStart(vms, o),
            VmStopOptions o => RunSimpleVmCommand($"stop {o.Name}", () => vms.Stop(o.Name)),
            VmDeleteOptions o => RunSimpleVmCommand(
                $"delete {o.Name}{(o.Purge ? " --purge" : string.Empty)}",
                () => vms.Delete(o.Name, o.Purge)),
            VmExecOptions o => Report(vms.Exec(o.Name, o.Command, o.TimeoutSeconds * 1000)),
            VmConnectOptions o => RunVmConnect(vms, o),
            VmPutOptions o => RunVmPut(vms, o),
            VmGetOptions o => RunVmGet(vms, o),
            ImageConfigureOptions o => RunImageConfigure(config, o),
            ImageListOptions => RunImageList(config),
            ImageDeleteOptions o => RunImageDelete(config, o),
            _ => throw new MultipassException($"Unbekannter CLI-Options-Typ: {options.GetType().Name}")
        };
    }

    private static ProgramExecution Guard(Func<ProgramExecution> action)
    {
        try
        {
            return action();
        }
        catch (MultipassException exception)
        {
            return ProgramExecution.Failure(1, error: exception.Message);
        }
        catch (System.Text.Json.JsonException exception)
        {
            return ProgramExecution.Failure(
                1,
                error: $"Unerwartete Multipass-Antwort (kein JSON): {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ProgramExecution.Failure(1, error: $"CLI-I/O-Fehler: {exception.Message}");
        }
    }

    private static ProgramExecution Report(MultipassRunResult result)
    {
        return new ProgramExecution(result.ExitCode, result.StdOut, result.StdErr);
    }

    private static ProgramExecution RunMultipassStatus()
    {
        var report = Doctor.Check();
        if (!report.Found)
        {
            var output = new StringBuilder("Multipass nicht gefunden.\n\n");
            foreach (var hint in report.Hints)
            {
                output.AppendLine($"  - {hint}");
            }

            return ProgramExecution.Failure(1, output.ToString());
        }

        var lines = new StringBuilder()
            .AppendLine($"Binary:  {report.BinaryPath}")
            .AppendLine($"CLI:     {report.ClientVersion ?? "unbekannt"}")
            .AppendLine($"Daemon:  {report.DaemonVersion ?? "keine Antwort"}");
        var error = string.Empty;
        if (report.Error is not null)
        {
            lines.AppendLine()
                .AppendLine($"Hinweis: {report.Error}");
            if (report.RawVersionOutput is not null)
            {
                error = report.RawVersionOutput.TrimEnd();
            }
        }

        return ProgramExecution.Ok(lines.ToString(), error);
    }

    private static ProgramExecution RunVmList(VmService vms)
    {
        var lines = new StringBuilder()
            .AppendLine($"{"Name",-30} {"State",-10} IPv4");
        foreach (var vm in vms.List())
        {
            lines.AppendLine($"{vm.Name,-30} {vm.State ?? "?",-10} {string.Join(", ", vm.Ipv4)}");
        }

        return ProgramExecution.Ok(lines.ToString());
    }

    private static ProgramExecution RunVmCreate(AppConfig config, VmService vms, VmCreateOptions options)
    {
        var requestedImage = string.IsNullOrWhiteSpace(options.Image)
            ? config.Images.DefaultRuntimeImage
            : options.Image.Trim();
        var profile = ImageProfileResolver.Resolve(config.Images, requestedImage, options.CloudInit);
        var launchImage = profile?.RuntimeImage ?? requestedImage;
        var cloudInit = options.CloudInit ?? profile?.CloudInitPath;

        var lines = new StringBuilder()
            .AppendLine(
                $"Lege VM `{options.Name}` an (Image: {requestedImage}"
                + (profile is null ? string.Empty : $" → Runtime: {launchImage}")
                + $"{(options.Disk is null ? string.Empty : $", Disk: {options.Disk}")}"
                + $"{(cloudInit is null ? string.Empty : ", cloud-init")}) ...");
        var result = vms.Create(options.Name, launchImage, options.Disk, cloudInitPath: cloudInit);
        if (result.ExitCode != 0)
        {
            return Report(result);
        }

        if (profile is not null)
        {
            lines.AppendLine($"Image-Profil: {profile.Name} (Runtime: {profile.RuntimeImage})")
                .AppendLine("Gast-Provisionierung: cloud-init");
        }

        lines.Append($"OK — {result.DurationMs / 1000.0:F1} s");
        return ProgramExecution.Ok(lines.ToString());
    }

    private static ProgramExecution RunVmConnect(VmService vms, VmConnectOptions options)
    {
        var exitCode = vms.Connect(options.Name);
        return exitCode == 0
            ? ProgramExecution.Ok()
            : ProgramExecution.Failure(exitCode, error: $"vm-connect fehlgeschlagen (Exit {exitCode}).");
    }

    private static ProgramExecution RunVmStart(VmService vms, VmStartOptions options)
    {
        var existing = vms.List().FirstOrDefault(vm =>
            string.Equals(vm.Name, options.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null
            && !existing.IsDeleted
            && string.Equals(existing.State, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return ProgramExecution.Ok($"start {options.Name}: bereits Running — OK");
        }

        return RunSimpleVmCommand($"start {options.Name}", () => vms.Start(options.Name));
    }

    private static ProgramExecution RunSimpleVmCommand(string label, Func<MultipassRunResult> action)
    {
        var result = action();
        if (result.ExitCode == 0)
        {
            return ProgramExecution.Ok($"{label}: OK ({result.DurationMs / 1000.0:F1} s)");
        }

        return Report(result);
    }

    private static ProgramExecution RunVmPut(VmService vms, VmPutOptions options)
    {
        if (!File.Exists(options.Source))
        {
            return ProgramExecution.Failure(1, error: $"Quelldatei fehlt: {options.Source}");
        }

        var result = vms.Put(options.Source, options.Name, options.Destination);
        if (result.ExitCode == 0)
        {
            return ProgramExecution.Ok($"transfer: OK ({result.DurationMs / 1000.0:F1} s)");
        }

        return Report(result);
    }

    private static ProgramExecution RunVmGet(VmService vms, VmGetOptions options)
    {
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Destination));
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var result = vms.Get(options.Name, options.Remote, options.Destination);
        if (File.Exists(options.Destination))
        {
            var megaBytes = new FileInfo(options.Destination).Length / (1024.0 * 1024.0);
            var error = result.ExitCode == 0
                ? string.Empty
                : $"Hinweis: Exit-Code {result.ExitCode} — {result.StdErr.Trim()}";
            return ProgramExecution.Ok($"transfer: OK — {megaBytes:N0} MB ({result.DurationMs / 1000.0:F1} s)", error);
        }

        return Report(result);
    }

    private static ProgramExecution RunImageConfigure(AppConfig config, ImageConfigureOptions options)
    {
        var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var images = new ImageService(config.Images);
        var result = images.Configure(
            options.Name,
            components,
            options.ModelUrl,
            force: options.Force,
            runtimeImage: options.RuntimeImage);

        var lines = new StringBuilder()
            .AppendLine($"Image '{result.Name}' konfiguriert.")
            .AppendLine($"  Modell:       {result.ModelPath} (Canonical dangerous, unverändert signiert)")
            .AppendLine($"  Config:       {result.ConfigPath}")
            .AppendLine($"  Runtime-Image: {result.RuntimeImage}")
            .AppendLine($"  Snaps:        {(result.Snaps.Length == 0 ? "keine" : string.Join(", ", result.Snaps))}");
        if (result.CloudInitPath is null)
        {
            lines.AppendLine("  cloud-init: nicht nötig (keine Gast-Provisionierung).");
        }
        else
        {
            lines.AppendLine($"  cloud-init: {result.CloudInitPath}");
        }

        lines.AppendLine()
            .Append($"VM starten: cb vm-create --name <vm> --image {result.Name}");
        return ProgramExecution.Ok(lines.ToString());
    }

    private static ProgramExecution RunImageList(AppConfig config)
    {
        var entries = new ImageService(config.Images).List();
        if (entries.Count == 0)
        {
            return ProgramExecution.Ok("Keine konfigurierten Classic-Profile.");
        }

        var lines = new StringBuilder()
            .AppendLine($"{"Name",-20} {"Komponenten",-28} Snaps");
        foreach (var entry in entries)
        {
            lines.AppendLine(
                $"{entry.Name,-20} {string.Join(",", entry.Components),-28} {string.Join(",", entry.Snaps)}");
        }

        return ProgramExecution.Ok(lines.ToString());
    }

    private static ProgramExecution RunImageDelete(AppConfig config, ImageDeleteOptions options)
    {
        var result = new ImageService(config.Images).Delete(options.Name);
        return ProgramExecution.Ok($"Konfiguration gelöscht: {result.ModelDir}");
    }
}
