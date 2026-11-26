using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Microsoft.AspNetCore.Http.Features;

namespace Aion.Host.Authentication;

/// <summary>
/// Bug reports ("/bug" in chat, docs/BUG_REPORTS.md) on the staff dashboard, from the realm: the list (newest first, by status,
/// map, quest or world), one report with its whole context as JSON, the same as a Markdown brief for an AI session, its
/// screenshot, and its status and note (HandleTickets; audited as staff_bug_report). Players attach a screenshot to their
/// character's latest report through /api/bug-reports/screenshot (the launcher).
/// </summary>
public static partial class StaffToolsEndpoints
{
    sealed record BugReportUpdate(string? Status, string? Note);

    /// <summary>The realm's (auth host's) bug report endpoints: the staff ones and the player's screenshot upload.</summary>
    public static void MapBugReportEndpoints(this WebApplication app)
    {
        MapBugReports(AdminGroup(app));
        app.MapBugReportPlayerEndpoints();
    }

    // "?worldId=" filters by world: "?world=" would send the request to that world server (the auth host's world proxy).
    static void MapBugReports(RouteGroupBuilder staff)
    {
        staff.MapGet("/bug-reports", async (string? status, int? map, string? quest, int? worldId, long? before, int? limit, HttpContext ctx,
            IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<IBugReports>() is not { } reports) return Results.BadRequest(new { error = "realm_only" });
            return Results.Ok(await reports.ListAsync(a, new BugReportFilter(status, map, quest, worldId, before, limit ?? 100), ct));
        });
        staff.MapGet("/bug-reports/{id:long}", async (long id, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<IBugReports>() is not { } reports) return Results.BadRequest(new { error = "realm_only" });
            return await reports.GetAsync(a, id, ct) is { } report ? Results.Ok(new { report = report.Summary, context = report.Context }) : Results.NotFound();
        });
        // The brief an AI session reads (curl -H "Authorization: Bearer <staff token>" .../markdown): everything in one text.
        staff.MapGet("/bug-reports/{id:long}/markdown", async (long id, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<IBugReports>() is not { } reports) return Results.BadRequest(new { error = "realm_only" });
            return await reports.GetAsync(a, id, ct) is { } report
                ? Results.Text(BugReportMarkdown.Build(report, $"/api/admin/bug-reports/{id}"), "text/markdown; charset=utf-8") : Results.NotFound();
        });
        staff.MapGet("/bug-reports/{id:long}/screenshot", async (long id, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<IBugReports>() is not { } reports) return Results.BadRequest(new { error = "realm_only" });
            return await reports.ScreenshotAsync(a, id, ct) is { } shot ? Results.File(shot.Data, shot.ContentType) : Results.NotFound();
        });
        staff.MapPut("/bug-reports/{id:long}", async (long id, BugReportUpdate input, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services,
            CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<IBugReports>() is not { } reports) return Results.BadRequest(new { error = "realm_only" });
            return await reports.UpdateAsync(a, id, input.Status ?? "", input.Note ?? "", ct) ? Results.NoContent() : Results.NotFound();
        });
    }

    /// <summary>
    /// The player's screenshot for a character's latest bug report (made in the last 30 minutes): POST the image as the body
    /// (image/png, image/jpeg or image/webp, at most 8 MB) to /api/bug-reports/screenshot?character=&lt;id&gt; with the account's
    /// sign-in. Answers the report's id, or 404 when that character has no recent report.
    /// </summary>
    static void MapBugReportPlayerEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/bug-reports").RequireRateLimiting("account-entry");
        api.MapPost("/screenshot", async (Guid character, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<IBugReports>() is not { } reports) return Results.BadRequest(new { error = "realm_only" });
            var type = (ctx.Request.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            if (!BugReportRules.ScreenshotTypes.Contains(type)) return Results.BadRequest(new { error = "invalid_screenshot" });
            if (ctx.Request.ContentLength is > BugReportRules.MaxScreenshotBytes) return Results.StatusCode(413);
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = BugReportRules.MaxScreenshotBytes;
            using var body = new MemoryStream();
            try { await ctx.Request.Body.CopyToAsync(body, ct); }
            catch (BadHttpRequestException) { return Results.StatusCode(413); }
            try
            {
                return await reports.AttachScreenshotAsync(a.AccountId, character, new BugScreenshot(type, body.ToArray()), ct) is { } id
                    ? Results.Ok(new { id }) : Results.NotFound(new { error = "no_recent_bug_report" });
            }
            catch (GameplayException error) { return Results.BadRequest(new { error = error.Code }); }
        });
    }
}
