using System.Globalization;
using System.Text;
using System.Text.Json;
using Aion.Application.Gameplay;

namespace Aion.Host.Authentication;

/// <summary>
/// A bug report as one self-contained Markdown brief (the dashboard's "Copy as Markdown", GET .../markdown): what the player
/// wrote, where they stood, their quests, the spawns around them with their data rows, the last packets, and where to look in
/// the repository. Built from the stored context JSON, so it reads reports of every version.
/// </summary>
public static class BugReportMarkdown
{
    public static string Build(BugReport report, string jsonPath)
    {
        var r = report.Summary;
        var c = report.Context;
        var md = new StringBuilder();
        var firstLine = r.Text.Split('\n')[0].Trim();
        md.Append("# Bug report #").Append(r.Id).Append(": ").AppendLine(Cell(firstLine.Length > 90 ? firstLine[..90] + "…" : firstLine, false)).AppendLine();
        md.AppendLine("A player reported this in game with `/bug`. Everything below was captured by the world server at that moment;");
        md.AppendLine("reproduce the bug from it without asking the player.").AppendLine();
        foreach (var line in r.Text.Split('\n')) md.Append("> ").AppendLine(line.TrimEnd());
        md.AppendLine();

        md.AppendLine("## Report");
        md.Append("- Reported: ").AppendLine(r.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        md.Append("- Status: ").Append(r.Status);
        if (r.StaffNote.Length > 0) md.Append(" (staff note: ").Append(r.StaffNote).Append(')');
        md.AppendLine();
        md.Append("- World: ").AppendLine(r.WorldId.ToString(CultureInfo.InvariantCulture));
        md.Append("- Server commit: ").AppendLine(r.Commit is { } commit ? $"`{commit}` (check out this commit to see the code the player ran)" : "unknown");
        if (Obj(c, "character") is { } ch)
        {
            md.Append("- Character: ").Append(Str(ch, "name") ?? "?").Append(", level ").Append(Num(ch, "level")).Append(' ').Append(Str(ch, "raceName"))
                .Append(' ').Append(Str(ch, "className") ?? "?");
            if (Str(ch, "staffRole") is { } role) md.Append(" (staff: ").Append(role).Append(')');
            md.Append("; HP ").Append(Num(ch, "hp")).Append('/').Append(Num(ch, "hpMax"));
            if (Bool(ch, "dead")) md.Append(", dead");
            if (Bool(ch, "inCombat")) md.Append(", in combat");
            md.AppendLine();
            md.Append("- Character id: `").Append(Str(ch, "id") ?? "?").Append("`, account `").Append(r.AccountName ?? Str(ch, "accountId") ?? "?").AppendLine("`");
        }
        md.AppendLine();

        md.AppendLine("## Where");
        if (Obj(c, "location") is { } at)
        {
            md.Append("- Map: ").Append(Str(at, "mapName")).Append(" (").Append(Num(at, "mapId")).Append(')');
            if (Num(at, "instance") is { Length: > 0 } instance && instance != "0") md.Append(", instance ").Append(instance);
            md.AppendLine();
            md.Append("- Subzone: ").Append(Num(at, "subzoneId"));
            if (Str(at, "subzone") is { } subzone) md.Append(" `").Append(subzone).Append('`');
            if (Str(at, "area") is { } area) md.Append(" (").Append(area).Append(')');
            md.AppendLine();
            md.Append("- Position: x ").Append(Num(at, "x")).Append(", y ").Append(Num(at, "y")).Append(", z ").Append(Num(at, "z"))
                .Append(", heading ").Append(Num(at, "heading")).AppendLine(" (units are cm)");
            md.Append("- Teleport there (GM): `//Teleport ").Append(Num(at, "mapId")).Append(' ').Append(Num(at, "x")).Append(' ').Append(Num(at, "y")).Append(' ')
                .Append(Num(at, "z")).AppendLine("`");
        }
        else md.AppendLine("Not in the world.");
        md.AppendLine();

        md.AppendLine("## Quests");
        if (Obj(c, "quests") is { } quests)
        {
            md.AppendLine("Active (tools/data/quests.json; steps and objectives from the client's Quest.dat):").AppendLine();
            if (Arr(quests, "active") is { Count: > 0 } active)
            {
                md.AppendLine("| Code | Id | Title | Status | Step | Goals | Step NPC | Step objectives |");
                md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
                foreach (var q in active)
                    md.Append("| `").Append(Str(q, "code")).Append("` | ").Append(Num(q, "id")).Append(" | ").Append(Cell(Str(q, "title"))).Append(" | ")
                        .Append(Str(q, "statusName")).Append(" | ").Append(Num(q, "step")).Append(" | ").Append(Ints(q, "goals")).Append(" | ")
                        .Append(Num(q, "stepNpc")).Append(" | ").Append(Cell(Objectives(q))).AppendLine(" |");
            }
            else md.AppendLine("(none)");
            if (Arr(quests, "recentEvents") is { Count: > 0 } events)
            {
                md.AppendLine().AppendLine("Recent quest events of this connection (newest first):").AppendLine();
                foreach (var e in events)
                    md.Append("- ").Append(Seconds(e, "agoMs")).Append(" before: ").Append(Str(e, "kind")).Append(" `").Append(Str(e, "code")).Append("` step ")
                        .Append(Num(e, "step")).Append(", status ").Append(Num(e, "status")).Append(Ints(e, "goals") is { Length: > 0 } g ? $", goals {g}" : "").AppendLine();
            }
            if (Arr(quests, "lastRewarded") is { Count: > 0 } done)
                md.AppendLine().Append("Last rewarded: ").AppendLine(string.Join(", ", done.Select(q => $"`{Str(q, "code")}` {Str(q, "title")}".TrimEnd())));
        }
        md.AppendLine();

        if (Arr(c, "mapEvents") is { Count: > 0 } runs)
        {
            md.AppendLine("## Map events running").AppendLine();
            foreach (var e in runs)
                md.Append("- ").Append(Str(e, "name")).Append(" (id ").Append(Num(e, "id")).Append("): status ").Append(Num(e, "status")).Append(", stage ")
                    .Append(Num(e, "stage")).Append(", step ").Append(Num(e, "step")).Append(", goals ").Append(Ints(e, "goals")).Append(", started ")
                    .Append(Seconds(e, "ageMs")).AppendLine(" before");
            md.AppendLine();
        }

        var radius = Num(c, "nearbyRadius");
        md.Append("## NPCs within ").Append(radius.Length > 0 && double.TryParse(radius, CultureInfo.InvariantCulture, out var cm) ? (cm / 100).ToString("0", CultureInfo.InvariantCulture) : "?")
            .AppendLine(" m (nearest first)").AppendLine();
        md.AppendLine("Seen: the server showed it to this player. Source `map_data`: the row `maps[\"<map>\"][<index>]` of tools/data/world-npcs.json");
        md.AppendLine("(built by tools/world_npcs.py from the client's MapData); home is where that row places it.").AppendLine();
        if (Arr(c, "nearby") is { Count: > 0 } npcs)
        {
            md.AppendLine("| Key | NPC id | Name | Dist | x, y, z | Heading | State | Seen | Source |");
            md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var n in npcs)
            {
                var state = (Bool(n, "dead") ? "dead, " : "") + (Bool(n, "hostile") ? "hostile" : "friendly") + $" rel {Num(n, "relationship")}"
                    + (Str(n, "function") is { } f && f != "None" ? $", {f}" : "");
                md.Append("| ").Append(Num(n, "key")).Append(" | ").Append(Num(n, "npcId")).Append(" | ").Append(Cell(Str(n, "name"))).Append(" | ")
                    .Append(Num(n, "distance")).Append(" | ").Append(Num(n, "x")).Append(", ").Append(Num(n, "y")).Append(", ").Append(Num(n, "z")).Append(" | ")
                    .Append(Num(n, "heading")).Append(" | ").Append(state).Append(" | ").Append(Bool(n, "seen") ? "yes" : "no").Append(" | ")
                    .Append(Cell(Source(Obj(n, "source")))).AppendLine(" |");
            }
        }
        else md.AppendLine("(none)");
        md.AppendLine();

        if (Arr(c, "nearbyObjects") is { Count: > 0 } objects)
        {
            md.AppendLine("## Objects nearby (tools/data/world-objects.json, built by tools/world_objects.py)").AppendLine();
            md.AppendLine("| Key | Env | Name | Dist | x, y, z | Yaw | Shown | Row |");
            md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var o in objects)
                md.Append("| ").Append(Num(o, "key")).Append(" | ").Append(Num(o, "env")).Append(" | ").Append(Cell(Str(o, "envName") ?? Str(o, "name"))).Append(" | ")
                    .Append(Num(o, "distance")).Append(" | ").Append(Num(o, "x")).Append(", ").Append(Num(o, "y")).Append(", ").Append(Num(o, "z")).Append(" | ")
                    .Append(Num(o, "yaw")).Append(" | ").Append(Bool(o, "shown") ? "yes" : "no").Append(" | maps[\"").Append(Num(o, "map")).Append("\"][")
                    .Append(Num(o, "index")).Append("], unique key ").Append(Num(o, "uniqueKey")).AppendLine(" |");
            md.AppendLine();
        }

