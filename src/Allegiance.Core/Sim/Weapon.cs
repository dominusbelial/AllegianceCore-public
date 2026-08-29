using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// A weapon mounted on a <see cref="Ship"/> — the port of <c>CweaponIGC</c>
/// (<c>src/Igc/weaponIGC.cpp</c>, 348 lines; <c>weaponIGC.h</c>), kept to the
/// minimum that lets a ship fire: the armed/active state, the cooldown gate, the
/// energy/ammo checks, and the projectile creation in
/// <c>FireWeapon</c> (<c>weaponIGC.cpp:142-348</c>). The mounting state machine
/// (the <c>mountedFraction</c> ramp, <c>weaponIGC.h:30-49</c>) is <b>not</b>
/// modelled — the part-equipping/unmounting path is out of this task's scope, so
/// a weapon is constructed <b>armed</b> (ready to fire once selected and
/// off-cooldown).
/// </summary>
/// <remarks>
/// Faithfulness notes (every deviation documented so the port stays honest):
/// <list type="bullet">
/// <item><b>No gunner.</b> <c>m_pshipGunner</c> is always null here (turret
/// gunners belong to the ship/child-ship slice), so firing uses the ship's
/// own orientation and <c>SetLauncher(ship)</c>.</item>
/// <item><b>No side global attributes.</b> <c>GetGlobalAttributeSet()</c>
/// (economy/tech) is unimplemented, so the <c>c_gaSpeedAmmo</c> speed boost and
/// <c>c_gaLifespanEnergy</c> lifespan modifier are omitted (both multiply by
/// the attribute's value, whose default is 1.0 — so omitting them is the
/// faithful no-side value).</item>
/// <item><b>No hardpoint geometry.</b> The weapon muzzle offset
/// (<c>m_pposition</c>, <c>weaponIGC.cpp:123</c>) comes from a loaded hull mesh
/// that this headless port does not load, so it is the origin: projectiles
/// spawn at the ship's centre (<c>weaponIGC.cpp:203</c> with a zero offset).</item>
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
    private readonly short _mountId;
    private readonly Random _rng;          // dispersion source (weaponIGC.cpp:259-262)

    private double _nextFire;              // Time m_nextFire (weaponIGC.cpp:267)
    private bool _active;                  // m_fActive (weaponIGC.cpp:26)
    private bool _firingShot;              // m_fFiringShot
    private bool _firingBurst;             // m_fFiringBurst

    /// <summary>Construct an <b>armed</b> weapon (the port's shortcut for the
    /// out-of-scope mounting state machine). Ready to fire once selected,
    /// off-cooldown, and with energy/ammo.</summary>
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

    /// <summary>Whether the weapon is active (has been selected and armed; <c>m_fActive</c>).</summary>
    public bool Active => _active;

    /// <summary>Whether the last <c>Update</c> produced a shot (<c>m_fFiringShot</c>).</summary>
    public bool FiringShot => _firingShot;

    /// <summary>Whether the weapon is mid-burst (<c>m_fFiringBurst</c>).</summary>
    public bool FiringBurst => _firingBurst;

    /// <summary><c>GetLifespan()</c> (<c>weaponIGC.h:165-172</c>): the projectile
    /// type's lifespan, without the side <c>c_gaLifespanEnergy</c> attribute
    /// multiplier (no side attribute set — see class doc).</summary>
    public float GetLifespan() => _projectileType.Lifespan;

    /// <summary><c>GetDtBurst()</c> (<c>weaponIGC.h:179-182</c>): the interval between shots in a burst.</summary>
    public float GetDtBurst() => _type.DtimeBurst;

    /// <summary>
    /// Per-part update — the port of <c>CweaponIGC::Update</c>
    /// (<c>weaponIGC.h:30-63</c>) minus the mounting ramp: select-then-activate,
    /// and fire when active. Calling this is what "firing on demand from a test"
    /// means this slice — the ship's <see cref="Ship.OnUpdate"/> invokes it for
    /// each mounted weapon inside the authoritative <c>Mission.Update</c>, and a
    /// test can also call it directly to fire at an exact time.
    /// </summary>
    public void Update(double now)
    {
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

        if (_active && _nextFire < now)
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

                if (ammo > 0 || _type.CAmmoPerShot == 0)
                {
                    fFiredShot = true;

                    var sector = _ship.Sector
                        ?? throw new InvalidOperationException("a placed ship must be in a sector before it can fire");
                    var shipOrientation = _ship.Orientation;
                    float dtimeBurst = _type.DtimeBurst;
                    float recharge = rechargeRate * dtimeBurst;
                    var myVelocity = _ship.Velocity;
                    var myPosition = _ship.Position;        // + weapon muzzle offset (none: no hull mesh)

                    float lifespan = GetLifespan();
                    float speed = _projectileType.Speed;    // c_gaSpeedAmmo boost omitted (no side)
                    bool absoluteF = _projectileType.AbsoluteF;

                    int nShots = fSelected ? 10 : 0;        // only a single shot once deselected

                    do
                    {
                        if (energy + energyDeficit < _type.EnergyPerShot)
                            _nextFire += (_type.EnergyPerShot - (energy + energyDeficit)) / rechargeRate;

                        // Perturb the forward direction by a random amount (dispersion).
                        Vector3 forward = shipOrientation.GetForward();
                        if (_type.Dispersion != 0.0f)
                        {
                            float r = NextRandom(0.0f, _type.Dispersion);
                            float a = NextRandom(0.0f, 2.0f * MathF.PI);
                            forward += (r * MathF.Cos(a)) * shipOrientation.GetRight()
                                     + (r * MathF.Sin(a)) * shipOrientation.GetUp();
                            forward = forward.Normalize();  // Vector::SetNormalize (weaponIGC.cpp:264)
                        }

                        Vector3 velocity = speed * forward;
                        if (!absoluteF)
                            velocity += myVelocity;

                        Vector3 position = myPosition + myVelocity * (float)(_nextFire - lastUpdate);

                        // Create the projectile as a real model in the sector (CreateObject,
                        // weaponIGC.cpp:290-304; SetPosition/SetVelocity/SetCluster).
                        var projectile = new Projectile(
                            id: _ship.Mission!.NextObjectId(),
                            sector,
                            _projectileType,
                            position,
                            velocity,
                            forward,
                            now);
                        sector.Add(projectile);

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

    /// <summary><c>fIsWeaponSelected()</c> (<c>weaponIGC.h:236-254</c>) for a
    /// fixed weapon with no gunner: selected when the ship's state has
    /// <c>allWeaponsIGC</c>, or has <c>oneWeaponIGC</c> with this mount being
    /// the one selected. (Turret/gunner path — <c>m_pshipGunner &amp;
    /// weaponsMaskIGC</c> — is null here, so it yields false.) The ripcord term
    /// is always satisfied (no ripcord model).</summary>
    private bool IsWeaponSelected()
    {
        bool fixed_ = _mountId < _ship.HullType.Hardpoints.Length
            ? _ship.HullType.Hardpoints[_mountId].Fixed
            : true;   // out-of-range mount treated as fixed (documented simplification)
        if (!fixed_)
            return false;   // turret gun: requires a gunner (none in this slice)

        int stateM = _ship.StateM;
        return ((stateM & ShipControlState.AllWeapons) != 0)
            || (((stateM & ShipControlState.OneWeapon) != 0)
                && (_mountId == ((stateM & ShipControlState.SelectedWeaponMask) >> ShipControlState.SelectedWeaponShift)));
    }

    /// <summary>A uniform draw in [min, max) — the C++ <c>random(min,max)</c> (zlib) used for dispersion.</summary>
    private float NextRandom(float min, float max) => min + (float)(_rng.NextDouble() * (max - min));
}
