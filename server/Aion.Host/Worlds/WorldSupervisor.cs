using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Aion.Application.Gameplay;

namespace Aion.Host.Worlds;

/// <summary>What the primary host knows about a non-primary world right now (dashboard and login list).</summary>
public sealed record WorldRuntime(string State, int? ProcessId, DateTimeOffset? Since, int? Players, int? Connections, string? LastError, int Restarts)
{
    public const string Stopped = "stopped", Starting = "starting", Running = "running", Stopping = "stopping", Crashed = "crashed",
        Online = "online", Offline = "offline";
    /// <summary>Whether the login port lists the world (its game port answers).</summary>
    public bool Listed => State is Running or Online;
    public static readonly WorldRuntime None = new(Stopped, null, null, null, null, null, 0);
}

/// <summary>The status a world-role host reports on its loopback port (/internal/world/status).</summary>
public sealed record WorldProcessStatus(int WorldId, int Players, int Connections, long StartedAtMs);

/// <summary>
/// Keeps managed worlds (game.worlds kind "managed") in their desired state: an enabled world runs as a child aion-host in
/// world role (Aion:Role=world) on its own game port, a disabled one is stopped gracefully (its players are saved). A child
/// that exits on its own is restarted with a growing delay. Remote worlds are only probed: their status port (internal
/// port, when set) or else their game port answers or not. The auth host runs no world itself.
/// </summary>
public sealed class WorldSupervisor(IWorldRegistry registry, IConfiguration configuration, ILogger<WorldSupervisor> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(5), StopTimeout = TimeSpan.FromSeconds(45), MaxBackoff = TimeSpan.FromMinutes(2);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    sealed class Child
    {
        public required WorldRecord World;
        public Process? Process;
        public string State = WorldRuntime.Stopped;
        public DateTimeOffset? Since;
        public DateTimeOffset NextStart;
        public int Failures;
        public int Restarts;
        public string? LastError;
        public WorldProcessStatus? Status;
        public bool Adopted;
        public DateTimeOffset ProbedAt;
    }

    readonly ConcurrentDictionary<int, Child> children = new();
    readonly HashSet<int> databases = [];
    readonly SemaphoreSlim wake = new(0);
    readonly SemaphoreSlim reconcile = new(1, 1);
    // Control tokens: derived from the bridge secret when set (a restarted auth host can still reach the world servers it did
    // not start, and a remote world server with the same secret derives its own), else random per auth host process.
    readonly byte[] controlKey = WorldProcessOptions.ControlKey(configuration) ?? RandomNumberGenerator.GetBytes(32);

    // Control tokens remote world servers reported when they signed in (they make their own; managed ones use the derived one).
    readonly ConcurrentDictionary<int, string> reported = new();

    /// <summary>The token a world server accepts on its control endpoints (/internal/world/...).</summary>
    public string ControlToken(int worldId) => reported.TryGetValue(worldId, out var token) ? token : WorldProcessOptions.Token(controlKey, worldId);

    /// <summary>The token a managed world server signs in to the realm link with (/internal/realm/...).</summary>
    public string LinkToken(int worldId) => WorldProcessOptions.LinkToken(controlKey, worldId);

    /// <summary>A remote world server signed in with the control token it accepts.</summary>
    public void Registered(WorldRecord world, string controlToken)
    {
        if (!world.IsManaged && controlToken.Length is >= 32 and <= 200) reported[world.WorldId] = controlToken;
    }

    public WorldRuntime Runtime(int worldId) => children.TryGetValue(worldId, out var c)
        ? new(c.State, c.Process is { HasExited: false } p ? p.Id : null, c.Since, c.Status?.Players, c.Status?.Connections, c.LastError, c.Restarts)
        : WorldRuntime.None;

    /// <summary>Reconcile now (after a staff change) instead of at the next interval.</summary>
    public void Nudge() { if (wake.CurrentCount == 0) wake.Release(); }

    /// <summary>Stops a running managed world and lets the next pass start it again.</summary>
    public async Task RestartAsync(int worldId, CancellationToken ct)
    {
        if (!children.TryGetValue(worldId, out var child)) return;
        await reconcile.WaitAsync(ct);
        try { await StopChildAsync(child, "restart"); child.NextStart = DateTimeOffset.UtcNow; child.Failures = 0; }
        finally { reconcile.Release(); }
        Nudge();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reconcile.WaitAsync(stoppingToken);
                try { await ReconcileAsync(stoppingToken); }
                finally { reconcile.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("World supervisor pass failed: {Error}", error.Message); }
            try { await wake.WaitAsync(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // The auth host is going down: every child is stopped gracefully (systemd also signals them; a double stop is harmless).
        await Task.WhenAll(children.Values.Where(c => c.Process is not null).Select(c => StopChildAsync(c, "host shutdown")));
    }

    async Task ReconcileAsync(CancellationToken ct)
    {
        var worlds = await registry.ListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var gone in children.Keys.Except(worlds.Select(w => w.WorldId)).ToList())
            if (children.TryRemove(gone, out var removed)) await StopChildAsync(removed, "removed");
        foreach (var world in worlds)
        {
            var child = children.GetOrAdd(world.WorldId, _ => new Child { World = world });
            var previous = child.World;
            child.World = world;
            if (world.Kind == WorldRecord.Remote)
            {
                if (child.Process is not null) await StopChildAsync(child, "now remote");
                // A probe opens a game connection there (closed after the greeting window), so only every 30 seconds.
                if (!world.Enabled) { child.State = WorldRuntime.Stopped; child.Status = null; }
                else if (world.InternalPort is not null)
                {
                    // A remote world server with a status port: asked like a managed one (players, uptime), every pass.
                    child.Status = await StatusAsync(world, ct);
                    child.State = child.Status is { } status && status.WorldId == world.WorldId ? WorldRuntime.Online : WorldRuntime.Offline;
                }
                else if (world.Host is null || world.GamePort is null) child.State = WorldRuntime.Offline;   // not signed in yet
                else if (now - child.ProbedAt >= TimeSpan.FromSeconds(30) || child.State == WorldRuntime.Stopped || previous != world)
                {
                    child.ProbedAt = now;
                    child.State = await ProbeAsync(world.Host, world.GamePort.Value, ct) ? WorldRuntime.Online : WorldRuntime.Offline;
                }
                continue;
            }
            var running = child.Process is { HasExited: false } || child.Adopted;
            if (running && (!world.Enabled || previous.GamePort != world.GamePort || previous.InternalPort != world.InternalPort))
            {
                await StopChildAsync(child, world.Enabled ? "ports changed" : "disabled");
                running = false;
            }
            if (child.Process is { HasExited: true } exited)
            {
                child.LastError = $"exited with code {exited.ExitCode}";
                child.Process.Dispose();
                child.Process = null;
                if (child.State != WorldRuntime.Stopped)
                {
                    child.Failures++;
                    child.State = WorldRuntime.Crashed;
                    child.NextStart = now + TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, child.Failures - 1)));
                    logger.LogWarning("World {World} ({Name}) exited unexpectedly ({Error}); restarting after {At:u}", world.WorldId, world.Name, child.LastError, child.NextStart);
                }
                running = false;
            }
            if (!world.Enabled) { child.State = WorldRuntime.Stopped; continue; }
            if (!running)
            {
                // A child left over from an earlier auth host process (still answering) is adopted instead of started twice.
                if (await StatusAsync(world, ct) is { } leftover && leftover.WorldId == world.WorldId)
                {
                    child.Adopted = true;
                    child.Status = leftover;
                    child.State = WorldRuntime.Running;
                    child.Since ??= DateTimeOffset.FromUnixTimeMilliseconds(leftover.StartedAtMs);
                    continue;
                }
                if (now >= child.NextStart && await DatabaseReadyAsync(child, ct)) Start(child);
                continue;
            }
            child.Status = await StatusAsync(world, ct);
            if (child.Status is not null)
            {
                if (child.State != WorldRuntime.Running) logger.LogInformation("World {World} ({Name}) is up on game port {Port}", world.WorldId, world.Name, world.GamePort);
                child.State = WorldRuntime.Running;
                child.Failures = 0;
            }
            else if (child.Adopted)
            {
                child.Adopted = false;
                child.State = WorldRuntime.Crashed;
            }
        }
    }

    /// <summary>
    /// The world's own database (server/DATABASES.md): created, migrated and seeded by tools/postgres_dev.py create-world
    /// before the first start in this process (Aion:Backups:Tool and :PgHome name the tool and cluster, Aion:Worlds:Catalog
    /// the catalogue to seed a new one with). Without the tool configured the database is assumed to exist.
    /// </summary>
    async Task<bool> DatabaseReadyAsync(Child child, CancellationToken ct)
    {
        var world = child.World;
        if (databases.Contains(world.WorldId)) return true;
        if (configuration["Aion:Backups:Tool"] is not { Length: > 0 } tool) { databases.Add(world.WorldId); return true; }
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(tool);
        start.ArgumentList.Add("create-world");
        start.ArgumentList.Add(world.WorldId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (configuration["Aion:Worlds:Catalog"] is { Length: > 0 } catalog) { start.ArgumentList.Add("--catalog"); start.ArgumentList.Add(catalog); }
        if (configuration["Aion:Backups:PgHome"] is { Length: > 0 } home) start.Environment["AION_PG_HOME"] = home;
        logger.LogInformation("World {World}: preparing its database", world.WorldId);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("create-world did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0)
            {
                databases.Add(world.WorldId);
                logger.LogInformation("World {World}: database {Database} ready", world.WorldId, (await output).Trim());
                return true;
            }
            child.LastError = "database: " + ((await errors).Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"exit {process.ExitCode}");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            child.LastError = "database: " + error.Message;
        }
        child.State = WorldRuntime.Crashed;
        child.Failures++;
        child.NextStart = DateTimeOffset.UtcNow + MaxBackoff;
        logger.LogWarning("World {World}: its database could not be prepared: {Error}", world.WorldId, child.LastError);
        return false;
    }

    /// <summary>A world's connection string: the realm's (ConnectionStrings:Aion) on the database <c>&lt;realm&gt;_world_&lt;id&gt;</c>.</summary>
    public string? WorldConnectionString(int worldId)
    {
        if (configuration.GetConnectionString("Aion") is not { Length: > 0 } realm) return null;
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(realm);
        builder.Database = $"{builder.Database ?? "aion"}_world_{worldId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return builder.ConnectionString;
    }

    void Start(Child child)
    {
        var world = child.World;
        var (file, arguments) = HostCommand();
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // The child inherits this host's configuration (environment, appsettings) and overrides its role and ports.
        var env = start.Environment;
        env["Aion__Role"] = "world";
        env["Aion__Native__WorldId"] = world.WorldId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["Aion__Native__GamePort"] = world.GamePort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["Aion__Native__LoginPort"] = "";
        env["Aion__LocalPort"] = world.InternalPort!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["Aion__WorldProcess__ControlToken"] = ControlToken(world.WorldId);
        env["Aion__WorldProcess__Name"] = world.Name;
        // Its own database only; the realm (accounts, bans, maintenance, the character index, chat) over the realm link.
        env["ConnectionStrings__World"] = WorldConnectionString(world.WorldId) ?? "";
        env["ConnectionStrings__Aion"] = "";
        env["ConnectionStrings__Auth"] = "";
        env.Remove("Aion__BridgeSecret");
        env.Remove("Aion__Totp__EncryptionKey");
        if (configuration.GetValue<int?>("Aion:ChatPort") is { } chatPort)
            env["Aion__Realm__Url"] = $"http://127.0.0.1:{chatPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        env["Aion__Realm__Token"] = LinkToken(world.WorldId);
        // The child's console goes to this host's log (with the world id); it writes no log file of its own.
        env["Aion__ServerLog__Enabled"] = "false";
        // Its HTTP port answers the auth host every few seconds (status, presence): no request logging.
        env["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        if (world.LevelCap is { } cap) env["Aion__Progression__LevelCap"] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, line) => { if (line.Data is { Length: > 0 } text) logger.LogInformation("[world {World}] {Line}", world.WorldId, text); };
            process.ErrorDataReceived += (_, line) => { if (line.Data is { Length: > 0 } text) logger.LogWarning("[world {World}] {Line}", world.WorldId, text); };
            process.Exited += (_, _) => Nudge();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (child.Since is not null) child.Restarts++;
            child.Process = process;
            child.State = WorldRuntime.Starting;
            child.Since = DateTimeOffset.UtcNow;
            child.Status = null;
            child.LastError = null;
            logger.LogInformation("World {World} ({Name}) starting: process {Pid}, game port {Port}", world.WorldId, world.Name, process.Id, world.GamePort);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            child.State = WorldRuntime.Crashed;
            child.LastError = error.Message;
            child.Failures++;
            child.NextStart = DateTimeOffset.UtcNow + MaxBackoff;
            logger.LogWarning("World {World} could not be started: {Error}", world.WorldId, error.Message);
        }
    }

    /// <summary>This host's own command: the apphost, or "dotnet Aion.Host.dll".</summary>
    public static (string File, string[] Arguments) HostCommand()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The host executable is unknown.");
        var assembly = typeof(WorldSupervisor).Assembly.Location;
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? (process, [assembly]) : (process, []);
    }

    async Task StopChildAsync(Child child, string reason)
    {
        var world = child.World;
        if (child.Process is null && !child.Adopted) { child.State = WorldRuntime.Stopped; return; }
        child.State = WorldRuntime.Stopping;
        logger.LogInformation("World {World} ({Name}) stopping: {Reason}", world.WorldId, world.Name, reason);
        try
        {
            if (world.InternalPort is { } port)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{StatusHost(world)}:{port}/internal/world/stop");
                request.Headers.Add("X-World-Control", ControlToken(world.WorldId));
                using var _ = await Http.SendAsync(request);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
        if (child.Process is { } process)
        {
            using var wait = new CancellationTokenSource(StopTimeout);
            try { await process.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException)
            {
                logger.LogWarning("World {World} did not stop within {Timeout}; killing it", world.WorldId, StopTimeout);
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            process.Dispose();
            child.Process = null;
        }
        else
        {
            // Adopted: wait until its status endpoint goes away.
            for (var i = 0; i < StopTimeout.TotalSeconds && await StatusAsync(world, CancellationToken.None) is not null; i++)
                await Task.Delay(1000);
        }
        child.Adopted = false;
        child.Status = null;
        child.State = WorldRuntime.Stopped;
        child.Since = null;
    }

    /// <summary>Where a world server's status and control port is: loopback for a managed one, its host for a remote one.</summary>
    public static string StatusHost(WorldRecord world) => world.IsManaged ? "127.0.0.1" : world.ControlHost ?? world.Host ?? "127.0.0.1";

    static async Task<WorldProcessStatus?> StatusAsync(WorldRecord world, CancellationToken ct)
    {
        if (world.InternalPort is not { } port) return null;
        try { return await Http.GetFromJsonAsync<WorldProcessStatus>($"http://{StatusHost(world)}:{port}/internal/world/status", ct); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }

    static async Task<bool> ProbeAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await client.ConnectAsync(host, port, timeout.Token); return true; }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }
}
