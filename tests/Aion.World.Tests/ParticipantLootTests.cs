using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// RewardData *_participant_all tables: every player on a field boss's hate table rolls them for its own drops
/// (the bound chest at 100%); the killer keeps the boss's other tables.
/// </summary>
public sealed class ParticipantLootTests
{
    const long Now = 1_793_368_800_000;
    const int Boss = 2101074;   // World_L_A_Single_IslandOfEternity_Boss_1 (map 1010)
    static readonly Vector3d Spot = new(-219620.906, 243711.359, 10028.326);

    static async Task<(GameSession Session, FakeCharacterStore Store)> EnterAsync(SharedWorld world, int seed)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore();
        var session = new GameSession(data,
            new GameSessionOptions { World = world, CharacterLevel = 50, WorldId = store.WorldId, LevelCap = data.MaxLevel },
            new Random(seed)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.Teleport(1010, Spot, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return (session, store);
    }

    static List<int> DroppedItems(IEnumerable<byte[]> packets)
    {
        var items = new List<int>();
        foreach (var p in packets.Where(p => GameSessionTestHelpers.Op(p) == LootPackets.DropNt))
        {
            var reader = new PacketReader(p) { Offset = 2 };
            var count = reader.U8();
            for (var i = 0; i < count; i++)
            {
                reader.Offset += 4 + 24 + 8;
                items.Add(reader.I32());
                reader.Offset += 4 + 1 + 8 + 8 + 1 + 1;
            }
        }
        return items;
    }

    [Fact]
    public void Participant_tables_are_split_from_the_kill_roll()
    {
        var loot = LootTables.Embedded;
        Assert.True(loot.HasParticipantLoot(Boss));
        var rng = new Random(3);
        var participant = loot.RollParticipant(Boss, 1, rng);
        Assert.NotEmpty(participant);   // the 100% chest
        var chest = participant[0].ItemId;
        for (var i = 0; i < 20; i++) Assert.DoesNotContain(loot.Roll(Boss, 1, rng), d => d.ItemId == chest);
        // No fallback: a monster without a participant table gives participants nothing.
        Assert.Empty(loot.RollParticipant(2600089, 1, rng));
    }

    [Fact]
    public async Task Every_player_on_the_hate_table_gets_its_own_participant_drops()
    {
        var world = new SharedWorld(GameSessionTestHelpers.LiveGameData);
        var (killer, _) = await EnterAsync(world, 1);
        var (helper, _) = await EnterAsync(world, 2);
        var (bystander, _) = await EnterAsync(world, 3);
        var boss = killer.Targets!.Values.First(t => t.NpcId == Boss);
        var chest = LootTables.Embedded.RollParticipant(Boss, 1, new Random(0)).Select(p => p.ItemId).First();

        var at = Now + 1000;
        boss.Hp = 1_000_000;
        await MapEventScenarioHelpers.CombatCastAsync(helper, MapEventScenarioHelpers.BasicAttackSkill(helper), boss.Key, boss.Position, at);
        Assert.True(boss.Hp < 1_000_000);
        boss.Hp = 1;
        var kill = await MapEventScenarioHelpers.CombatCastAsync(killer, MapEventScenarioHelpers.BasicAttackSkill(killer), boss.Key, boss.Position, at + 1000);
        Assert.True(boss.Dead);

        Assert.Contains(chest, DroppedItems(kill));
        // Each player may pick up only its own drops: the helper holds its own chest, the bystander (no hate) none.
        Assert.Contains(killer.GroundDropItems, d => d.ItemId == chest);
        Assert.Contains(helper.GroundDropItems, d => d.ItemId == chest);
        Assert.DoesNotContain(bystander.GroundDropItems, d => d.ItemId == chest);
        var outbox = new List<byte[]>();
        while (helper.WorldPlayer!.Outbox.TryDequeue(out var packet)) outbox.Add(packet);
        Assert.Contains(chest, DroppedItems(outbox));
    }
}
