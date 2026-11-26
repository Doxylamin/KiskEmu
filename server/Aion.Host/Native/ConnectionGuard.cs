using System.Net;
using System.Net.Sockets;

namespace Aion.Host.Native;

/// <summary>
/// Per-IP cap on the connections one native port holds at once (Aion:Native:MaxConnectionsPerIp), so one address cannot
/// take every slot of <see cref="NativeOptions.MaxConnections"/>. Each listener has its own guard, so a player's login and
/// game connections do not count against each other. Loopback and private (LAN) peers are never limited: they are the
/// operator's own tools (tools/load_test.py) and players on the same network, and a host behind NAT or a proxy that sees
/// every player from one private address would otherwise cap them all together. Entries exist only while an address holds
/// a connection, so memory is bounded by the open connections. 0 or less turns the cap off.
/// Ported from zaraza87's A2Core fork (its ConnectionGuard), without the per-minute rate limit and cooldown block.
/// </summary>
public sealed class ConnectionGuard(int maxPerIp)
{
    readonly Dictionary<IPAddress, int> open = new();
    readonly object gate = new();
    long refused;

    /// <summary>Connections refused because their address was at the cap, since start.</summary>
    public long Refused => Interlocked.Read(ref refused);

    /// <summary>Addresses that currently hold at least one counted connection.</summary>
    public int TrackedAddresses { get { lock (gate) return open.Count; } }

    /// <summary>True when a connection from <paramref name="address"/> may be served (it is then counted); false to drop it.</summary>
    public bool TryAccept(IPAddress address)
    {
        if (maxPerIp <= 0 || Exempt(address)) return true;
        address = Normalize(address);
        lock (gate)
        {
            open.TryGetValue(address, out var count);
            if (count >= maxPerIp)
            {
                Interlocked.Increment(ref refused);
                return false;
            }
            open[address] = count + 1;
            return true;
        }
    }

    /// <summary>A connection from <paramref name="address"/> ended; balances every <see cref="TryAccept"/> that returned true.</summary>
    public void Release(IPAddress address)
    {
        if (maxPerIp <= 0 || Exempt(address)) return;
        address = Normalize(address);
        lock (gate)
        {
            if (!open.TryGetValue(address, out var count)) return;
            if (count <= 1) open.Remove(address);
            else open[address] = count - 1;
        }
    }

    /// <summary>Loopback, the RFC 1918 private ranges, link-local, and IPv6 unique-local / site-local addresses.</summary>
    public static bool Exempt(IPAddress address)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>A dual-stack socket reports IPv4 peers as ::ffff:a.b.c.d; count them as the IPv4 address.</summary>
    static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
