using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>
/// The dashboard's NPCs page: the realm's custom NPCs (staff read, admins edit; auth host) and the export of their texts
/// for the client tool that builds the patch pak (docs/CUSTOM_NPCS.md).
/// </summary>
public static partial class StaffToolsEndpoints
{
    sealed record CustomNpcInput(string? Slug, int BaseTemplateId, Dictionary<string, string>? Name, CustomNpcPlacement[]? Placements,
        CustomNpcNode[]? Nodes, bool Enabled);

    static CustomNpc ToCustomNpc(int id, CustomNpcInput input) => new(id, (input.Slug ?? "").Trim(), input.BaseTemplateId,
        Trimmed(input.Name), input.Placements ?? [],
        (input.Nodes ?? []).Select(n => n with
        {
            Key = (n.Key ?? "").Trim(),
            Lines = (n.Lines ?? []).Select(l => (IReadOnlyDictionary<string, string>)Trimmed(l)).ToList(),
            Actions = n.Actions ?? [],
        }).ToList(),
        input.Enabled, DateTimeOffset.UtcNow);

    static Dictionary<string, string> Trimmed(IReadOnlyDictionary<string, string>? texts) =>
        (texts ?? new Dictionary<string, string>()).Where(t => !string.IsNullOrWhiteSpace(t.Value))
            .ToDictionary(t => t.Key.Trim(), t => t.Value.Trim());

    /// <summary>The base template must exist and be one players talk to, not fight (the server places it as is).</summary>
    static string? BaseTemplateError(int baseTemplateId) =>
        GameData.Embedded.NpcFromTemplate(baseTemplateId, "", [0, 0, 0], 0) is not { } template ? "unknown_base_template"
        : template.Aggressive || template.Relationship is 6 or 11 or >= 16 and <= 21 ? "hostile_base_template"
        : null;

    static void MapCustomNpcs(RouteGroupBuilder staff)
    {
        staff.MapGet("/custom-npcs", async (HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<ICustomNpcAdmin>() is not { } admin) return Results.BadRequest(new { error = "realm_only" });
            var npcs = await admin.ListAsync(a, ct);
            return Results.Ok(new
            {
                npcs,
                firstId = CustomNpcRules.FirstTemplateId,
                actions = CustomNpcRules.ActionTypes,
                talkNames = npcs.ToDictionary(n => n.TemplateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    n => n.Nodes.Select(node => CustomNpcRules.TalkName(n.Slug, node.Key))),
            });
        });
        staff.MapGet("/custom-npcs/export", async (HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<ICustomNpcAdmin>() is not { } admin) return Results.BadRequest(new { error = "realm_only" });
            // Every NPC, disabled ones too: the pak names them all, so enabling one later needs no new pak.
            return Results.Ok(CustomNpcRules.Export(await admin.ListAsync(a, ct)));
        });
        staff.MapPost("/custom-npcs", (CustomNpcInput input, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
            SaveCustomNpc(0, input, ctx, auth, services, ct));
        staff.MapPut("/custom-npcs/{id:int}", (int id, CustomNpcInput input, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
            CustomNpcRules.IsCustom(id) ? SaveCustomNpc(id, input, ctx, auth, services, ct) : Task.FromResult(Results.NotFound()));
        staff.MapDelete("/custom-npcs/{id:int}", async (int id, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if (services.GetService<ICustomNpcAdmin>() is not { } admin) return Results.BadRequest(new { error = "realm_only" });
            return await admin.DeleteAsync(a, id, ct) ? Results.NoContent() : Results.NotFound();
        });
    }

    static async Task<IResult> SaveCustomNpc(int id, CustomNpcInput input, HttpContext ctx, IAccountAuthentication auth, IServiceProvider services, CancellationToken ct)
    {
        if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
        if (services.GetService<ICustomNpcAdmin>() is not { } admin) return Results.BadRequest(new { error = "realm_only" });
        var npc = ToCustomNpc(id, input);
        if ((CustomNpcRules.Validate(npc) ?? BaseTemplateError(npc.BaseTemplateId)) is { } invalid) return Results.BadRequest(new { error = invalid });
        return Results.Ok(await admin.SaveAsync(a, npc, ct));
    }
}
