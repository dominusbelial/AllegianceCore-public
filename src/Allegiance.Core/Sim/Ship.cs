using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A standalone ship — the flight-model slice. Holds the kinematic state the
/// C++ <c>CshipIGC</c> exposes (position, velocity, orientation, controls,
/// turn rates, state bits) and implements <see cref="ExecuteMove"/>, the port
/// of <c>CshipIGC::ExecuteShipMove(Time, Time, Vector*, Orientation*)</c>
/// (<c>src/Igc/shipIGC.cpp:2852-3054</c>). Deliberately <b>not</b> wired into
/// <c>Sector</c>/<c>Mission</c> — integration is a separate slice.
/// </summary>
/// <remarks>
/// What is and is not modelled (faithfulness notes — every deviation is a
/// simplification that this slice deliberately does not attempt, documented
/// here so the port is honest):
/// <list type="bullet">
/// <item><b>No ripcord.</b> The C++ guards both the turn-rate ramp and the
/// engine-thrust block with <c>if (!m_pmodelRipcord)</c> (<c>shipIGC.cpp:2885,
/// 2962</c>); this ship has no ripcord path, so both blocks always run. Drag is
/// still computed (and subtracted) regardless, exactly as the C++ does even
/// when ripcording.</item>
/// <item><b>No afterburner part.</b> The C++ folds a mounted afterburner's
/// thrust into <c>thrustRatio</c> and drag (<c>shipIGC.cpp:2942-2959</c>).
/// This ship mounts nothing, so <c>thrustRatio == 0</c> and no afterburner
/// drag is added. The <c>afterburnerButtonIGC</c> bit is still honoured as a
/// gate (it changes which thrust branch runs even with no AB mounted), so the
/// full decision tree is reproduced.</item>
/// <item><b>No <c>engineVector</c> field.</b> It is computed as a local and
/// immediately folded into velocity, matching the observable behaviour.</item>
/// </list>
/// </remarks>
public sealed class Ship : Model, IShipIgc
{
    private readonly DataHullTypeIGC _hull;
    private readonly float[] _turnRates = new float[3];

    // Fractional hull integrity — the C++ m_fraction (shipIGC.cpp:1247). 1.0
    // = full hull, 0.0 = dead. Damage subtracts leakage/maxHP from it; death
    // occurs when it reaches ≤ 0.
    private float _fraction = 1.0f;

    /// <summary>Construct with a hull and initial kinematic state. Turn rates start at 0 (like the C++ constructor, <c>shipIGC.cpp:120-121</c>).</summary>
    public Ship(short shipId, DataHullTypeIGC hull, Vector3 position, Vector3 velocity, Orientation orientation)
        : base(shipId, position)
    {
        _hull = hull;
        Velocity = velocity;
        Orientation = orientation ?? throw new ArgumentNullException(nameof(orientation));
        Controls = ControlData.Neutral;
        StateM = 0;
        Energy = hull.MaxEnergy;   // pool starts full (the C++ recharges from this)
        Ammo = hull.MaxAmmo;       // magazine starts full
    }

    /// <summary>The hull's <c>Mass</c> — the "wet" mass the movement math uses.</summary>
    public float Mass => _hull.Mass;

    /// <summary>The hull's <c>DataHullTypeIGC</c> — the raw static-core record this ship is built on. (Named <c>HullType</c>, not <c>Hull</c>, to avoid clashing with <see cref="Model.Hull"/>, the collision hull.)</summary>
    public DataHullTypeIGC HullType => _hull;

    /// <summary>The ship's energy pool. Seeded to <c>Hull.MaxEnergy</c> at construction.</summary>
    public float Energy { get; set; }

    /// <summary>The ship's ammo count (magazine rounds for projectile weapons). Seeded to <c>Hull.MaxAmmo</c> at construction.</summary>
    public short Ammo { get; set; }

    /// <summary>
    /// Fractional hull integrity (<c>m_fraction</c>): 1.0 = full hull, 0.0 =
    /// dead. Damage reduces it by <c>leakage · dtmArmor / maxHP</c>
    /// (<c>shipIGC.cpp:1298</c>); death is the moment it reaches &lt;= 0. Read
    /// by <see cref="ServerShipUpdate"/> so the wire hull fraction reflects
    /// real damage taken.
    /// </summary>
    public float Fraction => _fraction;

