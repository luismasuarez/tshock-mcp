using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TShock.McpServer.TShock;

/// <summary>
/// <see cref="ITShockClient"/> implementation over TShock's REST API.
/// Every TShock route is a GET that carries the token as a query parameter, so
/// the token is injected here and scrubbed from anything that leaves the client.
/// </summary>
public sealed class TShockClient : ITShockClient
{
    private const string TooLargeMessage = "The TShock response exceeded the 5 MB limit.";
    private const string NetworkErrorPrefix = "Could not reach the TShock server: ";

    private readonly HttpClient _http;
    private readonly TShockOptions _options;
    private readonly ILogger<TShockClient> _logger;
    private readonly AuditLog _audit;

    public TShockClient(HttpClient http, TShockOptions options, ILogger<TShockClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _audit = new AuditLog(options, logger);
    }

    /// <inheritdoc />
    public async Task<TShockResult> CallAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        // The audit trail records the route only, never the query values.
        var auditPath = HttpPayload.StripQuery(path);
        var stopwatch = Stopwatch.StartNew();

        TShockResult result;
        if (path.IndexOf('?', StringComparison.Ordinal) >= 0)
        {
            // Reject inline query strings before any network call: a path such as
            // "/v3/bans/list?token=attacker" must never reach TShock.
            result = TShockResult.TransportError(
                ErrorCodes.UnexpectedError,
                "The TShock client rejected a path that contains an inline query string.");
        }
        else
        {
            try
            {
                var uri = BuildUri(path, query);
                result = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
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
                "TShock {Operation} GET {Path} succeeded with HTTP {StatusCode} in {ElapsedMs} ms.",
                operation,
                auditPath,
                result.StatusCode,
                elapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "TShock {Operation} GET {Path} failed with {ErrorCode} (HTTP {StatusCode}) in {ElapsedMs} ms.",
                operation,
                auditPath,
                result.ErrorCode,
                result.StatusCode,
                elapsedMilliseconds);
        }

        return result;
    }

    /// <summary>
    /// Builds the request URI without duplicating slashes and appends the token
    /// last, so a caller-supplied value can never override it. Callers must not
    /// embed a query in <paramref name="path"/>; <see cref="CallAsync"/> rejects
    /// it, and any query text is stripped here as a second line of defense.
    /// </summary>
    internal Uri BuildUri(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var builder = new StringBuilder();
        builder.Append(_options.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'));
        builder.Append('/');

        builder.Append(HttpPayload.StripQuery(path).TrimStart('/'));

        var parameters = new List<string>();
        if (query is not null)
        {
            foreach (var (key, value) in query)
            {
                if (value is null || string.Equals(key, "token", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parameters.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        parameters.Add($"token={Uri.EscapeDataString(_options.Token)}");

        builder.Append('?');
        builder.Append(string.Join('&', parameters));

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private async Task<TShockResult> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var statusCode = (int)response.StatusCode;

        // The reason phrase is server-controlled text: it may echo the token and
        // can be arbitrarily long, so it is sanitized and capped before use.
        var reasonPhrase = HttpPayload.Truncate(
            Sanitize(
                string.IsNullOrWhiteSpace(response.ReasonPhrase)
                    ? response.StatusCode.ToString()
                    : response.ReasonPhrase));

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > HttpPayload.MaxResponseBytes)
        {
            return TShockResult.TransportError(ErrorCodes.ResponseTooLarge, TooLargeMessage, statusCode);
        }

        var body = await HttpPayload.ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return TShockResult.TransportError(ErrorCodes.ResponseTooLarge, TooLargeMessage, statusCode);
        }

        var isSuccess = statusCode is >= 200 and < 300;
        var payload = body.Trim();

        if (payload.Length == 0)
        {
            return isSuccess
                ? TShockResult.Success(statusCode, null)
                : TShockResult.RestError(
                    statusCode,
                    $"TShock returned HTTP {statusCode} ({reasonPhrase}) with an empty body.");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
            HttpPayload.ValidateNoDuplicateKeys(node);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // JsonNode reports duplicate property keys with ArgumentException, not
            // JsonException (on .NET 8 only once the object is materialized), so
            // both failures share the same mapping.
            var detail = HttpPayload.Truncate(
                $"TShock returned HTTP {statusCode} ({reasonPhrase}) with a body that is not valid JSON: {Sanitize(payload)}");

            return isSuccess
                ? TShockResult.TransportError(ErrorCodes.InvalidResponse, detail, statusCode)
                : TShockResult.RestError(statusCode, detail);
        }

        // TShock reports errors in the body; a 200 can still carry an "error".
        if (node is JsonObject errorObject && TryGetError(errorObject, out var errorMessage))
        {
            return TShockResult.RestError(
                statusCode,
                HttpPayload.Truncate(
                    string.IsNullOrWhiteSpace(errorMessage)
                        ? $"TShock returned HTTP {statusCode} ({reasonPhrase})."
                        : Sanitize(errorMessage)));
        }

        if (!isSuccess)
        {
            return TShockResult.RestError(statusCode, $"TShock returned HTTP {statusCode} ({reasonPhrase}).");
        }

        return TShockResult.Success(statusCode, ResponseRedactor.Redact(node, redactIps: !_options.ShowIps));
    }

    private static bool TryGetError(JsonObject jsonObject, out string? message)
    {
        if (!jsonObject.TryGetPropertyValue("error", out var errorNode))
        {
            message = null;
            return false;
        }

        // TShock normally sends an "error" string, but a proxy or a future
        // version may send an object or an array. Any of them is still an error.
        if (errorNode is JsonValue errorValue && errorValue.TryGetValue<string>(out var errorText))
        {
            message = errorText;
            return true;
        }

        // A JSON null error is treated as an absent message so the caller falls
        // back to the generic HTTP description.
        message = errorNode?.ToJsonString();
        return errorNode is not null;
    }

    private TShockResult MapException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? TShockResult.TransportError(ErrorCodes.Cancelled, "The TShock call was cancelled by the caller.")
                : TShockResult.TransportError(
                    ErrorCodes.Timeout,
                    $"TShock did not respond within {_options.Timeout.TotalSeconds:0} seconds.");
        }

        if (exception is HttpRequestException)
        {
            return TShockResult.TransportError(
                ErrorCodes.NetworkError,
                HttpPayload.Truncate(NetworkErrorPrefix + Sanitize(exception.Message)));
        }

        // Diagnose the unexpected failure without the stack trace and without the
        // token or base URL: only the exception type and a sanitized message.
        _logger.LogWarning(
            "TShock call failed with an unexpected {ExceptionType}: {Message}",
            exception.GetType().Name,
            HttpPayload.Truncate(Sanitize(exception.Message)));

        return TShockResult.TransportError(ErrorCodes.UnexpectedError, exception.GetType().Name);
    }

    /// <summary>
    /// Removes the token and the base URI from a transport error message.
    /// </summary>
    private string Sanitize(string message)
    {
        var sanitized = message;

        if (!string.IsNullOrEmpty(_options.Token))
        {
            sanitized = sanitized.Replace(_options.Token, "***", StringComparison.Ordinal);
            sanitized = sanitized.Replace(
                Uri.EscapeDataString(_options.Token),
                "***",
                StringComparison.Ordinal);
        }

        sanitized = sanitized.Replace(_options.BaseUrl.ToString(), "<base-url>", StringComparison.Ordinal);
        sanitized = sanitized.Replace(
            _options.BaseUrl.GetLeftPart(UriPartial.Authority),
            "<base-url>",
            StringComparison.Ordinal);

        return sanitized;
    }

}
