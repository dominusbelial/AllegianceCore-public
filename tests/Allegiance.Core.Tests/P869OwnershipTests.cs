using System.Collections;
using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.Session;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P869OwnershipTests
{
    private static StaticCore LoadCore() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
    private static MissionMap LoadMap() => MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
    private static Mission World(IIgcSite? site = null, StaticCore? core = null) => Mission.Assemble(core ?? LoadCore(), LoadMap(),
        [new(0, "Alpha", 1), new(1, "Bravo", 3)], site ?? new ServerHost());
    private static object New(string kind, Mission m, short id) => kind switch
    {
        "Warp" => new ResolvedWarp(m.Warps[0].Warp with { WarpId = id }, m.Sectors[0]),
        "Station" => new Station(m.Sectors.SelectMany(s => s.StationRuntimes).First().Data with { StationId = id }, m.Core.StationTypes.Values.First()),
        "Asteroid" => new Asteroid(DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with { AsteroidId = id, Position = [0, 0, 0] }),
        "Treasure" => new Treasure(id, m.Sectors[0], DataTreasureIGC.Decode(new byte[DataTreasureIGC.Size]) with { Lifespan = 1000 }, 0, m),
        "Probe" => new Probe(id, m.Sectors[0], m.Core.ProbeTypes.Values.First(), Vector3.Zero, 0, 0),
        "Mine" => new Mine(id, m.Sectors[0], m.Core.MineTypes.Values.First(), Vector3.Zero, 0),
        "Side" => new Side(id, "Registry", m.Core.Civilizations.Values.First()),
        "Cluster" => new Sector(m.Sectors[0].Cluster with { ClusterId = id }),
        "Ship" => new Ship(id, m.Core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
        "Buoy" => new Buoy(new([0, 0, 0], m.Sectors[0].Id, 0, id, false)),
        _ => throw new ArgumentException(kind)
    };
    private static object? Invoke(Mission m, string method, params object[] args) => typeof(Mission).GetMethod(method)!.Invoke(m, args);

    // Every Add/Delete/Get/GetList row SIM-0704..0744 (excluding GetSeenSides).
    // Native independent contract: Igc/missionigc.cpp:3506-3652,3683-3732;
    // Igc/igc.h:5130-5197. Equal IDs are intentionally different identities.
    [SkippableTheory]
    [InlineData("Warp")]
    [InlineData("Station")]
    [InlineData("Treasure")]
    [InlineData("Asteroid")]
    [InlineData("Probe")]
    [InlineData("Mine")]
    [InlineData("Side")]
    [InlineData("Cluster")]
    [InlineData("Ship")]
    [InlineData("Buoy")]
    public void Registry_keeps_order_duplicates_first_identity_deletion_and_a_live_view(string kind)
    {
        Require.Artwork(); using var m = World();
        string plural = kind + "s";
        var view = (IEnumerable)Invoke(m, "Get" + plural)!;
        object a = New(kind, m, 30001), b = New(kind, m, 30001), c = New(kind, m, 30000);
        object absent = New(kind, m, 30001);
        object[] original = view.Cast<object>().ToArray();
        Invoke(m, "Add" + kind, a); Invoke(m, "Add" + kind, b);
        Invoke(m, "Add" + kind, a); Invoke(m, "Add" + kind, c);
        Assert.Equal(original.Concat(new[] { a, b, a, c }), view.Cast<object>());
        Assert.Same(a, Invoke(m, "Get" + kind, (short)30001));
        Invoke(m, "Delete" + kind, absent); // same ID must not remove anything
        Assert.Equal(original.Length + 4, view.Cast<object>().Count());
        Invoke(m, "Delete" + kind, a);
        Assert.Equal(original.Concat(new[] { b, a, c }), view.Cast<object>());
        Assert.Same(b, Invoke(m, "Get" + kind, (short)30001));
        Invoke(m, "Delete" + kind, b);
        Assert.Same(a, Invoke(m, "Get" + kind, (short)30001));
        Invoke(m, "Delete" + kind, a);
        Assert.Null(Invoke(m, "Get" + kind, (short)30001));
        Assert.Same(view, Invoke(m, "Get" + plural));
        Assert.Same(c, Invoke(m, "Get" + kind, (short)30000));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<Ship> RegisterWithoutExternalOwner(Mission mission)
    {
        var ship = (Ship)New("Ship", mission, 30001);
        mission.AddShip(ship);
        return new WeakReference<Ship>(ship);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool Alive(WeakReference<Ship> reference) => reference.TryGetTarget(out _);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void DeleteWeak(Mission mission, WeakReference<Ship> reference)
    {
        Assert.True(reference.TryGetTarget(out var ship));
        mission.DeleteShip(ship);
    }

    [SkippableFact]
    public void Registry_is_a_strong_owner_and_site_metadata_does_not_retain_removed_models()
    {
        Require.Artwork(); using var m = World();
        var weak = RegisterWithoutExternalOwner(m);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(Alive(weak)); // native AddRef keeps the registry member alive
        DeleteWeak(m, weak);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(Alive(weak)); // native Release relinquishes that ownership
    }

    [SkippableFact]
    public void Deleting_a_registry_reference_preserves_the_site_while_the_cluster_still_owns_the_model()
    {
        Require.Artwork(); using var m = World();
        var ship = (Ship)New("Ship", m, 30001); m.PlaceShip(ship, m.Sectors[0]);
        var site = m.GetThingSite(ship);
        m.DeleteShip(ship);
        Assert.Null(m.GetShip(ship.Id));
        Assert.Contains(ship, m.Sectors[0].Models);
        Assert.Same(site, m.GetThingSite(ship));
        m.AddShip(ship); m.Sectors[0].Terminate(ship);
        Assert.Null(m.GetThingSite(ship));
    }

    // Native AddSide/GetSide special case: missionigc.cpp:3608-3648.
    [SkippableFact]
    public void Team_lobby_is_separate_active_white_identity_with_no_tech_or_score()
    {
        Require.Artwork(); using var m = World();
        var lobby = Assert.IsType<Side>(m.GetSide(-2));
        Assert.Equal("Team Lobby", lobby.Name);
        Assert.Equal(m.Core.Civilizations.Values.First().CivilizationId, lobby.Civilization.CivilizationId);
        Assert.True(lobby.Active); Assert.Equal(1f, lobby.ColorR); Assert.Equal(1f, lobby.ColorG); Assert.Equal(1f, lobby.ColorB);
        Assert.Equal(GlobalAttributeSet.Identity, lobby.GlobalAttributes);
        Assert.Equal(TechTreeBitMask.Empty, lobby.DevelopmentTechs); Assert.Equal(TechTreeBitMask.Empty, lobby.InitialTechs);
        Assert.Null(lobby.SquadId); Assert.Null(lobby.AllyGroup);
        Assert.Equal(0, lobby.ConquestPercent); Assert.Equal(0, lobby.TerritoryCount);
        Assert.Equal(0, lobby.Kills); Assert.Equal(0, lobby.Deaths); Assert.Empty(lobby.Buckets);
        Assert.DoesNotContain(lobby, m.Sides);
        m.DeleteSide(m.Sides[0]); Assert.Same(lobby, m.GetSide(-2));
        Assert.Null(m.GetSide(0)); Assert.Null(m.GetSide(123));
    }

    // Real placement, sector transfer/docking and final retirement distinguish
    // mission ownership from cluster membership, native shipIGC.cpp:154,202.
    [SkippableFact]
    public void Runtime_registration_survives_cluster_removal_and_retires_on_termination()
    {
        Require.Artwork(); using var m = World();
        var ship = (Ship)New("Ship", m, 30001);
        var first = m.Sectors[0]; var second = m.Sectors[1];
        m.PlaceShip(ship, first);
        Assert.Same(ship, m.GetShip(ship.Id));
        var site = m.GetThingSite(ship);
        first.Remove(ship); Assert.Same(ship, m.GetShip(ship.Id)); // docked/in transit
        m.PlaceShip(ship, second);
        Assert.Single(m.GetShips()); Assert.Same(site, m.GetThingSite(ship));
        second.Terminate(ship);
        Assert.Null(m.GetShip(ship.Id)); Assert.Null(m.GetThingSite(ship));
        Assert.NotEmpty(m.GetStations());
        var station = m.GetStations()[0];
        m.Sectors.First(s => s.StationRuntimes.Contains(station)).RemoveStation(station);
        Assert.Null(m.GetStation(station.StationId));
        var warp = m.Warps[0];
        m.GetCluster(warp.Warp.ClusterId)!.RemoveWarp(warp);
        Assert.Null(m.GetWarp(warp.Warp.WarpId));
    }

    // SIM-0170/SIM-0634. Full ThingSiteImpl constructor is only flags +
    // owner; scanner identity is registered by cluster membership, not ctor.
    // FedSrv.CPP:10692-10709,11172-11226; modelIGC.cpp:117-118.
    [SkippableFact]
    public void Thing_sites_share_real_visibility_and_do_not_reset_it_on_cluster_moves()
    {
        Require.Artwork(); using var m = World(new ServerHost());
        var ship = (Ship)New("Ship", m, 30001); m.PlaceShip(ship, m.Sectors[0]);
        var site = Assert.IsType<ThingSite>(m.GetThingSite(ship));
        Assert.Same(ship, site.Owner); Assert.Same(ship.SideVisibility, site.Visibility);
        Assert.True(site.TracksSideVisibility); Assert.False(site.Predictable);
        Assert.False(site.Visibility!.SeenBySide(1)); Assert.False(site.Visibility.GetCurrentEye(1));
        Assert.Equal(((short)-1, (short)-1), site.Visibility.LastSpotterFor(1));
        ship.SideVisibility.SetSideVisibility(1, true);
        Assert.True(site.Visibility.GetCurrentEye(1));
        var replacement = new ServerHost().CreateThingSite(ship);
        Assert.Same(ship, replacement.Owner);
        Assert.NotSame(site.Visibility, replacement.Visibility);
        Assert.False(replacement.Visibility!.SeenBySide(1));
        Assert.False(replacement.Visibility.GetCurrentEye(1));
        Assert.Equal(((short)-1, (short)-1), replacement.Visibility.LastSpotterFor(1));
        Assert.True(site.Visibility.GetCurrentEye(1));
        var rock = (Asteroid)New("Asteroid", m, 30002); m.PlaceAsteroid(rock, m.Sectors[0]);
        var rockSite = Assert.IsType<ThingSite>(m.GetThingSite(rock));
        Assert.True(rockSite.Predictable); Assert.True(rockSite.TracksSideVisibility);
        rock.SideVisibility.SetSideVisibility(1, true); rock.SideVisibility.SetSideVisibility(1, false);
        Assert.True(rockSite.Visibility!.SeenBySide(1)); Assert.False(rockSite.Visibility.GetCurrentEye(1));
        var buoy = (Buoy)New("Buoy", m, 30003); m.AddBuoy(buoy);
        Assert.False(m.GetThingSite(buoy)!.TracksSideVisibility);
        Assert.Same(site, m.GetThingSite(ship));
    }

    [SkippableFact]
    public void Ordnance_site_flags_follow_native_constructor_attributes()
    {
        Require.Artwork(); using var m = World(); var sector = m.Sectors[0];
        var missile = new Missile(30001, sector, m.Core.MissileTypes.Values.First(), Vector3.Zero,
            Vector3.Zero, new Vector3(0, 0, 1), 0);
        var chaff = new Chaff(30002, sector, m.Core.ChaffTypes.Values.First(), Vector3.Zero, Vector3.Zero, 0);
        sector.Add(missile); sector.Add(chaff);
        // Igc/missileigc.cpp:51,67: SeenBySide, not Predictable.
        Assert.True(m.GetThingSite(missile)!.TracksSideVisibility);
        Assert.False(m.GetThingSite(missile)!.Predictable);
        // Igc/chaffigc.cpp:46: Predictable, not SeenBySide.
        Assert.False(m.GetThingSite(chaff)!.TracksSideVisibility);
        Assert.True(m.GetThingSite(chaff)!.Predictable);
        sector.Terminate(missile); sector.Terminate(chaff);
        Assert.Null(m.GetThingSite(missile)); Assert.Null(m.GetThingSite(chaff));
    }

    [SkippableFact]
    public void Goto_buoy_is_mission_owned_until_its_command_is_cleared()
    {
        Require.Artwork(); using var m = World();
        var ship = (Ship)New("Ship", m, 30001); m.PlaceShip(ship, m.Sectors[0]);
        ship.SetGoto(new GotoOrder(GotoKind.Cluster, m.Sectors[1].Id, new Vector3(1, 2, 3)));
        var first = Assert.Single(m.GetBuoys());
        Assert.Same(first, m.GetBuoy(first.Id)); Assert.Equal(new Vector3(1, 2, 3), first.Position);
        ship.SetGoto(new GotoOrder(GotoKind.Waypoint, m.Sectors[0].Id, Vector3.Zero));
        var second = Assert.Single(m.GetBuoys()); Assert.NotEqual(first.Id, second.Id);
        Assert.Null(m.GetBuoy(first.Id));
        ship.SetGoto(null); Assert.Empty(m.GetBuoys()); Assert.Null(m.GetThingSite(second));
    }

    // SIM-0695/SIM-0788. Native cache hits precede IO and version-only reads
    // never acquire consumers: missionigc.cpp:2390-2431,3033-3053.
    [SkippableFact]
    public void Named_core_cache_reads_version_only_shares_and_evicts_at_last_consumer()
    {
        Require.Artwork();
        string folder = Path.Combine(Path.Combine(Environment.CurrentDirectory, ".scratch"), "p869-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "core.igc");
            byte[] raw = File.ReadAllBytes(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
            File.WriteAllBytes(path, raw[..4]); // version-only must not parse body
            var cache = new StaticCoreCache();
            int version = cache.ReadVersion("Core", path);
            Assert.Equal(BitConverter.ToInt32(raw), version);
            File.WriteAllBytes(path, raw);
            using var a = cache.Load("Core", path);
            using var b = cache.Load("CORE", "does-not-exist.igc");
            Assert.Same(a.Core, b.Core);
            File.Delete(path);
            Assert.Equal(version, cache.ReadVersion("core", path));
            a.Dispose(); a.Dispose(); // post-decrement only once
            Assert.Same(b.Core, cache.Load("core", path).DisposeAndReturnCore());
            b.Dispose();
            Assert.Throws<FileNotFoundException>(() => cache.Load("core", path));
            Assert.Equal(-1, cache.ReadVersion("missing", path));
            File.WriteAllBytes(path, raw);
            using var replacement = cache.Load("core", path);
            Assert.NotSame(a.Core, replacement.Core);
            Assert.Equal(version, replacement.Core.Version);
            Assert.NotEmpty(a.Core.HullTypes); // retained immutable data remains safe
        }
        finally { Directory.Delete(folder, true); }
    }

    [SkippableFact]
    public void Missions_retain_shared_core_until_last_dispose_and_disposed_world_stops_callbacks()
    {
        Require.Artwork(); string name = "p869-" + Guid.NewGuid().ToString("N");
        using var lease = StaticCoreCache.Shared.Load(name, Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        var site = new RecordingIgcSite();
        using var a = World(site, lease.Core); using var b = World(core: lease.Core);
        lease.Dispose(); a.Dispose(); site.Reset();
        a.Update(1); Assert.Empty(site.Calls);
        using (var retained = StaticCoreCache.Shared.Load(name.ToUpperInvariant(), "missing.igc")) Assert.Same(b.Core, retained.Core);
        b.Dispose();
        Assert.Throws<FileNotFoundException>(() => StaticCoreCache.Shared.Load(name, "missing.igc"));
    }

    [SkippableFact]
    public void Server_boot_reuses_case_insensitive_cached_core_before_file_existence_checks()
    {
        Require.Artwork();
        string folder = Path.Combine(Path.Combine(Environment.CurrentDirectory, ".scratch"), "p869-boot-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "native.igc");
            File.Copy(Path.Combine(TestArtRoot.Value, "PCore001.igc"), path);
            File.Copy(Path.Combine(TestArtRoot.Value, "XXV_simple.igc"), Path.Combine(folder, "XXV_simple.igc"));
            var config = UpdateCadenceTests.Config(); config.ArtPath = folder; config.StaticCore = "native.igc";
            using var a = new Srv { SpawnInitialMinersAtBoot = false }; a.Boot(config);
            var core = a.Missions[0].Core;
            File.Delete(path);
            var upper = UpdateCadenceTests.Config(); upper.ArtPath = folder; upper.StaticCore = "NATIVE.IGC";
            using var b = new Srv { SpawnInitialMinersAtBoot = false }; b.Boot(upper);
            Assert.Same(core, b.Missions[0].Core);
            a.Shutdown(); Assert.NotEmpty(b.Missions[0].Core.HullTypes);
            b.Shutdown();
            using var c = new Srv { SpawnInitialMinersAtBoot = false };
            Assert.Throws<InvalidDataException>(() => c.Boot(config));
        }
        finally { Directory.Delete(folder, true); }
    }

    [SkippableFact]
    public void Client_world_reset_and_session_dispose_release_only_their_core_consumer()
    {
        Require.Artwork();
        string name = "p869-client-" + Guid.NewGuid().ToString("N");
        using var root = StaticCoreCache.Shared.Load(name, Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        using var a = new ConnectionSession(); using var b = new ConnectionSession();
        foreach (var session in new[] { a, b })
        {
            session.World.MissionCoreResolver = _ => root.Core;
            session.World.Apply(new MissionDef { SzIGCStaticFile = "PCore001" });
            session.World.Apply(new JoinedMission { ShipId = 1 });
            Assert.Same(root.Core, session.World.MissionCore);
        }
        using var standalone = new ClientWorld { MissionCoreResolver = _ => root.Core };
        standalone.Apply(new MissionDef { SzIGCStaticFile = "PCore001" });
        standalone.Apply(new JoinedMission { ShipId = 2 });
        Assert.Same(root.Core, standalone.MissionCore);
        int version = root.Core.Version;
        root.Dispose(); standalone.Dispose(); a.World.Reset();
        Assert.Null(a.World.MissionCore);
        Assert.Equal(version, StaticCoreCache.Shared.ReadVersion(name, "missing.igc"));
        b.Dispose();
        Assert.Null(b.World.MissionCore);
        Assert.Equal(-1, StaticCoreCache.Shared.ReadVersion(name, "missing.igc"));
    }

    [Fact]
    public void Default_client_core_resolver_rejects_remote_directory_escape()
    {
        using var session = new ConnectionSession();
        session.World.Apply(new MissionDef { SzIGCStaticFile = "../PCore001" });
        session.World.Apply(new JoinedMission { ShipId = 1 });
        Assert.Null(session.World.MissionCore); Assert.NotNull(session.World.JoinError);
    }

    // SIM-0685 actual host shutdown: FedSrv.CPP:13976-13979.
    [SkippableFact]
    public void Shutdown_stops_callbacks_sends_and_releases_transport_and_core_ownership()
    {
        Require.Artwork();
        string folder = Path.Combine(Path.Combine(Environment.CurrentDirectory, ".scratch"), "p869-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(TestArtRoot.Value, "PCore001.igc"), Path.Combine(folder, "PCore001.igc"));
            File.Copy(Path.Combine(TestArtRoot.Value, "XXV_simple.igc"), Path.Combine(folder, "XXV_simple.igc"));
            var config = UpdateCadenceTests.Config(); config.ArtPath = folder;
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(config);
            var mission = server.Missions[0]; double last = mission.LastUpdate;
            int sends = 0; server._testSendCapture = (_, _) => sends++;
            server.Shutdown(); server.Shutdown(); server.TickSim(); server.Tick(); mission.Update(last + 1);
            Assert.True(mission.IsDisposed); Assert.Equal(last, mission.LastUpdate); Assert.Equal(0, sends);
            Assert.Empty(server.Missions); Assert.Empty(mission.GetShips()); Assert.Empty(mission.Sectors);
            server._testSendCapture = null; // exercise the direct lease span path
            server.TestPublicationSizeCapture = (_, _, _, _) => sends++;
            typeof(Srv).GetMethod("PublishMemory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [new ConnectionId(42), new ReadOnlyMemory<byte>(new byte[8]), 0, true]);
            Assert.Equal(0, sends);
            using var staleOwnership = StaticCoreCache.Shared.Retain(mission.Core);
            Assert.Null(staleOwnership); // unique cache name: no other test server owns this core
        }
        finally { Directory.Delete(folder, true); }
    }

    // SIM-0837: name IDs survive reconnects and server instances, while online
    // accounts remain in the verified-subject dictionary (FedSrv.CPP:74-82).
    [Fact]
    public void Standalone_name_ids_are_stable_case_sensitive_and_distinct()
    {
        string name = "P869-" + Guid.NewGuid().ToString("N");
        int first = StandaloneCharacterIds.Get(name);
        Assert.Equal(first, StandaloneCharacterIds.Get(name));
        Assert.NotEqual(first, StandaloneCharacterIds.Get(name.ToLowerInvariant()));
        Assert.NotEqual(first, StandaloneCharacterIds.Get(name + "x"));
    }

    [Fact]
    public void Static_map_wire_layout_catalog_replacement_and_selection_use_native_fields()
    {
        var info = new StaticMapInfo { Maps = [new("arena", "Arena", 2), new("six", "Six teams", 6)] };
        byte[] raw = info.Encode();
        Assert.Equal(96, raw.Length); Assert.Equal((byte)8, raw[4]); Assert.Equal((byte)88, raw[6]);
        Assert.Equal((byte)'a', raw[8]); Assert.Equal((byte)'A', raw[21]); Assert.Equal((byte)2, raw[48]);
        Assert.Equal(info.Maps, StaticMapInfo.Decode(raw).Maps);
        var world = new ClientWorld(); world.Apply(StaticMapInfo.Decode(raw));
        info.Maps[0] = new("changed", "Changed", 3);
        Assert.Equal("arena", world.StaticMaps[0].FileName);
        var parameters = new MissionParamsMsg { NTems = 2 };
        Assert.False(world.SelectStaticMap(parameters, "six"));
        Assert.True(world.SelectStaticMap(parameters, "arena")); Assert.Equal("arena", parameters.SzCustomMapFile);
        byte[] empty = new StaticMapInfo().Encode(); empty[4] = empty[5] = 0;
        world.Apply(StaticMapInfo.Decode(empty)); Assert.Empty(world.StaticMaps);
        Assert.False(world.SelectStaticMap(parameters, "arena"));
        raw[6] = 87; // native cbmaps/sizeof floors a partial trailing record
        Assert.Equal("arena", Assert.Single(StaticMapInfo.Decode(raw).Maps).FileName);
        raw[4] = 255; Assert.Throws<ArgumentException>(() => StaticMapInfo.Decode(raw));
    }
}

internal static class P869LeaseTestExtensions
{
    internal static StaticCore DisposeAndReturnCore(this StaticCoreCache.Lease lease)
    { var core = lease.Core; lease.Dispose(); return core; }
}
