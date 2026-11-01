using System.Text.Json.Nodes;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests.TestDoubles;

/// <summary>Captures every invocation so bridge tool tests can assert the route.</summary>
internal sealed class RecordingBridgeClient : IBridgeClient
{
    internal sealed record RecordedCall(
        string Operation,
        string Method,
        string Path,
        IReadOnlyDictionary<string, string?>? Query,
        JsonNode? Body);

    internal List<RecordedCall> Calls { get; } = new();

    /// <summary>Outcome returned to every call. Defaults to a successful empty page.</summary>
    internal BridgeResult Result { get; set; } =
        BridgeResult.Success(200, new JsonObject { ["events"] = new JsonArray() });

    public bool IsConfigured => true;

    public Task<BridgeResult> GetAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new RecordedCall(operation, "GET", path, query, null));
        return Task.FromResult(Result);
    }

    public Task<BridgeResult> PostAsync(
        string operation,
        string path,
        JsonNode? body,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new RecordedCall(operation, "POST", path, null, body));
        return Task.FromResult(Result);
    }

    public Task<BridgeResult> DeleteAsync(
        string operation,
        string path,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new RecordedCall(operation, "DELETE", path, null, null));
        return Task.FromResult(Result);
    }
}
