using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Multi-world on the login port: rows per world, characters scoped to their world, routing before the redirect.</summary>
public sealed class MultiWorldLoginTests
{
    static readonly Guid Grant = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    static readonly LoginWorld[] Worlds =
    [
        new(1204, "Alpha", 1361, 2361, "10.0.0.5", 13328),
        new(1205, "Beta", 1362, 2362, "10.0.0.6", 13400),
    ];

    static byte[] Login() => new PacketWriter().U16(0x3905).String("").String("good").String("").String("").String("").String("").ToArray();
    static byte[] Pick(ushort opcode, int row) => [(byte)opcode, (byte)(opcode >> 8), (byte)row, (byte)(row >> 8)];

    static LoginSession Session(List<(int World, int Race)> routed, params (string Name, int Level, int Race, ulong Last, int World)[] characters) =>
        new(new LoginSessionOptions
        {
            Directory = Worlds,
            Route = (_, grant, world, race, _) => { Assert.Equal(Grant, grant); routed.Add((world, race)); return Task.FromResult(true); },
        }, (_, _) => Task.FromResult<LoginGrant?>(new LoginGrant("42", Grant, characters)));

    [Fact]
    public async Task Each_world_is_listed_with_a_light_and_a_dark_row()
    {
        var session = Session([]);
        await session.HandleAsync(Login(), 0);
        var directory = (await session.HandleAsync([0x08, 0x39], 0)).Replies.Single();
        Assert.Equal(LoginPackets.WorldDirectory(LoginWorld.Rows(Worlds)), directory);
        Assert.Equal([1361, 2361, 1362, 2362], LoginWorld.Rows(Worlds).Select(r => r.Id));
        Assert.Equal([1, 2, 1, 2], LoginWorld.Rows(Worlds).Select(r => r.Group));
    }

    [Fact]
    public async Task Characters_stay_on_their_own_world()
    {
        var session = Session([], ("A", 10, 1, 1, 1204), ("B", 11, 2, 2, 1205), ("Unlisted", 12, 1, 3, 1300));
        await session.HandleAsync(Login(), 0);
        var list = (await session.HandleAsync([0x0a, 0x39], 0)).Replies.Single();
        Assert.Equal(LoginPackets.Characters([new LoginCharacterRow(1361, "A", 10, LastPlayedUnixMs: 1), new LoginCharacterRow(2362, "B", 11, LastPlayedUnixMs: 2)]), list);
    }

    [Fact]
    public async Task Picking_a_row_routes_the_grant_and_redirects_to_that_worlds_game_port()
    {
        var routed = new List<(int World, int Race)>();
        var session = Session(routed);
        await session.HandleAsync(Login(), 0);
        Assert.Equal(LoginPackets.Redirect(1362, "10.0.0.6", 13400), (await session.HandleAsync(Pick(0x390e, 1362), 0)).Replies.Single());
        Assert.Equal(LoginPackets.Redirect(2361, "10.0.0.5", 13328), (await session.HandleAsync(Pick(0x390e, 2361), 0)).Replies.Single());
        Assert.Equal([(1205, 1), (1204, 2)], routed);
        Assert.Equal("unknown_world", (await session.HandleAsync(Pick(0x390e, 1999), 0)).Close);
    }

    [Fact]
    public async Task Without_a_directory_the_single_world_defaults_are_unchanged()
    {
        var session = new LoginSession(new LoginSessionOptions { RedirectHost = "10.1.1.1", RedirectPort = 13328 });
        await session.HandleAsync([0x05, 0x39], 0);
        Assert.Equal(LoginPackets.WorldDirectory(LoginPackets.DefaultWorlds), (await session.HandleAsync([0x08, 0x39], 0)).Replies.Single());
        Assert.Equal(LoginPackets.Redirect(2361, "10.1.1.1", 13328), (await session.HandleAsync(Pick(0x390e, 2361), 0)).Replies.Single());
    }
}
