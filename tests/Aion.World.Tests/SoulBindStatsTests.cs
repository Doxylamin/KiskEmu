using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Soul-bind stats (ported from the community fork, Addiboii 13aa9ef and 101b443): the item's pool, the draw and raise per
/// bind, 0x5630 and the item detail tail, and the stats counting for the wearer.
/// </summary>
public sealed class SoulBindStatsTests
{
    const long Now = 1_760_000_200_000;
    const int Gloves = 210540022, Cloak = 210730052, SoulCodex = 535400001;
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    static ItemState Item(int template, long count, string location, long wire, object? upgrades = null, int? slot = null) =>
        new(Guid.NewGuid(), "b", template, count, location, slot, 1, Empty, upgrades is null ? Empty : JsonSerializer.SerializeToElement(upgrades), wire);

    static async Task<GameSession> EnterAsync(FakeCharacterStore store)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 55, WorldId = 1204 }, new Random(21)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);
        return session;
    }

    static byte[] Reply(IEnumerable<byte[]> replies, ushort opcode) => replies.Single(r => BitConverter.ToUInt16(r, 0) == opcode);

    [Fact]
    public void Complete_reply_matches_the_recorded_bytes()
    {
        // Community recording 2026-11-06 (#49976, from the fork): gloves 210540022 at bind count 3, {u16 EStat, i32}, no skills.
        Assert.Equal("3056000025F8050000160100030329006A000000C2001400000002000E00000000",
            Convert.ToHexString(GamePackets.SoulBindComplete(0x116000005f825, 3, 0, [(41, 106), (194, 20), (2, 14)])));
    }

    [Fact]
    public void Detail_tail_carries_the_soul_stats_and_their_types_at_zero()
    {
        // Community recording 2026-11-06 (#49975, from the fork): the gloves' 0x561b after the godstones.
        var detail = new EquipDetail(25, 0, SoulBindCount: 3, SoulStats: [(41, 106), (194, 20), (2, 14)]);
        Assert.Equal("00" + "0329006A000000C2001400000002000E000000" + "03290000000000C20000000000020000000000" + "0000000000",
            Convert.ToHexString(GamePackets.DetailTail(new PacketWriter(), detail, 0).ToArray()));
        // Without soul stats the tail is unchanged: rolled stats, no _add_sub_stats.
        Assert.Equal("00" + "01" + "8000" + "05000000" + "00" + "0000000000",
            Convert.ToHexString(GamePackets.DetailTail(new PacketWriter(), new EquipDetail(1, 0, RolledStats: [(128, 5)]), 0).ToArray()));
    }

    [Fact]
    public void Pool_comes_from_the_items_substats()
    {
        var gloves = ItemEnhanceTables.Embedded.SoulBindStats(Gloves)!;
        Assert.Equal(3, gloves.StatCount);
        var range = gloves.Pool.ToDictionary(p => p.Stat, p => (p.Min, p.Max));
        // The recorded 106, 20 and 14 lie inside their rows.
        Assert.Equal((89, 109), range[41]);
        Assert.Equal((15, 24), range[194]);
        Assert.Equal((7, 15), range[2]);
        Assert.Null(ItemEnhanceTables.Embedded.SoulBindStats(110160001));
    }

    [Fact]
    public void Bind_draws_half_first_then_all_and_raises_within_the_range()
    {
        var gloves = ItemEnhanceTables.Embedded.SoulBindStats(Gloves)!;
        var rng = new Random(7);
        int Roll(int max) => rng.Next(max);
        var bind1 = ItemEnhanceTables.Bind(gloves, [], 1, Roll);
        var bind2 = ItemEnhanceTables.Bind(gloves, bind1, 2, Roll);
        var bind3 = ItemEnhanceTables.Bind(gloves, bind2, 3, Roll);
        bool Inside(List<(int Wire, int Value)> s) => s.All(x => gloves.Pool.Any(p => p.Stat == x.Wire && x.Value >= p.Min && x.Value <= p.Max));
        Assert.Equal(2, bind1.Count);
        Assert.Equal(3, bind2.Count);
        Assert.Equal(3, bind2.Select(s => s.Wire).Distinct().Count());
        Assert.Equal(bind1.Select(s => s.Wire), bind2.Take(2).Select(s => s.Wire));
        Assert.Equal(bind2.Select(s => s.Wire), bind3.Select(s => s.Wire));
        Assert.All(bind3.Zip(bind2), p => Assert.True(p.First.Value >= p.Second.Value));
        Assert.True(Inside(bind1) && Inside(bind2) && Inside(bind3));
        // At the maximum a stat stays there.
        var top = gloves.Pool.First(p => p.Stat == 41);
        Assert.Equal(top.Max, ItemEnhanceTables.Bind(gloves, [(41, top.Max)], 5, max => max - 1).Single(s => s.Wire == 41).Value);
    }

    [Fact]
    public async Task Free_bind_stores_the_stats_and_sends_them()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(Item(Gloves, 1, "bag", 0x95));
        var session = await EnterAsync(store);

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.SoulBindCompleteRq).I64(0x95).ToArray(), Now);

        var rs = Reply(replies, 0x5630);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(1, rs[12]);
        Assert.Equal(2, rs[13]);                    // half of 3, rounded up
        Assert.Equal(14 + 2 * 6 + 1, rs.Length);
        var row = store.Items.Single(i => i.WireId == 0x95);
        Assert.Equal(1, row.Upgrades.GetProperty("soul_bind").GetInt32());
        var stored = row.Upgrades.GetProperty("soul_stats").EnumerateArray().Select(s => (s[0].GetInt32(), s[1].GetInt32())).ToList();
        Assert.Equal(2, stored.Count);
        // The item update carries them in its detail tail.
        var tail = Convert.ToHexString(GamePackets.DetailTail(new PacketWriter(), new EquipDetail(1, 0, SoulStats: stored), 0).ToArray());
        Assert.Contains(tail, Convert.ToHexString(Reply(replies, 0x561b)));
    }

    [Fact]
    public async Task Paid_step_keeps_the_stats_and_fills_up_to_the_count()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(Item(Cloak, 1, "bag", 0x8e, new { soul_bind = 1 }));
        store.Items.Add(Item(GameSession.KinaItemId, 100_000, "bag", 0x100));
        store.Items.Add(Item(SoulCodex, 20, "storage", 0x90));
        var session = await EnterAsync(store);

        await session.HandleAsync(new PacketWriter().U16(GameSession.SoulBindStartRq).I64(0x8e).I64(0x90).I32(SoulCodex).ToArray(), Now);
        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.SoulBindCompleteRq).I64(0x8e).ToArray(), Now);

        var rs = Reply(replies, 0x5630);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(2, rs[12]);
        Assert.Equal(4, rs[13]);                    // the cloak's SoulbindRandomStatCount
        var row = store.Items.Single(i => i.WireId == 0x8e);
        Assert.Equal(2, row.Upgrades.GetProperty("soul_bind").GetInt32());
        Assert.Equal(4, row.Upgrades.GetProperty("soul_stats").GetArrayLength());
    }

    [Fact]
    public async Task Worn_soul_stats_count_for_the_wearer()
    {
        var unbound = new FakeCharacterStore();
        unbound.Items.Add(Item(Gloves, 1, "equipment", 0x95, slot: 7));
        var plain = await EnterAsync(unbound);

        var bound = new FakeCharacterStore();
        bound.Items.Add(Item(Gloves, 1, "equipment", 0x95, new { soul_bind = 3, soul_stats = new[] { new[] { 193, 80 }, new[] { 307, 100 } } }, slot: 7));
        var soul = await EnterAsync(bound);

        Assert.Equal(plain.GearStat("HPMax") + 80, soul.GearStat("HPMax"));
        Assert.Equal(plain.GearStat("ArmorDefense") + 100, soul.GearStat("ArmorDefense"));
        Assert.Equal(plain.StatSheetForTests().HpMax + 80, soul.StatSheetForTests().HpMax);
    }
}
