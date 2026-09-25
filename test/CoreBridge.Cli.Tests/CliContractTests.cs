using CoreBridge.Cli;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CommandLine;

namespace CoreBridge.Cli.Tests;

public sealed class CliContractTests
{
    [Fact]
    public void JedeRegistrierteVerbOptionsklasseImplementiertIOptions()
    {
        Assert.Equal(13, Program.RegisteredOptionTypes.Count);
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

        var result = Program.Run(config, new ImageListOptions());

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunLiefertEineNativeAsyncEnumerableAusgabe()
    {
        using var store = new TempDirectory();
        var config = new AppConfig
        {
            Images = new ImagesConfig { StoreRoot = store.Path }
        };

        var result = Program.Run(config, new ImageListOptions());
        var lines = new List<string>();
        await foreach (var line in result)
        {
            lines.Add(line);
        }

        Assert.True(result.Success);
        Assert.Contains("Keine konfigurierten Classic-Profile.", lines);
    }

    [Fact]
    public void RunBeendetFehlendesImageProfilMitFehler()
    {
        using var store = new TempDirectory();
        var config = new AppConfig
        {
            Images = new ImagesConfig { StoreRoot = store.Path }
        };

        var result = Program.Run(config, new ImageDeleteOptions { Name = "does-not-exist" });

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void VmConnectOptionIstNichtInteraktiv()
    {
        var name = Assert.Single(typeof(VmConnectOptions).GetProperty("Name")!
            .GetCustomAttributes(typeof(OptionAttribute), inherit: true)
            .Cast<OptionAttribute>());

        Assert.True(name.Required);
        Assert.Equal("name", name.LongName);
    }

    [Fact]
    public void VmCreateNameIstPflichtoption()
    {
        var name = Assert.IsType<OptionAttribute>(
            Assert.Single(typeof(VmCreateOptions).GetProperty("Name")!
                .GetCustomAttributes(typeof(OptionAttribute), inherit: true)));

        Assert.True(name.Required);
    }
}
