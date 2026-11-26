using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Projectile flights by SkillProjectile.dat type (tools/data/projectiles.json, Projectiles.Flight).</summary>
public sealed class ProjectileFlightTests
{
    [Fact]
    public void Every_skill_projectile_row_is_embedded()
    {
        Assert.Equal(new Projectiles.Row("Collide", 4000, 0, 0, 2500), Projectiles.Of(10002001));
        Assert.Equal(new Projectiles.Row("Splash_FixTime_Target", 1200, 550, 550, 0), Projectiles.Of(170800001));
        Assert.Equal("Target", Projectiles.Of(140200001)?.Type);   // Snipe
        Assert.Null(Projectiles.Of(0));
    }

    [Theory]
    [InlineData(123)]
    [InlineData(5_000)]
    public void Collide_flies_its_travel_over_speed_whatever_the_distance(double distance) =>
        Assert.Equal((600, 650), Projectiles.Flight(10002001, distance));   // 2500 cm at 4000 cm/s = 625 ms, in 50 ms steps

    [Fact]
    public void Fixed_time_ignores_the_distance()
    {
        Assert.Equal((550, 600), Projectiles.Flight(170800001, 99_999, new Random(1)));
        Assert.Equal((500, 550), Projectiles.Flight(161200011, 10));   // Fire spirit's shot, Splash_FixTime 500
    }

    [Fact]
    public void Fixed_time_range_is_drawn_in_ticks_within_it()
    {
        Assert.Equal(new Projectiles.Row("Splash_FixTime", 500, 3000, 6000, 0), Projectiles.Of(3));
        Assert.Equal((3000, 3050), Projectiles.Flight(3, 0));   // no rng: the minimum
        var rng = new Random(5);
        var drawn = Enumerable.Range(0, 200).Select(_ => Projectiles.Flight(3, 0, rng)!.Value).ToList();
        Assert.All(drawn, f =>
        {
            Assert.InRange(f.Announced, 3000, 6000);
            Assert.Equal(0, f.Announced % Projectiles.TickMs);
            Assert.Equal(f.Announced + Projectiles.TickMs, f.Arrival);
        });
        Assert.True(drawn.Select(f => f.Announced).Distinct().Count() > 10);
    }

    [Fact]
    public void Target_type_flies_the_distance_in_ticks()
    {
        Assert.Equal((150, 200), Projectiles.Flight(143400001, 750));   // Speed 5000
        Assert.Equal((100, 150), Projectiles.Flight(143400001, 749));
        Assert.Equal((50, 100), Projectiles.Flight(143400001, 0));      // at least one tick
        Assert.Equal((Projectiles.MaxDistanceFlightMs, Projectiles.MaxDistanceFlightMs + 50), Projectiles.Flight(143400001, 1_000_000));
    }

    [Fact]
    public void Unknown_projectile_keeps_the_distance_over_speed_flight()
    {
        Assert.Null(Projectiles.Flight(999_999_999, 700));
        var area = new SkillAreas.Area(null, null, 1, false, 999_999_999, 5000);
        Assert.Equal((140, 140), area.Flight(700));
        Assert.Equal((0, 0), (area with { Projectile = 0 }).Flight(700));
    }
}
