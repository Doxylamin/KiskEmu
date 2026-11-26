using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Content tickets (GameSession.ContentTickets.cs; port of the community forks addiboii and zaraza87, forge #72).</summary>
public class ContentTicketTests
{
    const long Now = 1_762_970_000_000;   // 2025-11-12 17:53:20 UTC, a Wednesday
    const int DailyDungeon = 900001, DailyTicket = 101, AwakenTicket = 80000001, OdyleEnergy = 60000001;

    static long Utc(int year, int month, int day, int hour) => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static string Hex(byte[] packet) => Convert.ToHexStringLower(packet);

    [Fact]
    public void Tables_hold_73_tickets_127_dungeon_uses_and_the_cube_costs()
    {
        var tickets = ContentTicketTables.Embedded;
        Assert.Equal(73, tickets.All.Count);
        Assert.Equal(127, tickets.DungeonsWithUses);
        Assert.Equal(tickets.All.Select(t => t.Id).Order(), tickets.All.Select(t => t.Id));
        Assert.Equivalent(new ContentTicketTables.Use(AwakenTicket, 1, "Enter"), tickets.UsesOf(910011).Single());
        Assert.Equivalent(new ContentTicketTables.Use(70000001, 1, "Clear"), tickets.UsesOf(690001).Single());
        Assert.Equivalent(new ContentTicketTables.Use(60000009, 1, "Unclaimed"), tickets.UsesOf(600001).Single());
        Assert.Equal((OdyleEnergy, 20), tickets.CubeCost(600002));
        Assert.Equal((OdyleEnergy, 40), tickets.CubeCost(600001));
        Assert.True(tickets.Of(AwakenTicket) is { Counted: true, Max: 3, Start: 3, Weekly: [3, 57600, 3] });
        Assert.True(tickets.Of(3) is { Counted: false, Type: "Time", Start: 25_200_000 });
    }

    [Fact]
    public void Recharges_daily_and_weekly_at_16_KST_and_by_cycle_up_to_the_maximum()
    {
        var tickets = ContentTicketTables.Embedded;
        var awaken = tickets.Of(AwakenTicket)!;   // +3 weekly, Wednesday 57600 s KST = Wednesday 07:00 UTC
        Assert.Equal(3, ContentTicketTables.Recharged(awaken, 0, Utc(2026, 11, 10, 12), Utc(2026, 11, 11, 7)));
        Assert.Equal(0, ContentTicketTables.Recharged(awaken, 0, Utc(2026, 11, 11, 8), Utc(2026, 11, 17, 12)));
        Assert.Equal(3, ContentTicketTables.Recharged(awaken, 1, Utc(2026, 10, 1, 12), Utc(2026, 11, 17, 12)));
        var field = tickets.Of(1)!;   // +3 daily at 57600 s KST = 07:00 UTC, max 12
        Assert.Equal(6, ContentTicketTables.Recharged(field, 0, Utc(2026, 11, 10, 6), Utc(2026, 11, 11, 8)));
        Assert.Equal(3, ContentTicketTables.Recharged(field, 0, Utc(2026, 11, 10, 8), Utc(2026, 11, 11, 8)));
        var normal = tickets.Of(60000006)!;   // +1 every 28800 s, max 21
        Assert.Equal(2, ContentTicketTables.Recharged(normal, 0, Utc(2026, 11, 10, 0), Utc(2026, 11, 10, 16)));
        // A count above the maximum (items: OBSERVED 60000001 at 840 of 560) stays as it is.
        Assert.Equal(840, ContentTicketTables.Recharged(tickets.Of(OdyleEnergy)!, 840, Utc(2026, 11, 10, 0), Utc(2026, 11, 12, 0)));
    }

    [Fact]
    public async Task A_new_character_lists_the_observed_new_character_tickets_on_0x610b()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var observed = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "data", "contents-tickets-new-character.hex")).Trim();
        Assert.Equal("0b61" + observed.ToLowerInvariant(), Hex(session.ContentsTicketsList(Now)));
        // Every counted ticket is stored from the first login on, so its recharges count from then.
        await session.TickAsync(Now + 100);
        Assert.Equal(14, store.ContentTicket(DailyTicket));
        Assert.Equal(180, store.ContentTicket(OdyleEnergy));
        Assert.Null(store.ContentTicket(3));
    }

    [Fact]
    public async Task Stored_counts_and_recharges_show_in_the_entry_list()
    {
        var store = new FakeCharacterStore();
        store.ContentTickets.Add(new ContentTicketRecord(AwakenTicket, 0, Utc(2026, 11, 10, 12)));
        store.ContentTickets.Add(new ContentTicketRecord(70000001, 0, Utc(2026, 11, 10, 12)));
        var later = Utc(2026, 11, 11, 8);   // past Wednesday 07:00 UTC
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, later);
        var list = session.ContentsTicketsList(later);
        Assert.Contains("04" + "01b4c404" + "03", Hex(list));   // 80000001 recharged to 3
        Assert.Equal(3, session.ContentTicketCount(70000001, later));
    }

    [Fact]
    public async Task Dungeon_enter_request_takes_the_daily_ticket_and_answers_0xe233()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(DailyDungeon).ToArray(), Now + 50);
        Assert.Equal("33e2" + "0000" + "a1bb0d00", Hex(replies[0]));
        // ContentsTicketUpdate_NT {00, mask 04, ticket 101, count 13, reason 3 Use}.
        Assert.Equal("0c61" + "00" + "04" + "65000000" + "0d" + "03", Hex(replies[1]));
        Assert.NotEqual(0, session.InstanceKey);
        await session.TickAsync(Now + 100);
        Assert.Equal(13, store.ContentTicket(DailyTicket));
    }

    [Fact]
    public async Task Dungeon_enter_request_without_the_ticket_is_refused()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        store.ContentTickets.Add(new ContentTicketRecord(DailyTicket, 0, Now));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(DailyDungeon).ToArray(), Now + 50);
        Assert.Equal("33e2" + "0545" + "a1bb0d00", Hex(Assert.Single(replies)));   // 17669 ContentsSweep_NotEnoughEntraceTicket
        Assert.Equal(0, session.InstanceKey);
        Assert.Empty(session.EnterDungeonById(DailyDungeon, Now + 60));
        var unknown = await session.HandleAsync(new PacketWriter().U16(GameSession.DungeonEnterRq).I32(12345).ToArray(), Now + 70);
        Assert.Equal(15602, BitConverter.ToUInt16(Assert.Single(unknown), 2));
    }

    [Fact]
    public async Task Expedition_entry_takes_no_entry_ticket()
    {
        // MapEntrance names only the Unclaimed reward ticket for expeditions: neither checked nor taken at the entry.
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        store.ContentTickets.Add(new ContentTicketRecord(60000009, 0, Now));
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        Assert.NotEmpty(session.EnterDungeonById(600002, Now + 50));
        var tick = await session.TickAsync(Now + 60_000);
        Assert.DoesNotContain(tick, b => GameSessionTestHelpers.Op(b) == GameSession.ContentsTicketUpdateNt && b[^1] == 3);
        Assert.Equal(0, store.ContentTicket(60000009));
    }

    [Fact]
    public async Task A_cycle_boundary_sends_the_recharge_update()
    {
        var store = new FakeCharacterStore();
        var tenMinutes = (Now / 600_000 + 2) * 600_000;   // a ten-minute mark that is not a three-hour one
        Assert.NotEqual(0, tenMinutes % 10_800_000);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, tenMinutes - 5_000);
        var before = await session.TickAsync(tenMinutes - 1_000);
        Assert.DoesNotContain(before, b => GameSessionTestHelpers.Op(b) == GameSession.ContentsTicketUpdateNt);
        var at = await session.TickAsync(tenMinutes + 1_000);
        var updates = at.Where(b => GameSessionTestHelpers.Op(b) == GameSession.ContentsTicketUpdateNt).Select(Hex).ToList();
        // Tickets 10-12 (600 s cycle): 2 at the start, 3 after the mark, reason 2 Recharge (OBSERVED form `0c61 00 04 0c000000 0e 02`).
        Assert.Contains("0c61" + "00" + "04" + "0a000000" + "03" + "02", updates);
        Assert.Contains("0c61" + "00" + "04" + "0c000000" + "03" + "02", updates);
        Assert.DoesNotContain(updates, u => u.Contains("01879303"));   // 60000001's 3-hour cycle did not pass
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "data", "expedition.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }
}
