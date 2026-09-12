using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Sim;

/// <summary>
/// A live missile as a sector model — the port of <c>CmissileIGC</c>
/// (<c>src/Igc/missileIGC.cpp</c>, ~900 lines), kept to this slice's need: a
/// real <see cref="Model"/> that lives in a <see cref="Sector"/>, moves in a
/// straight line at a constant velocity inherited from its firing ship, ticks
/// with the sector like any other model, and <b>expires</b> after its decoded
/// <see cref="DataMissileTypeIGC.Lifespan"/>, removing itself from the sector.
/// Structurally this mirrors <see cref="Projectile"/> exactly — a non-homing
/// missile is a projectile whose lifetime is data — so the two are siblings,
/// deliberately <em>not</em> a shared base (each is a port of a different,
/// unrelated C++ class).
/// </summary>
/// <remarks>
/// Honest-deferral statement, same style as P87's "no shield regeneration":
/// the <see cref="DataMissileTypeIGC"/> fields that make a missile a missile —
/// <c>TurnRate</c>, <c>Acceleration</c>, <c>LockTime</c>, <c>MaxLock</c>,
/// <c>LockAngle</c>, <c>ChaffResistance</c>, <c>Power</c> etc. — are read and
/// stored here, but <b>not acted on</b> this slice. They are carried on the
/// type record so a later homing/combat task has everything it needs without
/// touching the loader. Specifically <em>not</em> implemented (separate
/// follow-ups):
/// <list type="bullet">
/// <item><b>Homing/turning/target-tracking.</b> <c>TurnRate</c>/<c>LockTime</c>/
/// <c>MaxLock</c> are stored, never consulted. This missile flies straight, in
/// one unaccelerated line (advancing <c>position += velocity·dt</c> like
/// <see cref="Projectile.OnUpdate"/>, <c>projectileIGC.cpp:120-144</c>).</item>
/// <item><b>Collision/damage — now resolved (P91).</b> Since P91, a missile is a
/// first-class participant in <see cref="Sector.ResolveCombatCollisions"/>: it
/// builds a sphere collider from <c>MissileType.Radius</c>, lands its decoded
/// <c>Power</c> of its decoded <c>DamageType</c> on the first cross-side ship it
/// hits (via <see cref="Ship.ReceiveDamage"/>, shields included), is removed
/// from the sector on impact like a spent projectile, and raises
/// <see cref="IIgcSite.MissileDestroyedEvent"/> so a same-side peer who saw the
/// launch also learns of the destruction — the identical consistency fix that
/// the port's <c>HandleCollision</c> half represents (this retired the earlier
/// "collision deferred" line of P90, parallel to how <see cref="Projectile"/>'s
/// collision became a later task after its existence slice). Still deferred:
/// homing/turning, area-of-effect (<c>BlastPower</c>/<c>BlastRadius</c> are read
/// but not acted on — a missile is single-target, same as a projectile today),
/// and chaff/spoofing (<c>ChaffResistance</c>).</item>
/// </list>
/// The one behavioural addition beyond copying <see cref="Projectile"/>'s
/// shape verbatim is the destruction event: on expiry, in addition to
/// <c>sector.Remove(this)</c>, the missile raises the host seam's
/// <see cref="IIgcSite.MissileDestroyedEvent"/> so the server can broadcast a
/// real <c>FM_S_MISSILE_DESTROYED(83)</c> — the same idea as
/// <c>Ship.ReceiveDamage</c> firing <see cref="IIgcSite.KillShipEvent"/> on a
/// real ship death (<c>Sim/Ship.cs:494</c>), applied to a missile's own
/// expiry instead of a ship's death. The site is derived from the launcher (a
/// <see cref="Ship"/>'s <see cref="Ship.Mission"/>.<see cref="Mission.Site"/>);
/// a directly-constructed missile with no launcher has no site and its
/// destruction event is a no-op.
/// </remarks>
public sealed class Missile : Model, IOrdnance, IMissileIgc
{
    private readonly Sector _sector;
    private readonly double _timeExpire;

