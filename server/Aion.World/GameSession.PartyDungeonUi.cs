using System.Text;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Expedition UI / dungeon room matchmaking. Live numbers (#74): the room run is usmap 0x3AC1.. + 0x5c3f = 0x9700.. (OBSERVED
// 0x9701 room lists 2026-11-04, matching the community fork's 0x9701/0x9702/0x970b recordings); the 0x3A6x family is + 0x58ca
// (OBSERVED 0x9330 "309300" and 0x9331 "319300" at every entry; PartyDungeonEnter 0x932b/0x932c CANDIDATE by the same run).
// Wire layout OBSERVED (0x9701):
//   DungeonRoomInfo: key i32 | desc str | limit u8 | dungeon i32 | level u8 | ERace u8 | leader dbid i64 | public bool |
//     EPartyJoinType u8 | EDungeonState u8 | members varint + member records (every slot, empty ones too) | affixes varint + i32.
//   DungeonRoomMemberInfo: presence mask u8 (bit 0 conqueror level i32, bit 1 ready, bit 2 born server u16, bit 3 current server
//     u16, bit 4 rebirth item count i64; an optional field is only written when not its default) | slot number u8 (1-based) |
//     dbid i64 | name str | pc id i32 | level i32 | [conqueror] | equip item level i32 | [ready bool] | login bool | [born server]
//     | [current server] | EPartyRole u8 | combat power i64 | tickets map varint | [rebirth] | EMentoringRole u8 | latency u8.
//     Empty slot: mask 0, its number, zeros, role 4 (All). Bools pack across records (PacketWriter.Bool).
//   List_RS: result u16 | dungeon i32 | my room (none: zeros, public, join 2) | rooms varint + rooms.
//   InstantJoin_RS `0000 00000000`, Ready_RS `0000 01`, Leave_RS `0000`, Kickout_NT `00` (fork recordings). Results are u16.
//   EDungeonRoomInfoUpdateReason: 2 Join, 4 LeaveMember OBSERVED; 1 Create, 5 Kick CANDIDATE (fork).
// Flow (OURS): create or join a waiting room → optional apply/invite → PartyDungeonEnter or Start moves every member into
// the same instance copy (SharedWorld instance key = room key) and forms a party; each member spends an expedition ticket.
public sealed partial class GameSession
{
    public const ushort PartyDungeonEnterRq = 0x932b, PartyDungeonEnterRs = 0x932c, BookmarkedContentListNt = 0x9330, PartyDungeonChallengeNt = 0x9331,
        DungeonRoomListRq = 0x9700, DungeonRoomListRs = 0x9701, DungeonRoomInfoNt = 0x9702,
        DungeonRoomCreateRq = 0x9703, DungeonRoomCreateRs = 0x9704, DungeonRoomJoinRq = 0x9705, DungeonRoomJoinRs = 0x9706,
        DungeonRoomApplyNt = 0x9707, DungeonRoomApplyResponseRq = 0x9708, DungeonRoomApplyResponseRs = 0x9709, DungeonRoomApplyResponseNt = 0x970a,
        DungeonRoomAddMemberNt = 0x970b, DungeonRoomInviteRq = 0x970c, DungeonRoomInviteRs = 0x970d, DungeonRoomInviteNt = 0x970e,
        DungeonRoomInviteResponseRq = 0x970f, DungeonRoomInviteResponseRs = 0x9710, DungeonRoomLeaveRq = 0x971c, DungeonRoomLeaveRs = 0x971d,
        DungeonRoomLeaveNt = 0x971b, DungeonRoomKickMemberRq = 0x9728, DungeonRoomKickMemberRs = 0x9729, DungeonRoomKickoutNt = 0x972a,
        DungeonRoomInstantJoinRq = 0x9712, DungeonRoomInstantJoinRs = 0x9713,
        DungeonRoomReadyRq = 0x9714, DungeonRoomReadyRs = 0x9715, DungeonRoomReadyNt = 0x9716,
        DungeonRoomStartRq = 0x9717, DungeonRoomStartRs = 0x9718, DungeonRoomDisbandNt = 0x971a, DungeonRoomNewLeaderNt = 0x9719,
        DungeonRoomDisconnectMemberNt = 0x971e,
        DungeonRoomEditSettingRq = 0x9720, DungeonRoomEditSettingRs = 0x9721, DungeonRoomSettingUpdatedNt = 0x9722,
        DungeonRoomRetryRq = 0x972e, DungeonRoomRetryRs = 0x972f, DungeonRoomAskRetryNt = 0x9730,
        DungeonRoomRetryResponseRq = 0x9731, DungeonRoomRetryResponseRs = 0x9732, DungeonRoomRetryResponseNt = 0x9733, DungeonRoomRetryFailNt = 0x9734;

    const int RoomFailed = 1;
    const byte RoomInfoCreate = 1, RoomInfoJoin = 2, RoomInfoLeave = 4, RoomInfoKick = 5, RoomInfoDisband = 4;
    const byte RoomRoleAll = 4;
    const byte ApplyAccept = 0, ApplyRefuse = 1;
    const byte InviteAccept = 0, RetryAccept = 0, RetryRefuse = 1;
    const long RoomInviteMs = 30_000, RoomApplyMs = 30_000;

    /// <summary>Solo create path before PartyDungeonEnter (no room).</summary>
    int pendingPartyDungeonId;

    static byte[] BookmarkedContentList() => PacketWriter.Opcode(BookmarkedContentListNt).Varint(0).ToArray();

    static byte[] PartyDungeonChallengeList() => PacketWriter.Opcode(PartyDungeonChallengeNt).Varint(0).ToArray();

    DungeonRoom? MyRoom => worldPlayer is null ? null : Social?.DungeonRoomOf(worldPlayer);

    static string ReadFString(PacketReader reader)
    {
        var length = checked((int)reader.Varint());
        return Encoding.UTF8.GetString(reader.Take(length));
    }

    static List<int> ReadAffixList(PacketReader reader)
    {
        // The count is the client's: bound it before allocating (a count near 2^31 asked for an ~8 GB list).
        var count = reader.Varint();
        if (count > 32) throw new ProtocolException($"affix list of {count}");
        var list = new List<int>((int)count);
        for (var i = 0; i < count; i++) list.Add(reader.I32());
        return list;
    }

    static void SkipWantedRole(PacketReader reader)
    {
        var count = checked((int)reader.Varint());
        for (var i = 0; i < count; i++) { reader.U8(); reader.U8(); }
    }

    (string Desc, bool Public, byte JoinType, List<int> Affixes)? ParseEditSetting(byte[] plain)
    {
        if (plain.Length < 3) return null;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var desc = ReadFString(reader);
            var pub = reader.Bool();
            var join = reader.U8();
            if (reader.Remaining > 0) SkipWantedRole(reader);
            if (reader.Remaining > 0) reader.Bool();
            var affixes = reader.Remaining > 0 ? ReadAffixList(reader) : [];
            return (desc, pub, join, affixes);
        }
        catch (ProtocolException) { return null; }
    }

    (int ContentDungeonId, byte DungeonLevel, byte Race)? ParseInstantJoin(byte[] plain)
    {
        if (plain.Length < 7) return null;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var dungeonId = reader.I32();
            if (reader.Remaining > 0) SkipWantedRole(reader);
            var level = reader.Remaining > 0 ? reader.U8() : (byte)0;
            var race = reader.Remaining > 0 ? reader.U8() : (byte)0;
            return (dungeonId, level, race);
        }
        catch (ProtocolException) { return null; }
    }

    (int ContentDungeonId, byte DungeonLevel, byte Race, string Desc, bool Public, byte JoinType, List<int> Affixes)? ParseCreate(byte[] plain)
    {
        if (plain.Length < 7) return null;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var dungeonId = reader.I32();
            var level = reader.Remaining > 0 ? reader.U8() : (byte)0;
            var race = reader.Remaining > 0 ? reader.U8() : (byte)0;
            var desc = reader.Remaining > 0 ? ReadFString(reader) : "";
            var pub = reader.Remaining > 0 && reader.Bool();
            var join = reader.Remaining > 0 ? reader.U8() : PartyOptions.JoinFree;
            if (reader.Remaining > 0) SkipWantedRole(reader);
            if (reader.Remaining > 0) reader.Bool();
            var affixes = reader.Remaining > 0 ? ReadAffixList(reader) : [];
            return (dungeonId, level, race, desc, pub, join, affixes);
        }
        catch (ProtocolException) { return null; }
    }

    static void WriteInvitation(PacketWriter w, InvitationInfo i) =>
        w.I32(i.PcId).I32(i.Level).I32(i.EquipItemLevel).String(i.Nickname).I32(i.ServerId).I32(i.ConquerorLevel).I64(i.CombatPower).U8(i.MentoringRole);

    /// <summary>One DungeonRoomMemberInfo (OBSERVED layout, see the top). Optional fields go only when set: no conqueror level or
    /// rebirth items here; the servers are this host's.</summary>
    internal static void WriteDungeonRoomMember(PacketWriter w, byte number, long dbId, string name, int pcId, int level, int equipItemLevel,
        bool ready, int server, byte role = 0, long combatPower = 0)
    {
        var mask = (ready ? 0x02 : 0) | (server != 0 ? 0x0c : 0);
        w.U8(mask).U8(number).I64(dbId).String(name).I32(pcId).I32(level).I32(equipItemLevel);
        if (ready) w.Bool(true);
        w.Bool(dbId != 0);
        if (server != 0) w.U16(server).U16(server);
        w.U8(role).I64(combatPower).Varint(0).U8(dbId != 0 ? 1 : 0).U8(dbId != 0 ? 1 : 0);
    }

    internal static void WriteEmptyDungeonRoomMember(PacketWriter w, byte number) =>
        WriteDungeonRoomMember(w, number, 0, "", 0, 0, 0, false, 0, RoomRoleAll);

    void WriteDungeonRoomMember(PacketWriter w, WorldPlayer member, byte number, bool ready)
    {
        var s = member.Session;
        WriteDungeonRoomMember(w, number, s.DbId, s.StoredName, s.Profile()?.PcId ?? 1, s.level, s.EquipItemLevel(), ready, ServerId);
    }

    void WriteDungeonRoomInfo(PacketWriter w, DungeonRoom room, byte dungeonState = 0)
    {
        w.I32(room.RoomKey).String(room.Desc).U8((byte)Math.Min(room.Limit, 255)).I32(room.ContentDungeonId).U8(room.DungeonLevel)
            .U8(room.Race).I64(room.Leader.Session.DbId).Bool(room.Public).U8(room.JoinType).U8(dungeonState)
            .Varint((uint)Math.Max(room.Limit, room.Members.Count));
        // OBSERVED: every slot is listed; the leader is never marked ready.
        for (var i = 0; i < room.Members.Count; i++)
            WriteDungeonRoomMember(w, room.Members[i], (byte)(i + 1), room.Members[i] != room.Leader && room.Ready.Contains(room.Members[i].Key));
        for (var i = room.Members.Count; i < room.Limit; i++) WriteEmptyDungeonRoomMember(w, (byte)(i + 1));
        w.Varint((uint)room.AffixList.Count);
        foreach (var affix in room.AffixList) w.I32(affix);
    }

    byte[] DungeonRoomInfoPacket(DungeonRoom room, byte reason)
    {
        var w = PacketWriter.Opcode(DungeonRoomInfoNt);
        WriteDungeonRoomInfo(w, room);
        return w.U8(reason).ToArray();
    }

    static int TicketFailId(DungeonInfo room) => Expeditions.TicketFor(room)?.Id ?? 0;

    bool RoomEntryAllowed(DungeonInfo room, long now, out int ticketId, bool allowInCopy = false)
    {
        ticketId = 0;
        if ((!allowInCopy && instanceKey != 0) || resources is null || reloading || dead) return false;
        if (MapEventData.Dungeon(room.Map) is null || room.Type != "Party") return false;
        if (!IsAdmin && !MeetsEntry(room, now, out _)) { ticketId = TicketFailId(room); return false; }
        return true;
    }

    void BroadcastRoom(DungeonRoom room, byte[] packet, WorldPlayer? except = null)
    {
        foreach (var member in room.Members)
            if (member != except) member.Send(packet);
    }

    void BroadcastRoomInfo(DungeonRoom room, byte reason, WorldPlayer? except = null) =>
        BroadcastRoom(room, DungeonRoomInfoPacket(room, reason), except);

    /// <summary>0x9700 DungeonRoomList_RQ {dungeon id, level}: waiting rooms for the expedition window.</summary>
    List<byte[]> DungeonRoomList(byte[] plain)
    {
        if (plain.Length < 7) return [];
        var contentId = BitConverter.ToInt32(plain, 2);
        var level = plain.Length > 6 ? plain[6] : (byte)0;
        var resolved = Expeditions.ResolveDungeonId(contentId, level);
        if (MapEventData.Dungeon(resolved) is not { Type: "Party" })
            return [DungeonRoomListReply(RoomFailed, contentId, null, [])];
        var myRoom = MyRoom;
        var rooms = Social?.DungeonRooms.Values
            .Where(r => r.Public && r.ContentDungeonId == contentId && r.DungeonLevel == level && r.ResolvedDungeonId == resolved)
            .ToList() ?? [];
        return [DungeonRoomListReply(0, contentId, myRoom, rooms)];
    }

    byte[] DungeonRoomListReply(int result, int contentId, DungeonRoom? mine, IReadOnlyList<DungeonRoom> rooms)
    {
        var w = PacketWriter.Opcode(DungeonRoomListRs).U16(result).I32(contentId);
        if (mine is null) WriteEmptyDungeonRoomInfo(w); else WriteDungeonRoomInfo(w, mine);
        w.Varint((uint)rooms.Count);
        foreach (var room in rooms) WriteDungeonRoomInfo(w, room);
        return w.ToArray();
    }

    /// <summary>OBSERVED my_room of a player in no room: zeros, public set, join type 2.</summary>
    internal static void WriteEmptyDungeonRoomInfo(PacketWriter w) =>
        w.I32(0).String("").U8(0).I32(0).U8(0).U8(0).I64(0).Bool(true).U8(2).U8(0).Varint(0).Varint(0);

    /// <summary>0x9703 DungeonRoomCreate_RQ: open a waiting room for an expedition tier.</summary>
    List<byte[]> DungeonRoomCreate(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomCreateRs).U16(result).I32(ticketId).ToArray();
        if (ParseCreate(plain) is not { } settings || Social is not { } social || worldPlayer is null)
            return [Reply(RoomFailed)];
        var resolved = Expeditions.ResolveDungeonId(settings.ContentDungeonId, settings.DungeonLevel);
        if (MapEventData.Dungeon(resolved) is not { } room || MyRoom is not null)
            return [Reply(RoomFailed)];
        if (!Expeditions.AffixesAllowed(settings.ContentDungeonId, settings.DungeonLevel, settings.Affixes))
            return [Reply(RoomFailed)];
        if (!RoomEntryAllowed(room, now, out var ticketId))
            return [Reply(RoomFailed, ticketId)];
        var key = checked((int)social.NextId());
        var dungeonRoom = new DungeonRoom(key, worldPlayer, settings.ContentDungeonId, settings.DungeonLevel, resolved)
        {
            Desc = settings.Desc, Public = settings.Public, JoinType = settings.JoinType, Race = settings.Race,
        };
        dungeonRoom.AffixList.AddRange(settings.Affixes);
        social.DungeonRooms[key] = dungeonRoom;
        return [Reply(0), DungeonRoomInfoPacket(dungeonRoom, RoomInfoCreate)];
    }

    byte[] SettingUpdatedPacket(DungeonRoom room)
    {
        var w = PacketWriter.Opcode(DungeonRoomSettingUpdatedNt).String(room.Desc).Bool(room.Public).U8(room.JoinType).Varint(0).Bool(false);
        w.Varint((uint)room.AffixList.Count);
        foreach (var affix in room.AffixList) w.I32(affix);
        return w.ToArray();
    }

    /// <summary>0x9712 auto-match: join a public free room or open one.</summary>
    List<byte[]> DungeonRoomInstantJoin(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomInstantJoinRs).U16(result).I32(ticketId).ToArray();
        if (ParseInstantJoin(plain) is not { } req || Social is not { } social || worldPlayer is null || MyRoom is not null)
            return [Reply(RoomFailed)];
        var resolved = Expeditions.ResolveDungeonId(req.ContentDungeonId, req.DungeonLevel);
        if (MapEventData.Dungeon(resolved) is not { Type: "Party" } dungeon)
            return [Reply(RoomFailed)];
        if (!RoomEntryAllowed(dungeon, now, out var ticketId))
            return [Reply(RoomFailed, ticketId)];
        var room = social.DungeonRooms.Values
            .Where(r => r.Public && r.JoinType == PartyOptions.JoinFree && r.ContentDungeonId == req.ContentDungeonId
                && r.DungeonLevel == req.DungeonLevel && r.ResolvedDungeonId == resolved && r.Members.Count < r.Limit)
            .OrderByDescending(r => r.Members.Count)
            .FirstOrDefault();
        if (room is not null)
            return AddRoomMember(room, worldPlayer, now, [Reply(0)]);
        var key = checked((int)social.NextId());
        room = new DungeonRoom(key, worldPlayer, req.ContentDungeonId, req.DungeonLevel, resolved)
        {
            Public = true, JoinType = PartyOptions.JoinFree, Race = req.Race,
        };
        social.DungeonRooms[key] = room;
        return [Reply(0), DungeonRoomInfoPacket(room, RoomInfoCreate)];
    }

    /// <summary>0x9714 toggle ready in the expedition waiting room.</summary>
    List<byte[]> DungeonRoomReady(byte[] plain)
    {
        byte[] Reply(int result, bool ready) => PacketWriter.Opcode(DungeonRoomReadyRs).U16(result).Bool(ready).ToArray();
        if (plain.Length < 3 || MyRoom is not { } room || worldPlayer is null)
            return [Reply(RoomFailed, false)];
        var ready = plain[2] != 0;
        if (ready) room.Ready.Add(playerKey); else room.Ready.Remove(playerKey);
        var notice = PacketWriter.Opcode(DungeonRoomReadyNt).I64(DbId).Bool(ready).ToArray();
        BroadcastRoom(room, notice, worldPlayer);
        return [Reply(0, ready), notice];
    }

    /// <summary>0x9720 leader edits room settings.</summary>
    List<byte[]> DungeonRoomEditSetting(byte[] plain)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomEditSettingRs).U16(result).ToArray();
        if (ParseEditSetting(plain) is not { } settings || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Reply(RoomFailed)];
        if (!Expeditions.AffixesAllowed(room.ContentDungeonId, room.DungeonLevel, settings.Affixes))
            return [Reply(RoomFailed)];
        room.Desc = settings.Desc;
        room.Public = settings.Public;
        room.JoinType = settings.JoinType;
        room.AffixList.Clear();
        room.AffixList.AddRange(settings.Affixes);
        room.Ready.Clear();
        var updated = SettingUpdatedPacket(room);
        BroadcastRoom(room, updated);
        BroadcastRoomInfo(room, RoomInfoCreate);
        return [Reply(0), updated];
    }

    /// <summary>0x9705 DungeonRoomJoin_RQ: enter a public room or apply when approval is required.</summary>
    List<byte[]> DungeonRoomJoin(byte[] plain, long now)
    {
        byte[] Reply(int result, byte joinType = PartyOptions.JoinFree, int ticketId = 0) =>
            PacketWriter.Opcode(DungeonRoomJoinRs).U16(result).U8(joinType).I32(ticketId).ToArray();
        if (plain.Length < 6 || Social is not { } social || worldPlayer is null || MyRoom is not null)
            return [Reply(RoomFailed)];
        var roomKey = BitConverter.ToInt32(plain, 2);
        if (!social.DungeonRooms.TryGetValue(roomKey, out var room))
            return [Reply(RoomFailed)];
        if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
            return [Reply(RoomFailed)];
        if (!RoomEntryAllowed(dungeon, now, out var ticketId))
            return [Reply(RoomFailed, room.JoinType, ticketId)];
        if (room.Members.Count >= room.Limit)
            return [Reply(RoomFailed, room.JoinType)];
        if (room.JoinType == PartyOptions.JoinApproval)
        {
            room.PendingApply[playerKey] = now + RoomApplyMs;
            var applyW = PacketWriter.Opcode(DungeonRoomApplyNt).I32(room.RoomKey).I64(DbId);
            WriteInvitation(applyW, Invitation());
            var apply = applyW.I64(now + RoomApplyMs).ToArray();
            room.Leader.Send(apply);
            return [Reply(0, room.JoinType)];
        }
        return AddRoomMember(room, worldPlayer, now, [Reply(0, room.JoinType)]);
    }

    /// <summary>0x9708 leader answers a join application.</summary>
    List<byte[]> DungeonRoomApplyResponse(byte[] plain, long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomApplyResponseRs).U16(result).ToArray();
        if (plain.Length < 14 || Social is not { } social || worldPlayer is null || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Reply(RoomFailed)];
        var roomKey = BitConverter.ToInt32(plain, 2);
        if (room.RoomKey != roomKey) return [Reply(RoomFailed)];
        var applicantDbId = BitConverter.ToInt64(plain, 6);
        var accept = plain.Length > 14 ? plain[14] == ApplyAccept : false;
        var applicant = World?.Online.Values.FirstOrDefault(p => p.Session.DbId == applicantDbId);
        if (applicant is null || !room.PendingApply.Remove(applicant.Key))
            return [Reply(RoomFailed)];
        var notice = PacketWriter.Opcode(DungeonRoomApplyResponseNt).I32(room.RoomKey).U8(accept ? ApplyAccept : ApplyRefuse).ToArray();
        applicant.Send(notice);
        if (!accept) return [Reply(0)];
        if (room.Members.Count >= room.Limit || MyRoom is not null && applicant != worldPlayer)
            return [Reply(RoomFailed)];
        if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon || !applicant.Session.RoomEntryAllowed(dungeon, now, out _))
            return [Reply(RoomFailed)];
        return AddRoomMember(room, applicant, now, [Reply(0)]);
    }

    /// <summary>0x970c invite a player by name into the leader's room.</summary>
    List<byte[]> DungeonRoomInvite(byte[] plain, long now)
    {
        byte[] Fail(int ticketId = 0) => PacketWriter.Opcode(DungeonRoomInviteRs).U16(RoomFailed).I32(ServerId).String("").I32(0).I32(0).I64(0).I32(ticketId).ToArray();
        if (Social is not { } social || worldPlayer is null || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Fail()];
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var serverId = reader.I32();
            var name = ReadFString(reader);
            _ = reader.Remaining > 0 ? reader.U8() : (byte)0;
            if (World?.FindOnline(CharacterNames.Stored(name)) is not { } invitee || invitee == worldPlayer || MyRoom is not null && room.Has(invitee))
                return [Fail()];
            if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
                return [Fail()];
            if (!invitee.Session.RoomEntryAllowed(dungeon, now, out var inviteTicket))
                return [Fail(inviteTicket)];
            var expires = now + RoomInviteMs;
            social.DungeonRoomInvites[invitee.Key] = new DungeonRoomInvitePending(room.RoomKey, expires);
            room.PendingInvite[invitee.Key] = expires;
            var inviteW = PacketWriter.Opcode(DungeonRoomInviteNt).I32(room.RoomKey).I32(room.ContentDungeonId).U8(room.DungeonLevel).I64(DbId);
            WriteInvitation(inviteW, Invitation());
            invitee.Send(inviteW.I64(expires).ToArray());
            return [PacketWriter.Opcode(DungeonRoomInviteRs).I32(0).I32(serverId).String(invitee.Name).I32(invitee.Key).I32(invitee.Session.Profile()?.PcId ?? 1)
                .I64(expires).I32(0).ToArray()];
        }
        catch (ProtocolException) { return [Fail()]; }
    }

    /// <summary>0x970f answer a dungeon-room invitation.</summary>
    List<byte[]> DungeonRoomInviteResponse(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomInviteResponseRs).U16(result).I32(ticketId).ToArray();
        if (plain.Length < 10 || Social is not { } social || worldPlayer is null)
            return [Reply(RoomFailed)];
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var roomKey = reader.I32();
            var accept = reader.U8() == InviteAccept;
            if (!social.DungeonRoomInvites.Remove(playerKey, out var pending) || pending.RoomKey != roomKey || pending.ExpiresMs < now)
                return [Reply(RoomFailed)];
            if (!social.DungeonRooms.TryGetValue(roomKey, out var room))
                return [Reply(RoomFailed)];
            room.PendingInvite.Remove(playerKey);
            if (!accept) return [Reply(0)];
            if (MyRoom is not null || room.Members.Count >= room.Limit)
                return [Reply(RoomFailed)];
            if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
                return [Reply(RoomFailed)];
            if (!RoomEntryAllowed(dungeon, now, out var inviteFailTicket))
                return [Reply(RoomFailed, inviteFailTicket)];
            return AddRoomMember(room, worldPlayer, now, [Reply(0)]);
        }
        catch (ProtocolException) { return [Reply(RoomFailed)]; }
    }

    List<byte[]> AddRoomMember(DungeonRoom room, WorldPlayer member, long now, List<byte[]> replies)
    {
        room.Members.Add(member);
        room.Ready.Remove(member.Key);
        var addW = PacketWriter.Opcode(DungeonRoomAddMemberNt);
        WriteDungeonRoomMember(addW, member, (byte)room.Members.Count, false);
        var added = addW.ToArray();
        BroadcastRoom(room, added, member);
        replies.Add(added);
        replies.Add(DungeonRoomInfoPacket(room, RoomInfoJoin));
        BroadcastRoomInfo(room, RoomInfoJoin, member);
        return replies;
    }

    /// <summary>0x971c leave the expedition waiting room.</summary>
    List<byte[]> DungeonRoomLeave()
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomLeaveRs).U16(result).ToArray();
        if (MyRoom is not { } room) return [Reply(RoomFailed)];
        return LeaveDungeonRoom(room, RoomInfoLeave, [Reply(0)]);
    }

    /// <summary>0x9728 leader removes a member.</summary>
    List<byte[]> DungeonRoomKick(byte[] plain)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomKickMemberRs).U16(result).ToArray();
        if (plain.Length < 10 || MyRoom is not { Leader: var leader } room || leader != worldPlayer)
            return [Reply(RoomFailed)];
        var targetDbId = BitConverter.ToInt64(plain, 2);
        var kicked = room.Members.FirstOrDefault(m => m.Session.DbId == targetDbId && m != leader);
        if (kicked is null) return [Reply(RoomFailed)];
        kicked.Send(PacketWriter.Opcode(DungeonRoomKickoutNt).Bool(false).ToArray());
        return LeaveDungeonRoom(room, RoomInfoKick, [Reply(0)], kicked);
    }

    /// <summary>0x9717 or 0x932b: move everyone in the room into the same dungeon copy.</summary>
    List<byte[]> PartyDungeonEnter(long now) => DungeonRoomStart(now);

    List<byte[]> DungeonRoomStart(long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(DungeonRoomStartRs).U16(result).ToArray();
        byte[] EnterReply(int result) => PacketWriter.Opcode(PartyDungeonEnterRs).U16(result).ToArray();
        if (MyRoom is not { Leader: var leader } room)
        {
            if (pendingPartyDungeonId != 0)
            {
                var solo = EnterDungeon(pendingPartyDungeonId, now);
                pendingPartyDungeonId = 0;
                return solo.Count > 0 ? [EnterReply(0), .. solo] : [EnterReply(RoomFailed)];
            }
            return [EnterReply(RoomFailed), Reply(RoomFailed)];
        }
        if (leader != worldPlayer) return [EnterReply(RoomFailed), Reply(RoomFailed)];
        if (MapEventData.Dungeon(room.ResolvedDungeonId) is not { } dungeon)
            return [EnterReply(RoomFailed), Reply(RoomFailed)];
        foreach (var member in room.Members)
            if (!member.Session.RoomEntryAllowed(dungeon, now, out _))
                return [EnterReply(RoomFailed), Reply(RoomFailed)];
        EnsurePartyFromRoom(room);
        var replies = new List<byte[]> { Reply(0), EnterReply(0) };
        var copyKey = room.RoomKey;
        foreach (var member in room.Members.ToList())
        {
            var moved = member.Session.EnterDungeon(room.ResolvedDungeonId, now, copyKey, consumeTicket: true);
            if (moved.Count == 0) return [EnterReply(RoomFailed), Reply(RoomFailed)];
            member.Send(moved);
            if (member == worldPlayer) replies.AddRange(moved);
        }
        DisbandDungeonRoom(room, RoomInfoDisband);
        return replies;
    }

    void EnsurePartyFromRoom(DungeonRoom room)
    {
        if (Social is not { } social) return;
        if (social.PartyOf(room.Leader) is { } existing && room.Members.All(existing.Members.Contains))
        {
            if (existing.Leader != room.Leader)
            {
                existing.Leader = room.Leader;
                foreach (var member in existing.Members) member.Send(SocialPackets.PartyNewLeader(room.Leader.Session.DbId));
            }
            return;
        }
        foreach (var member in room.Members)
            if (social.PartyOf(member) is { } other) DisbandParty(other, PartyUpdateReason.LeaveMemberBySystem);
        var party = new Party(social.NextId(), room.Leader) { JoinType = room.JoinType };
        party.Members.Clear();
        foreach (var member in room.Members) party.Members.Add(member);
        social.Parties[party.Key] = party;
        var info = SocialPackets.PartyInfo(new PartyInfo
        {
            Key = party.Key, LeaderDbId = party.Leader.Session.DbId, LimitMember = Party.Limit, JoinType = party.JoinType,
            Members = party.Members.Select((m, i) => m.Session.MemberInfo((byte)(i + 1))).ToList(),
        }, PartyUpdateReason.Create);
        foreach (var member in party.Members) member.Send(info);
    }

    List<byte[]> LeaveDungeonRoom(DungeonRoom room, byte reason, List<byte[]> replies, WorldPlayer? who = null)
    {
        var leaving = who ?? worldPlayer;
        if (leaving is null) return replies;
        room.Members.Remove(leaving);
        room.Ready.Remove(leaving.Key);
        room.PendingApply.Remove(leaving.Key);
        leaving.Send(PacketWriter.Opcode(DungeonRoomLeaveNt).I32(room.RoomKey).ToArray());
        if (room.Members.Count == 0)
        {
            DisbandDungeonRoom(room, reason);
            return replies;
        }
        if (room.Leader == leaving)
        {
            room.Leader = room.Members[0];
            BroadcastRoom(room, PacketWriter.Opcode(DungeonRoomNewLeaderNt).I64(room.Leader.Session.DbId).U8(0).ToArray());
        }
        BroadcastRoomInfo(room, reason);
        return replies;
    }

    void DisbandDungeonRoom(DungeonRoom room, byte reason)
    {
        if (Social is not { } social) return;
        var disband = PacketWriter.Opcode(DungeonRoomDisbandNt).U8(reason).ToArray();
        foreach (var member in room.Members.ToList()) member.Send(disband);
        social.DungeonRooms.Remove(room.RoomKey);
    }

    /// <summary>0x972e party leader asks to re-run the expedition (everyone must accept).</summary>
    List<byte[]> DungeonRoomRetry(long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomRetryRs).U16(result).I32(ticketId).ToArray();
        if (Social is not { } social || worldPlayer is null || Social.PartyOf(worldPlayer) is not { Leader: var leader } party || leader != worldPlayer)
            return [Reply(RoomFailed)];
        if (MapEventData.DungeonOfMap(worldMap) is not { Type: "Party" } dungeon)
            return [Reply(RoomFailed)];
        var dungeonId = MapEventData.DungeonIdOfMap(worldMap);
        foreach (var member in party.Members)
            if (!member.Session.RoomEntryAllowed(dungeon, now, out var ticketId, allowInCopy: true))
                return [Reply(RoomFailed, ticketId)];
        social.DungeonRetries.Remove(party.Key);
        var pending = new DungeonRetryPending(leader, dungeonId, checked((int)social.NextId()), now + RoomInviteMs);
        pending.Responses[leader.Key] = RetryAccept;
        social.DungeonRetries[party.Key] = pending;
        var replies = new List<byte[]> { Reply(0) };
        var askW = PacketWriter.Opcode(DungeonRoomAskRetryNt).I64(leader.Session.DbId);
        WriteInvitation(askW, Invitation());
        var ask = askW.I32(pending.CopyKey).I64(pending.ExpiresMs).ToArray();
        foreach (var member in party.Members)
        {
            if (member == leader) continue;
            member.Send(ask);
        }
        if (party.Members.Count == 1) return FinishDungeonRetry(party, pending, now, replies);
        return replies;
    }

    /// <summary>0x9731 answer an in-dungeon retry vote.</summary>
    List<byte[]> DungeonRoomRetryResponse(byte[] plain, long now)
    {
        byte[] Reply(int result, int ticketId = 0) => PacketWriter.Opcode(DungeonRoomRetryResponseRs).U16(result).I32(ticketId).ToArray();
        if (plain.Length < 3 || Social is not { } social || worldPlayer is null || Social.PartyOf(worldPlayer) is not { } party)
            return [Reply(RoomFailed)];
        if (!social.DungeonRetries.TryGetValue(party.Key, out var pending) || pending.ExpiresMs < now)
            return [Reply(RoomFailed)];
        var accept = plain[2] == RetryAccept;
        pending.Responses[playerKey] = accept ? RetryAccept : RetryRefuse;
        var notice = PacketWriter.Opcode(DungeonRoomRetryResponseNt).I64(DbId).I32(accept ? RetryAccept : RetryRefuse).ToArray();
        foreach (var member in party.Members) member.Send(notice);
        if (!accept)
        {
            social.DungeonRetries.Remove(party.Key);
            var fail = PacketWriter.Opcode(DungeonRoomRetryFailNt).U16(RoomFailed).ToArray();
            foreach (var member in party.Members) member.Send(fail);
            return [Reply(0), notice, fail];
        }
        if (party.Members.Any(m => !pending.Responses.TryGetValue(m.Key, out var r) || r != RetryAccept))
            return [Reply(0), notice];
        return FinishDungeonRetry(party, pending, now, [Reply(0), notice]);
    }

    List<byte[]> FinishDungeonRetry(Party party, DungeonRetryPending pending, long now, List<byte[]> replies)
    {
        if (Social is { } social) social.DungeonRetries.Remove(party.Key);
        if (MapEventData.Dungeon(pending.DungeonId) is not { } dungeon) return replies;
        // A retry is a new run: the revives of a Hard Expedition start over (the Oct 28 2026 fix "resurrection count did not reset on retry").
        foreach (var member in party.Members) member.Session.copy?.Revives.Clear();
        foreach (var member in party.Members.ToList())
        {
            if (!member.Session.RoomEntryAllowed(dungeon, now, out _, allowInCopy: true))
                return [PacketWriter.Opcode(DungeonRoomRetryFailNt).U16(RoomFailed).ToArray()];
            var moved = member.Session.EnterDungeon(pending.DungeonId, now, pending.CopyKey, consumeTicket: true);
            if (moved.Count == 0)
                return [PacketWriter.Opcode(DungeonRoomRetryFailNt).U16(RoomFailed).ToArray()];
            member.Send(moved);
            if (member == worldPlayer) replies.AddRange(moved);
        }
        return replies;
    }

    /// <summary>Disconnect / lobby: leave waiting rooms cleanly.</summary>
    void LeaveDungeonRoomSocial(long now)
    {
        if (Social is { } social)
        {
            social.DungeonRoomInvites.Remove(playerKey);
            if (worldPlayer is not null && social.PartyOf(worldPlayer) is { } party) social.DungeonRetries.Remove(party.Key);
        }
        if (MyRoom is not { } room) return;
        var disconnect = PacketWriter.Opcode(DungeonRoomDisconnectMemberNt).I64(DbId).ToArray();
        var hadOthers = room.Members.Count > 1;
        if (room.Leader == worldPlayer)
            foreach (var (key, _) in room.PendingApply.ToList())
                World?.Online.GetValueOrDefault(key)?.Send(PacketWriter.Opcode(DungeonRoomApplyResponseNt).I32(room.RoomKey).U8(ApplyRefuse).ToArray());
        LeaveDungeonRoom(room, RoomInfoLeave, []);
        if (hadOthers && Social?.DungeonRooms.ContainsKey(room.RoomKey) == true)
            BroadcastRoom(room, disconnect);
    }
}
