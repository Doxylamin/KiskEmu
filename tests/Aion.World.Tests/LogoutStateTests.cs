using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// What a character keeps over a logout (GameSession.LogoutState.cs, ported from the addiboii fork 525b531): a timed buff comes
/// back with the time it had left, which does not run while logged out, and the flight gauge comes back as it was (OBSERVED in
/// the community recordings 2026-11-04 to 06).
/// </summary>
public sealed class LogoutStateTests
{
    const long Now = 1_760_300_000_000;
    const int Steak = 510715007;   // White Dragon's Chopped Steak: a 300 s food buff (HPMax up)
    const long Day = 86_400_000;

    static byte[] Select(FakeCharacterStore store) =>
        new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray();

    static byte[] Enter() => new PacketWriter().U16(0x3622).I32(1).ToArray();

    static byte[] Logout() => new PacketWriter().U16(0x3630).ToArray();

    static async Task<(GameSession Session, List<byte[]> Entry)> RelogAsync(FakeCharacterStore store, long now)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(9)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(Select(store), now);
        return (session, await session.HandleAsync(Enter(), now));
    }

    [Fact]
    public async Task A_timed_buff_keeps_the_time_it_had_left_over_a_day_offline()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(Steak, 1, 72));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var unfed = session.Resource.HpMax;
        var before = session.ActiveBuffs.ToHashSet();
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(72).ToArray(), Now);
        var food = session.ActiveBuffs.Except(before).Single();
        var fed = session.Resource.HpMax;
        Assert.True(fed > unfed);
        await session.TickAsync(Now + 2_000);   // the periodic flush saves the buff early

        // Logout 100 s after eating, back a day later: 200 s are left, and the entry announces the full 300 s duration.
        await session.HandleAsync(Logout(), Now + 100_000);
        var later = Now + 100_000 + Day;
        var (again, entry) = await RelogAsync(store, later);
        Assert.Contains(food, again.ActiveBuffs);
        Assert.Equal(fed, again.Resource.HpMax);
        var wire = BitConverter.GetBytes(food).Concat(BitConverter.GetBytes(300_000L)).Concat(BitConverter.GetBytes(later + 200_000)).ToArray();
        Assert.Contains(entry, b => b[0] == 0x2a && b[1] == 0x38 && b.AsSpan().IndexOf(wire) > 0);

        await again.TickAsync(later + 199_000);
        Assert.Contains(food, again.ActiveBuffs);
        await again.TickAsync(later + 200_100);
        Assert.DoesNotContain(food, again.ActiveBuffs);
        Assert.Equal(unfed, again.Resource.HpMax);
    }

    [Fact]
    public async Task A_buff_that_ran_out_before_the_logout_does_not_come_back()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(Steak, 1, 72));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var before = session.ActiveBuffs.ToHashSet();
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(72).ToArray(), Now);
        var food = session.ActiveBuffs.Except(before).Single();
        await session.TickAsync(Now + 300_500);
        Assert.DoesNotContain(food, session.ActiveBuffs);
        await session.HandleAsync(Logout(), Now + 310_000);
        var (again, _) = await RelogAsync(store, Now + 320_000);
        Assert.DoesNotContain(food, again.ActiveBuffs);
    }

    [Fact]
    public async Task The_flight_gauge_comes_back_as_it_was_at_the_logout()
    {
        var store = new FakeCharacterStore();
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = store.WorldId }, new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Ascension = 3 };
        await session.HandleAsync(Select(store), Now);
        await session.HandleAsync(Enter(), Now);
        var full = session.Resource.FpMax;
        Assert.True(full > 0);
        Assert.Equal(full, session.Resource.Fp);

        await session.HandleAsync(new PacketWriter().U16(0x370a).U8(0).ToArray(), Now);   // fly
        await session.TickAsync(Now + 2_000);
        var left = session.Resource.Fp;
        Assert.InRange(left, 1, full - 1);

        await session.HandleAsync(Logout(), Now + 2_000);
        var (again, _) = await RelogAsync(store, Now + Day);
        Assert.Equal(full, again.Resource.FpMax);
        Assert.Equal(left, again.Resource.Fp);
    }
}
