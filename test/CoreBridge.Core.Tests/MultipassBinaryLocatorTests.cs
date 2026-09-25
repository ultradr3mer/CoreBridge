using CoreBridge.Core.Multipass;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class MultipassBinaryLocatorTests
{
    private const string MissingProgramFiles = @"C:\CoreBridge-nicht-vorhanden";

    [Fact]
    public void EnvOverrideWirdVerbatimUebernommen()
    {
        Assert.Equal(
            @"Z:\custom\multipass.exe",
            MultipassBinaryLocator.LocateOrNull(@"Z:\custom\multipass.exe", null, MissingProgramFiles));
    }

    [Fact]
    public void PathEintragWirdGefunden()
    {
        using var directory = new TempDirectory();
        var exe = Path.Combine(directory, "multipass.exe");
        File.WriteAllText(exe, "fake");

        Assert.Equal(exe, MultipassBinaryLocator.LocateOrNull("", directory, MissingProgramFiles));
    }

    [Fact]
    public void PathEintragMitAnfuehrungszeichenWirdGefunden()
    {
        using var directory = new TempDirectory();
        var exe = Path.Combine(directory, "multipass.exe");
        File.WriteAllText(exe, "fake");

        var quotedPath = "\"" + directory + "\"";
        Assert.Equal(exe, MultipassBinaryLocator.LocateOrNull("", quotedPath, MissingProgramFiles));
    }

    [Fact]
    public void StandardPfadWirdGefunden()
    {
        using var programFiles = new TempDirectory();
        var exe = Path.Combine(programFiles, @"Multipass\bin\multipass.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "fake");

        Assert.Equal(exe, MultipassBinaryLocator.LocateOrNull("", "", programFiles));
    }

    [Fact]
    public void NichtsGefundenGibtNullZurueck()
    {
        Assert.Null(MultipassBinaryLocator.LocateOrNull("", "", MissingProgramFiles));
    }

    [Fact]
    public void IsInstalledPrueftExistenzDesEnvOverrides()
    {
        using var directory = new TempDirectory();
        var exe = Path.Combine(directory, "multipass.exe");
        File.WriteAllText(exe, "fake");

        Assert.True(MultipassBinaryLocator.IsInstalled(exe, null, MissingProgramFiles));
        Assert.False(MultipassBinaryLocator.IsInstalled(Path.Combine(directory, "fehlt.exe"), null, MissingProgramFiles));
    }
}
