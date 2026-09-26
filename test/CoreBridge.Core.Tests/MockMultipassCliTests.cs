using System.Text.Json;
using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Der Mock soll die ganze Anwendung ohne Multipass durchspielbar machen:
/// Zustandswechsel, Exec-Sonden, JSON-Protokoll und Persistenz über Prozesse.
/// </summary>
public class MockMultipassCliTests
{
    [Fact]
    public void ZyklusCreateListStopDeleteLaeuftUndWirdAlsJsonProtokolliert()
    {
        using var temp = new TempDirectory();
        var log = Path.Combine(temp.Path, MockMultipassCli.DefaultLogFileName);
        var mock = new MockMultipassCli(log);

        Assert.Equal(0, mock.Run(new[] { "launch", "daily:26.10", "--name", "cbx-a" }).ExitCode);
        var list = mock.Run(new[] { "list", "--format", "json" });
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("cbx-a", list.StdOut);
        Assert.Contains("\"state\":\"Running\"", list.StdOut);

        Assert.Equal(0, mock.Run(new[] { "stop", "cbx-a" }).ExitCode);
        Assert.Contains("\"state\":\"Stopped\"", mock.Run(new[] { "list", "--format", "json" }).StdOut);

        Assert.Equal(0, mock.Run(new[] { "delete", "cbx-a", "--purge" }).ExitCode);

        // Jeder Aufruf landet als eine JSON-Zeile in der Logdatei
        // (launch, list, stop, list, delete).
        var lines = File.ReadAllLines(log);
        Assert.Equal(5, lines.Length);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var first = JsonSerializer.Deserialize<MultipassCallLogEntry>(lines[0], options)!;
        Assert.Equal("launch", first.Verb);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(new[] { "launch", "daily:26.10", "--name", "cbx-a" }, first.Args);
        Assert.Equal("Run", first.Mode);
        Assert.Equal(5, mock.Entries.Count);
    }

    [Fact]
    public void ZustandUeberlebtDenProzess()
    {
        using var temp = new TempDirectory();
        var log = Path.Combine(temp.Path, MockMultipassCli.DefaultLogFileName);

        new MockMultipassCli(log).Run(new[] { "launch", "daily:26.10", "--name", "cbx-b" });

        // Neuer Prozess, neuer Mock — die VM muss trotzdem da sein.
        var second = new MockMultipassCli(log);
        Assert.Contains("cbx-b", second.Run(new[] { "list", "--format", "json" }).StdOut);
    }

    [Fact]
    public void ExecBeantwortetInstallationsSonden()
    {
        var mock = new MockMultipassCli();
        mock.Run(new[] { "launch", "daily:26.10", "--name", "cbx-c" });

        Assert.Contains("Claude Code", Exec(mock, "claude --version").StdOut);
        Assert.Contains("10.0", Exec(mock, "dotnet --version").StdOut);
        Assert.Equal(0, Exec(mock, "test -f /var/lib/corebridge/dotnet.ready").ExitCode);
        Assert.Equal(1, Exec(mock, "test -f /var/lib/corebridge/gibtsnicht.ready").ExitCode);
        Assert.Contains("26.10", Exec(mock, ". /etc/os-release && test \"$VERSION_ID\" = \"26.10\"").StdOut);
    }

    [Fact]
    public void ExecLiefertBeiMehrerenSegmentenAlleAusgaben()
    {
        var mock = new MockMultipassCli();
        mock.Run(new[] { "launch", "daily:26.10", "--name", "cbx-c2" });

        var result = mock.Run(new[] { "exec", "cbx-c2", "--", "/bin/sh", "-c", "claude --version; dotnet --version" });

        Assert.Contains("Claude Code", result.StdOut);
        Assert.Contains("10.0", result.StdOut);
    }

    [Fact]
    public void RunWithExtensionsFragtNichtNachKeepWaiting()
    {
        var mock = new MockMultipassCli();
        mock.Run(new[] { "launch", "daily:26.10", "--name", "cbx-d" });

        var asked = false;
        var result = mock.RunWithExtensions(
            new[] { "launch", "daily:26.10", "--name", "cbx-e" },
            TimeSpan.FromMilliseconds(50),
            () => { asked = true; return true; });

        // Ein Prompt hier würde genau dort haengen, wo der Mock beschleunigen soll.
        Assert.False(asked);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void UnbekanntesVerbUndFehlendeVmMeldenExit1()
    {
        var mock = new MockMultipassCli();

        var unknown = mock.Run(new[] { "quatsch" });
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("unbekanntes Verb", unknown.StdErr);

        var missing = mock.Run(new[] { "start", "cbx-nicht-da" });
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("nicht gefunden", missing.StdErr);
    }

    [Fact]
    public void IsEnabledErkenntNurWahrheitswerte()
    {
        Assert.True(MockMultipassCli.IsEnabled("1"));
        Assert.True(MockMultipassCli.IsEnabled("TRUE"));
        Assert.True(MockMultipassCli.IsEnabled("on"));
        Assert.False(MockMultipassCli.IsEnabled("0"));
        Assert.False(MockMultipassCli.IsEnabled(""));
    }

    [Fact]
    public void IsEnabledLiestDieEnvVariable()
    {
        const string name = MockMultipassCli.EnableEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "1");
            Assert.True(MockMultipassCli.IsEnabled());

            Environment.SetEnvironmentVariable(name, "0");
            Assert.False(MockMultipassCli.IsEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    private static MultipassRunResult Exec(MockMultipassCli mock, string command)
    {
        return mock.Run(new[] { "exec", "cbx-c", "--", "/bin/sh", "-c", command });
    }
}
