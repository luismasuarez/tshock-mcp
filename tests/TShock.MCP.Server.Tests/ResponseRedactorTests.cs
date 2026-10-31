using System.Text.Json.Nodes;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

public sealed class ResponseRedactorTests
{
    [Fact]
    public void Redact_RedactsNestedObjectsAndArrays()
    {
        var node = JsonNode.Parse(
            """
            {
              "ip": "10.0.0.1",
              "token": "abc",
              "password": "def",
              "server": {
                "ip": "10.0.0.2",
                "owner": { "password": "nested" }
              },
              "accounts": [
                { "ip": "10.0.0.3", "token": "ghi" },
                [
                  { "password": "deep-in-array" }
                ]
              ]
            }
            """);

        var redacted = ResponseRedactor.Redact(node, redactIps: true);

        Assert.Same(node, redacted);
        var root = Assert.IsType<JsonObject>(redacted);

        Assert.Equal(ResponseRedactor.RedactedValue, root["ip"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, root["token"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, root["password"]!.GetValue<string>());

        var server = Assert.IsType<JsonObject>(root["server"]);
        Assert.Equal(ResponseRedactor.RedactedValue, server["ip"]!.GetValue<string>());
        var owner = Assert.IsType<JsonObject>(server["owner"]);
        Assert.Equal(ResponseRedactor.RedactedValue, owner["password"]!.GetValue<string>());

        var accounts = Assert.IsType<JsonArray>(root["accounts"]);
        var account = Assert.IsType<JsonObject>(accounts[0]);
        Assert.Equal(ResponseRedactor.RedactedValue, account["ip"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, account["token"]!.GetValue<string>());

        var nestedArray = Assert.IsType<JsonArray>(accounts[1]);
        var deep = Assert.IsType<JsonObject>(nestedArray[0]);
        Assert.Equal(ResponseRedactor.RedactedValue, deep["password"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_MatchesSensitiveKeysCaseInsensitively()
    {
        var node = JsonNode.Parse(
            """{"IP":"10.0.0.1","Token":"abc","PassWord":"def","userName":"alice"}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: true));

        Assert.Equal(ResponseRedactor.RedactedValue, redacted["IP"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["Token"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["PassWord"]!.GetValue<string>());
        Assert.Equal("alice", redacted["userName"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_WithRedactIpsFalse_KeepsIpButRedactsCredentials()
    {
        var node = JsonNode.Parse(
            """{"ip":"10.0.0.1","token":"abc","password":"def","nested":{"ip":"10.0.0.2"}}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: false));

        Assert.Equal("10.0.0.1", redacted["ip"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["token"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["password"]!.GetValue<string>());

        var nested = Assert.IsType<JsonObject>(redacted["nested"]);
        Assert.Equal("10.0.0.2", nested["ip"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_RedactsBanIdentifierThatIsAnIpAddress()
    {
        // TShock's /v3/bans endpoints report the target in an "identifier"
        // field that can be an IP address, not in an "ip" field.
        var node = JsonNode.Parse(
            """{"identifier":"203.0.113.9","name":"alice","ipBan":true}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: true));

        Assert.Equal(ResponseRedactor.RedactedValue, redacted["identifier"]!.GetValue<string>());
        Assert.Equal("alice", redacted["name"]!.GetValue<string>());
        Assert.True(redacted["ipBan"]!.GetValue<bool>());
    }

    [Fact]
    public void Redact_RedactsBanIdentifierThatIsAnIpv6Address()
    {
        var node = JsonNode.Parse("""{"identifier":"2001:db8::1","reason":"griefing"}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: true));

        Assert.Equal(ResponseRedactor.RedactedValue, redacted["identifier"]!.GetValue<string>());
        Assert.Equal("griefing", redacted["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_KeepsVersionStringsThatLookLikeIpv4Addresses()
    {
        // "1.4.4.9" parses as an IPv4 address, but it is a Terraria version.
        var node = JsonNode.Parse(
            """{"serverversion":"1.4.4.9","tshockversion":"6.2.1.0","name":"World"}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: true));

        Assert.Equal("1.4.4.9", redacted["serverversion"]!.GetValue<string>());
        Assert.Equal("6.2.1.0", redacted["tshockversion"]!.GetValue<string>());
        Assert.Equal("World", redacted["name"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_RedactsAddressLikeIdentityFields()
    {
        var node = JsonNode.Parse(
            """{"address":"198.51.100.7","host":"198.51.100.8","endpoint":"example.org"}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: true));

        Assert.Equal(ResponseRedactor.RedactedValue, redacted["address"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["host"]!.GetValue<string>());
        Assert.Equal("example.org", redacted["endpoint"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_KeepsBanIdentifierThatIsNotAnIpAddress()
    {
        var node = JsonNode.Parse(
            """{"identifier":"abc-uuid","players":["alice","10.0.0.9"],"count":203}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: true));

        Assert.Equal("abc-uuid", redacted["identifier"]!.GetValue<string>());
        Assert.Equal("alice", redacted["players"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["players"]!.AsArray()[1]!.GetValue<string>());
        Assert.Equal(203, redacted["count"]!.GetValue<int>());
    }

    [Fact]
    public void Redact_WithRedactIpsFalse_KeepsIpIdentifierButRedactsCredentials()
    {
        var node = JsonNode.Parse(
            """{"identifier":"203.0.113.9","token":"abc","password":"def"}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node, redactIps: false));

        Assert.Equal("203.0.113.9", redacted["identifier"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["token"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, redacted["password"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_KeepsUnrelatedFieldsUntouched()
    {
        var node = JsonNode.Parse(
            """{"name":"World","players":["alice","bob"],"count":2,"online":true,"nothing":null}""");

        var redacted = Assert.IsType<JsonObject>(ResponseRedactor.Redact(node));

        Assert.Equal("World", redacted["name"]!.GetValue<string>());
        Assert.Equal(2, redacted["count"]!.GetValue<int>());
        Assert.True(redacted["online"]!.GetValue<bool>());
        Assert.Null(redacted["nothing"]);
        Assert.Equal(2, redacted["players"]!.AsArray().Count);
    }

    [Fact]
    public void Redact_ReturnsNullForNullNode()
    {
        Assert.Null(ResponseRedactor.Redact(null));
    }
}
