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

    public MultipassRunResult Create(string name, string image, int timeoutMs = 2_400_000)
    {
        return _cli.Run(new[] { "launch", image, "--name", name }, timeoutMs);
    }

    public MultipassRunResult Start(string name)
    {
        return _cli.Run(new[] { "start", name }, 180_000);
    }

    public MultipassRunResult Stop(string name)
    {
        return _cli.Run(new[] { "stop", name }, 180_000);
    }

    public MultipassRunResult Delete(string name)
    {
        return _cli.Run(new[] { "delete", name }, 180_000);
    }

    public MultipassRunResult Exec(string name, string command, int timeoutMs = 600_000)
    {
        return _cli.Run(new[] { "exec", name, "--", "/bin/sh", "-c", command }, timeoutMs);
    }

    public int Connect(string name)
    {
        return _cli.Attach(new[] { "shell", name });
    }
}
