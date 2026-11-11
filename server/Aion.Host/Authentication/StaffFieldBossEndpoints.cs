using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Security;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>
/// Field bosses on the staff dashboard (docs/FIELD_BOSSES.md), answered by the world server (?world=…): each boss with its
/// state, last kill, next respawn and respawn range; respawn one now (ManageMonsters); set or reset its range
/// (EditFieldBossTimers). Changes are audited as staff_field_boss_respawn and staff_field_boss_timer.
/// </summary>
public static partial class StaffToolsEndpoints
{
    sealed record FieldBossTimerRequest(int? MinMinutes, int? MaxMinutes);

    public static void MapFieldBosses(RouteGroupBuilder staff)
    {
        staff.MapGet("/field-bosses", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.ViewMonsters) is null) return Results.Unauthorized();
            if (services.GetService<SharedWorld>() is not { } world) return Results.BadRequest(new { error = "world_only" });
            await world.Lock.EnterExclusiveAsync(ct);
            try
            {
                var now = world.Clock();
                var timers = world.FieldBosses;
                return Results.Ok(new
                {
                    loaded = timers.Loaded,
                    now,
                    bosses = timers.Definitions.Select(d => timers.View(d, world.LoadedMap(d.MapId), world.Data, now)).ToList(),
                });
            }
            finally { world.Lock.ExitExclusive(); }
        });

        staff.MapPost("/field-bosses/{code:int}/respawn", async (int code, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services,
            CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.ManageMonsters) is not { } a) return Results.Unauthorized();
            if (services.GetService<SharedWorld>() is not { } world) return Results.BadRequest(new { error = "world_only" });
            if (!world.FieldBosses.ByCode.TryGetValue(code, out var boss)) return Results.NotFound();
            FieldBossView view;
            await world.Lock.EnterExclusiveAsync(ct);
            try
            {
                var now = world.Clock();
                world.FieldBosses.RespawnNow(code, now);
                world.LoadedMap(boss.MapId)?.RespawnFieldBoss(code, now);
                view = world.FieldBosses.View(boss, world.LoadedMap(boss.MapId), world.Data, now);
            }
            finally { world.Lock.ExitExclusive(); }
            await game.AdminRecordAsync(a, "staff_field_boss_respawn", null, new { code, map = boss.MapId, npc = boss.NpcId, view.Name }, ct);
            return Results.Ok(view);
        });

        staff.MapPut("/field-bosses/{code:int}/timer", (int code, FieldBossTimerRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) => SetTimer(code, input.MinMinutes, input.MaxMinutes, ctx, auth, game, services, ct));
        staff.MapDelete("/field-bosses/{code:int}/timer", (int code, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
            IServiceProvider services, CancellationToken ct) => SetTimer(code, null, null, ctx, auth, game, services, ct));
    }

    /// <summary>A boss's respawn range in minutes; null, null resets it to the default.</summary>
    static async Task<IResult> SetTimer(int code, int? min, int? max, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game,
        IServiceProvider services, CancellationToken ct)
    {
        if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, StaffPermission.EditFieldBossTimers) is not { } a) return Results.Unauthorized();
        if (services.GetService<SharedWorld>() is not { } world) return Results.BadRequest(new { error = "world_only" });
        if (!world.FieldBosses.ByCode.TryGetValue(code, out var boss)) return Results.NotFound();
        if ((min is null || max is null) && min != max) return Results.BadRequest(new { error = "invalid_timer" });
        FieldBossView view;
        await world.Lock.EnterExclusiveAsync(ct);
        try
        {
            if (!world.FieldBosses.SetRange(code, min, max)) return Results.BadRequest(new { error = "invalid_timer" });
            view = world.FieldBosses.View(boss, world.LoadedMap(boss.MapId), world.Data, world.Clock());
        }
        finally { world.Lock.ExitExclusive(); }
        await game.AdminRecordAsync(a, "staff_field_boss_timer", null,
            new { code, map = boss.MapId, npc = boss.NpcId, view.Name, minMinutes = min, maxMinutes = max, reset = min is null }, ct);
        return Results.Ok(view);
    }
}
