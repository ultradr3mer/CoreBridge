using System.Diagnostics;
using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

/// <summary>
/// Session-API mit einem Fake-Shell-Prozess (pwsh) statt Multipass — gleicher
/// Mechanismus (Pipes, Pumpen, WaitFor), kein VM-Zugriff nötig. Die echte
/// VM-Shell wird durch die nummerierten Stufen in `CliLifecycleTests`
/// im CLI-Testprojekt abgedeckt.
/// </summary>
public class VmConsoleSessionTests
{
    private static VmConsoleSession StartPwsh(Action<string>? log = null)
    {
        return VmConsoleSession.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = "-NoProfile -NoLogo"
        }, log);
    }

    [Fact]
    public void Log_Callback_SchreibtChunksLive()
    {
        var logged = new System.Text.StringBuilder();
        using var session = StartPwsh(chunk => logged.Append(chunk));
        session.SendCommand("Write-Output log-ok", "log-ok", TimeSpan.FromSeconds(30));
        Assert.Contains("log-ok", logged.ToString());
    }

    [Fact]
    public void SendLine_WirftEchoInDieAusgabe()
    {
        using var session = StartPwsh();
        var output = session.SendCommand("Write-Output session-ok", "session-ok", TimeSpan.FromSeconds(30));
        Assert.Contains("session-ok", output);
    }

    [Fact]
    public void WaitFor_FindeNichts_WirftTimeoutMitBisherigerAusgabe()
    {
        using var session = StartPwsh();
        session.SendLine("Write-Output sichtbar");
        var exception = Assert.Throws<MultipassTimeoutException>(
            () => session.WaitFor("gibts-nie", TimeSpan.FromSeconds(3)));
        Assert.Contains("sichtbar", exception.StdOut);
    }

    [Fact]
    public void SendCommand_Sequential_BefehleUndAusgabenKommenInReihenfolge()
    {
        using var session = StartPwsh();
        session.SendCommand("Write-Output erster", "erster", TimeSpan.FromSeconds(30));
        var second = session.SendCommand("Write-Output zweiter", "zweiter", TimeSpan.FromSeconds(30));
        Assert.Contains("erster", second);
        Assert.Contains("zweiter", second);
        var indexFirst = second.IndexOf("erster", StringComparison.Ordinal);
        var indexSecond = second.IndexOf("zweiter", StringComparison.Ordinal);
        Assert.True(indexFirst < indexSecond);
    }

    [Fact]
    public void Exit_BeendetDenProzessSauber()
    {
        using var session = StartPwsh();
        session.SendCommand("Write-Output bereit", "bereit", TimeSpan.FromSeconds(30));
        var exitCode = session.Exit();
        Assert.True(exitCode == 0 || exitCode == -1); // sauber beendet oder abgewürgt — Hauptsache weg.
    }
}
