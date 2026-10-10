using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P97 — blast-on-impact and blast-on-expiry for <see cref="Projectile"/> /
/// <see cref="Missile"/> via the newly exposed <see cref="IOrdnance.BlastPower"/>
/// / <see cref="IOrdnance.BlastRadius"/> (both decoded from the type records,
/// <c>DataProjectileTypeIGC.cs:56-60</c>, <c>DataMissileTypeIGC.cs:504-508</c>).
/// An area-effect round behaves exactly as native does:
/// <list type="bullet">
/// <item><b>On impact</b> (<c>projectileIGC.cpp:206-219</c>, "Area effect
/// weapon: create an explosion (no matter what)"): after the normal direct
/// single-target hit, the round ALSO creates a real <see cref="Explosion"/> at
/// the impact position with the type's own <c>BlastPower/BlastRadius/DamageType</c>,
/// so the directly-hit target takes the direct <c>Power</c> <em>and</em> its own
/// blast's falloff-adjusted <c>BlastPower</c> — two real, distinct, additive
/// damage components. A nearby ship takes only the second.</item>
/// <item><b>On expiry</b> (<c>projectileIGC.cpp:128-139</c>; missiles via
/// <c>Explode(GetPosition())</c>, <c>missileigc.cpp:356-360,516-532</c>): a round
/// that times out without hitting anything also creates a real explosion at its
/// expiry position.</item>
/// </list>
/// Every claimed number is computed from the decoded <c>PCore001.igc</c> type
/// records and the <c>DamageConstants</c> matrix — nothing hardcoded. (The
/// corpus has no blast-capable <em>missile</em> type — every
/// <c>DataMissileTypeIGC.BlastPower</c> is 0 — so these proofs exercise the
/// projectile path, which shares the impact resolver with missiles through
/// <see cref="IOrdnance"/>.)
/// </remarks>
public sealed class BlastOnImpactTests
{
    private static StaticCore RepresentativeCore()
    {
        var path = Corpus.Files.FirstOrDefault(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private static Mission NewStartedMission()
    {
        var core = RepresentativeCore();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship NewShooter(DataHullTypeIGC hull) =>
        new(9701, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);

    private static Ship NewTarget(DataHullTypeIGC hull, Vector3 pos) =>
        new(9702, hull, pos, Vector3.Zero, Orientation.Identity);

    /// <summary>A single-chunk tick (n == 1 chunk) loop.</summary>
    private static bool TickUntil(Mission m, int maxSteps, Func<bool> predicate)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < maxSteps; i++)
        {
            t += 0.041;
            m.Update(t);
            if (predicate()) return true;
        }
        return false;
    }

    /// <summary>Arm <paramref name="shooter"/> with a dispersion-0 weapon and
    /// cap the burst to exactly one projectile by giving it just enough of the
    /// limiting resource (one magazine for an ammo weapon, exactly the shot cost
    /// of energy for an energy weapon) — the standard recipe. Returns the
    /// mounted <see cref="Weapon"/>.</summary>
    private static Weapon ArmShooter(Ship shooter, DataPartTypeIGC.Weapon type)
    {
        shooter.StateM |= ShipControlState.AllWeapons;
        if (type.CAmmoPerShot > 0) shooter.Ammo = type.CAmmoPerShot;   // exactly one magazine → one round
        else shooter.Energy = type.EnergyPerShot;
        return shooter.MountWeapon(type);
    }

    // ── combo selection (data-derived, all from the real core) ─────────────

    private static bool Fightable(DataHullTypeIGC h) => h.MaxEnergy > 0 && h.MaxAmmo > 0
        && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed;

    /// <summary>A blast-capable projectile (dispersion-0 weapon firing a real
    /// <c>BlastPower != 0</c>, <c>Power &gt; 0</c> projectile) on a hull whose
    /// <b>direct + full blast</b> double damage is survivable and comfortably
    /// measurable (total fraction drop in [0.12, 0.6]). Chosen to minimize that
    /// total drop (gentlest stable survivor). The expected per-hit components
    /// (direct, blast, total) are returned pre-computed.</summary>
    private static readonly Lazy<(DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Pt, DataHullTypeIGC Hull, float DirectFrac, float BlastFracFull, float TotalFrac)> DoubleHit = new(() =>
    {
        var core = RepresentativeCore();
        float bestFrac = 2f;
        (DataPartTypeIGC.Weapon, DataProjectileTypeIGC, DataHullTypeIGC, float, float, float)? best = null;
        foreach (var wp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                     .Where(w => w.Dispersion == 0f && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var _)))
        {
            var pt = core.ProjectileTypes[wp.ProjectileTypeId];
            if (pt.BlastPower == 0f || pt.Power == 0f) continue;
            float rad = pt.BlastRadius;
            float dPri = pt.Radius;                              // impact-surface offset (see test math)
            if (dPri >= rad) continue;                           // blast must reach the direct target at all
            float fPri = 1f - dPri / rad;
            float priBlast = pt.BlastPower * fPri * fPri;        // direct target's own-blast amount (falloff)
            foreach (var hull in core.HullTypes.Values.Where(Fightable).OrderBy(h => h.HitPoints))
            {
                if (wp.EnergyPerShot > hull.MaxEnergy) continue;
                float cell = Dtm(core, pt.DamageType, hull.DefenseType);
                if (cell <= 0f) continue;
                float total = (pt.Power + priBlast) * cell;
                if (total >= hull.HitPoints) continue;            // double damage must NOT kill the target
                float frac = total / hull.HitPoints;
                if (frac < 0.12f || frac > 0.60f) continue;
                if (frac < bestFrac)
                {
                    bestFrac = frac;
                    best = (wp, pt, hull, pt.Power * cell / hull.HitPoints, priBlast * cell / hull.HitPoints, frac);
                }
                break;                                            // first surviving hull for this weapon
            }
        }
        if (best is not { } b) throw new InvalidOperationException("no blast double-hit combo in representative core");
        return b;
    });

