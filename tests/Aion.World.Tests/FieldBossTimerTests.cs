using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Field boss respawn timers (docs/FIELD_BOSSES.md): their own respawn range, stored, kept across a restart.</summary>
public sealed class FieldBossTimerTests
{
    const long Now = 1_793_000_000_000;
    const int Dartan = 101022;   // Silent Dartan (M_L1_FieldBoss_Dragon_01_1, map 1010): a grade-6 field boss
    const int Neikel = 101001;   // Neikel of the East (map 1010): a named monster
    const long Minute = 60_000;

    sealed class MemoryStore : IFieldBossStore
    {
        public Dictionary<int, FieldBossRecord> Rows { get; } = [];

        public Task<IReadOnlyList<FieldBossRecord>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FieldBossRecord>>([.. Rows.Values]);

        public Task SaveStateAsync(int code, DateTimeOffset? killedAt, DateTimeOffset? respawnAt, CancellationToken ct = default)
        {
            Rows[code] = (Rows.GetValueOrDefault(code) ?? new FieldBossRecord(code, null, null, null, null)) with { KilledAt = killedAt, RespawnAt = respawnAt };
            return Task.CompletedTask;
        }

        public Task SaveTimerAsync(int code, int? minMinutes, int? maxMinutes, CancellationToken ct = default)
        {
            Rows[code] = (Rows.GetValueOrDefault(code) ?? new FieldBossRecord(code, null, null, null, null)) with { MinMinutes = minMinutes, MaxMinutes = maxMinutes };
            return Task.CompletedTask;
        }
    }

    static SharedWorld World(MemoryStore store, long now)
    {
        var world = new SharedWorld(GameData.Embedded) { Clock = () => now };
        world.FieldBosses.Attach(store, null);
        return world;
    }

    /// <summary>Kills the boss as staff's "clear area" does (the same respawn path as a player's kill).</summary>
    static Target Kill(MapInstance map, int code, long now)
    {
        var boss = map.FieldBossTarget(code)!;
        Assert.False(boss.Dead);
        Assert.True(map.ClearArea(boss.Position.X, boss.Position.Y, 100, now, GameSession.RespawnMs) >= 1);
        Assert.True(boss.Dead);
        return boss;
    }

    [Fact]
    public void Field_bosses_are_the_client_WorldMapFieldNamed_rows_the_server_spawns()
    {
        var timers = new FieldBossTimers(GameData.Embedded);
        Assert.Equal(85, timers.Definitions.Count);
        Assert.Equal(("boss", 1010, 2101120), (timers.ByCode[Dartan].Kind, timers.ByCode[Dartan].MapId, timers.ByCode[Dartan].NpcId));
        Assert.Equal("named", timers.ByCode[Neikel].Kind);
        // Fixed waits fitted to the official captures (CANDIDATE), Altgard mirrored by index; the level-80 regions keep a range.
        Assert.Equal((360, 360), timers.Range(Dartan));
        Assert.Equal((180, 180), timers.Range(Neikel));
        Assert.Equal((180, 180), timers.Range(101008));
        Assert.Equal((180, 180), timers.Range(111008));
        Assert.Equal((120, 120), timers.Range(101017));
        var region = timers.Definitions.First(d => d.MapId == 1011);
        Assert.Equal((FieldBossTimers.BossMinMinutes, FieldBossTimers.BossMaxMinutes), timers.Range(region.Code));
        var map = new SharedWorld(GameData.Embedded) { Clock = () => Now }.Map(1010);
        Assert.Equal(24, timers.Definitions.Count(d => d.MapId == 1010 && map.FieldBossTarget(d.Code) is not null));
    }

