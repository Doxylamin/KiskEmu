using Aion.Protocol.Native;
using Xunit;
using static Aion.World.Tests.ExpeditionRoomMatchmakingTests;

namespace Aion.World.Tests;

/// <summary>Room request handling taken from the community fork (zaraza87/A2Core, #72): result codes, roles, ready-to-start, refresh.</summary>
public sealed class DungeonRoomRequestTests
{
    const long Now = 1_760_500_000_000;

    static ushort Result(List<byte[]> replies, ushort opcode) =>
        BitConverter.ToUInt16(replies.First(b => GameSessionTestHelpers.Op(b) == opcode), 2);

    static byte[] Create(string desc = "", bool isPublic = true) =>
        new PacketWriter().U16(GameSession.DungeonRoomCreateRq).I32(600002).U8(0).U8(0).String(desc).Bool(isPublic).U8(PartyOptions.JoinFree)
            .Varint(0).Bool(false).Varint(0).ToArray();

    static byte[] Join(int key, byte role) => new PacketWriter().U16(GameSession.DungeonRoomJoinRq).I32(key).U8(role).ToArray();

    static async Task<(SharedWorld World, GameSession Lead, GameSession Mate, FakeCharacterStore MateStore)> Pair(uint first)
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var mateStore = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(leadStore);
        FakeCharacterStore.SeedExpeditionItemLevel(mateStore);
        var lead = await EnterExpeditionHero(world, leadStore, "Lead" + (char)('a' + first), first);
        var mate = await EnterExpeditionHero(world, mateStore, "Mate" + (char)('a' + first), first + 1);
        await GameSessionTestHelpers.LoadInventoryAsync(lead, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(mate, Now);
        return (world, lead, mate, mateStore);
    }

    [Fact]
    public void Instant_join_reads_single_byte_wanted_roles()
    {
        // The fork's client trace: dungeon 600063, one wanted role (2), level 1, race 3.
        var parsed = GameSession.ParseInstantJoin(Convert.FromHexString("1297" + "FF270900" + "01" + "02" + "01" + "03"));
        Assert.Equal((600063, (byte)1, (byte)3, (byte)2), parsed);
        // No wanted roles: All (4).
        Assert.Equal((byte)4, GameSession.ParseInstantJoin(Convert.FromHexString("1297" + "C2270900" + "00" + "00" + "00"))!.Value.Role);
    }

    [Fact]
    public async Task Create_reads_the_3533_body_with_the_level_first()
    {
        // OBSERVED 2026-11-14 (game version 3533, our test server's client trace): level 0, dungeon 600002, race 3,
        // "Leaving now.", public, join type 1, four wanted roles, no invite. Read dungeon-first, it named no dungeon.
        var (world, lead, _, _) = await Pair(7);
        var body = Convert.FromHexString("0397" + "00" + "C2270900" + "03" + "0C4C656176696E67206E6F772E" + "01" + "01" + "0402040304040405" + "04" + "00");

        var replies = await lead.HandleAsync(body, Now + 1);

        Assert.Equal(0, Result(replies, GameSession.DungeonRoomCreateRs));
        var room = Assert.Single(world.Social.DungeonRooms.Values);
        Assert.Equal((600002, (byte)0, (byte)3, "Leaving now.", true), (room.ContentDungeonId, room.DungeonLevel, room.Race, room.Desc, room.Public));
    }