    /// <summary>Absolute hull hit points = <c>Fraction · HullType.HitPoints</c>.</summary>
    public float HitPoints => _fraction * _hull.HitPoints;

    /// <summary>True once the hull is dead (<c>m_fraction &lt;= 0</c>).</summary>
    public bool Dead => _fraction <= 0.0f;

    /// <summary>The sector this ship was placed into, set by <c>Mission.PlaceShip</c> — the analogue of the C++ <c>GetCluster()</c>, which the weapon reads to place a projectile (<c>weaponIGC.cpp:177</c>). <c>null</c> before placement. The warp-transit slice reassigns this (<c>cfsship.cpp::SetCluster</c> route) so a ship that crosses an aleph genuinely changes sector.</summary>
    public Sector? Sector { get; set; }

    /// <summary>
    /// Sim-time of the ship's most recent warp transit, or
    /// <c>NegativeInfinity</c> if it has never crossed. The port of the C++
    /// <c>CFSShip::m_warpState</c> debounce (<c>fsship.cpp:74</c>): native
    /// blocks a second crossing while <c>m_warpState != warpReady</c> and only
    /// re-arms ~2 s after a client update (<c>fsship.cpp:1185-1191</c>). Here a
    /// cooldown window (<see cref="Sector.WarpCooldownSeconds"/>, kept on the
    /// ship so it survives a sector transfer) stands in for the full state
    /// machine — a documented simplification for this server's simplified
    /// scope (single authoritative tick, no client-update re-arm cycle).
    /// Owned and written by the host's warp transit on the sim thread.
    /// </summary>
    public double LastWarpTime { get; set; } = double.NegativeInfinity;

    /// <summary>The weapons mounted on this ship, in mount order. Driven by
    /// <see cref="OnUpdate"/> so they fire only within the authoritative tick.
    /// Realised from the ship's <see cref="Mission"/> core at mount time.</summary>
    public IReadOnlyList<Weapon> Weapons => _weapons;

    private readonly List<Weapon> _weapons = [];

    public Vector3 Velocity { get; set; }
    public Orientation Orientation { get; }
    public ControlData Controls { get; set; }

    /// <inheritdoc/>
    public override Vector3 CollisionVelocity => Velocity;

    /// <inheritdoc/>
    public override Orientation CollisionOrientation => Orientation;

    /// <summary>State bitmask; the buttons live in <see cref="ShipControlState"/>.</summary>
    public int StateM { get; set; }

    /// <summary>
    /// The mission this ship was placed into, set by
    /// <c>Mission.PlaceShip</c> — the analogue of the C++ <c>GetMyMission()</c>
    /// (<c>shipIGC.cpp</c>). <c>null</c> before placement; the combat slice
    /// (weapons/projectiles/receive-damage) reads it to reach the mission's
    /// static core and host site.
    /// </summary>
    public Mission? Mission { get; set; }

    /// <summary>
    /// The side this ship belongs to, if any — the analogue of the C++
    /// <c>GetSide()</c> affiliation that the friendly-fire gate reads
    /// (<c>shipIGC.cpp:1222-1228</c>). Type matches <see cref="SideSpec.Id"/>.
    /// <c>null</c> before a side is assigned: a ship built outside the live
    /// spawn path (e.g. the flight/bot slices or a unit test) has no side and
    /// therefore fights every other ship. The live server assigns one at spawn
    /// (<see cref="Allegiance.Server.Server.SpawnDefaultShip"/>).
    /// </summary>
    public short? SideId { get; set; }