    [Fact]
    public async Task A_killed_boss_stays_dead_across_a_restart_until_its_time()
    {
        var store = new MemoryStore();
        var world = World(store, Now);
        var boss = Kill(world.Map(1010), Dartan, Now);
        var respawn = world.Map(1010).RespawnAt[boss.Key];
        Assert.Equal(Now + 360 * Minute, respawn);
        await world.FieldBosses.FlushAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now), store.Rows[Dartan].KilledAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(respawn), store.Rows[Dartan].RespawnAt);

        // The restart, an hour later: the timers are read before the map is loaded.
        var later = Now + 60 * Minute;
        var restarted = World(store, later);
        await FieldBossWorld.LoadAsync(store, restarted);
        var map = restarted.Map(1010);
        var again = map.FieldBossTarget(Dartan)!;
        Assert.True(again.Dead);
        Assert.True(again.CorpseRemoved);
        Assert.Equal(respawn, map.RespawnAt[again.Key]);
        Assert.Equal("dead", restarted.FieldBosses.View(restarted.FieldBosses.ByCode[Dartan], map, GameData.Embedded, later).Status);
        // Other bosses are up.
        Assert.False(map.FieldBossTarget(Neikel)!.Dead);

        // The timers arrive after the map was loaded (a slow database): the boss leaves then.
        var late = World(store, later);
        var lateMap = late.Map(1010);
        Assert.False(lateMap.FieldBossTarget(Dartan)!.Dead);
        await FieldBossWorld.LoadAsync(store, late);
        Assert.True(lateMap.FieldBossTarget(Dartan)!.Dead);
        Assert.Equal(respawn, lateMap.RespawnAt[lateMap.FieldBossTarget(Dartan)!.Key]);

        // Restarted after its time: up.
        var afterwards = World(store, respawn + 1);
        await FieldBossWorld.LoadAsync(store, afterwards);
        Assert.False(afterwards.Map(1010).FieldBossTarget(Dartan)!.Dead);
    }

    [Fact]
    public async Task A_player_on_the_map_sees_the_corpse_go_and_the_boss_return_only_at_its_time()
    {
        var store = new FakeCharacterStore();
        var world = new SharedWorld(GameData.Embedded) { Clock = () => Now };
        world.FieldBosses.SetRange(Dartan, 120, 120);
        var map = world.Map(1010);
        var boss = map.FieldBossTarget(Dartan)!;
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { World = world, CharacterLevel = 55, WorldId = store.WorldId }, new Random(5)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.Teleport(1010, boss.Position with { X = boss.Position.X + 600 }, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await session.TickAsync(Now + 1000);

        Kill(map, Dartan, Now + 2000);
        Assert.Equal(Now + 2000 + 120 * Minute, map.RespawnAt[boss.Key]);
        var corpse = await session.TickAsync(Now + 2000 + GameSession.CorpseMs + 100);
        Assert.True(boss.CorpseRemoved);
        Assert.Contains(corpse, p => GameSessionTestHelpers.HasOpcode(p, 0x3642));
        // An ordinary monster would be back after RespawnMs; the boss is not.
        await session.TickAsync(Now + 2000 + GameSession.RespawnMs + 1000);
        Assert.True(boss.Dead);
        Assert.Equal("dead", world.FieldBosses.View(world.FieldBosses.ByCode[Dartan], map, GameData.Embedded, Now + 3 * Minute).Status);
        await session.TickAsync(Now + 2000 + 120 * Minute);
        Assert.False(boss.Dead);
    }

    [Fact]
    public async Task Staff_respawn_now_and_timer_changes_are_stored()
    {
        var store = new MemoryStore();
        var world = World(store, Now);
        var map = world.Map(1010);
        var boss = Kill(map, Neikel, Now);
        Assert.True(world.FieldBosses.SetRange(Neikel, 30, 45));
        Assert.False(world.FieldBosses.SetRange(Neikel, 50, 40));
        Assert.False(world.FieldBosses.SetRange(Neikel, 0, 40));
        Assert.False(world.FieldBosses.SetRange(Neikel, 30, FieldBossTimers.MaxMinutes + 1));

        world.FieldBosses.RespawnNow(Neikel, Now + Minute);
        Assert.True(map.RespawnFieldBoss(Neikel, Now + Minute));
        Assert.Equal(Now + Minute, map.RespawnAt[boss.Key]);
        Assert.Equal("due", world.FieldBosses.View(world.FieldBosses.ByCode[Neikel], map, GameData.Embedded, Now + Minute).Status);
        await world.FieldBosses.FlushAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now + Minute), store.Rows[Neikel].RespawnAt);
        Assert.Equal((30, 45), (store.Rows[Neikel].MinMinutes, store.Rows[Neikel].MaxMinutes));

        // The next kill uses the staff's range; after a restart the range is still there; reset brings back the default.
        var restarted = World(store, Now + 2 * Minute);
        await FieldBossWorld.LoadAsync(store, restarted);
        Assert.False(restarted.Map(1010).FieldBossTarget(Neikel)!.Dead);
        Assert.Equal((30, 45), restarted.FieldBosses.Range(Neikel));
        var respawn = restarted.FieldBosses.Killed(Neikel, Now + 2 * Minute);
        Assert.InRange(respawn, Now + 32 * Minute, Now + 47 * Minute);
        Assert.True(restarted.FieldBosses.SetRange(Neikel, null, null));
        Assert.Equal((180, 180), restarted.FieldBosses.Range(Neikel));
        await restarted.FieldBosses.FlushAsync();
        Assert.Null(store.Rows[Neikel].MinMinutes);
    }

    static long Utc(int year, int month, int day, int hour, int minute) => new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Abyss_bosses_return_at_the_next_time_of_their_official_schedule()
    {
        // Now is Monday 2026-10-26 07:33:20 UTC.
        var store = new MemoryStore();
        var world = World(store, Now);
        world.FieldBosses.TimeZone = TimeZoneInfo.Utc;
        Assert.Equal(13, world.FieldBosses.Definitions.Count(d => d.Schedule is not null));
        Assert.Equal("Sun, Fri 21:00", world.FieldBosses.ByCode[2003].Schedule!.Describe());
        Assert.Equal("every 3 h from 01:00", world.FieldBosses.ByCode[2002].Schedule!.Describe());
        Assert.Null(world.FieldBosses.ByCode[Dartan].Schedule);

        // 2003 (Sun, Fri 21:00), killed on the Abyss map: back on Friday at 21:00, stored as such.
        var map = world.Map(20);
        var boss = Kill(map, 2003, Now);
        Assert.Equal(Utc(2026, 10, 30, 21, 0), map.RespawnAt[boss.Key]);
        Assert.Equal("Sun, Fri 21:00", world.FieldBosses.View(world.FieldBosses.ByCode[2003], map, GameData.Embedded, Now).Schedule);
        await world.FieldBosses.FlushAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Utc(2026, 10, 30, 21, 0)), store.Rows[2003].RespawnAt);

        // Killed at its own time: the next one. Cycles: 2002 every 3 h from 01:00, 2001 every 12 h from 10:30.
        Assert.Equal(Utc(2026, 11, 1, 21, 0), world.FieldBosses.Killed(2003, Utc(2026, 10, 30, 21, 0)));
        Assert.Equal(Utc(2026, 10, 26, 10, 0), world.FieldBosses.Killed(2002, Now));
        Assert.Equal(Utc(2026, 10, 26, 10, 30), world.FieldBosses.Killed(2001, Now));
        Assert.Equal(Utc(2026, 10, 27, 1, 0), world.FieldBosses.Killed(2002, Utc(2026, 10, 26, 22, 0)));

        // The schedule is in server local time: 2203 (Mon, Thu, Sat 21:30) at UTC+9 is 12:30 UTC that Monday.
        world.FieldBosses.TimeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+9", TimeSpan.FromHours(9), "UTC+9", "UTC+9");
        Assert.Equal(Utc(2026, 10, 26, 12, 30), world.FieldBosses.Killed(2203, Now));

        // A staff range replaces the schedule; reset brings it back.
        world.FieldBosses.TimeZone = TimeZoneInfo.Utc;
        Assert.True(world.FieldBosses.SetRange(2003, 60, 60));
        Assert.Equal(Now + 60 * Minute, world.FieldBosses.Killed(2003, Now));
        Assert.True(world.FieldBosses.SetRange(2003, null, null));
        Assert.Equal(Utc(2026, 10, 26, 21, 0) + 4 * 24 * 60 * Minute, world.FieldBosses.Killed(2003, Now));
    }

    [Fact]
    public void Instance_layers_and_ordinary_monsters_keep_the_usual_respawn()
    {
        var world = new SharedWorld(GameData.Embedded) { Clock = () => Now };
        var map = world.Map(1010);
        var ordinary = map.Targets.Values.First(t => t.Hostile && t.OwnerKey == 0 && map.FieldBossTarget(101022)?.Key != t.Key && t.NpcId != 2101120
            && !world.FieldBosses.Definitions.Any(d => d.NpcId == t.NpcId));
        Assert.Null(map.FieldBossKilled(ordinary, Now));
        var layer = world.Map(1010, instance: 123);
        Assert.Null(layer.FieldBossTarget(Dartan));
    }
}
