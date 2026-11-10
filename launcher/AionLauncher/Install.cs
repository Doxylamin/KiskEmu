using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

#pragma warning disable SYSLIB1096   // the classic COM interop for IShellLink is what single-file publishing supports

namespace AionLauncher;

/// <summary>
/// The exe installing itself for the current Windows user, no administrator rights: a copy in the
/// local programs folder, shortcuts on the desktop and in the Start menu, and an Apps &amp; features
/// entry that runs the uninstall. The downloaded file and the installed copy are the same exe, so
/// the self-update keeps working on the installed one.
/// </summary>
static class Install
{
    public const string Name = "KiskLauncher";
    public const string FileName = Name + ".exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KiskLauncher";
    /// <summary>kisklauncher://add?url=... links (an account site's "Play with KiskLauncher" button) open the installed exe.</summary>
    const string LinkKey = @"Software\Classes\" + Aion.Launcher.ServerSetup.LinkScheme;
    // The install before the rename (A2Core Launcher). An installed copy updates itself in place, so after the update it
    // still runs from there; it then installs itself under the new name and removes the old folder, shortcuts and entry.
    const string LegacyName = "A2Core Launcher";
    const string LegacyUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\A2CoreLauncher";

    public static string Folder { get; } = ProgramsFolder(Name);
    public static string ExePath => Path.Combine(Folder, FileName);
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
    static string LegacyFolder => ProgramsFolder(LegacyName);
    static string LegacyExePath => Path.Combine(LegacyFolder, LegacyName + ".exe");
    static string ProgramsFolder(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name);

    /// <summary>This process runs from the installed location.</summary>
    public static bool Running => IsProcess(ExePath);

    /// <summary>This process runs from the old A2Core Launcher install: it moves itself to the new one first.</summary>
    public static bool RunningLegacy => IsProcess(LegacyExePath);

    static bool IsProcess(string path) =>
        Environment.ProcessPath is { } exe && string.Equals(Path.GetFullPath(exe), path, StringComparison.OrdinalIgnoreCase);

    public static bool Present => File.Exists(ExePath);

    /// <summary>Copies this exe into place, makes the shortcuts and the Apps &amp; features entry. Returns the installed exe.</summary>
    public static string Run()
    {
        var source = Environment.ProcessPath ?? throw new LauncherException("The running program's path is unknown.");
        Directory.CreateDirectory(Folder);
        var staged = ExePath + ".new";
        File.Copy(source, staged, overwrite: true);
        try { File.Move(staged, ExePath, overwrite: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            File.Delete(staged);
            throw new LauncherException($"{Name} is already running from {Folder}. Close it and try again.");
        }
        try { Shortcut(DesktopShortcut); Shortcut(StartMenuShortcut); }
        catch (COMException error) { throw new LauncherException($"The shortcuts could not be made ({error.Message})."); }
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", Name);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", Name);
        key.SetValue("InstallLocation", Folder);
        key.SetValue("DisplayIcon", ExePath);
        key.SetValue("UninstallString", $"\"{ExePath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(ExePath).Length / 1024), RegistryValueKind.DWord);
        RegisterLinks();
        RemoveLegacy();
        return ExePath;
    }

    /// <summary>
    /// The old A2Core Launcher install, once the new one is in place: its shortcuts, Apps &amp; features entry and folder
    /// (after this exe exits when it runs from there). Best effort: a leftover never stops the new install.
    /// </summary>
    static void RemoveLegacy()
    {
        try
        {
            foreach (var shortcut in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LegacyName + ".lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LegacyName + ".lnk") })
                if (File.Exists(shortcut)) File.Delete(shortcut);
            Registry.CurrentUser.DeleteSubKeyTree(LegacyUninstallKey, throwOnMissingSubKey: false);
            if (!Directory.Exists(LegacyFolder)) return;
            if (RunningLegacy)
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{LegacyFolder}\"")
                    { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            else
                Directory.Delete(LegacyFolder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>
    /// Makes kisklauncher:// links open the installed exe (current user only). Also run on every start of the installed
    /// exe, so installs from before the links get them after their self-update.
    /// </summary>
    public static void RegisterLinks()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LinkKey);
            key.SetValue("", "URL:" + Name);
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{ExePath}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{ExePath}\" \"%1\"");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    public static string Version => Application.ProductVersion.Split('+')[0];

    /// <summary>
    /// Removes the shortcuts and the Apps &amp; features entry, and the installed folder: at once
    /// when this exe runs elsewhere, else a moment after this exe has exited.
    /// </summary>
    public static void Remove()
    {
        foreach (var shortcut in new[] { DesktopShortcut, StartMenuShortcut })
            if (File.Exists(shortcut)) File.Delete(shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(LinkKey, throwOnMissingSubKey: false);
        if (!Directory.Exists(Folder)) return;
        if (Running)
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{Folder}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        else
            Directory.Delete(Folder, recursive: true);
    }

    static void Shortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(ExePath);
        link.SetWorkingDirectory(Folder);
        link.SetDescription(Name);
        link.SetIconLocation(ExePath, 0);
        ((IPersistFile)link).Save(path, false);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int count, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
