using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class DoctorTests
{
    [Fact]
    public void OhneMultipassNichtGefundenMitHinweisen()
    {
        var report = Doctor.Check(() => null);

        Assert.False(report.Found);
        Assert.Null(report.BinaryPath);
        Assert.Null(report.ClientVersion);
        Assert.All(report.Hints, hint => Assert.False(string.IsNullOrWhiteSpace(hint)));
        Assert.Contains(report.Hints, hint => hint.Contains("Canonical.Multipass"));
        Assert.Contains(report.Hints, hint => hint.Contains("https://"));
    }

    [Fact]
    public void GefundenAberAntwortKeinJsonLiefertFehler()
    {
        var hostnamePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "hostname.exe");

        var report = Doctor.Check(() => hostnamePath);

        Assert.True(report.Found);
        Assert.Equal(hostnamePath, report.BinaryPath);
        Assert.Null(report.ClientVersion);
        Assert.NotNull(report.Error);
    }

    [Fact]
    public void TimeoutWirdDurchgereichtUndAlsFehlerGemeldet()
    {
        var cli = new HangingMultipassCli();

        var report = Doctor.Check(() => "multipass.exe", cli, timeoutMs: 1_234);

        Assert.Equal(1_234, cli.TimeoutMs);
        Assert.True(report.Found);
        Assert.Null(report.DaemonVersion);
        Assert.Contains("1234 ms", report.Error);
    }

    /// <summary>Daemon antwortet nicht: jeder Aufruf läuft in den Timeout.</summary>
    private sealed class HangingMultipassCli() : MultipassCli(() => null)
    {
        public int? TimeoutMs { get; private set; }

        public override MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30_000, string? exePath = null)
        {
            TimeoutMs = timeoutMs;
            throw new MultipassTimeoutException(string.Join(' ', args), timeoutMs, string.Empty, string.Empty);
        }
    }
}
