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
