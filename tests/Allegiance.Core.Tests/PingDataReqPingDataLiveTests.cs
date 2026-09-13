using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P200 — <c>PINGDATA(194)</c> / <c>REQPINGDATA(195)</c>, the w0dk4
/// player-pings feature: a client asks the server for every connected
/// player's latency roster and the server answers <b>that requester alone</b>
/// with one PINGDATA per live player plus a terminator marker (sentinel
/// <c>shipID = -1, ping = 1</c>), or — inside the 5-second debounce — only
/// the unchanged-window marker. Port of <c>FedSrv.CPP:7745-7806</c>
/// <c>case FM_C_REQPINGDATA</c> and <c>CFMConnection::PingQuery</c>
/// (<c>messagecore.h:217-223</c>). Three deliberately-verified behaviours
/// invert the usual per-mission/per-side conventions, matching native:
/// (1) the roster is whole-SERVER, not mission- or side-scoped; (2) the
/// response is requester-ONLY, not a broadcast; (3) the terminator reuses the
/// exact PINGDATA wire shape with a sentinel shipID.
/// </summary>
[Collection("LiveNetwork")]
public class PingDataReqPingDataLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "PD Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "PD A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "PD B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 900u, DwCookie = 0x900u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8890000000000000000ul, DrmHash = "pd200",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    /// <summary>A bare requester: connected to the server but NOT logged on, so
    /// it has no live player (never enters <c>_peerShips</c>) yet is still a
    /// routable ConnectionId the server can answer. This is the honest reading
    /// of the task's "a connection with 3 other live players": the roster holds
    /// the OTHER players' entries, not the requester's (native iterates
    /// connections-with-a-player, and the requester has none here).</summary>
    private static FltBot BareRequester(Srv server, string name = "PDReq")
    {
        var r = new FltBot();
        r.Connect(server.BoundPort, name);
        PumpUntil(server, () => false, 20);   // let the server register the peer
        return r;
    }

    private static IEnumerable<PingData> PlayerEntries(IEnumerable<PingData> xs) => xs.Where(x => x.ShipId != -1);
    private static IEnumerable<PingData> Markers(IEnumerable<PingData> xs) => xs.Where(x => x.ShipId == -1);

    // ── codec ────────────────────────────────────────────────────────────

    /// <summary>REQPINGDATA(195) is a zero-field client trigger: header only,
    /// cbmsg = 4. Exact bytes pinned, mirroring RandomizeTeams/StartGame.</summary>
    [Fact]
    public void ReqPingData_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)195, ReqPingData.Fmid);
        Assert.Equal(4, ReqPingData.FixedSize);

        byte[] b = new ReqPingData().Encode();
        Assert.Equal(4, b.Length);
        Assert.Equal(4, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b));       // cbmsg
        Assert.Equal(195, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2))); // fmid

        var d = ReqPingData.Decode(b);
        Assert.Equal((ushort)195, d.GetFmid());
    }

    /// <summary>PINGDATA(194) carries {shipID short, ping ushort, loss ushort}
    /// = 10 bytes fixed. Exact byte offsets pinned.</summary>
    [Fact]
    public void PingData_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)194, PingData.Fmid);
        Assert.Equal(10, PingData.FixedSize);

        var msg = new PingData { ShipId = -2, Ping = 9876, Loss = 765 };
        byte[] b = msg.Encode();
        Assert.Equal(10, b.Length);
        Assert.Equal(10, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b));                   // cbmsg
        Assert.Equal(194, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2)));         // fmid
        Assert.Equal(unchecked((short)0xFFFE), System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(4)));  // shipID=-2
        Assert.Equal(9876, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)));         // ping
        Assert.Equal(765, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));          // loss

        var d = PingData.Decode(b);
        Assert.Equal((short)-2, d.ShipId);
        Assert.Equal((ushort)9876, d.Ping);
        Assert.Equal((ushort)765, d.Loss);
    }

    /// <summary>⭐ The terminator/unchanged-window marker's exact bytes are
    /// pinned: <c>shipID = -1, ping = 1, loss = 0</c> (native
    /// pfmPingDataOpenWindow-&gt;shipID=-1, ping=1; loss left 0 by
    /// BEGIN_PFM_CREATE zero-init).</summary>
    [Fact]
    public void Terminator_marker_has_exact_bytes()
    {
        byte[] b = new PingData { ShipId = -1, Ping = 1, Loss = 0 }.Encode();
        Assert.Equal(unchecked((short)-1), System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(4)));
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)));
    }

    // ── the main roster ─────────────────────────────────────────────────

    /// <summary>⭐⭐ The quantitative must-fail-if-removed proof. A bare
    /// requester (connected, not logged on) with 3 OTHER live players on the
    /// server asks for the roster: it receives exactly 3 PINGDATA entries (one
    /// per live player) PLUS the terminator marker — and a second live peer
    /// observes NOTHING (requester-only audience, not a broadcast). Removing
    /// the per-player emission, the requester-only addressing, or the marker
    /// each breaks a distinct assertion here.</summary>
    [Fact]
    public void ReqPingData_full_roster_is_requester_only()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var b = new FltBot(); using var c = new FltBot(); using var d = new FltBot();
        short bId = Seat(server, b, "PDB");
        short cId = Seat(server, c, "PDC");
        short dId = Seat(server, d, "PDD");

        using var req = BareRequester(server);
        req.SendReqPingData();
        int got = PumpUntil(server, () => req.PingDatas.Count >= 4, 2000);
        Assert.True(got < 2000, "requester never received the full roster");

        // Exactly 3 player entries (the OTHER live players) + 1 terminator.
        var players = PlayerEntries(req.PingDatas).ToList();
        var markers = Markers(req.PingDatas).ToList();
        Assert.Equal(3, players.Count);
        Assert.Single(markers);
        Assert.Equal(new HashSet<short> { bId, cId, dId }, players.Select(p => p.ShipId).ToHashSet());

        // Terminator shape: sentinel shipID = -1, ping = 1.
        Assert.Equal((short)-1, markers[0].ShipId);
        Assert.Equal((ushort)1, markers[0].Ping);

        // Second peer (a real seated player) observes nothing.
        Assert.Empty(b.PingDatas);
        Assert.Empty(c.PingDatas);
    }

    /// <summary>⭐⭐ A second REQPINGDATA from the SAME connection within the
    /// 5-second debounce window is answered with ONLY the unchanged-window
    /// marker — no per-player entries (native PingQuery debounce,
    /// messagecore.h:217-223). Must-fail-if-removed: disabling the debounce
    /// makes the second response carry the full roster again.</summary>
    [Fact]
    public void ReqPingData_within_5s_is_debounced_to_marker_only()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var b = new FltBot(); using var c = new FltBot();
        Seat(server, b, "DB"); Seat(server, c, "DC");

        using var req = BareRequester(server);
        req.SendReqPingData();                                   // full roster (records the timestamp)
        Assert.True(PumpUntil(server, () => req.PingDatas.Count >= 3, 2000) < 2000, "first request not answered");
        req.ResetPingDataReceived();

        // Immediate second request — far under 5s of sim time elapses.
        req.SendReqPingData();
        PumpUntil(server, () => req.PingDatas.Count >= 1, 800);

        // Only the unchanged-window marker, no per-player entries.
        Assert.Single(req.PingDatas);
        Assert.Equal((short)-1, req.PingDatas[0].ShipId);
        Assert.Equal((ushort)1, req.PingDatas[0].Ping);
    }

    /// <summary>⭐ After the 5-second debounce window elapses (advanced via the
    /// sim clock, 150 ticks @ 20 upd = 7.5s), a subsequent request produces a
    /// full roster again — proving the debounce is time-bound, not sticky.</summary>
    [Fact]
    public void ReqPingData_after_5s_elapse_produces_full_roster_again()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var b = new FltBot(); using var c = new FltBot();
        Seat(server, b, "EB"); Seat(server, c, "EC");

        using var req = BareRequester(server);
        req.SendReqPingData();                                   // full roster (records the timestamp)
        Assert.True(PumpUntil(server, () => req.PingDatas.Count >= 3, 2000) < 2000, "first request not answered");
        req.ResetPingDataReceived();

        // Elapse > 5s of sim time (150 ticks @ 20 upd = 7.5s).
        PumpUntil(server, () => false, 150);

        req.SendReqPingData();
        int got = PumpUntil(server, () => req.PingDatas.Count >= 3, 2000);
        Assert.True(got < 2000, "post-elapse request did not produce a full roster");
        Assert.Equal(2, PlayerEntries(req.PingDatas).Count());   // the 2 live players
        Assert.Single(Markers(req.PingDatas));
    }

    // ── multi-mission (whole-server, deliberately NON-scoped) ────────────

    /// <summary>⭐⭐ Native iterates the WHOLE server's connections
    /// (<c>g.fm.GetConnections()</c>), not a mission or side — the inverse of
    /// this port's usual per-mission isolation. A requester receives entries
    /// for players in OTHER missions too. Must-fail-if-removed: a
    /// mission-scoped roster would omit mission B's players.</summary>
    [Fact]
    public void ReqPingData_roster_spans_all_missions()
    {
        using var server = new Srv();
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq < 2 ? server.Missions[0] : server.Missions[1];
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(320f, 715f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = null;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a1 = new FltBot(); using var a2 = new FltBot();
        using var b1 = new FltBot(); using var b2 = new FltBot();
        short a1Id = Seat(server, a1, "MA1"); short a2Id = Seat(server, a2, "MA2");   // mission A
        short b1Id = Seat(server, b1, "MB1"); short b2Id = Seat(server, b2, "MB2");   // mission B

        // Verify the spawner actually spread them across the two missions.
        Assert.True(server.Ships.Count(l => ReferenceEquals(l.Ship.Mission, server.Missions[0])) == 2);
        Assert.True(server.Ships.Count(l => ReferenceEquals(l.Ship.Mission, server.Missions[1])) == 2);

        using var req = BareRequester(server);
        req.SendReqPingData();
        int got = PumpUntil(server, () => req.PingDatas.Count >= 5, 2000);
        Assert.True(got < 2000, "requester did not receive the whole-server roster");

        // All FOUR live players — spanning both missions — appear, plus marker.
        var players = PlayerEntries(req.PingDatas).Select(p => p.ShipId).ToHashSet();
        Assert.Equal(new HashSet<short> { a1Id, a2Id, b1Id, b2Id }, players);
        Assert.Single(Markers(req.PingDatas));
    }
}