    /// <summary>The big-area gun (blast radius 250) — real type id 71, decoded
    /// from the core — on a hull that can mount it (energy ≥ 300) and survives
    /// the direct+blast double damage. Used for the near-splash and expiry tests
    /// where a large blast radius gives clean, separable geometry.</summary>
    private static readonly Lazy<(DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Pt, DataHullTypeIGC Hull)> BigGun = new(() =>
    {
        var core = RepresentativeCore();
        var pt = core.ProjectileTypes[71];
        foreach (var wp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                     .Where(w => w.ProjectileTypeId == 71 && w.Dispersion == 0f))
        {
            foreach (var hull in core.HullTypes.Values.Where(Fightable).OrderBy(h => h.HitPoints))
            {
                if (wp.EnergyPerShot > hull.MaxEnergy) continue;
                float cell = Dtm(core, pt.DamageType, hull.DefenseType);
                if (cell <= 0f) continue;
                if ((pt.Power + pt.BlastPower) * cell < hull.HitPoints)
                    return (wp, pt, hull);
            }
        }
        throw new InvalidOperationException("no big-gun (proj 71) hull combo in representative core");
    });

    // ══ IMPACT: primary takes direct + own-blast, two additive components ══

    /// <summary>⭐ The directly-hit target of a blast round takes damage TWICE —
    /// once from the direct single-target hit (its <c>Power</c>), once from its
    /// own blast's falloff-adjusted <c>BlastPower</c> (centred at the impact
    /// surface point) — two real, distinct, additive damage components. The
    /// <b>shooter</b> (launcher) takes nothing. Proven with exact decoded
    /// numbers, including the impact-surface falloff
    /// (<c>d = projectile radius</c>): the blast centre sits one projectile
    /// radius out from the target's surface (contact point), so the directly-hit
    /// ship receives <c>BlastPower·(1 − d/Radius)²</c> — not the full blast.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Impact_round_deals_direct_plus_own_blast_damage_two_additive_components()
    {
        Require.Artwork();
        var (weapon, pt, hull, directFrac, blastFrac, totalFrac) = DoubleHit.Value;
        float gap = MathF.Max(120f, 0.5f * pt.Speed * pt.Lifespan);   // a clean stop inside reach
        Assert.True(gap < pt.Speed * pt.Lifespan, "target must be inside the round's reach");
        Assert.True(gap > 0.5f * hull.Length, "target clear of both half-lengths");

        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter(hull); shooter.SideId = 0;
        var target = NewTarget(hull, new Vector3(0f, 0f, -gap)); target.SideId = 1;   // cross-side: gate lets the splash through
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmShooter(shooter, weapon);

        float tFrac0 = target.Fraction, sFrac0 = shooter.Fraction;
        Assert.Equal(1f, tFrac0, 4);
        Assert.True(TickUntil(mission, 1500, () => target.Fraction < 1f - 0.004f),
                    "the blast round must land on the cross-side target");

        // The two real additive components: direct Power + the direct target's own
        // blast (falloff at the impact surface, d = projectile radius).
        Assert.True(directFrac > 0f && blastFrac > 0f, "both damage components must be real and positive");
        Assert.Equal(1f - totalFrac, target.Fraction, 3);                 // exact total (both components)
        Assert.True(target.Fraction < 1f - directFrac - 0.004f,           // strictly below direct-only …
                    "the blast must add ON TOP of the direct hit, not replace it");
        Assert.Equal(1f, shooter.Fraction, 3);                            // launcher excluded from its own blast
        Assert.Empty(sector.Models.OfType<Projectile>());                 // spent on impact
        Console.WriteLine($"P97-IMPACT: target {tFrac0:F2}→{target.Fraction:F4} = 1 − [{directFrac:F4} direct + {blastFrac:F4} blast] = 1 − {totalFrac:F4}; shooter untouched");
    }

