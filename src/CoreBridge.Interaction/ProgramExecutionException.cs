namespace CoreBridge.Interaction;

/// <summary>
/// Einziger Fehlertyp, den <see cref="Program.Run"/> für erwartete Fehler wirft.
/// <see cref="Exception.Message"/> ist bereits die fertige Meldung für den
/// Benutzer und <see cref="ExitCode"/> der Exit-Code des Prozesses — der
/// Aufrufer gibt beides nur noch aus. Die ursprüngliche Ursache bleibt als
/// <see cref="Exception.InnerException"/> erhalten.
/// </summary>
public sealed class ProgramExecutionException : Exception
{
    public ProgramExecutionException(int exitCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
    }

    /// <summary>Exit-Code des Prozesses für diesen Fehler.</summary>
    public int ExitCode { get; }
}
