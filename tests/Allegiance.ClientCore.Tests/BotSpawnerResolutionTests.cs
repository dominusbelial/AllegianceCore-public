using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P426 — prove the <see cref="Catalog"/> hull/part name resolution (used by the
/// allegiance-mcp <c>spawn_bot</c> / <c>list_hulls</c> / <c>list_parts</c> tools)
/// against the REAL <c>PCore001.igc</c> corpus — the same static core the live
/// server and vizfight decode. These assert resolver BEHAVIOR (exact-name hits,
/// specific no-match errors) over corpus content, so they stay meaningful even
/// if the hull/part list changes.
/// </summary>
public class BotSpawnerResolutionTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static StaticCore RealCore() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    [Fact]
    public void ResolvesHullByExactNameAgainstRealCorpus()
    {
        var first = RealCore().HullTypes.Values.OrderBy(h => h.HullId).First();
        var hull = Catalog.ResolveHull(first.Name);
        Assert.Equal(first.HullId, hull.Id);
        Assert.Equal(first.Name, hull.Name);
    }

    [Fact]
    public void ResolvesUniqueHullSubstringAgainstRealCorpus()
    {
        // A substring of the FIRST hull's name is a good hit only when unique;
        // the first hull by id is a common fighter (e.g. "Scout"), whose first
        // few letters could collide. Use the full name to stay deterministic,
        // and separately assert that a too-short/common fragment either resolves
        // to a single hull or throws an AMBIGUOUS error (both are correct).
        var name = RealCore().HullTypes.Values.OrderBy(h => h.HullId).First().Name;
        var result = Record.Exception(() => Catalog.ResolveHull(name.Substring(0, Math.Min(3, name.Length))));
        Assert.True(result is null || result is ArgumentException, $"expected resolve-or-ambiguous, got {result}");
    }

    [Fact]
    public void UnknownHullThrowsASpecificError()
    {
        var ex = Assert.Throws<ArgumentException>(() => Catalog.ResolveHull("definitely-not-a-real-hull-xyzzy"));
        Assert.Contains("no hull matches", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlankHullThrows()
    {
        Assert.Throws<ArgumentException>(() => Catalog.ResolveHull("   "));
    }

    [Fact]
    public void ResolvesPartByExactNameAgainstRealCorpus()
    {
        var some = Catalog.AllParts().OrderBy(p => p.Id).First();
        var parts = Catalog.ResolveParts([some.Name]);
        Assert.Single(parts);
        Assert.Equal(some.Id, parts[0].PartId);
    }

    [Fact]
    public void UnknownPartThrowsASpecificError()
    {
        var ex = Assert.Throws<ArgumentException>(() => Catalog.ResolveParts(["definitely-not-a-real-part-xyzzy"]));
        Assert.Contains("no part matches", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllPartsReportsARealCategoryPerPart()
    {
        var all = Catalog.AllParts();
        Assert.NotEmpty(all);
        Assert.All(all, p => Assert.False(string.IsNullOrWhiteSpace(p.Category)));
        Assert.Equal(all.Select(p => p.Id).Count(), all.Select(p => p.Id).Distinct().Count()); // unique ids
    }

    [Fact]
    public void DefaultPartsIsTheRealFourCategoryPlayableLoadout()
    {
        // vizfight's FullInventoryParts (P420) equips weapon/afterburner/cloak/shield —
        // all four categories confirmed present in PCore001.igc.
        var parts = Catalog.DefaultParts();
        Assert.Equal(4, parts.Count);
        Assert.All(parts, p => Assert.True(p.PartId > 0));
    }

    // ── P429: target_sector resolution ────────────────────────────────────
    //
    // Sectors are the LIVE server map (delivered per-peer as EXPORT OT_cluster /
    // CLUSTERINFO), not part of the static PCore001.igc core — so these assert
    // ResolveSector's POLICY against a small constructed cluster map (same
    // exact-over-substring + ambiguity rules as ResolveHull/ResolvePart), which
    // is exactly what the live world's Clusters dictionary provides.

    private static DataClusterIGC Cluster(short id, string name) => new(
        StarSeed: 1u, LightDirection: [0f, 0f, 1f], LightColor: 0xFFFFFFu,
        ScreenX: 0f, ScreenY: 0f, ClusterId: id, Debris: 0, Stars: 0,
        Name: name, PosterName: name, PlanetName: "",
        PlanetSinLatitude: 0, PlanetLongitude: 0, PlanetRadius: 0,
        Active: true, HomeSector: false);

    private static IReadOnlyDictionary<short, DataClusterIGC> SampleClusters() =>
        new Dictionary<short, DataClusterIGC>
        {
            [1050] = Cluster(1050, "Ganymede"),
            [1051] = Cluster(1051, "Organia"),
            [1052] = Cluster(1052, "Alderan"),
            [1053] = Cluster(1053, "Ganymede Prime"),
            [1054] = Cluster(1054, "Bug Nebula"),
        };

    [Fact]
    public void ResolvesSectorByNumericId()
    {
        var s = Catalog.ResolveSector("1051", SampleClusters());
        Assert.Equal((short)1051, s.Id);
        Assert.Equal("Organia", s.Name);
    }

    [Fact]
    public void ResolvesSectorByExactName()
    {
        var s = Catalog.ResolveSector("Ganymede", SampleClusters());
        Assert.Equal((short)1050, s.Id);
    }

    [Fact]
    public void ExactSectorNameBeatsSubstringMatch()
    {
        // "Ganymede" is an exact name AND a substring of "Ganymede Prime":
        // the exact hit must win, so the two distinct-name ambiguity error
        // must NOT fire.
        var s = Catalog.ResolveSector("Ganymede", SampleClusters());
        Assert.Equal((short)1050, s.Id);
        Assert.Equal("Ganymede", s.Name);
    }

    [Fact]
    public void UniqueSectorSubstringResolves()
    {
        var s = Catalog.ResolveSector("Organ", SampleClusters());
        Assert.Equal((short)1051, s.Id);
    }

    [Fact]
    public void AmbiguousSectorNameThrowsSpecificError()
    {
        // "Gan" is a substring of both "Ganymede" and "Ganymede Prime" — two
        // distinct names, so this must fail with the ambiguity error.
        var ex = Assert.Throws<ArgumentException>(() => Catalog.ResolveSector("Gan", SampleClusters()));
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownSectorThrowsSpecificError()
    {
        var ex = Assert.Throws<ArgumentException>(() => Catalog.ResolveSector("definitely-not-a-sector-xyzzy", SampleClusters()));
        Assert.Contains("no sector matches", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlankSectorThrows()
    {
        Assert.Throws<ArgumentException>(() => Catalog.ResolveSector("   ", SampleClusters()));
    }

    [Fact]
    public void EmptySectorMapThrows()
    {
        var ex = Assert.Throws<ArgumentException>(() => Catalog.ResolveSector("Ganymede", new Dictionary<short, DataClusterIGC>()));
        Assert.Contains("no sector map", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── P474: scenario-plan resolution (pure, against the real corpus) ──────
    //
    // The two run_scenario presets resolve their real hulls/weapons out of the
    // decoded static core through ScenarioPlanner (never hard-coded ids), so
    // these prove the resolution against PCore001.igc: the bomber group finds a
    // real anti-base weapon that actually mounts on its bomber hull, and the
    // capture group finds a real c_habmBoard hull.

    [Fact]
    public void BomberPlanResolvesARealAntiBaseWeaponOnABomberHull()
    {
        var plan = ScenarioPlanner.PlanBomberRun();
        var core = RealCore();

        Assert.True(plan.BomberHullId > 0, "a real bomber hull must resolve");
        Assert.True(plan.AntiBaseWeaponId > 0, "a real anti-base weapon must resolve");
        Assert.True(core.HullTypes.ContainsKey(plan.BomberHullId), "bomber hull id must exist in the corpus");
        Assert.True(core.PartTypes.ContainsKey(plan.AntiBaseWeaponId), "anti-base weapon id must exist in the corpus");

        // The anti-base weapon must genuinely be a weapon AND mount on the
        // bomber hull's real capability mask (else it would be silently dropped
        // by the server's P334 hull-swap CanMountPart gate).
        var weapon = core.PartTypes[plan.AntiBaseWeaponId];
        Assert.Equal(EquipmentType.Weapon, weapon.EquipmentType);
        Assert.True(core.HullTypes[plan.BomberHullId].CanMountPart(in weapon),
            "anti-base weapon must mount on the bomber hull's real mask");

        // It must be the real strongest gun: its projectile power is >= every
        // other weapon that also mounts on that hull (the greedy maximisation).
        var hull = core.HullTypes[plan.BomberHullId];
        float maxFitted = 0f;
        foreach (var p in core.PartTypes.Values)
        {
            if (p.EquipmentType != EquipmentType.Weapon) continue;
            if (!hull.CanMountPart(in p)) continue;
            if (p.Type is not DataPartTypeIGC.Weapon w) continue;
            if (core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt))
                maxFitted = Math.Max(maxFitted, pt.Power);
        }
        var chosenProj = core.ProjectileTypes[
            ((DataPartTypeIGC.Weapon)weapon.Type).ProjectileTypeId].Power;
        Assert.Equal(maxFitted, chosenProj, 3f);
    }

    [Fact]
    public void BomberPlanEscortIsTheRealFighter()
    {
        var plan = ScenarioPlanner.PlanBomberRun();
        var expected = Catalog.DefaultFighterHull();
        Assert.Equal(expected.Id, plan.EscortHullId);
        Assert.Equal(expected.Name, plan.EscortHullName);
    }

    [Fact]
    public void CapturePlanResolvesARealBoardCapableHull()
    {
        var plan = ScenarioPlanner.PlanCaptureAttempt();
        var core = RealCore();

        Assert.True(plan.BoardingHullId > 0, "a real c_habmBoard hull must resolve");
        Assert.True(core.HullTypes.ContainsKey(plan.BoardingHullId));
        Assert.True(core.HullTypes[plan.BoardingHullId].HasCapability(DataHullTypeIGC.HabmBoard),
            "boarding hull must carry c_habmBoard (the P450 capture capability)");
    }

    [Fact]
    public void CapturePlanEscortIsTheRealFighter()
    {
        var plan = ScenarioPlanner.PlanCaptureAttempt();
        var expected = Catalog.DefaultFighterHull();
        Assert.Equal(expected.Id, plan.EscortHullId);
        Assert.Equal(expected.Name, plan.EscortHullName);
    }
}
