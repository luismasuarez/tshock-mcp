using System.Globalization;

namespace TShock.McpServer.TShock;

/// <summary>
/// Runtime configuration for the gateway. Everything is sourced from environment
/// variables so the same binary can be launched by any MCP host.
/// </summary>
public sealed record TShockOptions
{
    public const string DefaultBaseUrl = "http://127.0.0.1:7878";

    /// <summary>Loopback port published by the AgentBridge plugin.</summary>
    public const string DefaultBridgeUrl = "http://127.0.0.1:7879";

    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// TShock REST application token. TShock expects it as a query parameter,
    /// so it must never be written to logs or error messages.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>Enables the six write tools. Disabled by default.</summary>
    public bool AllowWrite { get; init; }

    /// <summary>When false, any "ip" field in a response is redacted.</summary>
    public bool ShowIps { get; init; }

    /// <summary>Allow plain HTTP to a non-loopback host (not recommended).</summary>
    public bool AllowInsecure { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Optional JSONL audit log destination. Logs go to stderr when unset.</summary>
    public string? AuditLogPath { get; init; }

    /// <summary>
    /// AgentBridge connection. Null when no <c>TSHOCK_BRIDGE_*</c> variable is set,
    /// which keeps the gateway fully usable against the REST API alone.
    /// </summary>
    public BridgeOptions? Bridge { get; init; }

    public static TShockOptions FromEnvironment()
    {
        var rawUrl = Get("TSHOCK_REST_URL") ?? DefaultBaseUrl;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConfigurationException(
                "TSHOCK_REST_URL must be an absolute http(s) URL, for example http://127.0.0.1:7878.");
        }

        // The client appends its own route and token query, so the base URL must
        // be a plain prefix: a query, a fragment or embedded credentials would
        // change where the token travels or leak it into logs.
        if (baseUrl.Query.Length > 0)
        {
            throw new ConfigurationException(
                "TSHOCK_REST_URL must not contain a query string; pass parameters through the tools instead.");
        }

        if (baseUrl.Fragment.Length > 0)
        {
            throw new ConfigurationException("TSHOCK_REST_URL must not contain a fragment.");
        }

        if (baseUrl.UserInfo.Length > 0)
        {
            throw new ConfigurationException(
                "TSHOCK_REST_URL must not embed credentials (userinfo); configure TSHOCK_TOKEN instead.");
        }

        var token = Get("TSHOCK_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ConfigurationException(
                "TSHOCK_TOKEN is required. Create an application token in TShock's config.json " +
                "(ApplicationRestTokens) and pass it through the environment.");
        }

        var allowInsecure = GetBool("TSHOCK_MCP_ALLOW_INSECURE", false);
        if (baseUrl.Scheme == Uri.UriSchemeHttp && !baseUrl.IsLoopback && !allowInsecure)
        {
            throw new ConfigurationException(
                "Refusing to send a TShock token over plain HTTP to a non-loopback host. " +
                "Use HTTPS or set TSHOCK_MCP_ALLOW_INSECURE=true if you understand the risk.");
        }

        var timeoutSeconds = GetTimeoutSeconds();
        if (timeoutSeconds is < 1 or > 300)
        {
            throw new ConfigurationException("TSHOCK_MCP_TIMEOUT_SECONDS must be between 1 and 300.");
        }

