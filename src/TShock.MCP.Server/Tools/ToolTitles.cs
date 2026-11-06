namespace TShock.McpServer.Tools;

/// <summary>
/// Human-readable display names for the tool catalog. They are constants, not
/// configuration: the title is part of the <c>tools/list</c> contract and is
/// shown in client interfaces and consent prompts.
/// </summary>
/// <remarks>
/// <para>
/// Policy: English, imperative verb + object, sentence case, at most 40
/// characters, letters/digits/spaces only, no trailing period, no acronyms or
/// jargon, unique across the catalog and different from the programmatic name.
/// Titles are static: they never include server data, player-provided text or
/// anything an operator could mistake for an instruction.
/// </para>
/// <para>
/// The MCP specification gives <c>title</c> precedence over
/// <c>annotations.title</c> and <c>name</c> for display. The SDK publishes the
/// same value in both places, so clients that still read the older location
/// show the same label.
/// </para>
/// </remarks>
internal static class ToolTitles
{
    internal const string ServerStatus = "Get server status";
    internal const string WorldInfo = "Get world details";
    internal const string PlayersList = "List players";
    internal const string PlayerInfo = "Get player details";
    internal const string ServerMotd = "Get message of the day";
    internal const string ServerRules = "Get server rules";
    internal const string UsersList = "List accounts";
    internal const string UserInfo = "Get account details";
    internal const string GroupsList = "List groups";
    internal const string GroupInfo = "Get group details";
    internal const string BansList = "List bans";
    internal const string BanInfo = "Get ban details";
    internal const string EventsSince = "Get server events";
    internal const string RegionsList = "List regions";
    internal const string RegionInfo = "Get region details";
    internal const string Broadcast = "Send message to all";
    internal const string KickPlayer = "Kick player";
    internal const string KillPlayer = "Kill player";
    internal const string MutePlayer = "Mute player";
    internal const string SaveWorld = "Save world";
    internal const string ReloadConfig = "Reload configuration";
    internal const string RegionCreate = "Create region";
    internal const string RegionDelete = "Delete region";
}
