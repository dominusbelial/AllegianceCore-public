using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P277 — the nanite gun (field-repair weapon). Native Allegiance has no
/// dedicated "Repair" weapon <em>type</em>: a nan gun is an ordinary
/// <c>DataWeaponTypeIGC</c> part whose fired projectile carries <b>negative
/// power</b> (<c>GetProjectileType()->GetPower() &lt; 0.0</c> is exactly how
/// native's AI recognises a repair weapon, <c>shipIGC.cpp:2608</c>). The
/// repair itself happens in <c>ReceiveDamage</c>'s <c>amount &lt; 0</c> branch
/// (<c>shipIGC.cpp:1244-1267</c>), which this port already implements
/// (<c>Ship.ReceiveDamage</c>, <c>Ship.cs:2907-2915</c>) and which the
/// <c>Station.ReceiveDamage</c> friendly-fire gate already lets through
/// (<c>Station.cs:246-248</c>, gate guarded on <c>amount &gt;= 0.0f</c>).
///
/// The one missing link this task adds is the SHIP friendly-fire gate: the
/// sector collision pass (<c>Sector.ResolveCombatCollisions</c>) suppressed
/// <em>every</em> same-side hit unconditionally, so a nan round could never
/// reach a friendly's hull. Native's gate is sign-sensitive
/// (<c>shipIGC.cpp:1222-1228</c> returns <c>c_drNoDamage</c> only when
/// <c>amount &gt;= 0.0f</c>), so a negative-power round must land on the ally.
///
/// These tests prove the real decoded nan weapon (ER Nanite, projectiles with
/// <c>Power &lt; 0</c>) fires, lands on a same-side target, and restores its
/// hull — plus the faithful native wrinkle that a nan heals <b>whichever</b>
/// hull it strikes (friend <em>or</em> foe; native's repair branch has no side
/// check, the side test only decides the launcher's repair-stat, which this
/// port has no infrastructure for and already omits).
/// </summary>
public class NaniteRepairTests
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

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return [new SideSpec(0, "Team 1", civ.CivilizationId)];
    }

    private static Mission NewStartedMission(IIgcSite? site = null)
    {
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    // A real nan weapon from the decoded core: a fixed-hardpoint hull that can
    // afford the nan's energy-per-shot, paired with a Weapon whose projectile
    // has negative Power (the native repair-weapon discriminant). The nan is an
    // energy weapon (cAmmoPerShot == 0), so only the energy gate matters.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile)> Nan =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.MaxEnergy > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                         .OrderBy(h => h.HullId))
            {
                foreach (var w in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                             .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                             .Where(w => w.EnergyPerShot <= hull.MaxEnergy)
                             .OrderBy(w => w.ProjectileTypeId))
                {
                    if (core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) && pt.Power < 0f)
                        return (hull, w, pt);
                }
            }
            throw new InvalidOperationException("no mountable nan weapon (negative-power projectile) in the representative core");
        });

    private static Ship NewShooter() => new(
        shipId: 9701, hull: Nan.Value.hull,
        position: Vector3.Zero, velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static Ship NewTarget(float z) => new(
        shipId: 9721, hull: Nan.Value.hull,
        position: new Vector3(0f, 0f, z), velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static Weapon ArmShooter(Ship shooter)
    {
        var (_, weaponType, _) = Nan.Value;
        shooter.StateM |= ShipControlState.AllWeapons;   // fire every fixed weapon
        return shooter.MountWeapon(weaponType);
    }

    /// <summary>The exact hull fraction a single nan hit restores:
    /// <c>-Power · dtmArmor / maxHP</c> (<c>shipIGC.cpp:1261</c>,
    /// <c>Ship.ReceiveDamage</c> repair branch) — Power is negative, so this is
    /// a positive <c>ΔFraction</c>. maxHP is the side-GA-scaled effective max
    /// (P467b: the denominator is <c>GetHitPoints()</c> = raw × c_gaMaxArmorShip,
    /// so the target's civ base 0.9 scales the repair 1/0.9 = 1.111×).</summary>
    private static float ExpectedRepairFraction(Ship target)
    {
        var core = RepresentativeCore.Value;
        var (_, _, pt) = Nan.Value;
        float dtmArmor = core.Constants.DamageConstants[pt.DamageType][target.HullType.DefenseType];
        return -pt.Power * dtmArmor / target.EffectiveMaxHitPoints();
    }

    /// <summary>Drive the target's hull to a known damaged state below full, by
    /// writing <c>_fraction</c> directly (deterministic start for the repair
    /// assertion; <c>Fraction</c> is get-only).</summary>
    private static void DamageHullTo(Ship ship, float fraction)
    {
        var f = typeof(Ship).GetField("_fraction", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ship._fraction field not found");
        f.SetValue(ship, fraction);
    }

    private static bool TickUntil(Mission m, int maxSteps, Func<bool> predicate)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < maxSteps; i++)
        {
            t += 0.025;                          // < 0.25 ⇒ n == 1 chunk per Update
            m.Update(t);
            if (predicate()) return true;
        }
        return false;
    }

    // ── the friendly repair: the whole point of the task ──────────────────

    /// <summary>
    /// ⭐ A nan round fired at a SAME-SIDE (friendly) ship restores its hull.
    /// Two ships nose to nose on the −z axis, both on side 0; the target's hull
    /// is driven to 0.5; the shooter fires one nan round straight into it. The
    /// round lands (not swallowed by the friendly-fire gate), terminates on
    /// impact, and the target's fraction climbs back up by the decoded repair
    /// amount. This is the P277 gate fix — without it the friendly round sails
    /// through and the hull never moves.
    /// </summary>
    [Fact]
    public void Same_side_nan_round_repairs_friendly_hull()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        shooter.SideId = 0;
        var (_, _, pt) = Nan.Value;
        float travel = pt.Speed * pt.Lifespan;                 // decoded reach of the round
        float gap = MathF.Max(MathF.Min(200f, 0.5f * travel), 30f);
        var target = NewTarget(z: -gap);
        target.SideId = 0;                                     // same side as the shooter
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmShooter(shooter);
        DamageHullTo(target, 0.5f);
        float expectedRepair = ExpectedRepairFraction(target);
        Assert.True(expectedRepair > 0f, "the nan must restore a positive hull fraction");

        Assert.True(TickUntil(mission, 1200, () => target.Fraction > 0.5f),
                    "a friendly nan round must land on the same-side target and repair its hull");

        Assert.Empty(sector.Models.OfType<Projectile>());      // spent on impact
        Assert.Equal(0.5f + expectedRepair, target.Fraction, 3);   // decoded repair, not a constant
        Assert.Equal(1.0f, shooter.Fraction);                  // the shooter is unaffected
    }

    // ── the faithful native wrinkle: nan heals whatever it hits ───────────

    /// <summary>
    /// Native's repair branch has no side check — the <c>amount &lt; 0</c> path
    /// restores <c>m_fraction</c> unconditionally, and the friendly-fire gate
    /// only guards <c>amount &gt;= 0</c> (<c>shipIGC.cpp:1222-1228,1244-1267</c>).
    /// So a nan round that strikes an ENEMY heals the enemy's hull too (the
    /// side test exists solely to award the launcher a repair-stat, which this
    /// port omits). This documents the chosen behavior: the nan is NOT a
    /// damage-dealer and NOT side-discriminating — you must aim it carefully.
    /// </summary>
    [Fact]
    public void Cross_side_nan_round_also_restores_enemy_hull()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        shooter.SideId = 0;
        var (_, _, pt) = Nan.Value;
        float travel = pt.Speed * pt.Lifespan;
        float gap = MathF.Max(MathF.Min(200f, 0.5f * travel), 30f);
        var target = NewTarget(z: -gap);
        target.SideId = 1;                                     // enemy side
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmShooter(shooter);
        DamageHullTo(target, 0.4f);
        float expectedRepair = ExpectedRepairFraction(target);

        Assert.True(TickUntil(mission, 1200, () => target.Fraction > 0.4f),
                    "a nan round striking an enemy must still restore its hull (native heals whatever it hits)");
        Assert.Equal(0.4f + expectedRepair, target.Fraction, 3);
    }

    // ── the positive-power gate is still intact (regression guard) ────────

    /// <summary>
    /// A normal POSITIVE-power round fired at a same-side ship must still be
    /// suppressed by the friendly-fire gate — the P277 change is sign-sensitive
    /// and must not weaken friendly-fire protection for damaging weapons. Uses
    /// the same nose-to-nose setup as <see cref="CombatDamageTests.Same_side_shot_deals_zero_damage"/>
    /// but re-checks it here as a guard that the nan gate edit didn't open a
    /// hole for real weapons.
    /// </summary>
    [Fact]
    public void Same_side_positive_power_round_still_deals_no_damage()
    {
        // Reuse the fightable hull+weapon from CombatDamageTests' Fight pick:
        // the cheapest positive-power projectile the fight hull can afford.
        var core = RepresentativeCore.Value;
        var (hull, weapon, _) = FightPick(core);
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = new Ship(9801, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        float gap = 100f;
        var target = new Ship(9821, hull, new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        shooter.StateM |= ShipControlState.AllWeapons;
        shooter.MountWeapon(weapon);
        float targetMax = target.HitPoints;

        Assert.True(TickUntil(mission, 3000, () => !sector.Models.OfType<Projectile>().Any()),
                    "the positive round must eventually leave the sector");
        Assert.Equal(targetMax, target.HitPoints);             // same-side ally still takes zero damage
    }

    // ── direct unit check of the repair branch ────────────────────────────

    /// <summary>
    /// <c>ReceiveDamage</c>'s negative-amount path restores hull fraction and
    /// clips at 1.0 (<c>shipIGC.cpp:1244-1267</c>), and does NOT touch the
    /// shield (the repair branch bypasses shields entirely — nan heals hull,
    /// not shields, per AFS/Nanning). This is the branch the gate fix now
    /// feeds; it was previously unreachable dead code.
    /// </summary>
    [Fact]
    public void ReceiveDamage_negative_amount_repairs_hull_and_does_not_touch_shield()
    {
        var ship = NewTarget(-100f);
        DamageHullTo(ship, 0.6f);
        var shield = typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        shield.SetValue(ship, 0.4f);
        float dtmArmor = RepresentativeCore.Value.Constants
            .DamageConstants[Nan.Value.projectile.DamageType][ship.HullType.DefenseType];

        var dr = ship.ReceiveDamage(
            Nan.Value.projectile.DamageType,   // damage type is irrelevant to repair magnitude except via dtmArmor
            Nan.Value.projectile.Power,        // negative power ⇒ repair
            now: 0.0,
            position1: Vector3.Zero,
            position2: Vector3.Zero,
            launcher: null);

        Assert.Equal(DamageResult.NoDamage, dr);               // a repair is not a damage event
        float expected = 0.6f - Nan.Value.projectile.Power * dtmArmor / ship.HullType.HitPoints;
        Assert.Equal(expected, ship.Fraction, 3);              // hull restored
        Assert.Equal(0.4f, ship.ShieldFraction);               // shield untouched
    }

    // A positive-power weapon the fight hull can afford (mirrors
    // CombatDamageTests.Fight) — used only by the gate-guard test above.
    private static (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC pt) FightPick(StaticCore core)
    {
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p, pt);
            }
        }
        throw new InvalidOperationException("no fightable positive-power weapon in the representative core");
    }
}
