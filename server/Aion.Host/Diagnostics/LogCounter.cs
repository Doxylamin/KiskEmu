namespace Aion.Host.Diagnostics;

/// <summary>
/// Counts this host's warnings and errors per minute for the last hour and keeps the last <see cref="MaxRecent"/> of them
/// (category and message, cut to <see cref="MaxMessage"/> characters), for the staff dashboard's diagnostics card. In memory only.
/// </summary>
public sealed class LogCounter(TimeProvider? clock = null) : ILoggerProvider
{
    public const int Minutes = 60, MaxRecent = 20, MaxMessage = 300;

    public sealed record Recent(DateTimeOffset At, string Level, string Category, string Message);
    public sealed record Counts(int Warnings5m, int Errors5m, int Warnings60m, int Errors60m, IReadOnlyList<Recent> Recent);

    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object sync = new();
    // Per minute since the epoch: the minute, its warnings and its errors (critical counts as an error).
    readonly (long Minute, int Warnings, int Errors)[] buckets = new (long, int, int)[Minutes];
    readonly Queue<Recent> recent = new();

    public void Add(LogLevel level, string category, string message)
    {
        if (level is < LogLevel.Warning or LogLevel.None) return;
        var now = time.GetUtcNow();
        var minute = now.ToUnixTimeSeconds() / 60;
        var error = level >= LogLevel.Error;
        lock (sync)
        {
            ref var bucket = ref buckets[minute % Minutes];
            if (bucket.Minute != minute) bucket = (minute, 0, 0);
            if (error) bucket.Errors++; else bucket.Warnings++;
            recent.Enqueue(new Recent(now, error ? (level == LogLevel.Critical ? "critical" : "error") : "warning", category,
                message.Length > MaxMessage ? message[..MaxMessage] + "…" : message));
            while (recent.Count > MaxRecent) recent.Dequeue();
        }
    }

    public Counts Read()
    {
        var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        int w5 = 0, e5 = 0, w60 = 0, e60 = 0;
        lock (sync)
        {
            foreach (var bucket in buckets)
            {
                var age = minute - bucket.Minute;
                if (age is < 0 or >= Minutes) continue;
                w60 += bucket.Warnings; e60 += bucket.Errors;
                if (age < 5) { w5 += bucket.Warnings; e5 += bucket.Errors; }
            }
            return new Counts(w5, e5, w60, e60, recent.Reverse().ToArray());
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    sealed class Logger(LogCounter owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and not LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += " (" + exception.GetType().Name + ": " + exception.Message + ")";
            owner.Add(logLevel, category, message);
        }
    }
}
