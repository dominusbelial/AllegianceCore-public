using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P764 — the owed SIM-0107 territory-chain test. Native
/// <c>CsideIGC::IsSurroundedByTerritory</c> (<c>Igc/sideigc.cpp:325-347</c>)
/// recurses with <c>currentDepth + 1</c> per hop, gives up at
/// <c>currentDepth == 3</c> (sideigc.cpp:327), requires the cluster to be
/// EMPTY of stations seen by the side (sideigc.cpp:328-333) and to hold
/// <b>more than one</b> destination warp (sideigc.cpp:340-342), and fails the
/// whole branch when any non-visited neighbour is not territory
/// (sideigc.cpp:345). <see cref="Sector.IsTerritory"/> threads
/// <c>depth + 1</c> per hop the same way (Sector.IsSurroundedByTerritory).
///
/// The map is synthetic (<see cref="MissionMap.FromLists"/> — the same
/// assembly path a file-decoded map takes): an own-side station in <b>A</b>;
/// a compact ring A–B–C–D–A whose empty clusters are each 1–3 hops from the
/// station (B is one hop and ALL its neighbours are territory → B is
/// territory, and so are C and D); a second ring A–J–K–L–M–A with FIVE
/// clusters, whose 1-hop member J the depth limit must EXCLUDE (its branch
/// dives J(0)→K(1)→L(2)→M(3) and bails — without the depth rule the recursion
/// would ride the ring back to A); and a separate pure line A–E–F–G–H of
/// empty clusters where nothing but the hop count and the terminal warp
/// count differ — every cluster in the line fails, and the terminal H sits
/// more than three hops from any territory.
/// </summary>
/// <remarks>
/// Both structures are evaluated by the LIVE recursive rule the same way
/// native's <c>IsSurroundedByTerritory</c> evaluates them (native's cached
/// <c>UpdateTerritory</c> loop can additionally rescue ring-shaped clusters
/// in evaluation-order-dependent ways — sideigc.cpp:459-493 — which the row's
/// notes already document as out of scope for the live rule; no ring is
/// asserted here).
/// </remarks>
[Trait("Requires", "Artwork")]
public sealed class P764TerritoryChainTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static DataClusterIGC Cluster(short id, string name) =>
        new(
            StarSeed: (uint)(id * 7919),
            LightDirection: [0f, 1f, 0f],
            LightColor: 0xffffffff,
            ScreenX: id * 0.1f,
            ScreenY: 0f,
            ClusterId: id,
            Debris: 250,
            Stars: 250,
            Name: name,
            PosterName: "globe1",
            PlanetName: "",
            PlanetSinLatitude: 0,
            PlanetLongitude: 0,
            PlanetRadius: 50,
            Active: true,
            HomeSector: id == 100);   // A is the home sector

    private static DataWarpIGC Warp(short id, short fromCluster, short toCluster) =>
        new(
            WarpId: id,
            DestinationId: toCluster,
            Radius: 100,
            TextureName: "warp1",
            IconName: "warp1",
            Name: $"to{toCluster}",
            Position: [0f, 0f, 0f],
            Forward: [0f, 0f, 1f],
            Rotation: [0f, 0f, 1f, 0f],
            Signature: 0,
            ClusterId: fromCluster);

    private static Mission BuildMission()
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

        // A=100 (own station), ring A–B–C–D–A (B=101, C=102, D=103), the
        // 5-ring A–J–K–L–M–A (J=108 … M=111) whose 1-hop member the depth
        // limit must EXCLUDE, and the pure line A–E–F–G–H (E=104 … H=107).
        // Every warp below is one leg of a bidirectional pair, so every
        // interior cluster holds TWO destination warps
        // (sideigc.cpp:340-342's checkedWarpCount > 1).
        var clusters = new List<DataClusterIGC>();
        for (short id = 100; id <= 111; id++)
            clusters.Add(Cluster(id, $"P764-{id}"));

        var warps = new List<DataWarpIGC>();
        short wid = 0;
        void Link(short a, short b)
        {
            warps.Add(Warp(wid++, a, b));
            warps.Add(Warp(wid++, b, a));
        }
        Link(100, 101);   // A–B
        Link(101, 102);   // B–C
        Link(102, 103);   // C–D
        Link(103, 100);   // D–A  (the ring closes: B is 1 hop, C 2, D 3 hops)
        Link(100, 104);   // A–E
        Link(104, 105);   // E–F
        Link(105, 106);   // F–G
        Link(106, 107);   // G–H  (H is the terminal: 4 hops from A)
        Link(100, 108);   // A–J (the 5-ring below)
        Link(108, 109);   // J–K
        Link(109, 110);   // K–L
        Link(110, 111);   // L–M
        Link(111, 100);   // M–A  (the 5-ring closes: J is 1 hop, K 2, L 3, M 4)

        var map = MissionMap.FromLists(clusters, warps, [], []);
        var mission = Mission.Assemble(core, map,
            [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], new RecordingIgcSite());
        mission.Stage = MissionStage.Started;

        // The own-side station in A (native IsTerritory's station seed,
        // sideigc.cpp:436-444 / Sector.IsTerritory's hasOwnStation branch).
        var baseType = mission.Core.StationTypes.Values.First();
        var sectorA = mission.Sectors.First(s => s.Id == 100);
        var station = new Station(
            new DataStationIGC(
                Position: [0f, 0f, -500f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: 100, SideId: 0, StationId: 7700,
                StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "P764-A"),
            baseType);
        mission.PlaceStation(station, sectorA);
        return mission;
    }

    private static Sector S(Mission m, short id) => m.Sectors.First(s => s.Id == id);

    [SkippableFact]
    public void OneHopCluster_WithAllNeighboursTerritory_IsTerritory()
    {
        Require.Artwork();
        var m = BuildMission();

        // A holds the own station → territory outright (sideigc.cpp:436-444).
        Assert.True(S(m, 100).IsTerritory(0));

        // B (1 hop): empty, two destination warps (A and C), and BOTH
        // neighbours are territory — A outright, C recursively — so B is
        // territory (sideigc.cpp:325-347). C (2 hops) and D too — D is 3 hops
        // from the station but only DEPTH 2 in B's recursion, and its branch
        // reaches A, territory by its station outright (sideigc.cpp:436-444)
        // before any depth check. The depth limit itself is pinned by the
        // 5-ring test below, where the recursion DOES hit depth 3 first.
        Assert.True(S(m, 101).IsTerritory(0), "B (1 hop, all neighbours territory)");
        Assert.True(S(m, 102).IsTerritory(0), "C (2 hops)");
        Assert.True(S(m, 103).IsTerritory(0), "D (3 hops from the station, depth 2 in B's recursion)");
    }

    [SkippableFact]
    public void FiveClusterRing_TheDepthLimitBailsBeforeTheLoopCloses_NotTerritory()
    {
        // P764b — the pin that actually exercises sideigc.cpp:327's
        // currentDepth < 3 bail: in the 5-ring A–J–K–L–M–A every member's
        // neighbours eventually reach A's station, so WITHOUT the depth rule
        // (the depth + 1 / depth >= 3 threading, Sector.IsSurroundedByTerritory)
        // the recursion would ride the ring around to A and crown J territory.
        // With it, the branch from J dives J(0)→K(1)→L(2)→M(3) and bails at
        // depth 3 — M never reaches A — so the branch fails, and one failing
        // branch fails the whole cluster (sideigc.cpp:345): J is NOT territory
        // despite being 1 hop from the station in an all-territory ring.
        Require.Artwork();
        var m = BuildMission();

        Assert.False(S(m, 108).IsTerritory(0), "J (1 hop: K(1)→L(2)→M(3) bails at the depth limit)");
    }

    [SkippableFact]
    public void LineBeyondTheStation_NeverTerritory_TerminalIsMoreThanThreeHopsOut()
    {
        Require.Artwork();
        var m = BuildMission();

        // The pure line A–E–F–G–H: from any origin in the line the recursion
        // dives E(0)→F(1)→G(2)→H(3) and H fails — at depth 3 outright
        // (sideigc.cpp:327 currentDepth < 3) for the deep origins, and via its
        // single destination warp (checkedWarpCount == 1, sideigc.cpp:340-342)
        // for its own. One failing branch fails the whole cluster
        // (sideigc.cpp:345), so nothing in the line is territory — H sits more
        // than three hops from any territory and is never one.
        Assert.False(S(m, 104).IsTerritory(0), "E (1 hop, but its branch dives to depth 3)");
        Assert.False(S(m, 105).IsTerritory(0), "F (2 hops)");
        Assert.False(S(m, 106).IsTerritory(0), "G (3 hops)");
        Assert.False(S(m, 107).IsTerritory(0), "H (4 hops — beyond the depth limit)");
    }
}
