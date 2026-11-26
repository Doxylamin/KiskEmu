using System.Text;

namespace Aion.World;

/// <summary>
/// Ring buffer of the last client and server game messages on one connection: opcode, direction, length, time and the first
/// <see cref="PreviewBytes"/> bytes of the body (disconnect forensics and "/bug" reports). Fixed size per connection
/// (about 14 KB, allocated once); recording copies at most <see cref="PreviewBytes"/> bytes. Written and read on the
/// session's own loop only.
/// </summary>
public sealed class ConnectionPacketTrace
{
    /// <summary>Messages kept.</summary>
    public const int Capacity = 256;
    /// <summary>Body bytes kept per message (after the opcode).</summary>
    public const int PreviewBytes = 40;
    /// <summary>Disconnect logs show this many of the latest.</summary>
    const int LogCount = 40;

    /// <summary>Client requests whose bodies are not kept (the login carries the game ticket).</summary>
    static readonly HashSet<ushort> Redacted = [0x3613];

    public readonly record struct Entry(bool Outbound, ushort Opcode, int Length, long Ms, byte[] Preview, bool Redacted);

    readonly (bool Outbound, ushort Opcode, int Length, long Ms, byte Kept, bool Redacted)[] ring = new (bool, ushort, int, long, byte, bool)[Capacity];
    readonly byte[] previews = new byte[Capacity * PreviewBytes];
    int count;
    int next;

    public void RecordClient(ushort opcode, int length, long nowMs) => Push(false, opcode, length, nowMs, default);
    public void RecordServer(ushort opcode, int length, long nowMs) => Push(true, opcode, length, nowMs, default);
    /// <summary>A whole message (opcode first): its opcode, length and the start of its body.</summary>
    public void RecordClient(ReadOnlySpan<byte> body, long nowMs) => Push(false, Opcode(body), body.Length, nowMs, body);
    public void RecordServer(ReadOnlySpan<byte> body, long nowMs) => Push(true, Opcode(body), body.Length, nowMs, body);

    static ushort Opcode(ReadOnlySpan<byte> body) => body.Length >= 2 ? (ushort)(body[0] | body[1] << 8) : (ushort)0;

    void Push(bool outbound, ushort opcode, int length, long nowMs, ReadOnlySpan<byte> body)
    {
        var redacted = !outbound && Redacted.Contains(opcode);
        var payload = body.Length > 2 && !redacted ? body[2..] : default;
        var kept = Math.Min(payload.Length, PreviewBytes);
        payload[..kept].CopyTo(previews.AsSpan(next * PreviewBytes, PreviewBytes));
        ring[next] = (outbound, opcode, length, nowMs, (byte)kept, redacted);
        next = (next + 1) % Capacity;
        if (count < Capacity) count++;
    }

    /// <summary>The messages kept, oldest first.</summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        var list = new List<Entry>(count);
        var start = count < Capacity ? 0 : next;
        for (var i = 0; i < count; i++)
        {
            var slot = (start + i) % Capacity;
            var (outbound, opcode, length, ms, kept, redacted) = ring[slot];
            list.Add(new Entry(outbound, opcode, length, ms, previews.AsSpan(slot * PreviewBytes, kept).ToArray(), redacted));
        }
        return list;
    }

    public string FormatRecent()
    {
        if (count == 0) return "(no packets recorded)";
        var sb = new StringBuilder();
        var shown = Math.Min(count, LogCount);
        var start = (next - shown + Capacity) % Capacity;
        for (var i = 0; i < shown; i++)
        {
            var (outbound, opcode, length, ms, _, _) = ring[(start + i) % Capacity];
            sb.Append(outbound ? "S" : "C").Append(' ').Append(ms).Append("ms 0x").Append(opcode.ToString("x4"))
                .Append(' ').Append(length).Append("B");
            if (i + 1 < shown) sb.Append(" | ");
        }
        return sb.ToString();
    }
}
