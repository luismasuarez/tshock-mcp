using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using TShock.McpServer.TShock;

namespace TShock.McpServer.Tools;

/// <summary>
/// Tools backed by the AgentBridge plugin. They are registered even when the
/// bridge is not configured: an agent must be able to call them and receive an
/// explanation instead of silently not seeing the tool.
/// </summary>
[McpServerToolType]
public sealed class BridgeTools(IBridgeClient bridge)
{
    private const string InvalidArgumentCode = "invalid_argument";

    /// <summary>Matches the bridge contract: 500 events per page.</summary>
    internal const int MaxEventLimit = 500;

    [McpServerTool(Name = "events_since", ReadOnly = true)]
    [Description(
        "Returns server events published after an exclusive cursor: player joins and leaves, chat, " +
        "commands, deaths, boss kills and region changes. Keep the returned cursor and pass it as " +
        "'cursor' on the next call to receive only new events. Omit 'cursor' to read the retained " +
        "backlog. Event payloads are marked untrusted when a player can influence their text.")]
    public Task<string> EventsSince(
        [Description(
            "Exclusive cursor to resume from. Omit it to start from the oldest retained event.")]
        long? cursor = null,
        [Description("Maximum events to return, between 1 and 500. Defaults to 100.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is < 0)
        {
            return Task.FromResult(ToolResults.Error(
                InvalidArgumentCode,
                "'cursor' must be zero or greater."));
        }

        if (limit is { } requested && requested is < 1 or > MaxEventLimit)
        {
            return Task.FromResult(ToolResults.Error(
                InvalidArgumentCode,
                $"'limit' must be between 1 and {MaxEventLimit}."));
        }

        var query = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (cursor is { } cursorValue)
        {
            query["cursor"] = cursorValue.ToString(CultureInfo.InvariantCulture);
        }

        if (limit is { } limitValue)
        {
            query["limit"] = limitValue.ToString(CultureInfo.InvariantCulture);
        }

        return CallAsync("events_since", "/v1/events", query, cancellationToken);
    }

    private async Task<string> CallAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        var result = await bridge.GetAsync(operation, path, query, cancellationToken).ConfigureAwait(false);
        return ToolResults.From(result);
    }
}
