using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TShock.AgentBridge.Tests;

/// <summary>
/// Locks the wire casing of the bridge. Events use explicit JSON names, so the
/// CamelCase policy applies only to plain records such as region DTOs; without
/// this guard a PascalCase contract can leak to the gateway again.
/// </summary>
public sealed class SerializationContractTests
{
    private static JsonObject Serialize<T>(T value) =>
        JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(value, BridgeServer.SerializerOptions))!
            .AsObject();

    [Fact]
    public void RegionDto_SerializesWithCamelCaseKeys()
    {
        var dto = new RegionDto(
            Id: 7,
            Name: "spawn",
            X: 100,
            Y: 200,
            Width: 40,
            Height: 20,
            Owner: "mcp-gateway",
            DisableBuild: true,
            Z: 0,
            AllowedIds: [1, 2],
            AllowedGroups: ["guest"]);

        var json = Serialize(dto);

        Assert.Equal(7, json["id"]!.GetValue<int>());
        Assert.Equal("spawn", json["name"]!.GetValue<string>());
        Assert.Equal("mcp-gateway", json["owner"]!.GetValue<string>());
        Assert.True(json["disableBuild"]!.GetValue<bool>());
        Assert.Equal(2, json["allowedIds"]!.AsArray().Count);
        Assert.False(json.ContainsKey("Name"));
    }

    [Fact]
    public void EventPage_KeepsTheExplicitSnakeAndCamelNames()
    {
        var page = new EventPageResponse(
            Events:
            [
                new BridgeEvent(
                    Cursor: 1,
                    At: "2026-11-01T17:01:59.554Z",
                    Type: "player.join",
                    Untrusted: true,
                    Data: new Dictionary<string, object?> { ["player"] = "Alice" })
            ],
            Cursor: 1,
            Head: 1,
            Oldest: 1,
            Limit: 100,
            HasMore: false,
            Session: "abc");

        var json = Serialize(page);

        Assert.Equal(1, json["cursor"]!.GetValue<int>());
        Assert.True(json["hasMore"] is not null);
        Assert.False(json.ContainsKey("HasMore"));
        var evt = json["events"]!.AsArray()[0]!.AsObject();
        Assert.Equal("player.join", evt["type"]!.GetValue<string>());
        Assert.True(evt["untrusted"]!.GetValue<bool>());
    }
}
