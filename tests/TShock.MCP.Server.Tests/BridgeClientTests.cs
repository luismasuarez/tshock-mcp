using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

public sealed class BridgeClientTests
{
    private const string Operation = "events_since";
    private const string Path = "/v1/events";

    private static Task<BridgeResult> GetAsync(
        StubHttpMessageHandler handler,
        TShockOptions? options = null,
        string path = Path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default) =>
        TestOptions
            .CreateBridgeClient(handler, options ?? TestOptions.Create(bridge: TestOptions.CreateBridge()))
            .GetAsync(Operation, path, query, cancellationToken);

    private static StubHttpMessageHandler Respond(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        StubHttpMessageHandler.Respond(body, statusCode);

    [Fact]
    public async Task GetAsync_WhenBridgeIsNotConfigured_ReturnsUnavailableWithoutNetwork()
    {
        var handler = Respond("""{"events":[]}""");
        var options = TestOptions.Create(bridge: null);

        var result = await GetAsync(handler, options);

        Assert.False(result.Ok);
        Assert.Equal("bridge_unavailable", result.ErrorCode);
        Assert.Contains("not configured", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheParsedBody()
    {
        var handler = Respond("""{"events":[{"cursor":1,"type":"chat"}],"cursor":1,"head":1,"hasMore":false}""");

        var result = await GetAsync(handler);

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        var body = Assert.IsType<JsonObject>(result.Body);
        Assert.Equal(1, body["cursor"]!.GetValue<long>());
        Assert.Single(Assert.IsType<JsonArray>(body["events"]));
    }

    [Fact]
    public async Task GetAsync_SendsBearerTokenAsHeaderAndNeverInTheUrl()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(StubHttpMessageHandler.BuildResponse("{}", HttpStatusCode.OK));
        });

        await GetAsync(
            handler,
            query: new Dictionary<string, string?> { ["cursor"] = "12", ["limit"] = "50" });

        var request = Assert.IsType<HttpRequestMessage>(captured);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(TestOptions.BridgeToken, request.Headers.Authorization?.Parameter);

        var uri = Assert.IsType<Uri>(request.RequestUri);
        Assert.Equal("12", QueryString.Value(uri, "cursor"));
        Assert.Equal("50", QueryString.Value(uri, "limit"));
        Assert.DoesNotContain(TestOptions.BridgeToken, uri.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_OmitsNullQueryValues()
    {
        var handler = Respond("{}");

        await GetAsync(
            handler,
            query: new Dictionary<string, string?> { ["cursor"] = null, ["limit"] = "5" });

        var uri = Assert.IsType<Uri>(handler.LastRequestUri);
        Assert.Equal(0, QueryString.Count(uri, "cursor"));
        Assert.Equal("5", QueryString.Value(uri, "limit"));
    }

    [Fact]
    public async Task GetAsync_RejectsAnInlineQueryInThePath()
    {
        var handler = Respond("{}");

        var result = await GetAsync(handler, path: "/v1/events?cursor=1");

        Assert.False(result.Ok);
        Assert.Equal("unexpected_error", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetAsync_MapsAuthenticationFailures(HttpStatusCode statusCode)
    {
        var handler = Respond("""{"error":"unauthorized"}""", statusCode);

        var result = await GetAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("bridge_auth_error", result.ErrorCode);
        Assert.Equal((int)statusCode, result.StatusCode);
        Assert.DoesNotContain(TestOptions.BridgeToken, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_MapsOtherErrorsToBridgeError()
    {
        var handler = Respond("""{"error":"region_not_found"}""", HttpStatusCode.NotFound);

        var result = await GetAsync(handler, path: "/v1/regions/spawn");

        Assert.False(result.Ok);
        Assert.Equal("bridge_error", result.ErrorCode);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("region_not_found", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_MapsCursorExpiredAndKeepsResumeDetails()
    {
        const string body = """
            {"error":"cursor_expired","reason":"evicted","oldest":9501,"head":10020,
             "resumeCursor":9500,"session":"abc123"}
            """;
        var handler = Respond(body, HttpStatusCode.Gone);

        var result = await GetAsync(handler, query: new Dictionary<string, string?> { ["cursor"] = "12" });

        Assert.False(result.Ok);
        Assert.Equal("cursor_expired", result.ErrorCode);
        Assert.Equal(410, result.StatusCode);

        var details = Assert.IsType<JsonObject>(result.ErrorDetails);
        Assert.Equal(9500, details["resumeCursor"]!.GetValue<long>());
        Assert.Equal("evicted", details["reason"]!.GetValue<string>());
        Assert.Equal(9501, details["oldest"]!.GetValue<long>());
        Assert.Equal(10020, details["head"]!.GetValue<long>());
        Assert.Equal("abc123", details["session"]!.GetValue<string>());

        // The bridge's "error" discriminator is not repeated inside the details.
        Assert.False(details.ContainsKey("error"));
        Assert.Contains("9500", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_MapsCursorExpiredWithAnEmptyBody()
    {
        var handler = Respond(string.Empty, HttpStatusCode.Gone);

        var result = await GetAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("cursor_expired", result.ErrorCode);
        Assert.Null(result.ErrorDetails);
    }

    [Fact]
    public async Task GetAsync_MapsTimeoutToBridgeTimeout()
    {
        var handler = StubHttpMessageHandler.Throws(new TaskCanceledException("timeout"));

        var result = await GetAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("bridge_timeout", result.ErrorCode);
        Assert.Contains("10 seconds", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_MapsCallerCancellationToCancelled()
    {
        var handler = StubHttpMessageHandler.Throws(new TaskCanceledException("cancelled"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await GetAsync(handler, cancellationToken: cancellation.Token);

        Assert.False(result.Ok);
        Assert.Equal("cancelled", result.ErrorCode);
    }

    [Fact]
    public async Task GetAsync_MapsConnectionFailuresToUnavailable()
    {
        var handler = StubHttpMessageHandler.Throws(
            new HttpRequestException("Connection refused (http://127.0.0.1:7879/v1/events)"));

        var result = await GetAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("bridge_unavailable", result.ErrorCode);
        Assert.Contains("Could not reach", result.ErrorMessage, StringComparison.Ordinal);

        // The base URL is scrubbed from transport messages.
        Assert.DoesNotContain("http://127.0.0.1:7879", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("<base-url>", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_MapsInvalidJsonToInvalidResponse()
    {
        var handler = Respond("<html>not json</html>");

        var result = await GetAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task GetAsync_RejectsResponsesOverTheSizeLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"))
            };
            response.Content.Headers.ContentLength = 6 * 1024 * 1024;
            return Task.FromResult(response);
        });

        var result = await GetAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("response_too_large", result.ErrorCode);
    }

    [Fact]
    public async Task GetAsync_RejectsDuplicateJsonKeys()
    {
        var handler = Respond("""{"cursor":1,"cursor":2}""");

        var result = await GetAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal("invalid_response", result.ErrorCode);
    }
}
