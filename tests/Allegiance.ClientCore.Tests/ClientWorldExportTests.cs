using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the new <see cref="ClientWorld"/>
/// surface — the EXPORT(26) object table, teammate ship visibility, and the
/// CLUSTERINFO(200) graph. Messages are constructed directly, exactly like
/// AllegianceCore's own <c>Wire/*Tests.cs</c>, and fed through
/// <see cref="ClientWorld.Apply"/>; each asserts the decoded object lands in
/// the right dictionary keyed by the right id.
/// </summary>
public class ClientWorldExportTests
{
    private static ClientWorld WorldWithMyShip(short myShipId)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });
        return world;
    }

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 501, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static DataClusterIGC MakeCluster(short id) => new(
        StarSeed: 642u, LightDirection: [1f, 2f, 3f], LightColor: 0xFFAAu,
        ScreenX: 617f, ScreenY: 630f, ClusterId: id, Debris: 613, Stars: 614,
        Name: $"sector_{id}", PosterName: "", PlanetName: "",
        PlanetSinLatitude: (byte)0x40, PlanetLongitude: (byte)0x41, PlanetRadius: (byte)0x42,
        Active: false, HomeSector: id == 624);

    private static DataStationIGC MakeStation(short id, short clusterId) => new(
        Position: [100f, 0f, 0f], Up: [0f, 791f, 703f], Forward: [713f, 714f, 715f],
        Rotation: [1f, 725f, 702f, 726f], ClusterId: clusterId, SideId: 793, StationId: id,
        StationTypeId: (short)0xA1, BpHull: (byte)0xA3, BpShield: (byte)0xB4, Name: $"garrison_{id}");

    private static DataWarpIGC MakeWarp(short id, short clusterId) => new(
        WarpId: id, DestinationId: (short)(id + 781), Radius: 782,
        TextureName: "tex", IconName: "ico", Name: $"warp_{id}",
        Position: [786f, 758f, 749f], Forward: [741f, 719f, 799f], Rotation: [786f, 784f, 735f, 790f],
        Signature: 764f, ClusterId: clusterId);

    private static DataAsteroidIGC MakeAsteroid(short id, short clusterId) => new(
        Signature: 722f, Position: [748f, 779f, 744f], Up: [793f, 772f, 728f],
        Forward: [771f, 723f, 778f], Rotation: [767f, 770f, 792f, 717f],
        Ore: 745f, OreMax: 765f, AabmCapabilities: 761, AsteroidId: id,
        HitPoints: 797f, Radius: 752, ModelName: "mdl", TextureName: "tex",
        IconName: "ico", ClusterId: clusterId, Name: $"rock_{id}", Fraction: (byte)0x61);

    // ── EXPORT(26) object table ──────────────────────────────────────────

    [Fact]
    public void ExportCluster_LandsInClusters_KeyedByClusterId()
    {
        var world = WorldWithMyShip(716);
        var cluster = MakeCluster(742);
        world.Apply(new ExportCluster { ObjectType = ExportCluster.OtCluster, Cluster = cluster });

        Assert.Single(world.Clusters);
        Assert.Equal(cluster.ClusterId, world.Clusters.Keys.Single());
        Assert.Equal(cluster.Name, world.Clusters[742].Name);
        Assert.Equal((short)742, world.Clusters[742].ClusterId);   // id survived verbatim
    }

    [Fact]
    public void ExportStation_LandsInStations_KeyedByStationId()
    {
        var world = WorldWithMyShip(716);
        var station = MakeStation(789, clusterId: 775);
        world.Apply(new ExportStation { ObjectType = ExportStation.OtStation, Station = station });

        Assert.Single(world.Stations);
        Assert.Equal(station.StationId, world.Stations.Keys.Single());
        Assert.Equal(station.Name, world.Stations[789].Name);
        Assert.Equal((short)775, world.Stations[789].ClusterId);
    }

    [Fact]
    public void ExportWarp_LandsInWarps_KeyedByWarpId()
    {
        var world = WorldWithMyShip(716);
        var warp = MakeWarp(757, clusterId: 794);
        world.Apply(new ExportWarp { ObjectType = ExportWarp.OtWarp, Warp = warp });

        Assert.Single(world.Warps);
        Assert.Equal(warp.WarpId, world.Warps.Keys.Single());
        Assert.Equal(warp.Name, world.Warps[757].Name);
        Assert.Equal((short)794, world.Warps[757].ClusterId);
    }

    [Fact]
    public void ExportAsteroid_LandsInAsteroids_KeyedByAsteroidId()
    {
        var world = WorldWithMyShip(716);
        var ast = MakeAsteroid(795, clusterId: 743);
        world.Apply(new ExportAsteroid { ObjectType = ExportAsteroid.OtAsteroid, Asteroid = ast });

        Assert.Single(world.Asteroids);
        Assert.Equal(ast.AsteroidId, world.Asteroids.Keys.Single());
        Assert.Equal(ast.Name, world.Asteroids[795].Name);
        Assert.Equal((short)743, world.Asteroids[795].ClusterId);
    }

    [Fact]
    public void ExportObjectDicts_AreIndependent_FromEachOther()
    {
        var world = WorldWithMyShip(716);
        world.Apply(new ExportCluster { Cluster = MakeCluster(724) });
        world.Apply(new ExportCluster { Cluster = MakeCluster(718) });   // replace-on-arrival,
                                                                       // same dict
        Assert.Equal(2, world.Clusters.Count);
        Assert.Empty(world.Stations);
        Assert.Empty(world.Warps);
        Assert.Empty(world.Asteroids);
    }

    // ── teammate ship visibility ─────────────────────────────────────────

    [Fact]
    public void ExportShip_ForNonMine_EntersShips_WithIsMineFalse()
    {
        var world = WorldWithMyShip(716);           // my own ship id known
        const short teammate = 755;

        world.Apply(new ExportShip
        {
            ShipId = teammate,
            HullId = 763,
            SideId = 796,
            CharacterName = "joan",
            PilotType = ExportShip.PilotTypePlayer,
        });

        Assert.True(world.Ships.ContainsKey(teammate));
        Assert.False(world.Ships[teammate].IsMine);            // NOT mine
        Assert.Equal((short)796, world.Ships[teammate].SideId);
        Assert.Equal("joan", world.Ships[teammate].CharacterName);
        Assert.Equal((short)763, world.Ships[teammate].HullId);
        Assert.Equal(2, world.Ships.Count);                    // mine + teammate
        Assert.Equal((short)716, world.MyShipId);
    }

    /// <summary>AllegianceCore P86: ExportShip's PartData[] tail is the only
    /// wire path a teammate's mounted parts can reach a non-owning peer
    /// (BuyLoadoutAck is owner-only). No FractionMounted on the wire — an
    /// already-mounted part is mapped to <see cref="ExpandedPartData.FullyMounted"/>.</summary>
    [Fact]
    public void ExportShip_WithParts_PopulatesTeammateLoadout()
    {
        var world = WorldWithMyShip(716);
        const short teammate = 755;

        world.Apply(new ExportShip
        {
            ShipId = teammate,
            HullId = 763,
            SideId = 796,
            CharacterName = "joan",
            PilotType = ExportShip.PilotTypePlayer,
            Parts = [new PartData(PartId: 41, Amount: 20, MountId: 0)],
        });

        var loadout = world.Ships[teammate].Loadout;
        Assert.NotNull(loadout);
        Assert.Single(loadout!);
        Assert.Equal(new ExpandedPartData(41, 20, 0, ExpandedPartData.FullyMounted), loadout![0]);
    }

    /// <summary>A re-sent EXPORT (the server's only way to signal a teammate's
    /// loadout changed post-creation) must overwrite the previous Loadout, not
    /// merge with or append to it.</summary>
    [Fact]
    public void ExportShip_Resent_ReplacesTeammateLoadout_NotAppends()
    {
        var world = WorldWithMyShip(716);
        const short teammate = 755;
        world.Apply(new ExportShip { ShipId = teammate, Parts = [new PartData(41, 20, 0)] });

        world.Apply(new ExportShip { ShipId = teammate, Parts = [new PartData(42, 5, 1)] });

        var loadout = world.Ships[teammate].Loadout;
        Assert.Single(loadout!);
        Assert.Equal(new ExpandedPartData(42, 5, 1, ExpandedPartData.FullyMounted), loadout![0]);
    }

    [Fact]
    public void PlayerInfo_AttachesToExistingShipEntity()
    {
        var world = WorldWithMyShip(751);
        const short teammate = 760;
        world.Apply(new ExportShip { ShipId = teammate, SideId = 783, CharacterName = "joan" });

        world.Apply(new PlayerInfo
        {
            ShipId = teammate,
            ISide = 773,
            CharacterName = "joan",
            Money = 766,
            NKills = 772, NEjections = 709, NDeaths = 701,
            FExperience = 759.5f,
            RankId = 777,
            PilotType = PlayerInfo.PilotTypePlayer,
        });

        Assert.Single(world.Ships, e => e.Key == teammate);
        var ship = world.Ships[teammate];
        Assert.NotNull(ship.Player);
        Assert.Equal((short)773, ship.Player.ISide);
        Assert.Equal(766, ship.Player.Money);
        Assert.Equal("joan", ship.Player.CharacterName);
        Assert.Equal((short)772, ship.Player.NKills);
        Assert.Equal(759.5f, ship.Player.FExperience, precision: 3);
    }

    [Fact]
    public void PlayerInfo_CanCreateShipAbsent_FromPlayerInfoAlone()
    {
        var world = WorldWithMyShip(720);
        const short unseen = 737;

        world.Apply(new PlayerInfo { ShipId = unseen, ISide = 714, CharacterName = "late_ship" });

        Assert.True(world.Ships.ContainsKey(unseen));
        Assert.False(world.Ships[unseen].IsMine);
        Assert.NotNull(world.Ships[unseen].Player);
        Assert.Equal("late_ship", world.Ships[unseen].CharacterName);
    }

    [Fact]
    public void EarlyExportShip_ForStranger_StaysIsMineFalse_WhenMyShipAssignedLater()
    {
        var world = new ClientWorld();
        const short stranger = 698;
        const short myShip = 712;

        // A stranger's EXPORT lands before we know which ship we are. It still
        // enters the table, just flagged not-mine (MyShipId unknown yet).
        world.Apply(new ExportShip { ShipId = stranger, SideId = 694, CharacterName = "stranger" });
        Assert.False(world.Ships[stranger].IsMine);   // MyShipId null → not mine

        // Later SET_CLUSTER assigns US a (different) ship. That one is mine; the
        // stranger exported earlier stays not-mine. No stale-IsMine reconciliation
        // is needed because SetCluster marks its own ship authoritative.
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShip,
                new Ship(myShip, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                2u),
            Cookie = 0x2,
        });
        Assert.Equal(myShip, world.MyShipId);
        Assert.True(world.Ships[myShip].IsMine);
        Assert.False(world.Ships[stranger].IsMine);   // unchanged — no leak
        Assert.Equal(2, world.Ships.Count);
    }

    // ── CLUSTERINFO(200) graph ──────────────────────────────────────────

    [Fact]
    public void ClusterInfo_PopulatesGraphOnce()
    {
        var world = WorldWithMyShip(756);
        var info = new ClusterInfo
        {
            Clusters =
            [
                new ClusterInfo.ClusterEntry(716, HomeSector: true),
                new ClusterInfo.ClusterEntry(724, HomeSector: false),
                new ClusterInfo.ClusterEntry(745, HomeSector: false),
            ],
            Warps =
            [
                new ClusterInfo.WarpEdge(716, 724),
                new ClusterInfo.WarpEdge(724, 745),
                new ClusterInfo.WarpEdge(745, 716),
            ],
        };

        world.Apply(info);
        Assert.NotNull(world.Graph);
        Assert.Equal(3, world.Graph.Clusters.Count);
        Assert.Equal(3, world.Graph.Warps.Count);
        Assert.Equal((short)724, world.Graph.Warps[0].DestClusterId);

        // Set once — a repeat is ignored, not rebuilt.
        var first = world.Graph;
        world.Apply(new ClusterInfo { Clusters = [], Warps = [] });
        Assert.Same(first, world.Graph);
    }

    /// <summary>
    /// P634 — the client-side "world complete" signal. A fresh world is not
    /// WorldReady; clusters alone (ExportCluster) don't make it ready because
    /// the routing GRAPH (CLUSTERINFO) is still missing; once the graph lands
    /// it becomes WorldReady (the routing consumer's prerequisite); and Reset
    /// tears it back down. Fails on pre-P634 code where WorldReady doesn't exist.
    /// </summary>
    [Fact]
    public void WorldReady_requires_graph_and_clusters_and_resets()
    {
        var world = new ClientWorld();

        // Fresh: neither the graph nor any cluster — not ready.
        Assert.False(world.WorldReady, "a fresh world must not be WorldReady");

        // Clusters alone: still not ready — no routing GRAPH yet (the exact
        // P634 state: some world data arrived, but CLUSTERINFO hasn't landed).
        world.Apply(new ExportCluster { Cluster = MakeCluster(742) });
        Assert.False(world.WorldReady, "clusters without the graph must not be WorldReady");

        // Graph arrives → world complete.
        world.Apply(new ClusterInfo
        {
            Clusters = [new ClusterInfo.ClusterEntry(742, HomeSector: true)],
            Warps = [],
        });
        Assert.True(world.WorldReady, "graph + clusters must make the world WorldReady");

        // Reset tears it down (a later game-over / re-join begins unworlded again).
        world.Reset();
        Assert.False(world.WorldReady, "Reset must clear WorldReady");
    }
}
