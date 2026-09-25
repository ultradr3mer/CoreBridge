using CommandLine;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CoreBridge.Core.Vm;
using Microsoft.Extensions.Configuration;

namespace CoreBridge.Cli;

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
        typeof(ImageBuildOptions),
        typeof(ImageListOptions),
        typeof(ImageDeleteOptions)
    ];

    /// <summary>
    /// Leitsatz 6: CLI und TUI lesen dieselbe Config-Quelle. Reihenfolge:
    /// appsettings.json → appsettings.{Environment}.json (Env DOTNET_ENVIRONMENT,
    /// Default Development) → appsettings.local.json (gitignored) → Env-Variablen
    /// (Doppelter Unterstrich, z. B. Images__Build__VmName). Keine Passwörter
    /// in der Config — Secrets laufen über die CORE_BRIDGE_*_PASSWORD-Env-Kette.
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
    public static int Main(string[] args)
    {
        var config = LoadConfig();
        return Parser.Default
            .ParseArguments(args, RegisteredOptionTypes.ToArray())
            .MapResult(
                (object options) => Run(config, (IOptions)options),
                errors => errors.Any(error => error.Tag is ErrorType.HelpRequestedError
                    or ErrorType.VersionRequestedError
                    or ErrorType.HelpVerbRequestedError)
                    ? 0
                    : 1);
    }

    /// <summary>
    /// Einziger Kommando-Dispatcher nach dem Parsen. Services werden aus dem
    /// übergebenen Config erzeugt; es gibt keine statischen Vm-/Config-Zustände.
    /// </summary>
    public static int Run(AppConfig config, IOptions options)
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

    private static int Dispatch(AppConfig config, VmService vms, IOptions options)
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
            VmConnectOptions o => vms.Connect(o.Name),
            VmPutOptions o => RunVmPut(vms, o),
            VmGetOptions o => RunVmGet(vms, o),
            ImageConfigureOptions o => RunImageConfigure(config, vms, o),
            ImageBuildOptions o => RunImageBuild(config, vms, o),
            ImageListOptions => RunImageList(config, vms),
            ImageDeleteOptions o => RunImageDelete(config, vms, o),
            _ => throw new MultipassException($"Unbekannter CLI-Options-Typ: {options.GetType().Name}")
        };
    }

    private static int Guard(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (MultipassException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (System.Text.Json.JsonException exception)
        {
            Console.Error.WriteLine($"Unerwartete multipass-Antwort (kein JSON): {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"CLI-I/O-Fehler: {exception.Message}");
            return 1;
        }
    }

    private static int Report(MultipassRunResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            Console.WriteLine(result.StdOut.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.StdErr))
        {
            Console.Error.WriteLine(result.StdErr.TrimEnd());
        }

        return result.ExitCode;
    }

    private static int RunMultipassStatus()
    {
        var report = Doctor.Check();
        if (!report.Found)
        {
            Console.WriteLine("Multipass nicht gefunden.");
            Console.WriteLine();
            foreach (var hint in report.Hints)
            {
                Console.WriteLine($"  - {hint}");
            }

            return 1;
        }

        Console.WriteLine($"Binary:  {report.BinaryPath}");
        Console.WriteLine($"CLI:     {report.ClientVersion ?? "unbekannt"}");
        Console.WriteLine($"Daemon:  {report.DaemonVersion ?? "keine Antwort"}");
        if (report.Error is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Hinweis: {report.Error}");
            if (report.RawVersionOutput is not null)
            {
                Console.WriteLine(report.RawVersionOutput.TrimEnd());
            }
        }

        return 0;
    }

    private static int RunVmList(VmService vms)
    {
        var vmsList = vms.List();
        Console.WriteLine($"{"Name",-30} {"State",-10} IPv4");
        foreach (var vm in vmsList)
        {
            Console.WriteLine($"{vm.Name,-30} {vm.State ?? "?",-10} {string.Join(", ", vm.Ipv4)}");
        }

        return 0;
    }

    private static int RunVmCreate(AppConfig config, VmService vms, VmCreateOptions options)
    {
        var requestedImage = string.IsNullOrWhiteSpace(options.Image)
            ? config.Images.DefaultRuntimeImage
            : options.Image.Trim();
        var profile = ImageProfileResolver.Resolve(config.Images, requestedImage, options.CloudInit);
        var launchImage = profile?.RuntimeImage ?? requestedImage;
        var cloudInit = options.CloudInit ?? profile?.CloudInitPath;

        Console.WriteLine(
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
            Console.WriteLine($"Image-Profil: {profile.Name} (Runtime: {profile.RuntimeImage})");
            Console.WriteLine("Gast-Provisionierung: cloud-init");
        }

        Console.WriteLine($"OK — {result.DurationMs / 1000.0:F1} s");
        return 0;
    }

    private static int RunVmStart(VmService vms, VmStartOptions options)
    {
        var existing = vms.List().FirstOrDefault(vm =>
            string.Equals(vm.Name, options.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null
            && !existing.IsDeleted
            && string.Equals(existing.State, "Running", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"start {options.Name}: bereits Running — OK");
            return 0;
        }

        return RunSimpleVmCommand($"start {options.Name}", () => vms.Start(options.Name));
    }

    private static int RunSimpleVmCommand(string label, Func<MultipassRunResult> action)
    {
        var result = action();
        if (result.ExitCode == 0)
        {
            Console.WriteLine($"{label}: OK ({result.DurationMs / 1000.0:F1} s)");
            return 0;
        }

        return Report(result);
    }

    private static int RunVmPut(VmService vms, VmPutOptions options)
    {
        if (!File.Exists(options.Source))
        {
            Console.Error.WriteLine($"Quelldatei fehlt: {options.Source}");
            return 1;
        }

        var result = vms.Put(options.Source, options.Name, options.Destination);
        if (result.ExitCode == 0)
        {
            Console.WriteLine($"transfer: OK ({result.DurationMs / 1000.0:F1} s)");
            return 0;
        }

        return Report(result);
    }

    private static int RunVmGet(VmService vms, VmGetOptions options)
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
            Console.WriteLine($"transfer: OK — {megaBytes:N0} MB ({result.DurationMs / 1000.0:F1} s)");
            if (result.ExitCode != 0)
            {
                Console.WriteLine($"Hinweis: Exit-Code {result.ExitCode} — {result.StdErr.Trim()}");
            }

            return 0;
        }

        return Report(result);
    }

    private static int RunImageConfigure(AppConfig config, VmService vms, ImageConfigureOptions options)
    {
        var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var images = new ImageService(config.Images, vms);
        var result = images.Configure(
            options.Name,
            components,
            options.ModelUrl,
            options.BuildTo,
            options.Force,
            options.RuntimeImage);

        Console.WriteLine($"Image '{result.Name}' konfiguriert.");
        Console.WriteLine($"  Modell:       {result.ModelPath} (Canonical dangerous, unverändert signiert)");
        Console.WriteLine($"  Config:       {result.ConfigPath}");
        Console.WriteLine($"  Runtime-Image: {result.RuntimeImage}");
        Console.WriteLine($"  Snaps:        {(result.Snaps.Length == 0 ? "keine" : string.Join(", ", result.Snaps))}");
        if (result.CloudInitPath is null)
        {
            Console.WriteLine("  cloud-init: nicht nötig (keine Gast-Provisionierung).");
        }
        else
        {
            Console.WriteLine($"  cloud-init: {result.CloudInitPath}");
        }

        Console.WriteLine();
        Console.WriteLine($"VM starten: cb vm-create --name <vm> --image {result.Name}");
        return 0;
    }

    private static int RunImageList(AppConfig config, VmService vms)
    {
        var images = new ImageService(config.Images, vms);
        var entries = images.List();
        if (entries.Count == 0)
        {
            Console.WriteLine("Keine konfigurierten Images.");
            return 0;
        }

        Console.WriteLine($"{"Name",-20} {"Komponenten",-28} Image");
        foreach (var entry in entries)
        {
            Console.WriteLine(
                $"{entry.Name,-20} {string.Join(",", entry.Components),-28} {(entry.HasImage ? $"{entry.ImageBytes / (1024.0 * 1024.0):N0} MB" : "-")}");
        }

        return 0;
    }

    private static int RunImageDelete(AppConfig config, VmService vms, ImageDeleteOptions options)
    {
        var images = new ImageService(config.Images, vms);
        var result = images.Delete(options.Name);
        if (result.ModelDir is not null)
        {
            Console.WriteLine($"Konfiguration gelöscht: {result.ModelDir}");
        }

        if (result.ImageFile is not null)
        {
            Console.WriteLine($"Image gelöscht:       {result.ImageFile}");
        }

        return 0;
    }

    private static int RunImageBuild(AppConfig config, VmService vms, ImageBuildOptions options)
    {
        var images = new ImageService(config.Images, vms);
        var buildVm = options.BuildVmName ?? config.Images.Build.VmName;
        Console.WriteLine($"Baue Image '{options.Name}' in Bau-VM '{buildVm}' (ubuntu-image + --snap) ...");
        var result = images.Build(
            options.Name,
            options.BuildVmName,
            options.BuildImage,
            options.Disk,
            options.TimeoutSeconds,
            options.Destination);

        var megaBytes = new FileInfo(result.ImagePath).Length / (1024.0 * 1024.0);
        Console.WriteLine($"OK — {result.ImagePath} ({megaBytes:N0} MB, {result.DurationMs / 1000.0:F1} s)");
        Console.WriteLine("Hinweis: Import via `multipass launch <url>` — unter Windows NICHT via file:// (multipass 1.16.3 Bug), sondern https:// (siehe EXPERIMENT_LOG T11).");
        return 0;
    }
}
