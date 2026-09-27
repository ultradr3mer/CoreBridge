using System.Diagnostics;

namespace CoreBridge.Core.Git;

/// <summary>Ergebnis eines git-Aufrufs auf dem Host.</summary>
public sealed record GitRunResult(int ExitCode, string StdOut, string StdErr, long DurationMs);

/// <summary>Ein git-Aufruf auf dem Host lief in den Timeout oder ließ sich nicht starten.</summary>
public sealed class GitException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Führt git auf dem Host aus; <c>GIT_TERMINAL_PROMPT=0</c> verhindert hängende
/// Passwortfragen. Für Tests überschreibbar (<c>virtual</c>).
/// </summary>
public class GitCli
{
    public virtual GitRunResult Run(IReadOnlyList<string> args, string workingDirectory, int timeoutMs = 120_000)
    {
        return Start(
            "git",
            args,
            workingDirectory,
            timeoutMs,
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
    }

    private static GitRunResult Start(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new GitException($"`{fileName}` konnte nicht gestartet werden (installiert und im PATH?).", exception);
        }

        using var process = started ?? throw new GitException($"`{fileName}` konnte nicht gestartet werden.");
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Schon beendet.
            }

            throw new GitException($"`{Path.GetFileNameWithoutExtension(fileName)} {string.Join(' ', args)}` hat nach {timeoutMs / 1000} s nicht geantwortet (Timeout).");
        }

        return new GitRunResult(process.ExitCode, stdOut.GetAwaiter().GetResult(), stdErr.GetAwaiter().GetResult(), stopwatch.ElapsedMilliseconds);
    }
}
