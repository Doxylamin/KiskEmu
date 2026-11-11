namespace Aion.Host.Diagnostics;

/// <summary>
/// Client requests this server handled without a reply (the game loop's "unanswered opcode" case, NativeListeners.cs), per
/// opcode: how often, first and last seen, and the last few decrypted bodies. For the staff dashboard's diagnostics card, so
/// a staff member can click through a client feature that is not implemented yet and read the requests it sends.
/// In memory only and bounded: <see cref="MaxOpcodes"/> opcodes (the least recently seen one makes room), the last
/// <see cref="MaxBodies"/> bodies of each, each cut to <see cref="MaxBodyBytes"/> bytes. A restart starts it empty.
/// </summary>
public sealed class UnansweredRequests(TimeProvider? clock = null)
{
    public const int MaxOpcodes = 200, MaxBodies = 8, MaxBodyBytes = 128;

    /// <summary>One body as received: the whole decrypted packet, opcode bytes included; Hex holds at most MaxBodyBytes.</summary>
    public sealed record Body(DateTimeOffset At, int Length, string Hex);
    public sealed record Entry(int Opcode, long Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, IReadOnlyList<Body> Bodies);
    public sealed record Snapshot(DateTimeOffset Since, long Evicted, IReadOnlyList<Entry> Opcodes);

    sealed class Slot(DateTimeOffset at)
    {
        public long Count;
        public DateTimeOffset FirstSeen = at, LastSeen = at;
        public readonly Queue<(DateTimeOffset At, int Length, byte[] Bytes)> Bodies = new();
    }

    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object sync = new();
    readonly Dictionary<int, Slot> slots = [];
    long evicted;
    DateTimeOffset since = (clock ?? TimeProvider.System).GetUtcNow();

    public void Record(int opcode, ReadOnlySpan<byte> body)
    {
        var now = time.GetUtcNow();
        var kept = body[..Math.Min(body.Length, MaxBodyBytes)].ToArray();
        lock (sync)
        {
            if (!slots.TryGetValue(opcode, out var slot))
            {
                if (slots.Count >= MaxOpcodes)
                {
                    var oldest = slots.MinBy(s => s.Value.LastSeen).Key;
                    slots.Remove(oldest);
                    evicted++;
                }
                slots[opcode] = slot = new Slot(now);
            }
            slot.Count++;
            slot.LastSeen = now;
            slot.Bodies.Enqueue((now, body.Length, kept));
            while (slot.Bodies.Count > MaxBodies) slot.Bodies.Dequeue();
        }
    }

    /// <summary>Every opcode, most recently seen first; each one's bodies newest first.</summary>
    public Snapshot Read()
    {
        lock (sync)
        {
            var entries = slots.OrderByDescending(s => s.Value.LastSeen).Select(s => new Entry(s.Key, s.Value.Count, s.Value.FirstSeen, s.Value.LastSeen,
                s.Value.Bodies.Reverse().Select(b => new Body(b.At, b.Length, Convert.ToHexString(b.Bytes))).ToArray())).ToArray();
            return new Snapshot(since, evicted, entries);
        }
    }

    /// <summary>Forgets everything (before a staff member tries one feature); the number of opcodes dropped.</summary>
    public int Clear()
    {
        lock (sync)
        {
            var count = slots.Count;
            slots.Clear();
            evicted = 0;
            since = time.GetUtcNow();
            return count;
        }
    }
}
