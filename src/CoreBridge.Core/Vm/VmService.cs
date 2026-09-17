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
        return MultipassVmList.Parse(result.StdOut);
    }

    public MultipassRunResult Create(string name, string image, string? disk = null, int timeoutMs = 2_400_000, string? cloudInitPath = null)
    {
        var args = new List<string> { "launch", image, "--name", name };
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
    /// Testbare Konsolen-Session (multipass shell mit Pipes): Befehle senden,
    /// Ausgaben prüfen. Für echtes interaktives Terminal weiterhin <c>Connect</c>.
    /// </summary>
    public VmConsoleSession OpenConsole(string name)
    {
        return VmConsoleSession.Open(name);
    }
}
