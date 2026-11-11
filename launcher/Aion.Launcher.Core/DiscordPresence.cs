using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aion.Launcher;

/// <summary>The character being played, for the presence's small image and text (when the launcher knows it).</summary>
public sealed record PresenceCharacter(string Name, int Level, string? ClassName);

/// <summary>The server's GET /api/characters/current answer, as far as the Discord status uses it.</summary>
public static class CurrentCharacter
{
    /// <summary>
    /// The character to show: only when the account is in the game and lets others see it (showPlaying, default true when
    /// the server leaves it out). Null for anything else, a malformed answer included.
    /// </summary>
    public static PresenceCharacter? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("inGame", out var inGame) || inGame.ValueKind != JsonValueKind.True) return null;
        if (root.TryGetProperty("showPlaying", out var show) && show.ValueKind == JsonValueKind.False) return null;
        if (!root.TryGetProperty("character", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (!c.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 and <= 64 } text) return null;
        var level = c.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n) && n is > 0 and <= 1000 ? n : 0;
        var className = c.TryGetProperty("className", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 and <= 32 } cls ? cls : null;
        return new PresenceCharacter(text, level, className);
    }
}

/// <summary>
/// What the launcher shows in Discord: the server in use (its name and account site come from the saved server, never
/// from the launcher itself), and while the game runs, since when and (when known) the character.
/// </summary>
public sealed record PresenceState(string? ServerName, string? SiteUrl, DateTimeOffset? PlayingSince = null, PresenceCharacter? Character = null);

/// <summary>
/// The Rich Presence activity for a <see cref="PresenceState"/> (Discord's SET_ACTIVITY "activity" object). Images are
/// the asset keys uploaded to the Discord application: the logo, and one per class.
/// </summary>
public static class DiscordActivity
{
    /// <summary>KiskLauncher's Discord application (public). launcher-settings.json "DiscordApplicationId" overrides it.</summary>
    public const string DefaultApplicationId = "1570040413790801941";
    public const string LogoAsset = "kisklauncher";
    public const string ButtonLabel = "Server website";

