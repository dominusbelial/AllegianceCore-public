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
        Model? launcher = null)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        MissileType = missileType;
        Velocity = velocity;
        Forward = forward.Normalize();
        Launcher = launcher;
        _timeExpire = now + missileType.Lifespan;
        InitClock(now);
    }

    /// <summary>The decoded missile type from the static core — the source of
    /// <c>Lifespan</c>, <c>InitialSpeed</c>, and (read-but-unused this slice)
    /// the homing/turning fields. Never hardcoded; the task rule.</summary>
    public DataMissileTypeIGC MissileType { get; }

    /// <summary>The constant linear velocity of this missile (inherited from the
    /// firing ship + the missile's decoded <c>InitialSpeed</c>).</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>The normalised heading this missile was fired along (used to seed
    /// its orientation / collider facing).</summary>
    public Vector3 Forward { get; }

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
}
