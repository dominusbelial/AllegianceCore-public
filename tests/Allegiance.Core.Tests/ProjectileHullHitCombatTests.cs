using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verification of the <b>projectile impact spark</b> trigger (P364) — the port
/// of native <c>pModel-&gt;GetThingSite()-&gt;AddHullHit(pos, pos.Normalize())</c>
/// (<c>projectileIGC.cpp:220-226</c>). When a projectile (not a missile) lands on
/// a damagable target, deals hull-or-worse damage, has no blast power and carries
/// no <c>c_dmgidNoDebris</c> flag, <c>Sector.ResolveCombatCollisions</c> raises
/// <see cref="IIgcSite.ProjectileHitEvent"/> (the host seam P364 bridges to the
/// <c>FM_S_PROJECTILE_HULL_HIT(213)</c> broadcast), witnessed here through a
/// <see cref="RecordingIgcSite"/> exactly as <see cref="CombatDeathTests"/>
/// witnesses <c>KillShipEvent</c>. Built on the same <c>StaticCore</c>-driven
/// fight setup as CombatDeathTests.
/// </summary>
public class ProjectileHullHitCombatTests
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

    private static Mission NewStartedMission(RecordingIgcSite site)
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(core, SomeMap.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    // Reuse the CombatDeathTests Fight fixture's weapon/hull/projectile triple:
    // a hull that can actually fight and the strongest per-hit gun, so hits land
    // promptly and the target visibly takes damage.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile)> Fight =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            var candidates = new List<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC pt, double perHit)>();
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                         .OrderBy(h => h.HullId))
            {
                foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                             .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                             .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy))
                {
                    if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    {
                        float cell = core.Constants.DamageConstants[pt.DamageType][hull.DefenseType];
                        if (cell > 0f)
                            candidates.Add((hull, p, pt, pt.Power * cell));
                    }
                }
            }
            if (candidates.Count == 0)
                throw new InvalidOperationException("no fightable hull+weapon in the representative core");
            var best = candidates.OrderByDescending(c => c.perHit).First();
            return (best.hull, best.weapon, best.pt);
        });

    // A GENTLE weapon for the per-hit growth test: weakest per-hit damage that
    // still hurts, so the target survives a long barrage and the impact event
    // count demonstrably keeps rising with every landed hit (the Fight weapon
    // would kill the target in a few hits, freezing the count).
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile)> Gentle =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            var candidates = new List<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC pt, double perHit)>();
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                         .OrderBy(h => h.HullId))
            {
                foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                             .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                             .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy))
                {
                    if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    {
                        float cell = core.Constants.DamageConstants[pt.DamageType][hull.DefenseType];
                        if (cell > 0f)
                            candidates.Add((hull, p, pt, pt.Power * cell));
                    }
                }
            }
            if (candidates.Count == 0)
                throw new InvalidOperationException("no gentle hull+weapon in the representative core");
            var weakest = candidates.OrderBy(c => c.perHit).First();
            return (weakest.hull, weakest.weapon, weakest.pt);
        });

    private static Ship NewShooter((DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile) fx) => new(
        shipId: 9601, hull: fx.hull,
        position: Vector3.Zero, velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static Ship NewTarget(float z, (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile) fx) => new(
        shipId: 9701, hull: fx.hull,
        position: new Vector3(0f, 0f, z), velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static void ArmWeapon(Ship ship, (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile) fx)
    {
        ship.StateM |= ShipControlState.AllWeapons;
        ship.MountWeapon(fx.weapon);
    }

    private static int HitEvents(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("ProjectileHitEvent"));

    private static void Step(Mission m, double[] tRef, Action feed)
    {
        tRef[0] += 0.045;                       // < 0.25 ⇒ n == 1 chunk per Update
        m.Update(tRef[0]);
        feed();
    }

    /// <summary>
    /// A projectile that connects with a target raises ProjectileHitEvent — the
    /// spark trigger. Two side-less ships nose to nose; the shooter (a GENTLE
    /// weapon, so the target survives a long barrage) pours rounds into the target
    /// (energy/ammo re-fed so it never starves) and the target's hull drops while
    /// the site records the impact event for every landed hit — proving the event
    /// is the direct result of real weapon damage, and (via the strictly-growing
    /// count between two windows) that it fires <em>per hit</em>, not once.
    /// </summary>
    [Fact]
    public void Shooting_fires_projectile_hit_event_per_landed_hit()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var fx = Gentle.Value;
        var shooter = NewShooter(fx);
        var (_, _, pt) = fx;
        float travel = pt.Speed * pt.Lifespan;
        float gap = MathF.Max(25f, 0.4f * travel);
        var target = NewTarget(z: -gap, fx);
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmWeapon(shooter, fx);

        float targetMax = target.HitPoints;
        var t = new[] { mission.LastUpdate };

        // Window A: plenty of steps for many shots to cross the gap and land.
        for (int i = 0; i < 120 && !target.Dead; i++)
            Step(mission, t, Feed(shooter));
        int hitsA = HitEvents(recording);

        // The target genuinely took hull damage — so hits really landed.
        Assert.True(target.HitPoints < targetMax, "landed shots must reduce the target's hull");
        Assert.True(hitsA >= 3, $"a sustained barrage must raise ProjectileHitEvent per landed hit (got {hitsA})");

        // Window B: keep shooting (target survives the gentle barrage); the count
        // must keep GROWING per further hit (edge-triggered every hit, not once).
        for (int i = 0; i < 120 && !target.Dead; i++)
            Step(mission, t, Feed(shooter));
        int hitsB = HitEvents(recording);
        Assert.True(hitsB > hitsA, "continuing fire must raise more ProjectileHitEvents, one per landed hit");

        // The event names the struck target's id and the sector it happened in.
        Assert.Contains(recording.Calls, c => c.StartsWith("ProjectileHitEvent") && c.Contains("9701"));
        Assert.Contains(recording.Calls, c => c.StartsWith("ProjectileHitEvent") && c.Contains($", {sector.Id})"));
    }

    /// <summary>
    /// A friendly-fire-suppressed round (same side, damaging power) lands NO
    /// damage and therefore raises NO ProjectileHitEvent — the gate is the real
    /// combat resolution, not a blind per-collision trigger. This is the
    /// discriminating inverse: it proves the event only follows a genuine
    /// landed hit that dealt damage.
    /// </summary>
    [Fact]
    public void Friendly_fire_suppressed_round_fires_no_projectile_hit_event()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var fx = Fight.Value;
        var shooter = NewShooter(fx);
        var (_, _, pt) = fx;
        float travel = pt.Speed * pt.Lifespan;
        float gap = MathF.Max(25f, 0.4f * travel);
        var target = NewTarget(z: -gap, fx);
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmWeapon(shooter, fx);

        // Same side ⇒ the friendly-fire gate (shipIGC.cpp:1222-1228) suppresses
        // the round: it passes through without damage and without terminating.
        shooter.SideId = 0;
        target.SideId = 0;

        float targetMax = target.HitPoints;
        var t = new[] { mission.LastUpdate };
        for (int i = 0; i < 40 && !target.Dead; i++)
            Step(mission, t, Feed(shooter));

        Assert.Equal(targetMax, target.HitPoints);   // friendly round dealt no damage
        Assert.Equal(0, HitEvents(recording));       // …and raised no impact spark
    }

    private static Action Feed(Ship shooter) => () =>
    {
        shooter.Energy = shooter.HullType.MaxEnergy;
        shooter.Ammo = Math.Max(shooter.Ammo, (short)1);
    };
}
