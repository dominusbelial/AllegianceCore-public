using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P109 firing proofs — <b>scenario 5</b> (a turret weapon with a gunner whose
/// orientation genuinely differs from the carrier fires a projectile whose
/// direction follows the <b>gunner</b>, not the carrier) and <b>scenario 6</b>
/// (a fixed mount — which never has a gunner, native asserts
/// <c>w-&gt;GetGunner()</c> null on <c>[0, MaxFixedWeapons)</c>, shipIGC.cpp:322
/// — behaves exactly as before). Pure unit tests on a mission (no network, no
/// server), mirroring the <see cref="CombatWeaponFiringTests"/> recipe: the
/// weapon is driven by its own <c>Update</c> at controlled times, with energy/
/// ammo seeded for exactly one projectile so assertions never race a burst.
/// </summary>
public class TurretGunnerFiringTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
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

    private static Mission NewStartedMission()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(core, SomeMap.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    // A carrier hull that has a NON-FIXED (turret) hardpoint AND can feed a
    // real dispersion-0 projectile weapon, plus the cheapest such weapon and
    // its resolved projectile. Not hardcoded — all read from the decoded core.
    private static readonly Lazy<(DataHullTypeIGC hull, int turretMount, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile)> TurretFight =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.MaxEnergy > 0 && h.Hardpoints.Length > 0
                                     && Enumerable.Range(0, h.Hardpoints.Length).Any(i => !h.Hardpoints[i].Fixed))
                         .OrderBy(h => h.HullId))
            {
                int turretMount = Enumerable.Range(0, hull.Hardpoints.Length).First(i => !hull.Hardpoints[i].Fixed);
                foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                             .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                             .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy
                                         && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) && pt.Power > 0f)
                             .OrderBy(w => w.EnergyPerShot))
                {
                    if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt))
                        return (hull, turretMount, p, pt);
                }
            }
            throw new InvalidOperationException("no turret-fightable hull+weapon in the representative core");
        });

    /// <summary>A gunner whose orientation is yawed 90° from identity so its
    /// forward (−z rot-90°) genuinely differs from the carrier's forward
    /// (identity = −z). All projections below are pure rotational transforms,
    /// so the fired-round heading and the gunner's heading are compared
    /// exactly, no trigonometry.</summary>
    private static Ship NewGunner(Mission m, Orientation o)
    {
        var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var g = new Ship(7701, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, o);
        m.PlaceShip(g, m.Sectors[0]);
        return g;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Turret_weapon_fires_along_gunner_orientation_not_carrier()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var (hull, turretMount, weaponType, pt) = TurretFight.Value;

        // Carrier: identity orientation (forward = −z).
        var carrier = new Ship(7702, hull, new Vector3(50f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(carrier, sector);
        var turretWeapon = carrier.MountWeapon(weaponType, (short)turretMount);

        // Gunner: yawed 90°, so its forward is distinctly off the carrier's.
        var gunnerOri = new Orientation();
        gunnerOri.Yaw(MathF.PI / 2f);                       // forward now ≈ (−1·s, 0, −c)? — off −z by 90°
        var gunner = NewGunner(mission, gunnerOri);

        // Bind the gunner (the P109 server step), and mark it firing.
        turretWeapon.SetGunner(gunner);
        gunner.StateM |= ShipControlState.WeaponsMask;      // turret selection gate (weaponIGC.h:250)

        // Seed for a single shot: one round of ammo or exactly one energy shot.
        if (weaponType.CAmmoPerShot > 0) carrier.Ammo = 1;
        else carrier.Energy = weaponType.EnergyPerShot;

        // Drive the weapon directly: first Update selects+activates, the next
        // (past cooldown) fires. Both off the carrier's clock (LastUpdate=0).
        double t0 = 0.0;
        turretWeapon.Update(t0);                             // select → activate
        Assert.True(turretWeapon.Active, "a gunned turret weapon must activate");
        double fire = turretWeapon.GetDtBurst() + 0.003;
        turretWeapon.Update(fire);                           // first chunk past cooldown → one shot

        var proj = Assert.Single(sector.Models.OfType<Projectile>());
        Assert.Equal(pt, proj.ProjectileType);

        Vector3 gunnerForward = gunnerOri.GetForward();      // the aim source (gunner's live orientation)
        Assert.NotEqual(carrier.Orientation.GetForward(), gunnerForward);
        // The round's velocity direction follows the gunner, not the carrier.
        Assert.Equal(gunnerForward.X, proj.Velocity.Normalize().X, 2);
        Assert.Equal(gunnerForward.Y, proj.Velocity.Normalize().Y, 2);
        Assert.Equal(gunnerForward.Z, proj.Velocity.Normalize().Z, 2);
        // And it is NOT the carrier's heading: the gunner is yawed about +Y,
        // so the discriminating component is X (Y is unchanged by a pure yaw —
        // it is NOT a bug that proj.Y matches carrier.Y).
        Assert.NotEqual(carrier.Orientation.GetForward().X, proj.Velocity.Normalize().X, 2);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Fixed_weapon_without_gunner_keeps_firing_along_carrier()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var (hull, turretMount, weaponType, _) = TurretFight.Value;

        // A fixed weapon on mount 0 (carrier identity → forward = −z), no
        // gunner — the native guarantees a fixed mount never has a gunner
        // (assert !w->GetGunner(), shipIGC.cpp:322). Selecting uses the
        // all-weapons carrier state, NOT the (absent) gunner.
        var carrier = new Ship(7703, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(carrier, sector);
        var fixedWeapon = carrier.MountWeapon(weaponType, 0);
        Assert.Null(fixedWeapon.Gunner);                     // never a gunner
        Assert.True(hull.Hardpoints[0].Fixed, "mount 0 must be a fixed hardpoint for this regression");

        carrier.StateM |= ShipControlState.AllWeapons;       // fixed selection gate (weaponIGC.h:243)
        if (weaponType.CAmmoPerShot > 0) carrier.Ammo = 1;
        else carrier.Energy = weaponType.EnergyPerShot;

        fixedWeapon.Update(0.0);                             // activate
        fixedWeapon.Update(fixedWeapon.GetDtBurst() + 0.004); // fire

        var proj = Assert.Single(sector.Models.OfType<Projectile>());
        var forward = proj.Velocity.Normalize();
        Assert.Equal(0f, forward.X, 2);
        Assert.Equal(0f, forward.Y, 2);
        Assert.Equal(-1f, forward.Z, 2);                     // identity carrier forward = −z, unchanged
    }

    /// <summary>Target leading for a blast weapon (gunner's own target) adjusts
    /// the round's lifespan to the intercept time rather than the decoded
    /// value — the gunner-target selection path of the aim slice.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Blast_weapon_uses_gunner_target_and_leads_it()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var (hull, turretMount, weaponType, _) = TurretFight.Value;

        // Find a BLAST projectile weapon (BlastPower ≠ 0) that fits this hull.
        var core = mission.Core;
        var blastWeapon = core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type)
            .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy
                        && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var bp) && bp.BlastPower != 0f)
            .FirstOrDefault();
        Assert.NotNull(blastWeapon);   // the representative core has a blast gun; if not this whole premise is moot
        var bwt = blastWeapon!;
        core.ProjectileTypes.TryGetValue(bwt.ProjectileTypeId, out var bpt);

        var carrier = new Ship(7704, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(carrier, sector);
        var weapon = carrier.MountWeapon(bwt, (short)turretMount);
        var gunner = NewGunner(mission, Orientation.Identity);
        weapon.SetGunner(gunner);
        gunner.StateM |= ShipControlState.WeaponsMask;
        if (bwt.CAmmoPerShot > 0) carrier.Ammo = 1; else carrier.Energy = bwt.EnergyPerShot;

        // A target in the same sector, positioned ahead on the round's path and
        // moving TOWARD the round (+z), so the intercept time is finite. The
        // gunner selects it (gunner's own command target — weaponIGC.cpp:228-229).
        // Choose the gap so the intercept time lands mid-lifespan (guaranteeing
        // it differs from the decoded lifespan, exercising the leading clamp's
        // interior): gap = relativeClosing · (lifespan·0.5).
        float S = bpt!.Speed;
        float relativeClosing = S + 50f;                 // |v_target − v_round| with target closing at 50 m/s
        float gap = relativeClosing * (bpt.Lifespan * 0.5f);
        var target = new Ship(7705, hull, new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(target, sector);
        target.Velocity = new Vector3(0f, 0f, 50f);       // closing on the round along +z
        gunner.CommandTarget = target;

        float fire = weapon.GetDtBurst() + 0.005f;
        weapon.Update(0.0);
        weapon.Update(fire);
        var proj = Assert.Single(sector.Models.OfType<Projectile>());

        // Deterministic intercept: round (carrier at origin, zero velocity) fires
        // along −z at pt.Speed; target sits at z=−gap closing at 50 m/s. Native
        // leading rewrites lifespan = (dV·dP)/|dV|² clamped to [0.1, decoded].
        // Replicate and assert the round's actual lifespan (TimeExpire − fire)
        // matches exactly — which here is lifespan·0.5 ≠ decoded.
        float intercept = gap / relativeClosing;         // = lifespan·0.5 by construction
        float expectedLife = intercept > bpt.Lifespan ? bpt.Lifespan
                            : intercept < 0.1f ? 0.1f
                            : intercept;
        float actualLife = (float)(proj.TimeExpire - fire);
        Assert.Equal(expectedLife, actualLife, 3);
        Assert.NotEqual(bpt.Lifespan, actualLife);       // the gunner target changed the lifespan off the decoded value
        Console.WriteLine($"BLAST-LEAD: turret blast round lifespan {actualLife:F3}s (intercept {intercept:F3}, decoded {bpt.Lifespan:F3})");
    }
}
