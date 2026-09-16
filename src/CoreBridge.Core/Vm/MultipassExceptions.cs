namespace CoreBridge.Core.Vm;

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
