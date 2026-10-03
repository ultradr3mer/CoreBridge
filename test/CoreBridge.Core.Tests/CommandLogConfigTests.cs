using CoreBridge.Core.Common;
using CoreBridge.Core.Config;

namespace CoreBridge.Core.Tests;

public class CommandLogConfigTests
{
    /// <summary>Ohne Einstellung bleibt das Protokoll aus; erst Development schaltet es ein.</summary>
    [Fact]
    public void StandardIstAus()
    {
        Assert.Null(new CommandLogConfig().ResolveDirectory());
        Assert.Null(new CommandLogConfig { Path = @"C:\irgendwo" }.ResolveDirectory());
    }

    /// <summary>Der Standardordner ist der einer Installation, kein Entwicklungspfad.</summary>
    [Fact]
    public void EingeschaltetOhnePfadSchreibtNachLocalAppData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreBridge", "logs");

        Assert.Equal(expected, new CommandLogConfig { Enabled = true }.ResolveDirectory());
        Assert.Equal(expected, new CommandLogConfig { Enabled = true, Path = " " }.ResolveDirectory());
    }

    /// <summary>Ein eigener Pfad wird wie <c>MultipassLogPath</c> aufgelöst: relativ zur Repo-Wurzel, absolut unverändert.</summary>
    [Fact]
    public void EigenerPfadWirdWieMultipassLogPathAufgeloest()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "cbx-logs");

        Assert.Equal(RepoPaths.Resolve("artifacts/cli-logs"), new CommandLogConfig { Enabled = true, Path = "artifacts/cli-logs" }.ResolveDirectory());
        Assert.Equal(absolute, new CommandLogConfig { Enabled = true, Path = absolute }.ResolveDirectory());
    }

    /// <summary>Den Entwicklungspfad setzt die mitgelieferte Development-Konfiguration selbst.</summary>
    [Fact]
    public void DevelopmentKonfigurationSchreibtNachArtifacts()
    {
        var file = RepoPaths.Resolve("src/CoreBridge.Cli/appsettings.Development.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        var commandLog = document.RootElement.GetProperty("CommandLog");

        Assert.True(commandLog.GetProperty("Enabled").GetBoolean());
        Assert.Equal("artifacts/logs", commandLog.GetProperty("Path").GetString());
    }
}
