using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TShock.McpServer.TShock;

/// <summary>
/// <see cref="IBridgeClient"/> over the loopback AgentBridge plugin. The bridge
/// token travels as a bearer header, never in the URL, and every failure is mapped
/// to one of the stable codes documented in <c>docs/testing.md</c>.
/// </summary>
public sealed class BridgeClient : IBridgeClient
{
    private const string UnreachablePrefix = "Could not reach the TShock bridge: ";
    private const string NotConfiguredMessage =
        "The TShock bridge is not configured. Set TSHOCK_BRIDGE_URL and TSHOCK_BRIDGE_TOKEN " +
        "(or TSHOCK_BRIDGE_TOKEN_FILE) to enable bridge tools.";

    private readonly HttpClient _http;
    private readonly TShockOptions _options;
    private readonly ILogger<BridgeClient> _logger;
    private readonly AuditLog _audit;

    public BridgeClient(HttpClient http, TShockOptions options, ILogger<BridgeClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _audit = new AuditLog(options, logger);
    }

    /// <inheritdoc />
    public bool IsConfigured => _options.Bridge is not null;

    /// <inheritdoc />
    public Task<BridgeResult> GetAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, operation, path, query, null, cancellationToken);

    /// <inheritdoc />
    public Task<BridgeResult> PostAsync(
        string operation,
        string path,
        JsonNode? body,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, operation, path, null, body, cancellationToken);

    /// <inheritdoc />
    public Task<BridgeResult> DeleteAsync(
        string operation,
        string path,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, operation, path, null, null, cancellationToken);

    private async Task<BridgeResult> SendAsync(
        HttpMethod method,
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        JsonNode? payload,
        CancellationToken cancellationToken)
    {
        var auditPath = HttpPayload.StripQuery(path);
        var stopwatch = Stopwatch.StartNew();

        BridgeResult result;
        if (_options.Bridge is not { } bridge)
        {
            result = BridgeResult.Failure(ErrorCodes.BridgeUnavailable, NotConfiguredMessage);
        }
        else if (path.IndexOf('?', StringComparison.Ordinal) >= 0)
        {
            // Same rule as the REST client: a caller must not smuggle a query in
            // the path, where it would bypass encoding.
            result = BridgeResult.Failure(
                ErrorCodes.UnexpectedError,
                "The bridge client rejected a path that contains an inline query string.");
        }
        else
        {
            try
            {
                result = await SendCoreAsync(bridge, method, BuildUri(bridge, path, query), payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = MapException(ex, cancellationToken);
            }
        }

        stopwatch.Stop();
        var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        _audit.Write(operation, auditPath, result.StatusCode, result.Ok, elapsedMilliseconds, result.ErrorCode);

        if (result.Ok)
        {
            _logger.LogDebug(
                "Bridge {Operation} {Method} {Path} succeeded with HTTP {StatusCode} in {ElapsedMs} ms.",
                method.Method,
                operation,
                auditPath,
                result.StatusCode,
                elapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "Bridge {Operation} {Method} {Path} failed with {ErrorCode} (HTTP {StatusCode}) in {ElapsedMs} ms.",
                method.Method,
                operation,
                auditPath,
                result.ErrorCode,
                result.StatusCode,
                elapsedMilliseconds);
        }

        return result;
    }

    /// <summary>
    /// Builds the request URI from the configured base address, the route and the
    /// caller's query parameters. The bridge token never appears in the URL.
    /// </summary>
    internal Uri BuildUri(BridgeOptions bridge, string path, IReadOnlyDictionary<string, string?>? query)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(bridge.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'));
        builder.Append('/');
        builder.Append(HttpPayload.StripQuery(path).TrimStart('/'));

        var parameters = new List<string>();
        if (query is not null)
        {
            foreach (var (key, value) in query)
            {
                if (value is null)
                {
                    continue;
                }

                parameters.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        if (parameters.Count > 0)
        {
            builder.Append('?');
            builder.Append(string.Join('&', parameters));
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private async Task<BridgeResult> SendCoreAsync(
        BridgeOptions bridge,
        HttpMethod method,
        Uri uri,
        JsonNode? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);
        if (payload is not null)
        {
            request.Content = new StringContent(
                payload.ToJsonString(),
                System.Text.Encoding.UTF8,
                "application/json");
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var statusCode = (int)response.StatusCode;

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > HttpPayload.MaxResponseBytes)
        {
            return BridgeResult.Failure(
                ErrorCodes.ResponseTooLarge,
                "The bridge response exceeded the 5 MB limit.",
                statusCode);
        }

        var body = await HttpPayload.ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return BridgeResult.Failure(
                ErrorCodes.ResponseTooLarge,
                "The bridge response exceeded the 5 MB limit.",
                statusCode);
        }

        JsonNode? node = null;
        var text = body.Trim();
        if (text.Length > 0)
        {
            try
            {
                node = JsonNode.Parse(text);
                HttpPayload.ValidateNoDuplicateKeys(node);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                return BridgeResult.Failure(
                    ErrorCodes.InvalidResponse,
                    $"The bridge returned HTTP {statusCode} with a body that is not valid JSON.",
                    statusCode);
            }
        }

        if (statusCode == (int)HttpStatusCode.Gone)
        {
            return MapCursorExpired(node);
        }

        // Only 401 means the bearer token was rejected. A 403 is a policy answer
        // from the bridge (for example owner_not_allowed) and must reach the tool
        // intact, so it is not collapsed into bridge_auth_error.
        if (statusCode == (int)HttpStatusCode.Unauthorized)
        {
            return BridgeResult.Failure(
                ErrorCodes.BridgeAuthError,
                $"The bridge rejected the request with HTTP {statusCode}. Check TSHOCK_BRIDGE_TOKEN.",
                statusCode);
        }

        if (statusCode is < 200 or >= 300)
        {
            return BridgeResult.Failure(ErrorCodes.BridgeError, DescribeError(statusCode, node), statusCode)
                with
                { BridgeError = ReadBridgeError(node) };
        }

        // A successful DELETE answers 204 with no body.
        if (node is null)
        {
            return statusCode == (int)HttpStatusCode.NoContent
                ? BridgeResult.Success(statusCode, null)
                : BridgeResult.Failure(
                    ErrorCodes.InvalidResponse,
                    $"The bridge returned HTTP {statusCode} with an empty body.",
                    statusCode);
        }

        return BridgeResult.Success(statusCode, node);
    }

    /// <summary>
    /// Turns a <c>410 cursor_expired</c> response into a stable error. The bridge
    /// body is reused as the machine-readable details so an agent can resume from
    /// <c>resumeCursor</c> without parsing prose.
    /// </summary>
    private static BridgeResult MapCursorExpired(JsonNode? node)
    {
        var details = node?.DeepClone();
        string? reason = null;
        string? resumeCursor = null;

        if (details is JsonObject jsonObject)
        {
            jsonObject.Remove("error");
            reason = ReadString(jsonObject["reason"]);
            resumeCursor = jsonObject["resumeCursor"]?.ToJsonString();
        }

        var message = resumeCursor is null
            ? "The event cursor is no longer available. Start a new read without a cursor."
            : $"The event cursor is no longer available (reason: {reason ?? "unknown"}). " +
              $"Resume from cursor {resumeCursor}.";

        return BridgeResult.Failure(ErrorCodes.CursorExpired, message, (int)HttpStatusCode.Gone, details);
    }

    private static string DescribeError(int statusCode, JsonNode? node)
    {
        if (node is JsonObject jsonObject && ReadString(jsonObject["error"]) is { } errorText)
        {
            return $"The bridge returned HTTP {statusCode} ({errorText}).";
        }

        return $"The bridge returned HTTP {statusCode}.";
    }

    private static string? ReadBridgeError(JsonNode? node) =>
        node is JsonObject jsonObject ? ReadString(jsonObject["error"]) : null;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private BridgeResult MapException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            // Distinguish a caller cancellation from an HttpClient timeout: both
            // surface as TaskCanceledException.
            return cancellationToken.IsCancellationRequested
                ? BridgeResult.Failure(ErrorCodes.Cancelled, "The bridge call was cancelled by the caller.")
                : BridgeResult.Failure(
                    ErrorCodes.BridgeTimeout,
                    $"The bridge did not respond within {_options.Bridge?.Timeout.TotalSeconds ?? 0:0} seconds.");
        }

        if (exception is HttpRequestException)
        {
            return BridgeResult.Failure(
                ErrorCodes.BridgeUnavailable,
                HttpPayload.Truncate(UnreachablePrefix + Sanitize(exception.Message)));
        }

        _logger.LogWarning(
            "Bridge call failed with an unexpected {ExceptionType}: {Message}",
            exception.GetType().Name,
            HttpPayload.Truncate(Sanitize(exception.Message)));

        return BridgeResult.Failure(ErrorCodes.UnexpectedError, exception.GetType().Name);
    }

    /// <summary>
    /// Removes both tokens and both base URIs from a transport error message.
    /// </summary>
    private string Sanitize(string message)
    {
        var sanitized = message;

        foreach (var secret in new[] { _options.Token, _options.Bridge?.Token })
        {
            if (!string.IsNullOrEmpty(secret))
            {
                sanitized = sanitized
                    .Replace(secret, "***", StringComparison.Ordinal)
                    .Replace(Uri.EscapeDataString(secret), "***", StringComparison.Ordinal);
            }
        }

        foreach (var baseUrl in new[] { _options.BaseUrl, _options.Bridge?.BaseUrl })
        {
            if (baseUrl is null)
            {
                continue;
            }

            sanitized = sanitized
                .Replace(baseUrl.ToString(), "<base-url>", StringComparison.Ordinal)
                .Replace(baseUrl.GetLeftPart(UriPartial.Authority), "<base-url>", StringComparison.Ordinal);
        }

        return sanitized;
    }
}
