using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A weapon mounted on a <see cref="Ship"/> — the port of <c>CweaponIGC</c>
/// (<c>src/Igc/weaponIGC.cpp</c>, 348 lines; <c>weaponIGC.h</c>), kept to the
/// minimum that lets a ship fire: the armed/active state, the cooldown gate, the
/// energy/ammo checks, and the projectile creation in
/// <c>FireWeapon</c> (<c>weaponIGC.cpp:142-348</c>). The mounting state machine
/// (the <c>mountedFraction</c> ramp, <c>weaponIGC.h:30-49</c>) follows its
/// mounted part, including swap and ammo reload delays.
/// </summary>
/// <remarks>
/// Faithfulness notes (every deviation documented so the port stays honest):
/// <list type="bullet">
/// <item><b>Gunner supported.</b> <c>m_pshipGunner</c> is represented by
/// <see cref="Gunner"/>/<see cref="SetGunner"/>, set by the boarding handler
/// (P109); when present, firing uses the gunner's orientation for the round's
/// direction and the gunner's command target for blast weapons, while the
/// mount's physical position still comes from the carrier ship
/// (<c>weaponIGC.cpp:184-231</c>).</item>
/// <item><b>Side global attributes wired (P467).</b> <c>GetGlobalAttributeSet()</c>
/// (economy/tech) is resolved through <see cref="Ship.GlobalAttributeFactor"/>,
/// so the <c>c_gaSpeedAmmo</c> speed boost (ammo weapons only, weaponIGC.cpp:220)
/// and the <c>c_gaLifespanEnergy</c> lifespan modifier (energy weapons only,
/// weaponIGC.h:167-170) now multiply by the launcher's side attribute; a
/// sideless / identity side yields the faithful no-side value (1.0).</item>
/// <item><b>Hardpoint geometry (P465c).</b> The weapon muzzle offset
/// (<c>m_pposition</c>, <c>weaponIGC.cpp:123</c>) is now realised from the
/// hull's <c>.cvh</c> named frames (<c>HullTypeIGC::Initialize</c>,
/// <c>hullTypeIGC.cpp:89-136</c>), served by the mission's
/// <see cref="Allegiance.Core.Collision.WeaponMuzzleCache"/>: each round spawns
/// at <c>ship position + offset * shipOrientation</c> (<c>weaponIGC.cpp:203</c>).
/// A mission without a wired cache (e.g. a bare test mission) keeps the historic
/// ship-centre spawn.</item>
/// <item><b>No target leading.</b> The <c>ptarget</c> block
/// (<c>weaponIGC.cpp:226-288</c>) needs a command target and only runs for
/// blast weapons; neither exists in this slice, so the projectile keeps its
/// full lifespann.</item>
/// <item><b>No energy recharge.</b> <c>FireWeapon</c> deducts energy exactly
/// as the C++ does (<c>weaponIGC.cpp:322</c>), but nothing replenishes it yet —
/// ship recharge arrives with the economy/energy task. A weapon therefore fires
/// until its energy pool is exhausted, then stays dry until the pool is
/// recharged externally.</item>
/// </list>
/// </remarks>
public sealed class Weapon
{
    private readonly Ship _ship;
    private readonly DataPartTypeIGC.Weapon _type;
    private readonly DataProjectileTypeIGC _projectileType;
    private short _mountId;
    private readonly Random _rng;          // dispersion source (weaponIGC.cpp:259-262)

    // The ship riding this turret mount as gunner (native m_pshipGunner,
    // weaponIGC.h:260) — null for a fixed gun, and null for a turret gun with
    // no occupant. Set/cleared by the server's boarding handler (P109) to
    // mirror native SetTurretID→SetGunner (shipIGC.h:1405-1437).

    private double _nextFire;              // Time m_nextFire (weaponIGC.cpp:267)
    private bool _active;                  // m_fActive (weaponIGC.cpp:26)
    private bool _firingShot;              // m_fFiringShot
    private bool _firingBurst;             // m_fFiringBurst
    private Ship? _gunner;                 // m_pshipGunner (weaponIGC.h:260)

