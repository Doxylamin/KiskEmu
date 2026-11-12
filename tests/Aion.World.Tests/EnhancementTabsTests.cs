using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Enhancement window tabs: Growth (0x5622 cost list), Manastone (0x5629) and Soul Binding (0x562b / 0x562f).</summary>
public sealed class EnhancementTabsTests
{
    const long Now = 1_760_000_200_000;
    const int NobleBelt = 215250001, EnhanceStone = 930100023, BeltScroll = 531110006;
    const int Cloak = 210730052, IntermediateManastone = 531360003, SoulCodex = 535400001;
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    static ItemState Item(int template, long count, string location, long wire, object? upgrades = null) =>
        new(Guid.NewGuid(), "b", template, count, location, null, 1, Empty, upgrades is null ? Empty : JsonSerializer.SerializeToElement(upgrades), wire);

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

    /// <summary>The live 0x5622 of 2026-11-12: uid, 2 components {origin, replaced, [key, count]}, then 4 undeclared zero bytes.</summary>
    static readonly byte[] LiveGrowthRequest = Convert.FromHexString(string.Concat(
        "2256", "E701000000000000", "02",
        "37337037", "00000000", "01", "7401000000000000", "C003000000000000",
        "7618A81F", "00000000", "01", "B401000000000000", "0100000000000000",
        "00000000"));

    static byte[] Reply(IEnumerable<byte[]> replies, ushort opcode) => replies.Single(r => BitConverter.ToUInt16(r, 0) == opcode);

