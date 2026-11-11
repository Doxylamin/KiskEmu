namespace Aion.Core.Security;

/// <summary>Staff ranks, lowest first. Each rank holds every permission of the ranks below it.</summary>
public enum StaffRank { None = 0, Gamemaster = 1, Moderator = 2, Administrator = 3 }

/// <summary>What a staff member may do on the dashboard. <see cref="StaffAccess.Required"/> names the lowest rank.</summary>
public enum StaffPermission
{
    // Gamemaster: look at the game and help players.
    ViewDashboard,        // the staff profile, live map, characters, inventories, catalogue, maps, server health, worlds list
    ViewAudit,            // the activity log
    ViewEconomy,          // economy and population figures
    ViewShop,             // shop goods and orders
    AssistPlayers,        // message, unstuck, teleport, restore a player
    Announce,             // announcements
    HandleTickets,        // read and answer tickets, read the chat log
    ViewMonsters,         // the monsters of a map
    ViewCustomNpcs,       // the custom NPCs and their dialogs
    // Moderator: act on players.
    Sanction,             // mute, kick, ban and lift (on players; staff need ManageStaff)
    DisableAccounts,      // disable player accounts
    ViewAccounts,         // account search and detail
    EditCharacters,       // character edits and item grants (POST /commands)
    RewardCharacters,     // rewards to one character
    ManageEvents,         // create and delete scheduled events
    ManageMonsters,       // spawn, clear and respawn monsters (and field bosses)
    // Administrator: run the server.
    ManageStaff,          // roles, and sanctions or disabling of staff accounts
    ManageQuna,           // add or take Quna
    EditShop,             // edit custom shop goods
    EditCustomNpcs,       // create, change and remove custom NPCs
    EditFieldBossTimers,  // the respawn range of each field boss
    ManageWorlds,         // add, change, start, stop, restart worlds and their tokens
    ManageServer,         // restart, maintenance, backups, debug switches
    ManageAccounts,       // new accounts, password and authenticator resets
    RewardEveryone,       // rewards to every character
    ViewDiagnostics,      // process health, recent errors and the client requests left unanswered (diagnostics card)
}

/// <summary>
/// The one place that turns stored roles into ranks and permissions. Stored role strings are
/// <c>gamemaster</c>, <c>moderator</c> and <c>admin</c> (auth.account_roles; "admin" is shown as Administrator).
/// </summary>
public static class StaffAccess
{
    public const string GamemasterRole = "gamemaster", ModeratorRole = "moderator", AdministratorRole = "admin";

    /// <summary>Every stored staff role, lowest rank first.</summary>
    public static readonly IReadOnlyList<string> Roles = [GamemasterRole, ModeratorRole, AdministratorRole];

    /// <summary>The roles as a SQL list for <c>r.role IN (…)</c> filters.</summary>
    public const string RoleSqlList = "'gamemaster','moderator','admin'";

    public static bool IsRole(string? role) => role is GamemasterRole or ModeratorRole or AdministratorRole;

    public static StaffRank RankOfRole(string? role) => role switch
    {
        AdministratorRole => StaffRank.Administrator,
        ModeratorRole => StaffRank.Moderator,
        GamemasterRole => StaffRank.Gamemaster,
        _ => StaffRank.None,
    };

    /// <summary>The highest rank among the roles; None for players.</summary>
    public static StaffRank RankOf(IEnumerable<string>? roles)
    {
        var rank = StaffRank.None;
        foreach (var role in roles ?? []) if (RankOfRole(role) > rank) rank = RankOfRole(role);
        return rank;
    }

    /// <summary>The stored role of a rank (null for None).</summary>
    public static string? RoleOf(StaffRank rank) => rank switch
    {
        StaffRank.Administrator => AdministratorRole,
        StaffRank.Moderator => ModeratorRole,
        StaffRank.Gamemaster => GamemasterRole,
        _ => null,
    };

    /// <summary>The lowest rank that holds a permission.</summary>
    public static StaffRank Required(StaffPermission permission) => permission switch
    {
        StaffPermission.ViewDashboard or StaffPermission.ViewAudit or StaffPermission.ViewEconomy or StaffPermission.ViewShop
            or StaffPermission.AssistPlayers or StaffPermission.Announce or StaffPermission.HandleTickets
            or StaffPermission.ViewMonsters or StaffPermission.ViewCustomNpcs => StaffRank.Gamemaster,
        StaffPermission.Sanction or StaffPermission.DisableAccounts or StaffPermission.ViewAccounts or StaffPermission.EditCharacters
            or StaffPermission.RewardCharacters or StaffPermission.ManageEvents or StaffPermission.ManageMonsters => StaffRank.Moderator,
        _ => StaffRank.Administrator,
    };

    public static bool Can(StaffRank rank, StaffPermission permission) => rank >= Required(permission);
    public static bool Can(IEnumerable<string>? roles, StaffPermission permission) => Can(RankOf(roles), permission);

    /// <summary>The permissions a rank holds, by name (for the dashboard, which hides what it cannot use).</summary>
    public static IReadOnlyList<string> PermissionNames(StaffRank rank) =>
        Enum.GetValues<StaffPermission>().Where(p => Can(rank, p)).Select(Name).ToArray();

    /// <summary>A permission's wire name: camelCase of the enum name ("ManageStaff" → "manageStaff").</summary>
    public static string Name(StaffPermission permission)
    {
        var name = permission.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>The label shown for a rank.</summary>
    public static string Label(StaffRank rank) => rank switch
    {
        StaffRank.Administrator => "Administrator",
        StaffRank.Moderator => "Moderator",
        StaffRank.Gamemaster => "Gamemaster",
        _ => "Player",
    };
}
