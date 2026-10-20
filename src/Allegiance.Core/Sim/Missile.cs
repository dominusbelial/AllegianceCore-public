using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Sim;

/// <summary>A guided, accelerating missile. Native Igc/missileigc.cpp:354-419
/// gates steering/thrust on activation, then integrates movement; burnout is
/// handled on the next update after the previous clock reaches expiry.</summary>
public sealed class Missile : Model, IOrdnance, IMissileIgc
{
    private readonly Sector _sector;
    private readonly double _timeExpire;
    private readonly double _timeReady;
    private float _impactTime;
    private bool _terminated;

    /// <summary>Captured warp detonation time, in simulation seconds.</summary>
    public double? WarpBombTime { get; internal set; }

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
        float lockFraction = 1f, bool dud = false)
        : base(id, position)
    {
        _sector = sector ?? throw new ArgumentNullException(nameof(sector));
        MissileType = missileType;
        Velocity = velocity;
        Forward = forward.Normalize();
        Launcher = launcher;
        Target = target;
        LockFraction = lockFraction;
        IsDud = dud;
        _timeExpire = now + missileType.Lifespan;
        // P549 — native missileigc.cpp:104: `m_timeActivate = now +
        // dataMissileType->readyTime`. The missile only begins homing and
        // thrusting once the sim clock passes this activation instant
        // (missileigc.cpp:363); before it, it coasts inert at its launch
        // velocity (the position step below still runs — native
        // TmodelIGC::Update always advances position).
        _timeReady = now + missileType.ReadyTime;
        if (target is not null && missileType.Acceleration > 0)
            _impactTime = missileType.ReadyTime + MathF.Sqrt(2 / missileType.Acceleration *
                (target.Position - position + missileType.ReadyTime * (target.CollisionVelocity - velocity)).Length());
        InitClock(now);
    }

    /// <summary>The decoded missile type from the static core — the source of
    /// lifespan, launch speed, activation delay, acceleration and turn rate.</summary>
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

    /// <summary>Native m_bDisarmed: early server fire (FedSrv.CPP:5434;
    /// missileigc.cpp:128,428). Duds ignore contact collisions (including warps); native expiry still explodes.</summary>
    public bool IsDud { get; }

    /// <summary>The absolute time (mission-seconds) at which this missile expires
    /// and removes itself.</summary>
    public double TimeExpire => _timeExpire;

    /// <summary>The sector this missile lives in — the sector that owns it and that
    /// it removes itself from on expiry.</summary>
    public Sector OwnerSector => _sector;

    /// <summary>Native missileigc.h:134-145 damageable health fraction.</summary>
    public float Fraction { get; private set; } = 1;
    public bool Dead => Fraction <= 0;

    /// <summary>Native missileigc.h:79-132: friendly immunity, matrix and
    /// experience scaling, capped repairs, and first-crossing destruction.</summary>
    public DamageResult ReceiveDamage(byte type, float amount, double now,
        Vector3 position1, Vector3 position2, Model? launcher)
    {
        var mission = _sector.Mission ?? (Launcher as Ship)?.Mission;
        if (BlastDamage.Suppressed(mission, BlastDamage.SideOf(Launcher), launcher, amount))
            return DamageResult.NoDamage;
        int row = type & BlastDamage.Mask, defense = MissileType.DefenseType;
        if (mission is not null && row < DataConstantsIGC.DmgidMax && defense < DataConstantsIGC.DefidMax)
            amount *= MathF.Max(0, mission.Core.Constants.DamageConstants[row][defense]);
        if (launcher is Ship source) amount *= source.ExperienceMultiplier;
        float maxHP = MissileType.HitPoints;
        if (amount < 0)
        {
            if (maxHP > 0) Fraction = MathF.Min(1, Fraction - amount / maxHP);
        }
        else if (amount != 0)
        {
            float previous = Fraction;
            Fraction = maxHP > 0 ? MathF.Max(0, Fraction - amount / maxHP) : 0;
            if (Dead && previous > 0)
            {
                _sector.Terminate(this);        // P754b: real Terminate (modelIGC.cpp:55-61)
                mission?.Site.MissileDestroyedEvent(this);
                // FedSrv.CPP:12524-12529: killed missiles also explode, except
                // the zero-position resonator sentinel. Remove first so the
                // queued secondary explosion cannot kill this missile again.
                if (position1 != Vector3.Zero && MissileType.BlastPower != 0)
                    _sector.ResolveExplosion(new Explosion(MissileType.DamageType,
                        MissileType.BlastPower * GaFactor(GlobalAttributeSet.DamageMissiles),
                        MissileType.BlastRadius, now, position1, Launcher), now);
                return DamageResult.Killed;
            }
        }
        return DamageResult.HullDamage;
    }

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

    /// <summary>Native Igc/missileigc.cpp:354-419.</summary>
    protected override void OnUpdate(double now, double dt)
    {
        if (_terminated) return;
        if (LastUpdate >= _timeExpire)
        {
            _terminated = true;
            _sector.Terminate(this);            // P754b: real Terminate (modelIGC.cpp:55-61)
            if (MissileType.BlastPower != 0)
                _sector.ResolveExplosion(new Explosion(MissileType.DamageType,
                    MissileType.BlastPower * GaFactor(GlobalAttributeSet.DamageMissiles),
                    MissileType.BlastRadius, now, Position, Launcher), now);
            (_sector.Mission ?? (Launcher as Ship)?.Mission)?.Site.MissileDestroyedEvent(this);
            return;
        }

        if (now > _timeReady)
        {
            // Only the part of this tick after activation contributes thrust
            // and turning (missileigc.cpp:371). Preserve the literal native
            // teleport at :399-405: its deltaV uses BACKWARD, so subtracting
            // deltaV adds forward displacement for the inactive prefix.
            float activeDt = (float)(now - Math.Max(_timeReady, LastUpdate));
            if (Target is { } t && (!EquipmentTarget.IsPresent(t, _sector)
                || t is Ship ship && !ReferenceEquals(ship.Sector, _sector))) Target = null;
            if (Target is { } target && MissileType.Acceleration > 0)
            {
                var dp = target.Position - Position;
                var dv = target.CollisionVelocity * LockFraction - Velocity;
                _impactTime = MissileIntercept.Solve(dp, dv, MissileType.Acceleration, _impactTime);
                var direction = dp + _impactTime * dv;
                if (direction.LengthSquared() > 0)
                    Forward = RotateToward(Forward, direction.Normalize(),
                        MissileType.TurnRate * activeDt * GaFactor(GlobalAttributeSet.TurnRateMissile));
            }
            var deltaV = Forward * (MissileType.Acceleration * activeDt);
            Velocity += deltaV;
            if (_timeReady > LastUpdate)
                Position += deltaV * (float)(_timeReady - LastUpdate);
            _impactTime -= activeDt;
        }
        Position += Velocity * (float)dt;
    }

    /// <summary>
    /// P467 — the missile's side-global-attribute factor for <paramref name="index"/>:
    /// the port of native's per-update <c>GetSide()-&gt;GetGlobalAttributeSet()
    /// .GetAttribute(..)</c> (<c>missileigc.cpp:389</c> turn, <c>:523</c> blast).
    /// A missile's side is its launcher's side; a launcher-less / non-Ship
    /// launcher reads identity (1.0).
    /// </summary>
    private float GaFactor(int index) => (Launcher as Ship)?.GlobalAttributeFactor(index) ?? 1f;

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
