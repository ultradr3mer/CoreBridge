using CommandLine;
using CoreBridge.Core.Config;
using CoreBridge.Core.Vm;
using Microsoft.Extensions.Configuration;

namespace CoreBridge.Cli;

public static class Program
{
    private static readonly VmService Vms = new();
    private static readonly AppConfig Config = LoadConfig();

    /// <summary>
    /// Leitsatz 6: CLI und TUI lesen dieselbe Config-Quelle. Reihenfolge:
    /// appsettings.json → appsettings.local.json (gitignored) → Env-Variablen
    /// (Doppelter Unterstrich, z. B. Images__Build__VmName). Keine Passwörter
    /// in der Config — Secrets laufen über die CORE_BRIDGE_*_PASSWORD-Env-Kette.
    /// </summary>
    private static AppConfig LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return configuration.Get<AppConfig>()
            ?? throw new InvalidOperationException("Konfiguration konnte nicht gebunden werden (appsettings.json).");
    }

    public static int Main(string[] args)
    {
        return Parser.Default
            .ParseArguments(args, new[]
            {
                typeof(DoctorOptions),
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
                typeof(ImageBuildOptions)
            })
            .MapResult(
                (DoctorOptions _) => Guard(RunDoctor),
                (VmListOptions _) => Guard(RunVmList),
                (VmCreateOptions o) => Guard(() => RunVmCreate(o)),                (VmStartOptions o) => Guard(() => RunSimpleVmCommand($"start {o.Name}", () => Vms.Start(o.Name))),
                (VmStopOptions o) => Guard(() => RunSimpleVmCommand($"stop {o.Name}", () => Vms.Stop(o.Name))),
                (VmDeleteOptions o) => Guard(() => RunSimpleVmCommand($"delete {o.Name}", () => Vms.Delete(o.Name))),
                (VmExecOptions o) => Guard(() => Report(Vms.Exec(o.Name, o.Command, o.TimeoutSeconds * 1000))),
                (VmConnectOptions o) => Guard(() => Vms.Connect(o.Name)),
                (VmPutOptions o) => Guard(() => RunVmPut(o)),
                (VmGetOptions o) => Guard(() => RunVmGet(o)),
                (ImageConfigureOptions o) => Guard(() => RunImageConfigure(o)),
                (ImageBuildOptions o) => Guard(() => RunImageBuild(o)),
                errs => errs.Any(e => e.Tag is ErrorType.HelpRequestedError
                    or ErrorType.VersionRequestedError
                    or ErrorType.HelpVerbRequestedError)
                    ? 0
                    : 1);
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

    private static int RunDoctor()
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

    private static int RunVmList()
    {
        var vms = Vms.List();
        Console.WriteLine($"{"Name",-30} {"State",-10} IPv4");
        foreach (var vm in vms)
        {
            Console.WriteLine($"{vm.Name,-30} {vm.State ?? "?",-10} {string.Join(", ", vm.Ipv4)}");
        }

        return 0;
    }

    private static int RunVmCreate(VmCreateOptions options)
    {
        Console.WriteLine(
            $"Lege VM `{options.Name}` an (Image: {options.Image}"
            + $"{(options.Disk is null ? string.Empty : $", Disk: {options.Disk}")}"
            + $"{(options.CloudInit is null ? string.Empty : ", cloud-init")}) ...");
        var result = Vms.Create(options.Name, options.Image, options.Disk, cloudInitPath: options.CloudInit);
        if (result.ExitCode == 0)
        {
            Console.WriteLine($"OK — {result.DurationMs / 1000.0:F1} s");
            return 0;
        }

        return Report(result);
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

    private static int RunVmPut(VmPutOptions options)
    {
        if (!File.Exists(options.Source))
        {
            Console.Error.WriteLine($"Quelldatei fehlt: {options.Source}");
            return 1;
        }

        var result = Vms.Put(options.Source, options.Name, options.Destination);
        if (result.ExitCode == 0)
        {
            Console.WriteLine($"transfer: OK ({result.DurationMs / 1000.0:F1} s)");
            return 0;
        }

        return Report(result);
    }

    private static int RunVmGet(VmGetOptions options)
    {
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(options.Destination));
        if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var result = Vms.Get(options.Name, options.Remote, options.Destination);
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

    private static int RunImageConfigure(ImageConfigureOptions options)
    {
        var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var images = new CoreBridge.Core.Images.ImageService(Config.Images);
        var result = images.Configure(options.Name, components, options.ModelUrl);

        Console.WriteLine($"Image '{result.Name}' konfiguriert — Snaps im Image: {string.Join(", ", result.Snaps)}");
        Console.WriteLine($"  Modell:   {result.ModelPath} (Canonical dangerous, unverändert signiert)");
        Console.WriteLine($"  Config:   {result.ConfigPath}");
        if (result.CloudInitPath is null)
        {
            Console.WriteLine("  cloud-init: nicht nötig (alle Komponenten reine Snaps).");
        }
        else
        {
            Console.WriteLine($"  cloud-init: {result.CloudInitPath} (Symlinks/.profile/vendored libs)");
            Console.WriteLine($"    -> beim Launch mitgeben: cb vm-create --name <vm> --image <img> --cloud-init \"{result.CloudInitPath}\"");
        }

        Console.WriteLine();
        Console.WriteLine($"Naechster Schritt: cb image-build --name {result.Name}");
        return 0;
    }

    private static int RunImageBuild(ImageBuildOptions options)
    {
        var images = new CoreBridge.Core.Images.ImageService(Config.Images);
        var buildVm = options.BuildVmName ?? Config.Images.Build.VmName;
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
