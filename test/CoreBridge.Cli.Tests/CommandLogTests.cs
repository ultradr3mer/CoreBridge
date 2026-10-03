using System.Threading.Channels;
using CoreBridge.Interaction;

namespace CoreBridge.Cli.Tests;

public class CommandLogTests
{
    /// <summary>
    /// Diagnose-Ereignisse landen im Protokoll: ein Werkzeugaufruf als aufruf/ergebn/out/err,
    /// gleiche Abfragen hintereinander einmal plus Zählzeile, Notizen als info.
    /// </summary>
    [Fact]
    public async Task DiagnoseEreignisseWerdenProtokolliert()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-commandlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var channel = Channel.CreateUnbounded<CliEvent>();
            var factory = new EventFactory(channel.Writer);
            factory.Post(factory.Diagnostic("Multipass-Client: MultipassCli"));
            for (var i = 0; i < 3; i++)
            {
                factory.Post(factory.ToolCall("sc", ["queryex", "Multipass"], 0, TimeSpan.FromMilliseconds(10 + i), "STATE : 3 STOP_PENDING", null));
            }

            factory.Post(factory.ToolCall("sc", ["queryex", "Multipass"], 0, TimeSpan.FromMilliseconds(10), "STATE : 1 STOPPED", "warn"));
            factory.Post(factory.Std("fertig"));
            channel.Writer.Complete();

            using (var log = CommandLog.Open(directory, ["multipass-restart"]))
            {
                await foreach (var _ in log.Tee(channel.Reader.ReadAllAsync()))
                {
                }
            }

            var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(directory)))
                .Select(line => line[(line.IndexOf(']') + 2)..].TrimEnd())
                .ToArray();
            Assert.Equal(
                [
                    "start   cb multipass-restart" + lines[0]["start   cb multipass-restart".Length..],
                    "info    Multipass-Client: MultipassCli",
                    "aufruf  sc queryex Multipass",
                    "ergebn  Exit 0 nach 10 ms",
                    "  out   STATE : 3 STOP_PENDING",
                    "ergebn  (derselbe Aufruf mit derselben Antwort noch 2× wiederholt)",
                    "aufruf  sc queryex Multipass",
                    "ergebn  Exit 0 nach 10 ms",
                    "  out   STATE : 1 STOPPED",
                    "  err   warn",
                    "stdout  fertig"
                ],
                lines);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