    /// <summary>
    /// The id of the station this ship is docked at, or <c>null</c> while flying
    /// free — the analogue of the C++ <c>IstationIGC* GetStation()</c> pointer
    /// (<c>shipIGC.h</c>) that <c>FedSrvSiteBase::DockWithStationEvent</c> sets
    /// (<c>FedSrv.CPP:13107-13110</c> → <c>CFSShip::ShipStatusDocked</c>,
    /// <c>fsship.cpp:377</c> sets <c>StationID</c>) and that the undock handler
    /// clears (<c>FedSrv.CPP:7040-7041</c> gates on <c>GetStation() != NULL</c>,
    /// <c>7050-7052</c> <c>SetStation(NULL)</c> for <c>stationID == NA</c>).
    /// <c>null</c> when not docked, i.e. docked ⇔ a value is present.
    /// <list type="bullet">
    /// <item><b>Why a nullable <c>short?</c>, not a <see cref="StateM"/> bit</b>:
    /// <see cref="StateM"/> is the C++ <c>ShipControlStateIGC</c> button bitmask
    /// (<c>igc.h:803-835</c>), sent verbatim as the control word on the wire
    /// (<c>ServerShipUpdate.FromModel</c>, <c>ServerShipUpdate.cs:106</c>) and
    /// consumed by the movement/weapon slices. Its value <c>2</c> already means
    /// <c>SelectedWeaponTwo</c> (<c>ShipControlState.cs:34</c>, inside
    /// <c>SelectedWeaponMask == 3</c>), so folding a "docked" bit into
    /// <c>StateM</c> would both collide with weapon selection and leak a control
    /// flag onto the wire as if it were a button. The wire <em>ShipState</em>
    /// enum that actually carries <c>c_ssDocked=2</c> is a separate 6-bit field
    /// in <c>ShipStatusMessage</c> (<c>ShipStatus.cs:40,62</c>) with no
    /// <c>StateM</c> home — this property is where it is derived from. This
    /// follows <see cref="SideId"/>'s nullable-short convention for "an id that
    /// may or may not be set"; the wire value <c>StateDocked == 2</c> is reused
    /// as the emitted <see cref="ShipStatusMessage.State"/> when docked (task's
    /// "reuse, don't invent" instruction honoured via the derived wire state,
    /// not via a colliding <see cref="StateM"/> bit).</item>
    /// <item><b>Latched until undock:</b> the dock state transition (setting /
    /// clearing this property) is exactly what this task scopes; a docked ship
    /// is not given docking-bay physics (it keeps flying on <see cref="OnUpdate"/>),
    /// but the sector pass skips already-docked ships so the state holds until
    /// an <c>FM_C_DOCKED</c>(48) with <c>stationID == NA</c> clears it.</item>
    /// </list>
    /// Owned and written by the host's dock seam (<see cref="IIgcSite.DockWithStationEvent"/>)
    /// and the server's <c>FM_C_DOCKED</c> undock handler, on the sim thread.
    /// </summary>
    public short? StationId { get; set; }

    /// <summary>
    /// Amount of mined ore this ship currently carries, in arbitrary ore units.
    /// Starts at 0 (plain value, not nullable — unlike <see cref=\"StationId\"/>,
    /// a cargo quantity has no meaningful \"absent\" state, 0 already encodes
    /// empty). Accumulated per tick by the sector's mining pass
    /// (<see cref=\"Model.Sector.ResolveMining\"/>) when the ship is adjacent
    /// to and stopped beside a minable asteroid, up to the flat
    /// <see cref=\"MineCapacity\"/> constant (standing in for the per-side
    /// attribute-scaled capacity, out of this task's scope). This mirrors the
    /// C++ <c>m_fOre</c> (<c>shipIGC.h:2634</c>), which the ship-fractions wire
    /// slot carries (<c>ServerShipUpdate</c> byte 45 = <c>Ore/MineCapacity</c>,
    /// <c>shipIGC.h:1125,1148</c> <c>SetOre(capacity, current)</c>) and which
    /// the native sells back for credits via a <c>PaydayEvent</c> — the P56
    /// sell step, out of scope here (never converted to money in this task).
    /// Written by the mining pass on the sim thread.
    /// </summary>
    public float Ore { get; set; }

    /// <summary>
    /// Flat ore-carried capacity constant, standing in for the per-side
    /// <c>GlobalAttributeSet</c>-scaled capacity that the native engine derives
    /// from tech-level attributes (out of this task's scope). A literal,
    /// chosen small enough that a synthetic test reaches the cap in a handful
    /// of <see cref=\"Model.Sector.MiningRate\"/> ticks — labelled a
    /// simplification, not a verified native value (the real default lives in
    /// core data this port does not decode). Saturating this value pauses
    /// mining (<see cref=\"Model.Sector.ResolveMining\"/> continues only while
    /// <c>Ore &lt; MineCapacity</c>), matching the native capacity clamp.
    /// </summary>
    public const float MineCapacity = 150.0f;

