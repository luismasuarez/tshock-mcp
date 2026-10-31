using System.Globalization;

namespace TShock.McpServer.TShock;

/// <summary>
/// Runtime configuration for the gateway. Everything is sourced from environment
/// variables so the same binary can be launched by any MCP host.
/// </summary>
public sealed record TShockOptions
{
    public const string DefaultBaseUrl = "http://127.0.0.1:7878";

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
            AuditLogPath = Get("TSHOCK_MCP_AUDIT_LOG")
        };
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

public sealed class ConfigurationException(string message) : Exception(message);
