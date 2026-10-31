using System.Text.Json.Nodes;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests.TestDoubles;

/// <summary>Captures every invocation so tool tests can assert the exact route.</summary>
internal sealed class RecordingTShockClient : ITShockClient
{
    internal sealed record RecordedCall(
        string Operation,
        string Path,
        IReadOnlyDictionary<string, string?>? Query);

    internal List<RecordedCall> Calls { get; } = new();

    public Task<TShockResult> CallAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new RecordedCall(operation, path, query));

        // A fresh body per call keeps JsonNode parent ownership valid when a
        // single tool object serves several invocations.
        return Task.FromResult(TShockResult.Success(200, new JsonObject { ["result"] = operation }));
    }
}
