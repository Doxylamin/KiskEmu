using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// 0x561e DropItem_NT (OBSERVED, AVION2 captures): u8 count, 63-byte records whose first field is the source (u32, the
/// corpse the item dropped from), then one reason byte. Drops are told apart by uid.
/// </summary>
public sealed class DropKeyTests
{
    const long Now = 1_793_368_800_000;
    const int RecordBytes = 4 + 24 + 8 + 4 + 4 + 1 + 8 + 8 + 1 + 1;

    static async Task<List<(int Target, int Source, long Uid)>> KillForDropsAsync(SharedWorld world, int seed, long now)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore();
        var session = new GameSession(data,
            new GameSessionOptions { World = world, CharacterLevel = 10, WorldId = store.WorldId, LevelCap = data.MaxLevel },
            new Random(seed)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var spot = new Vector3d(-127874, -111187, 25393);
        session.Teleport(1010, spot, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        var basic = MapEventScenarioHelpers.BasicAttackSkill(session);
        var drops = new List<(int, int, long)>();
        var at = now + 1000;
        foreach (var target in session.Targets!.Values.Where(t => t.Hostile && LootTables.Embedded.HasLoot(t.NpcId)).Take(3).ToList())
        {
            target.Hp = 1;
            foreach (var p in await MapEventScenarioHelpers.CombatCastAsync(session, basic, target.Key, target.Position, at))
                if (GameSessionTestHelpers.Op(p) == LootPackets.DropNt)
                {
                    var reader = new PacketReader(p) { Offset = 2 };
                    var count = reader.U8();
                    Assert.Equal(2 + 1 + count * RecordBytes + 1, p.Length);
                    for (var i = 0; i < count; i++)
                    {
                        var source = (int)reader.U32();
                        reader.Offset += 24;
                        drops.Add((target.Key, source, reader.I64()));
                        reader.Offset += RecordBytes - 4 - 24 - 8;
                    }
                }
            at += 1000;
        }
        return drops;
    }

    [Fact]
    public async Task Drops_name_their_corpse_and_keep_unique_uids_across_players()
    {
        var world = new SharedWorld(GameSessionTestHelpers.LiveGameData);
        var first = await KillForDropsAsync(world, 1, Now);
        var second = await KillForDropsAsync(world, 2, Now);
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        var all = first.Concat(second).ToList();
        Assert.All(all, d => Assert.Equal(d.Target, d.Source));
        Assert.Equal(all.Count, all.Select(d => d.Uid).Distinct().Count());
    }

    [Fact]
    public void Removal_batch_has_twenty_byte_records_and_one_trailing_byte()
    {
        var p = LootPackets.DropsRemoved([(7, 11, 99, true), (7, 12, 99, true)]);
        Assert.Equal(2 + 1 + 2 * 20 + 1, p.Length);
        var reader = new PacketReader(p) { Offset = 2 };
        Assert.Equal(2, reader.U8());
        Assert.Equal(7u, reader.U32());
        Assert.Equal(11, reader.I64());
        Assert.Equal(99, reader.I64());
        Assert.Equal(7u, reader.U32());
        Assert.Equal(12, reader.I64());
        Assert.Equal(99, reader.I64());
        Assert.Equal(1, reader.U8());
        Assert.True(reader.End);
    }
}
