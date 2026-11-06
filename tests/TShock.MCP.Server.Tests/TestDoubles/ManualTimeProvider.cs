namespace TShock.MCP.Server.Tests.TestDoubles;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> for the rate limiter. It exposes a
/// monotonic timestamp with a known frequency, so tests can advance the clock by
/// exact amounts without sleeping. The wall clock is not used by the limiter.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;

    internal void AdvanceMilliseconds(long milliseconds) =>
        Advance(TimeSpan.FromMilliseconds(milliseconds));
}
