namespace Aion.Application.Authentication;

/// <summary>
/// An account as the realm has it, for a world database's mirror (server/DATABASES.md): who it is, whether it is
/// disabled, its extra character slots and its staff roles.
/// </summary>
public sealed record AccountSnapshot(Guid AccountId, string Provider, string Subject, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt,
    int ExtendedCharacterSlots, IReadOnlyList<string> Roles);

/// <summary>A world's character as the realm's character index keeps it.</summary>
public sealed record IndexedCharacter(Guid CharacterId, Guid AccountId, int WireId, string Name, int Level, int Race,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? LastLogoutAt);

/// <summary>Realm side of the realm link: account snapshots for the worlds, and their characters for the index.</summary>
public interface IRealmAccounts
{
    Task<AccountSnapshot?> SnapshotAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>
    /// The world's characters changed: upserts and removals; with <paramref name="full"/> the upserts are all of them and
    /// every other indexed character of that world is removed.
    /// </summary>
    Task SyncCharactersAsync(int worldId, bool full, IReadOnlyList<IndexedCharacter> upserts, IReadOnlyList<Guid> removed, CancellationToken ct = default);
    /// <summary>The account's characters on one world with their wire ids (the realm's chat names them by it).</summary>
    Task<IReadOnlyList<(Guid CharacterId, int WireId)>> WireIdsAsync(Guid accountId, int worldId, CancellationToken ct = default);
}

/// <summary>World side: the world database's copy of the accounts that play here, and its characters for the index.</summary>
public interface IAccountMirror
{
    Task UpsertAsync(AccountSnapshot account, CancellationToken ct = default);
    Task<IReadOnlyList<IndexedCharacter>> CharactersAsync(CancellationToken ct = default);
}
