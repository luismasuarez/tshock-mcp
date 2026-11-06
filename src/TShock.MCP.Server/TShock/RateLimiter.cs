namespace TShock.McpServer.TShock;

/// <summary>
/// Invocation budget for the gateway. Rates are expressed per minute because
/// that is how an operator reasons about them, but the buckets refill
/// continuously so a burst does not reset at a wall-clock boundary.
/// </summary>
public sealed record RateLimitOptions
{
    public const int DefaultPerMinute = 120;
    public const int DefaultWritePerMinute = 20;
    public const int MinimumPerMinute = 1;
    public const int MaximumPerMinute = 60_000;

    /// <summary>Values used when the operator does not set the variables.</summary>
    public static RateLimitOptions Default { get; } = new()
    {
        PerMinute = DefaultPerMinute,
        WritePerMinute = DefaultWritePerMinute
    };

    /// <summary>Budget shared by every tool invocation.</summary>
    public required int PerMinute { get; init; }

    /// <summary>Budget shared by the tools whose read-only hint is false.</summary>
    public required int WritePerMinute { get; init; }
}

/// <summary>Bucket that denied a call. It is only used for diagnostics.</summary>
internal enum RateLimitScope
{
    Global,
    Write,
    Tool
}

/// <summary>
/// Result of one acquisition attempt. <see cref="RetryAfterMs"/> is only
/// meaningful when <see cref="Allowed"/> is false.
/// </summary>
internal readonly record struct RateLimitDecision(
    bool Allowed,
    int RetryAfterMs,
    RateLimitScope Scope)
{
    internal static RateLimitDecision Permit { get; } = new(true, 0, RateLimitScope.Global);
}

/// <summary>
/// Token buckets for tool invocations. Three limits apply at once:
/// <list type="bullet">
/// <item>a global bucket shared by every call;</item>
/// <item>a write bucket shared by the tools whose <c>readOnlyHint</c> is false;</item>
/// <item>a per-tool bucket capped at half of its class budget, so one tool cannot
/// drain the class and starve the others.</item>
/// </list>
/// Acquisition is atomic: the call consumes a token from every applicable bucket
/// or from none. Charging the global bucket and then denying on the write bucket
/// would let a write loop consume the read budget without ever reaching TShock.
/// </summary>
internal sealed class ToolRateLimiter
{
    /// <summary>
    /// A tool may use at most half of the budget of its class. With the default
    /// 120 reads/minute this caps one tool at 60/minute; with 20 writes/minute it
    /// caps one write tool at 10/minute.
    /// </summary>
    internal const int ToolFairShare = 2;

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly Bucket _global;
    private readonly Bucket _write;
    private readonly Dictionary<string, Bucket> _tools = new(StringComparer.Ordinal);

    internal ToolRateLimiter(RateLimitOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        Validate(options.PerMinute, nameof(options.PerMinute));
        Validate(options.WritePerMinute, nameof(options.WritePerMinute));

        _time = time;
        _global = new Bucket(options.PerMinute, RateLimitScope.Global, time);
        _write = new Bucket(options.WritePerMinute, RateLimitScope.Write, time);
    }

    /// <summary>
    /// Attempts to spend one invocation. <paramref name="toolName"/> is the name
    /// of the primitive matched by the SDK, never the raw argument sent by the
    /// client: an unknown name must not create a bucket. When it is null only the
    /// global bucket applies, because the call cannot reach a tool.
    /// </summary>
    internal RateLimitDecision Acquire(string? toolName, bool isWrite)
    {
        lock (_sync)
        {
            var now = _time.GetTimestamp();

            var global = _global;
            var write = isWrite ? _write : null;
            var tool = toolName is null
                ? null
                : GetToolBucket(toolName, isWrite ? _write.PerMinute : _global.PerMinute);

            global.Refill(now);
            write?.Refill(now);
            tool?.Refill(now);

            Bucket? denied = null;
            var retryAfterMs = 0;

            // When more than one bucket is empty the binding wait is the longest
            // one, not the first one encountered.
            Consider(global);
            Consider(write);
            Consider(tool);

            if (denied is not null)
            {
                return new RateLimitDecision(false, retryAfterMs, denied.Scope);
            }

            global.Take();
            write?.Take();
            tool?.Take();

            return RateLimitDecision.Permit;

            void Consider(Bucket? bucket)
            {
                if (bucket is null || bucket.Tokens >= 1)
                {
                    return;
                }

                var wait = bucket.RetryAfterMilliseconds;
                if (denied is null || wait > retryAfterMs)
                {
                    denied = bucket;
                    retryAfterMs = wait;
                }
            }
        }
    }

    private Bucket GetToolBucket(string name, int classPerMinute)
    {
        if (_tools.TryGetValue(name, out var bucket))
        {
            return bucket;
        }

        bucket = new Bucket(
            Math.Max(1, classPerMinute / ToolFairShare),
            RateLimitScope.Tool,
            _time);
        _tools.Add(name, bucket);
        return bucket;
    }

    private static void Validate(int perMinute, string parameterName)
    {
        if (perMinute is < RateLimitOptions.MinimumPerMinute or > RateLimitOptions.MaximumPerMinute)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                perMinute,
                $"The rate limit must be between {RateLimitOptions.MinimumPerMinute} and " +
                $"{RateLimitOptions.MaximumPerMinute} invocations per minute.");
        }
    }

    /// <summary>
    /// A continuously refilling bucket. Tokens are fractional; a call needs one
    /// whole token. The clock is monotonic and a backwards step is ignored instead
    /// of creating tokens.
    /// </summary>
    private sealed class Bucket
    {
        private readonly TimeProvider _time;
        private readonly double _capacity;
        private readonly double _tokensPerSecond;
        private double _tokens;
        private long _lastTimestamp;

        internal Bucket(int perMinute, RateLimitScope scope, TimeProvider time)
        {
            _time = time;
            _capacity = perMinute;
            _tokensPerSecond = perMinute / 60.0;
            _tokens = perMinute;
            _lastTimestamp = time.GetTimestamp();
            Scope = scope;
            PerMinute = perMinute;
        }

        internal RateLimitScope Scope { get; }

        internal int PerMinute { get; }

        internal double Tokens => _tokens;

        internal void Refill(long now)
        {
            if (now <= _lastTimestamp)
            {
                return;
            }

            var elapsedSeconds = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _tokensPerSecond);
            _lastTimestamp = now;
        }

        internal int RetryAfterMilliseconds
        {
            get
            {
                var seconds = (1 - _tokens) / _tokensPerSecond;
                var milliseconds = Math.Ceiling(seconds * 1000);
                return (int)Math.Clamp(milliseconds, 1, int.MaxValue);
            }
        }

        /// <summary>Only called after the caller verified <see cref="Tokens"/> is at least one.</summary>
        internal void Take() => _tokens -= 1;
    }
}