    /// <summary>Construct a weapon. Mounted loadout weapons follow their part's
    /// readiness; bare spawn weapons start armed.</summary>
    /// <param name="ship">The ship the weapon is mounted on (must be placed in a sector — it must have a <see cref="Mission"/> and <see cref="Ship.Sector"/>).</param>
    /// <param name="type">The decoded weapon part type.</param>
    /// <param name="projectileType">The decoded projectile type the weapon fires.</param>
    /// <param name="mountId">The mount/hardpoint index this weapon occupies.</param>
    /// <param name="rng">Optional RNG for dispersion; defaults to <see cref="Random.Shared"/>.</param>
    public Weapon(Ship ship, DataPartTypeIGC.Weapon type, DataProjectileTypeIGC projectileType,
                  short mountId, Random? rng = null)
    {
        _ship = ship ?? throw new ArgumentNullException(nameof(ship));
        _type = type;
        _projectileType = projectileType;
        _mountId = mountId;
        _rng = rng ?? Random.Shared;
        _nextFire = ship.LastUpdate;   // armed: subject only to cooldown/selection/energy from the first tick
    }

    /// <summary>The decoded weapon part type (<c>m_typeData</c>).</summary>
    public DataPartTypeIGC.Weapon Type => _type;

    /// <summary>The decoded projectile type this weapon fires (<c>m_projectileType</c>).</summary>
    public DataProjectileTypeIGC ProjectileType => _projectileType;

    /// <summary>The mount/hardpoint index this weapon occupies.</summary>
    public short MountId => _mountId;

    /// <summary>Update the mount/hardpoint index this weapon occupies — the
    /// <c>SetMountID</c> half of a <c>SWAP_PART(34)</c>/part-mount move, so a
    /// realized weapon keeps firing from (and binding a gunner to) the slot it
    /// actually occupies after a mount reassignment (<c>_mountId</c> drives the
    /// fixed-vs-turret hardpoint lookup and the selected-weapon match in
    /// <c>CanFire</c>, <c>Weapon.cs:357-366</c>).</summary>
    public void SetMountId(short mountId) => _mountId = mountId;

    /// <summary><c>GetGunner()</c> (<c>weaponIGC.h:193</c>) — the ship riding
    /// this turret mount as gunner, or <c>null</c> when the mount is a fixed
    /// gun (never a gunner) or an unoccupied turret. While set, this weapon's
    /// aim direction and (for blast weapons) target follow the gunner, not the
    /// carrier ship.</summary>
    public Ship? Gunner => _gunner;

    /// <summary><c>SetGunner(IshipIGC*)</c> (<c>weaponIGC.h:184-192</c>) —
    /// assign (or clear, with <c>null</c>) the ship riding this turret mount.
    /// Mirrors the native setter's <c>AddRef</c>/<c>Release</c> bookkeeping as
    /// a simple reference swap (this port has no refcounting). Called by the
    /// server's boarding handler on board/leave.</summary>
    public void SetGunner(Ship? pship) => _gunner = pship;

    /// <summary>Whether the weapon is active (has been selected and armed; <c>m_fActive</c>).</summary>
    public bool Active => _active;

    public float MountedFraction => _ship.Parts.FirstOrDefault(p => ReferenceEquals(p.FunctionalWeapon, this))?.MountedFraction ?? 1f;

    /// <summary>Whether the last <c>Update</c> produced a shot (<c>m_fFiringShot</c>).</summary>
    public bool FiringShot => _firingShot;

    /// <summary>Whether the weapon is mid-burst (<c>m_fFiringBurst</c>).</summary>
    public bool FiringBurst => _firingBurst;

    /// <summary><c>GetLifespan()</c> (<c>weaponIGC.h:165-172</c>): the projectile
    /// type's lifespan, multiplied by the side's <c>c_gaLifespanEnergy</c>
    /// attribute <b>only when this is an energy weapon</b> — the native's exact
    /// condition is <c>m_ship &amp;&amp; (energyPerShot != 0.0f)</c>
    /// (<c>weaponIGC.h:167-170</c>); a <c>_ship</c> is always present in this
    /// port (constructed with one), so the residual condition is
    /// <c>EnergyPerShot != 0</c> (an ammo weapon scales nothing).</summary>
    public float GetLifespan()
    {
        float ls = _projectileType.Lifespan;
        if (_type.EnergyPerShot != 0f)
            ls *= _ship.GlobalAttributeFactor(GlobalAttributeSet.LifespanEnergy);   // weaponIGC.h:169
        return ls;
    }

    /// <summary><c>GetDtBurst()</c> (<c>weaponIGC.h:179-182</c>): the interval between shots in a burst.</summary>
    public float GetDtBurst() => _type.DtimeBurst;

