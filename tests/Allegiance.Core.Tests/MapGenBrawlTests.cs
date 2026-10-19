namespace Allegiance.Core.Tests;

using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Xunit;
using Allegiance.TestInfra;

/// <summary>
/// P401 — structural verification of <see cref="MapGenType.Brawl"/>, the
/// final native procedural map topology (task 5 of 5, completing all 15).
/// Brawl is the structural odd one out: instead of a multi-cluster layout
/// with team/neutral clusters linked by warps, it builds a SINGLE shared
/// sector with every side's home station co-located inside it and ZERO
/// inter-cluster links (native <c>CmapMakerBrawlIGC</c>,
/// <c>mapmakerigc.cpp:1707-1798</c>):
///   • GenerateLayout sets team-clusters-per-team 0 and other-clusters 1 →
///     the whole map is that one neutral cluster (cpp 1707-1719).
///   • LinkClusters is an EMPTY no-op (cpp 1732-1734) — with one cluster
///     there is nothing to link; asserted here as exactly-zero warps (a real
///     confirmed no-op, not an accidental gap).
///   • PopulateCluster is the most substantial override (cpp 1737-1798): one
///     home station per side at angle 2π·side/teams on a 2500-radius circle,
///     3-5 random "huge" asteroids (randomInt(3,5), inclusive), then the
///     shared 3-kind population with multipliers (1, -3·(teams/2), 1) — the
///     first real use of the free function's negative (absolute-count)
///     multiplier branch.
///
/// All expected values below were derived from the native C++ (cited above),
/// not from the port re-reading its own logic.
/// </summary>
public sealed class MapGenBrawlTests
{
    private static readonly Lazy<StaticCore> GenCore = new(MapGenTestCore.LoadRepresentative);

