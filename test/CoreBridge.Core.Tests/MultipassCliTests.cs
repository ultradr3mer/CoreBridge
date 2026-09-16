using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class MultipassCliTests
{
    private static string CmdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    [Fact]
    public void ExitCodeWirdDurchgereicht()
    {
        var cli = new MultipassCli(() => CmdPath);
        var result = cli.Run(new[] { "/c", "exit", "42" }, 15_000, CmdPath);
        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public void StdOutUndStdErrWerdenGetrennt()
    {
        var cli = new MultipassCli(() => CmdPath);
        var result = cli.Run(new[] { "/c", "echo", "corebridge-stdout-test", "1>&2" }, 15_000, CmdPath);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("corebridge-stdout-test", result.StdErr);
        Assert.DoesNotContain("corebridge-stdout-test", result.StdOut);
    }

    [Fact]
    public void DauerWirdGemessen()
    {
        var cli = new MultipassCli(() => CmdPath);
        var result = cli.Run(new[] { "/c", "exit", "0" }, 15_000, CmdPath);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public void TimeoutKilltProzessbaumUndWirft()
    {
        var cli = new MultipassCli(() => CmdPath);
        var exception = Assert.Throws<MultipassTimeoutException>(() =>
            cli.Run(new[] { "/c", "ping", "-n", "30", "127.0.0.1" }, 1_000, CmdPath));

        Assert.Equal(1_000, exception.TimeoutMs);
        Assert.Contains("ping -n 30 127.0.0.1", exception.Command);
    }

    [Fact]
    public void FehlendesBinaryWirftMultipassNotFoundException()
    {
        var cli = new MultipassCli(() => null);
        Assert.Throws<MultipassNotFoundException>(() => cli.Run(new[] { "version" }, 15_000));
    }

    [Fact]
    public void StartFehlschlagWirftMultipassException()
    {
        var cli = new MultipassCli(() => @"Z:\gibtsnicht\multipass.exe");
        Assert.ThrowsAny<MultipassException>(() => cli.Run(new[] { "version" }, 15_000));
    }
}
