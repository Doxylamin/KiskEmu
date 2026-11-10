using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Field treasure chests: loot from the client reward tables, granted to the bag, then the chest is used up.
/// From A2Core-ChanAge (Artezianec); the S1 chest (env 1100130) has no RewardItem table in our export, so it is not tested.
/// </summary>
public sealed class FieldTreasureChestTests
{
    const long Now = 1_760_500_000_000;
    const int FieldEventMap = 510036;
    const int S2ChestEnv = 1100312;
    const int FieldEventRewardItem = 534960002;

    static async Task<(GameSession Session, WorldObject Obj, int Key)> NearChestAsync(FakeCharacterStore store, int envId, long now)
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 1, level: 41);
        var objects = WorldObjects.ObjectsOn(FieldEventMap).ToList();
        var index = objects.FindIndex(o => o.Env == envId);
        Assert.True(index >= 0, $"env {envId} on map {FieldEventMap}");
        var obj = objects[index];
        session.Teleport(FieldEventMap, obj.At, now + 5);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5);
        await MapEventScenarioHelpers.SyncPositionAsync(session, obj.At, now + 10);
        _ = session.QuestWorldGatingForTests(now + 15);
        return (session, obj, WorldObjects.FirstKey + index);
    }

    static async Task<List<byte[]>> OpenChestAsync(GameSession session, int key, WorldObject obj, long now)
    {
        var replies = await session.HandleAsync(new PacketWriter().U16(0x3636).Varint((uint)key).ToArray(), now + 20);
        if (obj.InteractTime > 0)
            replies.AddRange(await session.TickAsync(now + obj.InteractTime + 200));
        return replies;
    }

    [Fact]
    public void Treasure_box_tables_resolve_by_env_id_and_use_the_chest_up()
    {
        var loot = LootTables.Embedded;
        var tables = loot.ResolveEnvLootTableNames(1100311, "FE_TH_S2_Box_Group_1_1", null);
        Assert.Contains("reward_manual_FieldEvent_L_TreasureBox_1100311", tables);
        Assert.True(LootTables.TreasureChestConsumes(tables));
        Assert.False(LootTables.TreasureChestConsumes(["reward_manual_E_L1_HQ_GodFragment_01"]));
    }

    [Fact]
    public async Task S2_chest_grants_loot_once_and_is_used_up()
    {
        var store = new FakeCharacterStore();
        var before = store.Items.Sum(i => i.Quantity);
        var (session, obj, key) = await NearChestAsync(store, S2ChestEnv, Now);
        var replies = await OpenChestAsync(session, key, obj, Now);
        Assert.Contains(store.Items, i => i.TemplateId == FieldEventRewardItem && i.Quantity > 0);
        Assert.True(store.Items.Sum(i => i.Quantity) > before);
        Assert.Contains(replies, p => p.SequenceEqual(GamePackets.EnvObjDespawn(key, 4, 3)));
        var granted = store.Items.Sum(i => i.Quantity);
        await OpenChestAsync(session, key, obj, Now + 5000);
        Assert.Equal(granted, store.Items.Sum(i => i.Quantity));
    }
}
