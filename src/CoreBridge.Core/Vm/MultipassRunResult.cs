namespace CoreBridge.Core.Vm;

public sealed record MultipassRunResult(int ExitCode, string StdOut, string StdErr, long DurationMs);