    /// <summary>
    /// Per-part update — the port of <c>CweaponIGC::Update</c>
    /// (<c>weaponIGC.h:30-63</c>): readiness gate, select-then-activate,
    /// and fire when active. Calling this is what "firing on demand from a test"
    /// means this slice — the ship's <see cref="Ship.OnUpdate"/> invokes it for
    /// each mounted weapon inside the authoritative <c>Mission.Update</c>, and a
    /// test can also call it directly to fire at an exact time.
    /// </summary>
    public void Update(double now)
    {
        // weaponIGC.h:33-48: an unmounted/swapped/reloading gun is inert.
        if (MountedFraction < 1f)
        {
            _firingShot = _firingBurst = false;
            return;
        }
        if (!_active && IsWeaponSelected())
            Activate();

        if (_active)
        {
            FireWeapon(now);
        }
        else
        {
            _firingShot = false;
            _firingBurst = false;
        }
    }

    /// <summary><c>Activate()</c> (<c>weaponIGC.h:103-115</c>): arm the weapon
    /// and schedule the first possible shot a burst-interval away.</summary>
    public void Activate()
    {
        if (!_active)
        {
            _nextFire = _ship.LastUpdate + _type.DtimeBurst;
            _active = true;
        }
    }

    /// <summary><c>Deactivate()</c> (<c>weaponIGC.h:116-128</c>): disarm and clear the firing flags.</summary>
    public void Deactivate()
    {
        if (_active)
            _active = false;
        _firingBurst = false;
        _firingShot = false;
    }

