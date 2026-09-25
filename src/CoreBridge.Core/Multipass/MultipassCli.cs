using System.Diagnostics;

namespace CoreBridge.Core.Multipass;

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
        return RunCore(args, TimeSpan.FromMilliseconds(timeoutMs), () => false, exePath);
    }

    /// <summary>
    /// Wie <see cref="Run"/>, wartet aber in Abschnitten von <paramref name="slice"/>:
    /// Ist der Prozess danach noch nicht fertig, entscheidet <paramref name="keepWaiting"/>,
    /// ob ein weiterer Abschnitt gewartet wird. Bei <c>false</c> wird der Prozess
    /// beendet und <see cref="MultipassTimeoutException"/> geworfen.
    /// </summary>
    public virtual MultipassRunResult RunWithExtensions(
        IReadOnlyList<string> args,
        TimeSpan slice,
        Func<bool> keepWaiting,
        string? exePath = null)
    {
        return RunCore(args, slice, keepWaiting, exePath);
    }

    private MultipassRunResult RunCore(IReadOnlyList<string> args, TimeSpan slice, Func<bool> keepWaiting, string? exePath)
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

        var slices = 0;
        while (!process.WaitForExit(slice))
        {
            slices++;
            if (keepWaiting())
            {
                continue;
            }

            TryKill(process);
            throw new MultipassTimeoutException(
                string.Join(' ', args),
                (int)(slice.TotalMilliseconds * slices),
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
