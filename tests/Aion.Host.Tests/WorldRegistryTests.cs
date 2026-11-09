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

    static readonly NativeOptions Native = new() { WorldId = 1204, GamePort = 13328, LoginPort = 3700, Network = new NetworkOptions { PublicAddress = "10.0.0.5" } };

    [Fact]
    public void Login_list_has_the_primary_and_the_worlds_that_are_up()
    {
        WorldRecord[] worlds =
        [
            World(1204, "Alpha", 1361, 2361, WorldRecord.Primary),
            World(1205, "Beta", 1362, 2362, WorldRecord.Managed, 13329, 18781),
            World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13330, 18782),
            World(1207, "Delta", 1364, 2364, WorldRecord.Managed, 13331, 18783, enabled: false),
            World(1208, "Far", 1365, 2365, WorldRecord.Remote, 13328, host: "10.9.9.9"),
        ];
        WorldRuntime Runtime(int id) => id switch
        {
            1205 => WorldRuntime.None with { State = WorldRuntime.Running },
            1206 => WorldRuntime.None with { State = WorldRuntime.Starting },
            1207 => WorldRuntime.None with { State = WorldRuntime.Running },
            1208 => WorldRuntime.None with { State = WorldRuntime.Online },
            _ => WorldRuntime.None,
        };
        var list = LoginWorldList.Directory(worlds, Native, Runtime);
        Assert.Equal([1204, 1205, 1208], list.Select(w => w.WorldId));
        Assert.Equal(("10.0.0.5", 13328), (list[0].Host, list[0].Port));
        Assert.Equal(("10.0.0.5", 13329), (list[1].Host, list[1].Port));
        Assert.Equal(("10.9.9.9", 13328), (list[2].Host, list[2].Port));
    }

    [Fact]
    public void World_input_is_checked_against_the_other_worlds()
    {
        WorldRecord[] others = [World(1204, "Alpha", 1361, 2361, WorldRecord.Primary), World(1205, "Beta", 1362, 2362, WorldRecord.Managed, 13329, 18781)];
        Assert.Null(WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13330, 18782), others));
        Assert.Equal("name_in_use", WorldInput.Problem(World(1206, "beta", 1363, 2363, WorldRecord.Managed, 13330, 18782), others));
        Assert.Equal("server_id_in_use", WorldInput.Problem(World(1206, "Gamma", 2362, 2363, WorldRecord.Managed, 13330, 18782), others));
        Assert.Equal("port_in_use", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13329, 18782), others));
        Assert.Equal("internal_port_required", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Managed, 13330), others));
        Assert.Equal("remote_needs_host", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Remote, 13330), others));
        Assert.Equal("invalid_host", WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Remote, 13330, host: "example.com"), others));
        // A remote world on another machine may reuse a port of this host.
        Assert.Null(WorldInput.Problem(World(1206, "Gamma", 1363, 2363, WorldRecord.Remote, 13329, host: "10.9.9.9"), others));
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
    }

    [Fact]
    public void Child_command_runs_this_host()
    {
        var (file, arguments) = WorldSupervisor.HostCommand();
        Assert.True(File.Exists(file));
        Assert.All(arguments, a => Assert.EndsWith("Aion.Host.dll", a));
    }
}
