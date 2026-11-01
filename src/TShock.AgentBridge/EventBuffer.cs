using System.Globalization;
using System.Text.Json.Serialization;

namespace TShock.AgentBridge;

/// <summary>
/// One event in the bridge ring buffer. The cursor is assigned by the buffer and is
/// strictly increasing within a plugin session; <see cref="Untrusted"/> marks payloads
/// that contain text a player can influence.
/// </summary>
internal sealed record BridgeEvent(
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("untrusted")] bool Untrusted,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?> Data);

/// <summary>Result of a cursor read. Only <see cref="EventReadStatus.Ok"/> carries a page.</summary>
internal enum EventReadStatus
{
    Ok,
    CursorExpired,
    CursorUnknown
}

internal sealed record EventPage(
    BridgeEvent[] Events,
    long Cursor,
    long Head,
    long Oldest,
    bool HasMore);

/// <summary>Consistent view of the buffer counters, used by the health endpoint.</summary>
internal sealed record EventBufferStats(long Head, long Oldest, int Count, string Session);

internal sealed record EventReadResult(EventReadStatus Status, EventPage? Page, long Oldest, long Head)
{
    /// <summary>
    /// Cursor the client should use to read everything still retained. It is always
    /// one below the oldest event, which is the exclusive lower bound of the buffer.
    /// </summary>
    public long ResumeCursor => Oldest - 1;

    /// <summary>Stable reason attached to <c>cursor_expired</c> responses.</summary>
    public string? Reason => Status switch
    {
        EventReadStatus.CursorExpired => "evicted",
        EventReadStatus.CursorUnknown => "unknown",
        _ => null
    };
}

/// <summary>Wire shape of a successful <c>GET /v1/events</c> response.</summary>
internal sealed record EventPageResponse(
    [property: JsonPropertyName("events")] BridgeEvent[] Events,
    [property: JsonPropertyName("cursor")] long Cursor,
    [property: JsonPropertyName("head")] long Head,
    [property: JsonPropertyName("oldest")] long Oldest,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("session")] string Session);

/// <summary>
/// Fixed-capacity ring buffer of server events with a monotonic cursor. Publishing is
/// cheap and serialized by a lock; readers copy the requested slice so the game thread
/// is never blocked beyond the copy. When the ring wraps, the oldest events are dropped
/// and their cursors become expired.
/// </summary>
internal sealed class EventBuffer
{
    public const int DefaultCapacity = 10_000;
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    private readonly object _gate = new();
    private readonly BridgeEvent?[] _ring;
    private int _start;
    private int _count;
    private long _head;

    public EventBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ring = new BridgeEvent?[capacity];
        Session = Guid.NewGuid().ToString("N");
    }

    /// <summary>Random identifier for this buffer epoch; it changes when the plugin restarts.</summary>
    public string Session { get; }

    public long Head
    {
        get
        {
            lock (_gate)
            {
                return _head;
            }
        }
    }

    /// <summary>Consistent snapshot of the counters for diagnostics.</summary>
    public EventBufferStats Snapshot()
    {
        lock (_gate)
        {
            return new EventBufferStats(_head, OldestUnlocked(), _count, Session);
        }
    }

    /// <summary>Cursor of the oldest retained event, or <c>head + 1</c> when empty.</summary>
    public long Oldest
    {
        get
        {
            lock (_gate)
            {
                return OldestUnlocked();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Appends an event and returns its cursor. Cursors start at 1.</summary>
    public long Publish(string type, bool untrusted, IReadOnlyDictionary<string, object?> data)
    {
        lock (_gate)
        {
            // Timestamp inside the lock so "at" follows cursor order.
            var at = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var cursor = ++_head;
            var entry = new BridgeEvent(cursor, at, type, untrusted, data);

            if (_count < _ring.Length)
            {
                _ring[(_start + _count) % _ring.Length] = entry;
                _count++;
            }
            else
            {
                _ring[_start] = entry;
                _start = (_start + 1) % _ring.Length;
            }

            return cursor;
        }
    }

    /// <summary>
    /// Reads events strictly after <paramref name="cursor"/>. A null cursor starts at
    /// the oldest retained event, which lets a new client bootstrap even after the ring
    /// has wrapped.
    /// </summary>
    public EventReadResult Read(long? cursor, int limit)
    {
        if (limit < 1 || limit > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (cursor is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        lock (_gate)
        {
            var oldest = OldestUnlocked();
            var head = _head;
            var requested = cursor ?? oldest - 1;

            if (requested < oldest - 1)
            {
                return new EventReadResult(EventReadStatus.CursorExpired, null, oldest, head);
            }

            if (requested > head)
            {
                return new EventReadResult(EventReadStatus.CursorUnknown, null, oldest, head);
            }

            var offset = (int)(requested + 1 - oldest);
            var available = (int)(head - requested);
            var take = Math.Min(limit, available);
            var events = new BridgeEvent[take];

            for (var i = 0; i < take; i++)
            {
                events[i] = _ring[(_start + offset + i) % _ring.Length]!;
            }

            var last = events.Length > 0 ? events[^1].Cursor : requested;
            var page = new EventPage(events, last, head, oldest, last < head);
            return new EventReadResult(EventReadStatus.Ok, page, oldest, head);
        }
    }

    private long OldestUnlocked() => _count == 0 ? _head + 1 : _ring[_start]!.Cursor;
}

/// <summary>
/// Bounds player-controlled strings before they reach the buffer. Truncation never
/// splits a surrogate pair, so the JSON payload stays valid for non-BMP characters.
/// </summary>
internal static class EventText
{
    public const int PlayerNameMax = 64;
    public const int ChatMax = 512;
    public const int CommandMax = 64;
    public const int NpcNameMax = 96;
    public const int RegionNameMax = 128;

    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= max)
        {
            return value;
        }

        var length = max;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
