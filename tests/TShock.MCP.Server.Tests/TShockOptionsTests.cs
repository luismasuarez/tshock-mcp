using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

/// <summary>
/// Environment variables are process-global, so these tests share one
/// non-parallel collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Environment configuration";
}

[Collection(EnvironmentCollection.Name)]
public sealed class TShockOptionsTests
{
    [Fact]
    public void FromEnvironment_ThrowsWhenTokenIsMissing()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_TOKEN", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://127.0.0.1:7878")]
    public void FromEnvironment_ThrowsWhenUrlIsInvalid(string url)
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_REST_URL", url);

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_REST_URL", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7878/?foo=bar", "query")]
    [InlineData("http://127.0.0.1:7878/#fragment", "fragment")]
    [InlineData("http://user:secret@127.0.0.1:7878", "credentials")]
    public void FromEnvironment_ThrowsWhenUrlHasQueryFragmentOrUserInfo(string url, string expected)
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_REST_URL", url);

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_REST_URL", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromEnvironment_KeepsSubpathWhenNormalizingTrailingSlash()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_REST_URL", "http://127.0.0.1:7878/tshock/rest");

        var options = TShockOptions.FromEnvironment();

        Assert.Equal("http://127.0.0.1:7878/tshock/rest/", options.BaseUrl.ToString());
    }

    [Fact]
    public void FromEnvironment_ThrowsForPlainHttpToNonLoopbackHost()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_REST_URL", "http://tshock.example.com:7878");

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("plain HTTP", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TSHOCK_MCP_ALLOW_INSECURE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEnvironment_AllowsPlainHttpToNonLoopbackHostWhenInsecureOptIn()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_REST_URL", "http://tshock.example.com:7878");
        env.Set("TSHOCK_MCP_ALLOW_INSECURE", "true");

        var options = TShockOptions.FromEnvironment();

        Assert.True(options.AllowInsecure);
        Assert.Equal("tshock.example.com", options.BaseUrl.Host);
        Assert.Equal(Uri.UriSchemeHttp, options.BaseUrl.Scheme);
    }

    [Fact]
    public void FromEnvironment_AllowsPlainHttpToLoopbackWithoutInsecureOptIn()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_REST_URL", "http://127.0.0.1:7878");

        var options = TShockOptions.FromEnvironment();

        Assert.False(options.AllowInsecure);
        Assert.True(options.BaseUrl.IsLoopback);
        Assert.Equal("http://127.0.0.1:7878/", options.BaseUrl.ToString());
    }

    [Fact]
    public void FromEnvironment_UsesDocumentedDefaults()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");

        var options = TShockOptions.FromEnvironment();

        Assert.Equal(new Uri(TShockOptions.DefaultBaseUrl + "/"), options.BaseUrl);
        Assert.Equal("token", options.Token);
        Assert.False(options.AllowWrite);
        Assert.False(options.ShowIps);
        Assert.False(options.AllowInsecure);
        Assert.True(options.AllowElicitation);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Timeout);
        Assert.Null(options.AuditLogPath);
    }

    [Fact]
    public void FromEnvironment_ParsesWriteShowIpsTimeoutAndAuditPath()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "  token-with-spaces  ");
        env.Set("TSHOCK_REST_URL", "https://tshock.example.com:8443/api/");
        env.Set("TSHOCK_MCP_ALLOW_WRITE", "TRUE");
        env.Set("TSHOCK_MCP_SHOW_IPS", "true");
        env.Set("TSHOCK_MCP_TIMEOUT_SECONDS", "42");
        env.Set("TSHOCK_MCP_AUDIT_LOG", "/tmp/tshock-audit-test.jsonl");

        var options = TShockOptions.FromEnvironment();

        Assert.Equal("token-with-spaces", options.Token);
        Assert.Equal("https://tshock.example.com:8443/api/", options.BaseUrl.ToString());
        Assert.True(options.AllowWrite);
        Assert.True(options.ShowIps);
        Assert.Equal(TimeSpan.FromSeconds(42), options.Timeout);
        Assert.Equal("/tmp/tshock-audit-test.jsonl", options.AuditLogPath);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    public void FromEnvironment_ThrowsWhenTimeoutIsOutOfRange(string timeout)
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_MCP_TIMEOUT_SECONDS", timeout);

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_MCP_TIMEOUT_SECONDS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void FromEnvironment_ThrowsWhenTimeoutIsNotAnInteger(string timeout)
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_MCP_TIMEOUT_SECONDS", timeout);

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_MCP_TIMEOUT_SECONDS", exception.Message, StringComparison.Ordinal);
        Assert.Contains("integer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromEnvironment_UsesDefaultTimeoutWhenVariableIsAbsent()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");

        var options = TShockOptions.FromEnvironment();

        Assert.Equal(TimeSpan.FromSeconds(15), options.Timeout);
    }

    [Fact]
    public void FromEnvironment_CanDisableElicitation()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_MCP_ALLOW_ELICITATION", "false");

        var options = TShockOptions.FromEnvironment();

        Assert.False(options.AllowElicitation);
    }

    [Fact]
    public void FromEnvironment_LeavesTheBridgeDisabledByDefault()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");

        var options = TShockOptions.FromEnvironment();

        Assert.Null(options.Bridge);
    }

    [Fact]
    public void FromEnvironment_ReadsBridgeSettings()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_BRIDGE_URL", "http://127.0.0.1:7879");
        env.Set("TSHOCK_BRIDGE_TOKEN", "  bridge-secret  ");
        env.Set("TSHOCK_BRIDGE_TIMEOUT_SECONDS", "20");

        var options = TShockOptions.FromEnvironment();

        var bridge = Assert.IsType<BridgeOptions>(options.Bridge);
        Assert.Equal("http://127.0.0.1:7879/", bridge.BaseUrl.ToString());
        Assert.Equal("bridge-secret", bridge.Token);
        Assert.Equal(TimeSpan.FromSeconds(20), bridge.Timeout);
    }

    [Fact]
    public void FromEnvironment_ReadsBridgeTokenFromFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "file-secret\n");
            using var env = new EnvironmentScope();
            env.ClearAll();
            env.Set("TSHOCK_TOKEN", "token");
            env.Set("TSHOCK_BRIDGE_TOKEN_FILE", path);

            var options = TShockOptions.FromEnvironment();

            var bridge = Assert.IsType<BridgeOptions>(options.Bridge);
            Assert.Equal("file-secret", bridge.Token);
            Assert.Equal(new Uri(TShockOptions.DefaultBridgeUrl + "/"), bridge.BaseUrl);
            Assert.Equal(TimeSpan.FromSeconds(10), bridge.Timeout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromEnvironment_PrefersTheInlineBridgeTokenOverTheFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "file-secret");
            using var env = new EnvironmentScope();
            env.ClearAll();
            env.Set("TSHOCK_TOKEN", "token");
            env.Set("TSHOCK_BRIDGE_TOKEN", "inline-secret");
            env.Set("TSHOCK_BRIDGE_TOKEN_FILE", path);

            var options = TShockOptions.FromEnvironment();

            Assert.Equal("inline-secret", options.Bridge!.Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromEnvironment_ThrowsWhenTheBridgeTokenIsMissing()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_BRIDGE_URL", "http://127.0.0.1:7879");

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_BRIDGE_TOKEN", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEnvironment_ThrowsWhenTheBridgeTokenFileDoesNotExist()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_BRIDGE_TOKEN_FILE", "/nonexistent/bridge-token");

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_BRIDGE_TOKEN_FILE", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:7879/?cursor=1", "query")]
    [InlineData("http://127.0.0.1:7879/#fragment", "fragment")]
    [InlineData("http://user:secret@127.0.0.1:7879", "credentials")]
    public void FromEnvironment_ThrowsWhenTheBridgeUrlHasQueryFragmentOrUserInfo(
        string url,
        string expected)
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_BRIDGE_URL", url);
        env.Set("TSHOCK_BRIDGE_TOKEN", "bridge-secret");

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_BRIDGE_URL", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromEnvironment_ThrowsForPlainHttpBridgeToNonLoopbackHost()
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_BRIDGE_URL", "http://bridge.example.com:7879");
        env.Set("TSHOCK_BRIDGE_TOKEN", "bridge-secret");

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("plain HTTP", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("301")]
    public void FromEnvironment_ThrowsWhenTheBridgeTimeoutIsInvalid(string timeout)
    {
        using var env = new EnvironmentScope();
        env.ClearAll();
        env.Set("TSHOCK_TOKEN", "token");
        env.Set("TSHOCK_BRIDGE_TOKEN", "bridge-secret");
        env.Set("TSHOCK_BRIDGE_TIMEOUT_SECONDS", timeout);

        var exception = Assert.Throws<ConfigurationException>(() => TShockOptions.FromEnvironment());

        Assert.Contains("TSHOCK_BRIDGE_TIMEOUT_SECONDS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEnvironment_KeepsExistingValuesAfterScopeDisposal()
    {
        const string name = "TSHOCK_MCP_SHOW_IPS";
        var before = Environment.GetEnvironmentVariable(name);

        try
        {
            Environment.SetEnvironmentVariable(name, "false");

            using (var env = new EnvironmentScope())
            {
                env.ClearAll();
                env.Set("TSHOCK_MCP_SHOW_IPS", "true");
                Assert.Equal("true", Environment.GetEnvironmentVariable(name));
            }

            Assert.Equal("false", Environment.GetEnvironmentVariable(name));
            Assert.Null(Environment.GetEnvironmentVariable("TSHOCK_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
        }
    }
}
