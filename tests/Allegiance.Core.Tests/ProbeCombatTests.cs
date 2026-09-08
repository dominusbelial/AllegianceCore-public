using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P100 — pure-domain (no sockets) proofs of the probe combat-destructibility
/// math and integration: <see cref="Probe.ReceiveDamage"/> implements the real
/// <c>CprobeIGC::ReceiveDamage</c> formula (<c>src/Igc/probeigc.h:65-146</c>),
/// and a blast-capable round that directly hits a probe still creates a real
/// <see cref="Explosion"/> at the impact position (the P97
/// blast-on-impact trigger reached through the new probe path) whose blast
/// splashes nearby <em>ships</em> exactly as <see cref="BlastOnImpactTests"/>
/// proves for a ship target — while a probe itself takes only the direct hit
/// (extending <see cref="Sector.ResolveExplosion"/> to also damage nearby
/// probes is explicitly out of scope, named as the next follow-up).
/// Everything is data-derived from the real <c>PCore001.igc</c> static core —
/// no hardcoded numbers.
/// </summary>
/// <remarks>
/// The ReceiveDamage maths test mirrors the ship-death discipline: partial
/// damage reduces <see cref="Probe.Fraction"/> by the exact decoded
/// <c>Power·DamageConstant/maxHP</c> fraction and returns
/// <see cref="DamageResult.HullDamage"/>; lethal damage clamps to <c>0</c>,
/// removes the probe from its sector, raises
/// <see cref="IIgcSite.ProbeDestroyedEvent"/> exactly once and returns
/// <see cref="DamageResult.Killed"/> — and a second call past death does
/// <b>not</b> double-raise (native returns the default <c>c_drHullDamage</c>
/// when <c>oldFraction &le; 0</c>, <c>probeigc.h:91,121</c>). The launcher is a
/// real <see cref="Ship"/> so the probe derives the <c>Mission</c>/<c>Core</c>
/// (and thus the damage matrix) the same way production does.
/// </remarks>
public sealed class ProbeCombatTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static StaticCore Core()
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

    /// <summary>A probe type with real hit points and a comfortable lifespan
    /// (won't expire mid-test), ordered by ascending HitPoints.</summary>
    private static IEnumerable<DataProbeTypeIGC> UsableProbes(StaticCore core)
        => core.ProbeTypes.Values
            .Where(pt => pt.HitPoints > 0f && pt.Lifespan >= 8f)
            .OrderBy(pt => pt.HitPoints);

    /// <summary>A real damage type id (from a real ordnance record) whose armor
    /// cell for <paramref name="def"/> is strictly positive — so the computed
    /// damage uses a real matrix value, never a degenerate neutral fallback.</summary>
    private static byte PositiveCellDamageType(StaticCore core, byte def)
    {
        foreach (var pt in core.ProjectileTypes.Values)
        {
            if (pt.Power <= 0f) continue;
            if (Dtm(core, pt.DamageType, def) > 0f) return pt.DamageType;
        }
        throw new InvalidOperationException($"no projectile damage type with a positive cell for defense {def}");
    }

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship NewShooter(DataHullTypeIGC hull) =>
        new(9801, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);

    /// <summary>A site that counts probe-destroy broadcasts so the death maths
    /// test asserts <see cref="IIgcSite.ProbeDestroyedEvent"/> is raised exactly
    /// once. Everything else falls through to the interface default (no-op).</summary>
    private sealed class CountingSite : IIgcSite
    {
        public int ProbeDestroys { get; private set; }
        public void ProbeDestroyedEvent(Probe p) => ProbeDestroys++;
    }

    // ══ domain: ReceiveDamage math ══════════════════════════════════════════

    /// <summary>⭐ A real probe on a real mission takes a partial hit worth a
    /// computable fraction, returns <see cref="DamageResult.HullDamage"/> and
    /// stays in its sector; a lethal hit then clamps its <see cref="Probe.Fraction"/>
    /// to 0, removes it from the sector, raises
    /// <see cref="IIgcSite.ProbeDestroyedEvent"/> exactly once and returns
    /// <see cref="DamageResult.Killed"/> — and a THIRD call past death does NOT
    /// double-raise (native returns the default HullDamage when already dead,
    /// <c>probeigc.h:91,121</c>). All numbers decoded, not guessed.</summary>
    [Fact]
    public void ReceiveDamage_partial_reduces_fraction_lethal_destroys_exactly_once()
    {
        var core = Core();
        var site = new CountingSite();
        var mission = NewStartedMission(site);
        var sector = mission.Sectors[0];

        var pType = UsableProbes(core).FirstOrDefault();
        Assert.True(pType.ExpendableTypeId != 0 || core.ProbeTypes.Count == 0, "need a probe type with real HP and lifespan");
        byte dtype = PositiveCellDamageType(core, pType.DefenseType);
        float dtm = Dtm(core, dtype, pType.DefenseType);
        Assert.True(dtm > 0f, "selected damage type must have a positive armor cell for the probe");

        var launcher = NewShooter(core.HullTypes.Values.OrderBy(h => h.HitPoints).First());
        launcher.SideId = 0;
        mission.PlaceShip(launcher, sector);                    // gives it Mission (DamageConstant's route) and a Sector
        var probe = launcher.DeployProbe(pType, mission.LastUpdate);
        Assert.Contains(probe, sector.Models);
        Assert.Equal(1.0f, probe.Fraction, 3);
        Assert.Equal(0, site.ProbeDestroys);

        // PARTIAL: a hit worth exactly 40% of maxHP → fraction 1.0 − 0.4 = 0.6.
        float partialAmount = 0.4f * pType.HitPoints / dtm;
        var res = probe.ReceiveDamage(dtype, partialAmount, mission.LastUpdate, Vector3.Zero, probe.Position, launcher);
        Assert.Equal(DamageResult.HullDamage, res);
        Assert.Equal(0.6f, probe.Fraction, 3);
        Assert.False(probe.Dead);
        Assert.Contains(probe, sector.Models);                 // survived: not destroyed
        Assert.Equal(0, site.ProbeDestroys);                   // and certainly no destroy broadcast

        // LETHAL: a hit worth 200% of maxHP clamps to 0, destroys exactly once.
        float lethalAmount = 2f * pType.HitPoints / dtm;
        var resKill = probe.ReceiveDamage(dtype, lethalAmount, mission.LastUpdate, Vector3.Zero, probe.Position, launcher);
        Assert.Equal(DamageResult.Killed, resKill);
        Assert.Equal(0f, probe.Fraction, 3);
        Assert.True(probe.Dead);
        Assert.DoesNotContain(probe, sector.Models);           // removed from sector (death contract)
        Assert.Equal(1, site.ProbeDestroys);                   // exactly once

        // POST-DEATH call: no double-raise (native probeigc.h:91,121: oldFraction
        // ≤ 0 ⇒ dr stays c_drHullDamage), same discipline as the ship path's Dead check.
        var resAgain = probe.ReceiveDamage(dtype, lethalAmount, mission.LastUpdate, Vector3.Zero, probe.Position, launcher);
        Assert.Equal(DamageResult.HullDamage, resAgain);
        Assert.Equal(0f, probe.Fraction, 3);
        Assert.Equal(1, site.ProbeDestroys);                   // STILL exactly one
        Console.WriteLine($"P100-DOMAIN: partial hit {partialAmount:F2} @ dtm {dtm:F3} → fraction 0.600; lethal → Killed, removed, " +
                          $"{site.ProbeDestroys} destroy event (once, no double-raise)");
    }

    // ══ domain: blast-on-impact reached THROUGH the probe target ═════════════

    /// <summary>A (weapon, projectile, probe, splash-hull) quadruple where a
    /// single direct blast-capable round deals the probe a clean, survivable
    /// fraction (<c>Power·dtmProbe/maxHP ∈ [0.03, 0.6]</c>) — so the probe
    /// survives the direct hit and stays in its sector — while a nearby
    /// cross-side ship is comfortably inside the <c>BlastRadius</c> for the
    /// splash. Selected from decoded data, mirroring
    /// <see cref="BlastOnImpactTests"/>.BigGun/​DoubleHit selection.</summary>
    private static readonly Lazy<(DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Proj, DataProbeTypeIGC Probe, DataHullTypeIGC Hull, float DirectDelta)> Blast = new(() =>
    {
        var core = Core();
        foreach (var wp in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                     .Where(w => w.Dispersion == 0f && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var _)))
        {
            var pt = core.ProjectileTypes[wp.ProjectileTypeId];
            if (pt.BlastPower == 0f || pt.Power == 0f) continue;
            float rad = pt.BlastRadius;
            if (pt.Radius >= rad) continue;                    // blast must reach the probe's surface at all
            if (rad < 150f) continue;                          // big enough that the test's lateral splash ship (≈80 m) sits well inside with margin
            foreach (var probe in UsableProbes(core))
            {
                float cell = Dtm(core, pt.DamageType, probe.DefenseType);
                if (cell <= 0f) continue;
                float direct = pt.Power * cell;
                if (direct >= probe.HitPoints) continue;       // probe must survive the direct hit
                float pDelta = direct / probe.HitPoints;
                if (pDelta < 0.042f) continue;                  // clearly measurable (upper bound unnecessary — the test asserts the exact decoded value either way)
                foreach (var hull in core.HullTypes.Values
                             .Where(h => h.MaxEnergy >= wp.EnergyPerShot && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                             .OrderBy(h => h.HitPoints))
                {
                    float hCell = Dtm(core, pt.DamageType, hull.DefenseType);
                    if (hCell <= 0f) continue;
                    if (pt.BlastPower * hCell >= hull.HitPoints) continue;   // splash ship must survive full blast
                    return (wp, pt, probe, hull, pDelta);
                }
            }
        }
        throw new InvalidOperationException("no blast-through-probe combo in representative core");
    });

    /// <summary>⭐ A blast-capable round whose DIRECT target is a probe still
    /// creates a real <see cref="Explosion"/> at the impact position (the P97
    /// trigger is unconditional, projectileIGC.cpp:206), exactly as when it hits
    /// a ship — proven by the splash: a nearby cross-side ship takes the
    /// squared-falloff blast damage, while the probe takes ONLY its direct hit
    /// (<c>ResolveExplosion</c> targets Ship+Mine, not probes — out of scope,
    /// the named next follow-up) and the launcher takes nothing.</summary>
    [Fact]
    public void Blast_capable_round_directly_hitting_a_probe_creates_a_real_explosion_splashing_nearby_ships()
    {
        var (weapon, pt, probeType, hull, directDelta) = Blast.Value;
        var core = Core();
        var mission = NewStartedMission(new NullIgcSite());
        var sector = mission.Sectors[0];

        float gap = MathF.Max(120f, 0.5f * pt.Speed * pt.Lifespan);      // a clean stop inside reach
        Assert.True(gap < pt.Speed * pt.Lifespan, "probe must be inside the round's reach");

        var shooter = NewShooter(hull); shooter.SideId = 0;
        mission.PlaceShip(shooter, sector);
        var probe = shooter.DeployProbe(probeType, mission.LastUpdate);   // probe at the shooter's origin, side 0
        Assert.Equal(shooter.SideId, probe.SideId);
        // Park the probe ahead of the shooter on the beam, so the round fires
        // straight into it (DeployProbe placed it at the shooter's origin, which
        // is on the beam already; we slide it out to the impact gap so the blast
        // centre is unambiguous and well clear of the shooter).
        probe.Position = new Vector3(0f, 0f, -gap);

        // Cross-side splash ship, off the beam (lateral +y) inside BlastRadius
        // of the impact point: it must take ONLY the blast splash, no direct hit.
        const float lat = 80f;
        var splash = new Ship(9802, hull, new Vector3(0f, lat, -gap), Vector3.Zero, Orientation.Identity);
        splash.SideId = 1;                                                // cross-side to the launcher, so the FF gate lets the splash through
        mission.PlaceShip(splash, sector);

        // Arm the shooter (dispersion-0 weapon, exactly one round).
        shooter.StateM |= ShipControlState.AllWeapons;
        if (weapon.CAmmoPerShot > 0) shooter.Ammo = weapon.CAmmoPerShot;
        else shooter.Energy = weapon.EnergyPerShot;
        shooter.MountWeapon(weapon);

        float pFrac0 = probe.Fraction, sFrac0 = splash.Fraction, shoot0 = shooter.Fraction;
        Assert.Equal(1f, pFrac0, 4);
        Assert.Equal(1f, sFrac0, 4);

        // Blast centre = the impact surface point on the probe: the round
        // approaches along −z, so its centre stops when its front touches the
        // probe's +z face → centre = probe.pos + (0,0, probeRadius + projRadius).
        var centre = new Vector3(0f, 0f, -gap + probeType.Radius + pt.Radius);
        float shipRad = 0.5f * hull.Length;
        float d = (centre - splash.Position).Length() - shipRad;         // surface distance
        Assert.True(d > 0f && d < pt.BlastRadius, $"splash ship must be inside the blast with real falloff (d={d:F1})");
        float f = 1f - d / pt.BlastRadius;
        float splashDrop = (pt.BlastPower * f * f) * Dtm(core, pt.DamageType, hull.DefenseType) / hull.HitPoints;

        bool landed = false;
        double t = mission.LastUpdate;
        for (int i = 0; i < 3000 && !landed; i++)
        {
            t += 0.021;
            mission.Update(t);
            landed = probe.Fraction < 1f - 0.006f;                        // round arrived at the probe
        }
        Assert.True(landed, $"the round must hit the probe (probe fraction still {probe.Fraction:F4})");

        // The probe took ONLY its direct hit, and survived (stays in the sector).
        Assert.Equal(1f - directDelta, probe.Fraction, 3);
        Assert.Contains(probe, sector.Models);
        // The blast (real Explosion at the impact position) splashed the nearby
        // cross-side ship with the exact falloff — proving the P97 mechanism ran
        // through the probe path, not silently skipped.
        Assert.Equal(1f - splashDrop, splash.Fraction, 3);
        // The launcher is excluded from its own blast.
        Assert.Equal(1f, shooter.Fraction, 3);
        Console.WriteLine($"P100-BLAST: probe {pFrac0:F2}→{probe.Fraction:F4} (direct-only {1f - directDelta:F4}); splash ship " +
                          $"{sFrac0:F2}→{splash.Fraction:F4} = 1 − {splashDrop:F4} (d={d:F1}); shooter {shoot0:F1}");
    }
}
