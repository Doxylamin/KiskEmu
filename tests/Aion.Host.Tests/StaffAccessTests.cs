using Aion.Core.Characters;
using Aion.Core.Security;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>The staff permission matrix (Aion.Core/Security/StaffAccess.cs): gamemaster &lt; moderator &lt; administrator.</summary>
public sealed class StaffAccessTests
{
    [Theory]
    [InlineData(new string[0], StaffRank.None)]
    [InlineData(new[] { "gamemaster" }, StaffRank.Gamemaster)]
    [InlineData(new[] { "moderator" }, StaffRank.Moderator)]
    [InlineData(new[] { "admin" }, StaffRank.Administrator)]
    [InlineData(new[] { "gamemaster", "admin" }, StaffRank.Administrator)]
    [InlineData(new[] { "moderator", "gamemaster" }, StaffRank.Moderator)]
    [InlineData(new[] { "owner", "gm" }, StaffRank.None)]
    public void RankIsTheHighestRole(string[] roles, StaffRank rank) => Assert.Equal(rank, StaffAccess.RankOf(roles));

    [Fact]
    public void EachRankHoldsTheRanksBelow()
    {
        foreach (var permission in Enum.GetValues<StaffPermission>())
        {
            var required = StaffAccess.Required(permission);
            Assert.NotEqual(StaffRank.None, required);
            foreach (var rank in Enum.GetValues<StaffRank>())
                Assert.Equal(rank >= required, StaffAccess.Can(rank, permission));
        }
        Assert.All(Enum.GetValues<StaffPermission>(), p => Assert.True(StaffAccess.Can(["admin"], p)));
        Assert.All(Enum.GetValues<StaffPermission>(), p => Assert.False(StaffAccess.Can([], p)));
    }

    [Theory]
    [InlineData(StaffPermission.ViewDashboard, StaffRank.Gamemaster)]
    [InlineData(StaffPermission.AssistPlayers, StaffRank.Gamemaster)]
    [InlineData(StaffPermission.Announce, StaffRank.Gamemaster)]
    [InlineData(StaffPermission.HandleTickets, StaffRank.Gamemaster)]
    [InlineData(StaffPermission.Sanction, StaffRank.Moderator)]
    [InlineData(StaffPermission.DisableAccounts, StaffRank.Moderator)]
    [InlineData(StaffPermission.EditCharacters, StaffRank.Moderator)]
    [InlineData(StaffPermission.RewardCharacters, StaffRank.Moderator)]
    [InlineData(StaffPermission.ManageEvents, StaffRank.Moderator)]
    [InlineData(StaffPermission.ManageMonsters, StaffRank.Moderator)]
    [InlineData(StaffPermission.ManageStaff, StaffRank.Administrator)]
    [InlineData(StaffPermission.ManageQuna, StaffRank.Administrator)]
    [InlineData(StaffPermission.EditShop, StaffRank.Administrator)]
    [InlineData(StaffPermission.ViewCustomNpcs, StaffRank.Gamemaster)]
    [InlineData(StaffPermission.EditCustomNpcs, StaffRank.Administrator)]
    [InlineData(StaffPermission.EditFieldBossTimers, StaffRank.Administrator)]
    [InlineData(StaffPermission.ManageWorlds, StaffRank.Administrator)]
    [InlineData(StaffPermission.ManageServer, StaffRank.Administrator)]
    [InlineData(StaffPermission.ManageAccounts, StaffRank.Administrator)]
    [InlineData(StaffPermission.RewardEveryone, StaffRank.Administrator)]
    public void MatrixAsAgreed(StaffPermission permission, StaffRank lowest) => Assert.Equal(lowest, StaffAccess.Required(permission));

    [Fact]
    public void PermissionNamesAreCamelCaseForTheDashboard()
    {
        Assert.Equal("manageStaff", StaffAccess.Name(StaffPermission.ManageStaff));
        var gm = StaffAccess.PermissionNames(StaffRank.Gamemaster);
        Assert.Contains("viewDashboard", gm);
        Assert.DoesNotContain("sanction", gm);
        Assert.Contains("sanction", StaffAccess.PermissionNames(StaffRank.Moderator));
        Assert.Equal(Enum.GetValues<StaffPermission>().Length, StaffAccess.PermissionNames(StaffRank.Administrator).Count);
        Assert.Empty(StaffAccess.PermissionNames(StaffRank.None));
    }

    [Fact]
    public void RolesAndSqlListAgree()
    {
        Assert.Equal(StaffAccess.RoleSqlList, string.Join(",", StaffAccess.Roles.Select(r => $"'{r}'")));
        Assert.All(StaffAccess.Roles, r => Assert.True(StaffAccess.IsRole(r)));
        Assert.False(StaffAccess.IsRole("gm"));
        foreach (var rank in new[] { StaffRank.Gamemaster, StaffRank.Moderator, StaffRank.Administrator })
            Assert.Equal(rank, StaffAccess.RankOfRole(StaffAccess.RoleOf(rank)));
    }

    [Fact]
    public void ModeratorNameTag()
    {
        Assert.Equal("[Mod] Elyra", CharacterNames.Display("Elyra", "moderator"));
        Assert.Equal("Elyra", CharacterNames.Stored("[Mod] Elyra"));
        Assert.Equal("moderator", CharacterNames.DisplayRole(["gamemaster", "moderator"]));
        Assert.Equal("admin", CharacterNames.DisplayRole(["moderator", "admin"]));
        Assert.Null(CharacterNames.DisplayRole([]));
        Assert.True(CharacterNames.IsStaffStyle("Mod_Elyra"));
        Assert.True(CharacterNames.IsStaffStyle("Moderator"));
        Assert.False(CharacterNames.IsStaffStyle("Modrin"));
    }
}
