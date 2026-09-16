using System.Diagnostics;

namespace CoreBridge.Core.Vm;

public class MultipassCli
{
    private readonly Func<string?> _locate;

    public MultipassCli()
        : this(() => MultipassBinaryLocator.LocateOrNull())
    {
    }

    public MultipassCli(Func<string?> locate)
    {
        _locate = locate;
    }

    public virtual MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30_000, string? exePath = null)
    {
        var binary = exePath ?? _locate() ?? throw new MultipassNotFoundException();
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var stopwatch = Stopwatch.StartNew();
        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MultipassException($"Prozess konnte nicht gestartet werden: {binary}", exception);
        }

        using var process = started
            ?? throw new MultipassException($"Prozess konnte nicht gestartet werden: {binary}");
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            TryKill(process);
            throw new MultipassTimeoutException(
                string.Join(' ', args),
                timeoutMs,
                stdOutTask.GetAwaiter().GetResult(),
                stdErrTask.GetAwaiter().GetResult());
        }

        return new MultipassRunResult(
            process.ExitCode,
            stdOutTask.GetAwaiter().GetResult(),
            stdErrTask.GetAwaiter().GetResult(),
            stopwatch.ElapsedMilliseconds);
    }

    public virtual int Attach(IReadOnlyList<string> args, string? exePath = null)
    {
        var binary = exePath ?? _locate() ?? throw new MultipassNotFoundException();
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MultipassException($"Prozess konnte nicht gestartet werden: {binary}", exception);
        }

        using var process = started
            ?? throw new MultipassException($"Prozess konnte nicht gestartet werden: {binary}");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
