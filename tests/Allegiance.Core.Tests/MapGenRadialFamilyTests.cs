namespace Allegiance.Core.Tests;

using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;
using Allegiance.TestInfra;

/// <summary>
/// P399 — structural verification of the radial/spoke procedural map
/// topologies added on top of P397/P398: <see cref="MapGenType.Star"/>,
/// <see cref="MapGenType.PinWheel"/>, <see cref="MapGenType.DiamondRing"/> and
/// <see cref="MapGenType.SnowFlake"/>.
///
/// Every expected sector count, home-sector set and warp-adjacency graph below
/// was derived by tracing the native C++ (<c>mapmakerigc.cpp</c>: Star
/// 1555-1683, PinWheel 1821-1917, DiamondRing 1940-2051, SnowFlake 2061-2236)
/// and simulated independently of the port (a throwaway script re-implementing
/// the native <c>LinkClusters</c> bodies from the C++ and recording the
/// resulting edges) — so these tests assert against known-good native-derived
/// values, not against the port re-reading its own logic.
///
/// DiamondRing requires &gt;2 teams (its native <c>IsValid</c>, 1930-1937), so its
/// case uses 3 teams; the rest use 2 teams / iMapSize 0 (plus a second PinWheel
/// case exercising its multi-sector-centre layout at iMapSize != 0).
/// </summary>
public sealed class MapGenRadialFamilyTests
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

    public sealed record TopologyCase(
        MapGenType Type, int Teams, int MapSize, int Total, short[] Homes,
        Dictionary<short, short[]> Adjacency, short EdgeCount);

    public static IEnumerable<object[]> Cases()
    {
        // Star: 5 neutral sectors per team radiating from each home (cpp 1631-1683).
        yield return Case(new TopologyCase(MapGenType.Star, 2, 0, 12, [0, 6],
            new()
            {
                [0] = [1, 2], [1] = [0, 3, 10], [2] = [0, 3, 4], [3] = [1, 2, 4, 5, 10],
                [4] = [2, 3, 7, 9], [5] = [3, 11], [6] = [7, 8], [7] = [4, 6, 9],
                [8] = [6, 9, 10], [9] = [4, 7, 8, 10, 11], [10] = [1, 3, 8, 9], [11] = [5, 9],
            }, 19)); // 12 spokes; note edge 5-11 is issued twice (even teams) — native does not dedupe

        // PinWheel, single-centre mode (iMapSize 0): one neutral centre sector,
        // every team sector links straight to it (cpp 1893-1897).
        yield return Case(new TopologyCase(MapGenType.PinWheel, 2, 0, 3, [0, 1],
            new() { [0] = [2], [1] = [2], [2] = [0, 1] }, 2));

        // PinWheel, multi-centre mode (iMapSize != 0): one neutral per team in
        // the centre; each team sector links to its centre neutral and the
        // centre neutrals chain (cpp 1864-1886).
        yield return Case(new TopologyCase(MapGenType.PinWheel, 2, 1, 4, [0, 2],
            new() { [0] = [1], [1] = [0, 3], [2] = [3], [3] = [1, 2] }, 3));

        // DiamondRing: inner ring of neutrals + outer ring; each home links to
        // both of its two neutral sectors (cpp 2012-2051). Requires 3 teams.
        yield return Case(new TopologyCase(MapGenType.DiamondRing, 3, 0, 9, [0, 3, 6],
            new()
            {
                [0] = [1, 2], [1] = [0, 4, 7], [2] = [0, 5, 8], [3] = [4, 5],
                [4] = [1, 3, 7], [5] = [2, 3, 8], [6] = [7, 8], [7] = [1, 4, 6], [8] = [2, 5, 6],
            }, 12));

        // SnowFlake: 3 neutral sectors per team in a triangle; the triangles
        // chain into a ring (cpp 2138-2236).
        yield return Case(new TopologyCase(MapGenType.SnowFlake, 2, 0, 8, [0, 4],
            new()
            {
                [0] = [1, 2], [1] = [0, 2, 3, 6], [2] = [0, 1, 3, 5], [3] = [1, 2, 7],
                [4] = [5, 6], [5] = [2, 4, 6, 7], [6] = [1, 4, 5, 7], [7] = [3, 5, 6],
            }, 13));
    }

    private static object[] Case(TopologyCase c) => [c];

    private static MissionMap Generate(TopologyCase c)
    {
        var pmp = new MapGenParams
        {
            Type = c.Type,
            Teams = c.Teams,
            CivIds = CivIds(c.Teams),
            MapSize = c.MapSize,
        };
        return MissionMapGenerator.Generate(pmp, GenCore.Value, 0.0);
    }

    private static Mission AssembleMap(TopologyCase c, MissionMap map)
    {
        var specs = Enumerable.Range(0, c.Teams)
            .Select(i => new SideSpec((short)i, $"Team {i}", (short)CivIds(c.Teams)[i]))
            .ToArray();
        var m = Mission.Assemble(GenCore.Value, map, specs, new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Dictionary<short, short[]> ActualAdjacency(Mission m)
    {
        var adj = new Dictionary<short, short[]>();
        foreach (var sector in m.Sectors)
        {
            var dst = sector.Warps.Select(w => w.Destination!.Id).Distinct().OrderBy(x => x).ToArray();
            adj[sector.Id] = dst;
        }
        return adj;
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Topology_CorrectSectorCount_AndHomeSectors(TopologyCase c)
    {
        Require.Artwork();
        var map = Generate(c);
        var m = AssembleMap(c, map);

        Assert.Equal(c.Total, map.Clusters.Count);
        Assert.Equal(c.Total, m.Sectors.Count);

        var homeIds = map.Clusters.Where(x => x.HomeSector).Select(x => x.ClusterId).OrderBy(x => x).ToArray();
        Assert.Equal(c.Homes, homeIds);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Topology_Adjacency_MatchesTracedNativeGraph(TopologyCase c)
    {
        Require.Artwork();
        var m = AssembleMap(c, Generate(c));
        var actual = ActualAdjacency(m);

        var sb = new System.Text.StringBuilder();
        bool ok = true;
        foreach (var kv in c.Adjacency)
        {
            var exp = kv.Value.OrderBy(x => x).ToArray();
            var act = actual.TryGetValue(kv.Key, out var av) ? av : [];
            if (!exp.SequenceEqual(act))
            {
                ok = false;
                sb.Append($"  sector {kv.Key}: expected [{string.Join(",", exp)}] got [{string.Join(",", act)}]\n");
            }
        }
        Assert.True(ok, "adjacency mismatch vs traced native graph:\n" + sb.ToString());

        // The independent sim's edge count = half the sum of degrees (no self-loops).
        int degreeSum = c.Adjacency.Values.Sum(v => v.Length);
        Assert.Equal(c.EdgeCount, degreeSum / 2);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Topology_HomeSectorsGetStations_NeutralSectorsHaveNone(TopologyCase c)
    {
        Require.Artwork();
        var m = AssembleMap(c, Generate(c));
        var homeSet = c.Homes.ToHashSet();

        foreach (var sector in m.Sectors)
        {
            if (homeSet.Contains(sector.Id))
                Assert.Single(sector.Stations);
            else
                Assert.Empty(sector.Stations);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Topology_EverySectorHasMinableAsteroids(TopologyCase c)
    {
        Require.Artwork();
        var m = AssembleMap(c, Generate(c));
        foreach (var sector in m.Sectors)
        {
            Assert.NotEmpty(sector.Asteroids);
            Assert.Contains(sector.Mineable, a => a.CanMineHe3);
        }
    }

    /// <summary>
    /// ⭐ The PinWheel <c>PopulateCluster</c> override (mapmakerigc.cpp:1901-1917)
    /// genuinely takes effect: in single-centre mode (iMapSize 0) the non-home
    /// centre sector gets k = 2 — every asteroid count doubled — while every
    /// home sector gets k = 1. Using the default params (mine/special/generic =
    /// 10/2/10 home, 8/2/8 neutral) the centre holds 2·(8+2+8) = 36 asteroids and
    /// each home 1·(10+2+10) = 22; the centre also holds 2·8 = 16 He3 rocks vs a
    /// home's 10. If the multiplier were removed the centre would equal a
    /// neutral cluster at 22 — this test goes red (verified during development).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PinWheel_SingleCentreMode_DoublesCentreAsteroids()
    {
        Require.Artwork();
        var map = MissionMapGenerator.Generate(new MapGenParams
        {
            Type = MapGenType.PinWheel,
            Teams = 2,
            CivIds = CivIds(2),
            MapSize = 0,
        }, GenCore.Value, 0.0);

        // Single-centre PinWheel: ids 0,1 = team homes; id 2 = centre neutral.
        int Centre(short cid) => map.Asteroids.Count(a => a.ClusterId == cid);
        int He3(short cid) => map.Asteroids.Count(a => a.ClusterId == cid && (a.AabmCapabilities & DataAsteroidIGC.AabmMineHe3) != 0);

        Assert.Equal(22, Centre(0));
        Assert.Equal(22, Centre(1));
        Assert.Equal(36, Centre(2));   // 2 * (8+2+8) — the override doubles the centre

        Assert.Equal(10, He3(0));      // home, k=1: 1 * nPlayerSectorMineable(10)
        Assert.Equal(16, He3(2));      // centre, k=2: 2 * nNeutralSectorMineable(8)
    }

    /// <summary>⭐ DiamondRing's native <c>IsValid</c> is enforced: 2 teams is
    /// rejected (native requires &gt;2 teams, mapmakerigc.cpp:1930-1937); 3 teams
    /// is accepted and generates.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DiamondRing_RequiresMoreThanTwoTeams()
    {
        Require.Artwork();
        var bad = new MapGenParams
        {
            Type = MapGenType.DiamondRing, Teams = 2, CivIds = CivIds(2), MapSize = 0,
        };
        var ex = Assert.Throws<InvalidDataException>(() => MissionMapGenerator.Generate(bad, GenCore.Value, 0.0));
        Assert.Contains("more than 2 teams", ex.Message);

        var good = new MapGenParams
        {
            Type = MapGenType.DiamondRing, Teams = 3, CivIds = CivIds(3), MapSize = 0,
        };
        var map = MissionMapGenerator.Generate(good, GenCore.Value, 0.0);
        Assert.Equal(9, map.Clusters.Count);   // (1 team + 2 neutral) * 3 teams
    }

    /// <summary>⭐ PinWheel's own <c>IsValid</c> is a vestigial NULL stub — its
    /// only real check was commented out in March 2006 (mapmakerigc.cpp:1810-1818)
    /// — so unlike DiamondRing it adds no extra constraint and 2 teams is
    /// accepted normally. This pins that honest reading: PinWheel does not reject
    /// anything the base accepts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PinWheel_HasNoExtraValidityConstraint()
    {
        Require.Artwork();
        var map = MissionMapGenerator.Generate(new MapGenParams
        {
            Type = MapGenType.PinWheel, Teams = 2, CivIds = CivIds(2), MapSize = 0,
        }, GenCore.Value, 0.0);
        Assert.Equal(3, map.Clusters.Count);
    }
}
