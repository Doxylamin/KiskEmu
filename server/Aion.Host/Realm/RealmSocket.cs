using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Host.Realm;

// The realm socket (server/REALM_LINK.md): one WebSocket per world server, dialled by the world, carrying requests in both
// directions (the existing HTTP routes, tunnelled) and one-way events. HTTP stays the fallback.

/// <summary>One message on the realm socket.</summary>
public sealed record LinkFrame
{
    public const string Request = "req", Response = "res", Event = "evt", Ping = "ping", Pong = "pong";

    [JsonPropertyName("t")] public string Type { get; init; } = "";
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("m")] public string? Method { get; init; }
    [JsonPropertyName("p")] public string? Path { get; init; }
    [JsonPropertyName("h")] public Dictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("b")] public byte[]? Body { get; init; }
    [JsonPropertyName("s")] public int Status { get; init; }
    [JsonPropertyName("e")] public string? Name { get; init; }
    [JsonPropertyName("d")] public JsonElement? Data { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public static LinkFrame Of(string name, object? data) =>
        new() { Type = Event, Name = name, Data = JsonSerializer.SerializeToElement(data ?? new { }, Json) };

    public T? DataAs<T>() => Data is { } data ? data.Deserialize<T>(Json) : default;
}

/// <summary>Heartbeat, timeouts and reconnect backoff of the realm socket (shorter in tests).</summary>
public sealed record LinkTiming
{
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan Silence { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan Request { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Connect { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReconnectBase { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PresenceEvery { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan StatusEvery { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxMessage { get; init; } = 32 << 20;

    public static readonly LinkTiming Default = new();

    /// <summary>The wait before reconnect attempt <paramref name="attempt"/> (1, 2, …): doubling up to the maximum, ±20 %.</summary>
    public TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(ReconnectMax.TotalSeconds, ReconnectBase.TotalSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 30)));
        return TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * Random.Shared.NextDouble()));
    }
}

/// <summary>Counters of one side of the realm socket (the auth host's are on /api/admin/worlds).</summary>
public sealed class LinkCounters
{
    public long Connects, Reconnects, Disconnects, Refused, Tunnelled, Fallbacks, EventsSent, EventsReceived, HeartbeatTimeouts;

    public object Snapshot() => new
    {
        connects = Interlocked.Read(ref Connects), reconnects = Interlocked.Read(ref Reconnects), disconnects = Interlocked.Read(ref Disconnects),
        refused = Interlocked.Read(ref Refused), tunnelled = Interlocked.Read(ref Tunnelled), fallbacks = Interlocked.Read(ref Fallbacks),
        eventsSent = Interlocked.Read(ref EventsSent), eventsReceived = Interlocked.Read(ref EventsReceived),
        heartbeatTimeouts = Interlocked.Read(ref HeartbeatTimeouts),
    };
}

/// <summary>The socket closed before the call was answered. <see cref="Sent"/>: the request may have reached the other side.</summary>
public sealed class LinkClosedException(string message, bool sent) : Exception(message)
{
    public bool Sent { get; } = sent;
}

/// <summary>
/// One open realm socket: sends requests and waits for their answers, answers the other side's requests, delivers events,
/// and keeps the heartbeat. Every waiting call fails at once when it closes.
/// </summary>
public sealed class LinkChannel
{
    readonly WebSocket socket;
    readonly LinkTiming timing;
    readonly ILogger logger;
    readonly LinkCounters counters;
    readonly SemaphoreSlim sending = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<LinkFrame>> pending = new();
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long nextId;
    long heardAt = Environment.TickCount64;
    string? reason;

    public LinkChannel(WebSocket socket, int worldId, LinkTiming timing, LinkCounters counters, ILogger logger)
    {
        this.socket = socket;
        this.timing = timing;
        this.counters = counters;
        this.logger = logger;
        WorldId = worldId;
    }

