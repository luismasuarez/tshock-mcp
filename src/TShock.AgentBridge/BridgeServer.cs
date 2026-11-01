using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Terraria;

namespace TShock.AgentBridge;

/// <summary>
/// HTTP loopback-facing bridge. It binds 0.0.0.0 inside the container because the
/// gateway runs on the host and reaches it through a published port; the host side
/// must publish it on 127.0.0.1 only.
/// </summary>
internal sealed class BridgeServer : IDisposable
{
    private const string PluginName = "TShock.AgentBridge";
    private const string PluginVersion = "0.1.0";

    private readonly HttpListener _listener = new();
    private readonly byte[] _token;
    private readonly MainThreadDispatcher _dispatcher;
    private readonly RegionService _regions;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private Task? _acceptLoop;
    private Timer? _idlePump;
    private long _lastTicks = -1;
    private DateTime _lastTickChange = DateTime.UtcNow;

    public BridgeServer(int port, string token, MainThreadDispatcher dispatcher)
    {
        _port = port;
        _token = Encoding.UTF8.GetBytes(token);
        _dispatcher = dispatcher;
        _regions = new RegionService(dispatcher);
        _listener.Prefixes.Add($"http://*:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptAsync(_cts.Token));

        // An empty TShock server does not tick: Main.Update does not run, so the
        // GameUpdate pump never fires. Pump from a timer only while the tick counter
        // has been static, which means no game state is being mutated concurrently.
        _idlePump = new Timer(_ => PumpIfIdle(), null, 250, 250);
    }

    private void PumpIfIdle()
    {
        var ticks = _dispatcher.Ticks;
        if (ticks != _lastTicks)
        {
            _lastTicks = ticks;
            _lastTickChange = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - _lastTickChange < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _dispatcher.Pump();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Listener stopped or the port is gone: nothing left to serve.
                if (!_listener.IsListening)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteAsync(context, 401, new { error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            switch (context.Request.Url?.AbsolutePath)
            {
                case "/v1/health":
                    await WriteAsync(context, 200, await HealthAsync().ConfigureAwait(false)).ConfigureAwait(false);
                    return;
                case "/v1/world":
                {
                    // Exercises the dispatcher: the value is read through the game-thread queue.
                    var world = await _dispatcher.InvokeAsync(() => Main.worldName).ConfigureAwait(false);
                    await WriteAsync(context, 200, new { world }).ConfigureAwait(false);
                    return;
                }
                default:
                    if (IsRegionsPath(context.Request.Url?.AbsolutePath))
                    {
                        await RouteRegionsAsync(context).ConfigureAwait(false);
                        return;
                    }

                    await WriteAsync(context, 404, new { error = "not_found" }).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            // Never leak the token or a stack trace to the caller.
            TShockAPI.TShock.Log.ConsoleError($"{PluginName}: request failed: {ex.Message}");
            try
            {
                await WriteAsync(context, 500, new { error = "internal_error" }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                context.Response.Abort();
            }
        }
    }

    private static bool IsRegionsPath(string? path) =>
        path is "/v1/regions" || (path?.StartsWith("/v1/regions/", StringComparison.Ordinal) ?? false);

    private async Task RouteRegionsAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var method = request.HttpMethod;

        if (path == "/v1/regions")
        {
            if (method == "GET")
            {
                await WriteAsync(context, 200, new { regions = await _regions.ListAsync().ConfigureAwait(false) }).ConfigureAwait(false);
                return;
            }

            if (method == "POST")
            {
                RegionCreateRequest? body;
                try
                {
                    body = await JsonSerializer.DeserializeAsync<RegionCreateRequest>(
                        request.InputStream, cancellationToken: _cts.Token).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await WriteAsync(context, 400, new { error = "invalid_json" }).ConfigureAwait(false);
                    return;
                }

                if (body is null)
                {
                    await WriteAsync(context, 400, new { error = "invalid_json" }).ConfigureAwait(false);
                    return;
                }

                var (region, error) = await _regions.CreateAsync(body).ConfigureAwait(false);
                if (error is not null)
                {
                    await WriteAsync(context, StatusForRegionError(error), new { error }).ConfigureAwait(false);
                    return;
                }

                await WriteAsync(context, 201, region!).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, 405, new { error = "method_not_allowed" }).ConfigureAwait(false);
            return;
        }

        var name = Uri.UnescapeDataString(path["/v1/regions/".Length..]);
        if (method == "GET")
        {
            var region = await _regions.GetAsync(name).ConfigureAwait(false);
            if (region is null)
            {
                await WriteAsync(context, 404, new { error = "region_not_found" }).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, 200, region).ConfigureAwait(false);
            return;
        }

        if (method == "DELETE")
        {
            var (deleted, error) = await _regions.DeleteAsync(name).ConfigureAwait(false);
            if (!deleted)
            {
                await WriteAsync(context, StatusForRegionError(error ?? "delete_failed"), new { error = error ?? "delete_failed" }).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context, 204, new { deleted = true }).ConfigureAwait(false);
            return;
        }

        await WriteAsync(context, 405, new { error = "method_not_allowed" }).ConfigureAwait(false);
    }

    private static int StatusForRegionError(string error) => error switch
    {
        "name_required" or "invalid_size" or "invalid_position" or "out_of_bounds" or "invalid_json" => 400,
        "region_not_found" => 404,
        "region_exists" => 409,
        _ => 500
    };

    private async Task<object> HealthAsync()
    {
        // Reads are safe off the game thread; the tick counter reports whether the
        // world is actually updating, because an idle server does not tick.
        var world = Main.worldName;
        var ticks = _dispatcher.Ticks;
        await Task.Yield();
        return new
        {
            status = "ok",
            plugin = PluginName,
            version = PluginVersion,
            world,
            port = _port,
            ticks
        };
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        return provided.Length == _token.Length && CryptographicOperations.FixedTimeEquals(provided, _token);
    }

    private static async Task WriteAsync(HttpListenerContext context, int statusCode, object payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _idlePump?.Dispose();
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch (Exception)
        {
            // Shutdown races are harmless.
        }

        _listener.Close();
        _cts.Dispose();

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Ignore: the loop exits on cancellation.
        }
    }
}
