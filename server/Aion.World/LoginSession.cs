using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// An accepted 0x3905 login: the wire account, its grant GUID and its characters (race 1 Light, 2 Dark; WorldId 0: the
/// first listed row of its race).
/// </summary>
public sealed record LoginGrant(string WireAccountId, Guid Grant, IReadOnlyList<(string Name, int Level, int Race, ulong LastPlayedUnixMs, int WorldId)> Characters)
{
    public override string ToString() => "LoginGrant [grant redacted]";
}

/// <summary>
/// A world on the login port (multi-world): its world id, its two login-list rows (one per race) and the game port the
/// client is redirected to after picking either row.
/// </summary>
public sealed record LoginWorld(int WorldId, string Name, int LightServerId, int DarkServerId, string Host, int Port, byte Load = 50)
{
    /// <summary>The 0x3909 rows of these worlds: each world's Light row, then its Dark row (status 1, online).</summary>
    public static IReadOnlyList<WorldRow> Rows(IEnumerable<LoginWorld> worlds) =>
        [.. worlds.SelectMany(w => new[] { new WorldRow(w.LightServerId, 1, w.Name, 1, w.Load), new WorldRow(w.DarkServerId, 2, w.Name, 1, w.Load) })];
}

public sealed record LoginSessionOptions
{
    readonly IReadOnlyList<WorldRow> worlds = LoginPackets.DefaultWorlds;

    /// <summary>
    /// The listed worlds with their game ports (multi-world). When set, the 0x3909 rows come from them and 0x390e
    /// redirects to the picked row's world; otherwise the <see cref="Worlds"/> rows all redirect to RedirectHost:RedirectPort.
    /// </summary>
    public IReadOnlyList<LoginWorld>? Directory { get; init; }
    public IReadOnlyList<WorldRow> Worlds { get => Directory is { Count: > 0 } d ? LoginWorld.Rows(d) : worlds; init => worlds = value; }
    public string AccountId { get; init; } = DevelopmentDefaults.AccountSid;
    public IReadOnlyList<LoginCharacterRow> Characters { get; init; } = [];
    public string RedirectHost { get; init; } = "127.0.0.1";
    public int RedirectPort { get; init; } = 13328;
    /// <summary>The picked world's race for the game port's lobby (0x390c: wire account, race).</summary>
    public Action<string, int>? WorldPicked { get; init; }
    /// <summary>The race of the world the wire account last picked (since the host started), for the server list's default.</summary>
    public Func<string, int?>? LastPickedRace { get; init; }
    /// <summary>
    /// Multi-world routing on 0x390e (authenticated logins): rebinds the login's grant (wire account, grant, world id, row
    /// race) to the picked world before the redirect; false refuses the redirect and the connection closes.
    /// </summary>
    public Func<string, Guid, int, int, CancellationToken, Task<bool>>? Route { get; init; }
}

/// <summary>Replies to one decrypted body, or the reason the connection must close.</summary>
public sealed record SessionStep(List<byte[]> Replies, string? Close = null)
{
    public static SessionStep Of(params byte[][] replies) => new([.. replies]);
    public static SessionStep Closed(string reason) => new([], reason);
}

/// <summary>
/// One login-port connection after the handshake (port of tools/login_server.py). Without an
/// authenticator it is the development flow (local account, generated GUID). With one, only
/// pings may precede the single 0x3905, whose authn token must be accepted; a second 0x3905
/// closes the connection.
/// </summary>
public sealed class LoginSession(LoginSessionOptions options, Func<ClientIdentity, CancellationToken, Task<LoginGrant?>>? authenticate = null)
{
    // The rows of this connection (Directory is a snapshot of the world registry).
    readonly IReadOnlyList<WorldRow> rows = options.Worlds;
    string accountId = options.AccountId;
    Guid guid = Guid.NewGuid();
    IReadOnlyList<LoginCharacterRow> characters = options.Characters;
    // Races of the account's characters (from the grant).
    List<int> races = [];
    // With several worlds: the row of the most recently played character, which the server list opens on.
    int? lastPlayedRow;
    bool authenticated = authenticate is null;

    public bool SentAccount { get; private set; }
    public bool SentDirectory { get; private set; }
    public string? Authentication { get; private set; }

