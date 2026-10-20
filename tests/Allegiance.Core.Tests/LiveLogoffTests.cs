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
/// P127+P128 verification around <c>FM_CS_LOGOFF(3)</c> — the zero-field
/// "graceful logoff" request (<c>Messages.h</c>; native handler
/// <c>FedSrv.CPP:5787-5802</c>) and the ship retirement it now triggers.
/// P127 shipped the LOGOFF codec + echo + the shared <c>ReleasePeer</c>
/// disconnect path. P128 extends <c>ReleasePeer</c> to <b>retire</b> the
/// departing player's ship instead of leaving a zombie behind: broadcast
/// <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c> to the sector peers and remove the
/// ship from its sector (native <c>CFSShip::~CFSShip</c> →
/// <c>AnnounceExit(NULL, SDR_LOGGEDOFF)</c> + <c>m_pShip->Terminate()</c>,
/// <c>fsship.cpp:58-68</c>).
/// Layers:
///  (1) codec tests pin the exact bytes of LOGOFF and of the new
///      <c>ShipDelete.ForLoggedOffShip</c> (<c>sdr = SDR_LOGGEDOFF = 4</c>);
///  (2) live loopback-in-process-server proofs for: the graceful LOGOFF
///      (echo + release + ship retired from the sector), an observing peer
///      witnessing the SDR_LOGGEDOFF broadcast (both for graceful logoff and
///      an abrupt transport drop), graceful/abrupt footprint parity, a docked
///      player's logoff still retiring the ship, and a boarded carrier's
///      logoff detaching its children.
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveLogoffTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // P108/P110 concrete hulls (measured from PCore001.igc):
    //   792 = a turret-capable carrier, slots [2,6)
    //   504 = a 500-credit normal hull (the boarder)
    private const short CarrierHullId = 792;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: WireClient peers never send POSITIONREQ
        Games =
        [
            new Gm { Name = "Logoff Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = 0x882u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8850000000000000000ul, DrmHash = "logoff",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static bool InSector(Srv server, short id) =>
        server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().Any(s => s.Id == id);

    /// <summary>P439 — like <see cref="InSector(Srv, short)"/> but against a
    /// specific mission: after a carrier logoff empties a side, the shared
    /// game-over path fires DeclareGameOver and ROTATES <c>_missions[0]</c> to a
    /// fresh mission, so the boarder's still-placed ship must be looked up in the
    /// ORIGINAL mission (which the rotation left intact).</summary>
    private static bool InSector(Mission mission, short id) =>
        mission.Sectors.SelectMany(s => s.Models).OfType<Ship>().Any(s => s.Id == id);

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Place every spawn deterministically in sector 0 so a same-
    /// sector observer reliably witnesses another peer's SHIP_DELETE.</summary>
    private static void InjectSectorZeroSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, new Vector3(300f, 740f, 890f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>Plant a ship inside a friendly station's dock radius so the
    /// first tick latches it docked (<c>ServerHost.DockWithStationEvent</c> →
    /// <see cref="Ship.StationId"/>) — the same docked-spawn trick the other
    /// live suites use (LiveSuicideShipDeleteTests.InjectDockedSpawn).</summary>
    private static void InjectDockedSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, DockTestPlacement.Inside(mission, station, hull), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
    }

    /// <summary>Two-ship, one-side, both-docked board spawn (carrier hull 792
    /// first, boarder hull 504 second), mirroring LiveBoardShipTests.</summary>
    private sealed class BoardSpawn { public Ship Carrier = null!, Boarder = null!; }

    private static BoardSpawn InjectBoardSpawn(Srv server)
    {
        var sp = new BoardSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sideId = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0).SideId;
            var station = P550TestDockHome.CapLandHome(mission, sideId);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[n == 0 ? CarrierHullId : (short)504];
            var ship = new Ship(id, hull, StationPos(station) + new Vector3(25f + n * 10f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            ship.SideId = station.SideId;
            mission.PlaceShip(ship, sector);
            if (n == 0)
            {
                // Equip the carrier's FIRST turret slot (MaxFixedWeapons=2) with
                // a real part, so boarding finds a real slot (LiveBoardShipTests).
                var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
                ship.MountPart(weapon, 1, (byte)hull.MaxFixedWeapons, 255);
            }
            if (n == 0) sp.Carrier = ship; else sp.Boarder = ship;
            n++;
            return (ship, sector);
        };
        return sp;
    }

    private static void WaitDocked(Srv server, params Ship[] ships)
    {
        int n = PumpUntil(server, () => ships.All(s => s.StationId is not null), 400);
        Assert.True(n < 400, "ships did not dock");
    }

    // ── codecs ──────────────────────────────────────────────────────────────

    /// <summary><c>FM_CS_LOGOFF</c>(3) is header-only: encode emits exactly
    /// the 4 bytes <c>{ cbmsg=4, fmid=3 }</c> (P127).</summary>
    [Fact]
    public void Logoff_message_round_trips()
    {
        var msg = new Logoff();
        var d = Logoff.Decode(msg.Encode());
        Assert.Equal(Logoff.Fmid, d.GetFmid());

        var bytes = msg.Encode();
        Assert.Equal(4, bytes.Length);
        Assert.Equal(new byte[] { 4, 0, 3, 0 }, bytes);      // cbmsg, fmid, no fields
    }

    /// <summary>The same 4 bytes decode no matter who reads them — the codec
    /// is direction-agnostic, which is exactly why native can <c>ForwardMessage</c>
    /// the very message back as-is (<c>FedSrv.CPP:5787-5802</c>).</summary>
    [Fact]
    public void Logoff_echo_re_decodes_from_the_client_side()
    {
        var sent = new Logoff();
        var echoed = Logoff.Decode(sent.Encode());
        Assert.Equal(new byte[] { 4, 0, 3, 0 }, echoed.Encode());
    }

    /// <summary>P128 — <c>ShipDelete.ForLoggedOffShip</c> round-trips shipID
    /// with <c>sdr = SDR_LOGGEDOFF(4)</c> (Messages.h:191).</summary>
    [Fact]
    public void ShipDelete_loggedoff_round_trips()
    {
        var msg = ShipDelete.ForLoggedOffShip(701);
        var d = ShipDelete.Decode(msg.Encode());
        Assert.Equal(ShipDelete.Fmid, d.GetFmid());
        Assert.Equal((short)701, d.ShipId);
        Assert.Equal(ShipDelete.SdrLoggedOff, d.Sdr);
    }

    /// <summary>P128 — <c>ForLoggedOffShip</c> emits the exact 12 declared
    /// bytes with <c>sdr = 4</c> (same layout as SDR_KILLED's exact-bytes
    /// test, LiveSuicideShipDeleteTests, differing only in the enum value).</summary>
    [Fact]
    public void ShipDelete_loggedoff_emits_exact_declared_bytes()
    {
        var got = ShipDelete.ForLoggedOffShip(0x0383 /* 899 */).Encode();
        Assert.Equal(12, got.Length);
        Assert.Equal(new byte[]
        {
            12, 0,        // cbmsg
            20, 0,        // fmid
            0x83, 0x03,   // shipID (short, LE)
            0, 0,         // enum 4-align padding
            4, 0, 0, 0,   // sdr = SDR_LOGGEDOFF (int32 LE)
        }, got);
        var d = ShipDelete.Decode(got);
        Assert.Equal((short)0x0383, d.ShipId);
        Assert.Equal(ShipDelete.SdrLoggedOff, d.Sdr);
    }

    // ── the wire client ────────────────────────────────────────────────────

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<Logoff> _logoffs = new();
        private readonly List<ShipDelete> _deletes = new();
        private readonly List<LeaveShip> _leaves = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<Logoff> Logoffs { get { lock (_gate) return _logoffs.ToArray(); } }
        public IReadOnlyList<ShipDelete> Deletes { get { lock (_gate) return _deletes.ToArray(); } }
        public IReadOnlyList<LeaveShip> Leaves { get { lock (_gate) return _leaves.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendLogoff() => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new Logoff().Encode() }), guaranteed: true);
        public void SendBoard(short sidParent) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new BoardShip { SidParent = sidParent }.Encode() }), guaranteed: true);

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
                        case Logoff.Fmid: _logoffs.Add(Logoff.Decode(m)); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                        case LeaveShip.Fmid: _leaves.Add(LeaveShip.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    // ── live proofs ─────────────────────────────────────────────────────────

    /// <summary>⭐ A connected, placed player sends <c>LOGOFF(3)</c>: the
    /// server echoes the same zero-field message back to the same connection,
    /// then releases the peer through the shared disconnect path
    /// (<c>ReleasePeer</c>) <b>and</b> (P128) retires its ship — the peer
    /// vanishes from <c>server.Ships</c> AND the ship object is removed from
    /// its sector (no more zombie flight). (This supersedes P127's "ship stays
    /// in the sector" assertion — P128 deliberately retires it.)</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Logoff_echoes_releases_and_retires_the_ship()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var client = new WireClient();
        client.Connect(server.BoundPort, "LOGGER");
        client.Logon(Request("LOGGER"));
        int got = PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no ship assignment");
        short myId = client.ShipAssignment!.Ship.ShipId;
        Assert.Contains(server.Ships, s => s.Ship.Id == myId);   // commanding before logoff

        client.SendLogoff();
        int echoed = PumpUntil(server,
            () => client.Logoffs.Count > 0 && !server.Ships.Any(s => s.Ship.Id == myId),
            800);

        // (1) the echo arrived back at the same connection…
        Assert.True(echoed < 800, "no LOGOFF echo / no release within 800 ticks");
        Assert.Single(client.Logoffs);
        // (2) …and the peer was released from the broadcast/command set.
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == myId);
        // (3) P128: the ship itself is retired — gone from its sector.
        bool inSector = InSector(server, myId);
        Assert.False(inSector, "logged-off ship should be removed from the sector (P128)");

        Console.WriteLine($"LOGOFF-LIVE: peer LOGGER ship {myId} got {client.Logoffs.Count} echo(es), " +
                          $"released from Ships, ship-object in-sector={inSector} after {echoed} ticks");
    }

    /// <summary>⭐ A second, still-connected player in the same sector
    /// witnesses the retirement: it receives a real <c>SHIP_DELETE(20)</c>
    /// with <c>sdr = SDR_LOGGEDOFF</c> naming the leaver's ship id, and the
    /// leaver's ship is gone from the sector. The observer's own ship is
    /// untouched.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_observing_peer_witnesses_loggedoff_ship_delete()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var observer = new WireClient();
        using var leaver = new WireClient();
        observer.Connect(server.BoundPort, "OBSERVER"); leaver.Connect(server.BoundPort, "LEAVER");
        observer.Logon(Request("OBSERVER")); leaver.Logon(Request("LEAVER"));
        PumpUntil(server, () => observer.ShipAssignment is not null && leaver.ShipAssignment is not null, 4000);
        short obId = observer.ShipAssignment!.Ship.ShipId;
        short lvId = leaver.ShipAssignment!.Ship.ShipId;
        Assert.NotEqual(obId, lvId);

        // P548 — put the leaver on a DIFFERENT side (side1) so its departure
        // genuinely empties a side (the observer stays on side0). The old code
        // ran a mission-wide tally and ended the game on ANY departure that
        // left <=1 side populated (both peers were spawn-seated onto side0), so
        // the P480 re-seat below fired spuriously; with native's side-empty
        // gate (fsmission.cpp:1147) the game only ends because side1 is really
        // emptied, keeping the original "observer re-seated" intent intact.
        Assert.True(server.TryJoinSide(
            server.Ships.First(s => s.Ship.Id == lvId).Ship,
            server.Missions[0].Sides[1].Id));
        PumpUntil(server, () => false, 40);   // flush

        leaver.SendLogoff();
        int seen = PumpUntil(server,
            () => observer.Deletes.Any(d => d.ShipId == lvId && d.Sdr == ShipDelete.SdrLoggedOff) && !InSector(server, lvId),
            800);
        Assert.True(seen < 800, "observer did not witness SDR_LOGGEDOFF / ship not removed");

        Assert.Contains(observer.Deletes, d => d.ShipId == lvId && d.Sdr == ShipDelete.SdrLoggedOff);
        Assert.False(InSector(server, lvId), "leaver's ship must be gone from the sector");

        // P480 — the leaver was the only peer on its side, so its departure
        // ends the game (win by inactivity for the observer's side) and MIGRATES
        // the still-connected observer into a fresh mission (a NEW ship id). The
        // observer stays commanding — just re-seated, not disconnected.
        int reSeated = PumpUntil(server,
            () => observer.ShipAssignment is not null && observer.ShipAssignment!.Ship.ShipId != obId, 2000);
        Assert.True(reSeated < 2000, "observer was not re-seated after the leaver's departure ended the game");
        Assert.Contains(server.Ships, s => s.Ship.Id == observer.ShipAssignment!.Ship.ShipId);   // observer still commanding

        Console.WriteLine($"LOGOFF-OBS-LIVE: observer {obId} saw SHIP_DELETE(20) SDR_LOGGEDOFF " +
                          $"shipID={lvId} after {seen} ticks; leaver ship in-sector=False; observer re-seated as {observer.ShipAssignment!.Ship.ShipId}");
    }

    /// <summary>⭐ The same retirement via an <b>abrupt</b> connection drop
    /// (no explicit LOGOFF): a still-connected observer witnesses
    /// <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c> for the dropped player's ship and
    /// the ship is removed from the sector — the case P127's peer-bookkeeping
    /// test only partly covered, now extended to the ship itself.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Abrupt_drop_retires_the_ship_and_broadcasts_loggedoff()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var observer = new WireClient();
        using var dropper = new WireClient();
        observer.Connect(server.BoundPort, "OBSERVER"); dropper.Connect(server.BoundPort, "DROPPER");
        observer.Logon(Request("OBSERVER")); dropper.Logon(Request("DROPPER"));
        PumpUntil(server, () => observer.ShipAssignment is not null && dropper.ShipAssignment is not null, 4000);
        short drId = dropper.ShipAssignment!.Ship.ShipId;

        dropper.Dispose();                              // abrupt drop → transport OnPeerLeft → ReleasePeer
        int seen = PumpUntil(server,
            () => observer.Deletes.Any(d => d.ShipId == drId && d.Sdr == ShipDelete.SdrLoggedOff) && !InSector(server, drId),
            800);
        Assert.True(seen < 800, "observer did not witness SDR_LOGGEDOFF for the abrupt drop / ship not removed");

        Assert.Contains(observer.Deletes, d => d.ShipId == drId && d.Sdr == ShipDelete.SdrLoggedOff);
        Assert.False(InSector(server, drId), "dropped player's ship must be gone from the sector");
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == drId);

        Console.WriteLine($"LOGOFF-ABRUPT-LIVE: observer saw SHIP_DELETE(20) SDR_LOGGEDOFF " +
                          $"shipID={drId} for abrupt drop after {seen} ticks");
    }

    /// <summary>⭐ Footprint parity: a graceful <c>LOGOFF</c> and an abrupt
    /// drop leave the exact same observable footprint — the peer is gone from
    /// <c>server.Ships</c> and <b>both</b> ship objects are retired from their
    /// sectors. There is no second, parallel mechanism (P127's premise held;
    /// P128 changes both legs identically through the one <c>ReleasePeer</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Logoff_cleanup_matches_an_abrupt_disconnect_footprint()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var graceful = new WireClient();
        using var abrupt = new WireClient();
        graceful.Connect(server.BoundPort, "GRACEFUL"); abrupt.Connect(server.BoundPort, "ABRUPT");
        graceful.Logon(Request("GRACEFUL")); abrupt.Logon(Request("ABRUPT"));
        int got = PumpUntil(server,
            () => graceful.ShipAssignment is not null && abrupt.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no assignments");
        short gId = graceful.ShipAssignment!.Ship.ShipId;
        short aId = abrupt.ShipAssignment!.Ship.ShipId;

        graceful.SendLogoff();                       // graceful path
        abrupt.Dispose();                            // abrupt drop → transport OnPeerLeft

        int settled = PumpUntil(server,
            () => !server.Ships.Any(s => s.Ship.Id == gId || s.Ship.Id == aId), 800);
        Assert.True(settled < 800, "peers not both released");

        // Identical footprint: neither is commanded anymore…
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == gId);
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == aId);
        // …and P128 retires BOTH ship objects from the sector (was: both stayed).
        Assert.False(InSector(server, gId), "graceful ship should be retired");
        Assert.False(InSector(server, aId), "abrupt ship should be retired");

        Console.WriteLine($"LOGOFF-LIVE: graceful {gId} and abrupt {aId} both released and retired after {settled} ticks");
    }

    /// <summary>⭐ Finding #4 live-proof: a <b>docked</b> player (ship
    /// <c>StationId != null</c>) that logs off still has its ship retired —
    /// native's <c>CFSShip::~CFSShip</c> fires on destruction regardless of
    /// dock state (<c>fsship.cpp:58-68</c>), so being docked changes nothing.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_player_logoff_still_retires_the_ship()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectDockedSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var client = new WireClient();
        client.Connect(server.BoundPort, "DOCKED");
        client.Logon(Request("DOCKED"));
        int got = PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "no ship assignment");
        short myId = client.ShipAssignment!.Ship.ShipId;

        // Prove we really are docked before logging off.
        PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == myId).Ship.StationId is not null, 200);
        Assert.NotNull(server.Ships.First(s => s.Ship.Id == myId).Ship.StationId);

        client.SendLogoff();
        int retired = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == myId), 800);
        Assert.True(retired < 800, "docked peer not released");

        Assert.False(InSector(server, myId), "docked logged-off ship must still be retired (SDR_LOGGEDOFF applies docked or not)");
        Console.WriteLine($"LOGOFF-DOCKED-LIVE: docked ship {myId} retired from sector after logoff ({retired} ticks)");
    }

    /// <summary>⭐ Finding #3 live-proof: when the departing player's ship is
    /// a <b>carrier with a boarded child</b>, the child is detached (un-boarded)
    /// — native <c>Terminate</c> "blows away the children" via
    /// <c>SetParentShip(NULL)</c> (<c>shipIGC.cpp:243-250</c>), which ejects
    /// the child as an independent ship rather than destroying it
    /// (<c>shipIGC.cpp:3671-3700</c>). Here the child's parent/turret is
    /// cleared and it is out of the carrier's child list, while the carrier
    /// itself is retired from the sector and the (separate, still-connected)
    /// child's ship stays placed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Carrier_logoff_detaches_boarded_children_and_retires_the_carrier()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectBoardSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var carrier = new WireClient();
        using var boarder = new WireClient();
        carrier.Connect(server.BoundPort, "CARRIER"); boarder.Connect(server.BoundPort, "BOARDER");
        carrier.Logon(Request("CARRIER")); boarder.Logon(Request("BOARDER"));
        PumpUntil(server, () => carrier.ShipAssignment is not null && boarder.ShipAssignment is not null, 4000);

        // Role-match by spawned hull (robust to socket order): whichever
        // client owns the 792 carrier ship is the carrier.
        WireClient car, boa;
        short carId, boaId;
        Ship cShip, bShip;
        {
            short ca = carrier.ShipAssignment!.Ship.ShipId;
            short ba = boarder.ShipAssignment!.Ship.ShipId;
            Ship cs = server.Ships.First(s => s.Ship.Id == ca).Ship;
            if (cs.HullType.HullId == CarrierHullId) { car = carrier; boa = boarder; carId = ca; boaId = ba; }
            else { car = boarder; boa = carrier; carId = ba; boaId = ca; }
            cShip = server.Ships.First(s => s.Ship.Id == carId).Ship;
            bShip = server.Ships.First(s => s.Ship.Id == boaId).Ship;
        }

        WaitDocked(server, cShip, bShip);

        // Board the child onto the carrier.
        boa.SendBoard(carId);
        int boarded = PumpUntil(server, () => bShip.ParentShip is not null, 1200);
        Assert.True(boarded < 1200, "board did not set ParentShip");
        Assert.Contains(cShip.ChildShips, k => ReferenceEquals(k, bShip));

        // P439 — the carrier is the only player on its side, so its logoff empties
        // that side and the shared game-over path ROTATES _missions[0] to a fresh
        // mission. The carrier-retirement / child-still-placed checks must inspect
        // the ORIGINAL mission the docking happened in.
        var mission0 = server.Missions[0];

        // The carrier logs off.
        car.SendLogoff();
        int done = PumpUntil(server,
            () => bShip.ParentShip is null && !server.Ships.Any(s => s.Ship.Id == carId), 800);
        Assert.True(done < 800, "carrier logoff did not detach child / retire carrier");

        // Child detached (native "blow away the children"), not destroyed.
        Assert.Null(bShip.ParentShip);
        Assert.DoesNotContain(cShip.ChildShips, k => ReferenceEquals(k, bShip));
        // Carrier retired from its sector; the still-connected child's ship stays placed.
        Assert.False(InSector(mission0, carId), "carrier ship should be removed from the sector");
        Assert.True(InSector(mission0, boaId), "child's own ship should still be placed (detached, not destroyed)");

        Console.WriteLine($"LOGOFF-BOARD-LIVE: carrier {carId} logged off — child {boaId} detached " +
                          $"(ParentShip null, out of child list), carrier retired, child in-sector=True after {done} ticks");
    }
}
