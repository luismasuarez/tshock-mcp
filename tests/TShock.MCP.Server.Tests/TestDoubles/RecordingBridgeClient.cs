using System.Text.Json.Nodes;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests.TestDoubles;

/// <summary>Captures every invocation so bridge tool tests can assert the route.</summary>
internal sealed class RecordingBridgeClient : IBridgeClient
{
    internal sealed record RecordedCall(
        string Operation,
        string Path,
        IReadOnlyDictionary<string, string?>? Query);

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
        Calls.Add(new RecordedCall(operation, path, query));
        return Task.FromResult(Result);
    }
}
