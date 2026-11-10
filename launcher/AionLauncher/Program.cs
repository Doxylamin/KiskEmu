using Aion.Launcher;

namespace AionLauncher;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Elevated helper modes (firewall setup, hosts session, cleanup) run without a window.
        if (Elevated.Run(args) is { } exit) return exit;
        // After a self-update: the previous exe (still closing) and any unfinished download.
        if (Environment.ProcessPath is { } exe) _ = LauncherUpdate.RemoveLeftoversAsync(exe);
        ApplicationConfiguration.Initialize();
        // A launcher.json next to the exe (a server's download ZIP) adds that server; no server is built in.
        LauncherConfig? defaultServer;
        try { defaultServer = LauncherConfig.LoadDefault(Path.Combine(AppContext.BaseDirectory, "launcher.json")); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show("launcher.json is invalid: " + error.Message, Install.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var settings = LauncherSettings.Load();
        if (settings.Migrate(defaultServer, SupportedBuild.Embedded.Fingerprint))
            try { settings.Save(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (Install.Running) Install.RegisterLinks();
        // "--uninstall" is what the Apps & features entry runs; a kisklauncher:// link is what an account site's button opens.
        var link = args.FirstOrDefault(a => a.StartsWith(ServerSetup.LinkScheme + ":", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(settings, uninstall: args is ["--uninstall"], link));
        return 0;
    }
}
