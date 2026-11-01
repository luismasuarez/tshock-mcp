using Xunit;

namespace TShock.AgentBridge.Tests;

/// <summary>
/// The admin owns the policy: the bridge never invents an owner that the server
/// did not allow. Environment variables are process-global, so each test restores
/// them on dispose.
/// </summary>
public sealed class RegionOwnerPolicyTests
{
    private const string OwnerVariable = "TSHOCK_BRIDGE_REGION_OWNER";
    private const string OwnersVariable = "TSHOCK_BRIDGE_REGION_OWNERS";

    [Fact]
    public void FromEnvironment_UsesTheDefaultOwnerWhenNothingIsConfigured()
    {
        using var env = new EnvironmentScope();

        var policy = RegionOwnerPolicy.FromEnvironment();

        Assert.Equal("mcp-gateway", policy.DefaultOwner);
        Assert.True(policy.IsAllowed("mcp-gateway"));
        Assert.False(policy.IsAllowed("alice"));
    }

    [Fact]
    public void FromEnvironment_ReadsACustomDefaultOwner()
    {
        using var env = new EnvironmentScope();
        env.Set(OwnerVariable, "  server-admin  ");

        var policy = RegionOwnerPolicy.FromEnvironment();

        Assert.Equal("server-admin", policy.DefaultOwner);
        Assert.True(policy.IsAllowed("server-admin"));
    }

    [Fact]
    public void FromEnvironment_ReadsAnAllowListSeparatedByCommasOrSemicolons()
    {
        using var env = new EnvironmentScope();
        env.Set(OwnersVariable, "alice, bob; carol ,, ");

        var policy = RegionOwnerPolicy.FromEnvironment();

        Assert.True(policy.IsAllowed("alice"));
        Assert.True(policy.IsAllowed("bob"));
        Assert.True(policy.IsAllowed("carol"));
        Assert.False(policy.IsAllowed("mallory"));
    }

    [Fact]
    public void FromEnvironment_ComparesOwnersCaseInsensitively()
    {
        using var env = new EnvironmentScope();
        env.Set(OwnersVariable, "Alice");

        var policy = RegionOwnerPolicy.FromEnvironment();

        Assert.True(policy.IsAllowed("alice"));
        Assert.True(policy.IsAllowed("ALICE"));
    }

    [Fact]
    public void FromEnvironment_IgnoresUnusableEntries()
    {
        using var env = new EnvironmentScope();
        env.Set(OwnersVariable, $"{new string('a', 65)},bad\nname,ok");

        var policy = RegionOwnerPolicy.FromEnvironment();

        Assert.False(policy.IsAllowed(new string('a', 65)));
        Assert.False(policy.IsAllowed("bad\nname"));
        Assert.True(policy.IsAllowed("ok"));
    }

    [Fact]
    public void FromEnvironment_FallsBackWhenTheDefaultOwnerIsUnusable()
    {
        using var env = new EnvironmentScope();
        env.Set(OwnerVariable, "   ");

        var policy = RegionOwnerPolicy.FromEnvironment();

        Assert.Equal(RegionOwnerPolicy.DefaultOwnerName, policy.DefaultOwner);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string? _owner = Environment.GetEnvironmentVariable(OwnerVariable);
        private readonly string? _owners = Environment.GetEnvironmentVariable(OwnersVariable);

        internal EnvironmentScope()
        {
            Environment.SetEnvironmentVariable(OwnerVariable, null);
            Environment.SetEnvironmentVariable(OwnersVariable, null);
        }

        internal EnvironmentScope Set(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value);
            return this;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(OwnerVariable, _owner);
            Environment.SetEnvironmentVariable(OwnersVariable, _owners);
        }
    }
}
