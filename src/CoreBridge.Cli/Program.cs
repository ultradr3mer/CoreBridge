using CommandLine;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

[Verb("doctor", HelpText = "Prüft, ob Multipass auffindbar ist, und gibt CLI- und Daemon-Version aus.")]
public sealed class DoctorOptions
{
}

public static class Program
{
    public static int Main(string[] args)
    {
        return Parser.Default
            .ParseArguments(args, new[] { typeof(DoctorOptions) })
            .MapResult(
                (DoctorOptions _) => RunDoctor(),
                errs => errs.Any(e => e.Tag is ErrorType.HelpRequestedError
                    or ErrorType.VersionRequestedError
                    or ErrorType.HelpVerbRequestedError)
                    ? 0
                    : 1);
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
}