    [Fact]
    public async Task Refusals_carry_the_clients_room_codes()
    {
        var (world, lead, mate, _) = await Pair(3);
        Assert.Equal(GameSession.RoomInvalidDescLength, Result(await lead.HandleAsync(Create(new string('d', 41)), Now + 1), GameSession.DungeonRoomCreateRs));
        Assert.Empty(world.Social.DungeonRooms);

        await lead.HandleAsync(Create("closed", isPublic: false), Now + 2);
        var key = world.Social.DungeonRooms.Keys.Single();
        Assert.Equal(GameSession.RoomAlreadyInRoom, Result(await lead.HandleAsync(Create(), Now + 3), GameSession.DungeonRoomCreateRs));
        Assert.Equal(GameSession.RoomNotFound, Result(await mate.HandleAsync(Join(key + 1, 2), Now + 4), GameSession.DungeonRoomJoinRs));
        Assert.Equal(GameSession.RoomPrivate, Result(await mate.HandleAsync(Join(key, 2), Now + 5), GameSession.DungeonRoomJoinRs));
        Assert.Null(world.Social.DungeonRoomOf(mate.WorldPlayer!));
        Assert.Equal(GameSession.RoomNotMember, Result(await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomLeaveRq).ToArray(), Now + 6),
            GameSession.DungeonRoomLeaveRs));
    }

    [Fact]
    public async Task Joined_member_record_carries_role_and_combat_power()
    {
        var (world, lead, mate, _) = await Pair(5);
        await lead.HandleAsync(Create(), Now + 1);
        var key = world.Social.DungeonRooms.Keys.Single();

        var joined = await mate.HandleAsync(Join(key, 2), Now + 2);
        Assert.Equal(0, Result(joined, GameSession.DungeonRoomJoinRs));
        Assert.Equal((byte)2, world.Social.DungeonRooms[key].Roles[mate.WorldPlayer!.Key]);
        var added = joined.First(b => GameSessionTestHelpers.Op(b) == GameSession.DungeonRoomAddMemberNt);
        // role u8 then combat power i64, then the empty tickets map.
        var tail = new byte[] { 2 }.Concat(BitConverter.GetBytes(mate.CombatPower)).Append((byte)0).ToArray();
        Assert.True(mate.CombatPower > 0);
        Assert.True(added.AsSpan().IndexOf(tail) > 0);

        // A member who leaves takes the role along.
        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomLeaveRq).ToArray(), Now + 3);
        Assert.False(world.Social.DungeonRooms[key].Roles.ContainsKey(mate.WorldPlayer!.Key));
    }

    [Fact]
    public async Task Start_waits_for_every_member_ready_and_only_the_leader_starts()
    {
        var (world, lead, mate, mateStore) = await Pair(7);
        await lead.HandleAsync(Create(), Now + 1);
        var key = world.Social.DungeonRooms.Keys.Single();
        await mate.HandleAsync(Join(key, 1), Now + 2);
        var start = new PacketWriter().U16(GameSession.DungeonRoomStartRq).ToArray();

        Assert.Equal(GameSession.RoomNotLeader, Result(await mate.HandleAsync(start, Now + 3), GameSession.DungeonRoomStartRs));
        Assert.Equal(GameSession.RoomNotLeader, Result(await mate.HandleAsync(
            new PacketWriter().U16(GameSession.DungeonRoomKickMemberRq).I64(mateStore.States[^1].WireId).ToArray(), Now + 3), GameSession.DungeonRoomKickMemberRs));
        Assert.Equal(GameSession.RoomNotReadyAll, Result(await lead.HandleAsync(start, Now + 4), GameSession.DungeonRoomStartRs));
        Assert.Equal(0, lead.InstanceKey);
        Assert.True(world.Social.DungeonRooms.ContainsKey(key));

        await mate.HandleAsync(new PacketWriter().U16(GameSession.DungeonRoomReadyRq).U8(1).ToArray(), Now + 5);
        var started = await lead.HandleAsync(start, Now + 50);
        Assert.Equal(0, Result(started, GameSession.DungeonRoomStartRs));
        Assert.NotEqual(0, lead.InstanceKey);
        Assert.Equal(lead.InstanceKey, mate.InstanceKey);
    }

    [Fact]
    public async Task Refresh_answers_and_resends_the_room()
    {
        var (world, lead, mate, _) = await Pair(9);
        var refresh = new PacketWriter().U16(GameSession.DungeonRoomRefreshRq).ToArray();
        var alone = await mate.HandleAsync(refresh, Now + 1);
        Assert.Equal(0, Result(alone, GameSession.DungeonRoomRefreshRs));
        Assert.False(GameSessionTestHelpers.HasOpcode(alone, GameSession.DungeonRoomInfoNt));

        await lead.HandleAsync(Create(), Now + 2);
        var inRoom = await lead.HandleAsync(refresh, Now + 3);
        Assert.Equal(0, Result(inRoom, GameSession.DungeonRoomRefreshRs));
        Assert.True(GameSessionTestHelpers.HasOpcode(inRoom, GameSession.DungeonRoomInfoNt));
        Assert.Single(world.Social.DungeonRooms);
    }
}
