using Allegiance.Core.Wire;
// 'Server' and 'Bot' are ALSO namespace names (Allegiance.Server, Allegiance.Bot)
// reachable from this namespace, so the simple names are ambiguous with the
// namespaces. Aliased to distinct, unambiguous identifiers instead.
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using MissionMap = global::Allegiance.Core.Model.MissionMap;
using SideSpec = global::Allegiance.Core.Model.SideSpec;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Proves the P47 deliverable at the wire level: the server emits one
/// <c>FM_S_CLUSTERINFO(200)</c> per peer at the very start of the join burst,
/// and a real loopback <see cref="FltBot"/> decodes it. The decoded
/// cluster-id/home-flag list and warp-connectivity graph are then cross-checked
/// against an independently assembled <see cref="global::Allegiance.Core.Model.Mission"/>
/// of the same map file — the same data the server's own cluster-EXPORT burst
/// iterates (<c>Server.cs:808-812</c>) — proving the message reflects the real
/// topology rather than merely arriving.
/// </summary>
[Collection("LiveNetwork")]
public class ClusterInfoServerTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";
    private const string MapFile = "XXV_simple.igc";

    // Known ground truth from the map (verified via `tools/missiondump`):
    // 13 sectors, 38 warps, 0 dangling — comfortably within the 32-cluster /
    // 1026-warp arrays minus their -1 terminator slots (31 / 1025 usable).
    private const int ExpectedClusters = 13;
    private const int ExpectedWarps = 38;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "ClusterInfo Test", Map = MapFile, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 912u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 73000000000000000ul, DrmHash = "clusterinfotest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Independently assemble the map the same way the server's
    /// <c>LoadMission</c> does (<c>Server.cs:231-258</c>), so the test's expected
    /// topology comes from <c>Mission.Assemble</c> — the exact structure whose
    /// <c>Sectors</c> feed the server's <c>ExportCluster</c> burst
    /// (<c>Server.cs:808-812</c>) and the CLUSTERINFO factory.</summary>
    private static global::Allegiance.Core.Model.Mission AssembleExpectedMission()
    {
        var corePath = Path.Combine(ArtRoot, "PCore001.igc");
        var mapPath = Path.Combine(ArtRoot, MapFile);
        var core = global::Allegiance.Core.Model.StaticCore.Load(global::Allegiance.Core.IgcReader.ParseFile(corePath));
        var map = MissionMap.Load(global::Allegiance.Core.IgcReader.ParseFile(mapPath));
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> sides = civs.Count == 1
            ? [new SideSpec(0, "Team 1", civs[0].CivilizationId)]
            : [new SideSpec(0, "Team 1", civs[0].CivilizationId),
               new SideSpec(1, "Team 2", civs[1].CivilizationId)];
        return global::Allegiance.Core.Model.Mission.Assemble(core, map, sides, new NullIgcSite());
    }

    /// <summary>The peer must receive exactly one CLUSTERINFO in the join burst,
    /// decoding the right cluster count, the right home-sector flags, and a
    /// warp-connectivity graph identical to the real map's topology (checked
    /// entry-by-entry against the independently assembled mission).</summary>
    [Fact]
    public void PeerReceivesClusterInfo_InJoinBurst_MatchingMapTopology()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();

        bot.Connect(server.BoundPort, "ci_probe");
        bot.SendLogonRequest(Request("ci_probe"));

        // Wait until the join burst has been fully decoded, incl. CLUSTERINFO.
        int ticks = PumpUntil(server,
            () => bot.ShipAssignment is not null && bot.ClusterInfos.Count >= 1,
            4000);
        Assert.True(ticks > 0, "pump did not run");

        // Exactly one CLUSTERINFO in the burst.
        var ci = Assert.Single(bot.ClusterInfos);

        // Right cluster count.
        Assert.Equal(ExpectedClusters, ci.Clusters.Count);
        // Right home-sector flags (from the map dump: only Ceto 1053 and Quaoar 1061 are homes).
        Assert.Equal(2, ci.Clusters.Count(c => c.HomeSector));
        Assert.Contains(ci.Clusters, c => c is { Id: 1053, HomeSector: true });
        Assert.Contains(ci.Clusters, c => c is { Id: 1061, HomeSector: true });
        Assert.DoesNotContain(ci.Clusters, c => c.HomeSector && c is not { Id: 1053 } and not { Id: 1061 });

        // Right warp-edge count.
        Assert.Equal(ExpectedWarps, ci.Warps.Count);

        // ── Cross-check the DECODED graph against the real map's actual
        // topology, entry-by-entry, built independently from the map file. ──
        var expected = AssembleExpectedMission();

        var expClusters = expected.Sectors
            .Select(s => new ClusterInfo.ClusterEntry(s.Id, s.Cluster.HomeSector))
            .OrderBy(c => c.Id).ToArray();
        var decClusters = ci.Clusters.OrderBy(c => c.Id).ToArray();
        Assert.Equal(expClusters, decClusters);   // id + home flag match the mission exactly

        var expEdges = expected.Sectors
            .SelectMany(s => s.Warps
                .Where(r => r.Destination is not null)
                .Select(r => new ClusterInfo.WarpEdge(s.Id, r.Destination!.Id)))
            .OrderBy(e => e.SourceClusterId).ThenBy(e => e.DestClusterId)
            .ToArray();
        var decEdges = ci.Warps.OrderBy(e => e.SourceClusterId).ThenBy(e => e.DestClusterId).ToArray();
        Assert.Equal(expEdges, decEdges);   // the warp-connectivity graph matches the map

        // Spot-check against Sector.Warps directly (not just the aggregate set
        // above): every warp the first sector actually owns must appear as a
        // decoded edge sourced at that sector, resolved from the real map.
        var arrokoth = expected.Sectors.Single(s => s.Id == 1051);
        foreach (var r in arrokoth.Warps)
        {
            Assert.NotNull(r.Destination);   // no dangles in this map
            Assert.Contains(ci.Warps, e => e.SourceClusterId == 1051 && e.DestClusterId == r.Destination!.Id);
        }

        // And no edge mentions a sector that is not in the mission.
        var allIds = expected.Sectors.Select(s => s.Id).ToHashSet();
        Assert.All(ci.Warps, e => Assert.True(allIds.Contains(e.SourceClusterId) && allIds.Contains(e.DestClusterId)));
    }

    /// <summary>A round-trip guard independent of the server: build a CLUSTERINFO
    /// from a synthetic map, encode/decode, and confirm <c>Decode(Encode(x))</c>
    /// preserves the cluster list, home flags and warp edges exactly.</summary>
    [Fact]
    public void ClusterInfoRoundTripsThroughTheWire()
    {
        var original = new ClusterInfo
        {
            Clusters =
            [
                new ClusterInfo.ClusterEntry(1051, false),
                new ClusterInfo.ClusterEntry(1053, true),
            ],
            Warps =
            [
                new ClusterInfo.WarpEdge(1051, 1052),
                new ClusterInfo.WarpEdge(1053, 1070),
            ],
        };

        var decoded = ClusterInfo.Decode(original.Encode());
        Assert.Equal(original.Clusters, decoded.Clusters);
        Assert.Equal(original.Warps, decoded.Warps);
        Assert.Equal(ClusterInfo.Fmid, original.GetFmid());
        Assert.Equal(ClusterInfo.FixedSize, original.Encode().Length);
    }

    /// <summary>The fixed arrays carry the exact byte width the native client's
    /// parsing loop walks (<c>AllMessages.h:10176,10183</c>: <c>sizeof(...)/sizeof(SectorID)</c>),
    /// and the -1 sentinel terminates each list — so an oversized map is refused
    /// rather than emitting a corrupt message.</summary>
    [Fact]
    public void EncodeRejectsOversizedGraph()
    {
        var tooManyClusters = new ClusterInfo
        {
            Clusters = Enumerable.Range(0, ClusterInfo.MaxClusters).Select(i => new ClusterInfo.ClusterEntry((short)i, false)).ToArray(),
        };
        Assert.Throws<InvalidOperationException>(() => tooManyClusters.Encode());
    }
}
