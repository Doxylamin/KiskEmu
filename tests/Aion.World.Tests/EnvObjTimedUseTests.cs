using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Timed object use (community fork's recordings 2026-11-04 to 06, ported from addiboii's fork): the 0x3637 reply at the
/// start, 0x363a with 0x8d6d or 0x363b at the end, and 0x3639 when the player stops or is hit.
/// </summary>
public sealed class EnvObjTimedUseTests
{
    const long Now = 1_760_000_400_000;

    static async Task<(GameSession Session, int Key, WorldObject Object)> AtTimedObjectAsync()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 15);
        var objects = WorldObjects.ObjectsOn(1110);
        var index = objects.ToList().FindIndex(o => o.InteractTime > 0 && o.Gather is null && o.Carry is null && o.Achievement == 0
            && !o.Destructible && o.Portal is null && !o.Cube && !o.FlightRing && !o.Monolith && o.RequiredCombatPower == 0);
        Assert.True(index >= 0);
        var o = objects[index];
        session.Teleport(1110, o.At, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (session, WorldObjects.FirstKey + index, o);
    }

    static byte[] Use(int key, WorldObject o) => new PacketWriter().U16(0x3636).Varint(key).I64(0).I32(o.Env).ToArray();

    static ushort Op(byte[] p) => BitConverter.ToUInt16(p, 0);

    [Fact]
    public async Task Timed_use_replies_at_the_start_and_ends_with_one_state()
    {
        var (session, key, o) = await AtTimedObjectAsync();
        var start = await session.HandleAsync(Use(key, o), Now + 1000);
        Assert.Equal(2, start.Count);
        Assert.Equal(GamePackets.InteractEnvObjStarted(key, session.PlayerKey), start[0]);
        Assert.Equal(GamePackets.InteractEnvObjReply(key), start[1]);

        var done = await session.TickAsync(Now + 1000 + o.InteractTime);
        Assert.Contains(done, p => p.SequenceEqual(GamePackets.InteractEnvObjCompleted(key, session.PlayerKey)));
        Assert.DoesNotContain(done, p => Op(p) == 0x3637);
        Assert.Equal(1, done.Count(p => p.SequenceEqual(GamePackets.InteractEnvObjFinishState(key)) || p.SequenceEqual(GamePackets.InteractEnvObjAvailableState(key))));
    }

    [Fact]
    public async Task Stop_without_a_use_answers_0x3813()
    {
        var (session, key, _) = await AtTimedObjectAsync();
        var stopped = await session.HandleAsync(new PacketWriter().U16(0x363d).Varint(key).ToArray(), Now + 1000);
        Assert.Equal(Convert.FromHexString("3e36133800"), Assert.Single(stopped));
    }

    [Fact]
    public async Task Stop_during_a_use_interrupts_it()
    {
        var (session, key, o) = await AtTimedObjectAsync();
        var start = await session.HandleAsync(Use(key, o), Now + 1000);
        Assert.Contains(start, p => Op(p) == 0x3638);
        var stopped = await session.HandleAsync(new PacketWriter().U16(0x363d).Varint(key).ToArray(), Now + 1100);
        Assert.Equal(2, stopped.Count);
        Assert.Equal(GamePackets.InteractEnvObjStopReply(key), stopped[0]);
        Assert.Equal(GamePackets.InteractEnvObjInterrupted(key, GamePackets.InterruptStopped, session.PlayerKey), stopped[1]);
        Assert.DoesNotContain(await session.TickAsync(Now + 1000 + o.InteractTime), p => Op(p) == 0x363a);
    }

    [Fact]
    public async Task A_hit_interrupts_the_use()
    {
        var (session, key, o) = await AtTimedObjectAsync();
        var start = await session.HandleAsync(Use(key, o), Now + 1000);
        Assert.Contains(start, p => Op(p) == 0x3638);
        var hit = session.NpcHitsPlayer(1, Now + 1100, killerKey: 2001);
        Assert.Equal(GamePackets.InteractEnvObjAvailableState(key), hit[^2]);
        Assert.Equal(GamePackets.InteractEnvObjInterrupted(key, GamePackets.InterruptDamaged, session.PlayerKey), hit[^1]);
        Assert.DoesNotContain(await session.TickAsync(Now + 1000 + o.InteractTime), p => Op(p) == 0x363a);
    }

    [Fact]
    public void Interrupted_layout_matches_the_recording()
    {
        // 185000 #5957 `3936 bcef01 0519 d223 00000000` (key varint, reason u16, interactor varint, count i32).
        var key = new PacketReader(Convert.FromHexString("bcef01")).Varint();
        var interactor = new PacketReader(Convert.FromHexString("d223")).Varint();
        Assert.Equal(Convert.FromHexString("3936bcef010519d22300000000"), GamePackets.InteractEnvObjInterrupted((int)key, 0x1905, (int)interactor));
    }
}
