using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verification of the <b>second half of combat</b> — the collision→damage
/// hook (the "projectile that hits actually hurts" contract). Built on the
/// first half: a real <see cref="Weapon"/> fires a real <see cref="Projectile"/>
/// that the existing <see cref="CollisionDetector"/> sees; <c>Mission.Update</c>
/// is the only mover of models, and damage is applied during that tick.
/// Damage values come from the decoded <c>StaticCore</c> (projectile
/// <c>Power</c>/<c>DamageType</c>, hull <c>HitPoints</c>/<c>DefenseType</c>,
/// and the damage matrix) — nothing is hardcoded. Death + <c>KILL_SHIP</c> are
/// the <em>next</em> task and are not exercised here.
/// </summary>
public class CombatDamageTests
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

    // A hull that can actually fight (has energy/ammo to fire) and a fixed
    // hardpoint at mount 0, paired with the cheapest dispersion-0 weapon that
    // fires a real projectile and costs no more energy than the hull carries —
    // the same selection as the first half.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile)> Fight =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            // Collect every survivable combination: a fixed-hardpoint hull that
            // can afford a dispersion-0 weapon whose projectile has positive
            // power AND whose per-hit damage (power × armor cell) is strictly
            // less than the hull's hit points. A single shot must NOT kill, so
            // the point-blank test can assert the exact damage magnitude.
            var candidates = new List<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC pt)>();
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
                    {
                        float cell = core.Constants.DamageConstants[pt.DamageType][hull.DefenseType];
                        if (cell > 0f && pt.Power * cell < hull.HitPoints)
                            candidates.Add((hull, p, pt));
                    }
                }
            }
            if (candidates.Count == 0)
                throw new InvalidOperationException("no survivable fightable hull+weapon in the representative core");
            // The gentlest survivor (lowest per-hit damage) — a clean, non-lethal point-blank hit.
            return candidates
                .OrderBy(c => c.pt.Power * core.Constants.DamageConstants[c.pt.DamageType][c.hull.DefenseType])
                .First();
        });

    /// <summary>The per-hit hull damage this weapon's projectile inflicts on
    /// <paramref name="target"/>'s hull: <c>Power · dtmArmor</c> — the decoded
    /// power times the decoded damage matrix cell for this damage-type × the
    /// hull's defense-type (<c>shipIGC.cpp:1233,1298</c>).</summary>
    private static float ExpectedHit(DataHullTypeIGC hull, DataProjectileTypeIGC pt)
    {
        var core = RepresentativeCore.Value;
        return pt.Power * core.Constants.DamageConstants[pt.DamageType][hull.DefenseType];
    }

    private static Ship NewShooter() => new(
        shipId: 9401, hull: Fight.Value.hull,
        position: Vector3.Zero, velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static Ship NewTarget(float z) => new(
        shipId: 9501, hull: Fight.Value.hull,
        position: new Vector3(0f, 0f, z), velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    /// <summary>Arm the shooter with the fight weapon, cap its burst to exactly
    /// one projectile, and select it (same trick as the first half).</summary>
    private static Weapon ArmShooter(Ship shooter)
    {
        var (_, weaponType, _) = Fight.Value;
        shooter.StateM |= ShipControlState.AllWeapons;
        if (weaponType.CAmmoPerShot > 0)
            shooter.Ammo = 1;
        else
            shooter.Energy = weaponType.EnergyPerShot;
        return shooter.MountWeapon(weaponType);
    }

    /// <summary>Single-chunk mission ticks until <paramref name="predicate"/>
    /// holds (or <paramref name="maxSteps"/> elapse).</summary>
    private static bool TickUntil(Mission m, int maxSteps, Func<bool> predicate)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < maxSteps; i++)
        {
            t += 0.031;                       // < 0.25 ⇒ n == 1 chunk per Update
            m.Update(t);
            if (predicate()) return true;
        }
        return false;
    }

    // ── point blank: a hit hurts, and only the target ─────────────────────

    /// <summary>
    /// ⭐ The headline contract: a projectile that reaches a ship deals that
    /// ship hull damage equal to its <b>decoded</b> power (times the decoded
    /// armor matrix), and the <b>shooter</b> is left untouched. Two ships nose
    /// to nose on the −z axis; the shooter (identity, forward −z) fires one
    /// round straight into the target ahead. We tick until a hit lands, then
    /// assert the target's hull dropped by exactly the decoded damage while the
    /// shooter's stayed full. The projectile terminates on impact.
    /// </summary>
    [Fact]
    public void Point_blank_hit_reduces_target_hull_and_not_shooter()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        var (_, _, pt) = Fight.Value;
        float travel = pt.Speed * pt.Lifespan;                     // decoded reach of the round
        float gap = MathF.Min(200f, 0.5f * travel);                // a comfortable stop inside its range
        gap = MathF.Max(gap, 30f);                                 // but clear of both ships' half-lengths
        var target = NewTarget(z: -gap);                           // dead ahead
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmShooter(shooter);

        float expected = ExpectedHit(target.HullType, pt);
        float shooterMax = shooter.HitPoints;
        float targetMax = target.HitPoints;
        Assert.True(expected > 0f, "the representative weapon must inflict positive hull damage");

        Assert.True(TickUntil(mission, 800, () => target.HitPoints < targetMax),
                    "a projectile fired straight into the target must land and reduce its hull");
        Assert.Empty(sector.Models.OfType<Projectile>());          // spent on impact

        Assert.Equal(targetMax - expected, target.HitPoints, 2);   // decoded damage, not a constant
        Assert.Equal(shooterMax, shooter.HitPoints);               // the shooter is unhurt
    }

    // ── fired away: never damages anything ────────────────────────────────

    /// <summary>
    /// A projectile fired <b>away</b> from any target damages nothing. The
    /// shooter (identity, forward −z) fires into the empty −z; the target sits
    /// behind at +z, off the launch path. We tick until the round has flown its
    /// full (decoded) lifespan and expired — plenty of opportunity to hit
    /// anything — then assert both ships still have full hulls. Guards against
    /// the naive bug where the firing ship damages itself on spawn.
    /// </summary>
    [Fact]
    public void Projectile_fired_away_damages_nothing()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        var target = NewTarget(z: +120f);             // behind the shooter, off the launch path
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmShooter(shooter);

        float shooterMax = shooter.HitPoints;
        float targetMax = target.HitPoints;

        // Let the projectile fire, fly, and expire. Wait until no projectile
        // remains in the sector (robust to the burst interval delaying the shot
        // past any nominal "now + lifespan" estimate), then assert nobody hurt.
        Assert.True(TickUntil(mission, 3000, () => !sector.Models.OfType<Projectile>().Any()),
                    "the round must eventually leave the sector");

        Assert.Equal(shooterMax, shooter.HitPoints);   // self-damage on spawn is impossible
        Assert.Equal(targetMax, target.HitPoints);     // the missed target is unscathed
    }

    // ── friendly fire: same side → zero damage; cross side → normal ───────

    /// <summary>
    /// ⭐ The friendly-fire gate (<c>shipIGC.cpp:1222-1228</c>): a projectile
    /// fired at a ship on the <b>same</b> <see cref="Ship.SideId"/> lands no
    /// damage. Both ships carry side 0 (a real side — mission side id 0 from
    /// <see cref="SideSpecs"/>). The shooter fires straight into the target
    /// ahead; we let the round fly its full (decoded) lifespan and expire,
    /// then assert the friendly target's hull is untouched — a friendly round
    /// passes through its ally without landing and without terminating (it
    /// reaches the target's position and just sails on, mirroring the
    /// launcher self-exclusion). Guard against the gate being inverted (only
    /// same-side foes harmed) or bypassed.
    /// </summary>
    [Fact]
    public void Same_side_shot_deals_zero_damage()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        shooter.SideId = 0;
        var (_, _, pt) = Fight.Value;
        float travel = pt.Speed * pt.Lifespan;
        float gap = MathF.Max(MathF.Min(200f, 0.5f * travel), 30f);
        var target = NewTarget(z: -gap);
        target.SideId = 0;                                     // same side as the shooter
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmShooter(shooter);

        float targetMax = target.HitPoints;

        // Fire, let the round cross the target and fly out to expiry, then
        // assert the ally never took a hit.
        Assert.True(TickUntil(mission, 7000, () => !sector.Models.OfType<Projectile>().Any()),
                    "the friendly round must eventually leave the sector");

        Assert.Equal(targetMax, target.HitPoints);   // same-side target takes zero damage
        Assert.True(targetMax > 0f);
    }

    /// <summary>
    /// A projectile fired at a ship on a <b>different</b> side deals damage
    /// exactly as before the friendly-fire gate — the gate must not suppress
    /// legitimate cross-team combat. Shooter on side 0, target on side 1,
    /// nose to nose; the round lands and the target's hull drops by the
    /// decoded per-hit damage (same assertion as the headline point-blank
    /// contract).
    /// </summary>
    [Fact]
    public void Cross_side_shot_deals_normal_damage()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        shooter.SideId = 0;
        var (_, _, pt) = Fight.Value;
        float travel = pt.Speed * pt.Lifespan;
        float gap = MathF.Max(MathF.Min(200f, 0.5f * travel), 30f);
        var target = NewTarget(z: -gap);
        target.SideId = 1;                                    // different side from the shooter
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmShooter(shooter);

        float expected = ExpectedHit(target.HullType, pt);
        float targetMax = target.HitPoints;
        Assert.True(expected > 0f, "the representative weapon must inflict positive hull damage");

        Assert.True(TickUntil(mission, 900, () => target.HitPoints < targetMax),
                    "a cross-side projectile fired into the target must land and reduce its hull");

        Assert.Equal(targetMax - expected, target.HitPoints, 2);   // decoded damage, not a constant
    }
}
