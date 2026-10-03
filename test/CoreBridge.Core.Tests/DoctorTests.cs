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

    /// <summary>
    /// Lehnt der Dienst den Client ab, meldet <c>multipass version</c> Exit 0 und nur die
    /// Client-Version (aufgenommen mit 1.16.3 nach einem Wechsel von MULTIPASS_STORAGE);
    /// erst <c>list</c> verrät den Grund.
    /// </summary>
    [Fact]
    public void NichtAuthentifizierterClientWirdErkannt()
    {
        var cli = new ScriptedMultipassCli(new()
        {
            ["version --format json"] = new(0, "{\n    \"multipass\": \"1.16.3+win\"\n}\n", string.Empty, 50),
            ["list --format json"] = new(
                1,
                string.Empty,
                "list failed: The client is not authenticated with the Multipass service.\nPlease use 'multipass authenticate' before proceeding.\n",
                50)
        });

        var report = Doctor.Check(() => "multipass.exe", cli);

        Assert.True(report.NotAuthenticated);
        Assert.Equal("1.16.3+win", report.ClientVersion);
        Assert.Null(report.DaemonVersion);
        Assert.Equal(Doctor.NotAuthenticatedMessage, report.Error);
    }

    /// <summary>Fehlt nur die Daemon-Version, ohne Ablehnung, bleibt es bei „keine Version“.</summary>
    [Fact]
    public void FehlendeDaemonVersionOhneAblehnungIstKeinAuthentifizierungsfehler()
    {
        var cli = new ScriptedMultipassCli(new()
        {
            ["version --format json"] = new(0, "{ \"multipass\": \"1.16.3+win\" }", string.Empty, 50),
            ["list --format json"] = new(1, string.Empty, "list failed: cannot connect to the multipass socket\n", 50)
        });

        var report = Doctor.Check(() => "multipass.exe", cli);

        Assert.False(report.NotAuthenticated);
        Assert.Contains("keine Version", report.Error);
    }

    private sealed class ScriptedMultipassCli(Dictionary<string, MultipassRunResult> answers) : MultipassCli(() => null)
    {
        public override MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30_000, string? exePath = null)
        {
            return answers[string.Join(' ', args)];
        }
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
