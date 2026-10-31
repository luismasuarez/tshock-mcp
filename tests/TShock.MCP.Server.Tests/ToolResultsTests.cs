using System.Text.Json.Nodes;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

public sealed class ToolResultsTests
{
    [Fact]
    public void Ok_WrapsDataInStableEnvelope()
    {
        var json = ToolResults.Ok(new JsonObject { ["name"] = "Terraria" });
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(json));

        Assert.Equal(2, envelope.Count);
        Assert.True(envelope["ok"]!.GetValue<bool>());
        var data = Assert.IsType<JsonObject>(envelope["data"]);
        Assert.Equal("Terraria", data["name"]!.GetValue<string>());
        Assert.False(envelope.ContainsKey("error"));
    }

    [Fact]
    public void Ok_WithNullDataKeepsDataProperty()
    {
        var json = ToolResults.Ok(null);
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(json));

        Assert.True(envelope["ok"]!.GetValue<bool>());
        Assert.True(envelope.ContainsKey("data"));
        Assert.Null(envelope["data"]);
    }

    [Fact]
    public void Error_OmitsHttpStatusWhenNotPositive()
    {
        var json = ToolResults.Error("invalid_argument", "player must not be empty.");
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(json));

        Assert.Equal(2, envelope.Count);
        Assert.False(envelope["ok"]!.GetValue<bool>());

        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("invalid_argument", error["code"]!.GetValue<string>());
        Assert.Equal("player must not be empty.", error["message"]!.GetValue<string>());
        Assert.False(error.ContainsKey("httpStatus"));
        Assert.Equal(2, error.Count);
    }

    [Fact]
    public void Error_IncludesHttpStatusWhenPositive()
    {
        var json = ToolResults.Error("rest_error", "Forbidden", 403);
        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(json))["error"]);

        Assert.Equal(403, error["httpStatus"]!.GetValue<int>());
        Assert.Equal(3, error.Count);
    }

    [Fact]
    public void From_SuccessMapsBodyToOkEnvelope()
    {
        var json = ToolResults.From(
            TShockResult.Success(200, new JsonObject { ["status"] = 1 }));
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(json));

        Assert.True(envelope["ok"]!.GetValue<bool>());
        Assert.Equal(1, Assert.IsType<JsonObject>(envelope["data"])["status"]!.GetValue<int>());
    }

    [Fact]
    public void From_FailureMapsCodeMessageAndStatus()
    {
        var json = ToolResults.From(TShockResult.RestError(403, "Forbidden"));
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(json));

        Assert.False(envelope["ok"]!.GetValue<bool>());
        var error = Assert.IsType<JsonObject>(envelope["error"]);
        Assert.Equal("rest_error", error["code"]!.GetValue<string>());
        Assert.Equal("Forbidden", error["message"]!.GetValue<string>());
        Assert.Equal(403, error["httpStatus"]!.GetValue<int>());
    }

    [Fact]
    public void From_TransportErrorWithoutStatusOmitsHttpStatus()
    {
        var json = ToolResults.From(
            TShockResult.TransportError("network_error", "Could not reach the server."));
        var error = Assert.IsType<JsonObject>(
            Assert.IsType<JsonObject>(JsonNode.Parse(json))["error"]);

        Assert.Equal("network_error", error["code"]!.GetValue<string>());
        Assert.False(error.ContainsKey("httpStatus"));
    }
}