    /// <summary>Construct a missile in <paramref name="sector"/> at
    /// <paramref name="position"/> with <paramref name="velocity"/>, facing
    /// <paramref name="forward"/>, born at <paramref name="now"/>. Its clock is
    /// seeded to <paramref name="now"/> so the first sector tick is a genuine
    /// full chunk, not one oversized step equal to the mission age (same
    /// ship-clock fix <see cref="Projectile"/> documents). Expiry is
    /// <c>now + missileType.Lifespan</c>.</summary>
    public Missile(
        short id,
        Sector sector,
        DataMissileTypeIGC missileType,
        Vector3 position,
        Vector3 velocity,
        Vector3 forward,
        double now,
        Model? launcher = null,
        Model? target = null,
        float lockFraction = 1f)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        MissileType = missileType;
        Velocity = velocity;
        Forward = forward.Normalize();
        Launcher = launcher;
        Target = target;
        LockFraction = lockFraction;
        _timeExpire = now + missileType.Lifespan;
        InitClock(now);
    }

    /// <summary>The decoded missile type from the static core — the source of
    /// <c>Lifespan</c>, <c>InitialSpeed</c>, and (read-but-unused this slice)
    /// the homing/turning fields. Never hardcoded; the task rule.</summary>
    public DataMissileTypeIGC MissileType { get; }

    /// <summary>The linear velocity of this missile — constant for a straight-line
    /// (no-target, or zero-acceleration) missile, but accelerated along the
    /// current <see cref="Forward"/> each homing tick when it has a target and
    /// <see cref="MissileType"/>.<see cref="DataMissileTypeIGC.Acceleration"/>
    /// &gt; 0 (P179). Inherited at construction from the firing ship +
    /// <c>InitialSpeed</c>.</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>The normalised heading this missile flies along. Mutable since
    /// P179: a homing missile re-orients it each tick toward its lead point
    /// (seeded at construction with the launch facing; used to seed its
    /// orientation / collider facing).</summary>
    public Vector3 Forward { get; private set; }

    /// <summary>The model this missile is hunting (<c>m_target</c>,
    /// <c>missileigc.cpp:381</c>). Settable privately because homing can clear
    /// it to <c>null</c> when the target leaves the missile's cluster/sector
    /// (<c>missileigc.cpp:371-375</c>), and — since P182 — <c>internal</c>
    /// because <see cref="Ship.DeployChaff"/> reassigns it onto a freshly-deployed
    /// <see cref="Chaff"/> decoy on a successful spoof (<c>pmissile-&gt;SetTarget(c)</c>,
    /// <c>FedSrv.CPP:5705</c>); the one place in the sim that retargets a missile
    /// besides the missile's own homing logic. <c>null</c> ⇒ the missile flies the
    /// straight-line unaccelerated course it did before P179 (no homing, no
    /// turning, no thrust).</summary>
    public Model? Target { get; internal set; }

    /// <summary>P181 — the lock quality (<c>m_lock</c>) this missile was fired
    /// with, defaulting to <c>1f</c> (Stage 1's exact prior behavior:
    /// <c>m_lock ≡ 1.0</c>). Scales the target velocity fed into the homing
    /// lead solve each tick (<c>missileigc.cpp:386</c>,
    /// <c>deltaV = target.Velocity·lockFraction − myVelocity</c>), so a missile
    /// fired at reduced lock-quality homes less decisively — the consumer of a
    /// <see cref="Ship.UpdateMissileLock"/>-built lock value (P181). Supplied
    /// directly by the caller (e.g. <see cref="Ship.FireMissile"/>'s
    /// <c>lockFraction</c> argument); stored for inspection.</summary>
    public float LockFraction { get; }

    /// <summary>The absolute time (mission-seconds) at which this missile expires
    /// and removes itself.</summary>
    public double TimeExpire => _timeExpire;

    /// <summary>The sector this missile lives in — the sector that owns it and that
    /// it removes itself from on expiry.</summary>
    public Sector OwnerSector => _sector;

    /// <inheritdoc/>
    float IOrdnance.Radius => MissileType.Radius;
    /// <inheritdoc/>
    byte IOrdnance.DamageType => MissileType.DamageType;
    /// <inheritdoc/>
    float IOrdnance.Power => MissileType.Power;
    /// <inheritdoc/>
    float IOrdnance.BlastPower => MissileType.BlastPower;
    /// <inheritdoc/>
    float IOrdnance.BlastRadius => MissileType.BlastRadius;

    /// <summary>The model that launched this missile (<c>m_launcher</c>), mirroring
    /// <see cref="Projectile.Launcher"/>. Here it doubles as the route to the host
    /// seam: a <see cref="Ship"/> launcher exposes the authoritative
    /// <see cref="Mission.Site"/> (via <see cref="Ship.Mission"/>), which the
    /// expiry path uses to raise the destruction event. Optional (a directly
    /// constructed missile has no launcher and raises nothing).</summary>
    public Model? Launcher { get; }

    /// <inheritdoc/>
    public override Vector3 CollisionVelocity => Velocity;

    /// <summary>
    /// One chunk's work: advance the straight-line position by
    /// <c>velocity·dt</c> and expire once <paramref name="now"/> reaches
    /// <see cref="TimeExpire"/>. On expiry, remove <c>this</c> from
    /// <see cref="OwnerSector"/> and raise the host seam's
    /// <see cref="IIgcSite.MissileDestroyedEvent"/> so the server can broadcast a
    /// real <c>FM_S_MISSILE_DESTROYED(83)</c> to the same-side peers who saw it
    /// launch. An area-effect missile (<see cref="MissileType.BlastPower"/> ≠ 0)
    /// that times out without hitting anything still detonates at its
    /// <em>current</em> position before removal — <c>CmissileIGC::Update</c>'s
    /// <c>Explode(GetPosition())</c> (<c>missileigc.cpp:356-360,516-532</c>), a
    /// real <see cref="Explosion"/> handed to <see cref="Sector.ResolveExplosion"/>.
    /// Removing <c>this</c> from the sector during this call is safe: the
    /// sector ticks over a snapshot (<c>Sector.Update</c>), and a fire-at-expiry
    /// never repeats (expiry is latched by <see cref="TimeExpire"/>, monotone).
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        // P179 — the real homing/turning layer of native's Update, performed
        // BEFORE position integration (native's order: cluster-drop → turn →
        // thrust → TmodelIGC::Update's position step, missileigc.cpp:371-397,419).
        // Deliberate simplifications vs native, stated plainly (both are the
        // established "approximate, don't port the heavyweight original" choices
        // this port applies consistently — cf. the quartic-solve note below):
        //   (a) m_lock is now the real per-launch <see cref="LockFraction"/>
        //       (P181; default 1f reproduces Stage 1's m_lock ≡ 1.0 exactly),
        //       so dV = target.Velocity·lockFraction − myVelocity
        //       (missileigc.cpp:386).
        //   (b) the lead-point prediction reuses the already-ported quadratic
        //       Ship.SolveForImpact (P168's port of common.cpp:59-119) instead of
        //       porting native's quartic static solve(...) (missileigc.cpp:260-
        //       352) — the missile is modelled as coasting at its *current* speed
        //       for the prediction only; its real acceleration is still applied
        //       to Velocity every tick below. (c_gaTurnRateMissile, missileigc.cpp
        //       :389, is also omitted — no per-side global-attribute-set concept.)
        if (Target is { } t)
        {
            // Native drops the target outright when it leaves the missile's
            // cluster (missileigc.cpp:371-375). The port's cluster == sector, and
            // its membership primitive is the sector's model list (Sector.Add/
            // Remove, the same mechanism that backs Ship.Sector).
            if (!_sector.Models.Contains(t))
            {
                Target = null;   // lost our target — revert to straight line
            }
            else if (MissileType.Acceleration > 0f)
            {
                // Lead point: predict the intercept direction as if the missile
                // coasted at its current speed (radius 0 ⇒ aim at the predicted
                // centre, the dP + tImpact·dV target of native's TurnTo,
                // missileigc.cpp:389). SolveForImpact returns direction = Zero on
                // no-solution; fall back to aiming at the target's current
                // position so a temporarily-uncatchable target still gets chased.
                Vector3 deltaP = t.Position - Position;
                Vector3 deltaV = t.CollisionVelocity * LockFraction - Velocity;   // m_lock → LockFraction (a); Model.CollisionVelocity (Ship overrides) is the target's velocity
                float speed = Velocity.Length();                     // coast-at-current-speed predict (b)
                Ship.SolveForImpact(deltaP, deltaV, speed, 0f, out Vector3 direction);
                if (direction.LengthSquared() <= 1e-6f)
                    direction = deltaP.Normalize();                  // no intercept this tick → aim at current pos
                if (direction.LengthSquared() > 1e-6f)
                    Forward = RotateToward(Forward, direction, MissileType.TurnRate * (float)dt);

                // Thrust: accelerate along the JUST-UPDATED heading — native's
                // backward·(acceleration·dt) then SetVelocity(myVelocity − deltaV)
                // (missileigc.cpp:393-397) is a forward-acceleration written
                // backwards.
                Velocity += Forward * MissileType.Acceleration * (float)dt;
            }
        }

        Position += Velocity * (float)dt;
        if (now >= _timeExpire)
        {
            if (MissileType.BlastPower != 0f)
            {
                // Native projects the blast to the exact expiry instant
                // (missileigc.cpp:356-360, Explode at GetPosition(); the projectile
                // Update it inherits unwinds to the straight-line burnout point).
                // Our per-chunk advance has already overshot `now` past expiry, so
                // unwind that overshoot to the precise position at _timeExpire.
                var expiryPos = Position - Velocity * (float)(now - _timeExpire);
                var blast = new Explosion(
                    MissileType.DamageType,
                    MissileType.BlastPower,
                    MissileType.BlastRadius,
                    _timeExpire,
                    expiryPos,
                    Launcher);
                _sector.ResolveExplosion(blast, _timeExpire);
            }
            _sector.Remove(this);
            // The destruction-event broadcast: same shape as Ship.ReceiveDamage
            // raising KillShipEvent on a real death (Ship.cs:494), applied to the
            // missile's own expiry. Site derived from the launcher (a Ship's
            // mission host); a launcher-less missile has no site and raises nothing.
            if (Launcher is Ship launcherShip)
                launcherShip.Mission?.Site.MissileDestroyedEvent(this);
        }
    }

    /// <summary>
    /// P179 — the turn-rate limiter: rotate unit <paramref name="current"/>
    /// toward unit <paramref name="target"/> along the shortest arc by at most
    /// <paramref name="maxRadians"/>, returning the resulting unit heading.
    /// A dedicated helper (not a <see cref="Ship.SteerToward"/> reuse) because
    /// that Ship method writes a ship's throttle/<see cref="Controls"/> model
    /// a missile does not have — a missile just re-orients its Forward and
    /// re-thrusts directly (missileigc.cpp:389-397). Implemented as a spherical
    /// interpolation of the shortest arc clamped to <c>maxRadians</c>: the exact
    /// per-tick reorientation <c>Orientation::TurnTo(..., TurnRate·dt)</c>
    /// expresses (missileigc.cpp:389).
    /// </summary>
    private static Vector3 RotateToward(Vector3 current, Vector3 target, float maxRadians)
    {
        float dot = Math.Clamp(current * target, -1f, 1f);   // operator* is the dot product (Vector3.cs:44)
        float angle = MathF.Acos(dot);
        if (angle <= maxRadians || angle <= 1e-6f)
            return target;
        float sin = MathF.Sin(angle);
        float t = maxRadians / angle;                        // rotate the full remaining arc by this fraction
        float s0 = MathF.Sin((1f - t) * angle) / sin;        // slerp weights on the shortest arc
        float s1 = MathF.Sin(t * angle) / sin;
        return current * s0 + target * s1;
    }
}
