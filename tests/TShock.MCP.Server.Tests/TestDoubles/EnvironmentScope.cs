namespace TShock.MCP.Server.Tests.TestDoubles;

/// <summary>
/// Snapshots the configuration variables and restores them on dispose. Tests
/// wrap it in <c>using</c>, which compiles to a try/finally, so a failing
/// assertion can never leak environment state into other tests.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    internal static readonly string[] Names =
    {
        "TSHOCK_REST_URL",
        "TSHOCK_TOKEN",
        "TSHOCK_MCP_ALLOW_INSECURE",
        "TSHOCK_MCP_ALLOW_WRITE",
        "TSHOCK_MCP_SHOW_IPS",
        "TSHOCK_MCP_TIMEOUT_SECONDS",
        "TSHOCK_MCP_AUDIT_LOG",
        "TSHOCK_BRIDGE_URL",
        "TSHOCK_BRIDGE_TOKEN",
        "TSHOCK_BRIDGE_TOKEN_FILE",
        "TSHOCK_BRIDGE_TIMEOUT_SECONDS"
    };

    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);
    private bool _disposed;

    internal EnvironmentScope()
    {
        foreach (var name in Names)
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
        }
    }

    internal EnvironmentScope ClearAll()
    {
        foreach (var name in Names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        return this;
    }

    internal EnvironmentScope Set(string name, string? value)
    {
        if (!Names.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown configuration variable: {name}", nameof(name));
        }

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
