using System.Text.Json.Nodes;

namespace TShock.McpServer.TShock;

/// <summary>
/// Client for the loopback AgentBridge plugin. Implementations inject the bridge
/// bearer token, bound the response size and translate every failure into one of
/// the stable bridge error codes.
/// </summary>
public interface IBridgeClient
{
    /// <summary>
    /// False when no <c>TSHOCK_BRIDGE_*</c> variable is configured. Bridge tools
    /// stay registered in that case and answer <c>bridge_unavailable</c>.
    /// </summary>
    bool IsConfigured { get; }

    /// <param name="operation">Tool name, used only for audit logs.</param>
    /// <param name="path">Route such as <c>/v1/events</c>.</param>
    /// <param name="query">Optional query parameters. Null values are skipped.</param>
    Task<BridgeResult> GetAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default);

    /// <param name="body">JSON payload, or null for an empty request body.</param>
    Task<BridgeResult> PostAsync(
        string operation,
        string path,
        JsonNode? body,
        CancellationToken cancellationToken = default);

    Task<BridgeResult> DeleteAsync(
        string operation,
        string path,
        CancellationToken cancellationToken = default);
}

/// <summary>Normalized outcome of a single bridge call.</summary>
public sealed record BridgeResult(
    bool Ok,
    int StatusCode,
    JsonNode? Body,
    string? ErrorCode,
    string? ErrorMessage,
    JsonNode? ErrorDetails)
{
    /// <summary>
    /// The bridge's own error discriminator, for example <c>region_exists</c>.
    /// It is metadata for the tool that maps errors; it is never serialized to
    /// the agent unless that tool chooses to.
    /// </summary>
    public string? BridgeError { get; init; }

    public static BridgeResult Success(int statusCode, JsonNode? body) =>
        new(true, statusCode, body, null, null, null);

    /// <param name="details">
    /// Extra machine-readable context for the error envelope, such as the
    /// <c>resumeCursor</c> of an expired cursor.
    /// </param>
    public static BridgeResult Failure(
        string code,
        string message,
        int statusCode = 0,
        JsonNode? details = null) =>
        new(false, statusCode, null, code, message, details);
}
