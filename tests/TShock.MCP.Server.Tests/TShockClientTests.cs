using System.Net;
using System.Text.Json.Nodes;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

public sealed class TShockClientTests
{
    private const string Operation = "server_status";
    private const string Path = "/v2/server/status";

    private static Task<TShockResult> CallAsync(
        StubHttpMessageHandler handler,
        TShockOptions? options = null,
        string path = Path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default) =>
        TestOptions
            .CreateClient(handler, options ?? TestOptions.Create())
            .CallAsync(Operation, path, query, cancellationToken);

    private static StubHttpMessageHandler RespondWithReason(
        string body,
        HttpStatusCode statusCode,
        string reasonPhrase) =>
        new((_, _) =>
        {
            var response = StubHttpMessageHandler.BuildResponse(body, statusCode);
            response.ReasonPhrase = reasonPhrase;
            return Task.FromResult(response);
        });

    [Fact]
    public async Task CallAsync_AppendsTokenAndUrlEscapesValues()
    {
        const string token = "tok en+/=&";
        const string player = "a b&c=d";
        var options = TestOptions.Create(token: token);
        var handler = StubHttpMessageHandler.Respond("""{"ok":true}""");

        var result = await CallAsync(
            handler,
            options,
            path: "/v4/players/read",
            query: new Dictionary<string, string?> { ["player"] = player });

        Assert.True(result.Ok);
        var uri = Assert.IsType<Uri>(handler.LastRequestUri);
        var rawQuery = uri.Query;

        // The token travels as a single query parameter, escaped.
        Assert.Equal(1, QueryString.Count(uri, "token"));
        Assert.Equal(token, QueryString.Value(uri, "token"));
        Assert.Equal(player, QueryString.Value(uri, "player"));

        // Raw values must never appear literally in the URL.
        Assert.DoesNotContain(token, rawQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("a b&c=d", rawQuery, StringComparison.Ordinal);
        Assert.Contains("player=a%20b%26c%3Dd", rawQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_SkipsNullQueryValues()
    {
        var handler = StubHttpMessageHandler.Respond("""{"ok":true}""");

        await CallAsync(
            handler,
            query: new Dictionary<string, string?>
            {
                ["player"] = null,
                ["group"] = "default"
            });

        var uri = Assert.IsType<Uri>(handler.LastRequestUri);
        var keys = QueryString.Parse(uri).Select(pair => pair.Key).ToList();

        Assert.DoesNotContain("player", keys);
        Assert.Contains("group", keys);
        Assert.Equal("default", QueryString.Value(uri, "group"));
    }

    [Fact]
    public async Task CallAsync_CallerSuppliedTokenNeverOverridesConfiguredToken()
    {
        const string token = "configured-token";
        var handler = StubHttpMessageHandler.Respond("""{"ok":true}""");

        await CallAsync(
            handler,
            TestOptions.Create(token: token),
            query: new Dictionary<string, string?> { ["token"] = "attacker-token" });

        var uri = Assert.IsType<Uri>(handler.LastRequestUri);

        Assert.Equal(1, QueryString.Count(uri, "token"));
        Assert.Equal(token, QueryString.Value(uri, "token"));
        Assert.DoesNotContain("attacker-token", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_Parses200JsonBodyIntoSuccessResult()
    {
        var handler = StubHttpMessageHandler.Respond(
            """{"status":1,"name":"Terraria","players":2}""",
            HttpStatusCode.OK);

        var result = await CallAsync(handler);

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.ErrorMessage);

        var body = Assert.IsType<JsonObject>(result.Body);
        Assert.Equal("Terraria", body["name"]!.GetValue<string>());
        Assert.Equal(2, body["players"]!.GetValue<int>());
    }

    [Fact]
    public async Task CallAsync_TreatsErrorBodyOn200AsRestError()
    {
        var handler = StubHttpMessageHandler.Respond(
            """{"error":"Invalid REST token."}""",
            HttpStatusCode.OK);

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("rest_error", result.ErrorCode);
        Assert.Equal("Invalid REST token.", result.ErrorMessage);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task CallAsync_MapsForbiddenWithErrorBodyToRestError()
    {
        var handler = StubHttpMessageHandler.Respond(
            """{"error":"You do not have permission to use this endpoint."}""",
            HttpStatusCode.Forbidden);

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("rest_error", result.ErrorCode);
        Assert.Equal("You do not have permission to use this endpoint.", result.ErrorMessage);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task CallAsync_RedactsCredentialsAndIpsRecursively()
    {
        var handler = StubHttpMessageHandler.Respond(
            """
            {
              "name": "Terraria",
              "ip": "10.0.0.5",
              "token": "leaked-token",
              "nested": {
                "password": "hunter2",
                "ip": "10.0.0.6"
              },
              "players": [
                { "name": "alice", "ip": "10.0.0.7", "token": "leaked-token" },
                { "name": "bob", "password": "s3cret" }
              ]
            }
            """,
            HttpStatusCode.OK);

        var result = await CallAsync(handler);

        Assert.True(result.Ok);
        var body = Assert.IsType<JsonObject>(result.Body);

        Assert.Equal(ResponseRedactor.RedactedValue, body["ip"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, body["token"]!.GetValue<string>());

        var nested = Assert.IsType<JsonObject>(body["nested"]);
        Assert.Equal(ResponseRedactor.RedactedValue, nested["password"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, nested["ip"]!.GetValue<string>());

        var players = Assert.IsType<JsonArray>(body["players"]);
        var alice = Assert.IsType<JsonObject>(players[0]);
        Assert.Equal(ResponseRedactor.RedactedValue, alice["ip"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, alice["token"]!.GetValue<string>());
        Assert.Equal("alice", alice["name"]!.GetValue<string>());

        var bob = Assert.IsType<JsonObject>(players[1]);
        Assert.Equal(ResponseRedactor.RedactedValue, bob["password"]!.GetValue<string>());

        // Sanity: the raw secrets are gone from the serialized body.
        var serialized = body.ToJsonString();
        Assert.DoesNotContain("leaked-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.5", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_WithShowIpsKeepsIpButStillRedactsCredentials()
    {
        var handler = StubHttpMessageHandler.Respond(
            """{"ip":"10.0.0.5","token":"leaked-token","password":"hunter2"}""",
            HttpStatusCode.OK);

        var result = await CallAsync(handler, TestOptions.Create(showIps: true));

        var body = Assert.IsType<JsonObject>(result.Body);
        Assert.Equal("10.0.0.5", body["ip"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, body["token"]!.GetValue<string>());
        Assert.Equal(ResponseRedactor.RedactedValue, body["password"]!.GetValue<string>());
    }

    [Fact]
    public async Task CallAsync_InvalidJsonOn200MapsToInvalidResponse()
    {
        var handler = StubHttpMessageHandler.Respond("<html>not json</html>", HttpStatusCode.OK);

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("not valid JSON", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task CallAsync_SanitizesTokenInReasonPhrase()
    {
        const string token = "reason-secret-token";
        var handler = RespondWithReason(
            body: string.Empty,
            HttpStatusCode.InternalServerError,
            $"upstream rejected token={token}");

        var result = await CallAsync(handler, TestOptions.Create(token: token));

        Assert.False(result.Ok);
        Assert.Equal("rest_error", result.ErrorCode);
        Assert.Contains("***", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_CapsLongReasonPhraseAtFiveHundredCharacters()
    {
        var reasonPhrase = new string('R', 600);
        var handler = RespondWithReason(
            body: string.Empty,
            HttpStatusCode.InternalServerError,
            reasonPhrase);

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        var message = Assert.IsType<string>(result.ErrorMessage);

        // The whole reason phrase cannot be present, but a truncated prefix is.
        Assert.DoesNotContain(reasonPhrase, message, StringComparison.Ordinal);
        Assert.Contains(new string('R', 100), message, StringComparison.Ordinal);

        var longestRun = 0;
        var currentRun = 0;
        foreach (var character in message)
        {
            currentRun = character == 'R' ? currentRun + 1 : 0;
            longestRun = Math.Max(longestRun, currentRun);
        }

        Assert.InRange(longestRun, 1, 500);
    }

    [Fact]
    public async Task CallAsync_DuplicateJsonKeysOn200MapsToInvalidResponse()
    {
        var handler = StubHttpMessageHandler.Respond(
            """{"name":"Terraria","name":"OtherWorld"}""",
            HttpStatusCode.OK);

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("not valid JSON", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task CallAsync_DuplicateJsonKeysOnErrorStatusMapsToRestErrorWithSanitizedBody()
    {
        const string token = "duplicate-secret-token";
        var body = "{\"token\":\"" + token + "\",\"name\":\"a\",\"name\":\"b\"}";
        var handler = StubHttpMessageHandler.Respond(body, HttpStatusCode.InternalServerError);

        var result = await CallAsync(handler, TestOptions.Create(token: token));

        Assert.False(result.Ok);
        Assert.Equal("rest_error", result.ErrorCode);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("not valid JSON", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("***", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task CallAsync_NonStringErrorOn200MapsToRestError()
    {
        var handler = StubHttpMessageHandler.Respond(
            """{"error":{"msg":"boom"}}""",
            HttpStatusCode.OK);

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("rest_error", result.ErrorCode);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("""{"msg":"boom"}""", result.ErrorMessage);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task CallAsync_RejectsPathWithInlineQueryWithoutCallingTShock()
    {
        var handler = StubHttpMessageHandler.Respond("""{"ok":true}""");

        var result = await CallAsync(handler, path: "/v3/bans/list?token=attacker");

        Assert.False(result.Ok);
        Assert.Equal("unexpected_error", result.ErrorCode);
        Assert.Null(result.Body);
        Assert.Equal(0, handler.CallCount);
        Assert.Null(handler.LastRequestUri);
        Assert.DoesNotContain("attacker", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_TimeoutMapsToTimeout()
    {
        // A TaskCanceledException without a canceled token is how HttpClient
        // surfaces its own timeout.
        var handler = StubHttpMessageHandler.Throws(
            new TaskCanceledException("The request timed out."));

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("timeout", result.ErrorCode);
        Assert.Contains("15 seconds", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task CallAsync_CallerCancellationMapsToCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = StubHttpMessageHandler.Throws(new OperationCanceledException(cts.Token));

        var result = await CallAsync(handler, cancellationToken: cts.Token);

        Assert.False(result.Ok);
        Assert.Equal("cancelled", result.ErrorCode);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task CallAsync_HttpRequestExceptionMapsToNetworkErrorWithoutLeakingToken()
    {
        const string token = "super-secret-token";
        var options = TestOptions.Create(token: token);
        var message =
            $"Connection refused: http://127.0.0.1:7878/v2/server/status?token={token}" +
            $" (escaped: {Uri.EscapeDataString(token)})";
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(message));

        var result = await CallAsync(handler, options);

        Assert.False(result.Ok);
        Assert.Equal("network_error", result.ErrorCode);
        Assert.Contains("Could not reach the TShock server", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("***", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_ResponseLargerThanFiveMegabytesMapsToResponseTooLarge()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = StubHttpMessageHandler.BuildResponse("{}", HttpStatusCode.OK);
            response.Content.Headers.ContentLength = (6L * 1024 * 1024);
            return Task.FromResult(response);
        });

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("response_too_large", result.ErrorCode);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("5 MB", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_WritesAuditEntryWithMetadataButNoQueryValuesOrToken()
    {
        const string token = "audit-secret-token";
        const string player = "AliceThePlayer";
        var auditPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tshock-audit-{Guid.NewGuid():N}.jsonl");

        try
        {
            var handler = StubHttpMessageHandler.Respond("""{"ok":true}""");
            var options = TestOptions.Create(token: token, auditLogPath: auditPath);

            var result = await TestOptions
                .CreateClient(handler, options)
                .CallAsync(
                    "player_info",
                    "/v4/players/read",
                    new Dictionary<string, string?> { ["player"] = player });

            Assert.True(result.Ok);
            Assert.True(File.Exists(auditPath));

            var lines = await File.ReadAllLinesAsync(auditPath);
            var line = Assert.Single(lines);

            var entry = JsonNode.Parse(line) as JsonObject;
            Assert.NotNull(entry);
            Assert.True(entry.ContainsKey("ts"));
            Assert.True(entry.ContainsKey("operation"));
            Assert.True(entry.ContainsKey("path"));
            Assert.True(entry.ContainsKey("status"));
            Assert.True(entry.ContainsKey("ok"));
            Assert.True(entry.ContainsKey("elapsedMs"));
            Assert.True(entry.ContainsKey("errorCode"));

            Assert.Equal("player_info", entry["operation"]!.GetValue<string>());
            Assert.Equal("/v4/players/read", entry["path"]!.GetValue<string>());
            Assert.Equal(200, entry["status"]!.GetValue<int>());
            Assert.True(entry["ok"]!.GetValue<bool>());
            Assert.True(entry["elapsedMs"]!.GetValue<long>() >= 0);
            Assert.Null(entry["errorCode"]);
            Assert.True(
                DateTime.TryParse(
                    entry["ts"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _));

            // The audit trail records metadata only: never query values or the token.
            Assert.DoesNotContain(player, line, StringComparison.Ordinal);
            Assert.DoesNotContain(token, line, StringComparison.Ordinal);
            Assert.DoesNotContain("player=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", line, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(auditPath))
            {
                File.Delete(auditPath);
            }
        }
    }
}
