using System.Text.Json.Nodes;
using TShock.McpServer.Tools;

namespace TShock.MCP.Server.Tests;

public sealed class ArgumentValidationTests
{
    private static JsonObject ParseError(string? output)
    {
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<string>(output)));
        Assert.False(envelope["ok"]!.GetValue<bool>());
        return Assert.IsType<JsonObject>(envelope["error"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void RequireText_RejectsMissingOrBlankValues(string? value)
    {
        var error = ParseError(ToolSupport.RequireText(value, "message", ArgumentLimits.BroadcastMessage));

        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.Contains("'message' must not be empty.", error["message"]!.GetValue<string>());
        Assert.False(error.ContainsKey("httpStatus"));
    }

    [Fact]
    public void RequireText_AcceptsTheMaximumLength()
    {
        Assert.Null(ToolSupport.RequireText(
            new string('a', ArgumentLimits.BroadcastMessage),
            "message",
            ArgumentLimits.BroadcastMessage));
    }

    [Fact]
    public void RequireText_RejectsOneCharacterOverTheLimit()
    {
        var error = ParseError(ToolSupport.RequireText(
            new string('a', ArgumentLimits.BroadcastMessage + 1),
            "message",
            ArgumentLimits.BroadcastMessage));

        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.Contains(
            $"'message' must be at most {ArgumentLimits.BroadcastMessage} characters.",
            error["message"]!.GetValue<string>());
    }

    [Fact]
    public void RequireText_CountsUnicodeCodePointsNotUtf16Units()
    {
        var atLimit = string.Concat(Enumerable.Repeat("\U0001F600", ArgumentLimits.BroadcastMessage));

        // 256 emoji are 512 UTF-16 code units but only 256 code points.
        Assert.Equal(ArgumentLimits.BroadcastMessage * 2, atLimit.Length);
        Assert.Null(ToolSupport.RequireText(atLimit, "message", ArgumentLimits.BroadcastMessage));

        var overLimit = string.Concat(Enumerable.Repeat("\U0001F600", ArgumentLimits.BroadcastMessage + 1));
        Assert.NotNull(ToolSupport.RequireText(overLimit, "message", ArgumentLimits.BroadcastMessage));
    }

    [Fact]
    public void RequireText_AcceptsNonBmpAndCombiningCharactersWithinTheLimit()
    {
        Assert.Null(ToolSupport.RequireText("héllo \U0001F600 e\u0301", "message", ArgumentLimits.BroadcastMessage));
    }

    [Theory]
    [InlineData("a\u200Cb")]
    [InlineData("a\u200Db")]
    public void RequireText_AllowsJoinersUsedByScriptsAndEmoji(string value)
    {
        Assert.Null(ToolSupport.RequireText(value, "message", ArgumentLimits.BroadcastMessage));
    }

    [Theory]
    [InlineData("hello\nworld")]
    [InlineData("hello\rworld")]
    [InlineData("hello\tworld")]
    [InlineData("hello\0world")]
    [InlineData("hello\u001Bworld")]
    [InlineData("hello\u202Eworld")]
    [InlineData("hello\u200Bworld")]
    [InlineData("hello\u2028world")]
    [InlineData("hello\u2029world")]
    public void RequireText_RejectsControlAndInvisibleFormattingCharacters(string value)
    {
        var error = ParseError(ToolSupport.RequireText(value, "message", ArgumentLimits.BroadcastMessage));

        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.Contains("control or invisible formatting characters", error["message"]!.GetValue<string>());
    }

    [Fact]
    public void RequireText_DoesNotEchoTheRejectedValue()
    {
        var value = new string('A', ArgumentLimits.BroadcastMessage + 1);
        var error = ParseError(ToolSupport.RequireText(value, "message", ArgumentLimits.BroadcastMessage));

        Assert.DoesNotContain(value, error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("spawn/child")]
    [InlineData("spawn\nchild")]
    public void RequireRegionName_RejectsUnusableNames(string? name)
    {
        var error = ParseError(ToolSupport.RequireRegionName(name));

        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.Contains("name", error["message"]!.GetValue<string>());
    }

    [Fact]
    public void RequireRegionName_RejectsNamesOverTheLimit()
    {
        var error = ParseError(ToolSupport.RequireRegionName(
            new string('a', ArgumentLimits.RegionName + 1)));

        Assert.Contains(
            $"'name' must be at most {ArgumentLimits.RegionName} characters.",
            error["message"]!.GetValue<string>());
    }

    [Fact]
    public void RequireRegionName_AcceptsNamesAtTheLimit()
    {
        Assert.Null(ToolSupport.RequireRegionName(new string('a', ArgumentLimits.RegionName)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void NormalizeOptional_DropsWhitespaceOnlyValues(string? value)
    {
        Assert.Null(ToolSupport.NormalizeOptional(value));
    }

    [Fact]
    public void NormalizeOptional_KeepsMeaningfulValues()
    {
        Assert.Equal("griefing", ToolSupport.NormalizeOptional("griefing"));
        Assert.Equal(" griefing ", ToolSupport.NormalizeOptional(" griefing "));
    }
}
