using System.Text.Json;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Protocol.Native;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>Where a character is: the map (and its name), the area within it and the position (cm).</summary>
public sealed record CharacterLocation(int MapId, string MapName, string? Area, double? X, double? Y, double? Z);

/// <summary>The character's legion: name, level, member count and whether the character leads it (grade 0).</summary>
public sealed record CharacterLegion(long Id, string Name, int Level, int Members, bool Leader);

/// <summary>
/// The account site's character page (GET /api/characters/{id}/details), as the host that keeps the character's data
/// sees it: the stored row, and while the character is in the world on that host its live level, class, combat power and
/// place. Live is false for a stored character that is not in the world there (its location is the last saved one).
/// </summary>
public sealed record CharacterDetails(Guid CharacterId, string Name, int WorldId, int Level, int Race, int Gender,
    string? ClassName, int Ascension, long Experience, bool Live, DateTimeOffset? InWorldSince, long? CombatPower,
    long? HighestCombatPower, CharacterLocation? Location, CharacterLegion? Legion, DateTimeOffset? CreatedAt,
    DateTimeOffset? LastLoginAt, DateTimeOffset? LastLogoutAt);

public static class CharacterDetailsReader
{
    /// <summary>
    /// The details of an owned character from this host's own character store (a world server's database, or a host
    /// that runs its world in-process). Null when the account does not own it (the store checks ownership in its query),
    /// or when this host keeps no characters.
    /// </summary>
    public static async Task<CharacterDetails?> LocalAsync(IServiceProvider services, VerifiedAccount account, Guid characterId, CancellationToken ct)
    {
        if (characterId == Guid.Empty || services.GetService<ICharacterStateRepository>() is not { } repository) return null;
        var state = await repository.LoadOwnedAsync(account.AccountId, characterId, ct);
        if (state is null || state.Summary.AccountId != account.AccountId) return null;
        var data = services.GetService<GameData>() ?? GameData.Embedded;
        var summary = state.Summary;

        // The live session, read as the staff overview reads it (outside the session's loop: a snapshot, not a lock).
        var entry = services.GetService<WorldPresence>()?.FindCharacter(characterId);
        var session = entry is { Session: { InWorld: true } s } && (entry.AccountId is null || entry.AccountId == account.AccountId) ? s : null;

        CharacterLocation? location = null;
        if (session is { MapId: { } liveMap })
            location = Located(liveMap, session.Position, data);
        else if (state.Progress.MapId is { } savedMap and > 0)
            location = Located(savedMap, SavedPosition(state.Progress.Position), data);

        CharacterLegion? legion = null;
        if (services.GetService<IGameplayStore>() is { } game)
        {
            try
            {
                if (await game.GuildOfAsync(account, characterId, ct) is { } guild)
                    legion = new CharacterLegion(guild.Id, guild.Name, guild.Level, guild.Members.Count,
                        guild.Members.Any(m => m.CharacterId == characterId && m.Grade == 0));
            }
            catch (Exception error) when (error is GameplayException or System.Data.Common.DbException) { }
        }

        return new CharacterDetails(summary.CharacterId, summary.Name, summary.WorldId,
            session?.Level ?? summary.Level, state.Race, state.Gender,
            session?.ClassName ?? data.Profile(state.CreationId, state.Gender)?.Class,
            state.Ascension, state.Progress.Experience, session is not null, entry?.Since,
            session?.CombatPower, session?.HighestCombatPower, location, legion,
            Time(state.RegisteredUnixMs), Time(state.LastLoginUnixMs), Time(state.LastLogoutUnixMs));
    }

    static CharacterLocation Located(int mapId, Vector3d? at, GameData data) => new(mapId, MapNames.Of(mapId, data),
        at is { } p ? MapNames.AreaAt(mapId, p) : null, at?.X is { } x ? Math.Round(x) : null, at?.Y is { } y ? Math.Round(y) : null,
        at?.Z is { } z ? Math.Round(z) : null);

    static Vector3d? SavedPosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Object) return null;
        if (!position.TryGetProperty("x", out var x) || !position.TryGetProperty("y", out var y) || !position.TryGetProperty("z", out var z)) return null;
        if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number || z.ValueKind != JsonValueKind.Number) return null;
        var at = new Vector3d(x.GetDouble(), y.GetDouble(), z.GetDouble());
        return at.IsFinite ? at : null;
    }

    static DateTimeOffset? Time(long unixMs) => unixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs) : null;
}
