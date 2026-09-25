using CoreBridge.Cli;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;

namespace CoreBridge.Cli.Tests;

public sealed class CliProcessContractTests
{
    [Fact]
    public void JedeRegistrierteVerbOptionsklasseImplementiertIOptions()
    {
        Assert.Equal(14, Program.RegisteredOptionTypes.Count);
        Assert.All(Program.RegisteredOptionTypes, type => Assert.True(typeof(IOptions).IsAssignableFrom(type)));
    }

    [Fact]
    public void VmCreateImageKommtAusDerKonfiguration()
    {
        var options = new VmCreateOptions { Name = "demo" };

        Assert.Null(options.Image);
        Assert.Equal("daily:26.10", new ImagesConfig().DefaultRuntimeImage);
    }

    [Fact]
    public void RunVerwendetDenUebergebenenStoreRoot()
    {
        using var store = new TempDirectory();
        var config = new AppConfig
        {
            Images = new ImagesConfig { StoreRoot = store.Path }
        };

        var exitCode = Program.Run(config, new ImageListOptions());

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void RunBeendetFehlendesImageProfilMitFehler()
    {
        using var store = new TempDirectory();
        var config = new AppConfig
        {
            Images = new ImagesConfig { StoreRoot = store.Path }
        };

        var exitCode = Program.Run(config, new ImageDeleteOptions { Name = "does-not-exist" });

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void VmConnectHilfeIstEinNichtInteraktiverVertrag()
    {
        using var cli = new CliRunner();

        var result = cli.Run(["vm-connect", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--name", result.StdOut + result.StdErr);
    }

    [Fact]
    public void FehlendePflichtoptionIstEinParsefehler()
    {
        using var cli = new CliRunner();

        var result = cli.Run(["vm-create"]);

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.TimedOut);
    }
}