    /// <summary>
    /// True while this ship is actively mining (has mined at least one tick
    /// against a rock and not yet detached). Latched by the mining pass on the
    /// first satisfied tick and never auto-cleared — there is no server-side
    /// concept of an un-mine command, and travel leaves proximity naturally
    /// (the pass simply stops accruing; the flag records the transition so the
    /// start-mining log fires once per engagement, matching the transition-only
    /// log the task asks for).
    /// </summary>
    public bool IsMining { get; set; }

    /// <summary>
    /// Mount a weapon on this ship and realise it against this ship's mission
    /// core (the analogue of the C++ <c>CweaponIGC::Initialize</c> resolving
    /// <c>m_projectileType</c> from the mission's core,
    /// <c>weaponIGC.cpp:45</c>). Requires a <see cref="Mission"/> (the ship must
    /// be placed first) and a <c>Weapon</c> part type; the referenced projectile
    /// type must exist in the core. The weapon is constructed <b>ready</b>
    /// (fully mounted), so the combat slice can fire without first spinning a
    /// mounting state machine.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ship is not placed, the part is not a weapon, or the weapon's projectile type is absent from the core.</exception>
    public Weapon MountWeapon(DataPartTypeIGC.Weapon type, short mountId = 0)
    {
        var mission = Mission
            ?? throw new InvalidOperationException("cannot mount a weapon on a ship that has not been placed (no mission to resolve its projectile type)");
        if (!mission.Core.ProjectileTypes.TryGetValue(type.ProjectileTypeId, out var projectileType))
            throw new InvalidOperationException(
                $"weapon '{type.ProjectileTypeId}' names projectile type {type.ProjectileTypeId} absent from core {mission.Core.Version}");
        var weapon = new Weapon(this, type, projectileType, mountId);
        _weapons.Add(weapon);
        return weapon;
    }

    /// <summary>The three current angular rates (yaw, pitch, roll), rad/s. Indexed by <see cref="ControlData"/> axis constants.</summary>
    public IReadOnlyList<float> TurnRates => _turnRates;

