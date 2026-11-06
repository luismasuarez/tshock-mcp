using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;
using TShock.McpServer.TShock;

namespace TShock.McpServer.Tools;

/// <summary>
/// Mutation tools. They are disabled unless TSHOCK_MCP_ALLOW_WRITE=true, so a
/// read-only token can never be escalated by a confused model.
/// </summary>
[McpServerToolType]
public sealed class WriteTools(ITShockClient client, TShockOptions options)
{
    [McpServerTool(
        Name = "broadcast",
        Title = ToolTitles.Broadcast,
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Sends a message to every connected player. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.broadcast permission.")]
    public Task<string> Broadcast(
        [Description("Message to show in-game. Keep it short and plain text. At most 256 characters.")]
        [MaxLength(ArgumentLimits.BroadcastMessage)] string message,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (ToolSupport.RequireText(message, "message", ArgumentLimits.BroadcastMessage) is { } invalidMessage)
        {
            return Task.FromResult(invalidMessage);
        }

        return CallAsync(
            "broadcast",
            "/v2/server/broadcast",
            new Dictionary<string, string?> { ["msg"] = message },
            cancellationToken);
    }

    [McpServerTool(
        Name = "kick_player",
        Title = ToolTitles.KickPlayer,
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Disconnects a player from the server. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.kick permission.")]
    public Task<string> KickPlayer(
        [Description("Exact in-game player name (nickname). At most 64 characters.")]
        [MaxLength(ArgumentLimits.PlayerName)] string player,
        [Description("Optional reason shown to the player. At most 200 characters.")]
        [MaxLength(ArgumentLimits.KickReason)] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (ToolSupport.RequireText(player, "player", ArgumentLimits.PlayerName) is { } invalidPlayer)
        {
            return Task.FromResult(invalidPlayer);
        }

        var normalizedReason = ToolSupport.NormalizeOptional(reason);
        if (normalizedReason is not null &&
            ToolSupport.RequireText(normalizedReason, "reason", ArgumentLimits.KickReason) is { } invalidReason)
        {
            return Task.FromResult(invalidReason);
        }

        return CallAsync(
            "kick_player",
            "/v2/players/kick",
            new Dictionary<string, string?>
            {
                ["player"] = player,
                ["reason"] = normalizedReason
            },
            cancellationToken);
    }

    [McpServerTool(
        Name = "kill_player",
        Title = ToolTitles.KillPlayer,
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Kills a player without kicking them. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.kill permission.")]
    public Task<string> KillPlayer(
        [Description("Exact in-game player name (nickname). At most 64 characters.")]
        [MaxLength(ArgumentLimits.PlayerName)] string player,
        [Description("Optional name to attribute the kill to. At most 64 characters.")]
        [MaxLength(ArgumentLimits.KillSource)] string? from = null,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (ToolSupport.RequireText(player, "player", ArgumentLimits.PlayerName) is { } invalidPlayer)
        {
            return Task.FromResult(invalidPlayer);
        }

        var normalizedSource = ToolSupport.NormalizeOptional(from);
        if (normalizedSource is not null &&
            ToolSupport.RequireText(normalizedSource, "from", ArgumentLimits.KillSource) is { } invalidSource)
        {
            return Task.FromResult(invalidSource);
        }

        return CallAsync(
            "kill_player",
            "/v2/players/kill",
            new Dictionary<string, string?>
            {
                ["player"] = player,
                ["from"] = normalizedSource
            },
            cancellationToken);
    }

    [McpServerTool(
        Name = "mute_player",
        Title = ToolTitles.MutePlayer,
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Mutes a player so their chat is not broadcast. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.mute permission.")]
    public Task<string> MutePlayer(
        [Description("Exact in-game player name (nickname). At most 64 characters.")]
        [MaxLength(ArgumentLimits.PlayerName)] string player,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (ToolSupport.RequireText(player, "player", ArgumentLimits.PlayerName) is { } invalidPlayer)
        {
            return Task.FromResult(invalidPlayer);
        }

        return CallAsync(
            "mute_player",
            "/v2/players/mute",
            new Dictionary<string, string?> { ["player"] = player },
            cancellationToken);
    }

    [McpServerTool(
        Name = "save_world",
        Title = ToolTitles.SaveWorld,
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Saves the current world to disk. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.cfg permission.")]
    public Task<string> SaveWorld(CancellationToken cancellationToken = default) =>
        Disabled is { } disabled
            ? Task.FromResult(disabled)
            : CallAsync("save_world", "/v2/world/save", null, cancellationToken);

    [McpServerTool(
        Name = "reload_config",
        Title = ToolTitles.ReloadConfig,
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Reloads TShock configuration, permissions and regions. Some changes still need a restart. " +
        "Requires TSHOCK_MCP_ALLOW_WRITE=true and the tshock.rest.cfg permission.")]
    public Task<string> ReloadConfig(CancellationToken cancellationToken = default) =>
        Disabled is { } disabled
            ? Task.FromResult(disabled)
            : CallAsync("reload_config", "/v3/server/reload", null, cancellationToken);

    private string? Disabled =>
        options.AllowWrite
            ? null
            : ToolResults.Error(
                ErrorCodes.WriteDisabled,
                "Write tools are disabled. Set TSHOCK_MCP_ALLOW_WRITE=true to enable them.");

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
