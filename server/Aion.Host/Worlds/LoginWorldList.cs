using Aion.Application.Gameplay;
using Aion.Host.Native;
using Aion.World;

namespace Aion.Host.Worlds;

/// <summary>
/// The world servers the auth host's login port lists: enabled managed worlds whose process answers and enabled remote
/// worlds that answer. The registry is read at most every few seconds.
/// </summary>
public sealed class LoginWorldList(IWorldRegistry registry, NativeOptions native, WorldSupervisor? supervisor, ILogger<LoginWorldList> logger)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);
    readonly SemaphoreSlim gate = new(1, 1);
    IReadOnlyList<WorldRecord> cached = [];
    DateTimeOffset readAt = DateTimeOffset.MinValue;

    /// <summary>The registry's worlds (cached); the last good list when the database is unavailable.</summary>
    public async Task<IReadOnlyList<WorldRecord>> WorldsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - readAt < CacheFor) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - readAt < CacheFor) return cached;
            cached = await registry.ListAsync(ct);
            readAt = DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning("World registry unavailable, using the last list: {Error}", error.Message);
        }
        finally { gate.Release(); }
        return cached;
    }

    /// <summary>The login directory now (empty while no world server is up).</summary>
    public async Task<IReadOnlyList<LoginWorld>> DirectoryAsync(CancellationToken ct) =>
        Directory(await WorldsAsync(ct), native, supervisor is null ? _ => WorldRuntime.None : supervisor.Runtime);

    /// <summary>The listed worlds, by world id.</summary>
    public static IReadOnlyList<LoginWorld> Directory(IReadOnlyList<WorldRecord> worlds, NativeOptions native, Func<int, WorldRuntime> runtime)
    {
        var list = new List<LoginWorld>();
        foreach (var world in worlds)
        {
            if (!world.Enabled || world.GamePort is not { } gamePort || !runtime(world.WorldId).Listed) continue;
            list.Add(new LoginWorld(world.WorldId, world.Name, world.LightServerId, world.DarkServerId, world.Host ?? native.Network.PublicAddress, gamePort));
        }
        return list;
    }
}
