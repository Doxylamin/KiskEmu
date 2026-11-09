using System.Text.RegularExpressions;
using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

/// <summary>
/// A registered world (migration 090): a world_id (characters, tickets and world sessions are scoped by it) with one
/// login-list row per race. Kind: "primary" (the login host's own world), "managed" (a child process the primary host
/// starts and stops) or "remote" (a world-role host elsewhere on the same database, listed only).
/// </summary>
public sealed record WorldRecord(int WorldId, string Name, int LightServerId, int DarkServerId, string Kind, string? Host,
    int? GamePort, int? InternalPort, bool Enabled, int? LevelCap, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public const string Primary = "primary", Managed = "managed", Remote = "remote";
    public bool IsPrimary => Kind == Primary;
    public bool IsManaged => Kind == Managed;
}

/// <summary>Staff input for a new or changed world (null fields of an update keep their value).</summary>
public sealed record WorldInput(int? WorldId, string? Name, int? LightServerId, int? DarkServerId, string? Kind, string? Host,
    int? GamePort, int? InternalPort, int? LevelCap)
{
    static readonly Regex Ipv4 = new(@"^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$", RegexOptions.CultureInvariant);

    /// <summary>The checks the store applies before writing (error codes for the dashboard); null when valid.</summary>
    public static string? Problem(WorldRecord world, IReadOnlyCollection<WorldRecord> others)
    {
        if (world.WorldId is < 1 or > 65535) return "invalid_world_id";
        if (string.IsNullOrWhiteSpace(world.Name) || System.Text.Encoding.UTF8.GetByteCount(world.Name) > 32 || world.Name.Trim() != world.Name)
            return "invalid_name";
        if (world.LightServerId is < 1 or > 65535 || world.DarkServerId is < 1 or > 65535 || world.LightServerId == world.DarkServerId)
            return "invalid_server_ids";
        if (world.Kind is not (WorldRecord.Managed or WorldRecord.Remote or WorldRecord.Primary)) return "invalid_kind";
        if (world.Host is { } host && !Ipv4.IsMatch(host)) return "invalid_host";
        if (world.Kind == WorldRecord.Remote && world.Host is null) return "remote_needs_host";
        if (world.Kind != WorldRecord.Primary && world.GamePort is null) return "game_port_required";
        if (world.GamePort is < 1 or > 65535 || world.InternalPort is < 1 or > 65535) return "invalid_port";
        if (world.Kind == WorldRecord.Managed && world.InternalPort is null) return "internal_port_required";
        if (world.GamePort is not null && world.GamePort == world.InternalPort) return "port_in_use";
        if (world.LevelCap is < 1 or > 100) return "invalid_level_cap";
        foreach (var other in others.Where(o => o.WorldId != world.WorldId))
        {
            if (other.Name.Equals(world.Name, StringComparison.OrdinalIgnoreCase)) return "name_in_use";
            int[] rows = [other.LightServerId, other.DarkServerId];
            if (rows.Contains(world.LightServerId) || rows.Contains(world.DarkServerId)) return "server_id_in_use";
            // Ports clash only on the same machine (null host: the primary host's address).
            if (other.Host == world.Host || other.Kind != WorldRecord.Remote && world.Kind != WorldRecord.Remote)
            {
                int?[] ports = [other.GamePort, other.InternalPort];
                if (world.GamePort is { } gp && ports.Contains(gp) || world.InternalPort is { } ip && ports.Contains(ip)) return "port_in_use";
            }
        }
        return null;
    }
}

public interface IWorldRegistry
{
    /// <summary>Every registered world (host use: login list, supervisor).</summary>
    Task<IReadOnlyList<WorldRecord>> ListAsync(CancellationToken ct = default);
    /// <summary>Adds the primary row for this host's world when the registry has none (an existing primary row is kept).</summary>
    Task<WorldRecord> EnsurePrimaryAsync(int worldId, CancellationToken ct = default);

    // Staff (admin role), audited. GameplayException codes: forbidden, unknown_world, a WorldInput.Problem code,
    // primary_world (the primary cannot be removed or re-kinded), world_enabled (stop it first), world_has_characters.
    Task<WorldRecord> CreateAsync(VerifiedAccount actor, WorldInput input, CancellationToken ct = default);
    Task<WorldRecord> UpdateAsync(VerifiedAccount actor, int worldId, WorldInput input, CancellationToken ct = default);
    Task<WorldRecord> SetEnabledAsync(VerifiedAccount actor, int worldId, bool enabled, CancellationToken ct = default);
    Task DeleteAsync(VerifiedAccount actor, int worldId, CancellationToken ct = default);
    /// <summary>Characters per world (dashboard), staff only.</summary>
    Task<IReadOnlyDictionary<int, int>> CharacterCountsAsync(VerifiedAccount actor, CancellationToken ct = default);
}
