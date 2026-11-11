namespace Aion.Launcher;

/// <summary>
/// Optional client patch pak (z_KiskEmu_Patch-Windows_P.*) produced by KiskEmu Client Editor.
/// The launcher never modifies base paks; it only copies late-mount patch files when the player opts in. Files under the
/// pak's old name (z_A2Core_Patch-Windows_P) are removed from the game copy either way.
/// </summary>
public static class ClientPatchSupport
{
    public const string PatchBaseName = "z_KiskEmu_Patch-Windows_P";
    /// <summary>The pak's name before KiskEmu (client editors before 0.6).</summary>
    public const string LegacyPatchBaseName = "z_A2Core_Patch-Windows_P";
    static readonly string[] Extensions = [".pak", ".sig", ".utoc", ".ucas"];

    public static string PatchSourceFolder => Path.Combine(PrivateFolder.Local, "client-patches", "deploy");

    public static string GamePaksFolder(string installFolder) =>
        Path.Combine(installFolder, "Aion2", "Content", "Paks");

    /// <summary>Copies patch pak/sig/utoc/ucas from the editor deploy folder into the game Paks directory.</summary>
    public static void DeployIfEnabled(string installFolder, bool enabled)
    {
        RemoveLegacy(installFolder);
        if (!enabled) return;
        var source = PatchSourceFolder;
        if (!Directory.Exists(source)) return;
        var target = GamePaksFolder(installFolder);
        Directory.CreateDirectory(target);
        foreach (var ext in Extensions)
        {
            var from = Path.Combine(source, PatchBaseName + ext);
            if (!File.Exists(from)) continue;
            var to = Path.Combine(target, PatchBaseName + ext);
            File.Copy(from, to, overwrite: true);
        }
    }

    public static void RemoveDeployed(string installFolder)
    {
        Remove(installFolder, PatchBaseName);
        RemoveLegacy(installFolder);
    }

    /// <summary>Removes the pak under its old name (z_A2Core_Patch-Windows_P.*) from the game copy.</summary>
    public static void RemoveLegacy(string installFolder) => Remove(installFolder, LegacyPatchBaseName);

    static void Remove(string installFolder, string baseName)
    {
        var target = GamePaksFolder(installFolder);
        if (!Directory.Exists(target)) return;
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(target, baseName + ext);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
