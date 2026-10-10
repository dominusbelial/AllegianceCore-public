namespace Allegiance.Core.Tests;

using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;
using Allegiance.TestInfra;

/// <summary>
/// P398 — structural verification of the ring-family procedural map topologies
/// added on top of P397's SingleRing: <see cref="MapGenType.DoubleRing"/>,
/// <see cref="MapGenType.BigRing"/>, <see cref="MapGenType.HiLo"/>,
/// <see cref="MapGenType.HiHigher"/> and its zero-override subclasses
/// <see cref="MapGenType.LargeSplit"/>/<see cref="MapGenType.InsideOut"/>.
///
/// Every expected sector count, home-sector set and warp-adjacency graph below
/// was derived by tracing the native C++ (<c>mapmakerigc.cpp</c>:
/// DoubleRing 1107-1155, BigRing 1166-1242, HiLo 1252-1369, HiHigher
/// 1380-1544) and simulated independently of the port — so these tests assert
/// against known-good native-derived values, not against the port re-reading
/// its own logic.
/// </summary>
public sealed class MapGenRingFamilyTests
{
    private static readonly Lazy<StaticCore> GenCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            var ok = core.Civilizations.Values.Count(c =>
                c.InitialStationTypeId != -1 && core.StationTypes.ContainsKey(c.InitialStationTypeId));
            if (ok >= 2) return core;
        }
        throw new InvalidOperationException("no static core with 2 usable civilizations found in corpus");
    });

    private static readonly Lazy<int[]> CivPair = new(() =>
        GenCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId)
            .Where(c => c.InitialStationTypeId != -1 && GenCore.Value.StationTypes.ContainsKey(c.InitialStationTypeId))
            .Take(2)
            .Select(c => (int)c.CivilizationId)
            .ToArray());

    // Expected ground truth for 2 teams, iMapSize 0 (except LargeSplit/InsideOut
    // which pin their own team-per-team regardless of iMapSize). Adjacency maps
    // cluster-id -> set of neighbour cluster-ids (the warp-partner sectors).
    public sealed record TopologyCase(
        MapGenType Type, int Total, short[] Homes,
        Dictionary<short, short[]> Adjacency, short EdgeCount);

    public static IEnumerable<object[]> Cases()
    {
        yield return Case(new TopologyCase(MapGenType.DoubleRing, 4, [0, 2],
            new()
            {
                [0] = [1, 2, 3], [1] = [0, 2, 3], [2] = [0, 1, 3], [3] = [0, 1, 2],
            }, 6)); // degenerate 2-team double ring collapses to K4 — the real native output

        yield return Case(new TopologyCase(MapGenType.BigRing, 6, [0, 3],
            new()
            {
                [0] = [1, 5], [1] = [0, 2], [2] = [1, 3], [3] = [2, 4], [4] = [3, 5], [5] = [0, 4],
            }, 6)); // 2 neutrals between each pair of homes

        yield return Case(new TopologyCase(MapGenType.HiLo, 9, [0, 4],
            new()
            {
                [0] = [1, 2, 5, 7], [1] = [0, 4, 8], [2] = [0, 3], [3] = [2, 4],
                [4] = [1, 3, 5, 6], [5] = [0, 4, 8], [6] = [4, 7], [7] = [0, 6], [8] = [1, 5],
            }, 12)); // 3 neutrals/team + central 8

        yield return Case(new TopologyCase(MapGenType.HiHigher, 13, [0, 6],
            new()
            {
                [0] = [1, 4, 9, 11], [1] = [0, 2, 4], [2] = [1, 3, 12], [3] = [2, 5, 6],
                [4] = [0, 1, 5], [5] = [3, 4, 6], [6] = [3, 5, 7, 10], [7] = [6, 8, 10],
                [8] = [7, 9, 12], [9] = [0, 8, 11], [10] = [6, 7, 11], [11] = [0, 9, 10], [12] = [2, 8],
            }, 20)); // 5 neutrals/team + central 12

        yield return Case(new TopologyCase(MapGenType.LargeSplit, 15, [0, 1, 7, 8],
            new()
            {
                [0] = [2, 11], [1] = [5, 13], [2] = [0, 3, 5], [3] = [2, 4, 14], [4] = [3, 6, 7],
                [5] = [1, 2, 6], [6] = [4, 5, 8], [7] = [4, 9], [8] = [6, 12], [9] = [7, 10, 12],
                [10] = [9, 11, 14], [11] = [0, 10, 13], [12] = [8, 9, 13], [13] = [1, 11, 12], [14] = [3, 10],
            }, 20)); // 2 team clusters/team pinned

        yield return Case(new TopologyCase(MapGenType.InsideOut, 13, [0, 6],
            new()
            {
                [0] = [1, 3, 12], [1] = [0, 2, 4], [2] = [1, 4, 9, 11], [3] = [0, 5, 8],
                [4] = [1, 2, 5], [5] = [3, 4, 8], [6] = [7, 9, 12], [7] = [6, 8, 10],
                [8] = [3, 5, 7, 10], [9] = [2, 6, 11], [10] = [7, 8, 11], [11] = [2, 9, 10], [12] = [0, 6],
            }, 20)); // 1 team cluster/team pinned, home links to centre
    }

    private static object[] Case(TopologyCase c) => [c];

    private static MissionMap Generate(TopologyCase c)
    {
        var pmp = new MapGenParams
        {
            Type = c.Type,
            Teams = 2,
            CivIds = CivPair.Value,
            MapSize = 0,
        };
        return MissionMapGenerator.Generate(pmp, GenCore.Value, 0.0);
    }

    private static Mission AssembleMap(MissionMap map)
    {
        var specs = new[]
        {
            new SideSpec(0, "Team 0", (short)CivPair.Value[0]),
            new SideSpec(1, "Team 1", (short)CivPair.Value[1]),
        };
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
        var m = AssembleMap(map);

        Assert.Equal(c.Total, map.Clusters.Count);
        Assert.Equal(c.Total, m.Sectors.Count);

        // Exactly the traced home (team) clusters are home sectors; nothing else.
        var homeIds = map.Clusters.Where(x => x.HomeSector).Select(x => x.ClusterId).OrderBy(x => x).ToArray();
        Assert.Equal(c.Homes, homeIds);
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Topology_Adjacency_MatchesTracedNativeGraph(TopologyCase c)
    {
        Require.Artwork();
        var m = AssembleMap(Generate(c));
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
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Topology_HomeSectorsGetStations_NeutralSectorsHaveNone(TopologyCase c)
    {
        Require.Artwork();
        var m = AssembleMap(Generate(c));
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
        var m = AssembleMap(Generate(c));
        foreach (var sector in m.Sectors)
        {
            Assert.NotEmpty(sector.Asteroids);
            Assert.Contains(sector.Mineable, a => a.CanMineHe3);
        }
    }

    /// <summary>The strong, topology-discriminating invariant that the DoubleRing
    /// link port is genuinely wired in: 2 teams/iMapSize 0 collapses the double
    /// ring to K4 (all four sectors mutually linked). If <c>LinkClustersDoubleRing</c>
    /// were suppressed entirely the graph would have no edges and this fails;
    /// if it were left as the plain SingleRing ring it would fail the K4 check
    /// (verified red during development — see P398 report).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DoubleRing_TwoTeam_IsFullK4()
    {
        Require.Artwork();
        var c = Cases().Select(row => (TopologyCase)row[0]).First(x => x.Type == MapGenType.DoubleRing);
        var m = AssembleMap(Generate(c));
        foreach (var sector in m.Sectors)
        {
            // K4: every sector reaches all three others.
            var dst = sector.Warps.Select(w => w.Destination!.Id).Distinct().OrderBy(x => x).ToArray();
            var expected = Enumerable.Range(0, c.Total).Where(i => i != sector.Id).Select(i => (short)i).ToArray();
            Assert.Equal(expected, dst);
        }
    }
}
