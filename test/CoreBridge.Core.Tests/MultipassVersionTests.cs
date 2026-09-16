using CoreBridge.Core.Vm;

namespace CoreBridge.Core.Tests;

public class MultipassVersionTests
{
    [Fact]
    public void ParseLiestCliUndDaemonVersion()
    {
        const string json = """
            {
                "multipass": "1.16.3+win",
                "multipassd": "1.16.3+win"
            }
            """;

        var (client, daemon) = MultipassVersion.Parse(json);

        Assert.Equal("1.16.3+win", client);
        Assert.Equal("1.16.3+win", daemon);
    }

    [Fact]
    public void ParseOhneDaemonLiefertNull()
    {
        const string json = """
            {
                "multipass": "1.16.3+win"
            }
            """;

        var (client, daemon) = MultipassVersion.Parse(json);

        Assert.Equal("1.16.3+win", client);
        Assert.Null(daemon);
    }

    [Fact]
    public void ParseMitUngueltigemJsonWirft()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MultipassVersion.Parse("kein json"));
    }
}
