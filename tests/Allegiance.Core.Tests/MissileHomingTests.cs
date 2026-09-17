using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P179 — domain-level proof of <see cref="Missile"/>'s new homing layer: real
/// per-tick acceleration, turn-rate-limited steering toward a
/// <see cref="Ship.SolveForImpact"/>-approximated lead point, the drop-the-
/// target-on-cluster-exit rule, and the two gates that keep the change purely
/// additive (a <c>target: null</c> missile flies the identical straight-line
/// constant-velocity course as before; a zero-acceleration missile type never
/// turns even with a target set). Pure-domain (no sockets), mirroring
/// <see cref="MissileUnitTests"/>' assembled-mission structure: build a started
/// mission, drop a <see cref="Missile"/> directly into a sector with a real
/// placed target <see cref="Ship"/>, and tick the mission in small fixed steps
/// (so each <c>OnUpdate</c> receives a known small <c>dt</c> and the turn-rate
/// limiter is exercised per-tick rather than in one giant chunk).
/// </summary>
public class MissileHomingTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            if (core.MissileTypes.Count == 0) continue;
            return core;
        }
        throw new InvalidOperationException("no static core with a missile type found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    /// <summary>A genuinely homing missile type from the real corpus: positive
    /// <c>InitialSpeed</c>, positive <c>Acceleration</c> (so the home gate,
    /// <c>missileigc.cpp:381</c>, fires), positive <c>TurnRate</c>, and a lifespan
    /// comfortably longer than a homing test's few hundred milliseconds.</summary>
    private static readonly Lazy<DataMissileTypeIGC> HomingMissileType = new(() =>
    {
        var core = RepresentativeCore.Value;
        var t = core.MissileTypes.Values
            .Where(m => m.InitialSpeed > 0f && m.Acceleration > 0f && m.TurnRate > 0f
                        && m.TurnRate >= 1.3f && m.TurnRate <= 3f
                        && m.Lifespan >= 4f && m.Lifespan < 16f)
            .OrderBy(m => m.Acceleration)          // a moderately-thrusting, agile homer: shallow lead, enough turn
            .ThenBy(m => m.TurnRate)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0)
            throw new InvalidOperationException("no balanced homing missile in corpus (turnRate∈[1.3,3], lifespan [4,16))");
        return t;
    });

    private static Mission NewStartedMission(out Sector sector)
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(core, SomeMap.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        sector = m.Sectors[0];
        return m;
    }

    /// <summary>Place a real (stationary, sideless, unpiloted) target ship in the
    /// sector at <paramref name="position"/>, mirroring the coexist-test recipe
    /// (<c>MissileCombatTests.cs:446-449</c>). The missile homes against its
    /// presence in the sector; its <see cref="Ship.CollisionVelocity"/> (zero) is
    /// the target velocity feeding the lead solve.</summary>
    private static Ship PlaceTarget(Mission mission, Sector sector, Vector3 position)
    {
        var hull = RepresentativeCore.Value.HullTypes.Values.First();
        var ship = new Ship((short)(9000 + position.GetHashCode() % 997), hull, position, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>Tick the mission in fixed small steps so each missile
    /// <c>OnUpdate</c> sees a known <c>dt</c>. Returns the missile after
    /// <paramref name="ticks"/> steps of <paramref name="dt"/> seconds.</summary>
    private static void Step(Mission mission, int ticks, double dt, Action<int>? perTick = null)
    {
        double t = mission.LastUpdate;
        for (int i = 0; i < ticks; i++)
        {
            t += dt;
            mission.Update(t);
            perTick?.Invoke(i);
        }
    }

    private static float AngleBetween(Vector3 a, Vector3 b)
    {
        float dot = Math.Clamp(a * b, -1f, 1f);   // operator* is the dot product
        return MathF.Acos(dot);
    }

    /// <summary>⭐ Test 1 — a missile fired with a stationary target directly
    /// ahead flies straight at it while its velocity magnitude GROWS tick over
    /// tick: the real per-tick acceleration (<c>Velocity += Forward·Acceleration·
    /// dt</c>, <c>missileigc.cpp:393-397</c>) is applied. Before P179 this
    /// velocity was constant.</summary>
    [Fact]
    public void Missile_with_target_ahead_accelerates_straight_at_it()
    {
        var mType = HomingMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        Vector3 p0 = new(0f, 0f, 0f);
        Vector3 vel0 = forward * mType.InitialSpeed;
        float gap = 120f;
        var target = PlaceTarget(mission, sector, p0 + forward * gap);

        var missile = new Missile(9111, sector, mType, p0, vel0, forward, mission.LastUpdate, target: target);
        sector.Add(missile);

        int ticks = 24;                             // 24 × 0.0625 s = 1.5 s ≪ lifespan
        const double dt = 0.0625;
        var lengths = new List<float>();
        Step(mission, ticks, dt, _ =>
        {
            target.Position = p0 + forward * gap;   // keep the target exactly stationary ahead
            lengths.Add(missile.Velocity.Length());
        });

        // Velocity magnitude grew tick over tick (never shrank materially).
        for (int i = 1; i < lengths.Count; i++)
            Assert.True(lengths[i] >= lengths[i - 1] - 1e-3f,
                $"velocity length must not shrink tick-over-tick: {lengths[i - 1]:F3} -> {lengths[i]:F3}");
        // …and grew by a real, substantial amount (≈ Acceleration · elapsed).
        float expectedGrowth = mType.Acceleration * (float)(ticks * dt);
        Assert.True(lengths[^1] - lengths[0] > 0.5f * expectedGrowth,
            $"velocity must grow ~Acceleration·t ({expectedGrowth:F2} m/s): {lengths[0]:F3} -> {lengths[^1]:F3}");

        // Straight at it: heading stays aligned with the target direction.
        Vector3 dir = (target.Position - missile.Position).Normalize();
        Assert.True(AngleBetween(missile.Forward, dir) < 0.06f,
            $"missile must fly straight at an ahead target, heading-error {AngleBetween(missile.Forward, dir):F3} rad");
    }

    /// <summary>⭐ Test 2 — a missile fired with a target off to one side turns
    /// toward it: the angle between its heading and the target direction
    /// converges down toward zero (turn-rate-limited steering, <c>missileigc.cpp
    /// :389</c>) rather than staying fixed at the launch heading.</summary>
    [Fact]
    public void Missile_turns_toward_an_offset_target_over_several_ticks()
    {
        var mType = HomingMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        Vector3 p0 = new(0f, 0f, 0f);
        Vector3 vel0 = forward * mType.InitialSpeed;
        Vector3 targetPos = new(150f, 25f, 0f);     // ~9.5° off the launch heading
        var target = PlaceTarget(mission, sector, targetPos);

        var missile = new Missile(9112, sector, mType, p0, vel0, forward, mission.LastUpdate, target: target);
        sector.Add(missile);

        float launchError = AngleBetween(forward, targetPos.Normalize());   // heading error if it never turned

        int ticks = 40;
        const double dt = 0.055;
        var angles = new List<float>();
        Step(mission, ticks, dt, i =>
        {
            target.Position = targetPos;            // keep the target exactly fixed
            Vector3 dir = (targetPos - missile.Position).Normalize();
            angles.Add(AngleBetween(missile.Forward, dir));
        });

        // The missile TURNS toward the target rather than keeping the launch
        // heading: its heading error to the target direction converges onto the
        // target line (dips to a small minimum) and it re-orients well off the
        // launch heading. (A straight-line missile would hold launchError forever.)
        float minError = angles.Min();
        Assert.True(minError < 0.015f * Math.Max(1f, launchError),
            $"the missile must turn onto the target line; min heading error {minError:F3} rad (launch error {launchError:F3})");
        Assert.True(angles[0] < launchError - 1e-3f,
            "the missile must begin turning toward the target on the first tick, not hold the launch heading");
        Assert.True(AngleBetween(missile.Forward, forward) > 0.2f,
            "the missile must have re-oriented its heading substantially off the launch direction while homing");
    }

    /// <summary>⭐ Test 3 — a missile fired with <c>target: null</c> flies
    /// straight (no turning: there is nothing to steer toward) but still
    /// THRUSTS every tick, exactly like a targeted one.
    /// <para>
    /// CORRECTED 2026-09-16 (found live, interactive playtest — "missiles are
    /// super slow"): this test previously asserted the opposite (constant
    /// velocity, no acceleration without a target) as the intended, safety-net
    /// behavior. Re-checked native directly: <c>CmissileIGC::Update</c>
    /// (<c>missileigc.cpp:379-397</c>) only gates the STEERING (<c>solve</c>/
    /// <c>TurnTo</c>) on <c>m_target</c> — the thrust itself
    /// (<c>deltaV = backward * (acceleration * dt); SetVelocity(...)</c>) runs
    /// unconditionally every tick for every missile, targeted or not. The
    /// previous assertion was a real, confirmed divergence from native (every
    /// real missile type in the corpus launches at a slow 0-25 units/s and
    /// relies entirely on this post-launch thrust to become fast — a
    /// target-less/dumbfire missile that never accelerated was crawling at
    /// that launch speed for its whole lifespan). Fixed in <c>Missile.cs</c>;
    /// this test now asserts the corrected, native-faithful behavior instead
    /// of the bug.
    /// </para></summary>
    [Fact]
    public void Missile_with_no_target_flies_straight_and_still_accelerates()
    {
        var mType = HomingMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        Vector3 p0 = new(0f, 0f, 0f);
        Vector3 vel0 = forward * mType.InitialSpeed;

        var missile = new Missile(9113, sector, mType, p0, vel0, forward, mission.LastUpdate);   // no target
        sector.Add(missile);

        int ticks = 20;
        const double dt = 0.07;

        // Expected motion: same discrete per-tick integration OnUpdate performs
        // (velocity += forward * acceleration * dt; position += velocity * dt),
        // computed independently here from the real corpus type's own fields.
        Vector3 expectVel = vel0;
        Vector3 expectPos = p0;
        for (int i = 0; i < ticks; i++)
        {
            expectVel += forward * mType.Acceleration * (float)dt;
            expectPos += expectVel * (float)dt;
        }

        Step(mission, ticks, dt, null);

        Assert.True(mType.Acceleration > 0f, "test corpus type must have real acceleration or this proves nothing");
        Assert.True((missile.Velocity - expectVel).Length() < 0.5f,
            $"no-target missile must still thrust every tick: velocity {missile.Velocity} vs expected {expectVel}");
        Assert.True(missile.Velocity.Length() > vel0.Length() + 1f,
            "no-target missile must be measurably faster than its launch speed after thrusting");
        Assert.True((missile.Position - expectPos).Length() < 1f,
            $"no-target missile must fly a straight (unsteered) accelerating line: {missile.Position} vs {expectPos}");
        // Heading untouched — nothing to steer toward without a target.
        Assert.Equal(new Vector3(1f, 0f, 0f), missile.Forward);
    }

    /// <summary>⭐ Test 4 — a target that leaves the missile's sector/cluster
    /// mid-flight causes the missile to drop it (<c>Target</c> becomes
    /// <c>null</c>) and revert to straight-line flight from that point on (no
    /// more turning) — the <c>missileigc.cpp:371-375</c> cluster-exit rule.
    /// <para>
    /// CORRECTED 2026-09-16 (same fix as the no-target test above): dropping
    /// the target stops STEERING only — the missile keeps thrusting every
    /// tick afterward (native's thrust application is unconditional,
    /// <c>missileigc.cpp:393-397</c>, outside the target-gated steering
    /// block). The previous version of this test asserted velocity went flat
    /// after the drop, which was the same confirmed bug as the no-target
    /// test, not real native behavior.
    /// </para></summary>
    [Fact]
    public void Missile_drops_a_target_that_leaves_its_sector()
    {
        var mType = HomingMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        Vector3 p0 = new(0f, 0f, 0f);
        Vector3 vel0 = forward * mType.InitialSpeed;
        Vector3 targetPos = new(120f, 40f, 0f);
        var target = PlaceTarget(mission, sector, targetPos);

        var missile = new Missile(9114, sector, mType, p0, vel0, forward, mission.LastUpdate, target: target);
        sector.Add(missile);

        const double dt = 0.067;
        // Phase 1: homing engaged, target in-sector → velocity grows.
        Step(mission, 10, dt, _ => target.Position = targetPos);
        Assert.NotNull(missile.Target);
        float velWhileHoming = missile.Velocity.Length();
        Assert.True(velWhileHoming > vel0.Length(), "must be accelerating toward the in-sector target");

        // Phase 2: the target leaves the missile's sector (removed from it)…
        sector.Remove(target);
        Assert.DoesNotContain(target, sector.Models);

        // …the missile drops it, stops turning (heading latches), but keeps
        // thrusting every tick along that now-fixed heading — speed keeps
        // climbing by a constant per-tick increment (acceleration·dt), not
        // flattening out.
        Vector3 headingAtDrop = missile.Forward;
        var dropLengths = new List<float>();
        Step(mission, 12, dt, _ => dropLengths.Add(missile.Velocity.Length()));
        Assert.Null(missile.Target);
        Assert.Equal(headingAtDrop, missile.Forward);   // no more steering once dropped
        Assert.True(dropLengths[^1] > dropLengths[0] + 1f,
            $"missile must keep thrusting after dropping its target: {dropLengths[0]} -> {dropLengths[^1]}");
        float expectedPerTickGrowth = mType.Acceleration * (float)dt;
        for (int i = 1; i < dropLengths.Count; i++)
        {
            Assert.True(Math.Abs((dropLengths[i] - dropLengths[i - 1]) - expectedPerTickGrowth) < 0.5f,
                $"per-tick speed growth after drop must match acceleration·dt (~{expectedPerTickGrowth}), got {dropLengths[i] - dropLengths[i - 1]} at tick {i}");
        }
    }

    /// <summary>⭐ Test 5 — a missile whose type has <c>Acceleration == 0f</c>
    /// (the dumbfire allowance, <c>missileigc.cpp:380-381</c>) never turns even
    /// with a target set: heading stays locked on the launch direction.</summary>
    [Fact]
    public void Zero_acceleration_missile_never_turns_even_with_a_target()
    {
        var homing = HomingMissileType.Value;
        var dumb = homing with { Acceleration = 0f };   // record-struct copy with acceleration zeroed
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        Vector3 p0 = new(0f, 0f, 0f);
        Vector3 vel0 = forward * homing.InitialSpeed;
        Vector3 targetPos = new(100f, 60f, 0f);         // well off to the side
        var target = PlaceTarget(mission, sector, targetPos);

        var missile = new Missile(9115, sector, dumb, p0, vel0, forward, mission.LastUpdate, target: target);
        sector.Add(missile);

        Step(mission, 25, 0.065, _ => target.Position = targetPos);

        // No turning: Forward is untouched from launch.
        Assert.Equal(new Vector3(1f, 0f, 0f), missile.Forward);
        // No acceleration: velocity unchanged.
        Assert.Equal(vel0, missile.Velocity);
    }
}
