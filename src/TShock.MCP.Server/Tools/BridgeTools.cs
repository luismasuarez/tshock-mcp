using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TShock.McpServer.TShock;

namespace TShock.McpServer.Tools;

/// <summary>
/// Tools backed by the AgentBridge plugin. They are registered even when the
/// bridge is not configured: an agent must be able to call them and receive an
/// explanation instead of silently not seeing the tool.
/// </summary>
[McpServerToolType]
public sealed class BridgeTools(IBridgeClient bridge, TShockOptions options)
{
    private const string InvalidArgumentCode = "invalid_argument";
    private const string RegionNotFoundCode = "region_not_found";
    internal const string ConfirmationKey = "confirm_region_delete";

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

    [McpServerTool(
        Name = "region_create",
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Creates a protected region in the world. Requires TSHOCK_MCP_ALLOW_WRITE=true. The owner " +
        "defaults to the server's configured bridge owner; any other owner must be allowed by the " +
        "server administrator, otherwise the tool returns owner_not_allowed.")]
    public async Task<string> RegionCreate(
        [Description("Region name, unique in the world. No slashes.")] string name,
        [Description("Left tile column of the region, zero or greater.")] int x,
        [Description("Top tile row of the region, zero or greater.")] int y,
        [Description("Width in tiles, greater than zero.")] int width,
        [Description("Height in tiles, greater than zero.")] int height,
        [Description("Optional region owner allowed by the server policy.")] string? owner = null,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return disabled;
        }

        if (RequireRegionName(name) is { } invalidName)
        {
            return invalidName;
        }

        if (width <= 0 || height <= 0)
        {
            return ToolResults.Error(InvalidArgumentCode, "'width' and 'height' must be greater than zero.");
        }

        if (x < 0 || y < 0)
        {
            return ToolResults.Error(InvalidArgumentCode, "'x' and 'y' must be zero or greater.");
        }

        if (owner is not null &&
            (string.IsNullOrWhiteSpace(owner) || owner.Length > MaxRegionNameLength || owner.Any(char.IsControl)))
        {
            return ToolResults.Error(
                InvalidArgumentCode,
                "'owner' must be a non-empty name without control characters.");
        }

        var body = new JsonObject
        {
            ["name"] = name,
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height
        };

        if (owner is not null)
        {
            body["owner"] = owner;
        }

        var result = await bridge
            .PostAsync("region_create", "/v1/regions", body, cancellationToken)
            .ConfigureAwait(false);

