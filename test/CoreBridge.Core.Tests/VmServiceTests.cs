using System.Text;
using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class VmServiceTests
{
    [Fact]
    public void ListRuftJsonFormatAufUndParst()
    {
        var cli = new FakeMultipassCli(_ => new MultipassRunResult(
            0,
            """{ "list": [ { "name": "core26test", "state": "Running", "ipv4": [ "172.30.85.131" ] } ] }""",
            string.Empty,
            5));
        var service = new VmService(cli);

        var vms = service.List();

        Assert.Equal("list", cli.Calls[0][0]);
        Assert.Contains("--format", cli.Calls[0]);
        Assert.Contains("json", cli.Calls[0]);
        Assert.Single(vms);
        Assert.Equal("core26test", vms[0].Name);
    }

    [Fact]
    public void CreateLanciertImageMitNamen()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Create("cbx-test", "core26");

        Assert.Equal(new[] { "launch", "core26", "--name", "cbx-test", "--timeout", "2400" }, cli.Calls[0]);
    }

    [Fact]
    public void CreateMitDiskUebergibtOption()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Create("cbx-test", "26.04", "20G");

        Assert.Equal(new[] { "launch", "26.04", "--name", "cbx-test", "--timeout", "2400", "--disk", "20G" }, cli.Calls[0]);
    }

    [Fact]
    public void StartStopDeleteVerwendenNamen()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Start("cbx-test");
        service.Stop("cbx-test");
        service.Delete("cbx-test");

        Assert.Equal(new[] { "start", "cbx-test" }, cli.Calls[0]);
        Assert.Equal(new[] { "stop", "cbx-test" }, cli.Calls[1]);
        Assert.Equal(new[] { "delete", "cbx-test" }, cli.Calls[2]);
    }

    [Fact]
    public void StartMitKeepWaitingWartetInAbschnitten()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Start("cbx-test", 5_000, () => true);
        service.Stop("cbx-test", 5_000);

        Assert.Equal(["start:5000:extensions", "stop:5000:fixed"], cli.Modes);
    }

    [Fact]
    public void FindLiefertVmOderNull()
    {
        var cli = new FakeMultipassCli(_ => new MultipassRunResult(
            0,
            """{ "list": [ { "name": "cbx-a", "state": "Suspended", "ipv4": [] }, { "name": "cbx-b", "state": "Deleted", "ipv4": [] } ] }""",
            string.Empty,
            5));
        var service = new VmService(cli);

        var a = service.Find("CBX-A");
        var b = service.Find("cbx-b");

        Assert.NotNull(a);
        Assert.True(a.IsStopped);
        Assert.False(a.IsRunning);
        Assert.NotNull(b);
        Assert.True(b.IsDeleted);
        Assert.Null(service.Find("cbx-c"));
    }

    [Fact]
    public void ExecLaeuftUeberShC()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Exec("cbx-test", "uname -a");

        Assert.Equal(new[] { "exec", "cbx-test", "--", "/bin/sh", "-c", "uname -a" }, cli.Calls[0]);
    }

    [Fact]
    public void ConnectAttachtInteraktiveShell()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Connect("cbx-test");

        Assert.Equal(new[] { "shell", "cbx-test" }, cli.Calls[0]);
    }

    [Fact]
    public void PutTransferiertMitDefaultZielpfad()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Put(@"C:\temp\datei.txt", "cbx-test");

        Assert.Equal(new[] { "transfer", @"C:\temp\datei.txt", "cbx-test:datei.txt" }, cli.Calls[0]);
    }

    [Fact]
    public void PutTransferiertMitExplizitemZielpfad()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Put(@"C:\temp\datei.txt", "cbx-test", ".local/bin/tool");

        Assert.Equal(new[] { "transfer", @"C:\temp\datei.txt", "cbx-test:.local/bin/tool" }, cli.Calls[0]);
    }

    [Fact]
    public void GetTransferiertVonVmAufHost()
    {
        var cli = new FakeMultipassCli(EmptyResult);
        var service = new VmService(cli);

        service.Get("cbx-test", "/home/ubuntu/x.img", @"C:\images\x.img");

        Assert.Equal(new[] { "transfer", "cbx-test:/home/ubuntu/x.img", @"C:\images\x.img" }, cli.Calls[0]);
    }

    private static MultipassRunResult EmptyResult(string[] _) => new(0, string.Empty, string.Empty, 1);

    private sealed class FakeMultipassCli : MultipassCli
    {
        public List<string[]> Calls { get; } = new();

        /// <summary>Je Aufruf <c>&lt;verb&gt;:&lt;timeoutMs&gt;:fixed|extensions</c>.</summary>
        public List<string> Modes { get; } = new();

        private readonly Func<string[], MultipassRunResult> _respond;

        public FakeMultipassCli(Func<string[], MultipassRunResult> respond)
            : base(() => null)
        {
            _respond = respond;
        }

        public override MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30_000, string? exePath = null)
        {
            var call = args.ToArray();
            Calls.Add(call);
            Modes.Add($"{call[0]}:{timeoutMs}:fixed");
            return _respond(call);
        }

        public override MultipassRunResult RunWithExtensions(
            IReadOnlyList<string> args,
            TimeSpan slice,
            Func<bool> keepWaiting,
            string? exePath = null)
        {
            var call = args.ToArray();
            Calls.Add(call);
            Modes.Add($"{call[0]}:{(int)slice.TotalMilliseconds}:extensions");
            return _respond(call);
        }

        public override int Attach(IReadOnlyList<string> args, string? exePath = null)
        {
            Calls.Add(args.ToArray());
            return 0;
        }
    }
}
