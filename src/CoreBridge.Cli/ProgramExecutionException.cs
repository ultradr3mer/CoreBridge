namespace CoreBridge.Cli;

/// <summary>
/// Signals that a command completed unsuccessfully after producing its normal
/// output. The exception is thrown from the execution stream so callers can
/// handle it at the application boundary instead of receiving an error string
/// as if it were a normal message.
/// </summary>
public sealed class ProgramExecutionException : Exception
{
    public ProgramExecutionException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    /// <summary>The process or command exit code represented by this failure.</summary>
    public int ExitCode { get; }
}
