using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P574 — pure-logic tests for the world-explosion SOUND feature: the tier per
/// blast scale (<see cref="ExplosionSoundTierMapper"/>), the positional
/// attenuation (<see cref="ExplosionSoundCurve"/>), and the router's
/// same-sector-only / no-missile-double-play filtering
/// (<see cref="ExplosionSoundRouter"/>). Also the mine-decal THINNING geometry
/// (<see cref="MineFieldGeometry.OffsetCount(float,float)"/> — native
/// <c>GetCount(strength, radius)</c>, efgeo.cpp:21-24). No audio/graphics device
/// involved: these are the pure decision functions the live proof hears/sees.
/// </summary>
public class P574ExplosionSoundAndMineThinningTests
{
    // ── tier per scale (native trekigc.cpp:96-101) ──────────────────────────

    [Theory]
    [InlineData(1.5f, ExplosionSoundTier.Small)]     // a mine pop → small (ef120)
    [InlineData(10f, ExplosionSoundTier.Small)]      // boundary: >10 required for medium
    [InlineData(10.01f, ExplosionSoundTier.Medium)]
    [InlineData(60f, ExplosionSoundTier.Medium)]     // a typical ship death (2·radius) → medium
    [InlineData(100f, ExplosionSoundTier.Medium)]    // boundary: >100 required for large
    [InlineData(100.01f, ExplosionSoundTier.Large)]
    [InlineData(350f, ExplosionSoundTier.Large)]
    public void Explosion_tier_follows_scale_thresholds(float scale, ExplosionSoundTier expected)
        => Assert.Equal(expected, ExplosionSoundTierMapper.ForScale(scale));

    // ── positional attenuation (native GenericSoundSource) ──────────────────

    [Fact]
    public void Explosion_sound_volume_is_full_up_close_and_silent_past_audible_radius()
    {
        Assert.Equal(ExplosionSoundCurve.MaxVolume, ExplosionSoundCurve.Volume(0f), 5);
        Assert.True(ExplosionSoundCurve.Volume(ExplosionSoundCurve.AudibleRadius / 2f)
                    > ExplosionSoundCurve.Volume(ExplosionSoundCurve.AudibleRadius - 1f));
        Assert.Equal(0f, ExplosionSoundCurve.Volume(ExplosionSoundCurve.AudibleRadius), 5);
        Assert.Equal(0f, ExplosionSoundCurve.Volume(ExplosionSoundCurve.AudibleRadius + 500f), 5);
    }

    // ── router: same-sector only, no missile double-play, tier + volume ─────

    private static ExplosionSoundEntity Ev(int type, short cluster, Vector3 pos, float scale) =>
        new(type, cluster, pos, scale);

    [Fact]
    public void Router_plays_every_world_explosion_in_the_local_sector_with_tier_and_distance_volume()
    {
        var anchor = new Vector3(0f, 0f, 0f);
        var events = new[]
        {
            Ev(ExplosionType.Mine, 1, new Vector3(0f, 0f, 20f), 1.5f),           // near mine pop
            Ev(ExplosionType.SmallShip, 1, new Vector3(1000f, 0f, 0f), 60f),     // mid ship death
            Ev(ExplosionType.LargeStation, 1, new Vector3(3000f, 0f, 0f), 350f), // big station
        };

        var resolved = ExplosionSoundRouter.Resolve(events, mySector: 1, anchor);

        Assert.Equal(3, resolved.Count);
        Assert.Equal((ExplosionType.Mine, ExplosionSoundTier.Small,
                      ExplosionSoundCurve.Volume(20f)), (resolved[0].Type, resolved[0].Tier, resolved[0].Volume));
        Assert.Equal(ExplosionSoundTier.Medium, resolved[1].Tier);
        Assert.Equal(ExplosionSoundTier.Large, resolved[2].Tier);
        Assert.True(resolved[0].Volume > resolved[1].Volume && resolved[1].Volume > resolved[2].Volume,
                    "nearer blasts must be louder");
    }

    [Fact]
    public void Router_skips_missile_detonations_to_avoid_double_playing_the_missile_planner()
    {
        var events = new[]
        {
            Ev(ExplosionType.Missile, 1, new Vector3(0f, 0f, 10f), 60f),     // already voiced by MissileSoundPlanner
            Ev(ExplosionType.SmallShip, 1, new Vector3(0f, 0f, 10f), 60f),   // still voiced here
        };
        var resolved = ExplosionSoundRouter.Resolve(events, mySector: 1, new Vector3(0f, 0f, 0f));
        Assert.Single(resolved);
        Assert.Equal(ExplosionType.SmallShip, resolved[0].Type);
    }

    [Fact]
    public void Router_drops_explosions_outside_the_local_sector()
    {
        var events = new[]
        {
            Ev(ExplosionType.Mine, 1, new Vector3(0f, 0f, 10f), 1.5f),   // same sector
            Ev(ExplosionType.Mine, 2, new Vector3(0f, 0f, 10f), 1.5f),   // different sector (warp-separated, inaudible)
        };
        Assert.Single(ExplosionSoundRouter.Resolve(events, mySector: 1, new Vector3(0f, 0f, 0f)));
    }

    [Fact]
    public void Router_produces_nothing_without_a_listener()
    {
        var events = new[] { Ev(ExplosionType.SmallShip, 1, new Vector3(0f, 0f, 10f), 60f) };
        Assert.Empty(ExplosionSoundRouter.Resolve(events, mySector: 1, anchor: null));
    }

    // ── mine decal thinning (native GetCount(strength, radius)) ─────────────

    [Theory]
    [InlineData(1.0f, 100f, 101)]   // fresh: 1 + int(1.0·10000/100) = 101
    [InlineData(0.5f, 100f, 51)]    // 1 + int(0.5·10000/100) = 51
    [InlineData(0.25f, 100f, 26)]
    [InlineData(0.05f, 100f, 6)]
    [InlineData(0.005f, 100f, 1)]   // floor of 1
    [InlineData(1.0f, 200f, 401)]   // radius-200: dense field
    public void Mine_decal_count_thins_with_strength_natively(float strength, float radius, int expected)
        => Assert.Equal(expected, MineFieldGeometry.OffsetCount(strength, radius));

    [Fact]
    public void Mine_decal_count_defaults_to_fresh_strength_one()
        => Assert.Equal(MineFieldGeometry.OffsetCount(1.0f, 100f), MineFieldGeometry.OffsetCount(100f));

    // Sanity: a full cloud then the worn count draws only the FIRST decals
    // (native truncates the tail by index, efgeo.cpp:43-51).
    [Fact]
    public void Worn_field_keeps_the_same_first_decals_as_the_full_cloud()
    {
        const int radius = 100, seed = 90210;
        int full = MineFieldGeometry.OffsetCount(1.0f, radius);
        int worn = MineFieldGeometry.OffsetCount(0.3f, radius);
        var fullOffsets = new XnaVec[full];
        var wornOffsets = new XnaVec[worn];
        MineFieldGeometry.SampleUniformInSphere(radius, seed, fullOffsets);
        MineFieldGeometry.SampleUniformInSphere(radius, seed, wornOffsets);
        for (int i = 0; i < worn; i++)
            Assert.Equal(fullOffsets[i], wornOffsets[i]);
    }
}
