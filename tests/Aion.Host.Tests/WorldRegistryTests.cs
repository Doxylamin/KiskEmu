using Aion.Application.Gameplay;
using Aion.Host.Native;
using Aion.Host.Worlds;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aion.Host.Tests;

public sealed class WorldRegistryTests
{
    static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    static WorldRecord World(int id, string name, int light, int dark, string kind, int? port = null, int? internalPort = null, bool enabled = true, string? host = null) =>
        new(id, name, light, dark, kind, host, port, internalPort, enabled, null, At, At);

    static readonly NativeOptions Native = new() { WorldId = 1204, LoginPort = 3700, Network = new NetworkOptions { PublicAddress = "10.0.0.5" } };

    [Fact]
    public void Login_list_has_the_world_servers_that_are_up()
    {
        WorldRecord[] worlds =
        [
            World(1204, "Alpha", 1361, 2361, WorldRecord.Managed, 13328, 18780),
            World(1205, "Beta", 1362, 2362, WorldRecord.Managed, 13329, 18781),
            World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13330, 18782),
            World(1207, "Delta", 1364, 2364, WorldRecord.Managed, 13331, 18783, enabled: false),
            World(1208, "Far", 1365, 2365, WorldRecord.Remote, 13328, host: "10.9.9.9"),
        ];
        WorldRuntime Runtime(int id) => id switch
        {
            1204 => WorldRuntime.None with { State = WorldRuntime.Running },
            1205 => WorldRuntime.None with { State = WorldRuntime.Running },
            1206 => WorldRuntime.None with { State = WorldRuntime.Starting },
            1207 => WorldRuntime.None with { State = WorldRuntime.Running },
            1208 => WorldRuntime.None with { State = WorldRuntime.Online },
            _ => WorldRuntime.None,
        };
        var list = LoginWorldList.Directory(worlds, Native, Runtime);
        Assert.Equal([1204, 1205, 1208], list.Select(w => w.WorldId));
        Assert.Equal(("10.0.0.5", 13328), (list[0].Host, list[0].Port));
        Assert.Empty(LoginWorldList.Directory(worlds, Native, _ => WorldRuntime.None));
        Assert.Equal(("10.0.0.5", 13329), (list[1].Host, list[1].Port));
        Assert.Equal(("10.9.9.9", 13328), (list[2].Host, list[2].Port));
    }

    [Fact]
    public void World_input_is_checked_against_the_other_worlds()
    {
        WorldRecord[] others = [World(1204, "Alpha", 1361, 2361, WorldRecord.Managed, 13328, 18780), World(1205, "Beta", 1362, 2362, WorldRecord.Managed, 13329, 18781)];
        Assert.Null(WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13330, 18782), others));
        Assert.Equal("name_in_use", WorldInput.Problem(World(1206, "beta", 1363, 2363, WorldRecord.Managed, 13330, 18782), others));
        Assert.Equal("server_id_in_use", WorldInput.Problem(World(1206, "Gamma", 2362, 2363, WorldRecord.Managed, 13330, 18782), others));
        Assert.Equal("port_in_use", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13329, 18782), others));
        Assert.Equal("internal_port_required", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13330), others));
        // A remote world reports its address and ports when it signs in to the realm link.
        Assert.Null(WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Remote), others));
        Assert.Equal("invalid_host", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Remote, 13330, host: "example.com"), others));
        Assert.Equal("invalid_host", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Remote, 13330, host: "10.9.9.9") with { ControlHost = "far" }, others));
        // A remote world on another machine may reuse a port of this host.
        Assert.Null(WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Remote, 13329, host: "10.9.9.9"), others));
        Assert.Equal("game_port_required", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, null, 18782), others));
        Assert.Equal("invalid_kind", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, "primary", 13330, 18782), others));
        Assert.Equal("invalid_name", WorldInput.Problem(World(1206, new string('x', 33), 1363, 2363, WorldRecord.Managed, 13330, 18782), others));
    }

    [Fact]
    public void World_role_comes_from_configuration()
    {
        var plain = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        Assert.Null(WorldProcessOptions.From(plain));
        var world = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aion:Role"] = "world", ["Aion:WorldProcess:ControlToken"] = "abc", ["Aion:WorldProcess:Name"] = "Beta",
        }).Build();
        var options = WorldProcessOptions.From(world);
        Assert.NotNull(options);
        Assert.Equal(("abc", "Beta"), (options.ControlToken, options.Name));
        // A remote world server derives its control token from the shared bridge secret, as the auth host does.
        var secret = new string('s', 64);
        var remote = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aion:Role"] = "world", ["Aion:Native:WorldId"] = "1207", ["Aion:BridgeSecret"] = secret, ["Aion:WorldProcess:ListenAddress"] = "0.0.0.0",
        }).Build();
        var derived = WorldProcessOptions.From(remote)!;
        var auth = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Aion:BridgeSecret"] = secret }).Build();
        Assert.Equal(WorldProcessOptions.Token(WorldProcessOptions.ControlKey(auth)!, 1207), derived.ControlToken);
        Assert.NotEqual(WorldProcessOptions.Token(WorldProcessOptions.ControlKey(auth)!, 1208), derived.ControlToken);
        Assert.Equal("0.0.0.0", derived.ListenAddress);
        // Without a shared secret a world server makes its own control token (reported when it signs in).
        var alone = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Aion:Role"] = "world", ["Aion:Native:WorldId"] = "1300" }).Build();
        Assert.Equal(64, WorldProcessOptions.From(alone)!.ControlToken.Length);
        Assert.NotEqual(WorldProcessOptions.From(alone)!.ControlToken, WorldProcessOptions.From(alone)!.ControlToken);
    }

    [Fact]
    public void Managed_link_tokens_are_per_world()
    {
        var key = WorldProcessOptions.ControlKey(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Aion:BridgeSecret"] = new string('s', 64) }).Build())!;
        Assert.NotEqual(WorldProcessOptions.LinkToken(key, 1204), WorldProcessOptions.LinkToken(key, 1205));
        // The realm-link token is not the control token of the same world.
        Assert.NotEqual(WorldProcessOptions.Token(key, 1204), WorldProcessOptions.LinkToken(key, 1204));
    }

    [Fact]
    public void Child_command_runs_this_host()
    {
        var (file, arguments) = WorldSupervisor.HostCommand();
        Assert.True(File.Exists(file));
        Assert.All(arguments, a => Assert.EndsWith("Aion.Host.dll", a));
    }

    [Fact]
    public async Task Account_quna_is_summed_over_the_worlds_and_a_silent_world_counts_as_zero()
    {
        WorldRecord[] worlds =
        [
            World(1204, "Alpha", 1361, 2361, WorldRecord.Managed, 13328, 18780),
            World(1205, "Beta", 1362, 2362, WorldRecord.Managed, 13329, 18781),
            World(1208, "Far", 1365, 2365, WorldRecord.Remote, 13328, 18790, host: "10.9.9.9"),
        ];
        var answers = new Dictionary<int, long?> { [1204] = 1_500, [1205] = null, [1208] = 250 };
        var missing = new List<int>();
        var sum = await WorldLinks.SumOverAsync(worlds, w => Task.FromResult(answers[w.WorldId]), w => missing.Add(w.WorldId));
        Assert.Equal(1_750, sum);
        Assert.Equal([1205], missing);
        Assert.Equal(0, await WorldLinks.SumOverAsync([], _ => Task.FromResult<long?>(1), _ => { }));
    }

    [Fact]
    public void Shop_quna_counts_only_the_world_the_account_plays_on()
    {
        WorldRecord[] worlds =
        [
            World(1204, "Alpha", 1361, 2361, WorldRecord.Managed, 13328, 18780),
            World(1205, "Beta", 1362, 2362, WorldRecord.Managed, 13329, 18781),
        ];
        Assert.Equal([1205], WorldLinks.QunaWorlds(worlds, 1205).Select(w => w.WorldId));
        // Not in a world, or in one that is not running: every running world.
        Assert.Equal([1204, 1205], WorldLinks.QunaWorlds(worlds, null).Select(w => w.WorldId));
        Assert.Equal([1204, 1205], WorldLinks.QunaWorlds(worlds, 1299).Select(w => w.WorldId));
    }
}
