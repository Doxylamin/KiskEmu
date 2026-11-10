namespace Aion.Host;

/// <summary>
/// The player site and the staff dashboard on separate ports (Aion:Web:StaffPort), so a proxy can give each its own
/// hostname (e.g. world.example.com → the account port, admin.example.com → the staff port).
/// <list type="bullet">
/// <item>Staff port: the dashboard page at "/", its scripts, /api/admin, /api/status and the wardrobe files. Nothing else.</item>
/// <item>Account port: the player site and the player API, the realm link and downloads; no dashboard page, no
/// dashboard scripts, no /api/admin.</item>
/// </list>
/// Without a staff port both stay on the account port as before (the dashboard at Aion:Web:StaffPath).
/// Both pages come from one web build in wwwroot: index.html with assets/index-*, admin.html with assets/admin-*, and
/// shared chunks both pages load.
/// </summary>
public static class WebSites
{
    public const string StaffPage = "admin.html";

    /// <summary>What a request on the staff port may reach.</summary>
    public static bool StaffAllows(PathString path) =>
        path == "/" || path.StartsWithSegments("/api/admin") || path.StartsWithSegments("/api/status")
        || path.StartsWithSegments("/wardrobe") || path.StartsWithSegments("/wardrobe-viewer.js")
        || path.StartsWithSegments("/assets") && !Entry(path, "index");

    /// <summary>What a request on the account port may not reach once the dashboard has its own port.</summary>
    public static bool PlayerRefuses(PathString path) =>
        path.StartsWithSegments("/api/admin") || path.Equals("/" + StaffPage, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/assets") && Entry(path, "admin") || path.StartsWithSegments("/wardrobe")
        || path.StartsWithSegments("/wardrobe-viewer.js");

    /// <summary>A page's own entry files: assets/{page}-HASH.js / .css (Vite names them after the page).</summary>
    static bool Entry(PathString path, string page) =>
        path.Value is { } value && value.StartsWith("/assets/" + page + "-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits the sites by the port a request came in on. Other ports (chat, platform bootstrap, NCGuard) are untouched.
    /// Runs before every other route, so a refused path answers 404 whatever it is.
    /// </summary>
    public static void UseSiteSplit(this WebApplication app, int accountPort, int staffPort, string webRoot)
    {
        var staffPage = Path.Combine(webRoot, StaffPage);
        app.Use(async (context, next) =>
        {
            var local = context.Connection.LocalPort;
            var path = context.Request.Path;
            if (local == staffPort)
            {
                if (!StaffAllows(path)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                if (path == "/" && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
                {
                    if (!File.Exists(staffPage)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.SendFileAsync(staffPage);
                    return;
                }
            }
            else if (local == accountPort && PlayerRefuses(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }
}
