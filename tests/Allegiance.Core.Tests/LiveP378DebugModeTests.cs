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
/// P378 — the config-driven "debug mode" / lifepod-endurance override, proven
/// live through the server's REAL default spawner (no injected ShipSpawner),
/// so the config → boot-resolution → ship override → hull-swap deadline wiring
/// is exercised end to end, not mocked.
/// <para>
/// For each config shape the server is booted over loopback TCP, a peer joins
/// (its ship is created by <c>SpawnDefaultShip</c>, which stamps the resolved
/// override onto the Ship), and the real <see cref="Ship.ChangeHull"/> to the
/// side's Lifepod hull is performed — the exact hull-swap the death / eject /
/// rescue paths use. We then read the ARMED <see cref="Ship.LifepodTimeToDie"/>
/// and measure (deadline − now), i.e. the actual oxygen-endurance seconds the
/// engine will count down:
///   • no liveSettings        → ~300 s  (the real native 5-minute value, unchanged)
///   • debugMode:true         → ~1800 s (30-minute testing window)
///   • lifepodEnduranceSeconds:600 → ~600 s (explicit tunable wins, debug off)
/// We verify the DEADLINE VALUE directly (sufficient per the task) rather than
/// waiting the literal 5/30 real minutes.
/// </summary>
[Collection("LiveNetwork")]
public class LiveP378DebugModeTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port, Ls live) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: WireClient peers never send POSITIONREQ
        LiveSettings = live,
        Games =
        [
            new Gm { Name = "P378 Debug Mode Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 922u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9710000000000000000ul, DrmHash = "p378",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

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

    private static (Ship ship, double measuredEndurance) JoinAndMeasure(Srv server, int port, string name)
    {
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var c = new WireClient();
        c.Connect((ushort)port, name);
        c.Logon(Request(name));
        int n = PumpUntil(server, () => c.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, "peer never got a ship assignment (default spawner)");

        var mission = server.Missions[0];
        short id = c.ShipAssignment!.Ship.ShipId;
        var ship = server.Ships.First(s => s.Ship.Id == id).Ship;
        Assert.NotNull(ship.SideId);

        short lifepodId = mission.Sides.First(s => s.Id == ship.SideId!.Value).Civilization.Lifepod;
        Assert.True(mission.Core.HullTypes.TryGetValue(lifepodId, out var lp), "lifepod hull in core");
        ship.ChangeHull(lp!);                                    // the real death/eject/rescue hull-swap

        Assert.NotNull(ship.LifepodTimeToDie);                    // timer armed by the swap
        double measured = ship.LifepodTimeToDie!.Value - mission.LastUpdate;
        return (ship, measured);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void No_live_settings_preserves_native_300s_endurance()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(0, new Ls()));                         // absent section == all defaults
        var (ship, measured) = JoinAndMeasure(server, server.BoundPort, "P378DEF");
        Assert.Null(ship.LifepodEnduranceOverrideSeconds);         // no override stamped on the ship
        Assert.True(Math.Abs(measured - 300.0) < 10.0, $"expected ~300s native endurance, got {measured:0.#}s");
        Console.WriteLine($"[oxygen] P378 debug-off: real spawned ship {ship.Id} armed deadline = now+{measured:0.#}s (core FloatConstants[31]=300s) — unchanged from pre-P378.");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Debug_mode_defaults_to_1800s_endurance()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(0, new Ls { DebugMode = true }));
        var (ship, measured) = JoinAndMeasure(server, server.BoundPort, "P378DBG");
        Assert.Equal(Ls.DebugDefaultLifepodEnduranceSeconds, ship.LifepodEnduranceOverrideSeconds); // 1800 stamped
        Assert.True(Math.Abs(measured - 1800.0) < 10.0, $"expected ~1800s debug endurance, got {measured:0.#}s");
        Console.WriteLine($"[oxygen] P378 debug-ON: real spawned ship {ship.Id} armed deadline = now+{measured:0.#}s (30-min testing window).");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Explicit_lifepodEnduranceSeconds_wins_over_debug_off()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(0, new Ls { LifepodEnduranceSeconds = 600.0 }));   // debug off, explicit tunable
        var (ship, measured) = JoinAndMeasure(server, server.BoundPort, "P378EXP");
        Assert.Equal(600.0, ship.LifepodEnduranceOverrideSeconds!.Value);
        Assert.True(Math.Abs(measured - 600.0) < 10.0, $"expected ~600s explicit endurance, got {measured:0.#}s");
        Console.WriteLine($"[oxygen] P378 explicit 600s: real spawned ship {ship.Id} armed deadline = now+{measured:0.#}s.");
    }
}
