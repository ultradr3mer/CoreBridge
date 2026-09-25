namespace CoreBridge.Core.Vm;

public sealed class VmService
{
    private readonly MultipassCli _cli;

    public VmService(MultipassCli? cli = null)
    {
        _cli = cli ?? new MultipassCli();
    }

    public IReadOnlyList<VmInfo> List()
    {
        var result = _cli.Run(new[] { "list", "--format", "json" }, 60_000);
        if (result.ExitCode != 0)
        {
            var message = MultipassStdErr.Clean(result.StdErr);
            throw new MultipassException(string.IsNullOrEmpty(message)
                ? $"multipass list beendet mit Exit {result.ExitCode}."
                : $"{message} (multipass Exit {result.ExitCode})");
        }

        return MultipassVmList.Parse(result.StdOut);
    }

    public MultipassRunResult Create(string name, string image, string? disk = null, int timeoutMs = 2_400_000, string? cloudInitPath = null)
    {
        // multipass hat ein eigenes Launch-Timeout (Default 300 s, inkl. cloud-init) —
        // ohne --timeout bricht es lange vor unserem Prozess-Timeout ab.
        var args = new List<string> { "launch", image, "--name", name, "--timeout", (timeoutMs / 1000).ToString() };
        if (disk is not null)
        {
            args.Add("--disk");
            args.Add(disk);
        }

        if (cloudInitPath is not null)
        {
            args.Add("--cloud-init");
            args.Add(cloudInitPath);
        }

        return _cli.Run(args, timeoutMs);
    }

    public MultipassRunResult Start(string name)
    {
        return _cli.Run(new[] { "start", name }, 180_000);
    }

    public MultipassRunResult Stop(string name)
    {
        return _cli.Run(new[] { "stop", name }, 180_000);
    }

    public MultipassRunResult Delete(string name, bool purge = false)
    {
        var args = new List<string> { "delete", name };
        if (purge)
        {
            args.Add("--purge");
        }

        return _cli.Run(args, 180_000);
    }

    public MultipassRunResult Exec(string name, string command, int timeoutMs = 600_000)
    {
        return _cli.Run(new[] { "exec", name, "--", "/bin/sh", "-c", command }, timeoutMs);
    }

    public MultipassRunResult Put(string localPath, string name, string? remotePath = null, int timeoutMs = 600_000)
    {
        var target = remotePath is null ? Path.GetFileName(localPath) : remotePath;
        return _cli.Run(new[] { "transfer", localPath, $"{name}:{target}" }, timeoutMs);
    }

    public MultipassRunResult Get(string name, string remotePath, string localPath, int timeoutMs = 600_000)
    {
        return _cli.Run(new[] { "transfer", $"{name}:{remotePath}", localPath }, timeoutMs);
    }

    public int Connect(string name)
    {
        return _cli.Attach(new[] { "shell", name });
    }

    /// <summary>
    /// Testbare Konsolen-Session auf der VM: Befehle senden, Ausgaben sammeln
    /// und prüfen. Läuft über den exec-Kanal (pro Befehl ein exec) — gepipetes
    /// stdin forwardet der Multipass-Windows-Client nicht, `multipass shell`
    /// ist ohne TTY unbrauchbar. Optionaler log-Callback (z. B.
    /// ITestOutputHelper.WriteLine im Test) schreibt die Ausgabe live mit.
    /// </summary>
    public VmExecSession OpenConsole(string name, Action<string>? log = null)
    {
        return new VmExecSession(name, log);
    }

    /// <summary>
    /// Echte interaktive Shell über natives SSH (SSH.NET, PTY): gemeinsamer
    /// Shell-Status, Prompt-Ausgabe, vm-connect in testbarer Form. Benötigt
    /// SSH-Key auf der VM (cloud-init ssh_authorized_keys) — Verbindungsaufbau
    /// nur Host→VM (Plan §6).
    /// </summary>
    public VmSshSession OpenSshSession(string name, Action<string>? log = null, string? privateKeyPath = null)
    {
        return VmSshSession.Open(name, log, privateKeyPath);
    }
}
