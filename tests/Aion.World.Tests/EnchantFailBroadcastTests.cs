using System.Text.Json;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>#86: a failed enchant of an equipped item that keeps the level reaches those nearby as 0x5626 result 834.</summary>
public sealed class EnchantFailBroadcastTests
{
    const long Now = 1_760_000_300_000;
    // Enchant_Unique_36 step 10: 6500 / 10000, FailPenalty 0, 127,000 Kina and 3,290 Enhance Stones.
    const int Weapon = 110130002, EnhanceStone = 930100023;

    static async Task<GameSession> EnterAsync(SharedWorld world, FakeCharacterStore store, string name, int seed)
    {
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = world, CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(seed)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(1).Bytes([1]).U8(0).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public async Task Failed_enchant_that_keeps_the_level_is_broadcast_with_834()
    {
        var world = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        storeA.Items.Add(FakeCharacterStore.EquippedItem(Weapon, 0x1e7, 1) with
        {
            Upgrades = JsonSerializer.SerializeToElement(new { enchant = 10, enchant_bonus = 0 }),
        });
        storeA.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 500_000, 0x100));
        storeA.Items.Add(FakeCharacterStore.BagItem(EnhanceStone, 10_000, 0x174));
        var a = await EnterAsync(world, storeA, "EnchA", 11);
        var b = await EnterAsync(world, new FakeCharacterStore(Guid.NewGuid()), "EnchB", 12);
        var spot = new Vector3d(-127874, -111187, 25393);
        a.Teleport(1010, spot, Now);
        b.Teleport(1010, spot with { X = spot.X + 50 }, Now);
        await a.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await b.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await a.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);
        while (b.WorldPlayer!.Outbox.TryDequeue(out _)) { }

        a.EnchantRoll = () => 9999;
        var replies = await a.HandleAsync(new PacketWriter().U16(GameSession.ItemEnchantRq).I64(0x1e7).Varint(0).ToArray(), Now + 10);

        var rs = replies.Single(r => GameSessionTestHelpers.Op(r) == GameSession.ItemEnchantRs);
        Assert.NotEqual(GameSession.EnchantSuccess, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(10, storeA.Items.Single(i => i.WireId == 0x1e7).Upgrades.GetProperty("enchant").GetInt32());
        var outbox = new List<byte[]>();
        while (b.WorldPlayer!.Outbox.TryDequeue(out var packet)) outbox.Add(packet);
        // OBSERVED R1115 i=126437 `…42030a0a` (10 -> 10): {key varint, u16 834, before, after} with the level unchanged.
        var expected = new PacketWriter().U16(GameSession.ItemEnchantNt).Varint(a.PlayerKey).U16(GameSession.EnchantFail).U8(10).U8(10).ToArray();
        Assert.Contains(outbox, p => p.SequenceEqual(expected));
        // The own client keeps only its 0x5623 (CANDIDATE).
        Assert.DoesNotContain(replies, r => GameSessionTestHelpers.Op(r) == GameSession.ItemEnchantNt);
    }
}
