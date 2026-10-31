using System.Text.Json.Nodes;

namespace TShock.McpServer.TShock;

/// <summary>
/// Thin client over TShock's REST API. Implementations are responsible for
/// injecting the token, redacting sensitive fields and auditing the call.
/// </summary>
public interface ITShockClient
{
    /// <param name="operation">Tool name, used only for audit logs.</param>
    /// <param name="path">Route such as <c>/v2/server/status</c>.</param>
    /// <param name="query">Optional query parameters. Null values are skipped.</param>
    Task<TShockResult> CallAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Normalized outcome of a single REST call.</summary>
public sealed record TShockResult(
    bool Ok,
    int StatusCode,
    JsonNode? Body,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static TShockResult Success(int statusCode, JsonNode? body) =>
        new(true, statusCode, body, null, null);

    public static TShockResult RestError(int statusCode, string message) =>
        new(false, statusCode, null, "rest_error", message);

    public static TShockResult TransportError(string code, string message, int statusCode = 0) =>
        new(false, statusCode, null, code, message);
}
