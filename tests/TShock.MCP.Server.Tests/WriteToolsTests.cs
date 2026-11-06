using System.Text.Json.Nodes;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.Tools;

namespace TShock.MCP.Server.Tests;

public sealed class WriteToolsTests
{
    private static (WriteTools Tools, RecordingTShockClient Client) Arrange(bool allowWrite)
    {
        var client = new RecordingTShockClient();
        return (new WriteTools(client, TestOptions.Create(allowWrite: allowWrite)), client);
    }

    private static JsonObject ParseEnvelope(string output) =>
        Assert.IsType<JsonObject>(JsonNode.Parse(output));

    private static void AssertEnvelope(string output, string expectedOperation)
    {
        var envelope = ParseEnvelope(output);
        Assert.True(envelope["ok"]!.GetValue<bool>());
        Assert.Equal(
            expectedOperation,
            Assert.IsType<JsonObject>(envelope["data"])["result"]!.GetValue<string>());
    }

    private static void AssertWriteDisabled(string output)
    {
        var envelope = ParseEnvelope(output);
        Assert.False(envelope["ok"]!.GetValue<bool>());

        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("write_disabled", error["code"]!.GetValue<string>());
        Assert.Contains("TSHOCK_MCP_ALLOW_WRITE", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(error.ContainsKey("httpStatus"));
    }

    private static void AssertInvalidArgument(string output, string expectedMessageFragment)
    {
        var envelope = ParseEnvelope(output);
        Assert.False(envelope["ok"]!.GetValue<bool>());

        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.Contains(
            expectedMessageFragment,
            error["message"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.False(error.ContainsKey("httpStatus"));
    }

    [Fact]
    public async Task AllTools_ReturnWriteDisabledWhenWritesAreOff()
    {
        var (tools, client) = Arrange(allowWrite: false);

        var outputs = new[]
        {
            await tools.Broadcast("hello"),
            await tools.KickPlayer("alice"),
            await tools.KillPlayer("alice"),
            await tools.MutePlayer("alice"),
            await tools.SaveWorld(),
            await tools.ReloadConfig()
        };

        foreach (var output in outputs)
        {
            AssertWriteDisabled(output);
        }

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Broadcast_CallsV2ServerBroadcastWithMessage()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.Broadcast("Server restarting in 5 minutes");

        var call = Assert.Single(client.Calls);
        Assert.Equal("broadcast", call.Operation);
        Assert.Equal("/v2/server/broadcast", call.Path);
        Assert.Equal("Server restarting in 5 minutes", call.Query!["msg"]);
        Assert.Single(call.Query!);
        AssertEnvelope(output, "broadcast");
    }

    [Fact]
    public async Task KickPlayer_CallsV2PlayersKickWithPlayerAndReason()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.KickPlayer("alice", "griefing");

        var call = Assert.Single(client.Calls);
        Assert.Equal("kick_player", call.Operation);
        Assert.Equal("/v2/players/kick", call.Path);
        Assert.Equal("alice", call.Query!["player"]);
        Assert.Equal("griefing", call.Query!["reason"]);
        AssertEnvelope(output, "kick_player");
    }

    [Fact]
    public async Task KickPlayer_WithoutReasonLeavesReasonNull()
    {
        var (tools, client) = Arrange(allowWrite: true);

        await tools.KickPlayer("alice");

        var call = Assert.Single(client.Calls);
        Assert.Null(call.Query!["reason"]);
    }

    [Fact]
    public async Task KillPlayer_CallsV2PlayersKillWithPlayerAndSource()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.KillPlayer("alice", "server");

        var call = Assert.Single(client.Calls);
        Assert.Equal("kill_player", call.Operation);
        Assert.Equal("/v2/players/kill", call.Path);
        Assert.Equal("alice", call.Query!["player"]);
        Assert.Equal("server", call.Query!["from"]);
        AssertEnvelope(output, "kill_player");
    }

    [Fact]
    public async Task KillPlayer_WithoutSourceLeavesSourceNull()
    {
        var (tools, client) = Arrange(allowWrite: true);

        await tools.KillPlayer("alice");

        var call = Assert.Single(client.Calls);
        Assert.Null(call.Query!["from"]);
    }

    [Fact]
    public async Task MutePlayer_CallsV2PlayersMuteWithPlayer()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.MutePlayer("alice");

        var call = Assert.Single(client.Calls);
        Assert.Equal("mute_player", call.Operation);
        Assert.Equal("/v2/players/mute", call.Path);
        Assert.Equal("alice", call.Query!["player"]);
        Assert.Single(call.Query!);
        AssertEnvelope(output, "mute_player");
    }

    [Fact]
    public async Task SaveWorld_CallsV2WorldSaveWithoutQuery()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.SaveWorld();

