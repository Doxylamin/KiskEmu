using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Content tickets (ContentTicketTables; port of the community forks addiboii 87afb91 / feadae2 / 894105a / 660fe39 and
// zaraza87's DungeonEnter_RQ, forge #72).
//
// OBSERVED (our capture of 2026-11-12, the forks' recordings 2026-11-04 to 06): ContentsTickets_NT 0x610b at world entry =
// varint n, then per ticket by id {mask u8, id i32, bit0 remain time i64 ms, bit1 remain charge time i64, bit2 remain count
// varint, bit3 remain charge count varint}, a zero field left out (all 73 tickets). ContentsTicketUpdate_NT 0x610c = u8 mask
// (bit0: charge value follows), one ticket as above, u8 ETicketReason (1 Charge, 2 Recharge, 3 Use), [i32 charge value].
// A new character's list (tools/data/contents-tickets-new-character.hex, zaraza87) gives each ticket's starting count.
// Each TimeRechargeCycle boundary sends 0x610c reason 2 for that ticket, also when it is full (OBSERVED for the 600 s
// tickets 10 and 12: `0c61 00 04 0c000000 0e 02`; other cycles CANDIDATE). The reward cube costs its ConsumeTicketCount of
// ticket 60000001 (Odyle energy), the 0x610c before the items (OBSERVED in the fork's recordings: KraoCave Easy 20, Normal 40).
//
// MapEntrance.dat ContentsTicketList names, per dungeon, the tickets an entry checks and when it takes them (DeductType
// Enter at the entry, Clear at the clear; None takes nothing; Unclaimed is neither checked nor taken here). OURS: an entry
// short of a counted ticket is refused (staff and development logins pass); the forks differ (addiboii only counts unless
// Native:EnforceContentTickets, zaraza87 refuses as we do). Not modelled: Time tickets' spent time (they show their starting
// time), the charge count (the "(+n)" pool that Odyle Energy items fill, AddContentsTicket).
//
// DungeonEnter_RQ 0xe232 {dungeon i32} (the Daily dungeon and Abyss buttons): DungeonEnter_RS 0xe233 {u16 result, dungeon
// i32} (usmap fields; never recorded officially, CANDIDATE), then the ticket updates and the move (zaraza87).
public sealed partial class GameSession
{
    public const ushort ContentsTicketsNt = 0x610b, ContentsTicketUpdateNt = 0x610c, DungeonEnterRq = 0xe232, DungeonEnterRs = 0xe233;
    const byte TicketRecharged = 2, TicketSpent = 3;   // ETicketReason Recharge, Use
    // EResult (usmap; the numbering matches the codes seen in our capture, e.g. 814/815 kItem_Use_ContentsTicket_*).
    const int ResultDungeonInvalidId = 15602, ResultDungeonInvalidMap = 15604, ResultDungeonTooLowLevel = 15607,
        ResultDungeonTooHighLevel = 15608, ResultDungeonSameMap = 15610, ResultNotEnoughEntranceTicket = 17669;

    static ContentTicketTables ContentTickets => ContentTicketTables.Embedded;

    /// <summary>Ticket id -> (count, when that count held, ms) for the counted tickets.</summary>
    readonly Dictionary<int, (int Count, long At)> contentTickets = [];
    readonly HashSet<int> contentTicketsDirty = [];
    readonly List<byte[]> contentTicketOut = [];
    long ticketCycleCheckedAt;

    /// <summary>The stored counts; a counted ticket without a row starts at the new character's count from now.</summary>
    void LoadContentTickets(IReadOnlyList<ContentTicketRecord> stored, long now)
    {
        contentTickets.Clear();
        contentTicketsDirty.Clear();
        contentTicketOut.Clear();
        foreach (var row in stored) contentTickets[row.TicketId] = (Math.Max(0, row.Remaining), row.UpdatedMs);
        foreach (var ticket in ContentTickets.All)
            if (ticket.Counted && !contentTickets.ContainsKey(ticket.Id))
            {
                contentTickets[ticket.Id] = ((int)Math.Clamp(ticket.Start, 0, int.MaxValue), now);
                contentTicketsDirty.Add(ticket.Id);
            }
        ticketCycleCheckedAt = now;
    }

    /// <summary>The ticket's count now (the recharges since the stored count added); int.MaxValue for a ticket not counted.</summary>
    public int ContentTicketCount(int ticketId, long now)
    {
        if (ContentTickets.Of(ticketId) is not { Counted: true } ticket) return int.MaxValue;
        return contentTickets.TryGetValue(ticketId, out var held)
            ? ContentTicketTables.Recharged(ticket, held.Count, held.At, now)
            : (int)Math.Clamp(ticket.Start, 0, int.MaxValue);
    }

    /// <summary>The first counted ticket the dungeon's entry is short of (checked: Enter, Clear and None uses), or 0.</summary>
    int MissingContentTicket(int dungeonId, long now)
    {
        foreach (var use in ContentTickets.UsesOf(dungeonId))
        {
            if (use.Deduct == "Unclaimed" || ContentTickets.Of(use.TicketId) is not { Counted: true }) continue;
            if (ContentTicketCount(use.TicketId, now) < Math.Max(1, use.Count)) return use.TicketId;
        }
        return 0;
    }

    /// <summary>Whether the dungeon's entry tickets allow it; staff and development logins (no stored character) pass.</summary>
    bool ContentTicketsAllowEntry(DungeonInfo room, long now, out string why)
    {
        why = "";
        if (IsAdmin || Store is null) return true;
        var missing = MissingContentTicket(MapEventData.DungeonIdOfMap(room.Map), now);
        if (missing == 0) return true;
        why = $"no {ContentTickets.Of(missing)?.Name ?? missing.ToString(System.Globalization.CultureInfo.InvariantCulture)} left";
        return false;
    }

    /// <summary>Takes the tickets the dungeon deducts at this moment ("Enter" or "Clear").</summary>
    void SpendContentTickets(int dungeonId, string moment, long now)
    {
        if (IsAdmin || Store is null) return;
        foreach (var use in ContentTickets.UsesOf(dungeonId))
            if (use.Deduct == moment && use.Count > 0 && ContentTickets.Of(use.TicketId) is { Counted: true })
                SpendTicket(use.TicketId, use.Count, now);
    }

    /// <summary>Takes <paramref name="count"/> of a counted ticket (not below 0) and queues its 0x610c reason 3.</summary>
    void SpendTicket(int ticketId, int count, long now)
    {
        contentTickets[ticketId] = (Math.Max(0, ContentTicketCount(ticketId, now) - count), now);
        contentTicketsDirty.Add(ticketId);
        if (ContentTickets.Of(ticketId) is { } ticket)
            contentTicketOut.Add(WriteTicket(PacketWriter.Opcode(ContentsTicketUpdateNt).U8(0), ticket, now).U8(TicketSpent).ToArray());
    }

    /// <summary>One ticket as 0x610b/0x610c carry it: mask, id, then the fields the mask names (a zero one left out).</summary>
    PacketWriter WriteTicket(PacketWriter w, ContentTicketTables.Ticket ticket, long now)
    {
        var time = ticket.Type == "Time" ? Math.Max(0, ticket.Start) : 0;
        var count = ticket.Counted ? Math.Max(0, ContentTicketCount(ticket.Id, now)) : 0;
        w.U8((byte)((time > 0 ? 1 : 0) | (count > 0 ? 4 : 0))).I32(ticket.Id);
        if (time > 0) w.I64(time);
        if (count > 0) w.Varint((uint)count);
        return w;
    }

    /// <summary>ContentsTickets_NT 0x610b: every ticket of the table, by id.</summary>
    internal byte[] ContentsTicketsList(long now)
    {
        var all = ContentTickets.All;
        var w = PacketWriter.Opcode(ContentsTicketsNt).Varint((uint)all.Count);
        foreach (var ticket in all) WriteTicket(w, ticket, now);
        return w.ToArray();
    }

    /// <summary>A ticket's TimeRechargeCycle boundary passed since the last tick: 0x610c {00, the ticket, reason 2}, also when full.</summary>
    void ContentTicketRechargeTick(long now)
    {
        var from = ticketCycleCheckedAt;
        ticketCycleCheckedAt = now;
        if (from == 0 || now <= from || Store is null) return;
        foreach (var ticket in ContentTickets.All)
            if (ticket.Counted && ticket.Cycle is [var seconds, > 0] && seconds > 0 && now / (seconds * 1000L) > from / (seconds * 1000L))
                contentTicketOut.Add(WriteTicket(PacketWriter.Opcode(ContentsTicketUpdateNt).U8(0), ticket, now).U8(TicketRecharged).ToArray());
    }

    /// <summary>The 0x610c updates queued since the last call.</summary>
    List<byte[]> ContentTicketUpdates()
    {
        var updates = contentTicketOut.ToList();
        contentTicketOut.Clear();
        return updates;
    }

    /// <summary>What the dungeon's reward cube costs, taken when it pays; false when not enough is left (staff pass).</summary>
    bool PayCubeTicket(int dungeonId, long now)
    {
        if (IsAdmin || Store is null || ContentTickets.CubeCost(dungeonId) is not { } cost || ContentTickets.Of(cost.TicketId) is not { Counted: true } ticket)
            return true;
        if (ContentTicketCount(ticket.Id, now) < cost.Count)
        {
            Note($"cube: {ticket.Name} {ContentTicketCount(ticket.Id, now)} < {cost.Count}");
            return false;
        }
        SpendTicket(ticket.Id, cost.Count, now);
        return true;
    }

    async Task FlushContentTicketsAsync(CancellationToken ct)
    {
        if (contentTicketsDirty.Count == 0 || Store is not { } store || CharacterId is not { } character) return;
        var rows = contentTicketsDirty.Where(contentTickets.ContainsKey)
            .Select(id => new ContentTicketRecord(id, contentTickets[id].Count, contentTickets[id].At)).ToList();
        contentTicketsDirty.Clear();
        if ((await Stored(async () => await store.SaveContentTicketsAsync(character, rows, ct) ? [[]] : [])).Count == 0)
            foreach (var row in rows) contentTicketsDirty.Add(row.TicketId);
    }

    /// <summary>DungeonEnter_RQ {dungeon i32}: the checks, the reply, the Enter tickets, the move (zaraza87; the reply CANDIDATE).</summary>
    List<byte[]> DungeonEnterRequest(byte[] plain, long now)
    {
        if (plain.Length < 6) return [];
        var dungeonId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonEnterRs).U16((ushort)result).I32(dungeonId).ToArray();
        if (MapEventData.Dungeon(dungeonId) is not { } room) return [Reply(ResultDungeonInvalidId)];
        if (resources is null || reloading || dead || instanceKey != 0) return [Reply(ResultDungeonSameMap)];
        if (!IsAdmin && Store is not null)
        {
            if (room.MinLevel > 0 && level < room.MinLevel) return [Reply(ResultDungeonTooLowLevel)];
            if (room.MaxLevel > 0 && level > room.MaxLevel) return [Reply(ResultDungeonTooHighLevel)];
            if (MissingContentTicket(dungeonId, now) is not 0 and var missing)
            {
                Note($"dungeon {room.Name}: ticket {missing} used up");
                return [Reply(ResultNotEnoughEntranceTicket)];
            }
        }
        // An Abyss zone is shared (no copy, no ticket count): the race's start point of its map.
        var moved = room.Type == "Abyss" ? TeleportToMap(room.Map, now) : EnterDungeonById(dungeonId, now);
        if (moved.Count == 0) return [Reply(ResultDungeonInvalidMap)];
        return [Reply(0), .. ContentTicketUpdates(), .. moved];
    }
}
