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
    private const string RegionNotFoundCode = "region_not_found";

    /// <summary>Matches the bridge contract: 500 events per page.</summary>
    internal const int MaxEventLimit = 500;

    /// <summary>Bounds an argument that ends up in a URL path segment.</summary>
    internal const int MaxRegionNameLength = 128;

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

    [McpServerTool(Name = "regions_list", ReadOnly = true)]
    [Description(
        "Lists the regions of the current world with their name, area, owner, protection flags and " +
        "allowed users and groups. Regions are a bridge feature, so this tool answers " +
        "bridge_unavailable when the bridge is not configured.")]
    public Task<string> RegionsList(CancellationToken cancellationToken = default) =>
        CallAsync("regions_list", "/v1/regions", null, cancellationToken);

    [McpServerTool(Name = "region_info", ReadOnly = true)]
    [Description(
        "Returns one region by its exact name: area, owner, whether building is disabled and which " +
        "users and groups are allowed. Returns region_not_found when the name does not exist.")]
    public Task<string> RegionInfo(
        [Description("Exact region name, for example 'spawn'.")] string name,
        CancellationToken cancellationToken = default)
    {
        if (RequireRegionName(name) is { } invalidName)
        {
            return Task.FromResult(invalidName);
        }

        return CallAsync(
            "region_info",
            $"/v1/regions/{Uri.EscapeDataString(name)}",
            null,
            cancellationToken,
            regionName: name);
    }

    /// <summary>
    /// Rejects names that cannot travel as a single URL path segment or that are
    /// absurdly long. Validation runs before the network so a bad name never
    /// reaches the bridge and can never be mistaken for a missing region.
    /// </summary>
    internal static string? RequireRegionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolResults.Error(InvalidArgumentCode, "'name' must not be empty.");
        }

        if (name.Length > MaxRegionNameLength)
        {
            return ToolResults.Error(
                InvalidArgumentCode,
                $"'name' must be at most {MaxRegionNameLength} characters.");
        }

        if (name.Contains('/') || name.Any(char.IsControl))
        {
            return ToolResults.Error(
                InvalidArgumentCode,
                "'name' must not contain '/' or control characters.");
        }

        return null;
    }

    private async Task<string> CallAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken,
        string? regionName = null)
    {
        var result = await bridge.GetAsync(operation, path, query, cancellationToken).ConfigureAwait(false);

        // A missing region is a fact, not a bridge failure: it gets its own code so
        // an agent can offer a correction instead of retrying the bridge.
        if (!result.Ok && result.StatusCode == 404 && regionName is not null)
        {
            return ToolResults.Error(
                RegionNotFoundCode,
                $"The region '{regionName}' does not exist.",
                404);
        }

        return ToolResults.From(result);
    }
}
