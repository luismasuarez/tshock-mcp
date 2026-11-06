using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;
using TShock.McpServer.TShock;

namespace TShock.McpServer.Tools;

/// <summary>
/// Read-only tools. Every one of them maps to a TShock REST route that only
/// returns information, so they are always enabled.
/// </summary>
[McpServerToolType]
public sealed class ReadTools(ITShockClient client)
{
    [McpServerTool(
        Name = "server_status",
        Title = ToolTitles.ServerStatus,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Returns TShock server status: server name, Terraria and TShock versions, port, " +
        "connected/max players, world name and uptime. Use it as the first health check.")]
    public Task<string> ServerStatus(CancellationToken cancellationToken = default) =>
        CallAsync("server_status", "/v2/server/status", null, cancellationToken);

    [McpServerTool(
        Name = "world_info",
        Title = ToolTitles.WorldInfo,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Returns world metadata: name, tile size, current world time, whether it is daytime, " +
        "blood moon state and invasion size.")]
    public Task<string> WorldInfo(CancellationToken cancellationToken = default) =>
        CallAsync("world_info", "/world/read", null, cancellationToken);

    [McpServerTool(
        Name = "players_list",
        Title = ToolTitles.PlayersList,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Lists connected players with nickname, account name, group, active state, state code and team. " +
        "IP addresses are never included on this route.")]
    public Task<string> PlayersList(CancellationToken cancellationToken = default) =>
        CallAsync("players_list", "/v2/players/list", null, cancellationToken);

    [McpServerTool(
        Name = "player_info",
        Title = ToolTitles.PlayerInfo,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Returns detailed information for one connected player, including inventory, equipment, buffs, " +
        "group and mute state. The IP address is redacted unless TSHOCK_MCP_SHOW_IPS=true.")]
    public Task<string> PlayerInfo(
        [Description("Exact in-game player name (nickname). At most 64 characters.")]
        [MaxLength(ArgumentLimits.PlayerName)] string player,
        CancellationToken cancellationToken = default) =>
        ToolSupport.RequireText(player, "player", ArgumentLimits.PlayerName) is { } invalidPlayer
            ? Task.FromResult(invalidPlayer)
            : CallAsync(
                "player_info",
                "/v4/players/read",
                new Dictionary<string, string?> { ["player"] = player },
                cancellationToken);

    [McpServerTool(
        Name = "server_motd",
        Title = ToolTitles.ServerMotd,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Returns the message of the day shown to players when they join, if one is configured.")]
    public Task<string> ServerMotd(CancellationToken cancellationToken = default) =>
        CallAsync("server_motd", "/v3/server/motd", null, cancellationToken);

    [McpServerTool(
        Name = "server_rules",
        Title = ToolTitles.ServerRules,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Returns the server rules text shown to players, if one is configured.")]
    public Task<string> ServerRules(CancellationToken cancellationToken = default) =>
        CallAsync("server_rules", "/v3/server/rules", null, cancellationToken);

    [McpServerTool(
        Name = "users_list",
        Title = ToolTitles.UsersList,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Lists every registered TShock account with its numeric id, name and group.")]
    public Task<string> UsersList(CancellationToken cancellationToken = default) =>
        CallAsync("users_list", "/v2/users/list", null, cancellationToken);

    [McpServerTool(
        Name = "user_info",
        Title = ToolTitles.UserInfo,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Returns the group and id of one registered account, looked up by name or by numeric id.")]
    public Task<string> UserInfo(
        [Description("Account name, or numeric id when type is 'id'. At most 64 characters.")]
        [MaxLength(ArgumentLimits.AccountName)] string user,
        [Description("Lookup type: 'name' or 'id'. Defaults to 'name'.")]
        [MaxLength(ArgumentLimits.LookupType)] string type = "name",
        CancellationToken cancellationToken = default)
    {
        if (ToolSupport.RequireText(user, "user", ArgumentLimits.AccountName) is { } invalidUser)
        {
            return Task.FromResult(invalidUser);
        }

        if (!string.IsNullOrWhiteSpace(type) &&
            ToolSupport.RequireText(type, "type", ArgumentLimits.LookupType) is { } invalidType)
        {
            return Task.FromResult(invalidType);
        }

        var lookupType = string.IsNullOrWhiteSpace(type) ? "name" : type.Trim().ToLowerInvariant();
        if (lookupType is not ("name" or "id"))
        {
            return Task.FromResult(ToolResults.Error(
                ErrorCodes.InvalidArgument,
                "'type' must be 'name' or 'id'."));
        }

        return CallAsync(
            "user_info",
            "/v2/users/read",
            new Dictionary<string, string?>
            {
                ["user"] = user,
                ["type"] = lookupType
            },
            cancellationToken);
    }

    [McpServerTool(
        Name = "groups_list",
        Title = ToolTitles.GroupsList,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Lists every permission group with its parent group and chat color.")]
    public Task<string> GroupsList(CancellationToken cancellationToken = default) =>
        CallAsync("groups_list", "/v2/groups/list", null, cancellationToken);

    [McpServerTool(
        Name = "group_info",
        Title = ToolTitles.GroupInfo,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Returns one group's parent, chat color, granted permissions, negated permissions and total permissions.")]
    public Task<string> GroupInfo(
        [Description("Exact group name, for example 'default' or 'owner'. At most 64 characters.")]
        [MaxLength(ArgumentLimits.GroupName)] string group,
        CancellationToken cancellationToken = default) =>
        ToolSupport.RequireText(group, "group", ArgumentLimits.GroupName) is { } invalidGroup
            ? Task.FromResult(invalidGroup)
            : CallAsync(
                "group_info",
                "/v2/groups/read",
                new Dictionary<string, string?> { ["group"] = group },
                cancellationToken);

    [McpServerTool(
        Name = "bans_list",
        Title = ToolTitles.BansList,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Lists every ban in the TShock database with ticket number, identifier, reason and dates.")]
    public Task<string> BansList(CancellationToken cancellationToken = default) =>
        CallAsync("bans_list", "/v3/bans/list", null, cancellationToken);

    [McpServerTool(
        Name = "ban_info",
        Title = ToolTitles.BanInfo,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Returns one ban by ticket number, including identifier, reason, banning user and dates.")]
    public Task<string> BanInfo(
        [Description("Numeric ban ticket number.")] int ticketNumber,
        CancellationToken cancellationToken = default) =>
        ticketNumber <= 0
            ? Task.FromResult(ToolResults.Error(
                ErrorCodes.InvalidArgument,
                "'ticketNumber' must be greater than zero."))
            : CallAsync(
                "ban_info",
                "/v3/bans/read",
                new Dictionary<string, string?> { ["ticketNumber"] = ticketNumber.ToString() },
                cancellationToken);

    private async Task<string> CallAsync(
        string operation,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        var result = await client.CallAsync(operation, path, query, cancellationToken).ConfigureAwait(false);
        return ToolResults.From(result);
    }
}