        return MapRegionResult(result, name);
    }

    [McpServerTool(
        Name = "region_delete",
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Deletes a region and its protection. This cannot be undone. Requires " +
        "TSHOCK_MCP_ALLOW_WRITE=true. When the client supports elicitation the operator is asked " +
        "to confirm; otherwise the call must pass 'confirm: true'.")]
    public async Task<string> RegionDelete(
        ModelContextProtocol.Server.McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Exact region name to delete.")] string name,
        [Description(
            "Explicit confirmation for clients that cannot ask the operator. Ignored when " +
            "elicitation is available.")]
        bool? confirm = null,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return disabled;
        }

        if (RequireRegionName(name) is { } invalidName)
        {
            return invalidName;
        }

        // Ask the human only when the client declared elicitation on this request.
        // The model cannot skip the question by omitting a boolean.
        switch (DecideDelete(
                    options.AllowElicitation && CanElicit(server),
                    AnsweredConfirmation(context?.Params?.InputResponses),
                    confirm))
        {
            case DeleteDecision.Decline:
                return ToolResults.Error(
                    "confirmation_declined",
                    $"The deletion of region '{name}' was declined by the operator.");

            case DeleteDecision.Ask:
            {
                throw new InputRequiredException(
                inputRequests: new Dictionary<string, InputRequest>
                {
                    [ConfirmationKey] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message =
                            $"Delete region '{name}'? Its protection is removed and this cannot be undone.",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["confirm"] = new ElicitRequestParams.BooleanSchema
                                {
                                    Description = "Confirm the deletion"
                                }
                            },
                            Required = ["confirm"]
                        }
                    })
                },
                requestState: "awaiting-region-delete-confirmation");
            }

            case DeleteDecision.RequireConfirmation:
                return ToolResults.Error(
                    "confirmation_required",
                    "Deleting a region cannot be undone. This client cannot ask the operator, so " +
                    "pass 'confirm: true' to proceed.");

            default:
                return await DeleteAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What the confirmation policy requires for a destructive call.</summary>
    public enum DeleteDecision
    {
        Proceed,
        Ask,
        Decline,
        RequireConfirmation
    }

    /// <summary>
    /// True only when this request can ask the human: the session supports
    /// multi-round-trip requests and the client declared elicitation. The
    /// capabilities are request-scoped under 2026-07-28 and session-scoped on
    /// older revisions, and <c>McpServer</c> exposes the right view for both.
    /// </summary>
    internal static bool CanElicit(ModelContextProtocol.Server.McpServer? server) =>
        server?.IsMrtrSupported == true && server.ClientCapabilities?.Elicitation is not null;

    /// <summary>
    /// Reads the operator's answer from a multi-round-trip retry.
    /// <see langword="null"/> when the client did not answer the confirmation.
    /// </summary>
    internal static bool? AnsweredConfirmation(IDictionary<string, InputResponse>? responses)
    {
        if (responses is null || !responses.TryGetValue(ConfirmationKey, out var response))
        {
            return null;
        }

        var elicited = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        return elicited?.IsAccepted == true;
    }

    /// <summary>
    /// Capability-driven confirmation: a client that can ask the human must be
    /// asked; one that cannot must send the explicit flag. The model never picks
    /// the weaker path when the stronger one is available.
    /// </summary>
    internal static DeleteDecision DecideDelete(bool canElicit, bool? answered, bool? confirm)
    {
        if (answered is { } accepted)
        {
            return accepted ? DeleteDecision.Proceed : DeleteDecision.Decline;
        }

        if (canElicit)
        {
            return DeleteDecision.Ask;
        }

        return confirm == true ? DeleteDecision.Proceed : DeleteDecision.RequireConfirmation;
    }

    private async Task<string> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var result = await bridge
            .DeleteAsync("region_delete", $"/v1/regions/{Uri.EscapeDataString(name)}", cancellationToken)
            .ConfigureAwait(false);

        return MapRegionResult(result, name);
    }

    /// <summary>
    /// Maps bridge region failures to the codes an agent can act on: a missing or
    /// existing region and a rejected owner are facts, not bridge faults.
    /// </summary>
    private static string MapRegionResult(BridgeResult result, string regionName)
    {
        if (result.Ok)
        {
            return ToolResults.Ok(result.Body ?? new JsonObject
            {
                ["region"] = regionName,
                ["deleted"] = true
            });
        }

        // Transport, auth and cursor failures keep their own codes.
        if (!string.Equals(result.ErrorCode, "bridge_error", StringComparison.Ordinal))
        {
            return ToolResults.From(result);
        }

        return result.BridgeError switch
        {
            RegionNotFoundCode => ToolResults.Error(
                RegionNotFoundCode,
                $"The region '{regionName}' does not exist.",
                404),
            "region_exists" => ToolResults.Error(
                "region_exists",
                $"The region '{regionName}' already exists.",
                409),
            "owner_not_allowed" => ToolResults.Error(
                "owner_not_allowed",
                "The server's bridge policy does not allow that region owner.",
                403),
            _ when result.StatusCode == 400 => ToolResults.Error(
                InvalidArgumentCode,
                result.ErrorMessage ?? "The bridge rejected the region request.",
                400),
            _ => ToolResults.From(result)
        };
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

    /// <summary>Null when writes are enabled, otherwise the stable refusal.</summary>
    private string? Disabled =>
        options.AllowWrite
            ? null
            : ToolResults.Error(
                "write_disabled",
                "Writing is disabled. Set TSHOCK_MCP_ALLOW_WRITE=true to enable region changes.");

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
