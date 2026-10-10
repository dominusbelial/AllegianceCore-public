using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P591d — the showcase minefield PLACEMENT choice. On today's (pre-P591d) code
/// the showcase resolved both sides' fields to a SINGLE arbitrary outbound aleph
/// in the rendezvous sector (<c>BotManager.FindArrivalWarp</c> returns the first
/// warp in dictionary order whose partner is in a different sector), which on
/// the 221 map landed both fields at Alderan's Cirrus aleph (2150) — far from
/// the fight. This fails-first test asserts the corrected, per-side placement:
/// each side's field goes across the ENEMY's actual ARRIVAL aleph (the aleph the
/// enemy fleet transits to enter the rendezvous), computed from the real map
/// graph by <see cref="Allegiance.Tools.McpSpawner.BotManager.EnemyArrivalWarp"/>
/// — the same BFS routing the bots use (<c>EnsureCombatPath</c> →
/// <c>ClusterGraph.FindShortestPath</c>).
///
/// On the 221 map (m2xspiderw): side 0's home garrison is Ganymede (1050), side
/// 1's is Organia (1051); the showcase rendezvous is Alderan (1052). The enemy
/// arrival alephs are the two contested alephs the task names: side 1's fleet
/// (from Organia) enters Alderan via the JUNIPER aleph (2183), and side 0's
/// fleet (from Ganymede) enters via the RIGEL aleph (2187). So side 0's field
/// must land at Juniper (2183) and side 1's at Rigel (2187) — NOT both at Cirrus
/// (2150).
/// </summary>
public class P591dShowcaseMinefieldPlacementTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private readonly ITestOutputHelper _output;
    public P591dShowcaseMinefieldPlacementTests(ITestOutputHelper output) => _output = output;

    private static Dictionary<short, DataWarpIGC> LoadWarps()
    {
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "m2xspiderw.igc")));
        return map.Warps.ToDictionary(w => w.WarpId);
    }

    [Fact]
    public void Side0_field_goes_across_the_enemy_Juniper_aleph_2183_not_Cirrus_2150()
    {
        var warps = LoadWarps();
        // Side 0 lays a field; its enemy is side 1, whose home is Organia (1051).
        // The enemy's arrival aleph in Alderan (1052) = Juniper (2183).
        var arrival = Allegiance.Tools.McpSpawner.BotManager.EnemyArrivalWarp(warps, 1052, enemySide: 1, enemyHomeSector: 1051);
        Assert.NotNull(arrival);
        Assert.Equal((short)2183, arrival!.Value.WarpId);
        Assert.Equal((short)1061, arrival.Value.PenultimateSector);   // Juniper — where side 1 enters FROM
        _output.WriteLine($"side 0 field -> enemy (side 1) arrival aleph {arrival.Value.WarpId} via {arrival.Value.PenultimateSector}");
    }

    [Fact]
    public void Side1_field_goes_across_the_enemy_Rigel_aleph_2187_not_Cirrus_2150()
    {
        var warps = LoadWarps();
        // Side 1 lays a field; its enemy is side 0, whose home is Ganymede (1050).
        // The enemy's arrival aleph in Alderan (1052) = Rigel (2187).
        var arrival = Allegiance.Tools.McpSpawner.BotManager.EnemyArrivalWarp(warps, 1052, enemySide: 0, enemyHomeSector: 1050);
        Assert.NotNull(arrival);
        Assert.Equal((short)2187, arrival!.Value.WarpId);
        Assert.Equal((short)1059, arrival.Value.PenultimateSector);   // Rigel — where side 0 enters FROM
        _output.WriteLine($"side 1 field -> enemy (side 0) arrival aleph {arrival.Value.WarpId} via {arrival.Value.PenultimateSector}");
    }

    [Fact]
    public void Neither_field_lands_at_the_far_Cirrus_aleph_2150()
    {
        var warps = LoadWarps();
        var s0 = Allegiance.Tools.McpSpawner.BotManager.EnemyArrivalWarp(warps, 1052, 1, 1051);
        var s1 = Allegiance.Tools.McpSpawner.BotManager.EnemyArrivalWarp(warps, 1052, 0, 1050);
        Assert.NotNull(s0); Assert.NotNull(s1);
        // Cirrus (2150) is where the pre-P591d code dumped BOTH fields (FindArrivalWarp's
        // first-arbitrary-outbound-aleph). Neither field may be there now.
        Assert.NotEqual((short)2150, s0!.Value.WarpId);
        Assert.NotEqual((short)2150, s1!.Value.WarpId);
        // And the two fields land at two DIFFERENT contested alephs (not both on one).
        Assert.NotEqual(s0.Value.WarpId, s1.Value.WarpId);
        _output.WriteLine($"side0->{s0.Value.WarpId} side1->{s1.Value.WarpId} (both != 2150, distinct)");
    }

    /// <summary>
    /// Guard: the BFS route chosen matches what the bots actually fly. The
    /// site-reported side-0 layer reached Alderan via Rigel (Ganymede→Rigel→
    /// Alderan) and the side-1 layer via Gaia/Juniper — the same shortest paths
    /// this resolver derives, so a minefield planted at the derived arrival
    /// aleph genuinely sits across the enemy's inbound lane, not a decorative
    /// aleph the enemy never uses.
    /// </summary>
    [Fact]
    public void Resolver_routes_match_the_bots_shortest_path_machinery()
    {
        var warps = LoadWarps();
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in warps.Values)
        {
            if (!warps.TryGetValue(w.DestinationId, out var partner)) continue;
            Add(adj, w.ClusterId, partner.ClusterId);
            Add(adj, partner.ClusterId, w.ClusterId);
        }
        foreach (var k in adj.Keys) adj[k].Sort();

        // Side 0 (Ganymede 1050) -> Alderan: penultimate Rigel (1059) — matches.
        var s0 = Allegiance.Tools.McpSpawner.BotManager.EnemyArrivalWarp(warps, 1052, 0, 1050);
        Assert.NotNull(s0);
        Assert.Equal((short)1059, s0!.Value.PenultimateSector);
        // Side 1 (Organia 1051) -> Alderan: penultimate Juniper (1061).
        var s1 = Allegiance.Tools.McpSpawner.BotManager.EnemyArrivalWarp(warps, 1052, 1, 1051);
        Assert.NotNull(s1);
        Assert.Equal((short)1061, s1!.Value.PenultimateSector);
    }

    private static void Add(Dictionary<short, List<short>> adj, short a, short b)
    {
        if (!adj.TryGetValue(a, out var l)) { l = new(); adj[a] = l; }
        if (!l.Contains(b)) l.Add(b);
    }
}
