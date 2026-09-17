using CommandLine;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

public static class Program
{
    private static readonly VmService Vms = new();

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
                typeof(VmPutOptions)
            })
            .MapResult(
                (DoctorOptions _) => Guard(RunDoctor),
                (VmListOptions _) => Guard(RunVmList),
                (VmCreateOptions o) => Guard(() => RunVmCreate(o)),
                (VmStartOptions o) => Guard(() => RunSimpleVmCommand($"start {o.Name}", () => Vms.Start(o.Name))),
                (VmStopOptions o) => Guard(() => RunSimpleVmCommand($"stop {o.Name}", () => Vms.Stop(o.Name))),
                (VmDeleteOptions o) => Guard(() => RunSimpleVmCommand($"delete {o.Name}", () => Vms.Delete(o.Name))),
                (VmExecOptions o) => Guard(() => Report(Vms.Exec(o.Name, o.Command))),
                (VmConnectOptions o) => Guard(() => Vms.Connect(o.Name)),
                (VmPutOptions o) => Guard(() => RunVmPut(o)),
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
        Console.WriteLine($"Lege VM `{options.Name}` an (Image: {options.Image}) ...");
        var result = Vms.Create(options.Name, options.Image);
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
}