    public async Task<SessionStep> HandleAsync(byte[] plain, long now, CancellationToken ct = default)
    {
        if (plain.Length < 2) return SessionStep.Of();
        var header = (ushort)(plain[0] | plain[1] << 8);
        if (authenticate is not null)
        {
            if (header == 0x3905)
            {
                if (Authentication is not null) return SessionStep.Closed(Authentication = "replay_rejected");
                ClientIdentity identity;
                try { identity = ClientIdentity.Parse(plain, game: false); }
                catch (ProtocolException) { return SessionStep.Closed(Authentication = "rejected"); }
                var grant = await authenticate(identity, ct);
                if (grant is null) return SessionStep.Closed(Authentication = "rejected");
                (accountId, guid) = (grant.WireAccountId, grant.Grant);
                // Each stored character is listed on its world's row of its race (else the first listed row of its race);
                // characters of worlds not listed now (stopped) are left out.
                var listed = grant.Characters
                    .Where(c => c.WorldId == 0 || options.Directory is not { Count: > 0 } directory || directory.Any(w => w.WorldId == c.WorldId)).ToList();
                characters = listed.Take(255)
                    .Select(c => new LoginCharacterRow(RowOf(c.WorldId, c.Race), c.Name, (uint)Math.Max(1, c.Level), LastPlayedUnixMs: c.LastPlayedUnixMs)).ToList();
                races = listed.Select(c => c.Race).Where(r => r is 1 or 2).Distinct().ToList();
                if (options.Directory is { Count: > 1 } && listed.Count > 0)
                {
                    var last = listed.MaxBy(c => c.LastPlayedUnixMs);
                    lastPlayedRow = RowOf(last.WorldId, last.Race);
                }
                authenticated = true;
                Authentication = "accepted";
            }
            else if (!authenticated && header != 0x3902)
                return SessionStep.Closed(Authentication = "required");
        }
        switch (header)
        {
            case 0x3905:
                SentAccount = true;
                // The server list opens on the account's race's world: its characters' race (one race per account), else the
                // world it last picked, else the first (always Light 1361 before, also for an Asmodian account). With several
                // worlds it opens on the row of the most recently played character.
                var race = races.Count > 0 ? races[0] : options.LastPickedRace?.Invoke(accountId);
                var home = rows.FirstOrDefault(w => w.Id == lastPlayedRow) ?? rows.FirstOrDefault(w => w.Group == race) ?? rows[0];
                return SessionStep.Of(LoginPackets.Account(accountId, guid, home.Id,
                    (home.Group == 1 ? home : rows.FirstOrDefault(w => w.Group == 1) ?? home).Id,
                    (home.Group == 2 ? home : rows.FirstOrDefault(w => w.Group == 2))?.Id ?? 0x0903));
            case 0x390a:
                return SessionStep.Of(LoginPackets.Characters(characters));
            case 0x3908:
                SentDirectory = true;
                return SessionStep.Of(LoginPackets.WorldDirectory(rows));
            case 0x3902 when plain.Length >= 10:
                return SessionStep.Of(LoginPackets.Ping(BitConverter.ToUInt64(plain, 2), now));
            case 0x390c when plain.Length >= 4:
                var picked = BitConverter.ToUInt16(plain, 2);
                if (rows.FirstOrDefault(w => w.Id == picked) is { } world) options.WorldPicked?.Invoke(accountId, world.Group);
                return SessionStep.Of(LoginPackets.SelectResult(picked));
            case 0x390e when plain.Length >= 4:
                var target = BitConverter.ToUInt16(plain, 2);
                if (options.Directory is not { Count: > 0 } worlds)
                    return SessionStep.Of(LoginPackets.Redirect(target, options.RedirectHost, options.RedirectPort));
                // Only listed rows are served: the redirect names the picked world's game port, and the login's grant is
                // rebound to that world first (a game port accepts grants for its own world only).
                if (worlds.FirstOrDefault(w => w.LightServerId == target || w.DarkServerId == target) is not { } destination)
                    return SessionStep.Closed("unknown_world");
                var rowRace = destination.LightServerId == target ? 1 : 2;
                if (options.Route is { } route && authenticate is not null && !await route(accountId, guid, destination.WorldId, rowRace, ct))
                    return SessionStep.Closed("route_failed");
                return SessionStep.Of(LoginPackets.Redirect(target, destination.Host, destination.Port));
            default:
                return SessionStep.Of();
        }
    }

    /// <summary>The login row of a character: its world's row of its race, else the first row of its race, else the first row.</summary>
    int RowOf(int worldId, int race)
    {
        if (options.Directory?.FirstOrDefault(w => w.WorldId == worldId) is { } world)
            return race == 2 ? world.DarkServerId : world.LightServerId;
        return (rows.FirstOrDefault(w => w.Group == race) ?? rows[0]).Id;
    }
}