    /// <summary>
    /// The port of <c>CweaponIGC::FireWeapon(Time now)</c>
    /// (<c>weaponIGC.cpp:142-348</c>), the heart of the slice: gated on active +
    /// off-cooldown (<c>m_nextFire &lt; now</c>), it checks the ship's energy
    /// against <c>energyPerShot</c> and ammo against <c>cAmmoPerShot</c>, and
    /// creates one-or-more <see cref="Projectile"/> models in the ship's sector,
    /// each moving at the decoded projectile <c>Speed</c> along the ship's
    /// heading (plus the ship's velocity unless the projectile is
    /// <c>absoluteF</c>), living for the decoded <c>Lifespan</c>. Deducts energy
    /// and ammo and advances the cooldown (<c>m_nextFire += dtimeBurst</c>).
    /// The burst loop stops at 10 shots, or when cooldown/energy/ammo run out —
    /// exactly the C++ conditions.
    /// </summary>
    public void FireWeapon(double now)
    {
        bool fFiredShot = false;

        if (MountedFraction >= 1f && _active && _nextFire < now)
        {
            double lastUpdate = _ship.LastUpdate;

            // Never fire retroactively (weaponIGC.cpp:154-155).
            if (_nextFire < lastUpdate)
                _nextFire = lastUpdate;

            bool fSelected = IsWeaponSelected();
            float energy = _ship.Energy;

            if (energy >= _type.EnergyPerShot)
            {
                float rechargeRate = _ship.HullType.RechargeRate;
                float energyDeficit = (float)(_nextFire - now) * rechargeRate;
                short ammo = _ship.Ammo;
                short ammoStart = ammo;      // P361: pre-burst pool, for the >0→0 auto-reload trigger

                if (ammo > 0 || _type.CAmmoPerShot == 0)
                {
                    fFiredShot = true;

                    var sector = _ship.Sector
                        ?? throw new InvalidOperationException("a placed ship must be in a sector before it can fire");
                    var shipOrientation = _ship.Orientation;
                    // Aim orientation follows the gunner when one is assigned,
                    // else the carrier ship (native: pMyOrientation =
                    // m_pshipGunner ? &(m_pshipGunner->GetOrientation()) :
                    // &shipOrientation, weaponIGC.cpp:184-195). Kept as a live
                    // reference (Orientation is a class, `?.` returns it, not a
                    // copy), so every shot in a burst reads the gunner's
                    // orientation at that instant — matching the native's
                    // address-take. The mount's PHYSICAL position is unaffected:
                    // myPosition always comes from the carrier ship below.
                    Orientation pMyOrientation = _gunner?.Orientation ?? shipOrientation;
                    float dtimeBurst = _type.DtimeBurst;
                    float recharge = rechargeRate * dtimeBurst;
                    var myVelocity = _ship.Velocity;
                    // P465c — spawn each round at its hardpoint frame, not the
                    // ship centre: myPosition = GetPosition() + *m_pposition *
                    // shipOrientation (weaponIGC.cpp:203). m_pposition is the
                    // mount's offset built in HullTypeIGC::Initialize
                    // (hullTypeIGC.cpp:89-136) and served here by the mission's
                    // WeaponMuzzleCache; the transform is always by the CARRIER
                    // ship's orientation (never the gunner's), exactly as native.
                    // No cache/cvh (bare test mission) → zero offset (historic centre).
                    var muzzleOffset = _ship.Mission?.WeaponMuzzles?.Position(_ship.HullType, _mountId) ?? Vector3.Zero;
                    var myPosition = _ship.Position + muzzleOffset * shipOrientation;

                    float lifespan = GetLifespan();
                    float speed = _projectileType.Speed;
                    // c_gaSpeedAmmo (weaponIGC.cpp:220): scale projectile speed
                    // ONLY for an ammo weapon (cAmmoPerShot ≠ 0) — an energy
                    // weapon's projectile flies at the raw decoded speed.
                    if (_type.CAmmoPerShot != 0)
                        speed *= _ship.GlobalAttributeFactor(GlobalAttributeSet.SpeedAmmo);
                    bool absoluteF = _projectileType.AbsoluteF;

                    // Blast-power target: the gunner's own selected target when
                    // a gunner is assigned, else the carrier's (native
                    // weaponIGC.cpp:226-231, c_cmdCurrent). Only realised for a
                    // target in this sector — native also requires CanSee(ptarget)
                    // (weaponIGC.cpp:235); this port has no visibility system, so
                    // same-sector is the documented stand-in. No command-target
                    // subsystem exists beyond Ship.CommandTarget, so the common
                    // null case leaves this inert (a non-blast weapon never has a
                    // target here — matching native's blast-only scope).
                    Ship? ptarget = null;
                    Vector3 ppositionTarget = default, pvelocityTarget = default;
                    if (_projectileType.BlastPower != 0.0f)
                    {
                        Model? cmd = _gunner is { } gn ? gn.CommandTarget : _ship.CommandTarget;
                        if (cmd is Ship ts && ts.Sector == sector)
                        {
                            ptarget = ts;
                            ppositionTarget = ts.Position;
                            pvelocityTarget = ts.Velocity;
                        }
                    }

                    int nShots = fSelected ? 10 : 0;        // only a single shot once deselected

                    do
                    {
                        if (energy + energyDeficit < _type.EnergyPerShot)
                            _nextFire += (_type.EnergyPerShot - (energy + energyDeficit)) / rechargeRate;

                        // Perturb the forward direction by a random amount (dispersion).
                        Vector3 forward = pMyOrientation.GetForward();
                        if (_type.Dispersion != 0.0f)
                        {
                            float r = NextRandom(0.0f, _type.Dispersion);
                            float a = NextRandom(0.0f, 2.0f * MathF.PI);
                            forward += (r * MathF.Cos(a)) * pMyOrientation.GetRight()
                                     + (r * MathF.Sin(a)) * pMyOrientation.GetUp();
                            forward = forward.Normalize();  // Vector::SetNormalize (weaponIGC.cpp:264)
                        }

                        Vector3 velocity = speed * forward;
                        if (!absoluteF)
                            velocity += myVelocity;

                        Vector3 position = myPosition + myVelocity * (float)(_nextFire - lastUpdate);

                        // Target leading (blast weapons only): rewrite the round's
                        // lifespan to the intercept time (native weaponIGC.cpp:273-288).
                        // dV = v_target − v_round; dP = pos_round − pos_target
                        // (reversed so time has the right sense); t = (dV·dP)/|dV|²,
                        // clamped to [0.1, lifespan] (never explode in our face).
                        float shotLifespan = lifespan;
                        if (ptarget is not null)
                        {
                            Vector3 dV = pvelocityTarget - velocity;
                            float dV2 = dV.LengthSquared();
                            if (dV2 != 0.0f)
                            {
                                Vector3 dP = position - ppositionTarget;
                                float t = (dV.X * dP.X + dV.Y * dP.Y + dV.Z * dP.Z) / dV2;
                                shotLifespan = t > lifespan ? lifespan
                                             : t < 0.1f ? 0.1f
                                             : t;
                            }
                        }

                        // Create the projectile as a real model in the sector (CreateObject,
                        // weaponIGC.cpp:290-304; SetPosition/SetVelocity/SetCluster).
                        var projectile = new Projectile(
                            id: _ship.Mission!.NextObjectId(),
                            sector,
                            _projectileType,
                            position,
                            velocity,
                            forward,
                            now,
                            _ship,                    // m_launcher (projectileIGC.cpp:25)
                            lifespanOverride: shotLifespan);
                        sector.Add(projectile);

                        // P88: raise the host seam so the server can broadcast the
                        // real FM_CS_PROJECTILE_INSTANCE(12) message about this
                        // fresh projectile (a no-op default for a headless host).
                        // Follows Ship.ReceiveDamage's use of Mission?.Site
                        // (Ship.cs:487,494); the site is the authoritative mission's.
                        _ship.Mission?.Site.ProjectileCreatedEvent(projectile);

                        energyDeficit += rechargeRate * dtimeBurst;
                        energy -= _type.EnergyPerShot;
                        ammo -= _type.CAmmoPerShot;
                        _nextFire += dtimeBurst;
                    }
                    while ((nShots-- > 0)
                           && (_nextFire < now)
                           && (energy + energyDeficit >= _type.EnergyPerShot)
                           && (ammo > 0));

                    _ship.Ammo = (short)(ammo > 0 ? ammo : 0);
                    // P361 — auto-reload when this burst drove the pool from
                    // >0 to exactly 0 (native SetAmmo's zero-transition block,
                    // shipIGC.h:603-616). Energy-only weapons (CAmmoPerShot == 0)
                    // never consume ammo, so they are excluded — and the ship's
                    // already-zero guard above means we can't re-fire a depleted
                    // weapon, so the attempt is naturally rate-limited on top of
                    // the real debounce inside TryAutoReloadAmmo.
                    if (ammoStart > 0 && ammo == 0 && _type.CAmmoPerShot != 0)
                        _ship.TryAutoReloadAmmo(now);
                    if (ammo == 0 && _type.CAmmoPerShot != 0)
                        fSelected = false;
                    _ship.Energy = energy;
                }
            }

            if (!fSelected)
                Deactivate();
        }

        _firingShot = fFiredShot;

        if ((_ship.Energy >= _type.EnergyPerShot)
            && (_ship.Ammo >= _type.CAmmoPerShot)
            && (_firingBurst || (_active && _firingShot)))
            _firingBurst = true;
        else
            _firingBurst = false;
    }

