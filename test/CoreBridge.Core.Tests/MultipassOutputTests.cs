using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

public class MultipassOutputTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void SpinnerZeileWirdZumLetztenSichtbarenStand()
    {
        var output = $"{Esc}[2K{Esc}[0A{Esc}[0EStopping cbx-vm  /-\\|/-\\ {Esc}[2K{Esc}[0A{Esc}[0E\nstop failed: Cannot shut down.";

        Assert.Equal(["Stopping cbx-vm", "stop failed: Cannot shut down."], MultipassOutput.CleanLines(output));
    }

    [Fact]
    public void ReineSteuerzeileVerschwindet()
    {
        Assert.Empty(MultipassOutput.CleanLines($"{Esc}[2K{Esc}[0A{Esc}[0E"));
    }

    [Fact]
    public void ZeilenOhneSteuersequenzBleibenUnveraendert()
    {
        var output = "a/b/\n---\n\n---\n  eingerückt |";

        Assert.Equal(["a/b/", "---", "", "---", "  eingerückt |"], MultipassOutput.CleanLines(output));
    }
}
