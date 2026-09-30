using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P463b shield-hit flare pool
/// (<see cref="ShieldFlareParticles"/>) — a faithful port of native
/// <c>ThingGeo::AddFlare</c> (<c>effect/thinggeo.cpp:1243-1301</c>), the client
/// visual that lights up the struck ship's shield bubble where a shield-absorbed
/// hit lands (fired from <c>CshieldIGC::ApplyDamage</c>, <c>src/Igc/shieldIGC.cpp:171</c>).
/// These exercise the pool's own mechanics — placing the flare on the shield
/// toward the hit direction, sizing it from the ship's real render scale, the
/// native 0.75&#8209;s lifetime and its linear fade, and the native
/// at-most-four-flares-per-ship cap (<c>trekigc.cpp:1626 GetFlareCount() &lt; 4</c>)
/// — and never touch a graphics device (the pool is MonoGame-free).
/// </summary>
public class ShieldFlareParticlesTests
{
    private const short TARGET = 707;
    private static readonly SimVec ShipCenter = new(1000f, 500f, -200f);
    private const float RADIUS = 50f;

    [Fact]
    public void Emit_places_the_flare_on_the_shield_toward_the_hit()
    {
        // Native maps the hit direction onto the shield surface
        // (thinggeo.cpp:1253-1259): flare centre = centre + unitDir · radius,
        // radius = the ship's real render scale (0.5 × hull length).
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, new SimVec(1f, 0f, 0f), RADIUS);

        Assert.Equal(1, pool.ActiveCount);
        var v = Assert.Single(pool.Enumerate());
        Assert.Equal(ShipCenter.X + RADIUS, v.Center.X, 4);
        Assert.Equal(ShipCenter.Y, v.Center.Y, 4);
        Assert.Equal(ShipCenter.Z, v.Center.Z, 4);
        Assert.Equal(1f, v.Direction.X, 4);
        Assert.Equal(0f, v.Direction.Y, 4);
        Assert.Equal(0f, v.Direction.Z, 4);
        Assert.Equal(1f, v.RemainingFrac, 4);
    }

    [Fact]
    public void Emit_normalizes_a_non_unit_hit_direction()
    {
        // Direction (3,0,0) is not unit — the flare must still sit exactly
        // one radius out along +X.
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, new SimVec(3f, 0f, 0f), RADIUS);

        var v = Assert.Single(pool.Enumerate());
        Assert.Equal(ShipCenter.X + RADIUS, v.Center.X, 4);
        Assert.Equal(ShipCenter.Y, v.Center.Y, 4);
        Assert.Equal(ShipCenter.Z, v.Center.Z, 4);
    }

    [Fact]
    public void Emit_handles_a_zero_hit_direction_with_positive_X_fallback()
    {
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, SimVec.Zero, RADIUS);

        var v = Assert.Single(pool.Enumerate());
        Assert.Equal(ShipCenter.X + RADIUS, v.Center.X, 4);
        Assert.Equal(1f, v.Direction.X, 4);
        Assert.Equal(0f, v.Direction.Y, 4);
        Assert.Equal(0f, v.Direction.Z, 4);
    }

    [Fact]
    public void Flare_size_scales_with_the_ships_render_scale()
    {
        // The flare's half-extent is SizeFactor × the shield radius (native
        // scales the flare geo to roughly the ship's radius, thinggeo.cpp:1289).
        var small = new ShieldFlareParticles();
        small.Emit(TARGET, ShipCenter, new SimVec(0f, 1f, 0f), 30f);
        var big = new ShieldFlareParticles();
        big.Emit(TARGET, ShipCenter, new SimVec(0f, 1f, 0f), 90f);

        Assert.Equal(ShieldFlareParticles.SizeFactor * 30f, Assert.Single(small.Enumerate()).Size, 4);
        Assert.Equal(ShieldFlareParticles.SizeFactor * 90f, Assert.Single(big.Enumerate()).Size, 4);
    }

    [Fact]
    public void Flare_fades_linearly_over_its_native_lifetime()
    {
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, new SimVec(0f, 1f, 0f), RADIUS);

        // Half of the native 0.75s lifetime elapsed → RemainingFrac = 0.5.
        pool.Update(ShieldFlareParticles.LifetimeSeconds * 0.5f);
        Assert.Equal(0.5f, Assert.Single(pool.Enumerate()).RemainingFrac, 4);
    }

    [Fact]
    public void Flares_expire_after_the_native_point_seven_five_second_lifetime()
    {
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, new SimVec(0f, 1f, 0f), RADIUS);
        Assert.Equal(1, pool.ActiveCount);

        // Step well past LifetimeSeconds = 0.75 (native TimedGeo 0.75, thinggeo.cpp:1304).
        for (int i = 0; i < 100 && pool.ActiveCount > 0; i++)
            pool.Update(0.016f);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void At_most_four_flares_per_ship_matches_native_cap()
    {
        // Native admits a flare only while GetFlareCount() < 4 (trekigc.cpp:1626),
        // and drops the expired/oldest when at the cap. Emit six to one target
        // with growing radii; the two oldest (smallest) must be evicted.
        var pool = new ShieldFlareParticles();
        for (int i = 0; i < 6; i++)
            pool.Emit(TARGET, ShipCenter, new SimVec(0f, 1f, 0f), 10f + 10f * i);

        Assert.Equal(ShieldFlareParticles.MaxFlaresPerShip, pool.ActiveCount);
        var sizes = pool.Enumerate().Select(v => v.Size).OrderBy(s => s).ToList();
        // Retained are the four newest: radii 30,40,50,60 → halves 15,20,25,30.
        Assert.Equal(new[] { 15f, 20f, 25f, 30f }, sizes.Select(s => MathF.Round(s)).ToArray());
    }

    [Fact]
    public void Distinct_targets_share_the_pool()
    {
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, new SimVec(1f, 0f, 0f), RADIUS);
        pool.Emit((short)(TARGET + 1), new SimVec(0f, 0f, 0f), new SimVec(-1f, 0f, 0f), RADIUS);

        Assert.Equal(2, pool.ActiveCount);
    }

    [Fact]
    public void Reset_clears_every_flare()
    {
        var pool = new ShieldFlareParticles();
        pool.Emit(TARGET, ShipCenter, new SimVec(1f, 0f, 0f), RADIUS);
        Assert.Equal(1, pool.ActiveCount);

        pool.Reset();
        Assert.Equal(0, pool.ActiveCount);
        Assert.Empty(pool.Enumerate());
    }
}
