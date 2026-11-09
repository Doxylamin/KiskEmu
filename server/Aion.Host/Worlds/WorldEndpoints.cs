using System.Security.Cryptography;
using System.Text;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Host.Native;
using Aion.World;

namespace Aion.Host.Worlds;

/// <summary>Aion:WorldProcess, set by the primary host for a managed child (world role, Aion:Role=world).</summary>
public sealed record WorldProcessOptions
{
    /// <summary>Required on /internal/world/stop (the primary host derives it per world).</summary>
    public string ControlToken { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>The options when this host runs in world role, else null.</summary>
    public static WorldProcessOptions? From(IConfiguration configuration) =>
        string.Equals(configuration["Aion:Role"], "world", StringComparison.OrdinalIgnoreCase)
            ? configuration.GetSection("Aion:WorldProcess").Get<WorldProcessOptions>() ?? new WorldProcessOptions()
            : null;
}

/// <summary>
/// Multi-world HTTP: the staff dashboard's World Management (/api/admin/worlds: list, add, change, start, stop, restart,
/// remove; changes need the admin role, the store checks and audits them) and a world process's loopback endpoints for
/// the primary host (/internal/world/status, /internal/world/stop).
/// </summary>
public static class WorldEndpoints
{
    sealed record EnabledRequest(bool Enabled);

    public static void MapWorldEndpoints(WebApplication app)
    {
        var staff = app.MapGroup("/api/admin/worlds").RequireRateLimiting("admin");
        staff.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });

        staff.MapGet("", async (HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            if (services.GetService<IWorldRegistry>() is not { } registry) return Results.NotFound();
            var counts = await registry.CharacterCountsAsync(account, ct);
            var roles = services.GetService<IStaffTools>() is { } tools ? await tools.RolesAsync(account, ct) : [];
            var native = services.GetService<NativeOptions>();
            var supervisor = services.GetService<WorldSupervisor>();
            var presence = services.GetService<WorldPresence>();
            if (native is not null) await registry.EnsurePrimaryAsync(native.WorldId, ct);
            var worlds = await registry.ListAsync(ct);
            var listed = native is null ? [] : LoginWorldList.Directory(worlds, native, supervisor is null ? _ => WorldRuntime.None : supervisor.Runtime);
            return Results.Ok(new
            {
                canManage = roles.Contains("admin"),
                supervised = supervisor is not null,
                publicAddress = native?.Network.PublicAddress,
                worlds = worlds.Select(w =>
                {
                    var runtime = w.IsPrimary
                        ? new WorldRuntime(native?.GamePort is null ? WorldRuntime.Stopped : WorldRuntime.Running, Environment.ProcessId, null,
                            presence?.All.Count, presence?.Connections, null, 0)
                        : supervisor?.Runtime(w.WorldId) ?? WorldRuntime.None;
                    var worldId = w.IsPrimary && native is not null ? native.WorldId : w.WorldId;
                    return new
                    {
                        w.WorldId, w.Name, w.LightServerId, w.DarkServerId, w.Kind, w.Host,
                        GamePort = w.IsPrimary ? native?.GamePort : w.GamePort,
                        w.InternalPort, w.Enabled, w.LevelCap, w.CreatedAt, w.UpdatedAt,
                        Characters = counts.GetValueOrDefault(worldId),
                        Listed = listed.Any(l => l.WorldId == worldId),
                        Runtime = runtime,
                    };
                }),
            });
        });
        staff.MapPost("", async (WorldInput input, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            var created = await registry.CreateAsync(account, input, ct);
            services.GetService<WorldSupervisor>()?.Nudge();
            return Results.Ok(created);
        });
        staff.MapPut("/{id:int}", async (int id, WorldInput input, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            var updated = await registry.UpdateAsync(account, id, input, ct);
            services.GetService<WorldSupervisor>()?.Nudge();
            return Results.Ok(updated);
        });
        staff.MapPost("/{id:int}/start", (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
            SetEnabled(id, true, ctx, auth, registry, services, ct));
        staff.MapPost("/{id:int}/stop", (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
            SetEnabled(id, false, ctx, auth, registry, services, ct));
        staff.MapPost("/{id:int}/restart", async (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            // The admin check and the audit entry: re-enable (a no-op for a running world).
            var world = await registry.SetEnabledAsync(account, id, true, ct);
            if (!world.IsManaged) return Results.BadRequest(new { error = "not_managed" });
            if (services.GetService<WorldSupervisor>() is not { } supervisor) return Results.BadRequest(new { error = "not_supervised" });
            await supervisor.RestartAsync(id, ct);
            return Results.Ok(world);
        });
        staff.MapDelete("/{id:int}", async (int id, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
            await registry.DeleteAsync(account, id, ct);
            services.GetService<WorldSupervisor>()?.Nudge();
            return Results.Ok(new { removed = id });
        });
    }

    static async Task<IResult> SetEnabled(int id, bool enabled, HttpContext ctx, IAccountAuthentication auth, IWorldRegistry registry, IServiceProvider services, CancellationToken ct)
    {
        if (await Signed(ctx, auth, ct) is not { } account) return Results.Unauthorized();
        var world = await registry.SetEnabledAsync(account, id, enabled, ct);
        services.GetService<WorldSupervisor>()?.Nudge();
        return Results.Ok(world);
    }

    /// <summary>World role: status and graceful stop on the loopback HTTP port.</summary>
    public static void MapWorldProcessEndpoints(WebApplication app, WorldProcessOptions options, int worldId)
    {
        app.MapGet("/internal/world/status", (IServiceProvider services) =>
        {
            var presence = services.GetService<WorldPresence>();
            return Results.Ok(new WorldProcessStatus(worldId, presence?.All.Count ?? 0, presence?.Connections ?? 0, options.StartedAt.ToUnixTimeMilliseconds()));
        });
        app.MapPost("/internal/world/stop", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            var given = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-World-Control"].ToString());
            var expected = Encoding.UTF8.GetBytes(options.ControlToken);
            if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(given, expected)) return Results.StatusCode(403);
            app.Logger.LogInformation("World {World} stopping on request of the primary host", worldId);
            lifetime.StopApplication();
            return Results.Ok(new { stopping = true });
        });
    }

    static async Task<VerifiedAccount?> Signed(HttpContext ctx, IAccountAuthentication auth, CancellationToken ct)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return await auth.AuthenticateAsync(h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : "", ct);
    }
}
