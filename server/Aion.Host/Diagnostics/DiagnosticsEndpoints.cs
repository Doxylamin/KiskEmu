using System.Diagnostics;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core.Security;
using Aion.World;
using Npgsql;

namespace Aion.Host.Diagnostics;

/// <summary>
/// The staff dashboard's diagnostics card (administrators, <see cref="StaffPermission.ViewDiagnostics"/>): this host's process
/// CPU and memory, the database round trip, its recent warnings and errors, the players connected, and the client requests
/// it left unanswered (<see cref="UnansweredRequests"/>). The realm answers for itself; with ?world=N it forwards the request
/// to that world server (Program.cs), which answers for its own. Everything shown is in memory and starts empty at a restart.
/// </summary>
public static class DiagnosticsEndpoints
{
    /// <summary>The stored staff roles of a signed-in account (the host uses the gameplay store's admin profile).</summary>
    public delegate Task<IReadOnlyList<string>> StaffRoles(IServiceProvider services, VerifiedAccount account, CancellationToken ct);

    static readonly object cpuSync = new();
    static (long At, double Cpu)? cpuBefore;

    /// <param name="role">"realm" or "world": what the card calls this host.</param>
    public static RouteGroupBuilder Map(IEndpointRouteBuilder app, string role, StaffRoles? roles = null)
    {
        roles ??= async (services, account, ct) =>
            services.GetService<IGameplayStore>() is { } game ? (await game.AdminProfileAsync(account, ct)).Roles : [];
        var group = app.MapGroup("/api/admin/diagnostics");
        group.AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
            if (await ctx.RequestServices.GetRequiredService<IAccountAuthentication>().AuthenticateAsync(token, ctx.RequestAborted) is not { } account)
                return Results.Unauthorized();
            IReadOnlyList<string> held;
            try { held = await roles(ctx.RequestServices, account, ctx.RequestAborted); }
            catch (GameplayException error) when (error.Code == "forbidden") { return Results.StatusCode(403); }
            return StaffAccess.Can(held, StaffPermission.ViewDiagnostics) ? await next(context) : Results.StatusCode(403);
        });
        group.MapGet("", async (IServiceProvider services, CancellationToken ct) => Results.Ok(new
        {
            Role = role,
            ServerTime = DateTimeOffset.UtcNow,
            Process = ProcessHealth(),
            Database = await DatabaseAsync(services.GetService<NpgsqlDataSource>(), ct),
            Logs = services.GetService<LogCounter>()?.Read(),
            Players = services.GetService<WorldPresence>() is { } presence ? new { Online = presence.All.Count, presence.Connections } : null,
            Unanswered = services.GetService<UnansweredRequests>()?.Read(),
            Limits = new { UnansweredRequests.MaxOpcodes, UnansweredRequests.MaxBodies, UnansweredRequests.MaxBodyBytes },
        }));
        group.MapDelete("/unanswered", (IServiceProvider services) =>
            Results.Ok(new { cleared = services.GetService<UnansweredRequests>()?.Clear() ?? 0 }));
        return group;
    }

    /// <summary>CPU (cores busy since the last call, else since the start), memory, threads and collections of this process.</summary>
    static object ProcessHealth()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime.TotalSeconds;
        var now = Stopwatch.GetTimestamp();
        double cores;
        lock (cpuSync)
        {
            var before = cpuBefore ?? (Stopwatch.GetTimestamp() - (long)((DateTime.Now - process.StartTime).TotalSeconds * Stopwatch.Frequency), 0);
            var seconds = (now - before.At) / (double)Stopwatch.Frequency;
            cores = seconds > 0 ? Math.Max(0, (cpu - before.Cpu) / seconds) : 0;
            cpuBefore = (now, cpu);
        }
        return new
        {
            StartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime()),
            Cores = Environment.ProcessorCount, Cpu = Math.Round(cores, 3),
            MemoryMb = process.WorkingSet64 / 1048576, HeapMb = GC.GetTotalMemory(false) / 1048576, Threads = process.Threads.Count,
            Gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
        };
    }

    /// <summary>One SELECT 1 on this host's database: its round trip, or the error (null without a database).</summary>
    static async Task<object?> DatabaseAsync(NpgsqlDataSource? database, CancellationToken ct)
    {
        if (database is null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        try
        {
            await using var command = database.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(timeout.Token);
            return new { Ok = true, Ms = Math.Round(watch.Elapsed.TotalMilliseconds, 2), Error = (string?)null };
        }
        catch (Exception error) when (error is NpgsqlException or OperationCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return new { Ok = false, Ms = Math.Round(watch.Elapsed.TotalMilliseconds, 2), Error = (string?)(error is OperationCanceledException ? "timeout" : error.GetType().Name) };
        }
    }
}
