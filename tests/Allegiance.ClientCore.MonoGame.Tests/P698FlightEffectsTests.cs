using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.Core.Sim;
using Vec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P698FlightEffectsTests
{
    private sealed class FixedRandom : ITrailRandom
    {
        public float Range(float min, float max) => (min + max) * .5f;
        public Vec Direction() => new(1, 0, 0);
    }

    private static void Populate(FlightEffectPools p)
    {
        var rng = new FixedRandom();
        p.WarpPulses.Trigger(7, 1);
        p.OwnShots.Update([new OwnShotWeapon(0, 100, .2f, 1, 0, 0, 1000, 2, false, 0, 1, 41)],
            new OwnShotFireState(ShipControlState.AllWeapons),
            new OwnShotContext(Vec.Zero, Orientation.Identity, Vec.Zero, 1, 1, 1, 1000));
        Assert.NotEmpty(p.OwnShots.GetActiveRounds(1000));
        p.Missiles.Update(1, .016f, [new TrailMissile(2, Vec.Zero, new(0, 0, -1), 1)], rng);
        p.Warp.EmitBurst(Vec.Zero, new(10, 0, 0), new(0, 0, -1), new(1, 0, 0), 20, .016f, rng);
        p.Damage.Emit(Vec.Zero, new(10, 0, 0), Orientation.Identity, 20, 2, 3, .016f, rng);
        p.Engines.Emit(new EngineTrailShip(2, Vec.Zero, new(10, 0, 0), Orientation.Identity, 20, 1), .016f, rng);
        p.Motion.SetAnchor(2, Vec.Zero, new(1, 0, 0), 1, 1, 1);
        p.HullHits.Emit(Vec.Zero, new(1, 0, 0), 20, rng);
        p.Shields.Emit(2, Vec.Zero, Orientation.Identity, new(20, 0, 0), new(1, 1, 1), 20, 20);
        Assert.True(p.Missiles.ActiveCount > 0);
        Assert.True(p.Warp.ActiveCount > 0);
        Assert.True(p.Damage.ActiveCount > 0);
        Assert.True(p.Engines.ActiveCount > 0);
        Assert.True(p.Motion.RibbonCount > 0);
        Assert.NotEmpty(p.HullHits.Enumerate());
        Assert.NotEmpty(p.Shields.Enumerate());
    }

    private static void Empty(FlightEffectPools p)
    {
        Assert.Empty(p.Missiles.Enumerate());
        Assert.Empty(p.Warp.Enumerate());
        Assert.Empty(p.Damage.Enumerate());
        Assert.Empty(p.Engines.Enumerate());
        Assert.Empty(p.Motion.Enumerate());
        Assert.Equal(0, p.Motion.RibbonCount);
        Assert.Empty(p.HullHits.Enumerate());
        Assert.Empty(p.HullHits.EnumerateFlashes());
        Assert.Empty(p.Shields.Enumerate());
        Assert.Empty(p.OwnShots.GetActiveRounds(1000));
        Assert.Equal(0, p.WarpPulses.ActiveCount);
    }

    [Theory]
    [InlineData(2, 1051)]
    [InlineData(2, 1061)] // same sector SET_CLUSTER must reset too
    [InlineData(1, 1051)] // docked sector preview changes without SET_CLUSTER
    public void Sector_transition_clears_every_persistent_pool(long generation, short sector)
    {
        var p = new FlightEffectPools();
        p.BeginView(1, 1061);
        Populate(p);
        Assert.False(p.BeginView(1, 1061));
        Assert.True(p.BeginView(generation, sector));
        Empty(p);
    }

    [Fact]
    public void Removed_source_stops_emitting_and_detached_particles_keep_moving_and_expire()
    {
        var p = new FlightEffectPools();
        p.BeginView(1, 1061);
        Populate(p);
        var before = p.Damage.Enumerate().First();
        // No further Emit/SetAnchor calls: precisely the gather when source is gone.
        p.Motion.PruneUntouched(); // finish the populated frame
        p.Motion.PruneUntouched(); // source absent in the following frame
        p.Advance(.1f);
        p.Missiles.Update(1.1, .1f, [], new FixedRandom());
        var after = p.Damage.Enumerate().First();
        Assert.NotEqual(before.Position, after.Position);
        Assert.True(after.RemainingFrac < before.RemainingFrac);
        p.Advance(.25f); // damage smoke's native total lifetime is 0.35 s
        Assert.Empty(p.Damage.Enumerate());
        p.Advance(1.75f); // 2.1 s total: even the 2 s exhaust/sparks have expired
        p.WarpPulses.Update(31.1);
        Assert.Empty(p.OwnShots.GetActiveRounds(31100));
        p.Missiles.Update(3.1, 2, [], new FixedRandom());
        Empty(p);
    }
}
