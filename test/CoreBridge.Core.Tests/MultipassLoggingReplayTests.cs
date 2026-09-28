using System.Text.Json;
using CoreBridge.Core.Common;
using CoreBridge.Core.Config;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Protokoll echter Aufrufe (<see cref="LoggingMultipassCli"/>), Wiedergabe daraus
/// (<see cref="ReplayMultipassCli"/>) und die Auswahl über
/// <see cref="AppConfig.Bootstrap"/> statt Umgebungsvariablen.
/// </summary>
public class MultipassLoggingReplayTests
{
    private static readonly string[] ListArgs = ["list", "--format", "json"];

    [Fact]
    public void LoggingSchreibtKopfzeileUndEineZeileJeAufruf()
    {
        using var temp = new TempDirectory();
        var inner = new FakeMultipassCli(args => new MultipassRunResult(0, "out:" + args[0], "err", 5));
        var cli = new LoggingMultipassCli(inner, temp.Path);

        cli.Run(ListArgs, 1_000);
        cli.RunWithExtensions(["launch", "--name", "cbx-a"], TimeSpan.FromSeconds(2), () => true);
        cli.Attach(["shell", "cbx-a"]);

        var file = Assert.Single(Directory.GetFiles(temp.Path, "*.jsonl"));
        Assert.Equal(cli.LogFile, file);
        Assert.EndsWith($"-{Environment.ProcessId}.jsonl", file);

        var lines = File.ReadAllLines(file);
        Assert.Equal(4, lines.Length);
        using (var session = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal("session", session.RootElement.GetProperty("kind").GetString());
        }

        var entries = MultipassCallLog.Read(temp.Path);
        Assert.Equal(3, entries.Count);
        Assert.Equal([1, 2, 3], entries.Select(e => e.Seq));
        Assert.Equal(["Run", "RunWithExtensions", "Attach"], entries.Select(e => e.Mode));
        Assert.Equal(ListArgs, entries[0].Args);
        Assert.Equal("out:list", entries[0].StdOut);
        Assert.Equal(1_000, entries[0].TimeoutMs);
        Assert.Equal(2_000, entries[1].TimeoutMs);
        Assert.All(entries, e => Assert.False(e.TimedOut));
    }

    [Fact]
    public void LoggingProtokolliertTimeoutUndWirftWeiter()
    {
        using var temp = new TempDirectory();
        var inner = new FakeMultipassCli(args => throw new MultipassTimeoutException(
            string.Join(' ', args), 500, "teil", "fehlerteil"));
        var cli = new LoggingMultipassCli(inner, temp.Path);

        Assert.Throws<MultipassTimeoutException>(() => cli.Run(ListArgs, 500));

        var entry = Assert.Single(MultipassCallLog.Read(temp.Path));
        Assert.True(entry.TimedOut);
        Assert.Equal("teil", entry.StdOut);
        Assert.Equal("fehlerteil", entry.StdErr);
        Assert.Equal(500, entry.TimeoutMs);
    }

    [Fact]
    public void ReplayAntwortetGleicheAufrufeInAufgezeichneterReihenfolgeUndWiederholtDenLetzten()
    {
        var replay = ReplayMultipassCli.FromEntries(
        [
            Entry(ListArgs, stdOut: """{"list":[]}"""),
            Entry(["launch", "--name", "cbx-a"], stdOut: "Launched: cbx-a"),
            Entry(ListArgs, stdOut: """{"list":[{"name":"cbx-a"}]}""")
        ]);

        Assert.Equal("""{"list":[]}""", replay.Run(ListArgs).StdOut);
        Assert.Equal("Launched: cbx-a", replay.Run(["launch", "--name", "cbx-a"]).StdOut);
        Assert.Contains("cbx-a", replay.Run(ListArgs).StdOut);
        Assert.Contains("cbx-a", replay.Run(ListArgs).StdOut);
    }

    [Fact]
    public void ReplayWirftBeiUnbekanntemAufruf()
    {
        var replay = ReplayMultipassCli.FromEntries([Entry(ListArgs)]);

        var exception = Assert.Throws<MultipassException>(() => replay.Run(["stop", "cbx-x"]));
        Assert.Contains("multipass stop cbx-x", exception.Message);
    }

