namespace CoreBridge.Core.Multipass;

public sealed record MultipassRunResult(int ExitCode, string StdOut, string StdErr, long DurationMs);
