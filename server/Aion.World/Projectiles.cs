using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Skill projectile flights by SkillProjectile.dat type, from tools/data/projectiles.json (tools/projectile_data.py; game
/// version 3533). Ported from the A2Core fork (Addiboii, caa5f3f), whose recordings (every 0x3803 between 2026-10-13 and
/// 11-06) it reads as follows. The announced flight is the 0x3803's remaining time and the hit lands one tick after it.
/// OBSERVED there: Splash_FixTime(_Target) flies ArrivalFixTimeMin..Max (1068 of 1068), Collide flies CollideMoveValue01 /
/// Speed (597 of 597), every flight is a multiple of 50 ms, and the hit (0x3804) lands 0-99 ms after the announced flight.
/// Our own official arrows agree: Snipe 150 ms and Rapid Fire 100 ms announced (OfficialCombatFxTests).
/// CANDIDATE: Target and the VariableTime types fly the distance / Speed.
/// </summary>
public static class Projectiles
{
    /// <summary>The flight step and the hit's delay after the announced arrival (OBSERVED, above).</summary>
    public const int TickMs = 50;

    /// <summary>The longest distance-timed flight (OURS: the cap the distance / Speed flight had before the typed rows).</summary>
    public const int MaxDistanceFlightMs = 3000;

    /// <summary>One SkillProjectile.dat row: EProjectileType, Speed (cm/s), ArrivalFixTimeMin / Max (ms), CollideMoveValue01 (cm).</summary>
    public sealed record Row(string Type, int Speed, int FixMin, int FixMax, int CollideTravel);

    static readonly Lazy<Dictionary<int, Row>> rows = new(() =>
    {
        using var stream = typeof(Projectiles).Assembly.GetManifestResourceStream("projectiles.json")
            ?? throw new InvalidOperationException("Missing embedded game data projectiles.json");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("projectiles").EnumerateObject().ToDictionary(
            p => int.Parse(p.Name, CultureInfo.InvariantCulture),
            p => new Row(p.Value[0].GetString() ?? "None", p.Value[1].GetInt32(), p.Value[2].GetInt32(), p.Value[3].GetInt32(), p.Value[4].GetInt32()));
    });

    /// <summary>The SkillProjectile.dat row of <paramref name="projectile"/>, or null.</summary>
    public static Row? Of(int projectile) => rows.Value.GetValueOrDefault(projectile);

    /// <summary>
    /// A projectile's flight over <paramref name="distance"/> cm: the announced time (0x3803) and the hit's arrival, one tick
    /// later; null when the projectile is unknown or cannot move. A fixed-time range is drawn in 50 ms steps with
    /// <paramref name="rng"/> (its minimum without one).
    /// </summary>
    public static (int Announced, int Arrival)? Flight(int projectile, double distance, Random? rng = null)
    {
        if (Of(projectile) is not { } row) return null;
        int announced;
        if (row.Type is "Splash_FixTime" or "Splash_FixTime_Target" && row.FixMax > 0)
        {
            var min = Math.Min(row.FixMin, row.FixMax) / TickMs;
            var max = Math.Max(row.FixMin, row.FixMax) / TickMs;
            announced = (rng?.Next(min, max + 1) ?? min) * TickMs;
        }
        else if (row.Speed <= 0) return null;
        else if (row.Type == "Collide" && row.CollideTravel > 0)
            announced = (int)Math.Min(int.MaxValue / 2, row.CollideTravel * 1000.0 / row.Speed) / TickMs * TickMs;
        else
            announced = (int)Math.Min(MaxDistanceFlightMs, Math.Max(0, distance) * 1000 / row.Speed) / TickMs * TickMs;
        announced = Math.Max(TickMs, announced);
        return (announced, announced + TickMs);
    }
}