    [Fact]
    public void ReplayGibtTimeoutAlsExceptionWiederUndFragtNieNachWeiterWarten()
    {
        var replay = ReplayMultipassCli.FromEntries([Entry(["launch", "cbx-a"], timedOut: true, timeoutMs: 700)]);
        var asked = 0;

        var exception = Assert.Throws<MultipassTimeoutException>(() => replay.RunWithExtensions(
            ["launch", "cbx-a"],
            TimeSpan.FromMilliseconds(1),
            () =>
            {
                asked++;
                return true;
            }));

        Assert.Equal(700, exception.TimeoutMs);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AufzeichnungUndWiedergabeLiefernDieselbenErgebnisse()
    {
        using var temp = new TempDirectory();
        var answers = new Queue<MultipassRunResult>(
        [
            new(0, "erste", string.Empty, 1),
            new(3, string.Empty, "kaputt", 2),
            new(0, "zweite", string.Empty, 3)
        ]);
        var recorder = new LoggingMultipassCli(new FakeMultipassCli(_ => answers.Dequeue()), temp.Path);
        var recorded = new[]
        {
            recorder.Run(ListArgs),
            recorder.Run(["exec", "cbx-a", "--", "false"]),
            recorder.Run(ListArgs)
        };

        var replay = new ReplayMultipassCli(new AppConfig { MultipassLogPath = temp.Path });
        var replayed = new[]
        {
            replay.Run(ListArgs),
            replay.Run(["exec", "cbx-a", "--", "false"]),
            replay.Run(ListArgs)
        };

        Assert.Equal(recorded, replayed);
    }

    [Fact]
    public void StandardConfigLiefertEchtenClientOhneProtokoll()
    {
        var cli = MultipassCliFactory.Create(CoreBridgeContainer.Create(new AppConfig()));

        Assert.IsType<MultipassCli>(cli);
    }

    [Fact]
    public void GesetzterLogpfadUmhuelltDenEchtenClient()
    {
        using var temp = new TempDirectory();
        var config = new AppConfig { MultipassLogPath = temp.Path };

        var cli = MultipassCliFactory.Create(CoreBridgeContainer.Create(config));

        var logging = Assert.IsType<LoggingMultipassCli>(cli);
        Assert.IsType<MultipassCli>(logging.Inner);
        Assert.StartsWith(temp.Path, logging.LogFile);
    }

    [Fact]
    public void BootstrapAufReplayBekommtDieConfigUndWirdNichtProtokolliert()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(
            Path.Combine(temp.Path, "aufnahme.jsonl"),
            JsonSerializer.Serialize(Entry(ListArgs, stdOut: "aus der aufnahme")) + Environment.NewLine);
        var config = new AppConfig
        {
            MultipassLogPath = temp.Path,
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = typeof(ReplayMultipassCli).FullName! }
        };

        var cli = MultipassCliFactory.Create(CoreBridgeContainer.Create(config));

        Assert.IsType<ReplayMultipassCli>(cli);
        Assert.Equal("aus der aufnahme", cli.Run(ListArgs).StdOut);
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void ReplayOhneLogpfadMeldetKonfigurationsfehler()
    {
        var config = new AppConfig
        {
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = typeof(ReplayMultipassCli).FullName! }
        };

        var exception = Assert.Throws<MultipassException>(
            () => MultipassCliFactory.Create(CoreBridgeContainer.Create(config)));
        Assert.Contains(nameof(AppConfig.MultipassLogPath), exception.Message);
    }

    [Theory]
    [InlineData("CoreBridge.Core.Multipass.GibtsNicht")]
    [InlineData("CoreBridge.Core.Vm.VmService")]
    public void UngueltigeBootstrapZuordnungWirft(string implementation)
    {
        var config = new AppConfig
        {
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = implementation }
        };

        var exception = Assert.Throws<MultipassException>(() => CoreBridgeContainer.Create(config));
        Assert.Contains(implementation, exception.Message);
    }

    [Fact]
    public void NichtKonstruierbareImplementierungScheitertSchonBeimAufbau()
    {
        var config = new AppConfig
        {
            Bootstrap = new() { [typeof(IMultipassCli).FullName!] = typeof(OhneAufloesbarenKonstruktor).FullName! }
        };

        var exception = Assert.Throws<MultipassException>(() => CoreBridgeContainer.Create(config));
        Assert.Contains("Bootstrap-Konfiguration", exception.Message);
    }

    private static MultipassCallLogEntry Entry(
        string[] args,
        string stdOut = "",
        bool timedOut = false,
        int? timeoutMs = null)
    {
        return new MultipassCallLogEntry(
            1, 1, args[0], args, "Run", timeoutMs, 0, stdOut, string.Empty, 0, DateTimeOffset.Now, timedOut);
    }

    /// <summary>Verlangt einen nie registrierten Typ — der Container kann ihn nicht bauen.</summary>
    public sealed class OhneAufloesbarenKonstruktor(Uri nieRegistriert) : IMultipassCli
    {
        public Uri Quelle { get; } = nieRegistriert;

        public int Attach(IReadOnlyList<string> args, string? exePath = null) => 0;

        public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null) => new(0, "", "", 0);

        public MultipassRunResult RunWithExtensions(
            IReadOnlyList<string> args,
            TimeSpan slice,
            Func<bool> keepWaiting,
            string? exePath = null) => new(0, "", "", 0);
    }

    private sealed class FakeMultipassCli(Func<IReadOnlyList<string>, MultipassRunResult> answer) : IMultipassCli
    {
        public int Attach(IReadOnlyList<string> args, string? exePath = null) => answer(args).ExitCode;

        public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null) => answer(args);

        public MultipassRunResult RunWithExtensions(
            IReadOnlyList<string> args,
            TimeSpan slice,
            Func<bool> keepWaiting,
            string? exePath = null) => answer(args);
    }
}