    /// <summary>
    /// <c>CshipIGC::ReceiveDamage</c> (<c>shipIGC.cpp:1212-1350</c>): apply
    /// <paramref name="amount"/> of <paramref name="type"/> damage to this
    /// ship's hull at mission time <paramref name="now"/>. The reference
    /// signature is preserved so the projectile's
    /// <c>HandleCollision</c> (<c>projectileIGC.cpp:185-190</c>) maps 1:1.
    /// Returns a <see cref="DamageResult"/> (a projectile checks
    /// <c>dr &lt;= c_drHullDamage</c> to decide whether impact visibly damaged
    /// the hull, <c>projectileIGC.cpp:193</c>).
    /// </summary>
    /// <remarks>
    /// Faithful to the C++ within this slice's stated scope, with every
    /// deviation explicit:
    /// <list type="bullet">
    /// <item><b>Friendly-fire gate implemented.</b> The reference returns
    /// <c>c_drNoDamage</c> when the launcher is friendly and friendly-fire is
    /// off (<c>shipIGC.cpp:1222-1228</c>). Our ships now carry an optional
    /// <see cref="SideId"/>, and the sector collision pass enforces the gate:
    /// a projectile never damages another ship whose <see cref="SideId"/>
    /// equals the launcher's (<c>Sector.ResolveCombatCollisions</c>,
    /// <c>src/Allegiance.Core/Model/Sector.cs:190-195</c>). Friendly fire is
    /// always off — there is no toggle in this codebase to turn it on. The
    /// self-launcher exclusion (a ship cannot damage itself) remains, handled
    /// separately just above (<c>Sector.cs:189</c>); both sit at the same
    /// pass, and this method itself is unchanged — a same-side hit simply
    /// never reaches it.</item>
    /// <item><b>No shields.</b> When <c>m_mountedOthers[ET_Shield]</c> is
    /// empty the reference routes every point of damage to the hull
    /// (<c>leakage = amount</c>, <c>shipIGC.cpp:1287-1291</c>). Our ships mount
    /// no shields (shield parts are out of scope), so the no-shield branch is
    /// the only one reachable; <c>CshieldIGC::ApplyDamage</c>
    /// (<c>shieldIGC.cpp:140</c>) is not ported and shields are deferred — see
    /// README.</item>
    /// <item><b>No damage track.</b> The bookkeeping bucket tally
    /// (<c>common.cpp:2592</c>) only decides <em>who</em> gets kill credit at
    /// death (<c>shipIGC.cpp:1320-1332</c>); with a single identifiable launcher
    /// and no need to split credit, it is omitted (README).</item>
    /// <item><b>No experience multiplier.</b> Ships have no accumulated
    /// experience (<c>shipIGC.cpp:1239-1242</c> multiplies by 1.0 by default).</item>
    /// <item><b>Repair path retained structurally.</b> A negative <paramref name="amount"/>
    /// (repair) restores hull fraction capped at 1.0, per
    /// <c>shipIGC.cpp:1244-1267</c> minus the repair-stat awarding; no repair
    /// source exists yet, so it is dead code in practice.</item>
    /// </list>
    /// The armor factor <c>dtmArmor</c> is <b>not</b> hardcoded: it is the
    /// decoded damage matrix <c>DamageConstants[type][hull.DefenseType]</c>
    /// from the mission's <see cref="DataConstantsIGC"/> (the C++
    /// <c>GetDamageConstant(type, defenseType)</c>, <c>shipIGC.cpp:1233</c>).
    /// </remarks>
    public DamageResult ReceiveDamage(
        byte type,
        float amount,
        double now,
        Vector3 position1,
        Vector3 position2,
        Model? launcher)
    {
        float maxHP = _hull.HitPoints;
        float dtmArmor = DamageConstant(type);

        DamageResult dr;
        float leakage;

        if (amount < 0.0f)
        {
            // Repair path (shipIGC.cpp:1244-1267), minus the repair-stat + ThingSite
            // noise. Restores hull fraction, clipped at 1.0 (can't over-repair).
            _fraction -= amount * dtmArmor / maxHP;
            if (_fraction > 1.0f) _fraction = 1.0f;
            leakage = 0.0f;
            dr = DamageResult.NoDamage;
        }
        else
        {
            dr = DamageResult.ShieldDamage;

            // m_damageTrack->ApplyDamage(...) omitted (credit only; see remarks).

            // Shields: no shield mounted (shipIGC.cpp:1282), so the C++ else
            // branch (shipIGC.cpp:1287-1291) routes ALL damage to the hull.
            leakage = amount;

            float oldFraction = _fraction;
            if (leakage > 0.0f)
            {
                leakage *= dtmArmor;
                _fraction -= leakage / maxHP;
                dr = DamageResult.HullDamage;
            }

            if (_fraction > 0.0f)
            {
                Mission?.Site.DamageShipEvent(now, this, launcher, type, amount, leakage, position1, position2);
            }
            else
            {
                _fraction = 0.0f;
                if (oldFraction > 0.0f)   // Only send the death message once (shipIGC.cpp:1317)
                {
                    Mission?.Site.KillShipEvent(now, this, launcher, amount, position1, position2);
                    dr = DamageResult.Killed;
                }
            }
        }

        return dr;
    }

