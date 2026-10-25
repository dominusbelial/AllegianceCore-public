using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P813c (EXPERIMENTAL) — server-side reconnect grace. The native rule being
/// departed from, and restored at expiry: a dropped connection destroys the
/// player (<c>FedSrv/FedSrv.CPP:4549-4566</c> <c>OnDestroyConnection</c> →
/// <c>delete pfsPlayer</c>), which runs
/// <c>RemovePlayerFromMission(QSR_LinkDead)</c> (<c>FedSrv/fsship.cpp:995</c>)
/// and the ship destruction <c>CFSShip::~CFSShip</c> →
/// <c>AnnounceExit(NULL, SDR_LOGGEDOFF)</c> + <c>m_pShip-&gt;Terminate()</c>
/// (<c>FedSrv/fsship.cpp:58-68</c>). A voluntary <c>LOGOFF</c> is deleted
/// immediately in its own handler (<c>FedSrv.CPP:5800-5802</c>).
///
/// <para>Every test here runs with <c>reconnect.enabled</c> explicitly set
/// (the flag is <b>off</b> by default), so the default-off behaviour is covered
/// by the existing suites plus
/// <see cref="Grace_off_by_default_retires_the_ship_immediately"/>.</para>
/// </summary>
[Collection("LiveNetwork")]
public class P813cReconnectGraceTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const uint Token = 0xC0FFEE01u;

    private static Cfg Config(bool reconnect = false, double graceSeconds = 1.0, int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: peers that never send POSITIONREQ still seat
        Reconnect = new global::Allegiance.Server.ReconnectConfig { Enabled = reconnect, GraceSeconds = graceSeconds },
        Games =
        [
            new Gm { Name = "P813c Grace", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, uint token = Token) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 813u, DwCookie = token,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8813000000000000000ul, DrmHash = "p813c",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static bool InSector(Srv server, short id) =>
        server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().Any(s => s.Id == id);

    private static Ship? SectorShip(Srv server, short id) =>
        server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().FirstOrDefault(s => s.Id == id);

    /// <summary>Deterministic spawn: put every player in sector 0 on side 0 so an
    /// observer reliably witnesses the same-sector SDR_LOGGEDOFF/SDR_KILLED.</summary>
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

    private static void StartMission(Srv server)
    {
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up lobby
    }

    // ── the wire client ─────────────────────────────────────────────────────

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private readonly List<LogonAck> _acks = new();
        private readonly List<SetCluster> _sets = new();
        private readonly List<ShipDelete> _deletes = new();
        private readonly List<QuitMission> _quits = new();
        private readonly List<EnterGame> _enters = new();
        private readonly List<ShipStatusMessage> _statuses = new();

        public WireClient() => _t = new TcpTransport(this);

        public LogonAck? LastAck { get { lock (_gate) return _acks.Count > 0 ? _acks[^1] : null; } }
        public IReadOnlyList<LogonAck> Acks { get { lock (_gate) return _acks.ToArray(); } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _sets.Count > 0 ? _sets[^1] : null; } }
        public int SetClusterCount { get { lock (_gate) return _sets.Count; } }
        public IReadOnlyList<ShipDelete> Deletes { get { lock (_gate) return _deletes.ToArray(); } }
        public IReadOnlyList<QuitMission> Quits { get { lock (_gate) return _quits.ToArray(); } }
        public int EnterGameCount { get { lock (_gate) return _enters.Count; } }
        public IReadOnlyList<ShipStatusMessage> Statuses { get { lock (_gate) return _statuses.ToArray(); } }
        public bool Connected => _t.IsConnected;

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendPositionReq(short sideId) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new PositionReq { SideId = sideId }.Encode() }), guaranteed: true);
        public void SendLogoff() =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new Logoff().Encode() }), guaranteed: true);
        public void SendBoard(short parentId) =>
            _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new BoardShip { SidParent = parentId }.Encode() }), guaranteed: true);

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
                        case LogonAck.Fmid: _acks.Add(LogonAck.Decode(m)); break;
                        case SetCluster.Fmid: _sets.Add(SetCluster.Decode(m)); break;
                        case ShipDelete.Fmid: _deletes.Add(ShipDelete.Decode(m)); break;
                        case QuitMission.Fmid: _quits.Add(QuitMission.Decode(m)); break;
                        case EnterGame.Fmid: _enters.Add(new EnterGame()); break;
                        case ShipStatusMessage.Fmid: _statuses.Add(ShipStatusMessage.Decode(m)); break;
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>Connect + logon + side-select + wait for the launch, returning the
    /// seated ship id.</summary>
    private static (WireClient Client, short ShipId) Seat(Srv server, string name, uint token = Token, short side = 0)
    {
        var client = new WireClient();
        client.Connect(server.BoundPort, name);
        client.Logon(Request(name, token));
        int ack = PumpUntil(server, () => client.LastAck is not null, 4000);
        Assert.True(ack < 4000, $"{name}: no LOGONACK");
        Assert.True(client.LastAck!.FValidated, $"{name}: logon refused: {client.LastAck.CharNameOrFailureReason}");
        client.SendPositionReq(side);
        int seated = PumpUntil(server, () => client.ShipAssignment is not null, 4000);
        Assert.True(seated < 4000, $"{name}: never seated");
        return (client, client.ShipAssignment!.Ship.ShipId);
    }

    private static void ForceGameOver(Srv server) =>
        typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [server.Missions[0], (short)0, "p813c game over"]);

    // ── default-off: nothing changes ────────────────────────────────────────

    /// <summary>⭐ The flag's default is OFF, and off means the pre-P813c
    /// behaviour: an abrupt drop retires the ship immediately (native
    /// <c>FedSrv.CPP:4549-4566</c> → <c>fsship.cpp:58-68</c>) and nothing is
    /// parked. This is the guard that keeps every existing disconnect test's
    /// contract intact.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Grace_off_by_default_retires_the_ship_immediately()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        var config = Config(reconnect: false);
        Assert.False(config.Reconnect.Enabled);      // default off, explicit here for clarity
        server.Boot(config);
        StartMission(server);

        var (client, shipId) = Seat(server, "OFFDROP");
        Assert.False(server.ReconnectGraceEnabled);
        client.Dispose();                            // abrupt drop

        int gone = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == shipId), 800);
        Assert.True(gone < 800, "flag off: the peer must be released at once");
        Assert.False(InSector(server, shipId), "flag off: the ship must be retired at once (native path)");
        Assert.Equal(0, server.ReconnectParkedTotal);
        Assert.Equal(0, server.ReconnectParkedShipCount);
    }

    /// <summary>⭐ An unrequested drop with the flag ON parks the seat: the ship
    /// stays in its sector (still flying under <c>Mission.Update</c>) but is no
    /// longer commanded, and its controls are the native
    /// <c>ControlData::Reset()</c> neutral value (<c>Igc/igc.h:1666-1670</c>,
    /// <c>Igc/shipIGC.cpp:2308-2348</c>) — coast, no turn, no fire.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unrequested_drop_parks_the_ship_with_neutral_controls()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));   // long window: never expires in this test
        StartMission(server);

        var (client, shipId) = Seat(server, "PARKME");
        Assert.True(server.ReconnectGraceEnabled);

        // Fly it with non-neutral input first, so "neutral at park" is a real change.
        var flying = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        flying.Controls = new ControlData(0.9f, -0.4f, 0.2f, 1.0f);
        flying.StateM = 0x3;

        client.Dispose();                            // abrupt drop
        int parked = PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 800);
        Assert.True(parked < 800, "the dropped seat was not parked");
        Assert.Equal(1, server.ReconnectParkedTotal);
        Assert.Equal(shipId, server.ReconnectParkedShipId("PARKME"));

        // The peer is no longer commanded…
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == shipId);
        // …but the ship is still in the world (held)…
        Assert.True(InSector(server, shipId), "a parked ship stays in its sector");
        // …flying on neutral controls: coast (no turn, no thrust input, no fire).
        Assert.Equal(ControlData.Neutral, flying.Controls);
        Assert.Equal(0, flying.StateM);

        // No SHIP_DELETE / QUIT_MISSION yet — the departure has not happened.
        Console.WriteLine($"P813C-PARK: ship {shipId} parked; controls={flying.Controls} stateM={flying.StateM}");
    }

    /// <summary>⭐ The window expiring runs exactly the native removal: the ship is
    /// retired (<c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c> to its sector peers) and
    /// gone from the sector, and the mission hears <c>QUIT_MISSION</c> with
    /// <c>QSR_LinkDead</c>. Nothing else changes versus today's drop.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Grace_expiry_runs_the_native_removal()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 0.5));
        StartMission(server);

        var (observer, observerId) = Seat(server, "WATCHER");
        var (dropper, dropperId) = Seat(server, "VANISH");
        Assert.NotEqual(observerId, dropperId);
        dropper.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);
        Assert.True(InSector(server, dropperId), "precondition: the ship is being held");

        int expired = PumpUntil(server,
            () => server.ReconnectParkedShipCount == 0 && !InSector(server, dropperId), 2000);
        Assert.True(expired < 2000, "the grace window never expired / ship never retired");

        Assert.Equal(1, server.ReconnectExpiredTotal);
        Assert.False(InSector(server, dropperId), "expiry must retire the ship (native fsship.cpp:58-68)");
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == dropperId);
        Assert.Contains(observer.Deletes, d => d.ShipId == dropperId && d.Sdr == ShipDelete.SdrLoggedOff);
        Assert.Contains(observer.Quits, q => q.ShipId == dropperId && q.Reason == QuitSideReason.LinkDead);
        Console.WriteLine($"P813C-EXPIRE: ship {dropperId} retired after {expired} ticks; observer saw " +
                          $"SDR_LOGGEDOFF + QUIT_MISSION(LinkDead)");
    }

    /// <summary>⭐ A voluntary <c>LOGOFF</c> is never held: native deletes the
    /// player in the LOGOFF handler itself (<c>FedSrv.CPP:5800-5802</c>, "so we
    /// can tell when we get there whether it was a dropped connection") and the
    /// departure reason is <c>QSR_Quit</c> (<c>clintlib/appmsg.cpp:3970</c>
    /// renders "has quit"), not <c>QSR_LinkDead</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Voluntary_logoff_is_immediate_and_reports_quit()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (observer, _) = Seat(server, "SEEYA-WATCH");
        var (quitter, quitterId) = Seat(server, "SEEYA");
        quitter.SendLogoff();

        int gone = PumpUntil(server,
            () => !server.Ships.Any(s => s.Ship.Id == quitterId) && !InSector(server, quitterId), 800);
        Assert.True(gone < 800, "a voluntary logoff must release immediately");
        Assert.Equal(0, server.ReconnectParkedTotal);       // never parked
        Assert.Equal(0, server.ReconnectParkedShipCount);
        Assert.Contains(observer.Quits, q => q.ShipId == quitterId && q.Reason == QuitSideReason.Quit);
        Console.WriteLine($"P813C-LOGOFF: ship {quitterId} released immediately, QUIT_MISSION reason=Quit");
    }

    /// <summary>⭐ The resume itself: a second connection presenting the SAME
    /// character name and the SAME resume token rejoins the SAME ship/seat. The
    /// server answers with a validated LOGONACK carrying that ship id and then
    /// replays the native join burst (SET_CLUSTER for the same ship, ENTER_GAME
    /// for a started mission), so the client world is re-synced in place.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reconnect_within_grace_resumes_the_same_ship_and_seat()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (first, shipId) = Seat(server, "RETURNER");
        var firstShip = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        short side = firstShip.SideId!.Value;
        first.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);
        int setClustersBefore = 0;

        using var second = new WireClient();
        second.Connect(server.BoundPort, "RETURNER");
        second.Logon(Request("RETURNER", Token));
        int resumed = PumpUntil(server,
            () => second.LastAck is { FValidated: true } && second.SetClusterCount > setClustersBefore, 4000);
        Assert.True(resumed < 4000, "the resume was not acknowledged / no join burst");

        Assert.Equal(shipId, second.LastAck!.ShipID);       // the SAME ship
        Assert.Equal(shipId, second.ShipAssignment!.Ship.ShipId);
        Assert.Equal(1, server.ReconnectResumedTotal);
        Assert.Equal(0, server.ReconnectParkedShipCount);   // consumed
        Assert.Contains(server.Ships, s => s.Ship.Id == shipId);   // commanded again
        Assert.True(InSector(server, shipId));
        var resumedShip = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(side, resumedShip.SideId);             // the SAME seat/side
        Assert.Same(firstShip, resumedShip);
        int enterGame = PumpUntil(server, () => second.EnterGameCount > 0, 400);
        Assert.True(enterGame < 400, "the join burst must finish with ENTER_GAME for a started mission");
        Console.WriteLine($"P813C-RESUME: ship {shipId} side {side} resumed on a new connection; " +
                          $"acks={second.Acks.Count} setClusters={second.SetClusterCount} entergame={second.EnterGameCount}");
    }

    /// <summary>P808a interplay: a verified pilot (lobby join token, canonical callsign)
    /// who drops resumes the same seat under the canonical name, and the seat keeps
    /// its verified account — the single-use join token is not re-checked, the
    /// resume token proves the session.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Verified_pilot_resumes_with_the_canonical_name_and_keeps_the_account()
    {
        Require.Artwork();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p813c-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string pem = Path.Combine(dir, "public.pem");
        File.WriteAllText(pem, key.ExportSubjectPublicKeyInfoPem());
        const string issuer = "https://lobby.example.invalid", audience = "p813c-test";
        string account = Guid.NewGuid().ToString("N");
        var config = Config(reconnect: true, graceSeconds: 30);
        config.Identity = new global::Allegiance.Server.ServerIdentityConfig
            { Issuer = issuer, Audience = audience, PublicKeyFile = pem, AllowAnonymous = false };
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(config);
        StartMission(server);

        byte[] ticket = System.Text.Encoding.UTF8.GetBytes(global::Allegiance.Core.Identity.JoinTokens.Issue(
            key, issuer, audience, account, "CanonicalPilot", DateTimeOffset.UtcNow));
        var first = new WireClient();
        first.Connect(server.BoundPort, "typed-name");
        var req = Request("typed-name");
        req.ZoneTicket = ticket;
        first.Logon(req);
        Assert.True(PumpUntil(server, () => first.LastAck is not null, 4000) < 4000);
        Assert.True(first.LastAck!.FValidated);
        string canonical = first.LastAck.CharNameOrFailureReason!;   // the client adopts it (appmsg.cpp:39-40)
        Assert.Equal("CanonicalPilot", canonical);
        first.SendPositionReq(0);
        Assert.True(PumpUntil(server, () => first.ShipAssignment is not null, 4000) < 4000);
        short shipId = first.ShipAssignment!.Ship.ShipId;
        first.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);

        using var second = new WireClient();
        second.Connect(server.BoundPort, canonical);
        var again = Request(canonical);
        again.ZoneTicket = ticket;                                   // already consumed: must not matter
        second.Logon(again);
        Assert.True(PumpUntil(server, () => second.LastAck is { FValidated: true }, 4000) < 4000, "resume refused");
        Assert.Equal(shipId, second.LastAck!.ShipID);
        Assert.Equal(1, server.ReconnectResumedTotal);
        var identities = (System.Collections.IDictionary)typeof(Srv)
            .GetField("_peerIdentities", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        Assert.Contains(identities.Values.Cast<global::Allegiance.Core.Identity.JoinIdentity>(), i => i.Sub == account);
    }

    /// <summary>⭐ The token is the second factor: a different client that knows
    /// the character name but not the session nonce must NOT inherit the parked
    /// seat. It is refused and the parked ship is retired immediately (the native
    /// drop path), so a player can never end up with two ships.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Resume_with_the_wrong_token_is_refused_and_the_ship_retires()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (first, shipId) = Seat(server, "MINE");
        first.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);

        using var intruder = new WireClient();
        intruder.Connect(server.BoundPort, "MINE");
        intruder.Logon(Request("MINE", 0xDEADBEEFu));        // same name, wrong token
        int settled = PumpUntil(server,
            () => server.ReconnectParkedShipCount == 0 && !InSector(server, shipId), 1200);
        Assert.True(settled < 1200, "the claimed seat was not retired");
        Assert.Equal(1, server.ReconnectRefusedTotal);
        Assert.False(InSector(server, shipId), "the parked seat is retired when a wrong token claims it");

        // The intruder still gets a normal fresh logon (no seat theft, no lockout).
        int ack = PumpUntil(server, () => intruder.LastAck is not null, 4000);
        Assert.True(ack < 4000, "the wrong-token client must still be able to log on normally");
        Assert.True(intruder.LastAck!.FValidated);
        Console.WriteLine($"P813C-HIJACK: wrong-token claim refused; parked ship {shipId} retired; claimer got a fresh logon");
    }

    /// <summary>⭐ Death during the gap is final: the parked ship is dead and out
    /// of the world (the port's death contract, <c>FinalizeDeadShip</c>), so the
    /// resume is refused and the player is NOT resurrected — the logon continues
    /// as a normal fresh one, which is the pre-P813c outcome for a death.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Death_during_the_gap_is_not_resurrected()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (first, shipId) = Seat(server, "DOOMED");
        first.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);

        // The death contract (Server/Combat.cs FinalizeDeadShip): fraction 0,
        // out of the sector.
        var ship = SectorShip(server, shipId)!;
        ship.SetFraction(0f);
        ship.Sector!.Terminate(ship);
        ship.Sector = null;

        using var back = new WireClient();
        back.Connect(server.BoundPort, "DOOMED");
        back.Logon(Request("DOOMED", Token));
        int ack = PumpUntil(server, () => back.LastAck is not null, 4000);
        Assert.True(ack < 4000, "no LOGONACK after a death-in-gap resume attempt");
        Assert.True(back.LastAck!.FValidated);
        Assert.NotEqual(shipId, back.LastAck.ShipID);        // a NEW ship, not the dead one
        Assert.Equal(1, server.ReconnectRefusedTotal);
        Assert.Equal(0, server.ReconnectResumedTotal);
        Assert.True(ship.Dead, "the dead ship stays dead");
        Console.WriteLine($"P813C-DEATH: dead ship {shipId} not resurrected; resumed as fresh ship {back.LastAck.ShipID}");
    }

    /// <summary>⭐ Game over during the gap: the rotation discards the old world,
    /// so the parked seat goes with it (no SHIP_DELETE/QUIT_MISSION to a mission
    /// that is being reset — native's <c>Vacate()</c>/<c>Reset</c>,
    /// <c>fsmission.cpp:2604-2622</c>) and a reconnect afterwards joins the fresh
    /// mission normally instead of resuming into the discarded one.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Game_over_during_the_gap_ends_the_grace()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (survivor, _) = Seat(server, "SURVIVOR");
        var (first, shipId) = Seat(server, "GAPPED");
        first.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);
        var oldMission = server.Missions[0];

        ForceGameOver(server);
        int rotated = PumpUntil(server, () => !ReferenceEquals(server.Missions[0], oldMission), 2000);
        Assert.True(rotated < 2000, "the mission did not rotate");
        Assert.Equal(0, server.ReconnectParkedShipCount);
        Assert.Equal(1, server.ReconnectExpiredTotal);

        using var back = new WireClient();
        back.Connect(server.BoundPort, "GAPPED");
        back.Logon(Request("GAPPED", Token));
        int ack = PumpUntil(server, () => back.LastAck is not null, 4000);
        Assert.True(ack < 4000, "no LOGONACK after a game-over resume attempt");
        Assert.True(back.LastAck!.FValidated);
        Assert.NotEqual(shipId, back.LastAck.ShipID);   // new world, new ship
        Assert.Equal(0, server.ReconnectResumedTotal);
        Console.WriteLine($"P813C-GAMEOVER: grace ended by rotation; reconnect got fresh ship {back.LastAck.ShipID}");
    }

    /// <summary>⭐ The side leader dropping keeps leadership for the window
    /// (the seat is held, nobody left the mission), and the resume lands the same
    /// leader back. Native promotes at once (<c>RemovePlayerFromSide</c> →
    /// <c>pship-&gt;Promote()</c>, <c>fsmission.cpp:895-913</c>); this port runs
    /// that unchanged at window expiry, which is what keeps the choice honest.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side_leader_keeps_leadership_during_the_gap()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSectorZeroSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (leader, leaderShipId) = Seat(server, "LEADER");
        var (member, memberShipId) = Seat(server, "MEMBER", Token + 1);
        Assert.NotEqual(leaderShipId, memberShipId);
        var mission = server.Missions[0];
        short side = mission.Sides[0].Id;
        Assert.Equal(leaderShipId, mission.Sides.First(s => s.Id == side).LeaderShipId);

        leader.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);
        // The leader is still the leader while the seat is held (no succession).
        Assert.Equal(leaderShipId, mission.Sides.First(s => s.Id == side).LeaderShipId);

        using var back = new WireClient();
        back.Connect(server.BoundPort, "LEADER");
        back.Logon(Request("LEADER", Token));
        int resumed = PumpUntil(server, () => server.ReconnectResumedTotal == 1, 4000);
        Assert.True(resumed < 4000, "the leader's seat was not resumed");
        Assert.Equal(leaderShipId, mission.Sides.First(s => s.Id == side).LeaderShipId);

        // Now let the window expire without a resume: leadership passes to the
        // surviving member through the native succession path.
        back.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);
        int expired = PumpUntil(server,
            () => mission.Sides.First(s => s.Id == side).LeaderShipId == memberShipId, 2000);
        Assert.True(expired < 2000,
            $"expiry must run native leader succession (still {mission.Sides.First(s => s.Id == side).LeaderShipId}, expected {memberShipId})");
        Console.WriteLine($"P813C-LEADER: leadership held during the gap, passed to {memberShipId} on expiry");
    }

    /// <summary>Helper — plant a docked spawn (inside a friendly station's dock
    /// radius) so the first tick latches <c>StationId</c>, the same trick
    /// LiveLogoffTests uses.</summary>
    private static void InjectDockedSpawn(Srv server)
    {
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, DockTestPlacement.Inside(mission, station, hull), Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
    }

    /// <summary>⭐ A DOCKED player resumes still docked: the seat holds the same
    /// ship, the same station and the same side, and the replayed join burst
    /// carries a SHIP_STATUS with <c>StateDocked</c> + that station id for the
    /// resumed ship (the existing native message — no new field).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_player_resumes_still_docked()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectDockedSpawn(server);
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (first, shipId) = Seat(server, "DOCKEDBACK");
        int docked = PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId is not null, 400);
        Assert.True(docked < 400, "precondition: the ship docked");
        short stationId = server.Ships.First(s => s.Ship.Id == shipId).Ship.StationId!.Value;
        first.Dispose();
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);

        using var back = new WireClient();
        back.Connect(server.BoundPort, "DOCKEDBACK");
        back.Logon(Request("DOCKEDBACK", Token));
        int resumed = PumpUntil(server, () => back.LastAck is { FValidated: true } && back.Statuses.Count > 0, 4000);
        Assert.True(resumed < 4000, "no resume acknowledgement / no SHIP_STATUS in the burst");
        Assert.Equal(shipId, back.LastAck!.ShipID);
        var ship = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.Equal(stationId, ship.StationId);
        var status = back.Statuses.Last(s => s.ShipId == shipId);
        Assert.Equal(ShipStatusMessage.StateDocked, status.State);
        Assert.Equal(stationId, status.StationId);
        Console.WriteLine($"P813C-DOCKED: ship {shipId} resumed docked at station {stationId} (SHIP_STATUS state=docked)");
    }

    /// <summary>⭐ A TURRET gunner who drops resumes <b>into the turret</b>: the
    /// server keeps the same child ship (parent + turret ids intact) and the
    /// replayed burst's own SHIP_STATUS carries <c>StateTurret</c> + the carrier's
    /// ship id as ParentID — native's own-side status is how the client learns it
    /// is aboard (<c>FedSrv.CPP:3693-3700</c>; the port's <c>MakeOwnShipStatus</c>
    /// writes ParentID + stateTurret).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Turret_gunner_resumes_into_the_same_turret()
    {
        Require.Artwork();
        // 792 = a turret-capable carrier (slots [2,6)), 504 = a normal hull — the
        // same pair LiveLogoffTests/LiveBoardShipTests use.
        const short carrierHullId = 792;
        const short boarderHullId = 504;
        using var server = new Srv();
        int spawnIndex = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sideId = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0).SideId;
            // A capital-landing home (the LiveLogoffTests fixture): hull 792 cannot
            // fit the map garrison's fighter bays, and boarding gates on the
            // carrier being DOCKED (StationId != null).
            var station = P550TestDockHome.CapLandHome(mission, sideId);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var hull = core.HullTypes[spawnIndex == 0 ? carrierHullId : boarderHullId];
            var pos = new Vector3(station.Position[0] + 25f + spawnIndex * 10f, station.Position[1], station.Position[2]);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            if (spawnIndex == 0)
            {
                var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
                ship.MountPart(weapon, 1, (byte)hull.MaxFixedWeapons, 255);
            }
            spawnIndex++;
            return (ship, sector);
        };
        server.Boot(Config(reconnect: true, graceSeconds: 30));
        StartMission(server);

        var (carrierClient, carrierId) = Seat(server, "TURRET-CARRIER");
        var (gunnerClient, gunnerId) = Seat(server, "TURRET-GUNNER");
        var gunnerShip = server.Ships.First(s => s.Ship.Id == gunnerId).Ship;
        // Role-match by hull (socket order is racy): whichever seat is the 792 is
        // the carrier, and the gunner boards it.
        if (gunnerShip.HullType.HullId == carrierHullId)
        {
            (carrierClient, gunnerClient) = (gunnerClient, carrierClient);
            (carrierId, gunnerId) = (gunnerId, carrierId);
            gunnerShip = server.Ships.First(s => s.Ship.Id == gunnerId).Ship;
        }
        // Both seats must be docked before a board is legal (native gates on the
        // carrier being in a station, Server.cs board path).
        var carrierShip = server.Ships.First(x => x.Ship.Id == carrierId).Ship;
        int dockedBoth = PumpUntil(server, () => carrierShip.StationId is not null && gunnerShip.StationId is not null, 600);
        Assert.True(dockedBoth < 600, "precondition: both ships docked");
        gunnerClient.SendBoard(carrierId);
        int boarded = PumpUntil(server, () => gunnerShip.ParentShip?.Id == carrierId && gunnerShip.TurretId is not null, 800);
        Assert.True(boarded < 800, "precondition: the gunner boarded the carrier's turret");

        gunnerClient.Dispose();   // the gunner's socket drops mid-fight
        PumpUntil(server, () => server.ReconnectParkedShipCount == 1, 400);
        Assert.True(server.Ships.All(s => s.Ship.Id != gunnerId), "the gunner is uncommanded while parked");

        using var back = new WireClient();
        back.Connect(server.BoundPort, "TURRET-GUNNER");
        back.Logon(Request("TURRET-GUNNER", Token));
        int resumed = PumpUntil(server, () => back.LastAck is { FValidated: true } && back.Statuses.Count > 0, 4000);
        Assert.True(resumed < 4000, "no resume acknowledgement / no SHIP_STATUS in the burst");
        Assert.Equal(gunnerId, back.LastAck!.ShipID);
        Assert.Equal(carrierId, gunnerShip.ParentShip?.Id);
        Assert.NotNull(gunnerShip.TurretId);
        var status = back.Statuses.Last(s => s.ShipId == gunnerId);
        Assert.Equal(ShipStatusMessage.StateTurret, status.State);
        Assert.Equal(carrierId, status.ParentId);
        Console.WriteLine($"P813C-TURRET: gunner ship {gunnerId} resumed into turret {gunnerShip.TurretId} of carrier {carrierId}");
    }

    /// <summary>The CLI switches the rig recipe uses map onto the config (and the
    /// window implies enabled), and the defaults stay the native immediate
    /// removal.</summary>
    [Fact]
    public void Cli_switches_enable_and_size_the_grace_window()
    {
        var defaults = new Cfg();
        Assert.False(defaults.Reconnect.Enabled);
        Assert.Equal(5, defaults.Reconnect.GraceSeconds);

        var enabled = new Cfg();
        global::Allegiance.Server.ServerConfigLoader.ApplyCli(enabled, [("reconnect", "true")]);
        Assert.True(enabled.Reconnect.Enabled);

        var sized = new Cfg();
        global::Allegiance.Server.ServerConfigLoader.ApplyCli(sized, [("reconnectGrace", "2.5")]);
        Assert.True(sized.Reconnect.Enabled);
        Assert.Equal(2.5, sized.Reconnect.GraceSeconds);
        sized.Reconnect.Validate();

        Assert.Throws<InvalidDataException>(() =>
            new global::Allegiance.Server.ReconnectConfig { GraceSeconds = 0 }.Validate());
    }
}