    // ══ NEARBY second ship takes ONLY the blast splash ══

    /// <summary>⭐ A nearby second ship (not the direct target) takes <b>only</b>
    /// the blast falloff damage — a real, distinct, smaller number than the
    /// primary's direct+blast total, with no direct-hit component (it was never
    /// on the beam). Geometry resolved exactly: the blast centre is the impact
    /// surface point on the primary (one projectile radius + one ship-half-length
    /// out along the incoming −z axis), and the second ship's surface distance
    /// yields the computed <c>f = 1 − d/Radius</c> falloff.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Nearby_second_ship_takes_only_the_blast_splash_no_direct_component()
    {
        Require.Artwork();
        var (weapon, pt, hull) = BigGun.Value;
        float shipRad = 0.5f * hull.Length;
        float projRad = pt.Radius;
        float gap = MathF.Max(300f, 0.5f * pt.Speed * pt.Lifespan);        // far enough the round settles in mid-air
        Assert.True(gap < pt.Speed * pt.Lifespan, "primary must be inside reach");
        const float dx = 100f;                                             // lateral offset of the second ship (in blast radius 250)

        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter(hull); shooter.SideId = 0;
        var primary = NewTarget(hull, new Vector3(0f, 0f, -gap)); primary.SideId = 1;
        var secondary = NewTarget(hull, new Vector3(dx, 0f, -gap)); secondary.SideId = 1;
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(primary, sector);
        mission.PlaceShip(secondary, sector);
        ArmShooter(shooter, weapon);

        // Blast centre = the impact surface point on the primary (approach −z):
        // position1 = primary + (0,0, shipRad + projRad).
        var centre = new Vector3(0f, 0f, -gap + shipRad + projRad);
        float dPri = (centre - primary.Position).Length() - shipRad;       // = projRad (>0)
        float fPri = 1f - dPri / pt.BlastRadius;
        float priBlast = pt.BlastPower * fPri * fPri;
        float cell = Dtm(RepresentativeCore(), pt.DamageType, hull.DefenseType);
        float directDrop = pt.Power * cell / hull.HitPoints;
        float priBlastDrop = priBlast * cell / hull.HitPoints;

        // Second ship: pure splash. d = |centre − secondary| − shipRad; only the blast, no direct.
        float dSec = (centre - secondary.Position).Length() - shipRad;
        Assert.True(dSec > 0f && dSec < pt.BlastRadius, $"second ship must sit inside the blast with real falloff (d={dSec:F1})");
        float fSec = 1f - dSec / pt.BlastRadius;
        float secBlastDrop = (pt.BlastPower * fSec * fSec) * cell / hull.HitPoints;

        float pFrac0 = primary.Fraction, sFrac0 = secondary.Fraction, shoot0 = shooter.Fraction;
        Assert.True(TickUntil(mission, 2000, () => primary.Fraction < 1f - 0.004f),
                    "the big round must hit the primary");

        // Primary: direct + own-blast (both components).
        Assert.Equal(1f - (directDrop + priBlastDrop), primary.Fraction, 3);
        // Secondary: ONLY the blast splash, exact falloff, strictly smaller than the primary total.
        Assert.Equal(1f - secBlastDrop, secondary.Fraction, 3);
        Assert.True(secBlastDrop < priBlastDrop, "second ship (pure splash) must take LESS than the direct target's own-blast falloff");
        Assert.True(directDrop + priBlastDrop > secBlastDrop + 0.020f, "primary total must be clearly larger than the splash");
        Assert.Equal(1f, shoot0, 3);                                        // launcher untouched by its own blast
        Console.WriteLine($"P97-NEARBY: primary {pFrac0:F2}→{primary.Fraction:F4} (direct {directDrop:F4} + own-blast {priBlastDrop:F4}); secondary {sFrac0:F2}→{secondary.Fraction:F4} = 1 − {secBlastDrop:F4} (blast-only falloff d={dSec:F1}); shooter {shoot0:F1}");
    }