    public int WorldId { get; }
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;
    public bool IsOpen => !stop.IsCancellationRequested && socket.State == WebSocketState.Open;
    /// <summary>Completes when the channel has closed (after every waiting call failed).</summary>
    public Task Closed => closed.Task;
    public string? CloseReason => reason;

    /// <summary>Answers a request of the other side (the HTTP tunnel); null answers 404.</summary>
    public Func<LinkFrame, CancellationToken, Task<LinkFrame>>? OnRequest { get; set; }
    public Func<LinkFrame, Task>? OnEvent { get; set; }

    /// <summary>Receives until the socket closes, the heartbeat times out, <see cref="Abort"/> or <paramref name="ct"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var heartbeat = HeartbeatAsync(linked.Token);
        try { await ReceiveAsync(linked.Token); reason ??= "closed by the other side"; }
        catch (OperationCanceledException) { reason ??= ct.IsCancellationRequested ? "shutting down" : "aborted"; }
        catch (Exception error) when (error is WebSocketException or JsonException or InvalidDataException or IOException)
        { reason ??= error.Message; }
        finally
        {
            stop.Cancel();
            foreach (var id in pending.Keys)
                if (pending.TryRemove(id, out var waiting))
                    waiting.TrySetException(new LinkClosedException($"The realm socket closed ({reason}).", sent: true));
            if (socket.State == WebSocketState.Open)
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, bye.Token); }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { socket.Abort(); }
            }
            else socket.Abort();
            try { await heartbeat; } catch (OperationCanceledException) { }
            closed.TrySetResult();
        }
    }

    /// <summary>Closes the channel now (a replaced socket, a test); waiting calls fail.</summary>
    public void Abort(string why)
    {
        reason ??= why;
        stop.Cancel();
        socket.Abort();
    }

    async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (message.Length + part.Count > timing.MaxMessage) throw new InvalidDataException("realm socket message too large");
                message.Write(buffer, 0, part.Count);
            }
            while (!part.EndOfMessage);
            Interlocked.Exchange(ref heardAt, Environment.TickCount64);
            var frame = JsonSerializer.Deserialize<LinkFrame>(message.GetBuffer().AsSpan(0, (int)message.Length), LinkFrame.Json)
                ?? throw new InvalidDataException("empty realm socket frame");
            switch (frame.Type)
            {
                case LinkFrame.Response:
                    if (pending.TryRemove(frame.Id, out var waiting)) waiting.TrySetResult(frame);
                    break;
                case LinkFrame.Request:
                    _ = AnswerAsync(frame, ct);
                    break;
                case LinkFrame.Event:
                    Interlocked.Increment(ref counters.EventsReceived);
                    _ = DeliverAsync(frame);
                    break;
                case LinkFrame.Ping:
                    _ = TrySendAsync(new LinkFrame { Type = LinkFrame.Pong }, ct);
                    break;
            }
        }
    }

    async Task AnswerAsync(LinkFrame request, CancellationToken ct)
    {
        LinkFrame answer;
        try { answer = OnRequest is { } handler ? await handler(request, ct) : new LinkFrame { Status = 404 }; }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Realm socket (world {World}): {Method} {Path} failed: {Error}", WorldId, request.Method, request.Path, error.Message);
            answer = new LinkFrame { Status = 502 };
        }
        catch (OperationCanceledException) { return; }
        await TrySendAsync(answer with { Type = LinkFrame.Response, Id = request.Id }, ct);
    }

    async Task DeliverAsync(LinkFrame frame)
    {
        if (OnEvent is not { } handler) return;
        try { await handler(frame); }
        catch (Exception error) { logger.LogWarning("Realm socket (world {World}): event {Event} failed: {Error}", WorldId, frame.Name, error.Message); }
    }

    async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(timing.Heartbeat, ct);
            var silent = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref heardAt));
            if (silent > timing.Silence)
            {
                Interlocked.Increment(ref counters.HeartbeatTimeouts);
                logger.LogWarning("Realm socket (world {World}): nothing heard for {Seconds:0} s; closing it", WorldId, silent.TotalSeconds);
                Abort("heartbeat timeout");
                return;
            }
            await TrySendAsync(new LinkFrame { Type = LinkFrame.Ping }, ct);
        }
    }

    async Task<bool> TrySendAsync(LinkFrame frame, CancellationToken ct)
    {
        try { await SendAsync(frame, ct); return true; }
        catch (LinkClosedException) { return false; }
    }

    async Task SendAsync(LinkFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, LinkFrame.Json);
        if (bytes.Length > timing.MaxMessage) throw new InvalidDataException("realm socket message too large");
        try
        {
            await sending.WaitAsync(ct);
            try
            {
                if (!IsOpen) throw new LinkClosedException("The realm socket is closed.", sent: false);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally { sending.Release(); }
        }
        catch (Exception error) when (error is WebSocketException or ObjectDisposedException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new LinkClosedException("The realm socket closed while sending.", sent: false);
        }
    }

    /// <summary>Sends a request and waits for its answer; <see cref="LinkClosedException"/> when the socket closes first.</summary>
    public async Task<LinkFrame> RequestAsync(LinkFrame request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextId);
        var answer = new TaskCompletionSource<LinkFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            if (!IsOpen) throw new LinkClosedException("The realm socket is closed.", sent: false);
            await SendAsync(request with { Type = LinkFrame.Request, Id = id }, ct);
            return await answer.Task.WaitAsync(timing.Request, ct);
        }
        catch (TimeoutException)
        {
            throw new LinkClosedException($"No answer on the realm socket within {timing.Request.TotalSeconds:0} s.", sent: true);
        }
        finally { pending.TryRemove(id, out _); }
    }

    /// <summary>Sends a one-way event; false when the socket is closed.</summary>
    public async Task<bool> PublishAsync(string name, object? data, CancellationToken ct = default)
    {
        var sent = await TrySendAsync(LinkFrame.Of(name, data), ct);
        if (sent) Interlocked.Increment(ref counters.EventsSent);
        return sent;
    }
}

