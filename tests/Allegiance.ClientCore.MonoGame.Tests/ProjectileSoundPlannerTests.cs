using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Audio;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ProjectileSoundPlanner"/> — the P232
/// spawn→Start / continue→Update / expire→Stop transition contract for a
/// projectile's in-flight ambient sound, and the local-fire attribution on
/// Start. No audio device involved: the planner only emits
/// <see cref="ProjectileSoundCommand"/>s; <see cref="SoundManager"/> performs
/// them. These are the P232 brief's explicit "spawn/expire → play/stop calls"
/// unit tests (the play/stop side is the SoundManager wiring the live proof
/// exercises).
/// </summary>
public class ProjectileSoundPlannerTests
{
    private static ProjectileEntity Bolt(float velZ = -600f, float lifespan = 1.1f) => new()
    {
        ProjectileTypeId = 4,
        Position = default,
        Forward = new(0f, 0f, -1f),
        Velocity = new(0f, 0f, velZ),
        Lifespan = lifespan,
        ReceivedLocalMs = 0,
    };

    [Fact]
    public void NewProjectile_EmitsStart_WithDistanceScaledVolume()
    {
        var planner = new ProjectileSoundPlanner();
        var bolt = Bolt();

        // Spawned near the listener (muzzle): Start, local-fire true, volume high.
        var cmds = planner.Plan(new[] { (bolt, 10f) });

        var start = Assert.Single(cmds);
        Assert.Equal(ProjectileSoundAction.Start, start.Action);
        Assert.Same(bolt, start.Projectile);
        Assert.True(start.IsLocalFire);
        Assert.Equal(ProjectileAmbientSoundCurve.VolumeForDistance(10f), start.Volume, 5);
    }

    [Fact]
    public void ContinuedProjectile_EmitsUpdate_NotAnotherStart()
    {
        var planner = new ProjectileSoundPlanner();
        var bolt = Bolt();
        planner.Plan(new[] { (bolt, 10f) });

        // Next frame the bolt is still alive but farther away → Update, never
        // a second Start (each bolt's ambient begins exactly once).
        var cmds = planner.Plan(new[] { (bolt, 420f) });

        var update = Assert.Single(cmds);
        Assert.Equal(ProjectileSoundAction.Update, update.Action);
        Assert.False(update.IsLocalFire); // local-fire is a Start-only flag
        Assert.Equal(ProjectileAmbientSoundCurve.VolumeForDistance(420f), update.Volume, 5);
    }

    [Fact]
    public void ExpiredProjectile_EmitsStop()
    {
        var planner = new ProjectileSoundPlanner();
        var bolt = Bolt();
        planner.Plan(new[] { (bolt, 10f) });

        // Bolt vanishes (expired/pruned, or flew past audible radius) →
        // Stop, exactly once.
        var cmds = planner.Plan(Array.Empty<(ProjectileEntity, float)>());

        var stop = Assert.Single(cmds);
        Assert.Equal(ProjectileSoundAction.Stop, stop.Action);
        Assert.Same(bolt, stop.Projectile);
    }

    [Fact]
    public void FarSpawn_IsNotLocalFire()
    {
        var planner = new ProjectileSoundPlanner();
        var enemyBolt = Bolt();

        // A bolt that spawns well beyond the muzzle radius is another ship's
        // shot — it still gets an ambient, but no weapon-fire one-shot.
        var cmds = planner.Plan(new[] { (enemyBolt, ProjectileAmbientSoundCurve.FireOriginRadius + 500f) });

        var start = Assert.Single(cmds);
        Assert.Equal(ProjectileSoundAction.Start, start.Action);
        Assert.False(start.IsLocalFire);
    }

    [Fact]
    public void MultipleProjectiles_TrackedIndependently()
    {
        var planner = new ProjectileSoundPlanner();
        var a = Bolt();
        var b = Bolt();

        planner.Plan(new[] { (a, 10f), (b, 50f) });
        var alive = planner.Plan(new[] { (a, 200f) });   // b expired meanwhile

        // b → Stop; a → Update. Exactly those two commands.
        Assert.Equal(2, alive.Count);
        Assert.Contains(alive, c => c.Action == ProjectileSoundAction.Stop && ReferenceEquals(c.Projectile, b));
        Assert.Contains(alive, c => c.Action == ProjectileSoundAction.Update && ReferenceEquals(c.Projectile, a));

        // Planner no longer considers b active.
        Assert.Single(planner.Active);
        Assert.Contains(planner.Active, p => ReferenceEquals(p, a));
    }

    [Fact]
    public void TransientGoneThenRespawnedSameEntity_DistinguishesStaleFromFresh()
    {
        // Sanity for the reference-identity keying: once a projectile is
        // stopped it is forgotten, so a brand-new entity (even structurally
        // identical) is treated as a fresh spawn, never confused with the old.
        var planner = new ProjectileSoundPlanner();
        var first = Bolt();
        planner.Plan(new[] { (first, 10f) });
        planner.Plan(Array.Empty<(ProjectileEntity, float)>());   // stop first

        var second = Bolt();
        var cmds = planner.Plan(new[] { (second, 10f) });

        var start = Assert.Single(cmds);
        Assert.Equal(ProjectileSoundAction.Start, start.Action);
        Assert.Same(second, start.Projectile);
        Assert.NotSame(first, second);
    }
}
