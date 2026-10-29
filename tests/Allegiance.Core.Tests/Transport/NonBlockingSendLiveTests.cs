using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// Live (real loopback sockets + real in-process server) proof of P311: the
/// server's simulation thread must <b>never</b> block on socket I/O to a
/// peer, so one stalled (never-reading) client cannot freeze every player —
/// the single most severe stability finding (C1) from the full-codebase
/// review. Several real bots play normally while a raw, seated peer stops
/// reading its socket; the others must keep receiving per-tick broadcasts and
/// the mission clock must keep advancing, and the stalled peer must eventually
/// be disconnected rather than accumulating an unbounded backlog.
///
/// The stalled peer is a raw TCP client that speaks just enough of the wire
/// protocol (Greet + LOGONREQ + POSITIONREQ) to get <em>seated</em> into
/// <c>_peerShips</c> — so the server actually broadcasts to it every tick —
/// and then never reads again.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NonBlockingSendLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "NonBlockingSend", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 42u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000001ul, DrmHash = "nbsend",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Wrap <paramref name="body"/> in the transport's length-prefixed frame.</summary>
    private static byte[] Frame(byte type, byte[] body)
    {
        var f = new byte[1 + 4 + body.Length];
        f[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(1), (uint)body.Length);
        body.CopyTo(f.AsSpan(5));
        return f;
    }

    /// <summary>Boot a server that seats every peer (real bots and the stalled
    /// raw peer alike) in <c>Sectors[0]</c> with a non-zero velocity, so each
    /// peer is a per-tick broadcast candidate to the others.</summary>
    private static Srv Boot()
    {
        var server = new Srv();
        int placed = 0;
        server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(placed * 120f, 0f, 0f), new Vector3(5f, 0f, 0f), Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        server.Boot(Config());
        return server;
    }

    /// <summary>Connect and seat <paramref name="count"/> real bots; each returns
    /// its <see cref="FltBot"/> and (via ShipSpawner) lands in <c>Sectors[0]</c>.</summary>
    private static List<FltBot> SeatBots(Srv server, int count)
    {
        var bots = new List<FltBot>();
        for (int i = 0; i < count; i++)
        {
            var bot = new FltBot();
            bot.Connect(server.BoundPort, $"nb{i}");
            bot.SendLogonRequest(Request($"nb{i}"));
            bots.Add(bot);
        }
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        if (!bots.All(b => b.ShipAssignment is not null))
            throw new TimeoutException("bots did not get seated");
        return bots;
    }

    /// <summary>Open a raw socket, speak Greet + LOGONREQ + POSITIONREQ so the
    /// server seats the peer into <c>_peerShips</c>, then <b>never read again</b> —
    /// a genuinely stalled client the server keeps broadcasting to. The caller
    /// returns the live <see cref="TcpClient"/> (held open, never drained).</summary>
    private static TcpClient ConnectStalledSeatedPeer(Srv server, string name)
    {
        var raw = new TcpClient();
        raw.NoDelay = true;
        raw.ReceiveBufferSize = 1024;   // before connect so the OS window stays tiny
        raw.Connect("127.0.0.1", server.BoundPort);
        var ns = raw.GetStream();
        ns.Write(Frame(0x01, Encoding.UTF8.GetBytes(name)));
        ns.Flush();
        ns.Write(Frame(0x02, FedPacket.Pack(new[] { Request(name).Encode() })));
        ns.Flush();
        // let the server process the logon before the side-select request
        PumpUntil(server, () => server.Ships.Count >= 1, 600);
        ns.Write(Frame(0x02, FedPacket.Pack(new[] { new PositionReq { SideId = -1 }.Encode() })));
        ns.Flush();
        return raw;
    }

    /// <summary>⭐ Several real bots keep receiving per-tick broadcasts and the
    /// mission clock keeps advancing while a raw, seated peer has stopped reading
    /// its socket; and the stalled peer is eventually disconnected (not queued
    /// forever). This is the core anti-freeze claim of P311.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Perf")]
    public void Others_keep_ticking_and_mission_advances_while_one_peer_stalls_then_disconnects()
    {
        Require.Artwork();
        var oldT = TcpTransport.OverflowTimeout;
        var oldN = TcpTransport.NoWriteProgressTimeout;
        var oldC = TcpTransport.CapacityBytes;
        TcpTransport.OverflowTimeout = TimeSpan.FromSeconds(1);
        TcpTransport.NoWriteProgressTimeout = TimeSpan.FromSeconds(1);
        TcpTransport.CapacityBytes = 64 * 1024;
        var server = Boot();
        List<FltBot> bots = new();
        TcpClient? stalled = null;
        try
        {
            bots = SeatBots(server, 3);
            int realCount = server.Ships.Count;
            Assert.Equal(3, realCount);

            foreach (var b in bots) b.ResetHeavyShipsUpdates();

            // Introduce the stalled (never-reading) seated peer. Named with the
            // harness prefix so it rides the STRICT P611 bot liveness limit (a
            // connected human — non-harness name — would get the longer P619c
            // grace and correctly survive this window).
            stalled = ConnectStalledSeatedPeer(server, "mcp-STALLED");
            var seated = PumpUntil(server, () => server.Ships.Count == 4, 2000);
            Assert.True(server.Ships.Count == 4, $"stalled peer must get seated; Ships.Count={server.Ships.Count} after {seated} ticks");

            // ---- Phase A: while the stalled peer is present -------------------
            // Snapshot the healthy peers' per-tick reception + the mission clock.
            int aBefore = bots[0].HeavyShipsUpdates.Count;
            double nowBefore = server.SimNow;
            int ticks = 60; // ~3 s of simulation
            int pumped = 0;
            var wall = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++) { server.TickSim(); pumped++; Thread.Sleep(1); }
            wall.Stop();
            double nowAfter = server.SimNow;
            int aDuring = bots[0].HeavyShipsUpdates.Count;

            // The sim thread must not be stalled by the stuck peer: 60 ticks of
            // mission time advance in well under 5 s of wall clock. Under the old
            // synchronous-send behavior each tick blocks ~SendTimeout against the
            // stalled peer, blowing far past this.
            Assert.True(wall.Elapsed < TimeSpan.FromSeconds(5),
                $"sim thread must not stall on a stuck peer; 60 ticks took {wall.ElapsedMilliseconds} ms wall clock");
            // The mission clock kept advancing (tick-driven sim, not frozen).
            double expectedDelta = (double)pumped / 20.0;
            Assert.True(nowAfter - nowBefore >= expectedDelta * 0.9,
                $"mission clock must keep advancing while a peer stalls; {nowAfter - nowBefore:F3}s < {expectedDelta:F3}s");
            // Healthy peers kept receiving per-tick broadcasts throughout.
            Assert.True(aDuring > aBefore, $"healthy bot 0 must keep receiving per-tick updates while one peer stalls (had {aBefore}, now {aDuring})");
            foreach (var b in bots.Skip(1))
                Assert.True(b.HeavyShipsUpdates.Count > 0, "all healthy bots keep receiving per-tick updates");

            // ---- Phase B: the stalled peer is eventually disconnected ---------
            int discTicks = PumpUntil(server, () => server.Ships.Count == 3, 2000 /* up to ~100 s, but knob makes it fast */);
            Assert.True(server.Ships.Count == 3,
                $"stalled peer must be disconnected rather than accumulating an unbounded backlog (still {server.Ships.Count} after {discTicks} ticks)");

            // The server is still healthy after the disconnect — healthy peers
            // keep ticking normally.
            int aAfterDisc = bots[0].HeavyShipsUpdates.Count;
            for (int i = 0; i < 20; i++) { server.TickSim(); Thread.Sleep(1); }
            Assert.True(bots[0].HeavyShipsUpdates.Count > aAfterDisc,
                "healthy bots keep receiving updates after the stalled peer is disconnected");

            Console.WriteLine($"[P311-LIVE] 3 bots + 1 stalled seated peer: sim advanced {nowAfter - nowBefore:F2}s over {pumped} ticks while peer stalled; " +
                              $"bot0 per-tick updates {aBefore}->{aDuring}->{aAfterDisc}; stalled peer disconnected after ~{discTicks} ticks.");
        }
        finally
        {
            stalled?.Dispose();
            foreach (var b in bots) b.Dispose();
            server.Dispose();
            TcpTransport.OverflowTimeout = oldT;
            TcpTransport.NoWriteProgressTimeout = oldN;
            TcpTransport.CapacityBytes = oldC;
        }
    }
}
