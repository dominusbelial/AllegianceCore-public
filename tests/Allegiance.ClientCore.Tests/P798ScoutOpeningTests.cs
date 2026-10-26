using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P798 — pure unit proof for the scout opening: frontier selection and
/// one-lane-per-scout assignment on the real map corpus (m2xspiderw, m2scatter,
/// m2x3amber) and two generated maps of different sizes; the resource-driven
/// clash score; the support-call budget; and side isolation of the intel store
/// (two sides with different knowledge never see each other's data).
///
/// <para>The brief named <c>bcl1gncr.igc</c>; that file is not in the artwork
/// corpus (checked), so m2x3amber stands in as the third real map.</para>
/// </summary>
public sealed class P798ScoutOpeningTests
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static ClusterGraph GraphFrom(MissionMap map)
    {
        var byWarpId = map.Warps.ToDictionary(w => w.WarpId, w => w);
        var clusters = map.Clusters.Select(c => new ClusterInfo.ClusterEntry(c.ClusterId, c.HomeSector)).ToList();
        var edges = new List<ClusterInfo.WarpEdge>();
        foreach (var w in map.Warps)
            if (byWarpId.TryGetValue(w.DestinationId, out var partner))
                edges.Add(new ClusterInfo.WarpEdge(w.ClusterId, partner.ClusterId));
        return new ClusterGraph { Clusters = clusters, Warps = edges };
    }

    private static MissionMap RealMap(string file) =>
        MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, file)));

    private static ClusterGraph GeneratedGraph(int mapSize) => GraphFrom(MissionMapGenerator.Generate(
        new MapGenParams { Type = MapGenType.SingleRing, Teams = 2, CivIds = [25, 3], MapSize = mapSize },
        Catalog.RepresentativeCore(), 0));

    /// <summary>Model a side's home-sector start: its home cluster is known and
    /// the alephs that physically sit in it have been seen (the home station's
    /// scanner, P797 §1.4).</summary>
    private static (short Home, HashSet<short> Known, Dictionary<short, WarpKnowledge> Warps) HomeStart(MissionMap map, ClusterGraph graph)
    {
        short home = graph.Clusters.Where(c => c.HomeSector).Select(c => c.Id).Order().First();
        var byWarp = map.Warps.ToDictionary(w => w.WarpId, w => w);
        var warps = new Dictionary<short, WarpKnowledge>();
        foreach (var w in map.Warps.Where(w => w.ClusterId == home))
            warps[w.WarpId] = new WarpKnowledge(w.WarpId, w.ClusterId,
                byWarp.TryGetValue(w.DestinationId, out var p) ? p.ClusterId : (short)-1);
        return (home, [home], warps);
    }

    private static (ClusterGraph Graph, MissionMap Map) Real(string file) { var m = RealMap(file); return (GraphFrom(m), m); }

    [SkippableTheory]
    [InlineData("m2xspiderw.igc")]
    [InlineData("m2scatter.igc")]
    [InlineData("m2x3amber.igc")]
    public void Frontiers_are_adjacent_to_known_sectors_and_still_unseen(string file)
    {
        Require.Artwork();
        var (graph, map) = Real(file);
        var (home, known, warps) = HomeStart(map, graph);
        var lanes = ExplorationPlanner.FindFrontiers(graph, known, warps, home);

        Assert.NotEmpty(lanes);
        var adj = ExplorationPlanner.BuildAdjacency(graph);
        Assert.All(lanes, l =>
        {
            Assert.Contains(l.From, known);
            Assert.DoesNotContain(l.To, known);
            Assert.True(adj.TryGetValue(l.From, out var nbrs) && nbrs.Contains(l.To),
                $"lane {l.From}->{l.To} must be a topology edge");
            if (l.EntryKnown)
                Assert.Contains(warps.Values, w => w.Sector == l.From && w.PartnerSector == l.To);
            else
                Assert.DoesNotContain(warps.Values, w => w.Sector == l.From && w.PartnerSector == l.To);
        });
        // Unknown mouths exist only where the home scan did not reveal the aleph.
        Assert.Contains(lanes, l => l.EntryKnown);
        // Deterministic enumeration (same inputs -> same order).
        Assert.Equal(lanes.Select(l => (l.From, l.To, l.EntryWarpId)),
            ExplorationPlanner.FindFrontiers(graph, known, warps, home).Select(l => (l.From, l.To, l.EntryWarpId)));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Frontiers_work_on_two_generated_maps_of_different_sizes()
    {
        Require.Artwork();   // reads PCore001.igc from the artwork tree
        foreach (var size in new[] { 0, 1 })
        {
            var graph = GeneratedGraph(size);
            short home = graph.Clusters.Where(c => c.HomeSector).Select(c => c.Id).Order().First();
            var known = new HashSet<short> { home };
            // Generated maps: discover only the home sector's alephs by walking
            // the graph edges (the generator pairs warp ids, no file needed).
            var warps = new Dictionary<short, WarpKnowledge>();
            short id = 1;
            foreach (var edge in graph.Warps.Where(e => e.SourceClusterId == home).DistinctBy(e => e.DestClusterId))
                warps[id] = new WarpKnowledge(id++, home, edge.DestClusterId);
            var lanes = ExplorationPlanner.FindFrontiers(graph, known, warps, home);
            Assert.NotEmpty(lanes);
            Assert.All(lanes, l => Assert.Equal(home, l.From));
        }
    }

    [SkippableFact]
    public void Each_scout_gets_a_distinct_lane_and_keeps_it()
    {
        Require.Artwork();
        var (graph, map) = Real("m2xspiderw.igc");
        var (home, known, warps) = HomeStart(map, graph);
        var lanes = ExplorationPlanner.FindFrontiers(graph, known, warps, home);
        Assert.True(lanes.Count >= 2, "the home sector must expose at least two frontier lanes");
        var scouts = new[]
        {
            new ScoutPost(1, home), new ScoutPost(2, home), new ScoutPost(3, home),
        };
        var assigned = ExplorationPlanner.Assign(lanes, scouts,
            (from, to) => ExplorationPlanner.Hops(graph, from, to));
        Assert.Equal(Math.Min(scouts.Length, lanes.Count), assigned.Count);
        var keys = assigned.Values.Select(l => l.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());                 // no two on one lane
        Assert.All(assigned.Keys, id => Assert.Contains(scouts, s => s.Id == id));
        // Re-running with the current assignment keeps it (sticky, no churn).
        var again = ExplorationPlanner.Assign(lanes, scouts,
            (from, to) => ExplorationPlanner.Hops(graph, from, to), assigned);
        Assert.Equal(assigned.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)),
            again.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)));
        // A lane a scout cannot reach from its sector is never assigned.
        var far = ExplorationPlanner.Assign(lanes, [new ScoutPost(9, (short)9999)],
            (from, to) => ExplorationPlanner.Hops(graph, from, to));
        Assert.Empty(far);
    }

    // P830 — synthetic lanes for the churn/hysteresis proof: the score and the
    // Why are what the assignment stability depends on, not the map.
    private static FrontierLane Lane(short from, short to, double score, string why) =>
        new(from, to, -1, true, score, why);

    [Fact]
    public void P830_Lane_is_kept_across_passes_while_valid_and_taken_again_only_when_it_really_changed()
    {
        // The rig churn shape (batch 34/35): the same frontier pool and the same
        // explorers every 0.5 s pass must produce the SAME assignment — and the
        // 'assigned lane' line must not fire again.
        var lanes = new List<FrontierLane>
        {
            Lane(1050, 1054, 1005, "aleph 2168, partner alephs known"),
            Lane(1050, 1059, 1005, "aleph 2176, partner alephs known"),
            Lane(1052, 1052, 1500, "known sector not yet swept"),
            Lane(1052, 1053, 955, "aleph 2152, partner alephs known"),
        };
        var scouts = new[] { new ScoutPost(1, 1050), new ScoutPost(2, 1050) };
        int Hops(short _, short __) => 1;
        var current = ExplorationPlanner.Assign(lanes, scouts, Hops);
        Assert.Equal(2, current.Count);
        foreach (var _ in Enumerable.Range(0, 40))                 // 20 s of passes
        {
            var next = ExplorationPlanner.Assign(lanes, scouts, Hops, current);
            Assert.Equal(current.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)),
                next.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)));
            current = next;
        }

        // A lane that left the pool (swept / timed out / destination known) is
        // released: its scout takes the next frontier, the other scout holds.
        var held = current[1];
        var pool = lanes.Where(l => l.Key != held.Key).ToList();
        var after = ExplorationPlanner.Assign(pool, scouts, Hops, current);
        Assert.NotEqual(held.Key, after[1].Key);
        Assert.Equal(current[2].Key, after[2].Key);                // untouched scout keeps its lane
        Assert.Equal(after.Keys.Select(k => after[k].Key), after.Keys.Select(k => after[k].Key).Distinct());
    }

    [Fact]
    public void P830_No_churn_when_the_frontier_scores_jitter_and_explorers_are_unchanged()
    {
        // Race-agnostic: hop/intel jitter shifts a lane's score a little between
        // passes; a held lane must survive it (no re-assignment storm).
        var scouts = new[] { new ScoutPost(1, 1050), new ScoutPost(2, 1052) };
        int Hops(short _, short __) => 1;
        List<FrontierLane> Pool(int tick) =>
        [
            Lane(1050, 1054, 1005 + (tick % 3) * 5, "aleph 2168, partner alephs known"),
            Lane(1052, 1056, 955 + (tick % 2) * 5, "aleph 2150, partner alephs known"),
        ];
        var current = ExplorationPlanner.Assign(Pool(0), scouts, Hops);
        Assert.Equal(2, current.Count);
        foreach (var tick in Enumerable.Range(1, 30))
        {
            var next = ExplorationPlanner.Assign(Pool(tick), scouts, Hops, current);
            Assert.Equal(current.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)),
                next.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)));
            current = next;
        }
    }

    [Fact]
    public void P830_Held_lane_only_released_for_a_free_lane_beating_it_by_the_switch_margin()
    {
        var scouts = new[] { new ScoutPost(1, 1050), new ScoutPost(2, 1052) };
        int Hops(short _, short __) => 1;
        var lanes = new List<FrontierLane>
        {
            Lane(1050, 1054, 955, "aleph 2168, partner alephs known"),      // scout 1 holds
            Lane(1052, 1056, 955, "aleph 2150, partner alephs known"),      // scout 2 holds
        };
        var current = new Dictionary<int, FrontierLane>
        {
            [1] = lanes[0],
            [2] = lanes[1],
        };
        // A slightly better free lane is NOT enough (below the switch margin).
        lanes.Add(Lane(1050, 1059, 1005, "aleph 2176, partner alephs known"));
        var kept = ExplorationPlanner.Assign(lanes, scouts, Hops, current);
        Assert.Equal(current.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)),
            kept.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value.Key)));

        // A frontier far above the margin (a known-but-unswept sector) wins the
        // held lane back — and never one another scout still holds.
        lanes.Add(Lane(1050, 1052, 1500, "known sector not yet swept"));
        var switched = ExplorationPlanner.Assign(lanes, scouts, Hops, current);
        Assert.Equal(new LaneKey(1050, 1052), switched[1].Key);            // released for the better lane
        Assert.Equal(new LaneKey(1052, 1056), switched[2].Key);            // scout 2 keeps what it holds
        Assert.Equal(switched.Keys.Select(k => switched[k].Key), switched.Keys.Select(k => switched[k].Key).Distinct());

        // The released lane returns to the pool for whoever is free next pass.
        var released = ExplorationPlanner.Assign(lanes, scouts, Hops, switched);
        Assert.Equal(new LaneKey(1050, 1052), released[1].Key);
        Assert.Equal(new LaneKey(1052, 1056), released[2].Key);            // still held; no thrash
    }

    [SkippableTheory]
    [InlineData("m2xspiderw.igc")]
    [InlineData("m2scatter.igc")]
    [InlineData("m2x3amber.igc")]
    public void Clash_location_is_a_seen_resource_sector_with_every_term_logged(string file)
    {
        Require.Artwork();
        var (graph, map) = Real(file);
        var homes = graph.Clusters.Where(c => c.HomeSector).Select(c => c.Id).Order().ToArray();
        short ownHome = homes.First(), enemyHome = homes.Length > 1 ? homes[1] : homes[0];
        // All of this map's rocks are "seen" by this side (a fully scouted map).
        var rocks = map.Asteroids.Select(a => new RockSighting(a.AsteroidId, a.ClusterId, a.Ore, a.OreMax, a.AabmCapabilities)).ToArray();
        var known = graph.Clusters.Select(c => c.Id).ToHashSet();
        var choice = ClashScoring.Choose(graph, ownHome, enemyHome, known, rocks, [], [], []);
        if (rocks.Length == 0)
        {
            // No rocks on this map: topology fallback must still answer.
            choice = ClashScoring.Choose(graph, ownHome, enemyHome, known, rocks, [], [], [], topologyFallback: true);
            Assert.NotNull(choice);
            Assert.Contains("fallback", choice!.Value.Why);
        }
        else
        {
            Assert.NotNull(choice);
            Assert.True(choice!.Value.Resource > 0 || choice.Value.EnemyMiners > 0, "choice must be resource/activity based");
            Assert.Contains("resources=", choice.Value.Why);
        }
        Assert.Contains(choice!.Value.Sector, known);
        Assert.Contains("hops own=", choice.Value.Why);
    }

    [Fact]
    public void Clash_scoring_prefers_rich_contested_sectors_and_discounts_distance()
    {
        // 1(home) - 2 - 3 - 4 - 5 - 6(home); 3 and 5 are branches.
        var graph = new ClusterGraph
        {
            Clusters = Enumerable.Range(1, 6).Select(i => new ClusterInfo.ClusterEntry((short)i, i is 1 or 6)).ToList(),
            Warps =
            [
                new ClusterInfo.WarpEdge(1, 2), new ClusterInfo.WarpEdge(2, 3), new ClusterInfo.WarpEdge(3, 4),
                new ClusterInfo.WarpEdge(4, 5), new ClusterInfo.WarpEdge(5, 6),
            ],
        };
        var known = new HashSet<short> { 1, 2, 3, 4, 5, 6 };
        var richFar = new RockSighting(1, 4, 12000, 12000, 0x03);     // lots of He3
        var poorNear = new RockSighting(2, 2, 800, 800, 0x01);
        var choice = ClashScoring.Choose(graph, 1, 6, known, [poorNear, richFar],
            [new MinerSighting(10, 1, 4, 0)], [new MinerSighting(11, 2, 4, 0)],
            [new StationSighting(1, 2, 15, 4)]);
        Assert.NotNull(choice);
        Assert.Equal((short)4, choice!.Value.Sector);
        Assert.True(choice.Value.Resource > 0 && choice.Value.EnemyMiners == 1 && choice.Value.OwnMiners == 1 && choice.Value.EnemyStation);

        // Nothing seen yet -> no pick (unless the caller allows the topology fallback).
        Assert.Null(ClashScoring.Choose(graph, 1, 6, known, [], [], [], []));
        var fallback = ClashScoring.Choose(graph, 1, 6, known, [], [], [], [], topologyFallback: true);
        Assert.NotNull(fallback);
        Assert.Contains("fallback", fallback!.Value.Why);
    }

    /// <summary>P798b — the enemy-base recon route is topology-only: every hop
    /// of <see cref="ExplorationPlanner.NextHop"/> is a real CLUSTERINFO edge, the
    /// first step shortens the distance by one, and an unreachable target has no
    /// hop. This is the global knowledge a side has even with the map fogged.
    /// </summary>
    [SkippableTheory]
    [InlineData("m2xspiderw.igc")]
    [InlineData("m2scatter.igc")]
    [InlineData("m2x3amber.igc")]
    public void Recon_next_hop_is_a_topology_edge_that_shortens_the_distance(string file)
    {
        Require.Artwork();
        var (graph, _) = Real(file);
        var adj = ExplorationPlanner.BuildAdjacency(graph);
        var homes = graph.Clusters.Where(c => c.HomeSector).Select(c => c.Id).Order().ToArray();
        short ownHome = homes.First(), enemyHome = homes.Length > 1 ? homes[1] : homes[0];
        short cur = ownHome;
        int previous = ExplorationPlanner.Hops(graph, cur, enemyHome);
        Assert.True(previous > 0, "the two homes must be a real multi-hop route");
        var visited = new HashSet<short> { cur };
        while (cur != enemyHome)
        {
            short next = ExplorationPlanner.NextHop(graph, cur, enemyHome);
            Assert.True(next >= 0, $"recon must route from {cur} to the enemy home");
            Assert.True(adj[cur].Contains(next), $"recon hop {cur}->{next} must be a topology edge");
            Assert.True(visited.Add(next), "the recon path must not loop");
            int now = ExplorationPlanner.Hops(graph, next, enemyHome);
            Assert.Equal(previous - 1, now);
            previous = now;
            cur = next;
        }
        Assert.Equal(enemyHome, ExplorationPlanner.NextHop(graph, enemyHome, enemyHome));
        Assert.Equal(-1, ExplorationPlanner.NextHop(graph, ownHome, (short)9999));
    }

    /// <summary>P798b — exactly one scout takes the enemy-home recon. The widest
    /// scanner wins (it is what reveals a sector rim), then the nearest, then the
    /// stable bot id — so the opening never sends the whole wing to the enemy base
    /// and never hands the route to a 400-unit interceptor.</summary>
    [Fact]
    public void Enemy_home_recon_picks_one_scout_by_scanner_then_distance()
    {
        // home 1 - 2 - 3(enemy home); scouts start in 1, 2 and an unreachable 9.
        var graph = new ClusterGraph
        {
            Clusters = [new(1, true), new(2, false), new(3, true), new(9, false)],
            Warps = [new ClusterInfo.WarpEdge(1, 2), new ClusterInfo.WarpEdge(2, 3)],
        };
        var scouts = new[] { new ScoutPost(7, 1, 2400), new ScoutPost(4, 2, 2400), new ScoutPost(9, 9, 400) };
        int? pick = ExplorationPlanner.PickReconScout(scouts,
            (from, to) => ExplorationPlanner.Hops(graph, from, to), 3);
        Assert.Equal(4, pick);
        // A wider scanner beats a nearer hull; distance only breaks a scan tie.
        var wide = new[] { new ScoutPost(7, 2, 3000), new ScoutPost(4, 1, 2400) };
        Assert.Equal(7, ExplorationPlanner.PickReconScout(wide,
            (from, to) => ExplorationPlanner.Hops(graph, from, to), 3));
        var tied = new[] { new ScoutPost(6, 1, 2400), new ScoutPost(3, 1, 2400) };
        Assert.Equal(3, ExplorationPlanner.PickReconScout(tied,
            (from, to) => ExplorationPlanner.Hops(graph, from, to), 3));
        // Unknown target / no reachable scout -> no assignment.
        Assert.Null(ExplorationPlanner.PickReconScout(scouts, (_, _) => -1, 3));
        Assert.Null(ExplorationPlanner.PickReconScout(scouts,
            (from, to) => ExplorationPlanner.Hops(graph, from, to), -1));
    }

    [Fact]
    public void Clash_scoring_never_uses_unknown_sectors()
    {
        var graph = new ClusterGraph
        {
            Clusters = Enumerable.Range(1, 5).Select(i => new ClusterInfo.ClusterEntry((short)i, i is 1 or 5)).ToList(),
            Warps = [new(1, 2), new(2, 5), new(1, 3), new(3, 5)],
        };
        var rocks = new[]
        {
            new RockSighting(1, 2, 9999, 9999, 0x03),
            new RockSighting(2, 4, 9999, 9999, 0x03),
        };
        // Sector 4 is not known -> never a candidate; 2 is known and rich.
        var choice = ClashScoring.Choose(graph, 1, 5, new HashSet<short> { 1, 2, 3, 5 }, rocks, [], [], []);
        Assert.Equal((short)2, choice!.Value.Sector);
    }

    [Fact]
    public void Side_intel_store_keeps_each_sides_knowledge_isolated()
    {
        var store = new SideIntelStore();
        store.Write(7, 0, i =>
        {
            i.ObserveRock(new RockSighting(1, 2, 5000, 5000, 0x03), 1);
            i.ObserveStation(new StationSighting(10, 1, 15, 2), 1);
        });
        store.Write(7, 1, i => i.ObserveRock(new RockSighting(99, 5, 100, 100, 0x01), 1));
        var d0 = store.DigestAll().Single(d => d.Side == 0);
        var d1 = store.DigestAll().Single(d => d.Side == 1);
        Assert.Contains(d0.Rocks, r => r.RockId == 1);
        Assert.DoesNotContain(d0.Rocks, r => r.RockId == 99);
        Assert.Contains(d0.EnemyStations, s => s.StationId == 10);
        Assert.DoesNotContain(d1.Rocks, r => r.RockId == 1);
        Assert.Empty(d1.EnemyStations);
        // Applying a sibling's digest (the coordination channel) merges the SAME
        // side only; the other side never learns it.
        var other = new SideIntelStore();
        other.Apply(d0);
        Assert.NotEmpty(other.Get(7, 0).Rocks);
        Assert.Empty(other.Get(7, 1).Rocks);
        Assert.DoesNotContain(other.Get(7, 1).EnemyStations.Values, s => s.StationId == 10);
    }

    [Theory]
    [InlineData(20, 0, 30, .15, 3)]
    [InlineData(20, 3, 30, .15, 0)]
    [InlineData(100, 0, 30, .15, 15)]
    [InlineData(100, 20, 30, .15, 0)]
    [InlineData(10, 0, 30, .15, 2)]
    [InlineData(200, 0, 0, .15, 0)]
    public void Miner_support_call_uses_the_same_share_and_cap_budget(int population, int assigned, int cap, double share, int expected)
    {
        Assert.Equal(expected, BotManager.DefenseBudgetFor(population, assigned, cap, share));
        Assert.True(BotManager.DefenseBudgetFor(population, assigned, cap, share)
            <= Math.Max(0, Math.Min(cap, population)));
    }
}
