using Aion.Application.Gameplay;
using Aion.Host.Native;
using Aion.World;

namespace Aion.Host.Worlds;

/// <summary>
/// The worlds the login port lists (multi-world): the primary world (this host's game port) always, managed worlds whose
/// child process answers, enabled remote worlds whose game port answers. The registry is read at most every few seconds.
/// </summary>
public sealed class LoginWorldList(IWorldRegistry registry, NativeOptions native, WorldSupervisor? supervisor, ILogger<LoginWorldList> logger)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);
    readonly SemaphoreSlim gate = new(1, 1);
    IReadOnlyList<WorldRecord> cached = [];
    DateTimeOffset readAt = DateTimeOffset.MinValue;
    bool primaryEnsured;

    /// <summary>The registry's worlds (cached); the last good list when the database is unavailable.</summary>
    public async Task<IReadOnlyList<WorldRecord>> WorldsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - readAt < CacheFor) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - readAt < CacheFor) return cached;
            if (!primaryEnsured)
            {
                await registry.EnsurePrimaryAsync(native.WorldId, ct);
                primaryEnsured = true;
            }
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

    /// <summary>The login directory now, or null to keep the single-world defaults (no registry rows yet).</summary>
    public async Task<IReadOnlyList<LoginWorld>?> DirectoryAsync(CancellationToken ct)
    {
        var worlds = await WorldsAsync(ct);
        if (worlds.Count == 0) return null;
        return Directory(worlds, native, supervisor is null ? _ => WorldRuntime.None : supervisor.Runtime);
    }

    /// <summary>The listed worlds, primary first (its world id is this host's, whatever the primary row says).</summary>
    public static IReadOnlyList<LoginWorld> Directory(IReadOnlyList<WorldRecord> worlds, NativeOptions native, Func<int, WorldRuntime> runtime)
    {
        var list = new List<LoginWorld>();
        foreach (var world in worlds)
        {
            if (world.IsPrimary)
            {
                if (native.GamePort is { } port)
                    list.Insert(0, new LoginWorld(native.WorldId, world.Name, world.LightServerId, world.DarkServerId, world.Host ?? native.Network.PublicAddress, port));
                continue;
            }
            if (!world.Enabled || world.GamePort is not { } gamePort || !runtime(world.WorldId).Listed) continue;
            list.Add(new LoginWorld(world.WorldId, world.Name, world.LightServerId, world.DarkServerId, world.Host ?? native.Network.PublicAddress, gamePort));
        }
        return list;
    }
}