    [Fact]
    public async Task Growth_spends_the_listed_warehouse_scroll()
    {
        // Live request 2026-11-12: belt 0x1e7, Enhance Stone x960 from stack 0x174, scroll x1 from stack 0x1b4 (warehouse).
        var store = new FakeCharacterStore();
        store.Items.Add(Item(NobleBelt, 1, "bag", 0x1e7, new { enchant = 5, enchant_bonus = 0 }));
        store.Items.Add(Item(GameSession.KinaItemId, 100_000, "bag", 0x100));
        store.Items.Add(Item(EnhanceStone, 1_000, "bag", 0x174));
        store.Items.Add(Item(BeltScroll, 1, "storage", 0x1b4));
        var session = await EnterAsync(store);
        session.EnchantRoll = () => 0;

        var request = LiveGrowthRequest;
        var replies = await session.HandleAsync(request, Now);

        var rs = Reply(replies, GameSession.ItemEnchantRs);
        Assert.Equal(GameSession.EnchantSuccess, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(6, store.Items.Single(i => i.WireId == 0x1e7).Upgrades.GetProperty("enchant").GetInt32());
        Assert.DoesNotContain(store.Items, i => i.WireId == 0x1b4);
        Assert.Equal(40, store.Items.Single(i => i.WireId == 0x174).Quantity);
        Assert.Equal(100_000 - 24_000, store.Items.Single(i => i.WireId == 0x100).Quantity);
    }

    [Fact]
    public void Growth_cost_list_reads_the_components_and_tolerates_the_trailing_bytes()
    {
        var listed = GameSession.ReadCostItems(LiveGrowthRequest);
        Assert.Equal([0x174L], listed[EnhanceStone]);
        Assert.Equal([0x1b4L], listed[BeltScroll]);
        Assert.Empty(GameSession.ReadCostItems(new PacketWriter().U16(0x5622).I64(1).ToArray()));
    }

    [Fact]
    public void Manastone_values_match_the_official_imprint()
    {
        // 0x562a 2026-11-12, Intermediate Manastone: crit 6 (128/Low), MP 40 (199/Mid), MP 23 (199/Low), crit resist 9 (133/Mid).
        var tables = ItemEnhanceTables.Embedded;
        Assert.Equal(6, tables.StoneValue(IntermediateManastone, 128, 1));
        Assert.Equal(40, tables.StoneValue(IntermediateManastone, 199, 2));
        Assert.Equal(23, tables.StoneValue(IntermediateManastone, 199, 1));
        Assert.Equal(9, tables.StoneValue(IntermediateManastone, 133, 2));
        var cloak = tables.Item(Cloak)!;
        Assert.Equal((4, 2000L, "Unique_Cost"), (cloak.Slots, cloak.ImprintKina, cloak.SoulBindCost));
        Assert.Equal(["Magicstone_Group_Equip"], cloak.StoneGroups);
        Assert.Equal(new ItemEnhanceTables.Cost(42_000, SoulCodex, 12), tables.SoulBindStep(Cloak, 1));
    }

    [Fact]
    public void Imprint_reply_matches_the_official_bytes()
    {
        MagicStoneSlot[] slots = [new(IntermediateManastone, 128, 1), new(IntermediateManastone, 199, 2), new(IntermediateManastone, 199, 1), new(IntermediateManastone, 133, 2)];
        var reply = GamePackets.ItemImprint(0, 0x0000980000_07f28e, IntermediateManastone, 0x0000030000_0a8680, slots);
        Assert.Equal("2a5600008ef207000098000003e9ab1f80860a000003000004"
            + "03e9ab1f800001" + "03e9ab1fc70002" + "03e9ab1fc70001" + "03e9ab1f850002", Convert.ToHexString(reply).ToLowerInvariant());
    }

    [Fact]
    public async Task Imprint_fills_every_slot_and_spends_the_stone_and_kina()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(Item(Cloak, 1, "bag", 0x8e));
        store.Items.Add(Item(GameSession.KinaItemId, 10_000, "bag", 0x100));
        store.Items.Add(Item(IntermediateManastone, 2, "bag", 0x80));
        var session = await EnterAsync(store);
        session.UpgradeRoll = _ => 0;

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemImprintRq).I64(0x8e).I32(IntermediateManastone).I64(0x80).ToArray(), Now);

        var rs = Reply(replies, GameSession.ItemImprintRs);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(4, rs[24]);
        var stones = store.Items.Single(i => i.WireId == 0x8e).Upgrades.GetProperty("magicstones");
        Assert.Equal(4, stones.GetArrayLength());
        // Roll 0 draws the group's first stat (WeaponFixingDamage 317) at its first grade (Common = 2).
        Assert.All(stones.EnumerateArray(), s =>
        {
            Assert.Equal(IntermediateManastone, s.GetProperty("id").GetInt32());
            Assert.Equal(317, s.GetProperty("stat").GetInt32());
            Assert.Equal(1, s.GetProperty("grade").GetInt32());
            Assert.Equal(2, s.GetProperty("value").GetInt32());
        });
        Assert.Equal(8_000, store.Items.Single(i => i.WireId == 0x100).Quantity);
        Assert.Equal(1, store.Items.Single(i => i.WireId == 0x80).Quantity);
        // The item update carries the four slots: {03e9ab1f 3d01 01} x4 in 0x561b.
        var update = Reply(replies, 0x561b);
        Assert.Contains("0403E9AB1F3D010103E9AB1F3D0101", Convert.ToHexString(update));
    }

    [Fact]
    public async Task Imprint_rejects_a_stone_of_another_group()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(Item(Cloak, 1, "bag", 0x8e));
        store.Items.Add(Item(GameSession.KinaItemId, 10_000, "bag", 0x100));
        store.Items.Add(Item(533560001, 1, "bag", 0x81));   // accessory mana stone (Magicstone_Group_Acc)
        var session = await EnterAsync(store);

        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.ItemImprintRq).I64(0x8e).I32(533560001).I64(0x81).ToArray(), Now);

        Assert.Equal(GameSession.ImprintInvalidStone, BitConverter.ToUInt16(Reply(replies, GameSession.ItemImprintRs), 2));
        Assert.Equal(1, store.Items.Single(i => i.WireId == 0x81).Quantity);
    }

    [Fact]
    public async Task Imported_official_stones_are_sent_in_the_item_detail()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(Item(Cloak, 1, "bag", 0x8e, new
        {
            official_magicstones = new[] { new { id = IntermediateManastone, stat = 128, grade = 1 }, new { id = IntermediateManastone, stat = 199, grade = 2 } },
        }));
        var session = await EnterAsync(store);

        var replies = await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        // Two imported stones, then the two empty slots of a four-slot item.
        Assert.Contains(replies, r => Convert.ToHexString(r).Contains("0403E9AB1F80000103E9AB1FC7000200000000000000000000000000"));
    }

    [Fact]
    public async Task Soul_bind_step_spends_codex_from_the_warehouse_and_raises_the_count()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(Item(Cloak, 1, "bag", 0x8e, new { soul_bind = 1 }));
        store.Items.Add(Item(GameSession.KinaItemId, 100_000, "bag", 0x100));
        store.Items.Add(Item(SoulCodex, 20, "storage", 0x90));
        var session = await EnterAsync(store);

        var start = await session.HandleAsync(new PacketWriter().U16(GameSession.SoulBindStartRq).I64(0x8e).I64(0x90).I32(SoulCodex).ToArray(), Now);
        Assert.Equal(0, BitConverter.ToUInt16(Reply(start, 0x562c), 2));
        var complete = await session.HandleAsync(new PacketWriter().U16(GameSession.SoulBindCompleteRq).I64(0x8e).ToArray(), Now);

        var rs = Reply(complete, 0x5630);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(2, rs[12]);
        Assert.Equal(2, store.Items.Single(i => i.WireId == 0x8e).Upgrades.GetProperty("soul_bind").GetInt32());
        Assert.Equal(8, store.Items.Single(i => i.WireId == 0x90).Quantity);
        Assert.Equal(100_000 - 42_000, store.Items.Single(i => i.WireId == 0x100).Quantity);
        Assert.Contains(complete, r => BitConverter.ToUInt16(r, 0) == 0x561b);
    }

    [Fact]
    public async Task Soul_bind_without_codex_or_start_does_not_raise_a_bound_item()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(Item(Cloak, 1, "bag", 0x8e, new { soul_bind = 1 }));
        store.Items.Add(Item(GameSession.KinaItemId, 100_000, "bag", 0x100));
        store.Items.Add(Item(SoulCodex, 5, "bag", 0x90));   // the step needs 12
        var session = await EnterAsync(store);

        var start = await session.HandleAsync(new PacketWriter().U16(GameSession.SoulBindStartRq).I64(0x8e).I64(0x90).I32(SoulCodex).ToArray(), Now);
        Assert.Equal(GameSession.SoulBindNotEnoughCost, BitConverter.ToUInt16(Reply(start, 0x562c), 2));
        var complete = await session.HandleAsync(new PacketWriter().U16(GameSession.SoulBindCompleteRq).I64(0x8e).ToArray(), Now);

        Assert.Equal(GameSession.SoulBindInvalidData, BitConverter.ToUInt16(Reply(complete, 0x5630), 2));
        Assert.Equal(1, store.Items.Single(i => i.WireId == 0x8e).Upgrades.GetProperty("soul_bind").GetInt32());
        Assert.Equal(5, store.Items.Single(i => i.WireId == 0x90).Quantity);
    }
}
