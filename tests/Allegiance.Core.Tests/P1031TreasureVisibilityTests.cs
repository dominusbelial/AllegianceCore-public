using System.Collections;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Cfg = global::Allegiance.Server.ServerConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Gm = global::Allegiance.Server.GameConfig;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1031 — R1b finding F2: the treasure CREATION broadcasts ignored
/// <c>SeenBySide</c>, so a dropped part / death loot / ambient spawn was sent to
/// every peer in the sector and revealed an unseen enemy's exact position.
/// <para><b>Native rule.</b> Every <c>CtreasureIGC</c> is created with
/// <c>c_mtSeenBySide | c_mtPredictable</c> (<c>Igc/treasureIGC.cpp:167-177</c>,
/// and <c>:171-172</c> for <c>c_mtStatic</c>), and native has no creation
/// broadcast at all: <c>ThingSiteImpl::UpdateSideVisibility</c> exports the
/// treasure to a side the first time that side's scanner sees it
/// (<c>ShowObject</c>, <c>FedSrv/FedSrv.CPP:10750-10778</c>), and a joining
/// player receives only <c>ptlink-&gt;data()-&gt;SeenBySide(pside)</c> treasures
/// (<c>FedSrv/fsmission.cpp:4224-4231</c>).</para>
/// <para>These are the wire proofs. The live drop test drives the real inbound
/// <c>FM_CS_DROP_PART(33)</c> dispatcher over loopback TCP; the other two drive
/// the same private creation/snapshot senders the treasure spawn sites call.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P1031TreasureVisibilityTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple, measured in the sibling live tests: 1052 = the fly sector
    // with no station (P111/P123/P1029).
    private const short FlySector = 1052;
    private const short ObserverSide = 0;
    private const short EnemySide = 1;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P1031 Treasure Visibility", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 1031u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9310000000000000000ul, DrmHash = "p1031",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short OneRealWeapon(Srv server) =>
        server.Missions[0].Core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .OrderBy(p => p.PartId)
            .Select(p => p.PartId)
            .First();

    /// <summary>The <c>_peerShips</c> connection owning <paramref name="ship"/>
    /// (the port's "is this a player ship" key), the P1029 reflection seam.</summary>
    private static ConnectionId ConnOf(Srv server, Ship ship)
    {
        var peers = (IDictionary)typeof(Srv)
            .GetField("_peerShips", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        foreach (DictionaryEntry entry in peers)
        {
            var live = entry.Value!;
            if (ReferenceEquals(live.GetType().GetProperty("Ship")!.GetValue(live), ship))
                return (ConnectionId)entry.Key!;
        }
        throw new InvalidOperationException($"ship {ship.Id} has no peer connection");
    }

    private static void Call(Srv server, string method, params object?[] args) =>
        typeof(Srv).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server, args);

    /// <summary>Three seated bots in the station-less fly sector: one observer on
    /// <see cref="ObserverSide"/>, and a dropper + same-side mate on
    /// <see cref="EnemySide"/>, physically separated so neither side has a
    /// scanner in range of the other's drops.</summary>
    private sealed class Scene : IDisposable
    {
        public Srv Server = null!;
        public readonly FltBot[] Bots = new FltBot[3];
        public Ship Observer = null!, Dropper = null!, Mate = null!;
        public Sector Sector = null!;
        public long Tick;   // test-side tick counter for the capture callback

        public FltBot ObserverBot => Bots[0];
        public FltBot DropperBot => Bots[1];
        public FltBot MateBot => Bots[2];

        public static Scene Boot()
        {
            var scene = new Scene();
            var server = new Srv { SpawnInitialMinersAtBoot = false };
            scene.Server = server;
            int n = 0;
            server.ShipSpawner = (core, mission, shipId) =>
            {
                var fly = mission.Sectors.First(s => s.Id == FlySector);
                // A real core hull with a working scanner: the creation broadcast
                // below is a visibility question, and the owner's own ship is the
                // scanner that must see its drop (treasureIGC.cpp:167-177).
                var hull = core.HullTypes.Values
                    .Where(h => h.ScannerRange > 0f && h.ScannerRange <= 1200f)
                    .OrderBy(h => h.HullId)
                    .First();
                // 6 km separation: decisively outside even the widest real scanner
                // range in this core, and still inside the universe damage bound.
                Vector3 position = n == 0
                    ? new Vector3(288f, 773f, 867f)
                    : new Vector3(6288f, 773f, 867f) + new Vector3(n * 60f, 0f, 0f);
                short side = n == 0 ? ObserverSide : EnemySide;
                var ship = new Ship(shipId, hull, position, Vector3.Zero, Orientation.Identity) { SideId = side };
                mission.PlaceShip(ship, fly);
                if (n == 0) scene.Observer = ship;
                else if (n == 1) scene.Dropper = ship;
                else scene.Mate = ship;
                scene.Sector = fly;
                n++;
                return (ship, fly);
            };
            server.Boot(Config());
            var mission = server.Missions[0];
            mission.Stage = MissionStage.Started;
            // P864c creates starting treasure; this scene needs a deterministic
            // empty treasure table (DropPartTests' fixture recipe).
            foreach (var sector in mission.Sectors)
                foreach (var treasure in sector.Models.OfType<Treasure>().ToArray())
                    sector.Remove(treasure);
            mission.TreasureParameters.PlayerSectorTreasureRate = 0;
            mission.TreasureParameters.NeutralSectorTreasureRate = 0;

            for (int i = 0; i < scene.Bots.Length; i++)
            {
                scene.Bots[i] = new FltBot();
                scene.Bots[i].Connect(server.BoundPort, $"P1031{i}");
                scene.Bots[i].SendLogonRequest(Request($"P1031{i}"));
            }
            PumpUntil(server, () => scene.Bots.All(b => b.ShipAssignment is not null), 6000);
            if (scene.Bots.Any(b => b.ShipAssignment is null))
                throw new InvalidOperationException("all three P1031 bots must seat");
            return scene;
        }

        public void Dispose()
        {
            foreach (var bot in Bots) bot?.Dispose();
            Server?.Dispose();
        }
    }

    private static List<(ConnectionId Peer, byte[] Msg)> Capture(Srv server)
    {
        var sent = new List<(ConnectionId Peer, byte[] Msg)>();
        server._testSendCapture = (conn, packet) =>
            sent.AddRange(FedPacket.Unpack(packet).Select(m => (conn, m)));
        return sent;
    }

    // ── the finding, end to end over the real inbound dispatcher ────────────

    /// <summary>
    /// ⭐ F2 — a part dropped by a side-1 ship in a sector where side 0 has no
    /// scanner in range must NOT be exported to side 0, and must reach side 0
    /// only once (and after) its own scanner comes into range. Pre-fix the
    /// creation broadcast reached every peer in the sector, so the observer
    /// learned the unseen enemy's exact drop position.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Dropped_part_treasure_reaches_only_sides_that_can_see_it()
    {
        Require.Artwork();
        using var scene = Scene.Boot();
        var server = scene.Server;
        var sector = scene.Sector;
        var dropperConn = ConnOf(server, scene.Dropper);
        var captured = new List<(ConnectionId Peer, byte[] Msg, long Tick)>();
        server._testSendCapture = (conn, packet) =>
        {
            long tick = scene.Tick;
            captured.AddRange(FedPacket.Unpack(packet).Select(m => (conn, m, tick)));
        };

        // A real weapon in mount 0 — what DROP_PART(33) ejects.
        short weapon = OneRealWeapon(server);
        scene.Dropper.MountPart(server.Missions[0].Core.PartTypes[weapon], 20, 0, ExpandedPartData.FullyMounted);
        foreach (var bot in scene.Bots) bot.ResetTreasureSpawnsReceived();

        var before = sector.Models.OfType<Treasure>().Select(t => t.Id).ToHashSet();
        scene.DropperBot.SendDropPart(EquipmentType.Weapon, mount: 0);

        // Drive the production dispatcher until the server has created the drop
        // treasure; remember the tick it appeared on.
        Treasure? treasure = null;
        long createdTick = -1;
        for (int i = 0; i < 2000 && treasure is null; i++)
        {
            scene.Tick = i;
            server.TickSim();
            treasure = sector.Models.OfType<Treasure>().FirstOrDefault(t => !before.Contains(t.Id));
            if (treasure is not null) createdTick = i;
            else Thread.Sleep(1);
        }
        Assert.True(treasure is not null, "the drop never created a treasure");
        var drop = treasure!;

        // THE FINDING: the unseen side must not receive the creation frame. Its
        // FM_S_TREASURE(28) carries the drop's exact P0/V0 — i.e. the unseen
        // enemy ship's position (R1B_CARBON_REPORT.md F2).
        var observerConn = ConnOf(server, scene.Observer);
        Assert.DoesNotContain(captured, s => s.Peer == observerConn
            && FedMessageHeader.Read(s.Msg).Fmid == TreasureSpawn.Fmid
            && TreasureSpawn.Decode(s.Msg).Data.ObjectId == drop.Id);

        // Native: the treasure is only visible to a side whose scanner saw it.
        // The dropping ship (a real scanner hull) is right beside it, so the
        // enemy side sees it immediately; the observer's side, 6 km away, does
        // not (treasureIGC.cpp:167-177; FedSrv.CPP:10750-10778).
        Assert.True(drop.SeenBySide(EnemySide), "the dropper's own side must see its drop immediately");
        Assert.False(drop.SeenBySide(ObserverSide), "the observer side has no scanner within range of the drop");

        // …and the creation frame for the OWNER's side is emitted in the very
        // tick that created the treasure (the port's instant creation path; a
        // visibility-blind gate would defer it to the 10-pass sweep).
        var ownerFrames = captured.Where(s => s.Peer == dropperConn
                && FedMessageHeader.Read(s.Msg).Fmid == TreasureSpawn.Fmid
                && TreasureSpawn.Decode(s.Msg).Data.ObjectId == drop.Id)
            .ToList();
        Assert.Single(ownerFrames);
        Assert.Equal(createdTick, ownerFrames[0].Tick);

        // Same-side mate (at the drop site) also received it in that broadcast.
        Assert.True(PumpUntil(server, () => scene.DropperBot.TreasureSpawns.Any(t => t.Data.ObjectId == drop.Id), 500) < 500);
        Assert.True(PumpUntil(server, () => scene.MateBot.TreasureSpawns.Any(t => t.Data.ObjectId == drop.Id), 500) < 500);

        // Settle: the unseen side must get no creation frame at all (pre-fix it
        // received the full FM_S_TREASURE(28) with the drop's exact P0/V0).
        long settleUntil = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < settleUntil) { server.TickSim(); Thread.Sleep(1); }
        Assert.Empty(scene.ObserverBot.TreasureSpawns);

        // The per-tick discovery sweep delivers it when the side's scanner finally
        // sees it (native ShowObject's mid-game half, FedSrv.CPP:10750-10778).
        scene.Observer.Position = drop.Position + new Vector3(220f, 0f, 0f);
        int delivered = PumpUntil(server,
            () => scene.ObserverBot.TreasureSpawns.Any(t => t.Data.ObjectId == drop.Id), 2000);
        Assert.True(delivered < 2000,
            $"no TREASURE(28) for the drop reached side {ObserverSide} after its scanner came into range (seen={drop.SeenBySide(ObserverSide)})");
        Assert.Single(scene.ObserverBot.TreasureSpawns, t => t.Data.ObjectId == drop.Id);

        Console.WriteLine($"P1031-DROP-LIVE: enemy drop obj={drop.Id} -> owner side frame at tick {createdTick} " +
            $"(seen side1={drop.SeenBySide(EnemySide)} side0={drop.SeenBySide(ObserverSide)}); unseen side got nothing until its scanner closed range ({delivered} ticks)");
    }

    // ── ambient generation path (BroadcastGeneratedTreasure) ────────────────

    /// <summary>
    /// ⭐ F2 — the periodic ambient/development treasure path
    /// (<c>Server.Lifecycle.cs</c> <c>TreasureGenerated</c> →
    /// <c>BroadcastGeneratedTreasure</c>) must honour side visibility too: a
    /// freshly generated treasure is exported only to peers whose side sees it.
    /// Pre-fix the sector-wide frame carried its position to everyone.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Generated_treasure_broadcast_follows_side_visibility()
    {
        Require.Artwork();
        using var scene = Scene.Boot();
        var server = scene.Server;
        var mission = server.Missions[0];
        var captured = Capture(server);

        // A cash treasure next to the side-1 ships, 6 km from the observer.
        var position = scene.Dropper.Position + new Vector3(15f, 0f, 0f);
        var data = new DataTreasureIGC(
            P0: [position.X, position.Y, position.Z], V0: [0f, 0f, 0f], Lifespan: 9999f, Time0: 0u,
            ObjectId: 31100, TreasureId: 500, ClusterId: scene.Sector.Id, Amount: 500,
            TreasureCode: TreasureCode.Cash, CreateNow: true);
        var treasure = new Treasure(31100, scene.Sector, data, server.SimNow, mission);
        scene.Sector.Add(treasure);
        // Stand in for the creation path's visibility refresh (P1031 makes every
        // creation site compute it): the enemy ships' scanners see the object.
        treasure.SideVisibility.SetSideVisibility(EnemySide, true);
        Assert.False(treasure.SeenBySide(ObserverSide));

        captured.Clear();
        Call(server, "BroadcastGeneratedTreasure", treasure);

        var frames = captured
            .Where(s => FedMessageHeader.Read(s.Msg).Fmid == TreasureSpawn.Fmid)
            .Select(s => TreasureSpawn.Decode(s.Msg))
            .Where(t => t.Data.ObjectId == 31100)
            .ToList();
        Assert.Equal(2, frames.Count);   // both side-1 peers, nobody else
        var observerConn = ConnOf(server, scene.Observer);
        Assert.DoesNotContain(captured, s => s.Peer == observerConn
            && FedMessageHeader.Read(s.Msg).Fmid == TreasureSpawn.Fmid
            && TreasureSpawn.Decode(s.Msg).Data.ObjectId == 31100);

        Console.WriteLine($"P1031-AMBIENT: generated treasure obj=31100 at {position} -> {frames.Count} side-1 frame(s), 0 side-0 (unseen)");
    }

    // ── join / SET_CLUSTER snapshot (SendTreasureSnapshot) ──────────────────

    /// <summary>
    /// ⭐ F2 — <c>SendTreasureSnapshot</c> exported EVERY treasure to a peer with
    /// no side (the pre-fix <c>side is not { } s || …</c> branch), and a
    /// team-lobby/null-side peer has no per-side view to honour
    /// (<c>fsmission.cpp:4224-4231</c> exports only seen treasures to a joining
    /// player). Seated peers keep getting exactly their side's seen treasures.
    /// </summary>
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Treasure_snapshot_exports_only_treasures_the_peer_side_has_seen()
    {
        Require.Artwork();
        using var scene = Scene.Boot();
        var server = scene.Server;
        var sector = scene.Sector;
        var captured = Capture(server);

        var unseenData = new DataTreasureIGC([15000f, 0f, 0f], [0f, 0f, 0f], 9999f, 0u,
            31200, 600, sector.Id, 600, TreasureCode.Cash, true);
        var unseen = new Treasure(31200, sector, unseenData, server.SimNow, server.Missions[0]);
        sector.Add(unseen);
        var seenData = unseenData with { ObjectId = 31201 };
        var seen = new Treasure(31201, sector, seenData, server.SimNow, server.Missions[0]);
        sector.Add(seen);
        seen.SideVisibility.SetSideVisibility(EnemySide, true);

        // A seated peer gets its own side's seen treasures only.
        var dropperConn = ConnOf(server, scene.Dropper);
        captured.Clear();
        Call(server, "SendTreasureSnapshot", dropperConn, sector);
        var forEnemy = captured
            .Where(s => s.Peer == dropperConn && FedMessageHeader.Read(s.Msg).Fmid == TreasureSpawn.Fmid)
            .Select(s => TreasureSpawn.Decode(s.Msg).Data.ObjectId)
            .ToList();
        Assert.Contains((short)31201, forEnemy);
        Assert.DoesNotContain((short)31200, forEnemy);

        // A null-side (team-lobby) peer has no side view: no treasures at all.
        var observerConn = ConnOf(server, scene.Observer);
        scene.Observer.SideId = null;
        try
        {
            captured.Clear();
            Call(server, "SendTreasureSnapshot", observerConn, sector);
            Assert.DoesNotContain(captured, s => FedMessageHeader.Read(s.Msg).Fmid == TreasureSpawn.Fmid);
        }
        finally
        {
            scene.Observer.SideId = ObserverSide;
        }

        // Same rule for a seated observer: the seen one arrives, the unseen one
        // does not (the pre-P1031 per-side gate already did this for real sides).
        scene.Observer.SideId = EnemySide;
        try
        {
            captured.Clear();
            Call(server, "SendTreasureSnapshot", observerConn, sector);
            var forSwitched = captured
                .Where(s => s.Peer == observerConn && FedMessageHeader.Read(s.Msg).Fmid == TreasureSpawn.Fmid)
                .Select(s => TreasureSpawn.Decode(s.Msg).Data.ObjectId)
                .ToList();
            Assert.Contains((short)31201, forSwitched);
            Assert.DoesNotContain((short)31200, forSwitched);
        }
        finally
        {
            scene.Observer.SideId = ObserverSide;
        }

        Console.WriteLine("P1031-SNAPSHOT: seated side got only its seen treasure (31201); null-side peer got none; unseen 31200 never exported");
    }
}
