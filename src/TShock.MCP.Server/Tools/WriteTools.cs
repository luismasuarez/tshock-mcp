using System.ComponentModel;
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
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Sends a message to every connected player. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.broadcast permission.")]
    public Task<string> Broadcast(
        [Description("Message to show in-game. Keep it short and plain text.")] string message,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (RequireText(message, "message") is { } invalidMessage)
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
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Disconnects a player from the server. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.kick permission.")]
    public Task<string> KickPlayer(
        [Description("Exact in-game player name (nickname).")] string player,
        [Description("Optional reason shown to the player.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (RequireText(player, "player") is { } invalidPlayer)
        {
            return Task.FromResult(invalidPlayer);
        }

        return CallAsync(
            "kick_player",
            "/v2/players/kick",
            new Dictionary<string, string?>
            {
                ["player"] = player,
                ["reason"] = NormalizeOptional(reason)
            },
            cancellationToken);
    }

    [McpServerTool(
        Name = "kill_player",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Kills a player without kicking them. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.kill permission.")]
    public Task<string> KillPlayer(
        [Description("Exact in-game player name (nickname).")] string player,
        [Description("Optional name to attribute the kill to.")] string? from = null,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (RequireText(player, "player") is { } invalidPlayer)
        {
            return Task.FromResult(invalidPlayer);
        }

        return CallAsync(
            "kill_player",
            "/v2/players/kill",
            new Dictionary<string, string?>
            {
                ["player"] = player,
                ["from"] = NormalizeOptional(from)
            },
            cancellationToken);
    }

    [McpServerTool(
        Name = "mute_player",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Mutes a player so their chat is not broadcast. Requires TSHOCK_MCP_ALLOW_WRITE=true and the " +
        "tshock.rest.mute permission.")]
    public Task<string> MutePlayer(
        [Description("Exact in-game player name (nickname).")] string player,
        CancellationToken cancellationToken = default)
    {
        if (Disabled is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        if (RequireText(player, "player") is { } invalidPlayer)
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

    /// <summary>
    /// Returns the error envelope for a missing or blank text argument, or
    /// <see langword="null"/> when the value is usable.
    /// </summary>
    private static string? RequireText(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? ToolResults.Error(ErrorCodes.InvalidArgument, $"'{parameterName}' must not be empty.")
            : null;

    /// <summary>Drops whitespace-only optional values so they are not sent to TShock.</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