    /// <summary><c>fIsWeaponSelected()</c> (<c>weaponIGC.h:236-254</c>). A
    /// <b>fixed</b> weapon is selected when the ship's state has
    /// <c>allWeaponsIGC</c>, or has <c>oneWeaponIGC</c> with this mount being
    /// the one selected (ripcord always satisfied — no ripcord model). A
    /// <b>turret</b> weapon (not a fixed hardpoint) is selected only when it
    /// has a gunner <em>and</em> the gunner's own state has
    /// <c>weaponsMaskIGC</c> (<c>weaponIGC.h:250</c>) — the bit the
    /// <c>ACTIVE_TURRET_UPDATE(99)</c> handler sets (``SetStateM(weaponsMaskIGC)``,
    /// <c>shipIGC.cpp:3359</c>) so a gunner firing makes its turret fire.</summary>
    private bool IsWeaponSelected()
    {
        bool fixed_ = _mountId < _ship.HullType.Hardpoints.Length
            ? _ship.HullType.Hardpoints[_mountId].Fixed
            : true;   // out-of-range mount treated as fixed (documented simplification)
        if (!fixed_)
            return _gunner is { } g && (g.StateM & ShipControlState.WeaponsMask) != 0;

        int stateM = _ship.StateM;
        return ((stateM & ShipControlState.AllWeapons) != 0)
            || (((stateM & ShipControlState.OneWeapon) != 0)
                && (_mountId == ((stateM & ShipControlState.SelectedWeaponMask) >> ShipControlState.SelectedWeaponShift)));
    }

    /// <summary>A uniform draw in [min, max) — the C++ <c>random(min,max)</c> (zlib) used for dispersion.</summary>
    private float NextRandom(float min, float max) => min + (float)(_rng.NextDouble() * (max - min));
}
