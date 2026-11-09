namespace Aion.World;

/// <summary>
/// Research logging a world server can switch on and off while it runs (staff dashboard, Server page): the map-event debug
/// log (every spawner match, objective and quest step of the map-event scripts, one file per world) and the per-connection
/// client trace (opcode timeline and first bodies, a JSON file per game connection, written every minute and at the end).
/// Startup values come from AION_MAP_EVENT_DEBUG and Aion:Native:TraceDirectory; a switch made on the dashboard lasts until
/// the world server restarts. Thread-safe: game connections read the paths on every use.
/// </summary>
public static class WorldDebug
{
    /// <summary>The map-event log's file name when the dashboard switches it on (inside <see cref="Directory"/>).</summary>
    public const string MapEventFile = "map-events.log";
    /// <summary>The client traces' folder when the dashboard switches them on (inside <see cref="Directory"/>).</summary>
    public const string TraceFolder = "traces";

    static string? directory, mapEventLog, traceDirectory;

    /// <summary>Where the dashboard's switches write; null when this host has no writable debug folder configured.</summary>
    public static string? Directory => Volatile.Read(ref directory);

    /// <summary>The map-event debug log being written now, or null.</summary>
    public static string? MapEventLog => Volatile.Read(ref mapEventLog);

    /// <summary>The folder client traces go to now, or null (new connections only: a trace starts with its connection).</summary>
    public static string? TraceDirectory => Volatile.Read(ref traceDirectory);

    /// <summary>
    /// Startup state. The debug folder is <paramref name="configuredDirectory"/> (Aion:Native:DebugDirectory), else the folder
    /// of a configured map-event log, else the parent of a configured trace folder.
    /// </summary>
    public static void Configure(string? configuredDirectory, string? startMapEventLog, string? startTraceDirectory)
    {
        var start = Blank(startMapEventLog) ? null : Path.GetFullPath(startMapEventLog!);
        var traces = Blank(startTraceDirectory) ? null : Path.GetFullPath(startTraceDirectory!);
        var dir = !Blank(configuredDirectory) ? Path.GetFullPath(configuredDirectory!)
            : start is not null ? Path.GetDirectoryName(start)
            : traces is not null ? Path.GetDirectoryName(traces.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : null;
        Volatile.Write(ref directory, dir);
        Volatile.Write(ref mapEventLog, start);
        Volatile.Write(ref traceDirectory, traces);
    }

    /// <summary>Switches the map-event log on (into the debug folder unless one is already being written) or off.</summary>
    public static bool SetMapEventLog(bool on)
    {
        if (!on) { Volatile.Write(ref mapEventLog, null); return true; }
        if (MapEventLog is not null) return true;
        if (Directory is not { } dir) return false;
        System.IO.Directory.CreateDirectory(dir);
        Volatile.Write(ref mapEventLog, Path.Combine(dir, MapEventFile));
        return true;
    }

    /// <summary>Switches client traces for new connections on (into the debug folder unless already set) or off.</summary>
    public static bool SetTraces(bool on)
    {
        if (!on) { Volatile.Write(ref traceDirectory, null); return true; }
        if (TraceDirectory is not null) return true;
        if (Directory is not { } dir) return false;
        var traces = Path.Combine(dir, TraceFolder);
        System.IO.Directory.CreateDirectory(traces);
        Volatile.Write(ref traceDirectory, traces);
        return true;
    }

    /// <summary>The debug files on disk: the map-event logs (*.log) in the debug folder and the traces (*.json) in its traces folder.</summary>
    public static IReadOnlyList<FileInfo> Files()
    {
        var found = new List<FileInfo>();
        foreach (var (folder, pattern) in Folders())
            if (System.IO.Directory.Exists(folder)) found.AddRange(new DirectoryInfo(folder).GetFiles(pattern));
        return found.DistinctBy(f => f.FullName).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
    }

    /// <summary>A debug file by its bare name (no folders), or null.</summary>
    public static FileInfo? File(string name)
    {
        if (Blank(name) || name != Path.GetFileName(name) || name.StartsWith('.')) return null;
        return Files().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
    }

    static IEnumerable<(string Folder, string Pattern)> Folders()
    {
        if (Directory is { } dir)
        {
            yield return (dir, "*.log");
            yield return (Path.Combine(dir, TraceFolder), "*.json");
        }
        if (MapEventLog is { } log && Path.GetDirectoryName(log) is { } logDir) yield return (logDir, "*.log");
        if (TraceDirectory is { } traces) yield return (traces, "*.json");
    }

    static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);
}
