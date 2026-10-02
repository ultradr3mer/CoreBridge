using CoreBridge.Core.Common;

namespace CoreBridge.Core.Tests;

public class ExtensionsTests
{
    [Theory]
    [InlineData("10s", 10_000)]
    [InlineData("1000ms", 1_000)]
    [InlineData(" 5 S ", 5_000)]
    [InlineData("250MS", 250)]
    [InlineData("5m", 300_000)]
    [InlineData(" 2 M ", 120_000)]
    public void ReadTimeLiestMinutenSekundenUndMillisekunden(string value, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), value.ReadTime());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10min")]
    [InlineData("1h")]
    [InlineData("abc")]
    [InlineData("10")]
    public void ReadTimeWirftBeiUngueltigemWert(string? value)
    {
        Assert.Throws<FormatException>(() => value.ReadTime());
    }

    [Fact]
    public void InlineThrowWirftUebergebeneExceptionAlsArgument()
    {
        var expected = new InvalidOperationException("kaputt");

        var actual = Assert.Throws<InvalidOperationException>(() => Math.Max(1, Inline.Throw<int>(expected)));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void InlineTodoWirftNotImplemented()
    {
        var exception = Assert.Throws<NotImplementedException>(() => TimeSpan.FromSeconds(Inline.Todo<double>("Timeout")));

        Assert.Contains("Timeout", exception.Message);
    }
}
