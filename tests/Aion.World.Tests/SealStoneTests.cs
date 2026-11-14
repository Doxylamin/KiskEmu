using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Power Shards (SealStone): registering a stack (0x8d4e, 0x8d4d, the stat sheet), the toggle refusing its own state (0x1960),
/// 0x8d4d at world entry, shards spent per damaging cast (0x561b reason 22) and boosting its damage; none, a plain cast.
/// Wire forms OBSERVED in the community recording 2026-11-06 234912 (ported from the addiboii fork 2b93c15, #72).
/// </summary>
public sealed class SealStoneTests
{
    const long Now = 1_760_200_000_000;
    const int Shard = 533460001;
    const long ShardWire = 9100;
    const int NotAStone = 530126003;   // Life Potion
    const long NotAStoneWire = 9200;

    static byte[] Register(long uid) => new PacketWriter().U16(GameSession.SealStoneRegisterRq).I64(uid).ToArray();
    static byte[] Toggle(bool on) => new PacketWriter().U16(on ? GameSession.SealStoneActivateRq : GameSession.SealStoneDeactivateRq).ToArray();
    static byte[] Hex(string hex) => Convert.FromHexString(hex);

    static async Task<(GameSession Session, FakeCharacterStore Store)> BagAsync(long shards)
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 10, rng: new Random(7));
        if (shards > 0) store.Items.Add(FakeCharacterStore.BagItem(Shard, shards, ShardWire));
        store.Items.Add(FakeCharacterStore.BagItem(NotAStone, 5, NotAStoneWire));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        return (session, store);
    }

    static async Task<(GameSession Session, FakeCharacterStore Store, Target Target, Vector3d At)> FieldAsync(long shards, bool slot)
    {
        var (session, store) = await BagAsync(shards);
        if (slot)
        {
            await session.HandleAsync(Register(ShardWire), Now);
            await session.HandleAsync(Toggle(true), Now);
        }
        var spawn = GameSessionTestHelpers.LiveGameData.Spawns(1010).First(s => s.Relationship == 6 && s.Function == "None");
        var at = new Vector3d(spawn.Position[0], spawn.Position[1], spawn.Position[2]);
        session.Teleport(1010, at, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var target = session.Targets!.Values.First(t => t.NpcId == spawn.NpcId);
        return (session, store, target, at);
    }

    static async Task<(int Damage, List<byte[]> Replies)> HitAsync(GameSession session, Target target, Vector3d at)
    {
        var before = target.Hp;
        var skill = MapEventScenarioHelpers.BasicAttackSkill(session);
        var replies = await MapEventScenarioHelpers.CombatCastAsync(session, skill, target.Key, at, Now + 1000);
        return (before - target.Hp, replies);
    }

    [Fact]
    public void Stone_table_has_the_recorded_values()
    {
        // OBSERVED 534 = 500 for 533460001 and 1000 for 533460004 (2026-11-06 234912); SealStone.dat of build 26262663.
        var tables = SealStoneTables.Embedded;
        Assert.Equal(500, tables.Stat(Shard, "SealStoneAmplifyDamage"));
        Assert.Equal(10, tables.Stat(Shard, "SealStoneAddDamage"));
        Assert.Equal(1000, tables.Stat(533460004, "SealStoneAmplifyDamage"));
        Assert.Equal(1000, tables.Stat(533460006, "SealStonePvPIgnoreDefense"));
        Assert.All(GameSession.PowerShardItems, id => Assert.True(tables.IsStone(id)));
        Assert.False(tables.IsStone(NotAStone));
    }

    [Fact]
    public async Task Registering_sends_the_change_the_reply_then_the_stats()
    {
        var (session, store) = await BagAsync(50);
        var refused = Assert.Single(await session.HandleAsync(Register(NotAStoneWire), Now));
        Assert.Equal(GameSession.SealStoneRegisterRs, GameSessionTestHelpers.Op(refused));
        Assert.Equal(1, BitConverter.ToUInt16(refused, 2));

        var replies = await session.HandleAsync(Register(ShardWire), Now);
        Assert.True(replies.Count >= 3);
        Assert.Equal(PlayerPackets.SealstoneChange(session.PlayerKey, Shard), replies[0]);
        Assert.Equal(new PacketWriter().U16(GameSession.SealStoneRegisterRs).U16(0).I64(ShardWire).ToArray(), replies[1]);
        Assert.Equal(0x364a, GameSessionTestHelpers.Op(replies[2]));
        Assert.True(replies[2].AsSpan().IndexOf(Hex("45000a000000")) > 0, "69 SealStoneAddDamage = 10");
        Assert.True(replies[2].AsSpan().IndexOf(Hex("1602f4010000")) > 0, "534 SealStoneAmplifyDamage = 500");
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == GameSession.SealStoneStatusNt);
        Assert.False(session.SealStoneOn);
        Assert.Equal("{\"On\":false,\"Item\":9100}", store.Settings["seal_stone"].GetRawText());
    }

    [Fact]
    public async Task A_toggle_to_its_own_state_is_refused()
    {
        var (session, _) = await BagAsync(50);
        Assert.Equal(Hex("4a8d6019"), Assert.Single(await session.HandleAsync(Toggle(false), Now)));
        var on = await session.HandleAsync(Toggle(true), Now);
        Assert.Equal(Hex("488d0000"), on[0]);
        Assert.True(session.SealStoneOn);
        Assert.Equal(Hex("488d6019"), Assert.Single(await session.HandleAsync(Toggle(true), Now)));
    }

    [Fact]
    public async Task World_entry_sends_the_registered_stack()
    {
        var (session, _) = await BagAsync(50);
        var before = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        Assert.DoesNotContain(before, p => GameSessionTestHelpers.Op(p) == GameSession.SealStoneRegisterRs);
        await session.HandleAsync(Register(ShardWire), Now);
        var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        Assert.Contains(entry, p => p.SequenceEqual(new PacketWriter().U16(GameSession.SealStoneRegisterRs).U16(0).I64(ShardWire).ToArray()));
    }

    [Fact]
    public async Task Slotted_shards_are_spent_and_boost_the_hit()
    {
        var (plain, _, plainTarget, plainAt) = await FieldAsync(0, slot: false);
        var (boosted, store, target, at) = await FieldAsync(1000, slot: true);
        Assert.True(boosted.SealStoneOn);
        Assert.Equal("{\"On\":true,\"Item\":9100}", store.Settings["seal_stone"].GetRawText());

        var (plainDamage, _) = await HitAsync(plain, plainTarget, plainAt);
        var (damage, replies) = await HitAsync(boosted, target, at);

        var skill = MapEventScenarioHelpers.BasicAttackSkill(boosted);
        var spent = boosted.SealStoneConsumption(skill);
        Assert.True(spent > 0);
        Assert.Equal(1000 - spent, store.Items.Single(i => i.TemplateId == Shard).Quantity);
        var change = Assert.Single(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
        Assert.Equal(22, change[^2]);   // decrease reason kItemModifiedInCombat (OBSERVED)
        Assert.True(plainDamage > 0);
        Assert.True(damage > plainDamage, $"boosted {damage} vs plain {plainDamage}");
    }

    [Fact]
    public async Task Without_shards_the_cast_is_plain()
    {
        var (plain, _, plainTarget, plainAt) = await FieldAsync(0, slot: false);
        var (session, store, target, at) = await FieldAsync(1000, slot: true);
        store.Items.RemoveAll(i => i.TemplateId == Shard);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var (plainDamage, _) = await HitAsync(plain, plainTarget, plainAt);
        var (damage, replies) = await HitAsync(session, target, at);

        Assert.Equal(plainDamage, damage);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);
    }

    [Fact]
    public async Task Toggle_off_keeps_the_shards()
    {
        var (session, store, target, at) = await FieldAsync(1000, slot: true);
        await session.HandleAsync(Toggle(false), Now);
        await HitAsync(session, target, at);
        Assert.Equal(1000, store.Items.Single(i => i.TemplateId == Shard).Quantity);
    }
}