/// <summary>
/// An <see cref="HttpClient"/> handler that sends a request over the realm socket when one is open for it, and over HTTP
/// otherwise (and when the socket closes before the request was written, or for a call safe to repeat).
/// </summary>
public sealed class LinkTunnelHandler(HttpMessageHandler http, Func<HttpRequestMessage, LinkChannel?> channelFor, LinkCounters counters, ILogger logger)
    : DelegatingHandler(http)
{
    /// <summary>Request option: the world a <see cref="Worlds.WorldLinks"/> request is for.</summary>
    public static readonly HttpRequestOptionsKey<int> World = new("aion-link-world");
    /// <summary>Request option: a POST that may run twice (sent again over HTTP when the socket closed during it).</summary>
    public static readonly HttpRequestOptionsKey<bool> RetrySafe = new("aion-link-retry");

    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Connection", "Transfer-Encoding" };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (channelFor(request) is not { IsOpen: true } channel || request.RequestUri is not { } uri)
        {
            Interlocked.Increment(ref counters.Fallbacks);
            return await base.SendAsync(request, cancellationToken);
        }
        byte[]? body = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
            if (!Skipped.Contains(name)) headers[name] = string.Join(", ", values);
        if (request.Content is { } content)
        {
            body = await content.ReadAsByteArrayAsync(cancellationToken);
            foreach (var (name, values) in content.Headers)
                if (!Skipped.Contains(name)) headers[name] = string.Join(", ", values);
            // Read once: the HTTP fallback sends the same bytes.
            var copy = new ByteArrayContent(body);
            foreach (var (name, values) in content.Headers) copy.Headers.TryAddWithoutValidation(name, values);
            request.Content = copy;
        }
        try
        {
            var answer = await channel.RequestAsync(new LinkFrame { Method = request.Method.Method, Path = uri.PathAndQuery, Headers = headers, Body = body },
                cancellationToken);
            Interlocked.Increment(ref counters.Tunnelled);
            return Response(answer, request);
        }
        catch (LinkClosedException error) when (!error.Sent || Repeatable(request))
        {
            Interlocked.Increment(ref counters.Fallbacks);
            logger.LogInformation("Realm socket (world {World}): {Method} {Path} sent over HTTP ({Error})", channel.WorldId, request.Method, uri.AbsolutePath, error.Message);
            return await base.SendAsync(request, cancellationToken);
        }
        catch (LinkClosedException error)
        {
            throw new HttpRequestException($"The realm socket closed during {request.Method} {uri.AbsolutePath}: {error.Message}", error);
        }
    }

    static bool Repeatable(HttpRequestMessage request) =>
        request.Method != HttpMethod.Post && request.Method != HttpMethod.Patch
        || request.Options.TryGetValue(RetrySafe, out var safe) && safe;

    static HttpResponseMessage Response(LinkFrame answer, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage((HttpStatusCode)(answer.Status is >= 100 and <= 599 ? answer.Status : 502))
        { RequestMessage = request, Content = new ByteArrayContent(answer.Body ?? []) };
        foreach (var (name, value) in answer.Headers ?? [])
            if (!Skipped.Contains(name) && !response.Headers.TryAddWithoutValidation(name, value))
                response.Content.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

/// <summary>
/// Answers tunnelled requests by replaying them against this process's own HTTP port (the same routes, filters and checks
/// as a direct call): only the allowed paths, with some headers set by the receiver.
/// </summary>
public sealed class LinkDispatcher : IDisposable
{
    public const string Marker = "X-Realm-Link";
    static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Connection", "Transfer-Encoding", "Upgrade", "Keep-Alive" };
    readonly HttpClient http;
    readonly Uri self;
    readonly Func<string, bool> allowed;
    readonly IReadOnlyDictionary<string, string> forced;

    public LinkDispatcher(Uri self, Func<string, bool> allowed, IReadOnlyDictionary<string, string> forced, byte[]? certificateSha256 = null)
    {
        this.self = self;
        this.allowed = allowed;
        this.forced = forced;
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false, UseCookies = false };
        // This process's own TLS port: its certificate is for its public name, not the local address it is reached on here.
        if (certificateSha256 is { } pin)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pin);
        else if (self.Scheme == Uri.UriSchemeHttps)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>The request's path is one this side answers: under one of <paramref name="prefixes"/>, no host of its own.</summary>
    public static Func<string, bool> Under(params string[] prefixes) => path =>
        path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\')
        && prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

    public async Task<LinkFrame> DispatchAsync(LinkFrame request, CancellationToken ct)
    {
        if (request.Path is not { } path || !allowed(path) || request.Method is not { Length: > 0 and <= 10 } method)
            return new LinkFrame { Status = 403 };
        var target = new Uri(self, path);
        if (target.Authority != self.Authority) return new LinkFrame { Status = 403 };
        using var message = new HttpRequestMessage(new HttpMethod(method), target);
        if (request.Body is { } body) message.Content = new ByteArrayContent(body);
        foreach (var (name, value) in request.Headers ?? [])
        {
            if (Hop.Contains(name) || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || forced.ContainsKey(name)) continue;
            if (!message.Headers.TryAddWithoutValidation(name, value)) message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        foreach (var (name, value) in forced) message.Headers.TryAddWithoutValidation(name, value);
        message.Headers.TryAddWithoutValidation(Marker, "socket");
        try
        {
            using var response = await http.SendAsync(message, ct);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                if (!Hop.Contains(name)) headers[name] = string.Join(", ", values);
            return new LinkFrame { Status = (int)response.StatusCode, Headers = headers, Body = await response.Content.ReadAsByteArrayAsync(ct) };
        }
        catch (HttpRequestException) { return new LinkFrame { Status = 502 }; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new LinkFrame { Status = 504 }; }
    }

    public void Dispose() => http.Dispose();
}
