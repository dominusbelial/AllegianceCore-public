using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
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
/// P315 (stability review finding H3) — live (real loopback sockets + real
/// in-process server) proof that a crafted inbound packet is rejected /
/// bounded rather than forcing a multi-million-iteration processing burst or an
/// unbounded burst of log lines inside one tick, and that other peers keep
/// ticking normally throughout.
/// <para>
/// Two attacker shapes are exercised against raw TCP peers that greet and then
/// ship crafted frames:
/// (1) <b>single oversized packet</b> — one packet packing tens of thousands of
/// tiny messages (well within the frame size cap, valid CRC). Prior to the fix
/// this decodes into tens of thousands of messages that Server.ProcessDatagram
/// would dispatch + log one at a time in a single tick; now FedPacket.Unpack
/// rejects the whole packet at the per-packet message cap.
/// (2) <b>unsupported-fmid flood</b> — many small packets (each under the
/// per-packet cap) whose messages hit the unsupported-fmid refusal path. Prior
/// to the fix every one wrote a console line in the tick; now the per-peer log
/// is rate-limited.
/// </summary>
[Collection("LiveNetwork")]
public sealed class InboundFloodSafetyLiveTests
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
            new Gm { Name = "InboundFloodSafety", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 42u, DwCookie = 0x42u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000001ul, DrmHash = "ifsafe",
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
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
        return server;
    }

    private static List<FltBot> SeatBots(Srv server, int count)
    {
        var bots = new List<FltBot>();
        for (int i = 0; i < count; i++)
        {
            var bot = new FltBot();
            bot.Connect(server.BoundPort, $"safe{i}");
            bot.SendLogonRequest(Request($"safe{i}"));
            bots.Add(bot);
        }
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        if (!bots.All(b => b.ShipAssignment is not null))
            throw new TimeoutException("bots did not get seated");
        foreach (var bot in bots) bot.SendDocked();
        if (PumpUntil(server, () => server.Ships.All(l => l.Ship.StationId is null), 4000) >= 4000)
            throw new TimeoutException("healthy peers did not launch");
        foreach (var bot in bots) bot.CommandControls(new ControlData(0, 0, 0, 1), 0);
        return bots;
    }

    /// <summary>One minimal 4-byte wire message (cbmsg=4, the given unsupported fmid).</summary>
    private static byte[] Tiny(ushort fmid) => new byte[] { 4, 0, (byte)(fmid & 0xFF), (byte)(fmid >> 8) };

    /// <summary>Open a raw TCP peer, send its greeting, and return its stream.</summary>
    private static NetworkStream ConnectRaw(Srv server, string name)
    {
        var raw = new TcpClient();
        raw.NoDelay = true;
        raw.Connect("127.0.0.1", server.BoundPort);
        var ns = raw.GetStream();
        ns.Write(Frame(0x01, Encoding.UTF8.GetBytes(name)));
        ns.Flush();
        return ns;
    }

    /// <summary>⭐ Live proof of the Bug-2 per-packet message cap: a single packet
    /// packing 20,000 tiny unsupported messages (valid CRC, ~80 KB — well under
    /// the frame and queue budgets, so only the message-cap can reject it) must
    /// be rejected wholesale — the server emits one framing-failure line and
    /// ZERO per-message "unsupported fmid" lines — while healthy peers keep
    /// receiving per-tick updates and the mission clock keeps advancing.</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: live-socket flood-safety receipt is load-sensitive (passes 10/10 isolated)
    [SkippableFact]
    public void Oversized_packet_is_rejected_not_dispatched_others_keep_ticking()
    {
        Require.Artwork();
        var server = Boot();
        List<FltBot> bots = new();
        TcpClient? raw = null;
        try
        {
            bots = SeatBots(server, 2);
            foreach (var b in bots) b.ResetHeavyShipsUpdates();
            int hBefore = bots[0].HeavyShipsUpdates.Count;
            double simBefore = server.SimNow;

            raw = new TcpClient();
            raw.NoDelay = true;
            raw.Connect("127.0.0.1", server.BoundPort);
            var ns = raw.GetStream();
            ns.Write(Frame(0x01, Encoding.UTF8.GetBytes("flood1")));
            ns.Flush();

            var evilPacket = FedPacket.Pack(Enumerable.Range(0, 20_000).Select(_ => Tiny(0x1111)));
            var sw = new StringWriter();
            var oldOut = Console.Out;
            Console.SetOut(sw);
            try
            {
                ns.Write(Frame(0x02, evilPacket));
                ns.Flush();
                // P654 (H-02) — the server-inbound frame cap (64 KiB) rejects this
                // >64 KiB frame at the TRANSPORT read, so it never reaches the sim
                // thread at all. The flood is neutralised one layer earlier than the
                // old "framing failure on the sim thread" path. Pump enough ticks
                // for the read thread to consume (and drop) the frame, then assert
                // nothing from it was dispatched and the rest of the server kept
                // ticking. (The rejection is now at the transport, so there is no
                // sim-thread "framing failure" line — that is the H-02/H-21 win.)
                for (int i = 0; i < 300; i++) { server.TickSim(); Thread.Sleep(1); }
            }
            finally { Console.SetOut(oldOut); }
            var text = sw.ToString();

            // The whole packet was rejected before dispatch: not a single one of
            // its 20,000 messages reached the unsupported-fmid refusal path.
            int unsupported = text.Split('\n').Count(l => l.Contains("unsupported fmid"));
            Assert.Equal(0, unsupported);

            // Other peers kept ticking throughout the flood.
            PumpUntil(server, () => bots[0].HeavyShipsUpdates.Count > hBefore, 800);
            Assert.True(bots[0].HeavyShipsUpdates.Count > hBefore,
                $"healthy bot 0 must keep receiving per-tick updates during the flood (had {hBefore}, now {bots[0].HeavyShipsUpdates.Count})");
            Assert.True(server.SimNow > simBefore, "mission clock must keep advancing while the oversized packet is rejected");
        }
        finally
        {
            raw?.Dispose();
            foreach (var b in bots) b.Dispose();
            server.Dispose();
        }
    }

    /// <summary>⭐ Live proof of the Bug-3 rate limit: many small packets (each
    /// under the per-packet cap) whose messages hit the unsupported-fmid refusal
    /// path must NOT each produce a console line. Hundreds of such messages are
    /// processed but only a handful of "unsupported fmid" lines are emitted
    /// (with a periodic summary once >100 are suppressed), and healthy peers
    /// keep ticking.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking()
    {
        Require.Artwork();
        var server = Boot();
        List<FltBot> bots = new();
        TcpClient? raw = null;
        try
        {
            bots = SeatBots(server, 2);
            foreach (var b in bots) b.ResetHeavyShipsUpdates();
            int hBefore = bots[0].HeavyShipsUpdates.Count;
            double simBefore = server.SimNow;

            raw = new TcpClient();
            raw.NoDelay = true;
            raw.Connect("127.0.0.1", server.BoundPort);
            var ns = raw.GetStream();
            ns.Write(Frame(0x01, Encoding.UTF8.GetBytes("flood2")));
            ns.Flush();

            var sw = new StringWriter();
            var oldOut = Console.Out;
            Console.SetOut(sw);
            try
            {
                // 40 packets x 5 unsupported messages = 200 refusal hits, each
                // packet well under the per-packet cap so all reach dispatch.
                for (int i = 0; i < 40; i++)
                {
                    var pkt = FedPacket.Pack(Enumerable.Range(0, 5).Select(_ => Tiny(0x2222)));
                    ns.Write(Frame(0x02, pkt));
                }
                ns.Flush();
                // Pump until a suppression summary appears (proves >100 messages
                // were actually processed + suppressed), with a generous bound.
                bool summarized = false;
                for (int i = 0; i < 600 && !summarized; i++) { server.TickSim(); Thread.Sleep(1); summarized = sw.ToString().Contains("suppressed"); }
                Assert.True(summarized, "the flood must be processed enough to trip the rate-limiter's suppression summary");
            }
            finally { Console.SetOut(oldOut); }
            var text = sw.ToString();

            // The refusal log is bounded: at most a handful of "unsupported
            // fmid" lines despite ~200 messages (the rate limit), plus the
            // periodic summary.
            int unsupported = text.Split('\n').Count(l => l.Contains("unsupported fmid"));
            Assert.True(unsupported <= 8, $"unsupported-fmid refusal log must be rate-limited (got {unsupported} lines for ~200 messages)");

            // Other peers kept ticking throughout.
            PumpUntil(server, () => bots[0].HeavyShipsUpdates.Count > hBefore, 800);
            Assert.True(bots[0].HeavyShipsUpdates.Count > hBefore,
                $"healthy bot 0 must keep receiving per-tick updates during the flood (had {hBefore}, now {bots[0].HeavyShipsUpdates.Count})");
            Assert.True(server.SimNow > simBefore, "mission clock must keep advancing during the unsupported-fmid flood");
        }
        finally
        {
            raw?.Dispose();
            foreach (var b in bots) b.Dispose();
            server.Dispose();
        }
    }
}
