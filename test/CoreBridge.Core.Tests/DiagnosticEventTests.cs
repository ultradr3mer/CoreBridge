using System.Threading.Channels;
using CoreBridge.Core.Multipass;
using CoreBridge.Interaction;

namespace CoreBridge.Core.Tests;

public class DiagnosticEventTests
{
    private static (EventFactory Factory, ChannelReader<CliEvent> Reader) NewFactory()
    {
        var channel = Channel.CreateUnbounded<CliEvent>();
        return (new EventFactory(channel.Writer), channel.Reader);
    }

    private static List<CliEvent> Drain(ChannelReader<CliEvent> reader)
    {
        var events = new List<CliEvent>();
        while (reader.TryRead(out var cliEvent))
        {
            events.Add(cliEvent);
        }

        return events;
    }

    /// <summary>Der Werkzeugaufruf trägt Kommandozeile, Ergebnis und beide Ausgabeströme.</summary>
    [Fact]
    public void ToolCallTraegtAufrufUndErgebnis()
    {
        var (factory, _) = NewFactory();

        var call = factory.ToolCall("multipass", ["exec", "vm", "--", "/bin/sh", "-c", "echo hi"], 1, TimeSpan.FromMilliseconds(42), "hi\n", "oops\n", "Notiz");

        Assert.Equal("multipass exec vm -- /bin/sh -c \"echo hi\"", call.CommandLine);
        Assert.Equal("Exit 1 nach 42 ms — Notiz", call.Result);
        Assert.Equal("hi\n", call.StdOut);
        Assert.Equal("oops\n", call.StdErr);
        Assert.IsAssignableFrom<DiagnosticEvent>(call);
    }

    /// <summary>Die Passphrase von multipass-store erreicht keinen Konsumenten.</summary>
    [Fact]
    public void GeheimnisWirdVorDemKanalErsetzt()
    {
        var (factory, _) = NewFactory();
        factory.AddSecret("s3cr3t");

        var call = factory.ToolCall("multipass", ["set", "local.passphrase=s3cr3t"], 0, TimeSpan.Zero, "echo s3cr3t", null, "s3cr3t");
        var info = factory.Diagnostic("Passphrase s3cr3t");

        Assert.Equal("multipass set local.passphrase=***", call.CommandLine);
        Assert.DoesNotContain("s3cr3t", call.Printable + call.StdOut + call.Note + info.Printable);
    }

    /// <summary>Der Decorator meldet jeden Aufruf als Ereignis im Strom, ohne das Ergebnis zu ändern.</summary>
    [Fact]
    public void TracingMultipassCliMeldetJedenAufruf()
    {
        var (factory, reader) = NewFactory();
        var inner = new FakeCli(new MultipassRunResult(0, "out", "err", 5));
        var cli = new TracingMultipassCli(inner, factory);

        var result = cli.Run(["list", "--format", "json"]);

        Assert.Same(inner.Result, result);
        var call = Assert.IsType<ToolCallEvent>(Assert.Single(Drain(reader)));
        Assert.Equal("multipass list --format json", call.CommandLine);
        Assert.Equal(0, call.ExitCode);
        Assert.Equal("out", call.StdOut);
        Assert.Equal("err", call.StdErr);
    }

    /// <summary>Ein abgebrochener Aufruf erscheint mit der bis dahin gelesenen Ausgabe, der Fehler geht weiter.</summary>
    [Fact]
    public void TracingMultipassCliMeldetTimeout()
    {
        var (factory, reader) = NewFactory();
        var cli = new TracingMultipassCli(new FakeCli(new MultipassTimeoutException("launch", 1000, "halb", "")), factory);

        Assert.Throws<MultipassTimeoutException>(() => cli.RunWithExtensions(["launch"], TimeSpan.FromSeconds(1), () => false));

        var call = Assert.IsType<ToolCallEvent>(Assert.Single(Drain(reader)));
        Assert.Null(call.ExitCode);
        Assert.Equal("halb", call.StdOut);
        Assert.Contains("Prozess beendet", call.Note);
    }

    /// <summary>Eine Attrappe bleibt hinter dem Decorator erkennbar und erscheint unter ihrem Pseudo-Pfad.</summary>
    [Fact]
    public void AttrappeBleibtErkennbar()
    {
        var (factory, reader) = NewFactory();
        var cli = new TracingMultipassCli(new FakeDouble(), factory);

        cli.Run(["list"]);

        Assert.Equal("fake://multipass", cli.AsDouble()?.BinaryLabel);
        Assert.Null(new TracingMultipassCli(new FakeCli(new MultipassRunResult(0, "", "", 0)), factory).AsDouble());
        Assert.Equal("fake://multipass list", Assert.IsType<ToolCallEvent>(Assert.Single(Drain(reader))).CommandLine);
    }

    private class FakeCli : IMultipassCli
    {
        private readonly Exception? failure;

        public FakeCli(MultipassRunResult result) => this.Result = result;

        public FakeCli(Exception failure)
        {
            this.failure = failure;
            this.Result = new MultipassRunResult(0, "", "", 0);
        }

        public MultipassRunResult Result { get; }

        public int Attach(IReadOnlyList<string> args, string? exePath = null) => this.Get().ExitCode;

        public MultipassRunResult Run(IReadOnlyList<string> args, int timeoutMs = 30000, string? exePath = null) => this.Get();

        public MultipassRunResult RunWithExtensions(IReadOnlyList<string> args, TimeSpan slice, Func<bool> keepWaiting, string? exePath = null) => this.Get();

        private MultipassRunResult Get() => this.failure is null ? this.Result : throw this.failure;
    }

    private sealed class FakeDouble() : FakeCli(new MultipassRunResult(0, "[]", "", 0)), IMultipassDouble
    {
        public string BinaryLabel => "fake://multipass";
    }
}
