using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P127 verification — <c>FM_CS_LOGOFF(3)</c>, the client-or-server, zero-field
/// "graceful logoff" request (<c>Messages.h</c>: "sent when the client wants to
/// logoff, mirrored back to client"; native handler <c>FedSrv.CPP:5787-5802</c>).
/// Two layers:
///  (1) a codec test pins the exact bytes of the new message — header-only,
///      <c>cbmsg = 4</c>, the identical zero-field shape as <c>SUICIDE</c>(80)
///      (P103) differing only in its direction marker (<c>CS</c> vs <c>C</c>)
///      and fmid;
///  (2) a real loopback in-process-server live proof: a connected player's
///      <c>LOGOFF</c> is echoed verbatim back to the same connection, then the
///      peer is released through the exact same cleanup path an abrupt TCP drop
///      uses (<c>PeerLeftEvt</c> → <c>Server.ReleasePeer</c>) — proven by the
///      peer vanishing from <c>server.Ships</c> (= <c>_peerShips.Values</c>, the
///      broadcast/command set) while the ship <b>object</b> stays in the sector
///      (the port's defined disconnect behaviour: the ship keeps flying under
///      Mission.Update; we do not destroy it). The client then closing its
///      socket triggers the transport's own <c>OnPeerLeft</c>, which is an
///      idempotent second <c>ReleasePeer</c>.
/// Like the other live suites this drives a real in-process <see cref="Srv"/>
/// over loopback TCP; the [Collection("LiveNetwork")] tag keeps it sequential
/// against the other live suites.
/// </summary>
[Collection("LiveNetwork")]
public class LiveLogoffTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Logoff Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 881u, DwCookie = 0x881u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8840000000000000000ul, DrmHash = "logoff",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // ── the new codec ─────────────────────────────────────────────────────

    /// <summary><c>FM_CS_LOGOFF</c>(3) is header-only: encode emits exactly
    /// the 4 bytes <c>{ cbmsg=4, fmid=3 }</c>, decode round-trips and rejects
    /// a foreign fmid / a wrong length (<c>Messages.h</c> — same zero-field
    /// shape as <c>SUICIDE</c>(80), P103).</summary>
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

    // ── the wire client ────────────────────────────────────────────────────

    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;
        private readonly List<Logoff> _logoffs = new();

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }
        public IReadOnlyList<Logoff> Logoffs { get { lock (_gate) return _logoffs.ToArray(); } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);
        public void SendLogoff() => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { new Logoff().Encode() }), guaranteed: true);

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
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    // ── live proof ─────────────────────────────────────────────────────────

    /// <summary>⭐ A connected, placed player sends <c>LOGOFF(3)</c>: the
    /// server echoes the same zero-field message back to the same connection,
    /// then releases the peer through the exact same cleanup path an abrupt
    /// disconnect uses (<c>PeerLeftEvt</c> → <c>ReleasePeer</c>). Proven three
    /// ways: the client receives the <c>Logoff</c> echo; the peer vanishes from
    /// <c>server.Ships</c> (= <c>_peerShips.Values</c>, the broadcast/command
    /// set, what a disconnect also empties); and the ship <b>object</b> still
    /// occupies the sector (the port's defined disconnect behaviour — we reuse
    /// the path, we do not destroy the ship).</summary>
    [Fact]
    public void Logoff_echoes_and_releases_the_peer_via_the_disconnect_path()
    {
        using var server = new Srv();
        server.Boot(Config());
        ushort port = server.BoundPort;

        using var client = new WireClient();
        client.Connect(port, "LOGGER");
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

        // (2) …and the peer was released from the broadcast/command set the
        // same way a disconnect empties it.
        Assert.DoesNotContain(server.Ships, s => s.Ship.Id == myId);

        // (3) the ship object itself stays in the sector — we reused the
        // disconnect path, not destroyed the ship.
        var inSector = server.Missions[0].Sectors
            .SelectMany(s => s.Models).OfType<Ship>()
            .Any(s => s.Id == myId);
        Assert.True(inSector, "logged-off ship should remain in the sector (disconnect semantics)");

        Console.WriteLine($"LOGOFF-LIVE: peer LOGGER ship {myId} got {client.Logoffs.Count} echo(es), " +
                          $"released from Ships, ship-object in-sector={inSector} after {echoed} ticks");
    }

    /// <summary>⭐ Parity with an abrupt drop: a <c>LOGOFF</c> leaves the exact
    /// same observable footprint as the transport's natural disconnect — the
    /// peer is gone from <c>server.Ships</c> and the ship stays in the sector.
    /// A second player who simply disposes its socket (the abrupt case) shows
    /// the identical cleanup; there is no second, parallel mechanism.</summary>
    [Fact]
    public void Logoff_cleanup_matches_an_abrupt_disconnect_footprint()
    {
        using var server = new Srv();
        server.Boot(Config());
        ushort port = server.BoundPort;

        // Player A logs off gracefully; player B just drops (abrupt).
        using var graceful = new WireClient();
        using var abrupt = new WireClient();
        graceful.Connect(port, "GRACEFUL"); abrupt.Connect(port, "ABRUPT");
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
        // …yet both ship objects remain in the sector.
        var inSector = server.Missions[0].Sectors
            .SelectMany(s => s.Models).OfType<Ship>()
            .Where(s => s.Id == gId || s.Id == aId)
            .Select(s => s.Id)
            .ToHashSet();
        Assert.Contains(gId, inSector);
        Assert.Contains(aId, inSector);

        Console.WriteLine($"LOGOFF-LIVE: graceful {gId} and abrupt {aId} both released; " +
                          $"ship-objects in-sector {{ {string.Join(", ", inSector)} }} after {settled} ticks");
    }
}
