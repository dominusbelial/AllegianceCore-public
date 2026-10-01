using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for the P462 <see cref="OwnShotEcho"/>: the client's own-shot
/// visual tracer emitted at the trigger frame (no round trip), mirroring
/// <c>Weapon.FireWeapon</c>'s cadence (first shot immediate, then DtimeBurst),
/// per-round physics (real projectile speed/lifespan, inherited velocity when not
/// absolute, bounded dispersion), the energy/ammo gate, and the server-echo
/// de-dup rule. No graphics device, no MonoGame types on the surface.
///
/// <para>Time vocabulary: cadence uses the seconds clock (<c>ctx.NowSeconds</c>);
/// the de-dup echo window and the per-round timestamp use the millisecond clock
/// (<c>ctx.NowLocalMs</c>). Tests advance both consistently.</para>
/// </summary>
public class OwnShotEchoTests
{
    private static readonly OwnShotFireState AllFire = new(ShipControlState.AllWeapons);

    private static OwnShotContext Ctx(
        double nowSec, long nowMs,
        float energyFrac = 1f, float ammoFrac = 1f,
        Vector3? vel = null) => new(
        new Vector3(0f, 0f, 0f),
        Orientation.Identity,          // forward (0,0,-1), up (0,1,0), right (1,0,0)
        vel ?? new Vector3(0f, 0f, 0f),
        energyFrac, ammoFrac, nowSec, nowMs);

    private static OwnShotWeapon W(
        int mount = 0, short typeId = 100, float dtimeBurst = 0.2f,
        float energyPerShot = 1f, short ammoPerShot = 0, float dispersion = 0f,
        float speed = 1000f, float lifespan = 2f, bool absoluteF = false,
        float minEnergyFrac = 0f, short singleShot = 1, short burst = 41) => new(
        mount, typeId, dtimeBurst, energyPerShot, ammoPerShot, dispersion,
        speed, lifespan, absoluteF, minEnergyFrac, singleShot, burst);

    // ── cadence ───────────────────────────────────────────────────────────

    [Fact]
    public void FirstShot_IsImmediate_ThenCadence()
    {
        var e = new OwnShotEcho();
        var w = new[] { W(dtimeBurst: 10f) };   // huge burst interval

        // Rising edge at t=0 fires immediately despite the 10 s cadence.
        Assert.Single(e.Update(w, AllFire, Ctx(0.0, 0)));
        // Still inside the burst interval → nothing more for 9 s.
        Assert.Empty(e.Update(w, AllFire, Ctx(1.0, 1000)));
        Assert.Empty(e.Update(w, AllFire, Ctx(5.0, 5000)));
        // 10 s elapsed → due again.
        Assert.Single(e.Update(w, AllFire, Ctx(10.0, 10000)));
    }

    [Fact]
    public void Cadence_MatchesDtimeBurst_WhileTriggerHeld()
    {
        var e = new OwnShotEcho();
        var w = new[] { W(dtimeBurst: 0.2f) };

        // t=0 rising edge → immediate first round.
        Assert.Single(e.Update(w, AllFire, Ctx(0.0, 0)));
        // 0.05 s later (inside the 0.2 s interval) → no new round.
        Assert.Empty(e.Update(w, AllFire, Ctx(0.05, 50)));
        // 0.15 s more (0.20 total) → exactly due again.
        var r1 = e.Update(w, AllFire, Ctx(0.20, 200));
        Assert.Single(r1);
    }

    [Fact]
    public void ReleaseThenRepress_IsImmediateAgain()
    {
        var e = new OwnShotEcho();
        var w = new[] { W(dtimeBurst: 5f) };

        Assert.Single(e.Update(w, AllFire, Ctx(0.0, 0)));      // press → fire
        Assert.Empty(e.Update(w, AllFire, Ctx(1.0, 1000)));    // still held, not due
        // Release (no fire bits) clears the mount's cadence…
        e.Update(w, new OwnShotFireState(0), Ctx(1.1, 1100));
        // …so the next press is an immediate first shot again, not 4 s away.
        Assert.Single(e.Update(w, AllFire, Ctx(1.15, 1150)));
    }

    // ── energy / ammo gate ────────────────────────────────────────────────

    [Fact]
    public void NoRounds_WhenEnergyCannotCoverShot()
    {
        var e = new OwnShotEcho();
        // EnergyPerShot 5, effective max energy 10 → needs fraction >= 0.5.
        var w = new[] { W(energyPerShot: 5f, minEnergyFrac: 0.5f) };

        Assert.Empty(e.Update(w, AllFire, Ctx(0.0, 0, energyFrac: 0.25f)));
        // Recovers to 0.6 → fires immediately.
        Assert.Single(e.Update(w, AllFire, Ctx(0.01, 10, energyFrac: 0.6f)));
    }

    [Fact]
    public void NoRounds_WhenAmmoPoolEmpty()
    {
        var e = new OwnShotEcho();
        var w = new[] { W(ammoPerShot: 1, minEnergyFrac: 0f) };   // ammo weapon, no energy cost

        Assert.Empty(e.Update(w, AllFire, Ctx(0.0, 0, ammoFrac: 0f)));
        Assert.Single(e.Update(w, AllFire, Ctx(0.02, 20, ammoFrac: 0.5f)));
    }

    // ── dispersion ────────────────────────────────────────────────────────

