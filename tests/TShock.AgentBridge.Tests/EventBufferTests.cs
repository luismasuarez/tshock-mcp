using TShock.AgentBridge;
using Xunit;

namespace TShock.AgentBridge.Tests;

public sealed class EventBufferTests
{
    private static Dictionary<string, object?> Payload(string key, object? value) =>
        new(StringComparer.Ordinal) { [key] = value };

    private static Dictionary<string, object?> Empty() => new(StringComparer.Ordinal);

    [Fact]
    public void EmptyBuffer_ReturnsAnEmptyPageAtCursorZero()
    {
        var buffer = new EventBuffer(capacity: 4);

        var result = buffer.Read(null, limit: 10);

        Assert.Equal(EventReadStatus.Ok, result.Status);
        var page = Assert.IsType<EventPage>(result.Page);
        Assert.Empty(page.Events);
        Assert.Equal(0, page.Cursor);
        Assert.Equal(0, page.Head);
        Assert.Equal(1, page.Oldest);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void Publish_AssignsMonotonicCursorsStartingAtOne()
    {
        var buffer = new EventBuffer(capacity: 4);

        var first = buffer.Publish("player.join", true, Payload("player", "Alice"));
        var second = buffer.Publish("chat", true, Payload("text", "hello"));

        Assert.Equal(1, first);
        Assert.Equal(2, second);

        var page = Assert.IsType<EventPage>(buffer.Read(null, limit: 10).Page);
        Assert.Equal(new long[] { 1, 2 }, page.Events.Select(e => e.Cursor));
        Assert.Equal(2, page.Cursor);
        Assert.Equal(2, page.Head);
        Assert.Equal(1, page.Oldest);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void Read_IsExclusiveOfTheGivenCursor()
    {
        var buffer = new EventBuffer(capacity: 4);
        buffer.Publish("a", false, Empty());
        buffer.Publish("b", false, Empty());

        var page = Assert.IsType<EventPage>(buffer.Read(1, limit: 10).Page);

        var only = Assert.Single(page.Events);
        Assert.Equal(2, only.Cursor);
    }

    [Fact]
    public void Read_RespectsLimitAndPagesForward()
    {
        var buffer = new EventBuffer(capacity: 10);
        for (var i = 0; i < 5; i++)
        {
            buffer.Publish($"event.{i}", false, Empty());
        }

        var first = Assert.IsType<EventPage>(buffer.Read(0, limit: 2).Page);
        Assert.Equal(new long[] { 1, 2 }, first.Events.Select(e => e.Cursor));
        Assert.Equal(2, first.Cursor);
        Assert.True(first.HasMore);

        var second = Assert.IsType<EventPage>(buffer.Read(first.Cursor, limit: 2).Page);
        Assert.Equal(new long[] { 3, 4 }, second.Events.Select(e => e.Cursor));
        Assert.True(second.HasMore);

        var third = Assert.IsType<EventPage>(buffer.Read(second.Cursor, limit: 2).Page);
        Assert.Equal(new long[] { 5 }, third.Events.Select(e => e.Cursor));
        Assert.Equal(5, third.Cursor);
        Assert.False(third.HasMore);
    }

    [Fact]
    public void Read_AtHead_ReturnsAnEmptyPageAtTheSameCursor()
    {
        var buffer = new EventBuffer(capacity: 4);
        buffer.Publish("a", false, Empty());

        var page = Assert.IsType<EventPage>(buffer.Read(1, limit: 10).Page);

        Assert.Empty(page.Events);
        Assert.Equal(1, page.Cursor);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void Read_WithoutCursorReturnsTheRetainedBacklogAfterWrap()
    {
        var buffer = new EventBuffer(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            buffer.Publish($"event.{i}", false, Empty());
        }

        Assert.Equal(3, buffer.Oldest);
        Assert.Equal(5, buffer.Head);
        Assert.Equal(3, buffer.Count);

        var page = Assert.IsType<EventPage>(buffer.Read(null, limit: 10).Page);
        Assert.Equal(new long[] { 3, 4, 5 }, page.Events.Select(e => e.Cursor));
        Assert.Equal(3, page.Oldest);
    }

    [Fact]
    public void Read_BeforeOldestReportsExpiredWithResumeCursor()
    {
        var buffer = new EventBuffer(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            buffer.Publish($"event.{i}", false, Empty());
        }

        var result = buffer.Read(1, limit: 10);

        Assert.Equal(EventReadStatus.CursorExpired, result.Status);
        Assert.Null(result.Page);
        Assert.Equal(3, result.Oldest);
        Assert.Equal(5, result.Head);
        Assert.Equal(2, result.ResumeCursor);
        Assert.Equal("evicted", result.Reason);
    }

    [Fact]
    public void Read_AtResumeCursorReturnsEverythingRetained()
    {
        var buffer = new EventBuffer(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            buffer.Publish($"event.{i}", false, Empty());
        }

        var page = Assert.IsType<EventPage>(buffer.Read(2, limit: 10).Page);

        Assert.Equal(new long[] { 3, 4, 5 }, page.Events.Select(e => e.Cursor));
    }

    [Fact]
    public void Read_AheadOfHeadReportsUnknown()
    {
        var buffer = new EventBuffer(capacity: 4);
        buffer.Publish("a", false, Empty());

        var result = buffer.Read(2, limit: 10);

        Assert.Equal(EventReadStatus.CursorUnknown, result.Status);
        Assert.Null(result.Page);
        Assert.Equal(1, result.Head);
        Assert.Equal("unknown", result.Reason);
    }

    [Fact]
    public void Buffer_RetainsExactlyItsCapacity()
    {
        var buffer = new EventBuffer(capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            buffer.Publish($"event.{i}", false, Empty());
        }

        var page = Assert.IsType<EventPage>(buffer.Read(null, limit: 10).Page);

        Assert.Equal(new long[] { 8, 9, 10 }, page.Events.Select(e => e.Cursor));
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Publish_KeepsTheUntrustedFlagAndPayload()
    {
        var buffer = new EventBuffer(capacity: 4);

        buffer.Publish("chat", true, Payload("player", "Alice"));

        var entry = Assert.Single(Assert.IsType<EventPage>(buffer.Read(null, limit: 10).Page).Events);
        Assert.True(entry.Untrusted);
        Assert.Equal("chat", entry.Type);
        Assert.Equal("Alice", entry.Data["player"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", entry.At);
    }

    [Fact]
    public void Publish_ProducesUniqueSequentialCursorsUnderConcurrency()
    {
        var buffer = new EventBuffer(capacity: 2_000);

        Parallel.For(0, 1_000, _ => buffer.Publish("chat", false, Empty()));

        Assert.Equal(1_000, buffer.Head);
        var page = Assert.IsType<EventPage>(buffer.Read(0, limit: EventBuffer.MaxPageSize).Page);
        Assert.Equal(EventBuffer.MaxPageSize, page.Events.Length);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Snapshot_ReportsConsistentCounters()
    {
        var buffer = new EventBuffer(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            buffer.Publish($"event.{i}", false, Empty());
        }

        var stats = buffer.Snapshot();

        Assert.Equal(5, stats.Head);
        Assert.Equal(3, stats.Oldest);
        Assert.Equal(3, stats.Count);
        Assert.Equal(buffer.Session, stats.Session);
    }

    [Fact]
    public void Session_IsStableAndUniquePerBuffer()
    {
        var first = new EventBuffer(capacity: 4);
        var second = new EventBuffer(capacity: 4);

        Assert.Matches("^[0-9a-f]{32}$", first.Session);
        Assert.Equal(first.Session, first.Session);
        Assert.NotEqual(first.Session, second.Session);
    }

    [Fact]
    public void Read_RejectsInvalidArguments()
    {
        var buffer = new EventBuffer(capacity: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Read(0, limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Read(0, limit: EventBuffer.MaxPageSize + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Read(-1, limit: 10));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventBuffer(capacity: 0));
    }
}