        var call = Assert.Single(client.Calls);
        Assert.Equal("save_world", call.Operation);
        Assert.Equal("/v2/world/save", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "save_world");
    }

    [Fact]
    public async Task ReloadConfig_CallsV3ServerReloadWithoutQuery()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.ReloadConfig();

        var call = Assert.Single(client.Calls);
        Assert.Equal("reload_config", call.Operation);
        Assert.Equal("/v3/server/reload", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "reload_config");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Broadcast_ReturnsInvalidArgumentForEmptyMessage(string message)
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.Broadcast(message);

        var envelope = ParseEnvelope(output);
        Assert.False(envelope["ok"]!.GetValue<bool>());
        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.False(error.ContainsKey("httpStatus"));
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PlayerTools_ReturnInvalidArgumentForEmptyPlayer(string player)
    {
        var (tools, client) = Arrange(allowWrite: true);

        var outputs = new[]
        {
            await tools.KickPlayer(player),
            await tools.KillPlayer(player),
            await tools.MutePlayer(player)
        };

        foreach (var output in outputs)
        {
            var envelope = ParseEnvelope(output);
            Assert.False(envelope["ok"]!.GetValue<bool>());
            var error = Assert.IsType<JsonObject>(envelope["error"]);
            Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
            Assert.Contains("player", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.False(error.ContainsKey("httpStatus"));
        }

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task AllTools_PreferWriteDisabledOverInvalidArguments()
    {
        var (tools, client) = Arrange(allowWrite: false);

        // Every argument is invalid at the same time, yet the write gate must win:
        // a blocked write never performs validation work or an HTTP call.
        var outputs = new[]
        {
            await tools.Broadcast(string.Empty),
            await tools.KickPlayer(string.Empty),
            await tools.KillPlayer(string.Empty),
            await tools.MutePlayer(string.Empty),
            await tools.SaveWorld(),
            await tools.ReloadConfig()
        };

        foreach (var output in outputs)
        {
            AssertWriteDisabled(output);
        }

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Broadcast_ReturnsInvalidArgumentForNullMessage()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.Broadcast(null!);

        AssertInvalidArgument(output, "message");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task PlayerTools_ReturnInvalidArgumentForNullPlayer()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var outputs = new[]
        {
            await tools.KickPlayer(null!),
            await tools.KillPlayer(null!),
            await tools.MutePlayer(null!)
        };

        foreach (var output in outputs)
        {
            AssertInvalidArgument(output, "player");
        }

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task KickPlayer_NormalizesWhitespaceReasonToNull()
    {
        var (tools, client) = Arrange(allowWrite: true);

        await tools.KickPlayer("alice", "   ");

        var call = Assert.Single(client.Calls);
        Assert.Null(call.Query!["reason"]);
    }

    [Fact]
    public async Task KillPlayer_NormalizesWhitespaceSourceToNull()
    {
        var (tools, client) = Arrange(allowWrite: true);

        await tools.KillPlayer("alice", "\t");

        var call = Assert.Single(client.Calls);
        Assert.Null(call.Query!["from"]);
    }

    [Fact]
    public async Task Broadcast_AcceptsMessageAtTheLimit()
    {
        var (tools, client) = Arrange(allowWrite: true);
        var message = new string('a', 256);

        var output = await tools.Broadcast(message);

        Assert.Equal(message, Assert.Single(client.Calls).Query!["msg"]);
        AssertEnvelope(output, "broadcast");
    }

    [Fact]
    public async Task Broadcast_RejectsMessageOverTheLimit()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.Broadcast(new string('a', 257));

        AssertInvalidArgument(output, "message");
        Assert.Contains("256", ParseEnvelope(output)["error"]!["message"]!.GetValue<string>());
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Broadcast_RejectsControlCharacters()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.Broadcast("hello\nworld");

        AssertInvalidArgument(output, "message");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task KickPlayer_AcceptsReasonAtTheLimit()
    {
        var (tools, client) = Arrange(allowWrite: true);
        var reason = new string('r', 200);

        await tools.KickPlayer("alice", reason);

        Assert.Equal(reason, Assert.Single(client.Calls).Query!["reason"]);
    }

    [Fact]
    public async Task KickPlayer_RejectsReasonOverTheLimit()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.KickPlayer("alice", new string('r', 201));

        AssertInvalidArgument(output, "reason");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task KillPlayer_AcceptsSourceAtTheLimit()
    {
        var (tools, client) = Arrange(allowWrite: true);
        var source = new string('s', 64);

        await tools.KillPlayer("alice", source);

        Assert.Equal(source, Assert.Single(client.Calls).Query!["from"]);
    }

    [Fact]
    public async Task KillPlayer_RejectsSourceOverTheLimit()
    {
        var (tools, client) = Arrange(allowWrite: true);

        var output = await tools.KillPlayer("alice", new string('s', 65));

        AssertInvalidArgument(output, "from");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task PlayerTools_RejectOverlongPlayer()
    {
        var (tools, client) = Arrange(allowWrite: true);
        var player = new string('p', 65);

        var outputs = new[]
        {
            await tools.KickPlayer(player),
            await tools.KillPlayer(player),
            await tools.MutePlayer(player)
        };

        foreach (var output in outputs)
        {
            AssertInvalidArgument(output, "player");
        }

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task AllTools_PreferWriteDisabledOverOverlongArguments()
    {
        var (tools, client) = Arrange(allowWrite: false);

        var outputs = new[]
        {
            await tools.Broadcast(new string('a', 257)),
            await tools.KickPlayer("alice", new string('r', 201)),
            await tools.KillPlayer("alice", new string('s', 65)),
            await tools.MutePlayer(new string('p', 65))
        };

        foreach (var output in outputs)
        {
            AssertWriteDisabled(output);
        }

        Assert.Empty(client.Calls);
    }
}
