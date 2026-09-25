namespace CoreBridge.Cli.Tests;

internal sealed record CliProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    long DurationMs,
    bool TimedOut,
    string CommandLine);
