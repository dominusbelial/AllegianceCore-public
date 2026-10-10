namespace Allegiance.Core.Tests;

using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;
using Allegiance.TestInfra;

/// <summary>
/// P400 — structural verification of the procedural map topologies added on
/// top of P397/P398/P399: <see cref="MapGenType.Grid"/>,
/// <see cref="MapGenType.EastWest"/> and <see cref="MapGenType.SplitBase"/>.
///
/// Every expected sector count, home-sector set and warp-adjacency graph below
/// was derived by tracing the native C++ (<c>mapmakerigc.cpp</c>: Grid
/// 2272-2414, EastWest 2436-2553, SplitBase 2569-2625) and simulated
/// independently of the port (a throwaway Python script re-implementing the
/// native <c>LinkClusters</c> bodies from the C++ and recording the resulting
/// edges) — so these tests assert against known-good native-derived values,
/// not against the port re-reading its own logic.
///
/// EastWest requires exactly 2 teams (its real <c>IsValid</c>,
/// <c>mapmakerigc.cpp:2416-2427</c>) and overrides population with two
/// per-sector tables, so NOT every sector holds He3 (its second home sector
/// and neutral locals 6-9 legitimately have none) — its structural case sets
/// <see cref="TopologyCase.EverySectorMinable"/> false and the population
/// behaviour is pinned by its own dedicated override test.
/// </summary>
public sealed class MapGenGridFamilyTests
{
    private static readonly Lazy<StaticCore> GenCore = new(() =>
    {
        StaticCore? best = null;
        int bestOk = 0;
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            int ok = core.Civilizations.Values.Count(c =>
                c.InitialStationTypeId != -1 && core.StationTypes.ContainsKey(c.InitialStationTypeId));
            if (ok > bestOk) { bestOk = ok; best = core; }
        }
        if (best is null || bestOk < 2)
            throw new InvalidOperationException("no static core with 2 usable civilizations found in corpus");
        return best;
    });

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
        Dictionary<short, short[]> Adjacency, short EdgeCount,
        bool EverySectorMinable = true);

    public static IEnumerable<object[]> Cases()
    {
        // Grid, 2 teams / iMapSize 0 (cpp 2272-2414): team-per-team 1, 4
        // neutral per team; each home anchors a neutral diamond cross-linked to
        // the next team's block — a fully 4-regular 10-sector graph.
        yield return Case(new TopologyCase(MapGenType.Grid, 2, 0, 10, [0, 5],
            new()
            {
                [0] = [1, 2, 8, 9], [1] = [0, 2, 4, 9], [2] = [0, 1, 3, 8],
                [3] = [2, 4, 5, 7], [4] = [1, 3, 5, 6], [5] = [3, 4, 6, 7],
                [6] = [4, 5, 7, 9], [7] = [3, 5, 6, 8], [8] = [0, 2, 7, 9],
                [9] = [0, 1, 6, 8],
            }, 20));

        // EastWest (cpp 2436-2553): mirrored two-team layout — each team's two
        // home sectors (0,1) anchor 11 neutrals (2..12); the far-end neutrals
        // cross the midline (10-25, 11-24, 12-23). Exactly 2 teams required.
        yield return Case(new TopologyCase(MapGenType.EastWest, 2, 0, 26, [0, 1, 13, 14],
            new()
            {
                [0] = [2, 3, 4, 5], [1] = [6, 7, 8, 9], [2] = [0, 3, 4],
                [3] = [0, 2, 5, 6], [4] = [0, 2, 5, 10], [5] = [0, 3, 4, 10, 11],
                [6] = [1, 3, 7, 8], [7] = [1, 6, 9], [8] = [1, 6, 9, 11, 12],
                [9] = [1, 7, 8, 12], [10] = [4, 5, 25], [11] = [5, 8, 24],
                [12] = [8, 9, 23], [13] = [15, 16, 17, 18], [14] = [19, 20, 21, 22],
                [15] = [13, 16, 17], [16] = [13, 15, 18, 19], [17] = [13, 15, 18, 23],
                [18] = [13, 16, 17, 23, 24], [19] = [14, 16, 20, 21],
                [20] = [14, 19, 22], [21] = [14, 19, 22, 24, 25],
                [22] = [14, 20, 21, 25], [23] = [12, 17, 18], [24] = [11, 18, 21],
                [25] = [10, 21, 22],
            }, 49,
            EverySectorMinable: false));   // override leaves some sectors He3-less

        // SplitBase (cpp 2569-2625): 2 team + 2 neutral per team; each team's
        // split home hangs off the neutral chain. 2 teams → 8 sectors.
        yield return Case(new TopologyCase(MapGenType.SplitBase, 2, 0, 8, [0, 1, 4, 5],
            new()
            {
                [0] = [7], [1] = [2], [2] = [1, 3, 7], [3] = [2, 4, 6],
                [4] = [3], [5] = [6], [6] = [3, 5, 7], [7] = [0, 2, 6],
            }, 8));
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
    public void Topology_MinableAsteroidsPresent(TopologyCase c)
    {
        Require.Artwork();
        var map = Generate(c);
        if (c.EverySectorMinable)
        {
            var m = AssembleMap(c, map);
            foreach (var sector in m.Sectors)
            {
                Assert.NotEmpty(sector.Asteroids);
                Assert.Contains(sector.Mineable, a => a.CanMineHe3);
            }
        }
        else
        {
            // EastWest's PopulateCluster override (see the dedicated test below)
            // legitimately leaves its second home sector and neutral locals 6-9
            // with no He3, so assert presence across the map instead of in every
            // sector (still verifying it is populated with genuinely minable ore).
            Assert.Contains(map.Asteroids, a =>
                a.ClusterId is >= 0 and < 26 && (a.AabmCapabilities & DataAsteroidIGC.AabmMineHe3) != 0);
        }
    }

    /// <summary>
    /// ⭐ EastWest's native <c>IsValid</c> (mapmakerigc.cpp:2416-2427) is REAL:
    /// it returns an error unless <c>nTeams == 2</c>. A 3-team config is
    /// rejected before any cluster is generated; a 2-team config is accepted
    /// and generates its 26 sectors.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EastWest_RequiresExactlyTwoTeams()
    {
        Require.Artwork();
        var bad = new MapGenParams
        {
            Type = MapGenType.EastWest, Teams = 3, CivIds = CivIds(3), MapSize = 0,
        };
        var ex = Assert.Throws<InvalidDataException>(() => MissionMapGenerator.Generate(bad, GenCore.Value, 0.0));
        Assert.Contains("exactly 2 teams", ex.Message);

        var good = new MapGenParams
        {
            Type = MapGenType.EastWest, Teams = 2, CivIds = CivIds(2), MapSize = 0,
        };
        var map = MissionMapGenerator.Generate(good, GenCore.Value, 0.0);
        Assert.Equal(26, map.Clusters.Count);   // (2 team + 11 neutral) * 2 teams
    }

    /// <summary>⭐ SplitBase's own <c>IsValid</c> is a vestigial NULL stub — its
    /// only real check was commented out March 2006 (mapmakerigc.cpp:2555-2563)
    /// — so, unlike EastWest/DiamondRing, it adds no "must be N teams"
    /// constraint: a 3-team config (legal for the base) is accepted and
    /// generates its 12 sectors.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SplitBase_HasNoExtraValidityConstraint()
    {
        Require.Artwork();
        var map = MissionMapGenerator.Generate(new MapGenParams
        {
            Type = MapGenType.SplitBase, Teams = 3, CivIds = CivIds(3), MapSize = 0,
        }, GenCore.Value, 0.0);
        Assert.Equal(12, map.Clusters.Count);   // (2 team + 2 neutral) * 3 teams
    }

    /// <summary>
    /// ⭐ EastWest's <c>PopulateCluster</c> override (mapmakerigc.cpp:2541-2553)
    /// genuinely takes effect. Unlike every other topology, EastWest's
    /// mineable/special counts come from two per-sector tables (cMineable /
    /// cSpecial, cpp 2547-2548) rather than the uniform base counts — so the
    /// sectors differ sharply from each other AND from the base 22-total /
    /// 10-He3 home and 18-total / 8-He3 neutral. Using the default params
    /// (mine/special/generic = 10/2/10 home, 8/2/8 neutral), the team-0 block
    /// (global ids 0..12 = locals 0..12) must be exactly:
    ///   local 0 (home): 2·10 mine + 0 spec + 10 gen = 30 total, 20 He3
    ///   local 1 (home): 0 mine + 0 spec + 10 gen      = 10 total,  0 He3
    ///   local 2 (neutral): 2·8 mine + 0 + 8 gen       = 24 total, 16 He3
    ///   local 6 (neutral): 0 mine + 2·2 spec + 8 gen  = 12 total,  0 He3
    ///   local 10 (neutral): 1·8 mine + 0 + 8 gen      = 16 total,  8 He3
    /// Removing the override would collapse every home to 22 and every neutral
    /// to 18 — this test goes red (verified during development).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EastWest_PopulateCluster_OverrideDrivesPerSectorCounts()
    {
        Require.Artwork();
        var map = MissionMapGenerator.Generate(new MapGenParams
        {
            Type = MapGenType.EastWest, Teams = 2, CivIds = CivIds(2), MapSize = 0,
        }, GenCore.Value, 0.0);

        int Total(short cid) => map.Asteroids.Count(a => a.ClusterId == cid);
        int He3(short cid) => map.Asteroids.Count(a =>
            a.ClusterId == cid && (a.AabmCapabilities & DataAsteroidIGC.AabmMineHe3) != 0);
        int Special(short cid) => map.Asteroids.Count(a =>
            a.ClusterId == cid && (a.AabmCapabilities & DataAsteroidIGC.AabmSpecial) != 0);

        // Team-0 block: global 0 = local 0 (home), global 1 = local 1 (home),
        // global 2 = local 2 (neutral), global 6 = local 6, global 10 = local 10.
        Assert.Equal(30, Total(0));  Assert.Equal(20, He3(0));  Assert.Equal(0, Special(0));
        Assert.Equal(10, Total(1));  Assert.Equal(0, He3(1));   Assert.Equal(0, Special(1));
        Assert.Equal(24, Total(2));  Assert.Equal(16, He3(2));
        Assert.Equal(12, Total(6));  Assert.Equal(0, He3(6));   Assert.Equal(4, Special(6));
        Assert.Equal(16, Total(10)); Assert.Equal(8, He3(10));

        // Symmetry: the team-1 block mirrors team 0 (global 13..25 = locals 0..12).
        Assert.Equal(30, Total(13)); Assert.Equal(20, He3(13));
        Assert.Equal(10, Total(14)); Assert.Equal(0, He3(14));
        Assert.Equal(12, Total(19)); Assert.Equal(4, Special(19));
    }
}
