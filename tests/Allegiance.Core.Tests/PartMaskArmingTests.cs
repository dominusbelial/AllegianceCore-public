using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Srv = global::Allegiance.Server.Server;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P32: the production <c>Server.MountCombatWeapon</c> now honours real
/// <c>PartMask</c> compatibility per fixed hardpoint — the engine's
/// <c>CanMount</c> subset test (<c>src/Igc/hullTypeIGC.h:199-213</c>,
/// <c>partType.PartMask &amp; ~hardpoint.PartMask == 0</c>) applied to weapons
/// only. Two consequences that must hold against the real decoded PCore001.igc
/// corpus: (a) different fixed hardpoints on the same hull may legitimately get
/// different weapon types; (b) a fixed hardpoint with no compatible weapon is
/// left unmounted and logged, never silently dropped or crashed on. Everything
/// here drives the real private server method via reflection (the same
/// spawn-time seam P23's test uses), exactly as
/// <c>CombatWeaponFiringTests.MountCombatWeapon_arms_every_fixed_hardpoint_and_no_turrets</c>
/// does — the task's established pattern.
/// </summary>
public class PartMaskArmingTests
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

    /// <summary>The usable weapon candidates, cheapest energy-per-shot first —
    /// the exact filter <c>MountCombatWeapon</c> uses (dispersion-free, real
    /// projectile with Power&gt;0), and the parent record so its <c>PartMask</c>
    /// is reachable.</summary>
    private static List<(DataPartTypeIGC part, DataPartTypeIGC.Weapon w)> Candidates(StaticCore core)
        => core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (part: p, w: (DataPartTypeIGC.Weapon)p.Type))
            .Where(t => t.w.Dispersion == 0f
                        && core.ProjectileTypes.TryGetValue(t.w.ProjectileTypeId, out var ptyp) && ptyp.Power > 0f)
            .OrderBy(t => t.w.EnergyPerShot)
            .ToList();

    /// <summary>The cheapest candidate whose PartMask is a subset of the given
    /// hardpoint PartMask — the real CanMount subset test.</summary>
    private static (DataPartTypeIGC part, DataPartTypeIGC.Weapon w)? CheapestCompatible(
        List<(DataPartTypeIGC part, DataPartTypeIGC.Weapon w)> cands, short hpMask)
    {
        var r = cands.FirstOrDefault(t => (t.part.PartMask & ~hpMask) == 0);
        return r.part == default ? null : r;
    }

    /// <summary>Drive the real (private) production arming on a freshly-placed
    /// ship, returning the ship and the captured server stdout.</summary>
    private static (Ship ship, string stdout) ArmedShip(Mission mission, DataHullTypeIGC hull)
    {
        var ship = new Ship(9302, hull, new Vector3(10f, -20f, 30f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, mission.Sectors[0]);

        var mountCombatWeapon = typeof(Srv).GetMethod(
                "MountCombatWeapon", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Srv), "MountCombatWeapon");

        var orig = Console.Out;
        var sw = new StringWriter();
        try
        {
            Console.SetOut(sw);
            using var server = new Srv();
            mountCombatWeapon.Invoke(server, new object[] { ship });
        }
        finally
        {
            Console.SetOut(orig);
        }
        return (ship, sw.ToString());
    }

    // ── (a) different fixed hardpoints can get different weapon types ────────

    /// <summary>
    /// Pick a real hull whose fixed hardpoints need <b>different</b> cheapest
    /// compatible weapons — i.e. at least two of its fixed mounts have mutually
    /// non-fitting cheapest-compatible types (their hardpoint masks are not
    /// uniformly comparable, so a single weapon could never serve them all).
    /// Prefers a fightable hull (energy/ammo) and an interceptor-scale pick so
    /// the divergent types are ordinary, non-free guns.
    /// </summary>
    private static DataHullTypeIGC DivergentHull()
    {
        var core = RepresentativeCore.Value;
        var cands = Candidates(core);
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0
                                 && h.Hardpoints.Count(x => x.Fixed) >= 2)
                     .OrderBy(h => h.HullId))
        {
            var fixedIdx = Enumerable.Range(0, hull.Hardpoints.Length).Where(i => hull.Hardpoints[i].Fixed).ToList();
            var picks = fixedIdx.Select(i => CheapestCompatible(cands, hull.Hardpoints[i].PartMask)).ToList();
            if (picks.Count < 2 || picks.Any(p => p is null)) continue;
            int distinct = picks.Select(p => p!.Value.part.Name).Distinct().Count();
            if (distinct < 2) continue;
            // Prefer divergence with ordinary (EPS>0) guns — the real proof a
            // hardpoint difference, not an exotic free-gun quirk, drives it.
            if (picks.All(p => p!.Value.w.EnergyPerShot > 0f)) return hull;
        }
        throw new InvalidOperationException("no hull with divergent fixed-hardpoint weapon selection in the representative core");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Different_fixed_hardpoints_mount_different_weapon_types()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var hull = DivergentHull();
        var cands = Candidates(mission.Core);

        var fixedIdx = Enumerable.Range(0, hull.Hardpoints.Length).Where(i => hull.Hardpoints[i].Fixed).ToList();
        var expected = fixedIdx.ToDictionary(i => i,
            i => CheapestCompatible(cands, hull.Hardpoints[i].PartMask)!.Value);
        int distinctTypes = expected.Values.Select(e => e.part.Name).Distinct().Count();
        Assert.True(distinctTypes >= 2, $"guard: hull '{hull.Name}' must genuinely need different weapons per hardpoint");

        var (ship, _) = ArmedShip(mission, hull);

        // One weapon per fixed hardpoint that can arm, each the cheapest
        // compatible for ITS mount, and at least two DIFFERENT types across the
        // hull — proving the per-hardpoint compatibility diverges mounts.
        Assert.Equal(fixedIdx.Count, ship.Weapons.Count);
        Assert.Equal(distinctTypes, ship.Weapons.Select(w => w.Type).Distinct().Count());
        foreach (var idx in fixedIdx)
        {
            var mounted = Assert.Single(ship.Weapons, w => w.MountId == idx);
            Assert.Equal(expected[idx].w, mounted.Type);               // exact cheapest-compatible subtype
            Assert.Equal(0, expected[idx].part.PartMask & ~hull.Hardpoints[idx].PartMask);  // CanMount holds
        }
    }

    // ── (b) a hardpoint with no compatible weapon is left unmounted + logged ──

    /// <summary>A real hull with at least one fixed hardpoint that some usable
    /// weapon fits AND at least one fixed hardpoint that no usable weapon fits —
    /// so a correct impl must arm one and leave the other empty, not blanket-arm
    /// or bail.</summary>
    private static DataHullTypeIGC MixedHull()
    {
        var core = RepresentativeCore.Value;
        var cands = Candidates(core);
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.Hardpoints.Count(x => x.Fixed) >= 2)
                     .OrderBy(h => h.HullId))
        {
            var fixedIdx = Enumerable.Range(0, hull.Hardpoints.Length).Where(i => hull.Hardpoints[i].Fixed).ToList();
            int compatible = fixedIdx.Count(i => CheapestCompatible(cands, hull.Hardpoints[i].PartMask) is not null);
            if (compatible > 0 && compatible < fixedIdx.Count) return hull;
        }
        throw new InvalidOperationException("no hull with mixed-compatible fixed hardpoints in the representative core");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hardpoint_with_no_compatible_weapon_is_left_unmounted_and_logged()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var hull = MixedHull();
        var cands = Candidates(mission.Core);
        var fixedIdx = Enumerable.Range(0, hull.Hardpoints.Length).Where(i => hull.Hardpoints[i].Fixed).ToList();
        var armable = fixedIdx.Where(i => CheapestCompatible(cands, hull.Hardpoints[i].PartMask) is not null).ToList();
        var unarmable = fixedIdx.Where(i => CheapestCompatible(cands, hull.Hardpoints[i].PartMask) is null).ToList();
        Assert.NotEmpty(armable);
        Assert.NotEmpty(unarmable);   // guard: must actually exercise the fallback

        var (ship, stdout) = ArmedShip(mission, hull);

        // Exactly the armable fixed mounts armed; the mask-less mount stayed empty.
        Assert.Equal(armable.Count, ship.Weapons.Count);
        Assert.Equal(armable.Select(i => (short)i).OrderBy(m => m), ship.Weapons.Select(w => w.MountId).OrderBy(m => m));
        Assert.All(ship.Weapons, w =>
            Assert.Equal(0, CandParent(mission.Core, w.Type).PartMask & ~hull.Hardpoints[w.MountId].PartMask));

        // Not silent: the omission is plainly logged (this repo's "log, don't
        // fabricate" style) — and nothing threw.
        Assert.Contains("no PartMask-compatible weapon in core", stdout);
        foreach (var i in unarmable)
            Assert.Contains($"fixed mount {i}", stdout);
    }

    /// <summary>Resolve a mounted weapon subtype back to its parent
    /// <c>DataPartTypeIGC</c> (whose PartMask lives there) via record-value
    /// equality, exactly as the P23 test does.</summary>
    private static DataPartTypeIGC CandParent(StaticCore core, DataPartTypeIGC.Weapon w)
        => core.PartTypes.Values.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.First())[w];
}
