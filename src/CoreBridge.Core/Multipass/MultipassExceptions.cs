namespace CoreBridge.Core.Multipass;

public class MultipassException : Exception
{
    public MultipassException(string message)
        : base(message)
    {
    }

    public MultipassException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MultipassNotFoundException : MultipassException
{
    public MultipassNotFoundException()
        : base("Multipass-Binary nicht gefunden. Geprüft: Env CORE_BRIDGE_MULTIPASS_EXE, PATH, %ProgramFiles%\\Multipass\\bin\\multipass.exe.")
    {
    }
}

/// <summary>
/// Der Windows-Dienst hat innerhalb des Timeouts den erwarteten Zustand nicht
/// erreicht (siehe <see cref="MultipassService"/>); getrennt von anderen
/// <see cref="MultipassException"/>s, damit ein hängender Stop gezielt
/// abgefangen werden kann.
/// </summary>
public sealed class MultipassServiceTimeoutException : MultipassException
{
    public MultipassServiceTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Dem Benutzer fehlt ein Recht am Dienst oder am Daemon-Prozess (Windows-Fehler 5).
/// Getrennt, damit die CLI erst dann nach einem erhöhten Neustart fragt, wenn es
/// ohne Administratorrechte wirklich nicht geht.
/// </summary>
public sealed class MultipassAccessDeniedException : MultipassException
{
    public MultipassAccessDeniedException(string message)
        : base(message)
    {
    }

    public MultipassAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MultipassTimeoutException : MultipassException
{
    public string Command { get; }

    public int TimeoutMs { get; }

    public string StdOut { get; }

    public string StdErr { get; }

    public MultipassTimeoutException(string command, int timeoutMs, string stdOut, string stdErr)
        : base($"multipass {command} wurde nach {timeoutMs} ms abgebrochen (Timeout).")
    {
        Command = command;
        TimeoutMs = timeoutMs;
        StdOut = stdOut;
        StdErr = stdErr;
    }
}