    static readonly string[] ClassAssets = ["assassin", "chanter", "cleric", "elementalist", "fighter", "gladiator", "ranger", "sorcerer", "templar"];
    /// <summary>Other names a class is known by.</summary>
    static readonly Dictionary<string, string> ClassAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spiritmaster"] = "elementalist", ["warrior"] = "fighter",
    };

    /// <summary>The class's image asset key; null for no class or one without an image.</summary>
    public static string? ClassAsset(string? className)
    {
        var name = className?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (ClassAliases.TryGetValue(name, out var alias)) return alias;
        return ClassAssets.FirstOrDefault(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The URL for a button: only an absolute https URL (Discord's buttons take no other), else null.</summary>
    public static string? ButtonUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri : null;

    /// <summary>"Naicha · Cleric · Lv 38" (the class only when known).</summary>
    public static string CharacterText(PresenceCharacter character)
    {
        var parts = new List<string> { character.Name.Trim() };
        if (character.ClassName?.Trim() is { Length: > 0 } className) parts.Add(char.ToUpperInvariant(className[0]) + className[1..]);
        if (character.Level > 0) parts.Add($"Lv {character.Level}");
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    public static JsonObject Build(PresenceState state)
    {
        var server = state.ServerName?.Trim() is { Length: > 0 } name ? name : null;
        string details;
        string? text;
        if (state.PlayingSince is null)
        {
            details = "In the launcher";
            text = server;
        }
        else
        {
            details = server is null ? "Playing AION2" : $"Playing on {server}";
            text = state.Character is { } character && CharacterText(character) is { Length: > 0 } line ? line : "In game";
        }

        var activity = new JsonObject { ["details"] = Field(details) };
        if (text is not null) activity["state"] = Field(text);
        if (state.PlayingSince is { } since) activity["timestamps"] = new JsonObject { ["start"] = since.ToUnixTimeMilliseconds() };

        var assets = new JsonObject { ["large_image"] = LogoAsset, ["large_text"] = Field(server ?? "KiskLauncher") };
        if (state.PlayingSince is not null && state.Character is { } played && ClassAsset(played.ClassName) is { } classAsset)
        {
            assets["small_image"] = classAsset;
            assets["small_text"] = Field(CharacterText(played));
        }
        activity["assets"] = assets;

        if (ButtonUrl(state.SiteUrl) is { } url)
            activity["buttons"] = new JsonArray(new JsonObject { ["label"] = ButtonLabel, ["url"] = url });
        return activity;
    }

    /// <summary>Discord's text fields take 2 to 128 characters.</summary>
    static string Field(string text)
    {
        text = text.Trim();
        if (text.Length > 128) text = text[..127] + "…";
        return text.Length < 2 ? text.PadRight(2) : text;
    }
}

/// <summary>
/// Rich Presence over Discord's local IPC (the named pipe discord-ipc-0..9): a handshake with the application id, then
/// SET_ACTIVITY. Everything runs on one background task: when Discord is not running nothing fails, the launcher just tries
/// again every <see cref="RetryInterval"/>. <see cref="Dispose"/> clears the presence (Discord also drops it when the pipe
/// closes).
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(150), ReplyTimeout = TimeSpan.FromSeconds(3);
    const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

    readonly string applicationId, pipePrefix;
    readonly SemaphoreSlim wake = new(0);
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    readonly object gate = new();
    PresenceState? wanted;
    int version, sent = -1;
    NamedPipeClientStream? pipe;
    DateTimeOffset nextAttempt = DateTimeOffset.MinValue;
    volatile bool retryNow;

    /// <param name="pipePrefix">The pipe names without the 0-9 suffix (another value only for tests).</param>
    public DiscordPresence(string? applicationId = null, string pipePrefix = "discord-ipc-")
    {
        this.applicationId = applicationId?.Trim() is { Length: > 0 } id ? id : DiscordActivity.DefaultApplicationId;
        this.pipePrefix = pipePrefix;
        worker = Task.Run(RunAsync);
    }

    /// <summary>True while connected to Discord.</summary>
    public bool Connected => pipe is { IsConnected: true };

    /// <summary>Shows this state (null clears the presence). Returns at once; the background task sends it.</summary>
    public void Update(PresenceState? state)
    {
        lock (gate)
        {
            if (Equals(wanted, state) && version > 0) return;
            wanted = state;
            version++;
        }
        // A new state is worth a connection attempt even before the retry interval has passed.
        retryNow = true;
        Wake();
    }

    void Wake() { try { wake.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { } }

    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try { await wake.WaitAsync(RetryInterval, stop.Token); }
            catch (OperationCanceledException) { break; }
            try { await SyncAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception) { Disconnect(); }
        }
        // Clear what was shown (only when connected; never connects just for this).
        if (Connected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SetActivityAsync(null, timeout.Token);
            }
            catch (Exception) { }
        }
        Disconnect();
    }

    async Task SyncAsync(CancellationToken ct)
    {
        PresenceState? state;
        int current;
        lock (gate) { state = wanted; current = version; }
        if (current == 0 || current == sent && Connected) return;
        if (!Connected)
        {
            if (!retryNow && DateTimeOffset.UtcNow < nextAttempt) return;
            retryNow = false;
            nextAttempt = DateTimeOffset.UtcNow + RetryInterval;
            sent = -1;
            if (!await ConnectAsync(ct)) return;
        }
        await SetActivityAsync(state is null ? null : DiscordActivity.Build(state), ct);
        sent = current;
    }

    async Task<bool> ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var candidate = new NamedPipeClientStream(".", pipePrefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
                await WriteAsync(candidate, OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = applicationId }, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReplyTimeout);
                var (op, reply) = await ReadAsync(candidate, timeout.Token);
                if (op == OpFrame && reply?["evt"]?.GetValue<string>() == "READY") { pipe = candidate; return true; }
                candidate.Dispose();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { candidate.Dispose(); throw; }
            catch (Exception) { candidate.Dispose(); }
        }
        return false;
    }

    async Task SetActivityAsync(JsonObject? activity, CancellationToken ct)
    {
        var connection = pipe ?? throw new IOException("not connected");
        var nonce = Guid.NewGuid().ToString();
        var args = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity };
        await WriteAsync(connection, OpFrame, new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = nonce }, ct);
        // Read the answer (so the pipe never fills up); pings are answered, a close ends the connection.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        while (true)
        {
            var (op, frame) = await ReadAsync(connection, timeout.Token);
            if (op == OpPing) { await WriteAsync(connection, OpPong, frame ?? new JsonObject(), ct); continue; }
            if (op == OpClose) throw new IOException("Discord closed the connection");
            if (op == OpFrame && frame?["nonce"]?.GetValue<string>() == nonce) return;
        }
    }

    static async Task WriteAsync(Stream stream, int op, JsonNode payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    static async Task<(int Op, JsonNode? Payload)> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1 << 20) throw new InvalidDataException("Discord frame too large");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        try { return (op, length == 0 ? null : JsonNode.Parse(body)); }
        catch (JsonException) { return (op, null); }
    }

    void Disconnect()
    {
        pipe?.Dispose();
        pipe = null;
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        try { worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        // A worker still busy past the wait keeps its token and signal (they are only garbage then).
        if (!worker.IsCompleted) return;
        stop.Dispose();
        wake.Dispose();
    }
}
