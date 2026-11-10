using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Wing stats and the stored equipped wing (from A2Core-ChanAge, Artezianec).</summary>
public sealed class WingStatsTests
{
    const long Now = 1_760_090_200_000;
    const int Brawler = 30100300;   // equip HPMax 500 / Defense 400; owned FPMax 20000, PvP lines

    static byte[] Equip(int wing) => new PacketWriter().U16(0x5674).U8(0).I32(wing).ToArray();

    /// <summary>A level-55 ascended Elyos (flight and the default wing).</summary>
    static async Task<GameSession> EnterAscendedAsync(FakeCharacterStore store, long now)
    {
        var session = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), now);
        store.States[0] = store.States[0] with { Ascension = 3 };
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    [Fact]
    public void Wing_table_has_every_wing_with_english_names_and_the_default_gauge()
    {
        Assert.Equal(84, WingTables.Embedded.All.Count);
        Assert.All(WingTables.Embedded.All, w => Assert.True(w.Name.All(c => c < 128)));
        Assert.Equal(250_000, WingTables.Embedded.OwnedStat(GamePackets.DefaultWing(1), 0, "FPMax"));
        Assert.Contains(30401100, GamePackets.LightWings);
        Assert.Contains(40401100, GamePackets.DarkWings);
    }

    [Fact]
    public async Task Equipped_wing_adds_equip_stats_and_unequip_removes_them()
    {
        var store = new FakeCharacterStore();
        store.Wings.Add(Brawler);
        var session = await EnterAscendedAsync(store, Now);
        var defaultWing = GamePackets.DefaultWing(1);
        var hpBefore = session.Resource.HpMax;
        var defBefore = session.Defense;
        var equipHp = WingTables.Embedded.EquipStat(Brawler, "HPMax") - WingTables.Embedded.EquipStat(defaultWing, "HPMax");

        var replies = await session.HandleAsync(Equip(Brawler), Now + 10);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x5675);
        Assert.Equal(Brawler, session.EquippedWingForTests);
        Assert.Equal(hpBefore + equipHp, session.Resource.HpMax);
        Assert.Equal(defBefore + 400 - WingTables.Embedded.EquipStat(defaultWing, "Defense"), session.Defense);

        await session.HandleAsync(Equip(defaultWing), Now + 20);
        Assert.Equal(hpBefore, session.Resource.HpMax);
        Assert.Equal(defBefore, session.Defense);
    }

    [Fact]
    public async Task Owned_wing_adds_owned_stats_at_its_enchant_level()
    {
        var plain = await EnterAscendedAsync(new FakeCharacterStore(), Now);
        var withWing = new FakeCharacterStore();
        withWing.Wings.Add(Brawler);
        withWing.WingEnchants[Brawler] = 3;
        var owner = await EnterAscendedAsync(withWing, Now);

        var expected = WingTables.Embedded.OwnedStat(Brawler, 3, "PvPAmplifyDamage");
        Assert.True(expected > 0);
        Assert.Equal(plain.WingStatForTests("PvPAmplifyDamage") + expected, owner.WingStatForTests("PvPAmplifyDamage"));
        Assert.Equal(GameSession.FpMax + WingTables.Embedded.OwnedStat(Brawler, 3, "FPMax"), owner.Resource.FpMax);
        Assert.Equal(GameSession.FpMax, plain.Resource.FpMax);
    }

    [Fact]
    public async Task Equipped_wing_survives_relog()
    {
        var store = new FakeCharacterStore();
        store.Wings.Add(Brawler);
        var first = await EnterAscendedAsync(store, Now);
        await first.HandleAsync(Equip(Brawler), Now + 10);
        Assert.Equal(Brawler, store.EquippedWing);

        var relog = new GameSession(GameData.Embedded,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await relog.HandleAsync(GameSessionTestHelpers.Login(), Now + 100);
        var wireId = store.States[0].WireId;
        await relog.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now + 100);
        await relog.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        Assert.Equal(Brawler, relog.EquippedWingForTests);
    }
}
