using System.Collections.Concurrent;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace TShock.AgentBridge;

/// <summary>
/// Subscribes to the TShock and Terraria hooks and turns them into bridge events.
/// All hooks fire on the game thread; the buffer itself is thread-safe, so handlers
/// never need to hop through the dispatcher.
/// </summary>
internal sealed class EventService : IDisposable
{
    private readonly TerrariaPlugin _plugin;
    private readonly EventBuffer _buffer;
    private readonly ConcurrentDictionary<int, string> _roster = new();

    public EventService(TerrariaPlugin plugin, EventBuffer? buffer = null)
    {
        _plugin = plugin;
        _buffer = buffer ?? new EventBuffer();

        SeedRoster();

        ServerApi.Hooks.NetGreetPlayer.Register(_plugin, OnGreetPlayer);
        ServerApi.Hooks.ServerLeave.Register(_plugin, OnLeave);
        ServerApi.Hooks.NpcKilled.Register(_plugin, OnNpcKilled);
        PlayerHooks.PlayerChat += OnPlayerChat;
        PlayerHooks.PlayerCommand += OnPlayerCommand;
        // gethandled: true keeps the event visible even if another plugin cancels the death.
        GetDataHandlers.KillMe.Register(OnKillMe, HandlerPriority.Normal, true);
        RegionHooks.RegionCreated += OnRegionCreated;
        RegionHooks.RegionDeleted += OnRegionDeleted;
    }

    public EventBuffer Buffer => _buffer;

    /// <summary>
    /// Captures players already online when the plugin is (re)loaded, so their leave
    /// event still has a name even though the bridge never saw them join.
    /// </summary>
    private void SeedRoster()
    {
        var players = TShockAPI.TShock.Players;
        if (players is null)
        {
            return;
        }

        for (var i = 0; i < players.Length; i++)
        {
            var name = EventText.Truncate(players[i]?.Name, EventText.PlayerNameMax);
            if (name.Length > 0)
            {
                _roster[i] = name;
            }
        }
    }

    // A greet fires after the handshake, when the name is final. PlayerPostLogin only
    // fires for authenticated accounts, so it is not a general join hook.
    private void OnGreetPlayer(GreetPlayerEventArgs args)
    {
        var name = PlayerName(args.Who);
        if (name.Length == 0)
        {
            return;
        }

        _roster[args.Who] = name;
        _buffer.Publish("player.join", true, Payload(("player", name)));
    }

    private void OnLeave(LeaveEventArgs args)
    {
        // TShock (Order 0) clears TShock.Players[who] in its own handler before this
        // one runs, so prefer the roster captured at greet time.
        if (!_roster.TryRemove(args.Who, out var name) || name.Length == 0)
        {
            name = PlayerName(args.Who);
        }

        if (name.Length == 0)
        {
            return;
        }

        _buffer.Publish("player.leave", true, Payload(("player", name)));
    }

    private void OnPlayerChat(PlayerChatEventArgs args)
    {
        if (args.Handled)
        {
            return;
        }

        var name = TsPlayerName(args.Player);
        if (name.Length == 0 || string.IsNullOrEmpty(args.RawText))
        {
            return;
        }

        _buffer.Publish("chat", true, Payload(
            ("player", name),
            ("text", EventText.Truncate(args.RawText, EventText.ChatMax))));
    }

    private void OnPlayerCommand(PlayerCommandEventArgs args)
    {
        // Console commands are administrative actions, not player events.
        if (args.Player is null || !args.Player.RealPlayer)
        {
            return;
        }

        var name = TsPlayerName(args.Player);
        var command = EventText.Truncate(args.CommandName?.ToLowerInvariant(), EventText.CommandMax);
        if (name.Length == 0 || command.Length == 0)
        {
            return;
        }

        // Arguments are deliberately dropped: /login and /register carry passwords and
        // /msg carries private text. Only the command token is safe to retain.
        _buffer.Publish("command", true, Payload(
            ("player", name),
            ("command", command)));
    }

    private void OnKillMe(object? sender, GetDataHandlers.KillMeEventArgs args)
    {
        var name = TsPlayerName(args.Player);
        if (name.Length == 0)
        {
            return;
        }

        _buffer.Publish("player.death", true, Payload(
            ("player", name),
            ("pvp", args.Pvp)));
    }

    private void OnNpcKilled(NpcKilledEventArgs args)
    {
        var npc = args.npc;
        if (npc is null || !npc.boss)
        {
            return;
        }

        var npcName = EventText.Truncate(Lang.GetNPCNameValue(npc.netID), EventText.NpcNameMax);
        _buffer.Publish("boss.kill", false, Payload(
            ("npc", npcName),
            ("netId", npc.netID)));
    }

    private void OnRegionCreated(RegionHooks.RegionCreatedEventArgs args)
    {
        var region = args.Region;
        if (region is null)
        {
            return;
        }

        _buffer.Publish("region.created", true, Payload(
            ("region", EventText.Truncate(region.Name, EventText.RegionNameMax)),
            ("owner", EventText.Truncate(region.Owner, EventText.PlayerNameMax))));
    }

    private void OnRegionDeleted(RegionHooks.RegionDeletedEventArgs args)
    {
        // RegionManager.DeleteRegion fires this hook even when no row matched, so the
        // event args can carry a null region.
        if (args.Region is null)
        {
            return;
        }

        _buffer.Publish("region.deleted", true, Payload(
            ("region", EventText.Truncate(args.Region.Name, EventText.RegionNameMax))));
    }

    private static string PlayerName(int who)
    {
        var players = TShockAPI.TShock.Players;
        if (players is not null && who >= 0 && who < players.Length)
        {
            var name = EventText.Truncate(players[who]?.Name, EventText.PlayerNameMax);
            if (name.Length > 0)
            {
                return name;
            }
        }

        // TShock.Players is already null at leave time; the Terraria array may still
        // carry the last known name.
        var main = Main.player;
        if (main is not null && who >= 0 && who < main.Length)
        {
            return EventText.Truncate(main[who]?.name, EventText.PlayerNameMax);
        }

        return string.Empty;
    }

    private static string TsPlayerName(TSPlayer? player) =>
        EventText.Truncate(player?.Name, EventText.PlayerNameMax);

    private static Dictionary<string, object?> Payload(params (string Key, object? Value)[] values)
    {
        var payload = new Dictionary<string, object?>(values.Length, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            payload[key] = value;
        }

        return payload;
    }

    public void Dispose()
    {
        ServerApi.Hooks.NetGreetPlayer.Deregister(_plugin, OnGreetPlayer);
        ServerApi.Hooks.ServerLeave.Deregister(_plugin, OnLeave);
        ServerApi.Hooks.NpcKilled.Deregister(_plugin, OnNpcKilled);
        PlayerHooks.PlayerChat -= OnPlayerChat;
        PlayerHooks.PlayerCommand -= OnPlayerCommand;
        GetDataHandlers.KillMe.UnRegister(OnKillMe);
        RegionHooks.RegionCreated -= OnRegionCreated;
        RegionHooks.RegionDeleted -= OnRegionDeleted;
    }
}
