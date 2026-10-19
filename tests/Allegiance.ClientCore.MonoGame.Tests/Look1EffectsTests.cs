using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class Look1EffectsTests
{
    private static ExplosionEntity Blast(int type = 3, short sector = 1, long received = 1000) =>
        new() { Type = type, ClusterId = sector, ReceivedLocalMs = received, Position = new(100, 50, 0), Velocity = new(10, 0, 0) };

    [Fact]
    public void Low_bypasses_flashes_and_invalid_or_other_sector_events_are_ignored()
    {
        var lights = new EffectLights { Detail = EffectsDetail.Low };
        lights.Explosion(Blast(), 1, 1050, Vector3.Zero);
        lights.Impact(new() { ClusterId = 1, ReceivedLocalMs = 1000 }, 1, 1050, Vector3.Zero);
        Assert.Equal(0, lights.Count);
        lights.Detail = EffectsDetail.High;
        lights.Explosion(Blast(sector: 2), 1, 1050, Vector3.Zero);
        lights.Explosion(Blast(type: 0), 1, 1050, Vector3.Zero);
        lights.Explosion(Blast(type: 8), 1, 1050, Vector3.Zero);
        lights.Explosion(Blast(received: 1100), 1, 1050, Vector3.Zero);
        lights.Explosion(Blast(), 1, 1650, Vector3.Zero);
        Assert.Equal(0, lights.Count);
    }

    [Fact]
    public void Station_flash_drifts_in_camera_relative_space_and_expires()
    {
        var lights = new EffectLights { Detail = EffectsDetail.High };
        lights.Explosion(Blast(type: 7), 1, 1100, new(100, 40, 0));
        Assert.Equal(1, lights.Count);
        Assert.Equal(new Vector3(1, 10, 0), new Vector3(lights.Positions[0].X, lights.Positions[0].Y, lights.Positions[0].Z));
        Assert.Equal(1750, lights.Positions[0].W);
        float early = lights.Colors[0].X;
        lights.Clear();
        lights.Explosion(Blast(type: 7), 1, 1500, Vector3.Zero);
        Assert.InRange(lights.Colors[0].X, 0, early);
        lights.Clear();
        Assert.All(lights.Positions, p => Assert.Equal(Vector4.Zero, p));
        lights.Explosion(Blast(type: 7), 1, 1650, Vector3.Zero);
        Assert.Equal(0, lights.Count);
    }

    [Theory]
    [InlineData(EffectsDetail.Medium, 2)]
    [InlineData(EffectsDetail.High, 4)]
    public void Light_budget_keeps_nearby_bright_events(EffectsDetail detail, int expected)
    {
        var lights = new EffectLights { Detail = detail };
        for (int i = 0; i < 20; i++)
        {
            var blast = Blast(); blast.Position = new(100000 + i * 100, 0, 0);
            lights.Explosion(blast, 1, 1000, Vector3.Zero);
        }
        lights.Explosion(Blast(type: 7), 1, 1000, Vector3.Zero);
        Assert.Equal(expected, lights.Count);
        Assert.Contains(lights.Positions.Take(lights.Count), p => p.W == 1750);
    }

    [Fact]
    public void Shield_and_hull_flashes_keep_distinct_colours_and_short_lifetimes()
    {
        var lights = new EffectLights();
        var hit = new HullHitEntity { ClusterId = 1, ReceivedLocalMs = 1000, Radius = 20, Normal = new(0, 0, 1) };
        lights.Impact(hit, 1, 1050, Vector3.Zero);
        Assert.True(lights.Colors[0].X > lights.Colors[0].Z);
        hit.AbsorbedByShield = true;
        lights.Impact(hit, 1, 1050, Vector3.Zero);
        Assert.True(lights.Colors[1].Z > lights.Colors[1].X);
        lights.Clear();
        lights.Impact(hit, 1, 1160, Vector3.Zero);
        Assert.Equal(0, lights.Count);
    }

    [Fact]
    public void P729_requires_measured_headroom_for_high_and_steps_down_to_low()
    {
        var run = new BenchmarkRun { Rendered = true, CpuMs = 4, GpuMs = 5, FpsOnePercentLow = 90 };
        var high = BenchmarkPolicy.Recommend(run, SkirmishSize.Medium);
        Assert.Equal(EffectsDetail.High, high.EffectsQuality);
        Assert.True(high.Bloom);
        Assert.Equal(EffectsDetail.Medium, BenchmarkPolicy.Recommend(run with { GpuMs = null }, SkirmishSize.Medium).EffectsQuality);
        Assert.Equal(EffectsDetail.Low, BenchmarkPolicy.Recommend(run with { GpuMs = 25 }, SkirmishSize.Medium).EffectsQuality);
        var low = BenchmarkPolicy.StepDown(high);
        Assert.Equal(EffectsDetail.Low, low.EffectsQuality);
        Assert.False(low.Bloom);
        Assert.Contains(low.Reasons, r => r.Setting == "Effects quality" && r.Value == "Low");
        Assert.Contains("EffectsQuality", ShareSettings.From(low).ChangedFrom(ShareSettings.From(high)));
    }

    [Fact]
    public void Quality_preferences_round_trip_and_invalid_values_normalize()
    {
        string path = Path.Combine(Path.GetTempPath(), "look1-" + Guid.NewGuid() + ".json");
        try
        {
            new UpscalingSettings { Effects = EffectsDetail.High }.Save(path);
            Assert.Equal(EffectsDetail.High, UpscalingSettings.Load(path).Effects);
            File.WriteAllText(path, "{\"Effects\":900}");
            Assert.Equal(EffectsDetail.Medium, UpscalingSettings.Load(path).Effects);
            File.WriteAllText(path, "{}");
            Assert.Equal(EffectsDetail.Medium, UpscalingSettings.Load(path).Effects);
        }
        finally { File.Delete(path); }
    }
}
