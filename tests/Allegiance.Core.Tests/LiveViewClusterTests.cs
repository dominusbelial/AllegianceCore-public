using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P106 verification — the <c>FM_C_VIEW_CLUSTER(104)</c> /
/// <c>FM_S_VIEW_CLUSTER(177)</c> docked-spectator/map-view slice. Two layers:
/// (1) codec tests pin the exact bytes of both new wire messages against
/// hand-composed buffers (no alignment padding, this codebase's standing
/// assumption — SetCluster's doc comment and the P36 proof); (2) real
/// loopback-in-process-server live proofs for the five task scenarios: a
/// docked ship requests a bare cluster (no target) → reply naming it with
/// bUsePosition=false; names a real same-side object in it → bUsePosition=true
/// with the object's real position; names an object in a <em>different</em>
/// cluster or an enemy (not-visible) object → bUsePosition=false; a flying
/// (not-docked) ship's request is a no-op with no reply.
/// Like the other live suites this drives a real in-process <see cref="Srv"/>
/// over loopback TCP; the [Collection("LiveNetwork")] tag keeps it sequential
/// against the other live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveViewClusterTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Verifiable ground truth from XXV_simple.igc (missiondump, same source
    // LiveChatSectorWireTests cites): 13 clusters, real sectors 1053 'Ceto'
    // and 1056 'Farout'.
    private const short SectorCeto = 1053;
    private const short SectorFarout = 1056;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: WireClient peers never send POSITIONREQ
        Games =
        [
            new Gm { Name = "ViewCluster Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 966u, DwCookie = 0x106u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8860000000000000000ul, DrmHash = "viewcluster",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Wait until every client has its ship assignment, then return the
    /// index of the client whose ship is <b>docked</b> (the P106 requester —
    /// spawn 0, planted inside the station's dock radius). Role detection by
    /// observable state rather than logon order: with the async transport the
    /// server can seat a later-logged-in bot before an earlier one, so the
    /// requester is whoever ended up docked, not necessarily clients[0].</summary>
    private static int WaitForDockedRequester(Srv server, WireClient[] clients, int maxAssign = 4000, int maxDock = 400)
    {
        PumpUntil(server, () => clients.All(c => c.ShipAssignment is not null), maxAssign);
        int ri = PumpUntil(server, () => clients.Select(c => c.ShipAssignment!.Ship.ShipId)
            .Any(sid => server.Ships.Any(s => s.Ship.Id == sid && s.Ship.StationId is not null)), maxDock);
        Assert.True(ri < maxDock, "no client's ship became docked");
        int dockedIndex = 0;
        for (int i = 0; i < clients.Length; i++)
        {
            short sid = clients[i].ShipAssignment!.Ship.ShipId;
            if (server.Ships.Any(s => s.Ship.Id == sid && s.Ship.StationId is not null)) { dockedIndex = i; break; }
        }
        Assert.Equal(1, clients.Count(c => server.Ships.Any(s => s.Ship.Id == c.ShipAssignment!.Ship.ShipId && s.Ship.StationId is not null)));
        return dockedIndex;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static void AssertClose(Vector3 expected, Vector3 actual, float tol)
    {
        Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
        Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
        Assert.InRange(actual.Z, expected.Z - tol, expected.Z + tol);
    }

    /// <summary>Mutable role layout filled by the spawner lambda as clients log
    /// on (ship ids/sectors are only known at logon time). The tests read these
    /// AFTER pumping everyone into the sim.</summary>
    private sealed class ViewLayout
    {
        public short DockId;
        public short DiffId;
        public short SameTargetId;
        public short EnemyTargetId;
        public short FlyerId;
        public Vector3 SameTargetPos;
    }

    /// <summary>
    /// Seed <see cref="Srv.ShipSpawner"/> with a deterministic five-ship layout
    /// across two real sectors, roles resolved at runtime (never assuming list
    /// order — sector ids are the verified ground truth, mission.Sectors is
    /// looked up by id):
    /// <list type="bullet">
    /// <item>spawn 0 = <b>requester, DOCKED</b> at the first side&amp;gt;=0 station,
    /// side = station's side (sideA) — the genuine docked gate is latched by the
    /// first dock-proximity tick.</item>
    /// <item>spawn 1 = <b>same-side target in dockSector</b>, far from any
    /// station so it stays flying.</item>
    /// <item>spawn 2 = <b>same-side target in the OTHER sector</b> (diffSector).</item>
    /// <item>spawn 3 = <b>enemy-side target in dockSector</b> (otherSide != sideA).</item>
    /// <item>spawn 4 = <b>second requester, FLYING</b> in dockSector (never
    /// docked) — its request must be a no-op.</item>
    /// </list>
    /// </summary>
    private static ViewLayout InjectViewClusterSpawn(Srv server)
    {
        var layout = new ViewLayout();
        short spawnCounter = 0;

        server.ShipSpawner = (core, mission, id) =>
        {
            var dockStation = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var dockSector = mission.Sectors.First(s => s.Stations.Contains(dockStation));
            var diffSector = mission.Sectors.First(s => s.Id != dockSector.Id);
            short sideA = dockStation.SideId;
            short otherSide = mission.Sides.First(s => s.Id != sideA).Id;
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();

            short idx = spawnCounter++;

            // Controlled positions per role.
            if (idx == 0)       // requester: docked
            {
                var ship = new Ship(id, hull, DockTestPlacement.Inside(mission, dockStation, hull), Vector3.Zero, Orientation.Identity);
                ship.SideId = sideA;
                mission.PlaceShip(ship, dockSector);
                layout.DockId = dockSector.Id;
                layout.SameTargetPos = new Vector3(5000f, -5000f, 2000f);
                return (ship, dockSector);
            }
            if (idx == 1)       // same-side target in dockSector
            {
                var ship = new Ship(id, hull, layout.SameTargetPos, Vector3.Zero, Orientation.Identity);
                ship.SideId = sideA;
                mission.PlaceShip(ship, dockSector);
                layout.SameTargetId = id;
                return (ship, dockSector);
            }
            if (idx == 2)       // same-side target in diffSector
            {
                var ship = new Ship(id, hull, new Vector3(9000f, -9000f, 3000f), Vector3.Zero, Orientation.Identity);
                ship.SideId = sideA;
                mission.PlaceShip(ship, diffSector);
                layout.DiffId = diffSector.Id;
                return (ship, diffSector);
            }
            if (idx == 3)       // enemy-side target in dockSector
            {
                var ship = new Ship(id, hull, new Vector3(6000f, 6000f, -2000f), Vector3.Zero, Orientation.Identity);
                ship.SideId = otherSide;
                mission.PlaceShip(ship, dockSector);
                layout.EnemyTargetId = id;
                return (ship, dockSector);
            }
            // idx == 4: flying requester in dockSector, never near a station.
            var flyer = new Ship(id, hull, new Vector3(-7000f, 7000f, 1000f), Vector3.Zero, Orientation.Identity);
            flyer.SideId = sideA;
            mission.PlaceShip(flyer, dockSector);
            layout.FlyerId = id;
            return (flyer, dockSector);
        };

        return layout;
    }

    // ── the two new codecs ─────────────────────────────────────────────────

    /// <summary><c>FM_C_VIEW_CLUSTER</c>(104) emits exactly the 10 declared
    /// bytes: header + clusterID short + otTarget short + oidTarget short,
    /// hand-composed from the native struct (no alignment padding).</summary>
    [Fact]
    public void ViewClusterRequest_emits_exact_declared_bytes()
    {
        var got = new ViewClusterRequest
        {
            ClusterId = 1109, OtTarget = 0 /* OT_ship */, OidTarget = 291,
        }.Encode();
        Assert.Equal(10, got.Length);
        Assert.Equal(new byte[]
        {
            10, 0,        // cbmsg
            104, 0,       // fmid
            0x55, 0x04,   // clusterID (1109)
            0x00, 0x00,   // otTarget (0 = OT_ship)
            0x23, 0x01,   // oidTarget (291)
        }, got);

        // NA(-1) target — the "bare cluster" request shape.
        var na = new ViewClusterRequest { ClusterId = 1109, OtTarget = -1, OidTarget = -1 }.Encode();
        Assert.Equal(new byte[] { 10, 0, 104, 0, 0x55, 0x04, 0xFF, 0xFF, 0xFF, 0xFF }, na);

        // Decode the hand-built buffer symmetrically.
        var d = ViewClusterRequest.Decode(got);
        Assert.Equal(1109, d.ClusterId);
        Assert.Equal((short)0, d.OtTarget);
        Assert.Equal(291, d.OidTarget);
    }

    /// <summary><c>FM_S_VIEW_CLUSTER</c>(177) emits exactly the 19 declared
    /// bytes: header + Vector(12) + clusterID short + bUsePosition byte — the
    /// vector at 4-15 (4-aligned, like <see cref="MissileDestroyed"/>), then the
    /// short, then the 1-byte bool, hand-composed.</summary>
    [Fact]
    public void ViewClusterReply_emits_exact_declared_bytes()
    {
        var got = new ViewClusterReply
        {
            Position = new Vector3(1.5f, -2.0f, 3.25f),
            ClusterId = 1109,
            BUsePosition = true,
        }.Encode();
        Assert.Equal(19, got.Length);
        Assert.Equal(new byte[]
        {
            19, 0,        // cbmsg
            177, 0,       // fmid
            0x00, 0x00, 0xC0, 0x3F,  // position.x = 1.5f   (0x3FC00000 LE)
            0x00, 0x00, 0x00, 0xC0,  // position.y = -2.0f  (0xC0000000 LE)
            0x00, 0x00, 0x50, 0x40,  // position.z = 3.25f  (0x40500000 LE)
            0x55, 0x04,    // clusterID (1109)
            0x01,         // bUsePosition = true
        }, got);

        // bUsePosition=false variant: position is irrelevant but the byte is 0.
        var noPos = new ViewClusterReply
        {
            Position = Vector3.Zero, ClusterId = 1109, BUsePosition = false,
        }.Encode();
        Assert.Equal(19, noPos.Length);
        Assert.Equal((byte)0, noPos[18]);

        // Decode the hand-built buffer symmetrically.
        var d = ViewClusterReply.Decode(got);
        Assert.Equal(1.5f, d.Position.X);
        Assert.Equal(-2.0f, d.Position.Y);
        Assert.Equal(3.25f, d.Position.Z);
        Assert.Equal(1109, d.ClusterId);
        Assert.True(d.BUsePosition);
    }

    /// <summary>Both codecs round-trip.</summary>
    [Fact]
    public void ViewCluster_messages_round_trip()
    {
        var req = new ViewClusterRequest { ClusterId = SectorCeto, OtTarget = -1, OidTarget = -1 };
        var rd = ViewClusterRequest.Decode(req.Encode());
        Assert.Equal(SectorCeto, rd.ClusterId);
        Assert.Equal(-1, rd.OtTarget);
        Assert.Equal(-1, rd.OidTarget);

        var rep = new ViewClusterReply { Position = new Vector3(1f, 2f, 3f), ClusterId = SectorFarout, BUsePosition = true };
        var rpd = ViewClusterReply.Decode(rep.Encode());
        Assert.Equal(new Vector3(1f, 2f, 3f), rpd.Position);
        Assert.Equal(SectorFarout, rpd.ClusterId);
        Assert.True(rpd.BUsePosition);
    }

    // ── the wire client ────────────────────────────────────────────────────

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<ViewClusterReply> _views = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<ViewClusterReply> Views { get { lock (_gate) return _views.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);

        public void SendViewCluster(short clusterId, short otTarget, short oidTarget)
            => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new ViewClusterRequest { ClusterId = clusterId, OtTarget = otTarget, OidTarget = oidTarget }.Encode() }), guaranteed: true);

        void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
        void ITransportSite.OnPeerLeft(ConnectionId id) { }
        void ITransportSite.OnSessionLost() { }
        void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            byte[][] msgs;
            try { msgs = FedPacket.Unpack(data.ToArray()); }
            catch (FedCrcException) { return; }
            lock (_gate)
            {
                foreach (var m in msgs)
                {
                    ushort fmid;
                    try { fmid = FedMessageHeader.Read(m).Fmid; } catch { continue; }
                    switch (fmid)
                    {
                        case LogonAck.Fmid: _ack = LogonAck.Decode(m); break;
                        case SetCluster.Fmid: _set = SetCluster.Decode(m); break;
                        case ViewClusterReply.Fmid: _views.Add(ViewClusterReply.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    // ── live proofs ────────────────────────────────────────────────────────

    /// <summary>⭐ A docked ship requests a real, existing cluster with no
    /// target (<c>otTarget = NA</c>) — the reply names that cluster with
    /// <c>bUsePosition = false</c> (a bare-cluster preview). Sent only to the
    /// requesting peer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_request_bare_cluster_gets_reply_naming_it()
    {
        Require.Artwork();
        using var server = new Srv();
        var lay = InjectViewClusterSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var client = new WireClient();
        client.Connect(server.BoundPort, "VIEW-A");
        client.Logon(Request("VIEW-A"));
        PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        var reqId = client.ShipAssignment!.Ship.ShipId;

        // Prove genuinely docked first.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == reqId).Ship.StationId is not null, 200);
        Assert.NotNull(server.Ships.First(s => s.Ship.Id == reqId).Ship.StationId);

        client.SendViewCluster(SectorCeto, -1, -1);
        int ticks = PumpUntil(server, () => client.Views.Count > 0, 1000);
        Assert.True(ticks < 1000, "no VIEW_CLUSTER reply");
        var v = client.Views[0];
        Assert.Equal(SectorCeto, v.ClusterId);
        Assert.False(v.BUsePosition);
        Console.WriteLine($"VIEW-LIVE: docked ship {reqId} bare-cluster request -> reply cluster {v.ClusterId} bUsePosition=false ({v.Encode().Length}B, {BitConverter.ToString(v.Encode())})");
    }

    /// <summary>⭐ A docked ship requests a cluster naming a real, visible
    /// (same-side) object in it — the reply carries <c>bUsePosition = true</c>
    /// and the object's real position. Both target and requester share the
    /// dock sector. The target is a second live peer (spawn 1), so this also
    /// proves the port resolves a real, running ship, not a fabricated model.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_request_named_visible_object_returns_its_position()
    {
        Require.Artwork();
        using var server = new Srv();
        var lay = InjectViewClusterSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var requester = new WireClient();
        using var targetPeer = new WireClient();
        var clients = new[] { requester, targetPeer };
        requester.Connect(server.BoundPort, "VIEW-B");
        targetPeer.Connect(server.BoundPort, "VIEW-B2");
        requester.Logon(Request("VIEW-B"));
        targetPeer.Logon(Request("VIEW-B2"));
        int ri = WaitForDockedRequester(server, clients);
        var req = clients[ri];
        short reqId = req.ShipAssignment!.Ship.ShipId;

        // The target peer really exists (its ship is on the server and in DockId).
        Assert.Contains(server.Ships, s => s.Ship.Id == lay.SameTargetId);
        Assert.True(server.Ships.First(s => s.Ship.Id == lay.SameTargetId).Sector.Id == lay.DockId, "target not in dock sector");

        req.SendViewCluster(lay.DockId, (short)IgcObjectType.Ship, lay.SameTargetId);
        int ticks = PumpUntil(server, () => req.Views.Count > 0, 1000);
        Assert.True(ticks < 1000, "no VIEW_CLUSTER reply");
        var v = req.Views[0];
        Assert.True(v.BUsePosition);
        Assert.Equal(lay.DockId, v.ClusterId);
        AssertClose(lay.SameTargetPos, v.Position, 0.01f);
        Console.WriteLine($"VIEW-LIVE: docked ship {reqId} -> cluster {lay.DockId} target ship {lay.SameTargetId} bUsePosition=true position {v.Position} (expected {lay.SameTargetPos})");
    }

    /// <summary>⭐ A docked ship names an object that is real but in a
    /// <b>different</b> cluster, or real but <b>not visible</b> to its side
    /// (an enemy ship) — both fail the native's own
    /// <c>ptarget-&gt;GetCluster() == pcluster &amp;&amp; SeenBySide</c> gate, so
    /// both get <c>bUsePosition = false</c> (the cluster is still echoed).
    /// Two separate requests are asserted in one flight.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_request_invisible_or_wrong_cluster_target_gets_bare_reply()
    {
        Require.Artwork();
        using var server = new Srv();
        var lay = InjectViewClusterSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var requester = new WireClient();
        using var samePeer = new WireClient();   // spawn 1: same-side target (dock sector)
        using var diffPeer = new WireClient();   // spawn 2: same-side target (other sector)
        using var enemyPeer = new WireClient();  // spawn 3: enemy target (dock sector)
        requester.Connect(server.BoundPort, "VIEW-C");
        samePeer.Connect(server.BoundPort, "VIEW-C2");
        diffPeer.Connect(server.BoundPort, "VIEW-C3");
        enemyPeer.Connect(server.BoundPort, "VIEW-C4");
        requester.Logon(Request("VIEW-C"));
        samePeer.Logon(Request("VIEW-C2"));
        diffPeer.Logon(Request("VIEW-C3"));
        enemyPeer.Logon(Request("VIEW-C4"));
        PumpUntil(server, () =>
            requester.ShipAssignment is not null && samePeer.ShipAssignment is not null
            && diffPeer.ShipAssignment is not null && enemyPeer.ShipAssignment is not null, 4000);
        int ri = WaitForDockedRequester(server, new[] { requester, samePeer, diffPeer, enemyPeer });
        var req = new[] { requester, samePeer, diffPeer, enemyPeer }[ri];
        short reqId = req.ShipAssignment!.Ship.ShipId;

        // Both target ships really exist on the server; the enemy is in the
        // same (dock) sector as the requester, while SameTargetId — the object we
        // wrongly name for a DIFFERENT cluster below — is genuinely in DockId,
        // i.e. NOT in the requested DiffId (the real wrong-cluster case).
        Assert.Contains(server.Ships, s => s.Ship.Id == lay.SameTargetId);
        Assert.Contains(server.Ships, s => s.Ship.Id == lay.EnemyTargetId);
        Assert.Equal(lay.DockId, server.Ships.First(s => s.Ship.Id == lay.EnemyTargetId).Sector.Id);
        Assert.Equal(lay.DockId, server.Ships.First(s => s.Ship.Id == lay.SameTargetId).Sector.Id);

        // (a) target real but in a DIFFERENT cluster (SameTargetId lives in DockId,
        // we request DiffId and name it).
        req.SendViewCluster(lay.DiffId, (short)IgcObjectType.Ship, lay.SameTargetId);
        // (b) target real and in the same cluster but ENEMY (not visible).
        req.SendViewCluster(lay.DockId, (short)IgcObjectType.Ship, lay.EnemyTargetId);

        int ticks = PumpUntil(server, () => req.Views.Count >= 2, 1000);
        Assert.True(ticks < 1000, "no VIEW_CLUSTER reply");

        var vWrongCluster = req.Views[0];
        var vEnemy = req.Views[1];
        Assert.Equal(lay.DiffId, vWrongCluster.ClusterId);
        Assert.False(vWrongCluster.BUsePosition);
        Assert.Equal(lay.DockId, vEnemy.ClusterId);
        Assert.False(vEnemy.BUsePosition);
        Assert.Equal(Vector3.Zero, vEnemy.Position);   // native leaves position zero when bUsePosition=false
        Console.WriteLine($"VIEW-LIVE: docked ship {reqId} wrong-cluster target -> reply {vWrongCluster.ClusterId} bUsePosition=false; enemy target -> {vEnemy.ClusterId} bUsePosition=false");
    }

    /// <summary>⭐ A ship that is <b>not docked</b> (flying) sending the same
    /// request is a no-op — no reply — matching the native <c>GetStation()</c>
    /// gate (<c>FedSrv.CPP:6422</c>). Proved with a dedicated flying requester
    /// (never near a station) rather than an un-dock dance, so it stays clean.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flying_ship_request_gets_no_reply()
    {
        Require.Artwork();
        using var server = new Srv();
        server.ShipSpawner = (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var sector = mission.Sectors.First();     // any sector; the ship flies, far from any station
            var ship = new Ship(id, hull, new Vector3(-7000f, 7000f, 1000f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides.Count > 0 ? mission.Sides[0].Id : (short?)null;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var client = new WireClient();
        client.Connect(server.BoundPort, "VIEW-FLY");
        client.Logon(Request("VIEW-FLY"));
        PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        var flyerId = client.ShipAssignment!.Ship.ShipId;
        // Confirm it really is flying (not docked) server-side.
        Assert.Null(server.Ships.First(s => s.Ship.Id == flyerId).Ship.StationId);

        client.SendViewCluster(SectorCeto, -1, -1);
        int quiet = PumpUntil(server, () => false, 120);   // ample sim for a reply if one were coming

        Assert.Empty(client.Views);
        Console.WriteLine($"VIEW-LIVE: flying ship {flyerId} no reply after {quiet} ticks (gate GetStation()!=NULL)");
    }
}