    private static int[] CivIds(int n) =>
        GenCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId)
            .Where(c => c.InitialStationTypeId != -1
                && GenCore.Value.StationTypes.ContainsKey(c.InitialStationTypeId))
            .Take(n)
            .Select(c => (int)c.CivilizationId)
            .ToArray();

    private static MissionMap Generate(int teams, int mapSize = 0)
        => MissionMapGenerator.Generate(new MapGenParams
        {
            Type = MapGenType.Brawl,
            Teams = teams,
            CivIds = CivIds(teams),
            MapSize = mapSize,
        }, GenCore.Value, 0.0);

    /// <summary>⭐ The whole point of Brawl is that the map IS one shared
    /// sector with no warps — a real, confirmed no-op for LinkClusters
    /// (cpp:1732-1734), not an accidental gap. Exactly one neutral cluster and
    /// zero warps in the whole map.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Brawl_SingleSharedCluster_ZeroWarps()
    {
        Require.Artwork();
        var map = Generate(2);
        Assert.Single(map.Clusters);
        var cluster = map.Clusters[0];
        Assert.False(cluster.HomeSector, "the single shared sector is a neutral (other) cluster");
        Assert.Equal(0, cluster.ClusterId);
        Assert.Empty(map.Warps);
    }

    /// <summary>⭐ Brawl's PopulateCluster override places one station per
    /// side, all in the single shared cluster, evenly spaced on a
    /// 2500-radius circle at angle 2π·side/teams (cpp 1753-1759, 1771).
    /// Removing the override would leave the map with NO stations (there is no
    /// home sector to trigger GenerateStarbase) — this test goes red.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Brawl_TwoTeams_OneStationPerSide_AtCircularPositions()
    {
        Require.Artwork();
        var map = Generate(2);
        Assert.Equal(2, map.Stations.Count);

        // side 0 → angle 0 → (2500, 0, 0); side 1 → angle π → (-2500, 0, 0).
        foreach (var side in new[] { 0, 1 })
        {
            var st = map.Stations.Single(s => s.SideId == side);
            Assert.Equal((short)0, st.ClusterId);          // every side in the single shared sector
            float angle = 2.0f * MathF.PI * side / 2;
            AssertApprox(2500.0f * MathF.Cos(angle), st.Position![0]);
            AssertApprox(2500.0f * MathF.Sin(angle), st.Position![1]);
            AssertApprox(0f, st.Position![2]);
        }
    }

    /// <summary>⭐ Each Brawl station is owned by its side and uses that side's
    /// own civilization's INITIAL station type (pSide-&gt;GetCivilization()-&gt;
    /// GetInitialStationType(), cpp 1774-1778) — not a fixed type id — and is
    /// named after that type.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Brawl_StationsUseCorrectInitialStationTypeAndOwner()
    {
        Require.Artwork();
        int teams = 4;
        var map = Generate(teams);
        Assert.Equal(teams, map.Stations.Count);

        for (int side = 0; side < teams; side++)
        {
            var civ = GenCore.Value.Civilizations[(short)CivIds(teams)[side]];
            short typeId = civ.InitialStationTypeId;
            Assert.True(GenCore.Value.StationTypes.ContainsKey(typeId),
                $"side {side} civ '{civ.Name}' initial type {typeId} missing from core");

            var st = map.Stations.Single(s => s.SideId == side);
            Assert.Equal(typeId, st.StationTypeId);
            Assert.Equal((short)side, st.SideId);
            Assert.Equal(GenCore.Value.StationTypes[typeId].Name, st.Name);
            Assert.Equal(1, st.BpHull);
            Assert.Equal(1, st.BpShield);
        }
    }

    /// <summary>⭐ Brawl scatters 3-5 random "huge" neutral asteroids
    /// (randomInt(3,5) inclusive → port Random.Next(3,6)) via GetRandomType(0)
    /// = capabilities 0 (cpp 1790-1793; asteroidigc.cpp:238-240). Removing the
    /// scatter would leave zero capability-0 rocks — this test goes red.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Brawl_ThreeToFiveScatterAsteroids()
    {
        Require.Artwork();
        var map = Generate(2);
        int scatter = map.Asteroids.Count(a => a.AabmCapabilities == 0);
        Assert.InRange(scatter, 3, 5);
    }

    /// <summary>⭐ Brawl is the first topology to pass a NEGATIVE multiplier to
    /// the shared population — -3·(teams/2) for special asteroids — which the
    /// free function treats as an ABSOLUTE count (n = -multiplier,
    /// common.cpp:3042-3050) independent of the neutral-special base. With 2
    /// teams that's exactly 3 special rocks, while mineable stays 8 (neutral
    /// base × 1) and generic stays 8 (neutral base × 1, the default 9th arg).
    /// Removing the Brawl branch would collapse special to the base 2 and this
    /// test goes red.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Brawl_NegativeSpecialMultiplier_GivesAbsoluteCount()
    {
        Require.Artwork();
        var map = Generate(2);
        int mineable = map.Asteroids.Count(a => (a.AabmCapabilities & DataAsteroidIGC.AabmMineHe3) != 0);
        int special = map.Asteroids.Count(a => (a.AabmCapabilities & DataAsteroidIGC.AabmSpecial) != 0);
        int generic = map.Asteroids.Count(a => (a.AabmCapabilities & DataAsteroidIGC.AabmBuildable) != 0);

        Assert.Equal(8, mineable);    // neutral 8 × 1
        Assert.Equal(3, special);     // abs(-3·(2/2)) = 3, NOT the base 2
        Assert.Equal(8, generic);     // neutral 8 × 1 (nAsteroidsMultiplier default 1)
    }

    /// <summary>⭐ Brawl's own <c>IsValid</c> is a vestigial NULL stub — its only
    /// real check (nMaxPlayersPerTeam·nTeams &gt; 36) was commented out March
    /// 24 2006 (cpp 1695-1704) — so, like PinWheel/SplitBase, it adds no "must
    /// be N teams" constraint: a 3-team config is accepted and still yields one
    /// shared cluster with one station per side.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Brawl_HasNoExtraValidityConstraint()
    {
        Require.Artwork();
        var map = Generate(3);
        Assert.Single(map.Clusters);         // still one shared sector
        Assert.Empty(map.Warps);             // still nothing to link
        Assert.Equal(3, map.Stations.Count); // one station per side (3 teams)
        Assert.Equal(3, map.Asteroids.Count(a => (a.AabmCapabilities & DataAsteroidIGC.AabmSpecial) != 0));
        // -3·(3/2) = -3·1 = -3 (integer division) → exactly 3 special rocks.
    }

    private static void AssertApprox(float expected, float actual)
    {
        Assert.InRange(actual, expected - 1e-3f, expected + 1e-3f);
    }
}
