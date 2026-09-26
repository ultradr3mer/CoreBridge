using CoreBridge.Core.Multipass;

namespace CoreBridge.Core.Tests;

public class MultipassSizeTests
{
    [Theory]
    [InlineData("20G", 20L * 1024 * 1024 * 1024)]
    [InlineData("5.0GiB", 5L * 1024 * 1024 * 1024)]
    [InlineData("0.5G", 512L * 1024 * 1024)]
    [InlineData("42MiB", 42L * 1024 * 1024)]
    [InlineData("512m", 512L * 1024 * 1024)]
    [InlineData("1234B", 1234L)]
    [InlineData("1234", 1234L)]
    public void ParstWieMultipass(string text, long expected)
    {
        Assert.True(MultipassSize.TryParse(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-5G")]
    [InlineData("1.5")]
    [InlineData("20T")]
    public void LehntAbWasMultipassAblehnt(string? text)
    {
        Assert.False(MultipassSize.TryParse(text, out _));
    }
}