    [Fact]
    public void Dispersion_StaysWithinBounds()
    {
        var e = new OwnShotEcho();
        const float dispersion = 0.3f;                 // radians
        var w = new[] { W(dtimeBurst: 0.008f, dispersion: dispersion, speed: 100f) };
        var fwd = new Vector3(0f, 0f, -1f);            // identity forward

        double t = 0.0;
        long ms = 0;
        float maxAngularDeviation = 0f;
        int emitted = 0;
        for (int i = 0; i < 400; i++)
        {
            t += 0.009;                                 // step > DtimeBurst → fires most frames
            ms += 9;
            foreach (var r in e.Update(w, AllFire, Ctx(t, ms)))
            {
                emitted++;
                float dot = r.Forward.X * fwd.X + r.Forward.Y * fwd.Y + r.Forward.Z * fwd.Z;
                float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                maxAngularDeviation = MathF.Max(maxAngularDeviation, angle);
            }
        }

        Assert.True(emitted >= 300, $"expected a healthy burst sample, got {emitted}");
        // Every shot stays inside the dispersion cone (tiny margin for fp).
        Assert.True(maxAngularDeviation <= dispersion * 1.05f,
            $"max deviation {maxAngularDeviation} exceeded dispersion {dispersion}");
        // And the scatter is real (not a dead-on beam).
        Assert.True(maxAngularDeviation > dispersion * 0.5f,
            $"dispersion produced no real scatter (max {maxAngularDeviation})");
    }

    // ── inherited velocity ────────────────────────────────────────────────

    [Fact]
    public void InheritsLauncherVelocity_WhenNotAbsolute()
    {
        var shipVel = new Vector3(0f, 0f, -5f);   // drifting forward

        // Absolute projectile: only speed * forward.
        var abs = new OwnShotEcho();
        var wAbs = new[] { W(speed: 100f, absoluteF: true) };
        var rAbs = abs.Update(wAbs, AllFire, Ctx(0.0, 0, vel: shipVel)).Single();
        Assert.Equal(new Vector3(0f, 0f, -100f), rAbs.Velocity);

        // Relative projectile: speed * forward + ship velocity.
        var rel = new OwnShotEcho();
        var wRel = new[] { W(speed: 100f, absoluteF: false) };
        var rRel = rel.Update(wRel, AllFire, Ctx(0.0, 0, vel: shipVel)).Single();
        Assert.Equal(new Vector3(0f, 0f, -105f), rRel.Velocity);
    }

    // ── server-echo de-dup rule ───────────────────────────────────────────

    [Fact]
    public void Dedup_OwnEcho_IsDropped()
    {
        var e = new OwnShotEcho();
        e.Update(new[] { W(typeId: 7) }, AllFire, Ctx(0.0, 1000));
        // Echo arrives 100 ms later, same type, spawned at our position → drop.
        Assert.True(e.ShouldDropServerEcho(7, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), 1100));
    }

    [Fact]
    public void Dedup_ForeignRound_IsKept()
    {
        var e = new OwnShotEcho();
        e.Update(new[] { W(typeId: 7) }, AllFire, Ctx(0.0, 1000));

        // Different type → not our echo → keep.
        Assert.False(e.ShouldDropServerEcho(8, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), 1100));
        // Same type but spawned >150 units from our position → not our muzzle → keep.
        Assert.False(e.ShouldDropServerEcho(7, new Vector3(0f, 0f, 300f), new Vector3(0f, 0f, 0f), 1100));
    }

    [Fact]
    public void Dedup_RoundOutsideEchoWindow_IsKept()
    {
        var e = new OwnShotEcho();
        e.Update(new[] { W(typeId: 7) }, AllFire, Ctx(0.0, 1000));
        // Echo arrives 500 ms later — past EchoWindowMs (400) → kept.
        Assert.False(e.ShouldDropServerEcho(7, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), 1500));
    }

    [Fact]
    public void Dedup_UnmatchedRound_FallsBackToServerCopy()
    {
        var e = new OwnShotEcho();
        // No local round was emitted (e.g. energy/ammo gate blocked, or the
        // window lapsed) → the server echo is NOT dropped and renders as-is.
        Assert.False(e.ShouldDropServerEcho(7, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), 0));
    }

    /// <summary>P465c — the local tracer leaves the mount's hardpoint frame
    /// (ship position + muzzle offset × orientation), not the ship centre, so
    /// the client's own-shot echo matches where the server's real round spawns.</summary>
    [Fact]
    public void LocalRound_SpawnsAtMuzzleOffset_NotShipCentre()
    {
        var e = new OwnShotEcho();
        var w = new OwnShotWeapon(
            MountId: 0, ProjectileTypeId: 100, DtimeBurst: 0.2f, EnergyPerShot: 1f,
            CAmmoPerShot: 0, Dispersion: 0f, ProjectileSpeed: 1000f, ProjectileLifespan: 2f,
            AbsoluteF: false, MinEnergyFraction: 0f, SingleShotSoundId: 1, BurstSoundId: 41,
            MuzzleOffset: new Vector3(10f, 0f, 0f));
        var rounds = e.Update(new[] { w }, AllFire, Ctx(0.0, 0));
        var r = Assert.Single(rounds);
        // Ctx position (0,0,0), identity orientation → spawn at offset (10,0,0).
        Assert.Equal(new Vector3(10f, 0f, 0f), r.Position);
        // And the muzzle shift does not disturb the de-dup (matches by type +
        // proximity radius + window, not exact position).
        Assert.True(e.ShouldDropServerEcho(100, new Vector3(10f, 0f, 0f), new Vector3(0f, 0f, 0f), 100));
    }
}
