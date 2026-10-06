using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P181 — domain-level proof of <see cref="Ship.UpdateMissileLock"/> (the real
/// per-tick lock-quality buildup ported from native
/// <c>CmagazineIGC::Update</c>, <c>Igc/magazineigc.cpp:104-176</c>) and of the
/// lock-fraction threading into <see cref="Missile"/>'s homing lead solve.
/// Pure-domain (no sockets): assemble a started mission, place a real firing
/// <see cref="Ship"/> and a real target <see cref="Ship"/> in one sector with
/// opposing <see cref="Ship.SideId"/>s, set <see cref="Ship.CommandTarget"/>,
/// and call <see cref="Ship.UpdateMissileLock"/> directly with a known <c>dt</c>
/// (it is a directly-callable capability, mirroring <see cref="Ship.FireMissile"/>'s
/// own "caller supplies the type directly" precedent — no control-input wiring).
/// </summary>
public class MissileLockTests
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

    /// <summary>A missile type suited to exercising the lock mechanic: it
    /// <em>requires</em> a lock (<see cref="DataMissileTypeIGC.MaxLock"/> &gt; 0),
    /// builds over a measurable <see cref="DataMissileTypeIGC.LockTime"/> (1–3 s),
    /// has a sane <see cref="DataMissileTypeIGC.LockAngle"/> (0.1–1.0 rad, small
    /// enough to place a target visibly outside the cone), and enough range
    /// (<see cref="DataMissileTypeIGC.InitialSpeed"/>/<see cref="DataMissileTypeIGC.Lifespan"/>)
    /// to hold a ~100-unit target inside <c>maxRange</c>.</summary>
    private static readonly Lazy<DataMissileTypeIGC> LockMissileType = new(() =>
    {
        var core = RepresentativeCore.Value;
        var t = core.MissileTypes.Values
            .Where(m => m.MaxLock > 0f && m.LockTime >= 1f && m.LockTime <= 3f
                        && m.LockAngle >= 0.1f && m.LockAngle <= 1.0f
                        && m.InitialSpeed > 0f && m.Lifespan >= 2f)
            .OrderBy(m => m.LockTime)
            .FirstOrDefault();
        if (t.LockTime < 1f)   // default(all-zero) sentinel ⇒ nothing matched
            throw new InvalidOperationException("no lock-eligible missile type in corpus (maxLock>0, lockTime∈[1,3], lockAngle∈[0.1,1], range)");
        return t with { ReadyTime = 0f };   // P549: isolate homing from the arming delay (missileigc.cpp:363)
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

    /// <summary>An <see cref="Orientation"/> facing <paramref name="forward"/>
    /// (normalized), used to point the firing ship's nose.</summary>
    private static Orientation OrientForward(Vector3 forward)
    {
        var o = new Orientation();
        o.Set(forward);
        return o;
    }

    /// <summary>Place a real, stationary ship in <paramref name="sector"/> at
    /// <paramref name="position"/> with the given side, mirroring
    /// <c>MissileHomingTests.PlaceTarget</c>'s assembled-mission recipe plus a
    /// settable <see cref="Ship.SideId"/>.</summary>
    private static Ship PlaceShip(Mission mission, Sector sector, short id, Vector3 position, short? sideId, Orientation? orientation = null)
    {
        var hull = RepresentativeCore.Value.HullTypes.Values.First();
        var ship = new Ship(id, hull, position, Vector3.Zero, orientation ?? new Orientation());
        ship.SideId = sideId;
        mission.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>⭐ Test 1 — with the command target dead ahead (angle-off-bow 0)
    /// the lock builds toward 1.0 over repeated <see cref="Ship.UpdateMissileLock"/>
    /// calls, reaching it after ≈ <see cref="DataMissileTypeIGC.LockTime"/>·ECM
    /// seconds of continuous dead-on aim (the in-cone rate is
    /// <c>1/(lockTime·ECM)</c> per second — ECM the target hull's decoded
    /// <see cref="DataHullTypeIGC.Ecm"/>, <c>magazineigc.cpp:148-167</c>).</summary>
    [Fact]
    public void MissileLock_builds_to_full_dead_ahead_after_lock_time()
    {
        var mType = LockMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9411, new Vector3(0, 0, 0), 0, OrientForward(forward));
        var target = PlaceShip(mission, sector, 9511, forward * 100f, 1);
        firing.CommandTarget = target;

        const double dt = 0.020;
        float lockTime = mType.LockTime;
        // Native's dead-on in-cone rate is 1/(lockTime·ECM) per second — the
        // target hull's decoded ECM (DataHullTypeIGC.Ecm) scales the buildup, so
        // reaching full lock takes lockTime·ECM seconds of continuous dead-on aim.
        float ecm = target.HullType.Ecm > 0f ? target.HullType.Ecm : 1f;
        int ticks = (int)Math.Ceiling(lockTime * ecm / dt) + 2;   // a little past full-lock time

        var samples = new List<float>();
        for (int i = 0; i < ticks; i++)
        {
            firing.UpdateMissileLock(mType, dt);
            samples.Add(firing.MissileLock);
        }

        // Monotone, strictly increasing (it never drops while aim stays dead-on).
        for (int i = 1; i < samples.Count; i++)
            Assert.True(samples[i] > samples[i - 1] - 1e-6f, $"lock must not decrease while dead-on: {samples[i - 1]:F4} -> {samples[i]:F4}");
        // …and reaches (clamped) full lock once the summed dt crosses LockTime.
        Assert.True(firing.MissileLock >= 0.998f,
            $"dead-ahead lock must reach ~1.0 after ~LockTime={lockTime:F2}s of aim; got {firing.MissileLock:F4}");
    }

    /// <summary>⭐ Test 2 — with the target well outside <see cref="DataMissileTypeIGC.LockAngle"/>
    /// (off-cone ⇒ the decay branch, <c>magazineigc.cpp:162-167</c>), the lock
    /// never builds: it stays at 0 from 0.</summary>
    [Fact]
    public void MissileLock_never_builds_when_target_is_well_off_cone()
    {
        var mType = LockMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9412, new Vector3(0, 0, 0), 0, OrientForward(forward));
        var target = PlaceShip(mission, sector, 9512, -forward * 100f, 1);   // directly BEHIND ⇒ angle-off-bow = π ≫ lockAngle
        firing.CommandTarget = target;

        for (int i = 0; i < 80; i++)
            firing.UpdateMissileLock(mType, 0.040);

        Assert.Equal(0f, firing.MissileLock);
    }

    /// <summary>⭐ Test 3 — the derived off-cone DECAY rate is asserted numerically,
    /// not just "it goes down": a ship holding partial lock that lets the target
    /// stray outside <see cref="DataMissileTypeIGC.LockAngle"/> decays by exactly
    /// <c>(lockAngle − angleOffBow)/lockAngle</c> per second, ÷ the target hull's
    /// ECM (the two <c>lockTime</c> factors cancel; ECM does not,
    /// <c>magazineigc.cpp:161-167</c>), clamped to 0.
    /// Here angle-off-bow = π (target directly behind).</summary>
    [Fact]
    public void MissileLock_decays_at_the_derived_off_cone_rate()
    {
        var mType = LockMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9413, new Vector3(0, 0, 0), 0, OrientForward(forward));
        var target = PlaceShip(mission, sector, 9513, forward * 100f, 1);
        firing.CommandTarget = target;

        // Build a partial lock dead-ahead (rate 1/(lockTime·ECM) per sec) to L0 ≈ 0.5.
        const double dt = 0.010;
        float ecm = target.HullType.Ecm > 0f ? target.HullType.Ecm : 1f;
        float lockTime = mType.LockTime;
        int buildTicks = (int)(0.5f * lockTime * ecm / (float)dt);
        for (int i = 0; i < buildTicks; i++)
            firing.UpdateMissileLock(mType, dt);
        float l0 = firing.MissileLock;
        Assert.True(l0 is > 0.4f and < 0.62f, $"expected partial lock ~0.5, got {l0:F4}");

        // Now swing the target to dead astern (angle-off-bow = π) for ONE step.
        target.Position = -forward * 100f;
        float angleOffBow = MathF.PI;
        // Off-cone: v is scaled by lockTime then divided by lockTime·ECM, leaving
        // (lockAngle−angleOffBow)/lockAngle per second, itself ÷ ECM (the same
        // hull ECM that scales the in-cone rate, magazineigc.cpp:165-167).
        float offConeRate = (mType.LockAngle - angleOffBow) / mType.LockAngle;   // negative per-second rate (v.unscaled)
        float expected = Math.Clamp(l0 + offConeRate * (float)dt / ecm, 0f, 1f);

        firing.UpdateMissileLock(mType, dt);

        Assert.True(firing.MissileLock < l0, "moving off-cone must decay the lock");
        Assert.Equal(expected, firing.MissileLock, 3);
    }

    /// <summary>⭐ Test 4 — a mid-ripcord ship (<see cref="Ship.IsRipcordActive"/>) has
    /// its lock reset to 0 immediately, regardless of aim (<c>magazineigc.cpp:104-105</c>).</summary>
    [Fact]
    public void Active_ripcord_resets_lock_to_zero_regardless_of_aim()
    {
        var mType = LockMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9414, new Vector3(0, 0, 0), 0, OrientForward(forward));
        var target = PlaceShip(mission, sector, 9514, forward * 100f, 1);
        firing.CommandTarget = target;

        for (int i = 0; i < 40; i++) firing.UpdateMissileLock(mType, 0.020);
        Assert.True(firing.MissileLock > 0.5f, "precondition: lock should be partial before ripcord");

        firing.SetRipcordModel(new Ship.RipcordTarget(0, 0, 0));   // any active ripcord target
        Assert.True(firing.IsRipcordActive);

        firing.UpdateMissileLock(mType, 0.020);
        Assert.Equal(0f, firing.MissileLock);
    }

    /// <summary>⭐ Test 5 — no command target ⇒ no lock (stays/resets to 0).</summary>
    [Fact]
    public void No_command_target_leaves_lock_at_zero()
    {
        var mType = LockMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9415, new Vector3(0, 0, 0), 0, OrientForward(forward));
        _ = PlaceShip(mission, sector, 9515, forward * 100f, 1);   // a hostile ship that is NOT the command target

        for (int i = 0; i < 40; i++) firing.UpdateMissileLock(mType, 0.020);
        Assert.Equal(0f, firing.MissileLock);
    }

    /// <summary>⭐ Test 6 — a same-side command target never builds lock (the
    /// hostile-side gate, <c>magazineigc.cpp:128</c>).</summary>
    [Fact]
    public void Same_side_command_target_never_builds_lock()
    {
        var mType = LockMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9416, new Vector3(0, 0, 0), 0, OrientForward(forward));
        var target = PlaceShip(mission, sector, 9516, forward * 100f, 0);   // same side as the firer
        firing.CommandTarget = target;

        for (int i = 0; i < 40; i++) firing.UpdateMissileLock(mType, 0.020);
        Assert.Equal(0f, firing.MissileLock);
    }

    /// <summary>⭐ Test 7 — a missile type with <see cref="DataMissileTypeIGC.MaxLock"/>
    /// == 0 needs no lock at all: instant full lock (1f) regardless of aim
    /// (<c>magazineigc.cpp:145-146</c>), even with the target dead astern
    /// (off-cone).</summary>
    [Fact]
    public void MaxLock_zero_gives_instant_full_lock_regardless_of_aim()
    {
        var mType = LockMissileType.Value;
        var dumb = mType with { MaxLock = 0f };
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9417, new Vector3(0, 0, 0), 0, OrientForward(forward));
        var target = PlaceShip(mission, sector, 9517, -forward * 100f, 1);   // dead astern ⇒ far off-cone
        firing.CommandTarget = target;

        firing.UpdateMissileLock(dumb, 0.050);
        Assert.Equal(1f, firing.MissileLock);
    }

    /// <summary>⭐ Test 8 — <see cref="Ship.FireMissile"/>'s supplied
    /// <c>lockFraction</c> reaches the <see cref="Missile"/> (stored on
    /// <see cref="Missile.LockFraction"/>) and genuinely changes the homing lead
    /// solve's <c>deltaV = target.Velocity·lockFraction − myVelocity</c>
    /// (<c>missileigc.cpp:386</c>): against a target reporting a sideways velocity,
    /// the <c>lockFraction = 1</c> missile leads into that velocity while the
    /// <c>lockFraction = 0</c> missile treats the target as stationary and aims at
    /// its current position — a measurable heading difference. The <b>default</b>
    /// (no argument) reproduces Stage 1's exact <c>m_lock ≡ 1.0</c> behavior
    /// (<c>LockFraction == 1f</c>).</summary>
    [Fact]
    public void FireMissile_lockFraction_threads_through_and_changes_homing()
    {
        var mType = LockMissileType.Value;
        var mission = NewStartedMission(out var sector);
        Vector3 forward = new(1f, 0f, 0f);
        var firing = PlaceShip(mission, sector, 9431, new Vector3(0, 0, 0), 0, OrientForward(forward));
        var target = PlaceShip(mission, sector, 9531, forward * 150f, 1);
        target.Velocity = new Vector3(0f, 30f, 0f);   // reports a sideways (crossing) velocity
        firing.CommandTarget = target;

        double now = mission.LastUpdate;
        var noLock = firing.FireMissile(mType, now, target, lockFraction: 0f);
        var fullLock = firing.FireMissile(mType, now, target, lockFraction: 1f);
        var defLock = firing.FireMissile(mType, now, target);                       // default (Stage 1 regression net)

        Assert.Equal(0f, noLock.LockFraction);
        Assert.Equal(1f, fullLock.LockFraction);
        Assert.Equal(1f, defLock.LockFraction);   // default reproduces m_lock ≡ 1.0 exactly

        // Tick a few small steps, keeping the target POSITION fixed so the only
        // varying term is the reported velocity feeding the lead solve.
        double t = mission.LastUpdate;
        const double dt = 0.030;
        for (int i = 0; i < 6; i++)
        {
            t += dt;
            target.Position = forward * 150f;
            mission.Update(t);
        }

        // lockFraction=1 leads INTO the target's +Y velocity; lockFraction=0 aims
        // at the current (y=0) position, so the full-lock missile's forward carries
        // a distinctly larger +Y (lead) component.
        Assert.True(fullLock.Forward.Y > noLock.Forward.Y + 0.003f,
            $"lockFraction must change the lead: full {fullLock.Forward.Y:F4} vs no-lock {noLock.Forward.Y:F4}");
        // The default (1f) tracks the full-lock control closely.
        Assert.True(MathF.Abs(defLock.Forward.Y - fullLock.Forward.Y) < 0.002f,
            $"default (Stage 1) must reproduce full-lock homing: def {defLock.Forward.Y:F4} vs full {fullLock.Forward.Y:F4}");
    }
}