    /// <summary>
    /// <c>GetDamageConstant(type, defenseType)</c>
    /// (<c>shipIGC.cpp:1233</c>): the decoded damage matrix
    /// <c>DamageConstants[dmgid][defid]</c>. Returns 1.0 (neutral) when the
    /// ship has no mission (unplaced) or the indexes lie outside the 20×20
    /// matrix, so a stray damage type can never amplify/dilute a hit into
    /// nonsense. Not a hardcoded constant — it is data-driven from
    /// <see cref="DataConstantsIGC"/>.
    /// </summary>
    private float DamageConstant(byte type)
    {
        var core = Mission?.Core;
        if (core is null) return 1.0f;
        var dm = core.Constants.DamageConstants;
        int dmg = type, def = _hull.DefenseType;
        if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax)
            return 1.0f;
        return MathF.Max(dm[dmg][def], 0.0f);
    }


    /// <summary>Copies the turn rates into <paramref name="dest"/> and returns it (avoids exposing the backing array for mutation at a distance).</summary>
    public float[] GetTurnRates(float[] dest)
    {
        Array.Copy(_turnRates, dest, 3);
        return dest;
    }

    /// <summary>
    /// Advance the ship's kinematics from <paramref name="timeStart"/> to
    /// <paramref name="timeStop"/> given the current controls — the port of
    /// <c>CshipIGC::ExecuteShipMove(timeStart, timeStop, &amp;vel, &amp;ori)</c>
    /// (<c>shipIGC.cpp:2852-3054</c>). Mutates <see cref="Velocity"/> and
    /// <see cref="Orientation"/>. <b>Does not move <see cref="Position"/></b> —
    /// the C++ does not either; integrating <c>Δposition = velocity·ΔT</c> is
    /// the mission/cluster slice.
    /// </summary>
    /// <param name="timeStart">Start instant (seconds).</param>
    /// <param name="timeStop">End instant (seconds); must exceed <paramref name="timeStart"/> (the C++ <c>assert(dT &gt; 0)</c>).</param>
    public void ExecuteMove(double timeStart, double timeStop)
    {
        if (!(timeStop > timeStart))
            throw new ArgumentOutOfRangeException(nameof(timeStop), "ExecuteMove requires timeStop > timeStart");

        float dT = (float)(timeStop - timeStart);
        float thrust = _hull.Thrust;
        float thrust2 = thrust * thrust;
        float thrustToVelocity = dT / Mass;          // Newtons → Δv; shipIGC.cpp:2868

        // ── manoeuvre: ramp turn rates toward the desired, clipped by torque ─
        // (shipIGC.cpp:2887-2910; no ripcord, so this block always runs)
        float l = Controls.Yaw * Controls.Yaw + Controls.Pitch * Controls.Pitch + Controls.Roll * Controls.Roll;
        l = l > 1f ? 1f / MathF.Sqrt(l) : 1f;         // constrain desired rate to a sphere, not a box

        float tm = GetTorqueMultiplier() * thrustToVelocity;
        for (int i = 0; i < 3; i++)
        {
            float desiredRate = Controls.TurnAxis(i) * l * _hull.MaxTurnRates[i];
            float maxDelta = tm * _hull.TurnTorques[i];
            if (desiredRate < _turnRates[i] - maxDelta)
                _turnRates[i] -= maxDelta;
            else if (desiredRate > _turnRates[i] + maxDelta)
                _turnRates[i] += maxDelta;
            else
                _turnRates[i] = desiredRate;
        }

        Orientation.Yaw(_turnRates[ControlData.AxisYaw] * dT);
        Orientation.Pitch(-_turnRates[ControlData.AxisPitch] * dT);
        Orientation.Roll(_turnRates[ControlData.AxisRoll] * dT);
        Orientation.Renormalize();

        var backward = Orientation.GetBackward();
        float speed = Velocity.Length();
        float maxSpeed = _hull.Speed;

        // Drag = thrust required to produce the velocity change caused by drag.
        // Computed with double intermediates exactly as the C++ casts
        // (shipIGC.cpp:2930-2934).
        double f = Math.Exp((-thrust) * (double)thrustToVelocity / maxSpeed);
        var drag = Velocity * (float)((1.0 - f) / thrustToVelocity);

        bool afterF = (StateM & ShipControlState.AfterburnerButton) != 0;

        Vector3 localThrust;
        if ((StateM & ShipControlState.MoveButtonsMask) != 0)
        {
            // Manual control: whichever direction buttons are pressed, at full thrust.
            int x = ((StateM & ShipControlState.LeftButton) != 0 ? -1 : 0)
                  + ((StateM & ShipControlState.RightButton) != 0 ? 1 : 0);
            int y = ((StateM & ShipControlState.DownButton) != 0 ? -1 : 0)
                  + ((StateM & ShipControlState.UpButton) != 0 ? 1 : 0);
            int z = ((StateM & ShipControlState.BackwardButton) != 0 ? 1 : 0)
                  + ((StateM & ShipControlState.ForwardButton) != 0 ? -1 : 0);
            localThrust = (x != 0 || y != 0 || z != 0)
                ? new Vector3(thrust * x, thrust * y, thrust * z)
                : Vector3.Zero;
        }
        else if ((StateM & ShipControlState.CoastButton) != 0 && !afterF)
        {
            // Vector lock: kill lateral velocity (bring drag into ship-local).
            localThrust = Orientation.TimesInverse(drag);
        }
        else
        {
            // Throttle cruise: aim at a target speed and cancel drag.
            float negDesiredSpeed = afterF
                ? maxSpeed * (-1.0f - 0.0f)                          // thrustRatio == 0 (no AB)
                : (-0.5f * (1.0f + Controls.Throttle)) * ((speed > maxSpeed) ? speed : maxSpeed);
            var desiredVelocity = backward * negDesiredSpeed;
            localThrust = Orientation.TimesInverse((desiredVelocity - Velocity) / thrustToVelocity + drag);
        }

        // Clip the engine vector to the engine's available thrust.
        // (shipIGC.cpp:3008-3033)
        float sm = _hull.SideMultiplier;
        float backMult = _hull.BackMultiplier;
        var scaledThrust = new Vector3(
            localThrust.X / sm,
            localThrust.Y / sm,
            localThrust.Z <= 0f ? localThrust.Z : localThrust.Z / backMult);
        float r2 = scaledThrust.LengthSquared();

        Vector3 engineVector;
        if (r2 == 0f)
            engineVector = Vector3.Zero;
        else if (r2 <= thrust2)
            engineVector = localThrust * Orientation;
        else
            engineVector = (localThrust * Orientation) * (thrust / MathF.Sqrt(r2));

        Velocity += thrustToVelocity * (engineVector - drag);
    }

    /// <summary>
    /// <c>CshipIGC::GetTorqueMultiplier()</c> (<c>shipIGC.h:542-549</c>): 0.50
    /// at rest rising toward 2.50 as speed approaches/exceeds max, using
    /// <c>f = |v|/maxSpeed</c>. Read from the current velocity (at the moment
    /// the ramp runs) exactly as the reference reads <c>GetVelocity()</c>.
    /// </summary>
    private float GetTorqueMultiplier()
    {
        const float c = 0.50f;
        float fraction = Velocity.Length() / _hull.Speed;
        return c + (1.0f - c) * 2.0f * fraction / (fraction + 1.0f);
    }

    /// <summary>
    /// The sector-tick hook (called via <see cref="Model.Update"/> once per
    /// chunk). Runs the flight model for the elapsed <c>dt</c> —
    /// <see cref="ExecuteMove"/> — then integrates the C++-owned position step
    /// <c>Δposition = velocity·ΔT</c>, which <c>ExecuteShipMove</c> deliberately
    /// does <b>not</b> do (<c>CalculateShip</c>, <c>shipIGC.cpp:3273-3286</c>;
    /// the mission/chunk driver owns it in the reference). Integrating here is
    /// the minimal honest spot: the whole <c>Move()</c> pass lives in the
    /// out-of-scope collision block (<c>clusterIGC.cpp:338-346</c>), and without
    /// a position step a sector tick would not move a ship at all. Physics is
    /// unchanged — <c>ExecuteMove</c> is called verbatim with the same elapsed
    /// time it would receive as a standalone move, so velocity/orientation match
    /// a direct call exactly (the measured split-vs-single divergence is the
    /// mission cadence, not this wrapper).
    /// </summary>
    protected override void OnUpdate(double now, double dt)
    {
        ExecuteMove(now - dt, now);   // flight kinematics for exactly this chunk
        Position += Velocity * (float)dt;   // integrate Δpos = velocity·ΔT

        // Drive mounted weapons (CshipIGC::Update calls its parts' Update,
        // which for a weapon is CweaponIGC::Update → FireWeapon). Runs after the
        // movement step so the muzzle state reflects the tick's end; the weapon
        // fires only if selected, off-cooldown, and with enough energy/ammo.
        // With no weapons mounted (the flight/bot slices) this is a no-op.
        foreach (var weapon in _weapons)
            weapon.Update(now);
    }
}
