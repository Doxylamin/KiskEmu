namespace Aion.Launcher;

/// <summary>
/// The launcher's own folder, KiskPrivate: settings, build references, the client editor's patches, and the private game
/// copies (under %LOCALAPPDATA%, or &lt;drive&gt;:\KiskPrivate on the drive of their source). Launchers before 0.6 named it
/// A2CorePrivate. An existing A2CorePrivate is renamed in place (on the same drive, so it is instant and the game copy's
/// hard links to the Steam install stay). While that is not possible (the old folder in use, the game running from it),
/// the old folder is used as it is and the move is tried again at the next start.
/// </summary>
public static class PrivateFolder
{
    public const string Name = "KiskPrivate";
    public const string LegacyName = "A2CorePrivate";

    static readonly Lazy<string> local = new(() => Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    /// <summary>%LOCALAPPDATA%\KiskPrivate, after moving an old A2CorePrivate there (else that one while it cannot move).</summary>
    public static string Local => local.Value;

    /// <summary>
    /// <paramref name="parent"/>\KiskPrivate, after renaming <paramref name="parent"/>\A2CorePrivate to it when only the old
    /// one exists. The old folder when the rename failed (in use); the new one when neither exists yet.
    /// </summary>
    public static string Resolve(string parent)
    {
        var current = Path.Combine(parent, Name);
        var legacy = Path.Combine(parent, LegacyName);
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
        try
        {
            Directory.Move(legacy, current);
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return legacy; }
    }

    /// <summary>
    /// A stored path under an old A2CorePrivate folder (a game copy, an interrupted download), moved with its folder: the
    /// path under KiskPrivate once that folder is renamed (it is renamed here when it can be), else the path unchanged.
    /// Paths elsewhere come back unchanged.
    /// </summary>
    public static string? Rebase(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        var parts = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(parts, p => p.Equals(LegacyName, StringComparison.OrdinalIgnoreCase));
        if (at <= 0) return path;
        var parent = string.Join(Path.DirectorySeparatorChar, parts[..at]);
        if (parent.EndsWith(':')) parent += Path.DirectorySeparatorChar;
        var resolved = Resolve(parent);
        if (!Path.GetFileName(resolved).Equals(Name, StringComparison.OrdinalIgnoreCase)) return path;
        return Path.Combine([resolved, .. parts[(at + 1)..]]);
    }
}
