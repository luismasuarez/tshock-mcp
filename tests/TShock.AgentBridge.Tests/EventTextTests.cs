using TShock.AgentBridge;
using Xunit;

namespace TShock.AgentBridge.Tests;

public sealed class EventTextTests
{
    [Theory]
    [InlineData(null, 3, "")]
    [InlineData("", 3, "")]
    [InlineData("Alice", 3, "Ali")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("Alice", 5, "Alice")]
    public void Truncate_ReturnsTheExpectedPrefix(string? value, int max, string expected)
    {
        Assert.Equal(expected, EventText.Truncate(value, max));
    }

    [Fact]
    public void Truncate_DoesNotSplitASurrogatePair()
    {
        // U+1F600 is two UTF-16 code units (D83D DE00); a naive cut at three would
        // leave a lone high surrogate.
        var value = "ab" + "\uD83D\uDE00";

        var truncated = EventText.Truncate(value, max: 3);

        Assert.Equal("ab", truncated);
    }

    [Fact]
    public void Truncate_KeepsTheStringWhenItFits()
    {
        const string value = "Alice";

        Assert.Same(value, EventText.Truncate(value, max: value.Length));
    }
}