    // ══ EXPIRY: a round that hits nothing still detonates at its expiry spot ══

    /// <summary>⭐ A blast-capable round fired into empty space (nothing on the
    /// beam) times out and — even though it never hit anything — still creates a
    /// real explosion at its <b>expiry position</b> (where it actually was when
    /// time ran out: origin + <c>velocity·lifespan</c>). A ship placed off-beam
    /// but within blast radius of that expiry point takes real blast falloff
    /// damage, with no direct hit ever occurring. The same trigger drives the
    /// projectile expiry path (<c>projectileIGC.cpp:128-139</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Round_that_times_out_still_detonates_at_its_expiry_position()
    {
        Require.Artwork();
        var (weapon, pt, hull) = BigGun.Value;
        float shipRad = 0.5f * hull.Length;
        float expiry = pt.Speed * pt.Lifespan;                            // (0,0,−expiry) = where it burns out
        const float vx = 50f;
        float vz = expiry - 160f;                                         // off-beam victim, ~160 before the expiry point (inside radius 250)
        var expiryPoint = new Vector3(0f, 0f, -expiry);
        var victimPos = new Vector3(vx, 0f, -vz);                         // 740 before expiry, 50 lateral ⇒ never on the beam (beam sphere r=20)

        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var shooter = NewShooter(hull); shooter.SideId = 0;
        var victim = NewTarget(hull, victimPos); victim.SideId = 1;
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(victim, sector);
        ArmShooter(shooter, weapon);

        float dVictim = (expiryPoint - victimPos).Length() - shipRad;
        Assert.True(dVictim > 0f && dVictim < pt.BlastRadius, $"victim must be within the expiry blast with real falloff (d={dVictim:F1})");
        float f = 1f - dVictim / pt.BlastRadius;
        float drop = (pt.BlastPower * f * f) * Dtm(RepresentativeCore(), pt.DamageType, hull.DefenseType) / hull.HitPoints;

        float vFrac0 = victim.Fraction;
        Assert.Equal(1f, vFrac0, 4);
        // Wait for the round to fire, fly its full lifespan and expire. The
        // projectile is observed first, then required to leave the sector — a
        // bare "no projectile remains" is true before it ever fires, so we latch
        // on having SEEN one before demanding it vanish.
        bool seen = false;
        Assert.True(TickUntil(mission, 6000, () =>
                    {
                        if (sector.Models.OfType<Projectile>().Any()) seen = true;
                        return seen && !sector.Models.OfType<Projectile>().Any();
                    }),
                    "the round must eventually burn out and leave the sector");

        Assert.True(victim.Fraction < vFrac0, "the timed-out round must blast the off-beam victim near where it expired");
        Assert.Equal(1f - drop, victim.Fraction, 3);                     // exact falloff from the expiry point
        Assert.Equal(1f, shooter.Fraction, 3);                            // launcher untouched (self-excluded)
        Console.WriteLine($"P97-EXPIRY: victim {vFrac0:F2}→{victim.Fraction:F4} = 1 − {drop:F4} (d={dVictim:F1} from expiry point); round burned out at {expiryPoint}");
    }
}
