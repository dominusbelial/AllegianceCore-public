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

        // The event names the struck target's id and the sector it happened in,
        // with the P463 absorbed flag cleared (a gentler, unshielded hull hit).
        Assert.Contains(recording.Calls, c => c.StartsWith("ProjectileHitEvent") && c.Contains("9701"));
        Assert.Contains(recording.Calls, c => c.StartsWith("ProjectileHitEvent") && c.Contains($", {sector.Id}, False)"));
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

    // ── P463 — shield-absorbed hits carry the absorbed flag ─────────────────

    /// <summary>A hull + weapon + SHIELD combo whose single hit is fully absorbed
    /// by a fresh shield (≤ 0.5 ≪ the 0.9 useful ceiling ⇒ leakage == 0 ⇒ the
    /// server's ReceiveDamage returns <c>DamageResult.ShieldDamage</c>, not
    /// HullDamage) — the exact precondition for the absorbed-flag broadcast.
    /// Mirrors CombatShieldTests.SmallHit.</summary>
    private sealed record ShieldCombo(
        DataHullTypeIGC Hull, DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Projectile, DataPartTypeIGC ShieldPart)
    {
        public float AbsorbedFull => Projectile.Power
            * RepresentativeCore.Value.Constants.DamageConstants[Projectile.DamageType][((DataPartTypeIGC.Shield)ShieldPart.Type).DefenseType]
            / ((DataPartTypeIGC.Shield)ShieldPart.Type).MaxStrength;
    }

    private static readonly Lazy<ShieldCombo> ShieldHit = new(() =>
    {
        var core = RepresentativeCore.Value;
        var candidates = new List<ShieldCombo>();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed))
        {
            foreach (var wp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy))
            {
                if (!core.ProjectileTypes.TryGetValue(wp.ProjectileTypeId, out var pt) || pt.Power <= 0f) continue;
                if (core.Constants.DamageConstants[pt.DamageType][hull.DefenseType] <= 0f) continue;
                foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
                {
                    var st = (DataPartTypeIGC.Shield)sp.Type;
                    if (st.MaxStrength <= 0f) continue;
                    if (core.Constants.DamageConstants[pt.DamageType][st.DefenseType] <= 0f) continue;
                    var c = new ShieldCombo(hull, wp, pt, sp);
                    if (c.AbsorbedFull >= 0.2f && c.AbsorbedFull <= 0.5f) candidates.Add(c);
                }
            }
        }
        return candidates.MinBy(c => c.Weapon.EnergyPerShot)
            ?? throw new InvalidOperationException("no fully-absorbed shield combo in the representative core");
    });

    /// <summary>⭐ A round fully absorbed by the target's shield (leakage == 0 →
    /// <c>DamageResult.ShieldDamage</c>) still raises ProjectileHitEvent —
    /// the P463 shield-hit signal — carrying <c>absorbedByShield=True</c> on the
    /// recorded call, and leaves the hull untouched (proving it was truly a
    /// shield-only hit, the case native keys the myShieldHitSound / damage flash
    /// on). This is the discriminating inverse of the P364 hull-hit tests: a
    /// shield-absorbed strike is a real hit that must reach the client, but for
    /// the OTHER purpose (sound/flash), not the hull-impact spark.</summary>
    [Fact]
    public void Shield_absorbed_hit_raises_projectile_hit_event_with_absorbed_flag()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var combo = ShieldHit.Value;
        float travel = combo.Projectile.Speed * combo.Projectile.Lifespan;
        float gap = MathF.Max(25f, 0.4f * travel);
        var shooter = new Ship(9601, combo.Hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var target = new Ship(9701, combo.Hull, new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity);
        target.MountPart(combo.ShieldPart, amount: 1, mountId: 0, fractionMounted: 255);   // real shield, armed full
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmWeapon(shooter, (combo.Hull, combo.Weapon, combo.Projectile));

        float targetMax = target.HitPoints;
        float startShield = target.ShieldFraction;
        var t = new[] { mission.LastUpdate };
        bool absorbedLand = false;
        for (int i = 0; i < 200 && !target.Dead; i++)
        {
            Step(mission, t, Feed(shooter));
            if (target.ShieldFraction < startShield - 0.001f) { absorbedLand = true; break; }
        }

        Assert.True(absorbedLand, "a shield-absorbed round must land and dent the target's shield");
        Assert.Equal(targetMax, target.HitPoints);   // fully absorbed: hull utterly untouched

        // The site recorded the hit WITH the absorbed flag (native bAbsorbedByShield).
        Assert.Contains(recording.Calls, c => c.StartsWith("ProjectileHitEvent") && c.EndsWith(", True)"));
    }

    /// <summary>A hull-or-worse hit (leakage through an absent/overcome shield)
    /// raises ProjectileHitEvent with <c>absorbedByShield=False</c> — the flag is
    /// explicitly cleared for the hull-impact spark case, so the client knows a
    /// hull spark is warranted.</summary>
    [Fact]
    public void Hull_hit_raises_projectile_hit_event_with_clear_absorbed_flag()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var fx = Gentle.Value;
        var shooter = NewShooter(fx);
        var (_, _, pt) = fx;
        float travel = pt.Speed * pt.Lifespan;
        float gap = MathF.Max(25f, 0.4f * travel);
        var target = NewTarget(z: -gap, fx);   // no shield mounted → every hit leaks to hull
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmWeapon(shooter, fx);

        float targetMax = target.HitPoints;
        var t = new[] { mission.LastUpdate };
        for (int i = 0; i < 120 && !target.Dead; i++)
            Step(mission, t, Feed(shooter));

        Assert.True(target.HitPoints < targetMax, "the hull must have taken damage");
        Assert.Contains(recording.Calls, c => c.StartsWith("ProjectileHitEvent") && c.EndsWith(", False)"));
    }
}
