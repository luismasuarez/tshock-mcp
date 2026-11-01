using System.Text.Json.Nodes;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.Tools;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

public sealed class BridgeToolsTests
{
    private static (BridgeTools Tools, RecordingBridgeClient Client) Arrange()
    {
        var client = new RecordingBridgeClient();
        return (new BridgeTools(client), client);
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
    public async Task EventsSince_WithoutArgumentsReadsTheBacklog()
    {
        var (tools, client) = Arrange();

        var output = await tools.EventsSince();

        var call = Assert.Single(client.Calls);
        Assert.Equal("events_since", call.Operation);
        Assert.Equal("/v1/events", call.Path);
        Assert.NotNull(call.Query);
        Assert.Empty(call.Query!);

        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        Assert.True(envelope["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EventsSince_ForwardsCursorAndLimit()
    {
        var (tools, client) = Arrange();

        await tools.EventsSince(cursor: 42, limit: 50);

        var call = Assert.Single(client.Calls);
        Assert.Equal("42", call.Query!["cursor"]);
        Assert.Equal("50", call.Query["limit"]);
    }

    [Fact]
    public async Task EventsSince_OmitsCursorWhenNotProvided()
    {
        var (tools, client) = Arrange();

        await tools.EventsSince(limit: 1);

        var call = Assert.Single(client.Calls);
        Assert.False(call.Query!.ContainsKey("cursor"));
        Assert.Equal("1", call.Query["limit"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task EventsSince_RejectsNegativeCursorBeforeTheNetwork(long cursor)
    {
        var (tools, client) = Arrange();

        var output = await tools.EventsSince(cursor: cursor);

        AssertInvalidArgument(output, "cursor");
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(int.MaxValue)]
    public async Task EventsSince_RejectsOutOfRangeLimitBeforeTheNetwork(int limit)
    {
        var (tools, client) = Arrange();

        var output = await tools.EventsSince(limit: limit);

        AssertInvalidArgument(output, "limit");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task EventsSince_AcceptsTheMaximumPageSize()
    {
        var (tools, client) = Arrange();

        await tools.EventsSince(limit: 500);

        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task EventsSince_ProjectsBridgeErrorsWithDetails()
    {
        var (tools, client) = Arrange();
        client.Result = BridgeResult.Failure(
            "cursor_expired",
            "The event cursor is no longer available.",
            410,
            new JsonObject { ["reason"] = "unknown", ["resumeCursor"] = 0 });

        var output = await tools.EventsSince(cursor: 7);

        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        Assert.False(envelope["ok"]!.GetValue<bool>());

        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("cursor_expired", error["code"]!.GetValue<string>());
        Assert.Equal(410, error["httpStatus"]!.GetValue<int>());
        var details = Assert.IsType<JsonObject>(error["details"]);
        Assert.Equal(0, details["resumeCursor"]!.GetValue<int>());
    }

    [Fact]
    public async Task RegionsList_CallsTheRegionsRoute()
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionsList();

        var call = Assert.Single(client.Calls);
        Assert.Equal("regions_list", call.Operation);
        Assert.Equal("/v1/regions", call.Path);
        Assert.Null(call.Query);

        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        Assert.True(envelope["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RegionInfo_EscapesTheNameIntoSinglePathSegment()
    {
        var (tools, client) = Arrange();

        await tools.RegionInfo("mi arena/1");

        // A slash is rejected before the network; this call never happens.
        Assert.Empty(client.Calls);

        var (tools2, client2) = Arrange();
        await tools2.RegionInfo("mi arena");
        Assert.Equal("/v1/regions/mi%20arena", Assert.Single(client2.Calls).Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("spawn/child")]
    [InlineData("spawn\nchild")]
    public async Task RegionInfo_RejectsUnusableNamesBeforeTheNetwork(string name)
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionInfo(name);

        AssertInvalidArgument(output, "name");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionInfo_RejectsNamesOverTheLimit()
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionInfo(new string('a', 129));

        AssertInvalidArgument(output, "name");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionInfo_MapsAMissingRegionToItsOwnCode()
    {
        var (tools, client) = Arrange();
        client.Result = BridgeResult.Failure(
            "bridge_error",
            "The bridge returned HTTP 404 (region_not_found).",
            404);

        var output = await tools.RegionInfo("spwan");

        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        Assert.False(envelope["ok"]!.GetValue<bool>());
        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("region_not_found", error["code"]!.GetValue<string>());
        Assert.Equal(404, error["httpStatus"]!.GetValue<int>());
        Assert.Contains("spwan", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegionInfo_DoesNotRewriteOtherBridgeFailures()
    {
        var (tools, client) = Arrange();
        client.Result = BridgeResult.Failure("bridge_unavailable", "The TShock bridge is not configured.");

        var output = await tools.RegionInfo("spawn");

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("bridge_unavailable", error["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task EventsSince_ReportsAnUnconfiguredBridge()
    {
        var tools = new BridgeTools(new RecordingBridgeClient
        {
            Result = BridgeResult.Failure("bridge_unavailable", "The TShock bridge is not configured.")
        });

        var output = await tools.EventsSince();

        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("bridge_unavailable", error["code"]!.GetValue<string>());
        Assert.False(error.ContainsKey("httpStatus"));
    }
}