        md.AppendLine("## Last packets of the connection (oldest first)").AppendLine();
        if (Obj(c, "packets") is { } packets && Arr(packets, "recent") is { Count: > 0 } recent)
        {
            md.Append("C = client to server, S = server to client; time before the report; length with the 2-byte opcode; hex = first ")
                .Append(Num(packets, "previewBytes")).AppendLine(" body bytes. Names from tools/data/opcode-names.json.").AppendLine();
            md.AppendLine("```");
            foreach (var p in recent)
                md.Append(Seconds(p, "agoMs").PadLeft(9)).Append(' ').Append(Str(p, "dir")).Append(' ').Append(Str(p, "op")).Append(' ')
                    .Append((Str(p, "name") ?? "?").PadRight(28)).Append(' ').Append(Num(p, "length").PadLeft(6)).Append("B ").AppendLine(Str(p, "hex") ?? "");
            md.AppendLine("```");
        }
        else md.AppendLine("(none recorded)");
        md.AppendLine();

        md.AppendLine("## More");
        md.Append("- The full context as JSON: `GET ").Append(jsonPath).AppendLine("` on the account site with a staff sign-in (`Authorization: Bearer <token>`).");
        if (r.ScreenshotType is not null) md.Append("- Screenshot: `GET ").Append(jsonPath).AppendLine("/screenshot`.");
        md.AppendLine("- Quest logic: server/Aion.World/GameSession.Quests*.cs, map events: server/Aion.World/MapEvents.cs, NPC spawns: server/Aion.World/Combat.cs (MapTargets).");
        md.AppendLine("- Mark the report fixed on the staff dashboard (Bug reports) once the fix is merged.");
        return md.ToString();
    }

    static string Objectives(JsonElement quest) => Arr(quest, "stepObjectives") is { Count: > 0 } list
        ? string.Join("; ", list.Select(o =>
        {
            var text = Str(o, "type") ?? "?";
            if (Num(o, "npc") is { Length: > 0 } npc) text += " npc " + npc;
            if (Ints(o, "npcs") is { Length: > 0 } npcs) text += " npcs " + npcs;
            if (Num(o, "item") is { Length: > 0 } item) text += " item " + item;
            if (Str(o, "target") is { } target) text += " " + target;
            if (Num(o, "count") is { Length: > 0 } count && count != "1") text += " x" + count;
            return text;
        }))
        : "";

    static string Source(JsonElement? source)
    {
        if (source is not { } s) return "?";
        var kind = Str(s, "kind") ?? "?";
        if (kind != "map_data") return kind + (Num(s, "ownerKey") is { Length: > 0 } owner ? $" of {owner}" : "") + (Str(s, "file") is { } file ? $" ({file})" : "");
        var home = Arr(s, "home") is { Count: >= 3 } h ? $"home {Raw(h[0])}, {Raw(h[1])}, {Raw(h[2])} yaw {Num(s, "homeYaw")}" : "";
        return $"map_data maps[\"{Num(s, "map")}\"][{Num(s, "index")}] unique key {Num(s, "uniqueKey")}, {home}";
    }

    static JsonElement? Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    static List<JsonElement>? Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray()] : null;
    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String ? Raw(v) : "";
    static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static string Ints(JsonElement e, string name) => Arr(e, name) is { } list ? string.Join(",", list.Select(Raw)) : "";
    static string Raw(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
    static string Seconds(JsonElement e, string name) =>
        long.TryParse(Num(e, name), CultureInfo.InvariantCulture, out var ms) ? (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " s" : "?";
    static string Cell(string? text, bool table = true) =>
        (text ?? "").Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) is var t && table ? t.Replace("|", "\\|", StringComparison.Ordinal) : t;
}