        return new TShockOptions
        {
            BaseUrl = new Uri(baseUrl.ToString().TrimEnd('/') + "/", UriKind.Absolute),
            Token = token,
            AllowWrite = GetBool("TSHOCK_MCP_ALLOW_WRITE", false),
            ShowIps = GetBool("TSHOCK_MCP_SHOW_IPS", false),
            AllowInsecure = allowInsecure,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            AuditLogPath = Get("TSHOCK_MCP_AUDIT_LOG"),
            Bridge = ReadBridgeOptions(allowInsecure)
        };
    }

    /// <summary>
    /// Reads the AgentBridge settings. The bridge is optional: when no
    /// <c>TSHOCK_BRIDGE_*</c> variable is present the gateway starts in REST-only
    /// mode. A partial configuration is an error rather than a silent fallback, so
    /// an operator never believes the bridge is enabled when it is not.
    /// </summary>
    private static BridgeOptions? ReadBridgeOptions(bool allowInsecure)
    {
        var rawUrl = Get("TSHOCK_BRIDGE_URL");
        var rawToken = Get("TSHOCK_BRIDGE_TOKEN");
        var rawTokenFile = Get("TSHOCK_BRIDGE_TOKEN_FILE");
        var rawTimeout = Get("TSHOCK_BRIDGE_TIMEOUT_SECONDS");

        if (rawUrl is null && rawToken is null && rawTokenFile is null && rawTimeout is null)
        {
            return null;
        }

        var url = rawUrl ?? DefaultBridgeUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var bridgeUrl) ||
            (bridgeUrl.Scheme != Uri.UriSchemeHttp && bridgeUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConfigurationException(
                "TSHOCK_BRIDGE_URL must be an absolute http(s) URL, for example http://127.0.0.1:7879.");
        }

        // Same reasoning as the REST base URL: the client appends its own route,
        // so extras in the base URL would change where the request goes.
        if (bridgeUrl.Query.Length > 0)
        {
            throw new ConfigurationException("TSHOCK_BRIDGE_URL must not contain a query string.");
        }

        if (bridgeUrl.Fragment.Length > 0)
        {
            throw new ConfigurationException("TSHOCK_BRIDGE_URL must not contain a fragment.");
        }

        if (bridgeUrl.UserInfo.Length > 0)
        {
            throw new ConfigurationException(
                "TSHOCK_BRIDGE_URL must not embed credentials (userinfo); configure TSHOCK_BRIDGE_TOKEN instead.");
        }

        if (bridgeUrl.Scheme == Uri.UriSchemeHttp && !bridgeUrl.IsLoopback && !allowInsecure)
        {
            throw new ConfigurationException(
                "Refusing to send the bridge token over plain HTTP to a non-loopback host. " +
                "Use HTTPS or set TSHOCK_MCP_ALLOW_INSECURE=true if you understand the risk.");
        }

        var token = rawToken ?? ReadTokenFile(rawTokenFile);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ConfigurationException(
                "TSHOCK_BRIDGE_TOKEN or TSHOCK_BRIDGE_TOKEN_FILE is required when any other " +
                "TSHOCK_BRIDGE_* variable is set.");
        }

        var timeoutSeconds = 10;
        if (rawTimeout is not null)
        {
            if (!int.TryParse(rawTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutSeconds))
            {
                throw new ConfigurationException(
                    "TSHOCK_BRIDGE_TIMEOUT_SECONDS must be an integer number of seconds, for example 10.");
            }

            if (timeoutSeconds is < 1 or > 300)
            {
                throw new ConfigurationException("TSHOCK_BRIDGE_TIMEOUT_SECONDS must be between 1 and 300.");
            }
        }

        return new BridgeOptions
        {
            BaseUrl = new Uri(bridgeUrl.ToString().TrimEnd('/') + "/", UriKind.Absolute),
            Token = token.Trim(),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    /// <summary>
    /// Reads the bridge secret from a file so it can be mounted as a read-only
    /// secret. The content is never logged; only the path appears in an error.
    /// </summary>
    private static string? ReadTokenFile(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                throw new ConfigurationException(
                    "TSHOCK_BRIDGE_TOKEN_FILE does not point to an existing file.");
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ConfigurationException(
                $"TSHOCK_BRIDGE_TOKEN_FILE could not be read ({ex.GetType().Name}); check the path and its permissions.");
        }
    }

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool GetBool(string name, bool fallback) =>
        Get(name) is { } raw && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Reads the timeout strictly: an absent variable keeps the default, but a
    /// present non-numeric value is a configuration error rather than a silent
    /// fallback. Range validation happens in the caller.
    /// </summary>
    private static int GetTimeoutSeconds()
    {
        var raw = Get("TSHOCK_MCP_TIMEOUT_SECONDS");
        if (raw is null)
        {
            return 15;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new ConfigurationException(
                "TSHOCK_MCP_TIMEOUT_SECONDS must be an integer number of seconds, for example 15.");
        }

        return seconds;
    }
}

/// <summary>
/// Connection settings for the loopback AgentBridge plugin. The bridge token is a
/// separate secret from the TShock REST token so one leak cannot reach both surfaces.
/// </summary>
public sealed record BridgeOptions
{
    public required Uri BaseUrl { get; init; }

    /// <summary>Bridge bearer secret. Never logged, never returned by a tool.</summary>
    public required string Token { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed class ConfigurationException(string message) : Exception(message);
