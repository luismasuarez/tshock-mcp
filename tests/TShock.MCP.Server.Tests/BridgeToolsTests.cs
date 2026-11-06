using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.Tools;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

public sealed class BridgeToolsTests
{
    private static (BridgeTools Tools, RecordingBridgeClient Client) Arrange(bool allowWrite = true)
    {
        var client = new RecordingBridgeClient();
        return (new BridgeTools(client, TestOptions.Create(allowWrite: allowWrite)), client);
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

    [Theory]
    [InlineData(false, null, false, BridgeTools.DeleteDecision.RequireConfirmation)]
    [InlineData(false, true, false, BridgeTools.DeleteDecision.Proceed)]
    [InlineData(true, null, false, BridgeTools.DeleteDecision.Ask)]
    [InlineData(true, null, true, BridgeTools.DeleteDecision.Ask)]
    [InlineData(true, true, null, BridgeTools.DeleteDecision.Proceed)]
    [InlineData(true, false, null, BridgeTools.DeleteDecision.Decline)]
    public void DecideDelete_IsCapabilityDriven(
        bool canElicit,
        bool? answered,
        bool? confirm,
        BridgeTools.DeleteDecision expected)
    {
        Assert.Equal(expected, BridgeTools.DecideDelete(canElicit, answered, confirm));
    }

    [Fact]
    public void AnsweredConfirmation_ReadsTheOperatorAnswer()
    {
        var accepted = new Dictionary<string, InputResponse>
        {
            [BridgeTools.ConfirmationKey] = InputResponse.FromElicitResult(
                new ElicitResult { Action = "accept" })
        };
        var declined = new Dictionary<string, InputResponse>
        {
            [BridgeTools.ConfirmationKey] = InputResponse.FromElicitResult(
                new ElicitResult { Action = "decline" })
        };

        Assert.True(BridgeTools.AnsweredConfirmation(accepted));
        Assert.False(BridgeTools.AnsweredConfirmation(declined));
        Assert.Null(BridgeTools.AnsweredConfirmation(null));
        Assert.Null(BridgeTools.AnsweredConfirmation(new Dictionary<string, InputResponse>()));
    }

    [Fact]
    public async Task RegionCreate_IsBlockedWhenWritesAreDisabled()
    {
        var (tools, client) = Arrange(allowWrite: false);

        var output = await tools.RegionCreate("arena", 10, 10, 20, 20);

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("write_disabled", error["code"]!.GetValue<string>());
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionCreate_SendsTheBodyWithoutOwnerByDefault()
    {
        var (tools, client) = Arrange();

        await tools.RegionCreate("arena", 10, 20, 30, 40);

        var call = Assert.Single(client.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("/v1/regions", call.Path);
        Assert.Equal("arena", call.Body!["name"]!.GetValue<string>());
        Assert.Equal(10, call.Body["x"]!.GetValue<int>());
        Assert.Equal(20, call.Body["y"]!.GetValue<int>());
        Assert.Equal(30, call.Body["width"]!.GetValue<int>());
        Assert.Equal(40, call.Body["height"]!.GetValue<int>());
        Assert.False(call.Body!.AsObject().ContainsKey("owner"));
    }

    [Fact]
    public async Task RegionCreate_ForwardsTheRequestedOwner()
    {
        var (tools, client) = Arrange();

        await tools.RegionCreate("parcela", 10, 20, 30, 40, owner: "Alice");

        Assert.Equal("Alice", Assert.Single(client.Calls).Body!["owner"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public async Task RegionCreate_RejectsInvalidSizeBeforeTheNetwork(int width, int height)
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionCreate("arena", 10, 10, width, height);

        AssertInvalidArgument(output, "width");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionCreate_MapsAPolicyRejectionToOwnerNotAllowed()
    {
        var (tools, client) = Arrange();
        client.Result = BridgeResult.Failure(
            "bridge_error",
            "The bridge returned HTTP 403 (owner_not_allowed).",
            403) with { BridgeError = "owner_not_allowed" };

        var output = await tools.RegionCreate("parcela", 10, 10, 20, 20, owner: "Alice");

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("owner_not_allowed", error["code"]!.GetValue<string>());
        Assert.Equal(403, error["httpStatus"]!.GetValue<int>());
    }

    [Fact]
    public async Task RegionCreate_MapsAnExistingRegion()
    {
        var (tools, client) = Arrange();
        client.Result = BridgeResult.Failure(
            "bridge_error",
            "The bridge returned HTTP 409 (region_exists).",
            409) with { BridgeError = "region_exists" };

        var output = await tools.RegionCreate("arena", 10, 10, 20, 20);

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("region_exists", error["code"]!.GetValue<string>());
        Assert.Equal(409, error["httpStatus"]!.GetValue<int>());
    }

    [Fact]
    public async Task RegionCreate_MapsAWorldBoundsRejectionToInvalidArgument()
    {
        var (tools, client) = Arrange();
        client.Result = BridgeResult.Failure(
            "bridge_error",
            "The bridge returned HTTP 400 (out_of_bounds).",
            400) with { BridgeError = "out_of_bounds" };

        var output = await tools.RegionCreate("arena", 9999, 9999, 20, 20);

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.Equal(400, error["httpStatus"]!.GetValue<int>());
    }

    [Fact]
    public async Task RegionDelete_IsBlockedWhenWritesAreDisabled()
    {
        var (tools, client) = Arrange(allowWrite: false);

        var output = await tools.RegionDelete(null!, null!, "arena", confirm: true);

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("write_disabled", error["code"]!.GetValue<string>());
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionDelete_ValidatesTheNameBeforeTheNetwork()
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionDelete(null!, null!, "");

        AssertInvalidArgument(output, "name");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionDelete_MapsAMissingRegion()
    {
        var (tools, client) = Arrange();
        client.Result = BridgeResult.Failure(
            "bridge_error",
            "The bridge returned HTTP 404 (region_not_found).",
            404) with { BridgeError = "region_not_found" };

        var output = await tools.RegionDelete(null!, null!, "nope", confirm: true);

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("region_not_found", error["code"]!.GetValue<string>());
        Assert.Equal(404, error["httpStatus"]!.GetValue<int>());
    }

    [Fact]
    public async Task RegionCreate_ReportsAnUnconfiguredBridge()
    {
        var tools = new BridgeTools(
            new RecordingBridgeClient
            {
                Result = BridgeResult.Failure("bridge_unavailable", "The TShock bridge is not configured.")
            },
            TestOptions.Create(allowWrite: true));

        var output = await tools.RegionCreate("arena", 10, 10, 20, 20);

        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(output))["error"]);
        Assert.Equal("bridge_unavailable", error["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task EventsSince_ReportsAnUnconfiguredBridge()
    {
        var tools = new BridgeTools(
            new RecordingBridgeClient
            {
                Result = BridgeResult.Failure("bridge_unavailable", "The TShock bridge is not configured.")
            },
            TestOptions.Create());

        var output = await tools.EventsSince();

        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("bridge_unavailable", error["code"]!.GetValue<string>());
        Assert.False(error.ContainsKey("httpStatus"));
    }

    [Fact]
    public async Task RegionInfo_AcceptsNameAtTheLimit()
    {
        var (tools, client) = Arrange();
        var name = new string('r', 128);

        await tools.RegionInfo(name);

        Assert.Equal($"/v1/regions/{name}", Assert.Single(client.Calls).Path);
    }

    [Fact]
    public async Task RegionCreate_RejectsOverlongName()
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionCreate(new string('r', 129), 10, 10, 20, 20);

        AssertInvalidArgument(output, "name");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionCreate_AcceptsOwnerAtTheLimit()
    {
        var (tools, client) = Arrange();
        var owner = new string('o', 128);

        await tools.RegionCreate("arena", 10, 10, 20, 20, owner: owner);

        Assert.Equal(owner, Assert.Single(client.Calls).Body!["owner"]!.GetValue<string>());
    }

    [Fact]
    public async Task RegionCreate_RejectsOverlongOwner()
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionCreate(
            "arena", 10, 10, 20, 20, owner: new string('o', 129));

        AssertInvalidArgument(output, "owner");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionCreate_RejectsControlCharactersInOwner()
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionCreate("arena", 10, 10, 20, 20, owner: "ali\nce");

        AssertInvalidArgument(output, "owner");
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RegionDelete_RejectsOverlongName()
    {
        var (tools, client) = Arrange();

        var output = await tools.RegionDelete(null!, null!, new string('r', 129), confirm: true);

        AssertInvalidArgument(output, "name");
        Assert.Empty(client.Calls);
    }
}
