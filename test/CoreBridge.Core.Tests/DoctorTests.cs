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
}
