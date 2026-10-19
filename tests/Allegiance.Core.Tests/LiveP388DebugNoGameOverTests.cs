using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P388 — debug mode must NOT end the mission just because the last real
/// player disconnected. The P379/P381/P382/P387 unattended autonomous test
/// arena is designed to run with ZERO players (NPC miners undock/warp/mine/
/// sell/redock on their own); when the last connected client leaves,
/// <c>CheckForVictoryByInactiveSides</c> (<c>Server.Lobby.cs</c>, the port of
/// native fsmission.cpp:5371-5427) would otherwise count ZERO sides with
/// players and declare a draw — flipping the mission to
/// <see cref="MissionStage.Over"/> and freezing the arena, the exact symptom
/// live-tested this session right after P387 landed. P388 gates that
/// population check off in debug mode: only a genuine combat/conquest outcome
/// (which goes through the shared <c>DeclareGameOver</c> via
/// <c>OnVictoryByStationsComputed</c> — untouched) may end a debug mission.
/// <para>
/// Two live proofs, mirroring the <c>InitialMinerSpawnLiveTests</c> /
/// <c>LiveWinConditionTests</c> harness (in-process real Allegiance.Server
/// over loopback TCP):
/// <list type="number">
/// <item><b>Debug mode survives the disconnect and keeps cycling</b>: boot the
///      real m2xspiderw map in debug mode (auto-starts, P387; 2 initial
///      docked miners), connect a real client, disconnect it — the mission
///      stays <b>not</b> <see cref="MissionStage.Over"/> and, with zero clients
///      connected afterwards, both autonomous miners each complete at least
///      two full mine-sell cycles (undock → warp to Alderan → mine He3 →
///      warp home → dock &amp; sell, <see cref="Ship.Ore"/> reset to 0).</item>
/// <item><b>Production (debugMode off) is unchanged</b>: a normal game still
///      declares the draw/game-over when the last real player disconnects —
///      <see cref="MissionStage.Over"/>, exactly as before this change.</item>
/// </list>
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public sealed class LiveP388DebugNoGameOverTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(bool debugMode, string map = "m2xspiderw.igc") => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = debugMode ? new Ls { DebugMode = true } : new Ls(),
        Games =
        [
            new Gm { Name = "P388 Arena", Map = map, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 933u, DwCookie = 0x933u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9440000000000000000ul, DrmHash = "p388",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    /// <summary>A real loopback TCP client — the "connected client" of the
    /// repro. Disposing it abruptly drops the connection, which the server
    /// observes as a peer left → <c>ReleasePeer</c> → the P178 victory check.</summary>
    private sealed class WireClient : ITransportSite, IDisposable
    {
        private readonly TcpTransport _t;
        private readonly object _gate = new();
        private LogonAck? _ack;
        private SetCluster? _set;

        public LogonAck? Ack { get { lock (_gate) return _ack; } }
        public SetCluster? ShipAssignment { get { lock (_gate) return _set; } }

        public WireClient() => _t = new TcpTransport(this);

        public void Connect(ushort port, string name) => _t.Join("127.0.0.1", port, name);
        public void Logon(LogonRequest req) => _t.Send(ConnectionId.Server, FedPacket.Pack(new[] { req.Encode() }), guaranteed: true);

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
                    }
                }
            }
        }

        public void Dispose() => _t.Shutdown();
    }

    /// <summary>Seat a real client and return its ship id (same pattern as the
    /// other live suites' <c>Seat</c>).</summary>
    private static (WireClient client, short shipId) Seat(Srv server, string name)
    {
        var c = new WireClient();
        c.Connect(server.BoundPort, name);
        c.Logon(Request(name));
        int n = PumpUntil(server, () => c.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
        return (c, c.ShipAssignment!.Ship.ShipId);
    }

    /// <summary>The P382 initial miners: every IsNpc, PilotTypeMiner ship in
    /// the mission (captured from all sectors; docked at spawn).</summary>
    private static List<Ship> Miners(Srv server) =>
        server.Missions[0].Sectors
            .SelectMany(s => s.Models.OfType<Ship>())
            .Where(s => s.IsNpc && s.PilotType == Ship.PilotTypeMiner)
            .ToList();

    /// <summary>A full mine-sell cycle is observed as <see cref="Ship.Ore"/>
    /// rising to a substantial level (mining He3) then falling back to ~0 (the
    /// dock transition sells and zeroes it, ServerHost.DockWithStationEvent).
    /// This pump ticks the sim and samples each miner's Ore every tick,
    /// counting a completed cycle each time Ore crosses the high watermark and
    /// then drops to the low one, until every miner hits <paramref name="targetPer"/>.
    /// No <see cref="Thread.Sleep"/> here: the cycle loop touches no transport,
    /// so it can run as fast as the CPU allows (TickSim advances sim time by dt
    /// regardless of wall time).</summary>
    private static (Dictionary<int, int> counts, int ticks) PumpMineSellCycles(
        Srv server, IReadOnlyList<Ship> miners, int targetPer, int max)
    {
        const float highWater = 0.5f * Ship.MineCapacity;   // genuinely mined most of the bag
        const float lowWater = 5f;
        var high = new Dictionary<int, bool>();
        var counts = new Dictionary<int, int>();
        foreach (var m in miners) { high[m.Id] = false; counts[m.Id] = 0; }

        int n = 0;
        while (n < max && !miners.All(m => counts[m.Id] >= targetPer))
        {
            server.TickSim();
            foreach (var m in miners)
            {
                float ore = m.Ore;
                if (ore >= highWater && !high[m.Id])
                    high[m.Id] = true;
                else if (ore <= lowWater && high[m.Id])
                {
                    high[m.Id] = false;
                    counts[m.Id]++;
                    Console.WriteLine($"[P388-LIVE] miner {m.Id} (side {m.SideId}) completed mine-sell cycle #{counts[m.Id]} — ore sold, back to {ore:0.##}");
                }
            }
            n++;
        }
        return (counts, n);
    }

    // ══ live proof #1 — debug mode survives the disconnect and keeps cycling ══

    /// <summary>⭐ The exact session repro: boot the real debug-mode arena,
    /// connect a real client, disconnect it, and confirm the mission does NOT
    /// end and both autonomous miners keep completing full mine-sell cycles
    /// with ZERO clients connected afterwards. Red when the P388 gate is
    /// removed (the last-player disconnect would declare a draw → Stage Over →
    /// sim frozen → no cycles → the cycle pump times out).</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")] // Expensive corpus/scenario or collision-work budget; docs/testing/TIERS.md.
    [SkippableFact]
    public void Debug_mode_disconnect_does_not_end_mission_and_miners_keep_cycling()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: true));

        var mission = server.Missions[0];
        Assert.NotEqual(MissionStage.NotStarted, mission.Stage);   // P387: debug auto-starts at boot

        var miners = Miners(server);
        Assert.Equal(2, miners.Count);
        // P528: the P382 initial miners are server-internal (no construction
        // seam), so pin the per-second rate base on each discovered miner — the
        // full mine-sell cycle under test is timing-independent, and this keeps
        // the two-cycle proof fast instead of waiting out native fill times.
        foreach (var m in miners) m.MiningRateOverridePerSecond = 100f;

        // The repro: connect a real client...
        var (client, peerShipId) = Seat(server, "P388PEER");
        try
        {
            Assert.Contains(server.Ships, s => s.Ship.Id == peerShipId);
            Assert.True(miners.All(m => m.SideId is not null));
            Flush(server);
            Assert.Equal(MissionStage.Started, mission.Stage);

            // ...then disconnect it — the moment ReleasePeer runs the P178
            // victory check. With the fix the debug-mode arena skips the
            // empty-lobby elimination, so the mission survives.
            client.Dispose();
            int released = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == peerShipId), 800);
            Assert.True(released < 800, "disconnected peer was not released");

            Assert.NotEqual(MissionStage.Over, mission.Stage);
            Console.WriteLine($"[P388-LIVE] debug mode: client {peerShipId} disconnected → mission stage '{mission.Stage}' (NOT Over) — arena survives, 0 peers connected.");

            // With zero clients connected, both autonomous miners must keep
            // cycling (undock→warp→mine→sell→redock) on their own.
            var (counts, ticks) = PumpMineSellCycles(server, miners, targetPer: 2, max: 240000);
            foreach (var m in miners)
            {
                Assert.True(counts[m.Id] >= 2,
                    $"debug-mode miner {m.Id} (side {m.SideId}) completed only {counts[m.Id]} cycle(s) in {ticks} ticks with zero clients connected");
                Console.WriteLine($"[P388-LIVE] debug mode, 0 peers: miner {m.Id} (side {m.SideId}) completed {counts[m.Id]} full mine-sell cycles after the disconnect (ore high→sold, stationId={m.StationId}).");
            }
            Assert.NotEqual(MissionStage.Over, mission.Stage);   // still not over at the end either
        }
        finally
        {
            client.Dispose();
        }
    }

    // ══ live proof #2 — production (debugMode off) still ends correctly ══

    /// <summary>⭐ Regression control: with debugMode OFF a normal game still
    /// declares game-over when the last real player disconnects, exactly as
    /// before this change — the mission flips to <see cref="MissionStage.Over"/>
    /// (draw: zero sides with players). Guards against the P388 gate leaking
    /// into production.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Production_last_player_disconnect_still_ends_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(debugMode: false, map: "XXV_simple.igc"));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180)

        var (solo, soloId) = Seat(server, "P388PROD");
        try
        {
            Assert.Contains(server.Ships, s => s.Ship.Id == soloId);
            Flush(server);
            Assert.Equal(MissionStage.Started, server.Missions[0].Stage);
            // P439 — the shared game-over path rotates _missions[0] to a fresh
            // NotStarted mission; assert the ended stage on the ORIGINAL object.
            var mission0 = server.Missions[0];

            solo.Dispose();                                      // the very last real player leaves
            int ended = PumpUntil(server, () => mission0.Stage == MissionStage.Over, 800);
            Assert.True(ended < 800, "production last-player disconnect did not end the mission (regression!)");
            Console.WriteLine($"[P388-LIVE] production (debugMode off): last player {soloId} disconnected → stage Over after {ended} ticks — game-over/draw intact.");
        }
        finally
        {
            solo.Dispose();
        }
    }
}
