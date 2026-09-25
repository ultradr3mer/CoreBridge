using CoreBridge.Core.Common;

namespace CoreBridge.Core.Tests;

public class ExtensionsTests
{
    [Theory]
    [InlineData("10s", 10_000)]
    [InlineData("1000ms", 1_000)]
    [InlineData(" 5 S ", 5_000)]
    [InlineData("250MS", 250)]
    public void ReadTimeLiestSekundenUndMillisekunden(string value, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), value.ReadTime());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10m")]
    [InlineData("abc")]
    [InlineData("10")]
    public void ReadTimeWirftBeiUngueltigemWert(string? value)
    {
        Assert.Throws<FormatException>(() => value.ReadTime());
    }
}
