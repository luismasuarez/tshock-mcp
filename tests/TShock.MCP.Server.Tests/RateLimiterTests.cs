using TShock.MCP.Server.Tests.TestDoubles;
using TShock.McpServer.TShock;

namespace TShock.MCP.Server.Tests;

public sealed class RateLimiterTests
{
    [Fact]
    public void GlobalBucket_StopsTheCallAfterTheSharedBudget()
    {
        var time = new ManualTimeProvider();
        var limiter = Create(time, perMinute: 2);

        Assert.True(limiter.Acquire("tool-a", isWrite: false).Allowed);
        Assert.True(limiter.Acquire("tool-b", isWrite: false).Allowed);

        var denied = limiter.Acquire("tool-c", isWrite: false);

        Assert.False(denied.Allowed);
        Assert.Equal(RateLimitScope.Global, denied.Scope);
        Assert.InRange(denied.RetryAfterMs, 29_000, 30_001);
    }

    [Fact]
    public void ToolBucket_StopsOneToolWithoutStarvingAnother()
    {
        var time = new ManualTimeProvider();
        var limiter = Create(time, perMinute: 4);

        Assert.True(limiter.Acquire("tool-a", isWrite: false).Allowed);
        Assert.True(limiter.Acquire("tool-a", isWrite: false).Allowed);

        var denied = limiter.Acquire("tool-a", isWrite: false);

        Assert.False(denied.Allowed);
        Assert.Equal(RateLimitScope.Tool, denied.Scope);
        Assert.True(limiter.Acquire("tool-b", isWrite: false).Allowed);
    }

    [Fact]
    public void WriteBucket_OnlyChargesWriteTools()
    {
        var time = new ManualTimeProvider();
        var limiter = new ToolRateLimiter(
            new RateLimitOptions { PerMinute = 10, WritePerMinute = 1 },
            time);

        Assert.True(limiter.Acquire("broadcast", isWrite: true).Allowed);

        var denied = limiter.Acquire("broadcast", isWrite: true);
        Assert.False(denied.Allowed);
        Assert.InRange(denied.RetryAfterMs, 59_000, 60_001);

        Assert.True(limiter.Acquire("server_status", isWrite: false).Allowed);
    }

    [Fact]
    public void DeniedWrite_DoesNotConsumeTheGlobalBudget()
    {
        var time = new ManualTimeProvider();
        var limiter = new ToolRateLimiter(
            new RateLimitOptions { PerMinute = 2, WritePerMinute = 1 },
            time);

        Assert.True(limiter.Acquire("broadcast", isWrite: true).Allowed);
        Assert.False(limiter.Acquire("broadcast", isWrite: true).Allowed);

        // The second write was denied before touching the network and must not
        // have spent the remaining global token.
        Assert.True(limiter.Acquire("server_status", isWrite: false).Allowed);
    }

    [Fact]
    public void Refill_AllowsOneCallOnlyAfterTheWindow()
    {
        var time = new ManualTimeProvider();
        var limiter = Create(time, perMinute: 1);

        Assert.True(limiter.Acquire("tool-a", isWrite: false).Allowed);
        Assert.False(limiter.Acquire("tool-a", isWrite: false).Allowed);

        time.AdvanceMilliseconds(59_999);
        Assert.False(limiter.Acquire("tool-a", isWrite: false).Allowed);

        time.AdvanceMilliseconds(2);
        Assert.True(limiter.Acquire("tool-a", isWrite: false).Allowed);
    }

    [Fact]
    public void BackwardsClockStep_DoesNotCreateTokens()
    {
        var time = new ManualTimeProvider();
        var limiter = Create(time, perMinute: 1);

        Assert.True(limiter.Acquire("tool-a", isWrite: false).Allowed);

        time.Advance(TimeSpan.FromSeconds(-60));
        Assert.False(limiter.Acquire("tool-a", isWrite: false).Allowed);

        time.Advance(TimeSpan.FromSeconds(120));
        Assert.True(limiter.Acquire("tool-a", isWrite: false).Allowed);
    }

    [Fact]
    public void UnmatchedTool_OnlySpendsTheGlobalBudget()
    {
        var time = new ManualTimeProvider();
        var limiter = Create(time, perMinute: 1);

        Assert.True(limiter.Acquire(null, isWrite: false).Allowed);

        var denied = limiter.Acquire(null, isWrite: false);
        Assert.False(denied.Allowed);
        Assert.Equal(RateLimitScope.Global, denied.Scope);
    }

    [Fact]
    public void ParallelCalls_NeverExceedTheBudget()
    {
        var time = new ManualTimeProvider();
        var limiter = Create(time, perMinute: 10);
        var allowed = 0;

        Parallel.For(0, 100, index =>
        {
            if (limiter.Acquire($"tool-{index}", isWrite: false).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(10, allowed);
    }

    [Fact]
    public void Constructor_RejectsAZeroBudget()
    {
        var time = new ManualTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolRateLimiter(
            new RateLimitOptions { PerMinute = 0, WritePerMinute = 1 },
            time));
    }

    private static ToolRateLimiter Create(ManualTimeProvider time, int perMinute) =>
        new(new RateLimitOptions { PerMinute = perMinute, WritePerMinute = 20 }, time);
}
