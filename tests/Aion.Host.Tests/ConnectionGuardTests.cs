using System.Net;
using Aion.Host.Native;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>The per-IP connection cap on the native ports (Aion:Native:MaxConnectionsPerIp).</summary>
public sealed class ConnectionGuardTests
{
    // Documentation addresses (RFC 5737 / RFC 3849): public, so the cap applies.
    static readonly IPAddress Peer = IPAddress.Parse("203.0.113.7");
    static readonly IPAddress Other = IPAddress.Parse("198.51.100.20");

    [Fact]
    public void One_address_is_capped_while_others_still_get_in()
    {
        var guard = new ConnectionGuard(2);
        Assert.True(guard.TryAccept(Peer));
        Assert.True(guard.TryAccept(Peer));
        Assert.False(guard.TryAccept(Peer));
        Assert.True(guard.TryAccept(Other));
        Assert.Equal(1, guard.Refused);
        Assert.Equal(2, guard.TrackedAddresses);
    }

    [Fact]
    public void A_closed_connection_frees_its_place_and_the_entry_goes_away()
    {
        var guard = new ConnectionGuard(1);
        Assert.True(guard.TryAccept(Peer));
        Assert.False(guard.TryAccept(Peer));
        guard.Release(Peer);
        Assert.Equal(0, guard.TrackedAddresses);
        Assert.True(guard.TryAccept(Peer));
    }

    [Fact]
    public void Release_without_accept_does_not_go_negative()
    {
        var guard = new ConnectionGuard(1);
        guard.Release(Peer);
        Assert.True(guard.TryAccept(Peer));
        Assert.False(guard.TryAccept(Peer));
    }

    [Fact]
    public void Mapped_IPv4_counts_as_the_same_peer()
    {
        var guard = new ConnectionGuard(1);
        Assert.True(guard.TryAccept(Peer.MapToIPv6()));
        Assert.False(guard.TryAccept(Peer));
        guard.Release(Peer);
        Assert.True(guard.TryAccept(Peer));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.0.9")]
    [InlineData("fe80::1")]
    [InlineData("fd00::7")]
    [InlineData("::ffff:192.168.1.10")]
    public void Loopback_and_LAN_peers_are_never_limited(string text)
    {
        var address = IPAddress.Parse(text);
        var guard = new ConnectionGuard(1);
        for (var i = 0; i < 20; i++) Assert.True(guard.TryAccept(address));
        Assert.Equal(0, guard.TrackedAddresses);
        Assert.Equal(0, guard.Refused);
    }

    [Theory]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("2001:db8::1")]
    public void Addresses_next_to_the_private_ranges_are_limited(string text) =>
        Assert.False(ConnectionGuard.Exempt(IPAddress.Parse(text)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_less_turns_the_cap_off(int limit)
    {
        var guard = new ConnectionGuard(limit);
        for (var i = 0; i < 50; i++) Assert.True(guard.TryAccept(Peer));
        Assert.Equal(0, guard.TrackedAddresses);
    }

    [Fact]
    public void Concurrent_accepts_never_exceed_the_cap()
    {
        var guard = new ConnectionGuard(3);
        var accepted = 0;
        Parallel.For(0, 200, _ => { if (guard.TryAccept(Peer)) Interlocked.Increment(ref accepted); });
        Assert.Equal(3, accepted);
        Assert.Equal(197, guard.Refused);
    }

    [Fact]
    public void The_default_leaves_room_for_other_players()
    {
        var options = new NativeOptions();
        Assert.InRange(options.MaxConnectionsPerIp, 2, options.MaxConnections - 1);
    }
}
