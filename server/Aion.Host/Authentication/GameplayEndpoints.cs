using Aion.Core.Security;
using Aion.Application.Authentication;
using System.Text.Json;
using Aion.Application.Gameplay;

namespace Aion.Host.Authentication;

public static class GameplayEndpoints
{
    private sealed record MoveItem(long Revision, int? EquipmentSlot);
    private sealed record ChangePassword(string CurrentPassword, string NewPassword);
    private sealed record StaffRoleChange(string Role, bool Granted);
    /// <param name="Amount">Quna to add (positive) or take (negative).</param>
    private sealed record QunaStaffChange(Guid RequestId, long Amount, string? Note);
    /// <param name="accounts">The account store's routes (password, sessions, TOTP reset, disable, roles): the auth host
    /// only; a world server has the realm's accounts only as a mirror (server/DATABASES.md).</param>
    public static void MapGameplayEndpoints(this WebApplication app, bool accounts = true)
    {
        var api=app.MapGroup("/api").RequireRateLimiting("account-entry");
        api.AddEndpointFilter(async (context,next) =>
        {
            try { return await next(context); }
            catch(GameplayException error)
            { return error.Code=="forbidden" ? Results.StatusCode(403) : Results.BadRequest(new {error=error.Code}); }
        });
        api.MapPost("/characters", async(CreatePlayer input,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct);
            if(account is null) return Results.Unauthorized();
            var id=await game.CreateAsync(account,input,ct);
            return Results.Ok(new {characterId=id});
        });
        api.MapGet("/characters/{id:guid}/inventory",async(Guid id,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            var inventory=await game.InventoryAsync(account,id,ct); return inventory is null ? Results.NotFound() : Results.Ok(inventory);
        });
        api.MapPost("/characters/{id:guid}/items/{item:guid}/move",async(Guid id,Guid item,MoveItem input,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return await game.MoveItemAsync(account,id,item,input.Revision,input.EquipmentSlot,ct) ? Results.NoContent() : Results.Conflict();
        });
        if (accounts)
        {
        api.MapPost("/accounts/password",async(ChangePassword input,HttpContext ctx,IAccountManagement management,CancellationToken ct) =>
            await management.ChangePasswordAsync(Bearer(ctx),input.CurrentPassword,input.NewPassword,ct) ? Results.NoContent() : Results.Unauthorized());
        api.MapPost("/accounts/revoke-sessions",async(HttpContext ctx,IAccountManagement management,CancellationToken ct) =>
            await management.RevokeSessionsAsync(Bearer(ctx),ct) ? Results.NoContent() : Results.Unauthorized());
        }
        // Staff dashboard: role-checked in the store; a separate, larger rate budget than account entry.
        var admin=app.MapGroup("/api/admin").RequireRateLimiting("admin");
        admin.AddEndpointFilter(async (context,next) =>
        {
            try { return await next(context); }
            catch(GameplayException error)
            { return error.Code=="forbidden" ? Results.StatusCode(403) : Results.BadRequest(new {error=error.Code}); }
        });
        admin.MapGet("/me",async(HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            var profile=await game.AdminProfileAsync(account,ct);
            // The rank and its permissions: the dashboard hides what it cannot use (the server checks every request).
            var rank=StaffAccess.RankOf(profile.Roles);
            return Results.Ok(new {profile.Roles,profile.BuildId,rank=StaffAccess.RoleOf(rank),rankLabel=StaffAccess.Label(rank),
                permissions=StaffAccess.PermissionNames(rank)});
        });
        admin.MapGet("/characters",async(string? q,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return Results.Ok(await game.AdminFindCharactersAsync(account,q??"",ct));
        });
        admin.MapGet("/characters/{id:guid}",async(Guid id,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            var detail=await game.AdminCharacterAsync(account,id,ct); return detail is null ? Results.NotFound() : Results.Ok(detail);
        });
        admin.MapGet("/items",async(string? q,string? category,int? offset,int? limit,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            return Results.Ok(await game.AdminCatalogAsync(account,q??"",category,offset??0,limit??120,ct));
        });
        if (accounts)
        admin.MapPost("/accounts/{id:guid}/totp/reset", async (Guid id, HttpContext ctx, IAccountAuthentication auth, ITotpAuthentication totp, IGameplayStore game, CancellationToken ct) =>
        {
            var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
            if (account is null) return Results.Unauthorized();
            try
            {
                if (!StaffAccess.Can((await game.AdminProfileAsync(account, ct)).Roles, StaffPermission.ManageAccounts)) return Results.StatusCode(403);
            }
            catch (GameplayException) { return Results.StatusCode(403); }
            await totp.AdminResetAsync(id, ct);
            return Results.NoContent();
        });
        admin.MapPost("/commands",async(AdminCommand input,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            if(!StaffAccess.Can((await game.AdminProfileAsync(account,ct)).Roles,StaffPermission.EditCharacters)) return Results.StatusCode(403);
            return Results.Ok(await game.AdminAsync(account,input,ct));
        });
        // Minimap outlines: a map's subzone polygons (2D, cm), the same data that clears world-map fog.
        admin.MapGet("/maps/{id:int}/outline",async(int id,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            await game.AdminProfileAsync(account,ct);   // staff only
            var areas=Aion.World.Subzones.OnMap(id).OrderBy(a => a.Priority).Select(a => new {
                a.Name, a.Priority, Points=a.Points.Select(p => new[]{Math.Round(p[0]),Math.Round(p[1])}).ToArray() });
            return Results.Ok(new { MapId=id, Areas=areas });
        });
        if (!accounts) return;
        // Both reach the world servers' account mirrors at once over their realm sockets (server/REALM_LINK.md).
        api.MapPost("/admin/accounts/{id:guid}/disable",async(Guid id,HttpContext ctx,IAccountManagement management,IServiceProvider services,CancellationToken ct) =>
        {
            if(!await management.DisableAccountAsync(Bearer(ctx),id,ct)) return Results.StatusCode(403);
            if(services.GetService<Aion.Host.Realm.RealmSocketHub>() is { } sockets) await sockets.AccountChangedAsync(id);
            return Results.NoContent();
        });
        // Admins only; the world servers mirror the change now, the player's client sees it at their next game login.
        api.MapPost("/admin/accounts/{id:guid}/roles",async(Guid id,StaffRoleChange input,HttpContext ctx,IAccountManagement management,IServiceProvider services,CancellationToken ct) =>
        {
            if(!await management.SetStaffRoleAsync(Bearer(ctx),id,input.Role,input.Granted,ct)) return Results.StatusCode(403);
            if(services.GetService<Aion.Host.Realm.RealmSocketHub>() is { } sockets) await sockets.AccountChangedAsync(id);
            return Results.NoContent();
        });
        // The account's Quna wallet (server/DATABASES.md). Staff read it; admins add or take Quna here, the only place Quna
        // is credited (world servers can only spend and refund). The ledger keeps the admin and the note; the realm pushes
        // the new balance to the world the account plays on.
        admin.MapGet("/accounts/{id:guid}/quna",async(Guid id,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,IQunaWallet wallet,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            await game.AdminProfileAsync(account,ct);   // staff only
            var balance=await wallet.BalanceAsync(id,ct);
            return Results.Ok(new {balance=balance.Balance});
        });
        admin.MapPost("/accounts/{id:guid}/quna",async(Guid id,QunaStaffChange input,HttpContext ctx,IAccountAuthentication auth,IGameplayStore game,IQunaWallet wallet,CancellationToken ct) =>
        {
            var account=await auth.AuthenticateAsync(Bearer(ctx),ct); if(account is null) return Results.Unauthorized();
            if(!StaffAccess.Can((await game.AdminProfileAsync(account,ct)).Roles,StaffPermission.ManageQuna)) return Results.StatusCode(403);
            var note=(input.Note??"").Trim();
            if(input.RequestId==Guid.Empty || input.Amount==0 || Math.Abs(input.Amount)>QunaWalletRules.MaxAmount || note.Length>200)
                return Results.BadRequest(new {error="invalid_quna_change"});
            var request=new QunaRequest(id,"staff:"+input.RequestId.ToString("N"),Math.Abs(input.Amount),QunaWalletRules.Staff,
                Details:JsonSerializer.SerializeToElement(new {actor=account.AccountId,note}));
            QunaResult result;
            try { result=input.Amount>0 ? await wallet.GrantAsync(request,ct) : await wallet.SpendAsync(request,ct); }
            catch(System.Data.Common.DbException) { return Results.NotFound(new {error="unknown_account"}); }   // no such account (the wallet's key)
            return result.Ok ? Results.Ok(new {balance=result.Balance}) : Results.BadRequest(new {error="quna_"+result.Error,balance=result.Balance});
        });
    }
    private static string Bearer(HttpContext context)
    { var h=context.Request.Headers.Authorization.ToString(); return h.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?h[7..]:""; }
}
