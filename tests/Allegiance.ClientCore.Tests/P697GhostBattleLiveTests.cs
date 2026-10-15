using System.Diagnostics;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P697GhostBattleLiveTests
{
    private static ServerConfig Config(bool native = true, double floor = 2, bool tiers = false) => new()
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
        Port = 0, UpdatesPerSecond = 20,
        Broadcast = new() { ShipUpdateModel = native ? "native" : "port", MinUpdateHz = floor,
            InterestTiers = tiers, ImmutableSnapshot = tiers, EncodeOnce = tiers, ParallelBuild = tiers },
        Games = [new() { Name = "P697", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static void Pump(Srv server, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed.TotalSeconds < 10) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "loopback condition must arrive within 10 seconds");
    }

    private static ConnectionSession Join(Srv server, string name = "p697-viewer")
    {
        var client = new ConnectionSession();
        client.Connect(server.BoundPort, name);
        client.SendLogonRequest(new LogonRequest { CharacterName = name, ZoneTicket = [1],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 697, DwCookie = 697, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = name == "p697-viewer" ? 99000000000000697ul : 99000000000000698ul, DrmHash = "P697" });
        Pump(server, () => { lock (client.Gate) return client.World.MyShipId is not null; });
        return client;
    }

    private static void SpawnViewer(Srv server) => server.ShipSpawner = (core, mission, id) =>
    {
        var ship = new Ship(id, core.HullTypes.Values.First(h => h.MaxEnergy > 0 && h.MaxAmmo > 0),
            new Vector3(0, 10000, 0), Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return (ship, mission.Sectors[0]);
    };

    private static Ship Subject(Srv server, short id, short side, int index)
    {
        var mission = server.TestMission!;
        var hull = mission.Core.HullTypes.Values.First(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0);
        var ship = new Ship(id, hull, new Vector3(20000 + index * 200, 10000, 0),
            Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    [SkippableTheory]
    [InlineData(true, 2, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 2, true)]
    [Trait("Requires", "Artwork")]
    public void Crowded_fight_orders_every_source_before_fire_even_outside_detection(bool native, double floor, bool tiers)
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        server.Boot(Config(native, floor, tiers));
        server.TestMission!.Stage = MissionStage.Started;
        using var client = Join(server);
        var viewer = server.Ships.Single().Ship;
        var side = (short)(1 - viewer.SideId!.Value);
        var idle = Enumerable.Range(0, 32).Select(i => Subject(server, (short)(18000 + i), side, i)).ToArray();
        // Deliberately outside detection: the floor must match the existing
        // sector pose/identity rule, rather than leaving these to ranked luck.
        for (int i = 0; i < 22; i++) server.Tick();
        Assert.Contains(idle, s => !s.SeenBySide(viewer.SideId.Value));
        if (floor > 0)
        {
            Pump(server, () => { lock (client.Gate) return idle.All(s =>
                client.World.Ships.TryGetValue(s.Id, out var e) && e.Latest is not null); });
        }
        var core = server.TestMission.Core;
        var weapon = core.PartTypes.Values.Where(p => p.Type is DataPartTypeIGC.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type).First(w =>
                w.Dispersion == 0 && core.ProjectileTypes[w.ProjectileTypeId].Power > 0 && w.EnergyPerShot < 100);
        var shooters = Enumerable.Range(0, 32).Select(i => Subject(server, (short)(18100 + i), side, i + 32)).ToArray();
        foreach (var ship in shooters)
        {
            ship.MountWeapon(weapon);
            ship.StateM = ShipControlState.AllWeapons;
            ship.Energy = 1_000_000;
            ship.Ammo = 30000;
        }
        // These ships fire on their first sim update, before the normal export
        // sweep or any pose warmup. Both the sim and wire paths are real.
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 2) server.Tick();
        Pump(server, () => { lock (client.Gate) return shooters.All(s =>
            client.World.Projectiles.Any(p => p.LauncherId == s.Id)); });
        lock (client.Gate)
        {
            var world = client.World;
            Assert.True(world.Diag.ReceivedEnemy >= 32);
            Assert.Equal(0, world.Diag.FromNeverPosed);
            Assert.Equal(0, world.Diag.FromStale);
            Assert.All(shooters, s =>
            {
                Assert.Equal(s.HullType.HullId, world.Ships[s.Id].HullId);
                Assert.Equal(viewer.Sector!.Id, world.Ships[s.Id].SectorId);
                Assert.False(world.Ships[s.Id].IsNotVisible);
            });
            Console.WriteLine(world.Diag.FormatLine());
        }
        Assert.Contains(shooters, s => !s.SeenBySide(viewer.SideId.Value));
        string metric = server.TakeFireAudienceReport();
        Assert.Contains("neverposed=0", metric);
        Assert.DoesNotContain("initialPoses=0 ", metric);
        Assert.DoesNotContain("refreshPoses=0 ", metric);
        Console.WriteLine(metric);
    }

    [SkippableTheory]
    [InlineData("missile")]
    [InlineData("hit")]
    [InlineData("mine")]
    [InlineData("probe")]
    [Trait("Requires", "Artwork")]
    public void First_emission_from_unexported_npc_orders_identity_sector_and_pose(string kind)
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        server.Boot(Config());
        using var client = Join(server);
        var viewer = server.Ships.Single().Ship;
        var source = Subject(server, 19000, viewer.SideId!.Value, 0);
        var sector = source.Sector!;
        var core = server.TestMission!.Core;
        // Observe the actual guaranteed publishing order, with no intervening
        // tick to supply identity/pose from the regular broadcast suffix.
        var sent = new List<byte[]>();
        server.TestPublicationCapture = (_, bytes, key, guaranteed) =>
        { Assert.Equal(0, key); Assert.True(guaranteed); sent.AddRange(FedPacket.Unpack(bytes)); };
        ushort expected;
        Probe? deployedProbe = null;
        Mine? deployedMine = null;
        switch (kind)
        {
            case "missile": source.FireMissile(core.MissileTypes.Values.First(), 0); expected = FireMissile.Fmid; break;
            case "mine": deployedMine = source.DeployMine(core.MineTypes.Values.First(), 0); expected = ExportMine.Fmid; break;
            case "probe": deployedProbe = source.DeployProbe(core.ProbeTypes.Values.First(), 0); expected = ExportProbe.Fmid; break;
            default:
                var p = new Projectile(19001, sector, core.ProjectileTypes.Values.First(), source.Position,
                    Vector3.Zero, new Vector3(0, 0, -1), 0, source);
                server.Host.ProjectileHitEvent(p, source.Position, Vector3.Zero, 1, viewer.Id, sector.Id, false);
                expected = ProjectileHullHit.Fmid;
                break;
        }
        server.TestPublicationCapture = null;
        Assert.True(sent.Count >= 4);
        Assert.Equal(source.Id, ExportShip.Decode(sent[0]).ShipId);
        Assert.Equal(sector.Id, ShipStatusMessage.Decode(sent[1]).SectorId);
        Assert.Equal(source.Id, SingleShipUpdate.Decode(sent[2]).OidTarget);
        Assert.Equal(expected, (ushort)(sent[3][2] | sent[3][3] << 8));
        Assert.Contains("sent=1 neverposed=0 initialPoses=1", server.TakeFireAudienceReport());
        Assert.Contains("sent=0 neverposed=0 initialPoses=0", server.TakeFireAudienceReport());
        if (deployedProbe is not null || deployedMine is not null)
        {
            // An unconnected NPC's static object must be removed for everyone
            // who learned it, even when the historical deployer has changed side.
            source.SideId = (short)(1 - viewer.SideId!.Value);
            sent.Clear();
            server.TestPublicationCapture = (_, bytes, _, _) => sent.AddRange(FedPacket.Unpack(bytes));
            if (deployedProbe is not null) server.Host.ProbeDestroyedEvent(deployedProbe);
            else server.Host.MineDestroyedEvent(deployedMine!);
            server.TestPublicationCapture = null;
            if (deployedProbe is not null)
                Assert.Equal(deployedProbe.Id, ProbeDestroyed.Decode(Assert.Single(sent)).ProbeId);
            else Assert.Equal(deployedMine!.Id, MineDestroyed.Decode(Assert.Single(sent)).MineId);
        }
    }
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Cloak_hidden_chaff_is_not_relayed_and_decloak_bootstraps_the_source()
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        server.Boot(Config());
        server.TestMission!.Stage = MissionStage.Started;
        using var viewerClient = Join(server);
        var viewer = server.Ships.Single().Ship;
        using var sourceClient = Join(server, "p697-source");
        var source = server.Ships.Single(s => s.Ship != viewer).Ship;
        source.SideId = (short)(1 - viewer.SideId!.Value);
        source.Position = new Vector3(20000, 10000, 0);
        var core = server.TestMission.Core;
        var cloak = core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Cloak);
        var chaffType = core.ChaffTypes.Values.First(t => t.Lifespan >= 2);
        var chaff = new DataPartTypeIGC(Price: 0, TimeToBuild: 0, ModelName: "", IconName: "",
            Name: "P697 chaff", Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
            Mass: 0, Signature: 0, PartId: 697, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ChaffLauncher(chaffType.ExpendableTypeId));
        source.ChangeHull(core.HullTypes.Values.First(h => h.MaxEnergy > 0
            && h.CapacityChaffLauncher >= 3 * chaffType.LauncherExpendableSize));
        source.MountPart(cloak, 1, 0, 255);
        var dispenser = source.MountPart(chaff, 3, 0, 255);
        Assert.NotNull(dispenser);
        source.Energy = 1_000_000;
        source.StateM = ShipControlState.CloakActive;
        Pump(server, () => source.IsCloaked && !source.SeenBySide(viewer.SideId.Value));
        var viewerPeer = server.TestSnapshotPeers().Single(p => p.Ship == viewer).Conn;
        var seen = new List<ushort>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        {
            if (peer != viewerPeer) return;
            foreach (var message in FedPacket.Unpack(bytes)) seen.Add((ushort)(message[2] | message[3] << 8));
        };
        Assert.Equal(3, dispenser.Amount);
        Assert.Equal(MissionStage.Started, source.Mission!.Stage);
        Assert.Null(source.StationId);
        Assert.Same(source, server.TestSnapshotPeers().Single(p => p.Ship.Id == sourceClient.World.MyShipId).Ship);
        sourceClient.SendFireExpendable(EquipmentType.ChaffLauncher);
        Pump(server, () => source.Parts.Any(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.Amount == 2));
        Assert.DoesNotContain(FireExpendable.Fmid, seen);
        Assert.DoesNotContain(CreateChaff.Fmid, seen);
        source.StateM = 0;
        Pump(server, () => !source.IsCloaked);
        seen.Clear();
        server.TakeFireAudienceReport();
        sourceClient.SendFireExpendable(EquipmentType.ChaffLauncher);
        Pump(server, () => source.Parts.Any(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.Amount == 1));
        server.TestPublicationCapture = null;
        int emission = seen.IndexOf(FireExpendable.Fmid);
        Assert.True(emission > 0);
        Assert.Contains(SingleShipUpdate.Fmid, seen.Take(emission));
        Assert.Contains(CreateChaff.Fmid, seen);
        Assert.Contains("neverposed=0", server.TakeFireAudienceReport());
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Late_missile_tracking_establishes_source_and_cleanup_survives_launcher_warp()
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        server.Boot(Config());
        // Keep simulation paused during the asynchronous join handshake so
        // the real missile does not expire while TickSim advances the clock.
        server.TestMission!.Stage = MissionStage.NotStarted;
        // Launch before any viewer exists: only tracking can hydrate it later.
        var source = Subject(server, 19100, 1, 0);
        var type = server.TestMission.Core.MissileTypes.Values.OrderByDescending(t => t.Lifespan).First();
        var missile = source.FireMissile(type, server.TestMission.LastUpdate);
        using var client = Join(server);
        Pump(server, () => { lock (client.Gate) return client.World.Missiles.ContainsKey(missile.Id); });
        lock (client.Gate) Assert.NotNull(client.World.Ships[source.Id].Latest);
        // Changing the source's sector must not erase its known missile's
        // destruction audience, or bootstrap it into the viewer's old sector.
        var oldSector = source.Sector!;
        oldSector.Remove(source);
        server.TestMission.PlaceShip(source, server.TestSectors[1]);
        missile.Position += new Vector3(123, 0, 0);
        Pump(server, () => { lock (client.Gate) return
            client.World.Missiles[missile.Id].Position == missile.Position; });
        server.Host.MissileDestroyedEvent(missile);
        Pump(server, () => { lock (client.Gate) return !client.World.Missiles.ContainsKey(missile.Id); });
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Independently_detected_mine_does_not_reveal_its_departed_deployer()
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        server.Boot(Config());
        server.TestMission!.Stage = MissionStage.Started;
        using var client = Join(server);
        var viewer = server.Ships.Single().Ship;
        var core = server.TestMission.Core;
        viewer.ChangeHull(core.HullTypes.Values.First(h => h.ModelName == "faohshcar"));
        var source = Subject(server, 19200, (short)(1 - viewer.SideId!.Value), 0);
        source.Position = viewer.Position + new Vector3(300, 0, 0);
        var mine = source.DeployMine(core.MineTypes.Values.OrderBy(t => t.MineRadius).First(),
            server.TestMission.LastUpdate);
        // It leaves before any identity/pose sweep; the mine is still a real,
        // independently detectable hazard in the old sector.
        source.Sector!.Remove(source);
        server.TestMission.PlaceShip(source, server.TestSectors[1]);
        Pump(server, () => { lock (client.Gate) return client.World.Mines.ContainsKey(mine.Id); });
        Assert.True(mine.SeenBySide(viewer.SideId.Value));
        lock (client.Gate) Assert.False(client.World.Ships.TryGetValue(source.Id, out var entity)
            && entity.Latest is not null);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Admin_camera_fire_follows_its_pose_sector_and_includes_its_visible_cloakers()
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        var config = Config();
        config.LiveSettings.AdminCheats = new() { Enabled = true, AllowNames = ["p697-viewer"] };
        server.Boot(config);
        server.TestMission!.Stage = MissionStage.Started;
        using var client = Join(server);
        var viewer = server.Ships.Single().Ship;
        var source = Subject(server, 19300, (short)(1 - viewer.SideId!.Value), 0);
        var targetSector = server.TestSectors[1];
        source.Sector!.Remove(source);
        server.TestMission.PlaceShip(source, targetSector);
        source.MountPart(server.TestMission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Cloak),
            1, 0, 255);
        source.Energy = 1_000_000;
        source.StateM = ShipControlState.CloakActive;
        Pump(server, () => source.IsCloaked && !source.SeenBySide(viewer.SideId.Value));
        client.SendViewClusterRequest(targetSector.Id);
        Pump(server, () => { lock (client.Gate) return client.World.Ships.TryGetValue(source.Id, out var e)
            && e.Latest is not null; });
        var outsideView = Subject(server, 19301, source.SideId!.Value, 1);
        var projectileType = server.TestMission.Core.ProjectileTypes.Values.First();
        server.Host.ProjectileCreatedEvent(new Projectile(19310, targetSector, projectileType,
            source.Position, Vector3.Zero, new Vector3(0, 0, -1), server.TestMission.LastUpdate, source));
        server.Host.ProjectileCreatedEvent(new Projectile(19311, outsideView.Sector!, projectileType,
            outsideView.Position, Vector3.Zero, new Vector3(0, 0, -1), server.TestMission.LastUpdate, outsideView));
        Pump(server, () => { lock (client.Gate) return client.World.Projectiles.Any(p => p.LauncherId == source.Id); });
        lock (client.Gate)
        {
            Assert.DoesNotContain(client.World.Projectiles, p => p.LauncherId == outsideView.Id);
            Assert.Equal(source.HullType.HullId, client.World.Ships[source.Id].HullId);
            Assert.Equal(0, client.World.Diag.FromNeverPosed);
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Static_emissions_and_cleanup_stay_in_the_owning_mission_when_side_ids_repeat()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config();
        config.Games = [config.Games[0], new() { Name = "P697 other", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["OtherAlpha", "OtherBravo"] }];
        server.Boot(config);
        var homeMission = server.Missions[0];
        var foreignMission = server.Missions[1];
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
        Ship Seat(short id, Mission mission, short side)
        {
            var s = new Ship(id, mission.Core.HullTypes.Values.First(), new Vector3(0, 10000, 0),
                Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(s, mission.Sectors[0]);
            return s;
        }
        var home = new ConnectionId(700);
        var foreignOwner = new ConnectionId(701);
        var foreignEnemy = new ConnectionId(702);
        var homeViewer = Seat(19500, homeMission, 0);
        var otherOwner = Seat(19501, foreignMission, 0);
        var otherEnemy = Seat(19502, foreignMission, 1);
        server.TestSeedPeers([(home, homeViewer, homeMission.Sectors[0]),
            (foreignOwner, otherOwner, foreignMission.Sectors[0]),
            (foreignEnemy, otherEnemy, foreignMission.Sectors[0])]);
        var source = Subject(server, 19510, 0, 0);
        var sent = new List<(ConnectionId Peer, byte[] Message)>();
        server.TestPublicationCapture = (peer, bytes, _, _) =>
        { foreach (var message in FedPacket.Unpack(bytes)) sent.Add((peer, message)); };
        var mine = source.DeployMine(homeMission.Core.MineTypes.Values.First(), 0);
        var probe = source.DeployProbe(homeMission.Core.ProbeTypes.Values.First(), 0);
        static bool Hazard(byte[] message) => (message[2] | message[3] << 8) == ExportMine.Fmid
            && (BitConverter.ToInt32(message, 8) == ExportMine.OtMine
                || BitConverter.ToInt32(message, 8) == ExportProbe.OtProbe);
        Assert.Equal(2, sent.Count(s => s.Peer == home && Hazard(s.Message)));
        Assert.DoesNotContain(sent, s => s.Peer != home && Hazard(s.Message));
        sent.Clear();
        mine.SideVisibility.SetSideVisibility(1, true);
        server.TestProjectRecordedTick(1, snapshot: false);
        Assert.DoesNotContain(sent, s => s.Peer != home && Hazard(s.Message));
        // Ownership remains with these static objects after their parent moves
        // into the other game and changes side.
        source.Sector!.Remove(source);
        foreignMission.PlaceShip(source, foreignMission.Sectors[0]);
        source.SideId = 1;
        sent.Clear();
        server.Host.MineDestroyedEvent(mine);
        server.Host.ProbeDestroyedEvent(probe);
        server.TestPublicationCapture = null;
        Assert.Equal(2, sent.Count(s => s.Peer == home));
        Assert.DoesNotContain(sent, s => s.Peer != home);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Armed_probe_keeps_its_own_source_pose_after_the_deployer_leaves()
    {
        Require.Artwork();
        using var server = new Srv();
        SpawnViewer(server);
        server.Boot(Config());
        server.TestMission!.Stage = MissionStage.Started;
        using var client = Join(server);
        var viewer = server.Ships.Single().Ship;
        var source = Subject(server, 19600, viewer.SideId!.Value, 0);
        source.Position = viewer.Position + new Vector3(2000, 0, 0);
        var mission = server.TestMission;
        var probe = source.DeployProbe(mission.Core.ProbeTypes[30], mission.LastUpdate);
        var enemy = Subject(server, 19601, (short)(1 - viewer.SideId.Value), 0);
        enemy.Position = probe.Position + new Vector3(0, 0, 100);
        enemy.Velocity = Vector3.Zero;
        probe.Target = enemy;
        source.Sector!.Remove(source);
        mission.PlaceShip(source, mission.Sectors[1]);
        Pump(server, () => { lock (client.Gate) return client.World.Projectiles.Any(p =>
            p.LauncherType == IgcObjectType.Probe && p.LauncherId == probe.Id); });
        lock (client.Gate)
        {
            Assert.Contains(probe.Id, client.World.Probes.Keys);
            Assert.Equal(probe.Position, client.World.Probes[probe.Id].Position);
            Assert.Equal(0, client.World.Diag.FromNeverPosed);
            Assert.Equal(0, client.World.Diag.FromStale);
        }
        Assert.True(probe.Ammo < 5);
        Assert.Contains("neverposed=0", server.TakeFireAudienceReport());
    }

}
