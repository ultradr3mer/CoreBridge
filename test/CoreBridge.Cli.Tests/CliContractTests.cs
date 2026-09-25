using CoreBridge.Cli;
using CoreBridge.Core.Config;
using CoreBridge.Core.Images;
using CommandLine;
using CoreBridge.Core.Vm;

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
    public async Task RunLiefertMeldungenAlsAsyncEnumerable()
    {
        using var store = new TempDirectory();
        var config = new AppConfig
        {
            Images = new ImagesConfig { StoreRoot = store.Path }
        };

        var lines = new List<string>();
        await foreach (var line in Program.Run(config, new ImageListOptions()))
        {
            lines.Add(line);
        }

        Assert.Contains("Keine konfigurierten Classic-Profile.", lines);
    }

    [Fact]
    public async Task RunWirftBeiFehlendemImageProfil()
    {
        using var store = new TempDirectory();
        var config = new AppConfig
        {
            Images = new ImagesConfig { StoreRoot = store.Path }
        };

        await Assert.ThrowsAsync<MultipassException>(async () =>
        {
            await foreach (var _ in Program.Run(config, new ImageDeleteOptions { Name = "does-not-exist" }))
            {
            }
        });
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
