using System.Text.Json.Nodes;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.Tools;

namespace TShock.MCP.Server.Tests;

public sealed class ReadToolsTests
{
    private const string PlayerName = "alice";
    private const string GroupName = "default";

    private static (ReadTools Tools, RecordingTShockClient Client) Arrange()
    {
        var client = new RecordingTShockClient();
        return (new ReadTools(client), client);
    }

    private static void AssertEnvelope(string output, string expectedOperation)
    {
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        Assert.True(envelope["ok"]!.GetValue<bool>());
        Assert.Equal(
            expectedOperation,
            Assert.IsType<JsonObject>(envelope["data"])["result"]!.GetValue<string>());
    }

    private static void AssertInvalidArgument(string output, string expectedMessageFragment)
    {
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
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
    public async Task ServerStatus_CallsV2ServerStatusWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.ServerStatus();

        var call = Assert.Single(client.Calls);
        Assert.Equal("server_status", call.Operation);
        Assert.Equal("/v2/server/status", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "server_status");
    }

    [Fact]
    public async Task WorldInfo_CallsWorldReadWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.WorldInfo();

        var call = Assert.Single(client.Calls);
        Assert.Equal("world_info", call.Operation);
        Assert.Equal("/world/read", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "world_info");
    }

    [Fact]
    public async Task PlayersList_CallsV2PlayersListWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.PlayersList();

        var call = Assert.Single(client.Calls);
        Assert.Equal("players_list", call.Operation);
        Assert.Equal("/v2/players/list", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "players_list");
    }

    [Fact]
    public async Task PlayerInfo_CallsV4PlayersReadWithPlayerQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.PlayerInfo(PlayerName);

        var call = Assert.Single(client.Calls);
        Assert.Equal("player_info", call.Operation);
        Assert.Equal("/v4/players/read", call.Path);
        Assert.NotNull(call.Query);
        Assert.Equal(PlayerName, call.Query!["player"]);
        Assert.Single(call.Query!);
        AssertEnvelope(output, "player_info");
    }

    [Fact]
    public async Task ServerMotd_CallsV3ServerMotdWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.ServerMotd();

        var call = Assert.Single(client.Calls);
        Assert.Equal("server_motd", call.Operation);
        Assert.Equal("/v3/server/motd", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "server_motd");
    }

    [Fact]
    public async Task ServerRules_CallsV3ServerRulesWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.ServerRules();

        var call = Assert.Single(client.Calls);
        Assert.Equal("server_rules", call.Operation);
        Assert.Equal("/v3/server/rules", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "server_rules");
    }

    [Fact]
    public async Task UsersList_CallsV2UsersListWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.UsersList();

        var call = Assert.Single(client.Calls);
        Assert.Equal("users_list", call.Operation);
        Assert.Equal("/v2/users/list", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "users_list");
    }

    [Fact]
    public async Task UserInfo_DefaultsTypeToName()
    {
        var (tools, client) = Arrange();

        var output = await tools.UserInfo(PlayerName);

        var call = Assert.Single(client.Calls);
        Assert.Equal("user_info", call.Operation);
        Assert.Equal("/v2/users/read", call.Path);
        Assert.NotNull(call.Query);
        Assert.Equal(PlayerName, call.Query!["user"]);
        Assert.Equal("name", call.Query!["type"]);
        AssertEnvelope(output, "user_info");
    }

    [Fact]
    public async Task UserInfo_UsesExplicitTypeAndFallsBackForWhitespace()
    {
        var (tools, client) = Arrange();

        await tools.UserInfo("42", "id");
        await tools.UserInfo("alice", "   ");

        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("42", client.Calls[0].Query!["user"]);
        Assert.Equal("id", client.Calls[0].Query!["type"]);
        Assert.Equal("alice", client.Calls[1].Query!["user"]);
        Assert.Equal("name", client.Calls[1].Query!["type"]);
    }

    [Fact]
    public async Task GroupsList_CallsV2GroupsListWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.GroupsList();

        var call = Assert.Single(client.Calls);
        Assert.Equal("groups_list", call.Operation);
        Assert.Equal("/v2/groups/list", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "groups_list");
    }

    [Fact]
    public async Task GroupInfo_CallsV2GroupsReadWithGroupQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.GroupInfo(GroupName);

        var call = Assert.Single(client.Calls);
        Assert.Equal("group_info", call.Operation);
        Assert.Equal("/v2/groups/read", call.Path);
        Assert.Equal(GroupName, call.Query!["group"]);
        Assert.Single(call.Query!);
        AssertEnvelope(output, "group_info");
    }

    [Fact]
    public async Task BansList_CallsV3BansListWithoutQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.BansList();

        var call = Assert.Single(client.Calls);
        Assert.Equal("bans_list", call.Operation);
        Assert.Equal("/v3/bans/list", call.Path);
        Assert.Null(call.Query);
        AssertEnvelope(output, "bans_list");
    }

    [Fact]
    public async Task BanInfo_FormatsTicketNumberQuery()
    {
        var (tools, client) = Arrange();

        var output = await tools.BanInfo(4242);

        var call = Assert.Single(client.Calls);
        Assert.Equal("ban_info", call.Operation);
        Assert.Equal("/v3/bans/read", call.Path);
        Assert.Equal("4242", call.Query!["ticketNumber"]);
        Assert.Single(call.Query!);
        AssertEnvelope(output, "ban_info");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PlayerInfo_ReturnsInvalidArgumentForMissingPlayer(string? player)
    {
        var (tools, client) = Arrange();

        var output = await tools.PlayerInfo(player!);

        AssertInvalidArgument(output, "player");
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UserInfo_ReturnsInvalidArgumentForMissingUser(string? user)
    {
        var (tools, client) = Arrange();

        var output = await tools.UserInfo(user!);

        AssertInvalidArgument(output, "user");
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("nickname")]
    [InlineData("guid")]
    [InlineData("id;drop")]
    [InlineData("42")]
    public async Task UserInfo_ReturnsInvalidArgumentForUnknownType(string type)
    {
        var (tools, client) = Arrange();

        var output = await tools.UserInfo(PlayerName, type);

        AssertInvalidArgument(output, "type");
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("ID", "id")]
    [InlineData("Name", "name")]
    [InlineData("  ID  ", "id")]
    public async Task UserInfo_NormalizesTypeToLowercase(string type, string expectedType)
    {
        var (tools, client) = Arrange();

        await tools.UserInfo("42", type);

        var call = Assert.Single(client.Calls);
        Assert.Equal(expectedType, call.Query!["type"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GroupInfo_ReturnsInvalidArgumentForMissingGroup(string? group)
    {
        var (tools, client) = Arrange();

        var output = await tools.GroupInfo(group!);

        AssertInvalidArgument(output, "group");
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task BanInfo_ReturnsInvalidArgumentForNonPositiveTicket(int ticketNumber)
    {
        var (tools, client) = Arrange();

        var output = await tools.BanInfo(ticketNumber);

        AssertInvalidArgument(output, "ticketNumber");
        Assert.Empty(client.Calls);
    }
}
