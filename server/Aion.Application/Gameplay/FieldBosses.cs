namespace Aion.Application.Gameplay;

/// <summary>
/// One field boss's stored row (world table game.field_bosses, migration 104): its last kill and announced respawn, and the
/// staff's respawn range in minutes (null: the default). A row whose respawn has passed means the boss is up.
/// </summary>
public sealed record FieldBossRecord(int Code, DateTimeOffset? KilledAt, DateTimeOffset? RespawnAt, int? MinMinutes, int? MaxMinutes);

/// <summary>The world's field boss timers (docs/FIELD_BOSSES.md). Writes are upserts by code.</summary>
public interface IFieldBossStore
{
    Task<IReadOnlyList<FieldBossRecord>> LoadAsync(CancellationToken ct = default);

    /// <summary>The last kill and the respawn (both null: forget them).</summary>
    Task SaveStateAsync(int code, DateTimeOffset? killedAt, DateTimeOffset? respawnAt, CancellationToken ct = default);

    /// <summary>The respawn range in minutes (both null: back to the default).</summary>
    Task SaveTimerAsync(int code, int? minMinutes, int? maxMinutes, CancellationToken ct = default);
}
