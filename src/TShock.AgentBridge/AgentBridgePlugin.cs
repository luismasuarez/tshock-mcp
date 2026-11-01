using Terraria;
using TerrariaApi.Server;

namespace TShock.AgentBridge;

/// <summary>
/// Phase 1 bridge plugin. It exposes a loopback HTTP surface for what the TShock REST
/// API does not cover (regions, events, command discovery). The MCP gateway remains the
/// only process that speaks MCP.
/// </summary>
[ApiVersion(2, 1)]
public sealed class AgentBridgePlugin : TerrariaPlugin
{
    private const int DefaultPort = 7879;
    private const string PluginVersion = "0.1.0";

    private MainThreadDispatcher? _dispatcher;
    private BridgeServer? _server;

    public AgentBridgePlugin(Main game)
        : base(game)
    {
        // TShock uses 0, so the bridge initializes after it.
        Order = 10;
    }

    public override string Name => "TShock.AgentBridge";

    public override Version Version => new(0, 1, 0);

    public override string Author => "tshock-mcp";

    public override string Description => "Loopback HTTP bridge for the tshock-mcp gateway.";

    public override void Initialize()
    {
        // Never throw from Initialize: the loader aborts server startup if a plugin does.
        try
        {
            var token = ResolveToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                TShockAPI.TShock.Log.ConsoleError(
                    $"{Name} {PluginVersion}: no bridge token configured; the bridge is disabled.");
                return;
            }

            var port = DefaultPort;
            var rawPort = Environment.GetEnvironmentVariable("TSHOCK_BRIDGE_PORT");
            if (!string.IsNullOrWhiteSpace(rawPort) && int.TryParse(rawPort, out var parsed) && parsed > 0 && parsed < 65536)
            {
                port = parsed;
            }

            _dispatcher = new MainThreadDispatcher();
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);

            _server = new BridgeServer(port, token, _dispatcher);
            _server.Start();

            TShockAPI.TShock.Log.ConsoleInfo($"{Name} {PluginVersion}: listening on 0.0.0.0:{port}.");
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.ConsoleError($"{Name} {PluginVersion}: failed to start: {ex.Message}");
            Dispose(true);
        }
    }

    private void OnGameUpdate(EventArgs args) => _dispatcher?.Pump();

    /// <summary>
    /// Reads the bridge secret from TSHOCK_BRIDGE_TOKEN, or from the file named by
    /// TSHOCK_BRIDGE_TOKEN_FILE (default /run/secrets/bridge-token). The file path is
    /// preferred so the secret never shows up in `docker inspect`.
    /// </summary>
    private string? ResolveToken()
    {
        var inline = Environment.GetEnvironmentVariable("TSHOCK_BRIDGE_TOKEN");
        if (!string.IsNullOrWhiteSpace(inline))
        {
            return inline.Trim();
        }

        var path = Environment.GetEnvironmentVariable("TSHOCK_BRIDGE_TOKEN_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "/run/secrets/bridge-token";
        }

        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.ConsoleError($"{Name}: cannot read the bridge token file: {ex.Message}");
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
            _server?.Dispose();
            _server = null;
            _dispatcher?.Dispose();
            _dispatcher = null;
        }

        base.Dispose(disposing);
    }
}
