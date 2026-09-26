using System.Diagnostics;

namespace CoreBridge.Core.Multipass;

/// <summary>
/// Decorator, der jeden Aufruf des inneren <see cref="IMultipassCli"/> mit
/// Eingabe und Ausgabe als JSON Line protokolliert — die Grundlage für
/// <see cref="ReplayMultipassCli"/>. Jeder Prozess schreibt eine eigene Datei
/// (<see cref="MultipassCallLog.NewFilePath"/>), beginnend mit einer Kopfzeile.
/// </summary>
public sealed class LoggingMultipassCli : IMultipassCli
{
    private readonly IMultipassCli _inner;
    private readonly object _gate = new();
    private int _seq;

    /// <param name="inner">Der eigentliche Client.</param>
    /// <param name="directory">Bereits aufgelöstes Protokollverzeichnis.</param>
    public LoggingMultipassCli(IMultipassCli inner, string directory)
    {
        _inner = inner;
        LogFile = MultipassCallLog.NewFilePath(directory);
        MultipassCallLog.Append(LogFile, new MultipassCallLogSession(
            MultipassCallLog.SessionKind,
            Environment.ProcessId,
            Environment.CurrentDirectory,
            DateTimeOffset.Now));
    }

    /// <summary>Die Datei, in die dieser Prozess schreibt.</summary>
    public string LogFile { get; }

    /// <summary>Der umhüllte Client.</summary>
    public IMultipassCli Inner => _inner;

    public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null)
    {
        return Record(args, "Run", timeoutMs, () => _inner.Run(args, timeoutMs, exePath));
    }

    public MultipassRunResult RunWithExtensions(
        IReadOnlyList<string> args,
        TimeSpan slice,
        Func<bool> keepWaiting,
        string? exePath = null)
    {
        return Record(
            args,
            "RunWithExtensions",
            (int)slice.TotalMilliseconds,
            () => _inner.RunWithExtensions(args, slice, keepWaiting, exePath));
    }

    public int Attach(IReadOnlyList<string> args, string? exePath = null)
    {
        return Record(
            args,
            "Attach",
            null,
            () => new MultipassRunResult(_inner.Attach(args, exePath), string.Empty, string.Empty, 0)).ExitCode;
    }

    private MultipassRunResult Record(
        IReadOnlyList<string> args,
        string mode,
        int? timeoutMs,
        Func<MultipassRunResult> call)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = call();
            Write(args, mode, timeoutMs, result.ExitCode, result.StdOut, result.StdErr, result.DurationMs, false);
            return result;
        }
        catch (MultipassTimeoutException timeout)
        {
            Write(args, mode, timeout.TimeoutMs, -1, timeout.StdOut, timeout.StdErr, stopwatch.ElapsedMilliseconds, true);
            throw;
        }
    }

    private void Write(
        IReadOnlyList<string> args,
        string mode,
        int? timeoutMs,
        int exitCode,
        string stdOut,
        string stdErr,
        long durationMs,
        bool timedOut)
    {
        lock (_gate)
        {
            var call = args.ToArray();
            MultipassCallLog.Append(LogFile, new MultipassCallLogEntry(
                ++_seq,
                Environment.ProcessId,
                call.Length > 0 ? call[0] : string.Empty,
                call,
                mode,
                timeoutMs,
                exitCode,
                stdOut,
                stdErr,
                durationMs,
                DateTimeOffset.Now,
                timedOut));
        }
    }
}
