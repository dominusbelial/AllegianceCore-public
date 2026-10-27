using System.Reflection;
using System.Text;
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
/// P855 / SIM-0809 — trap-hack validation failures produce one structured
/// operator-log line, rate-limited. Native <c>LogTrapHack</c>
/// (<c>FedSrv/FedSrv.CPP:2749-2798</c>, macros :4662-4665) reports who, which
/// check and the native source line for every message-validation failure. The
/// port keeps its log-and-ignore stance (no disconnect — recorded design), but
/// the refusals now emit the structured <c>[traphack]</c> event, and a
/// spamming client cannot flood the operator log (per-connection window).
/// </summary>
public sealed class P855TrapHackTests
{
    // ---- unit: the rate limiter and the line shape ----

    [Fact]
    public void Limiter_allows_burst_within_window_then_suppresses()
    {
        long now = 1_000;
        var lim = new global::Allegiance.Server.TrapHackRateLimiter(maxPerWindow: 3, windowMs: 1_000, nowMs: () => now);
        Assert.True(lim.TryAcquire("c1"));
        Assert.True(lim.TryAcquire("c1"));
        Assert.True(lim.TryAcquire("c1"));
        Assert.False(lim.TryAcquire("c1"));   // 4th in the same window — suppressed
        Assert.False(lim.TryAcquire("c1"));

        Assert.Equal(2, lim.TakeSuppressed("c1"));            // the two suppressed events are reported once
        Assert.Equal(0, lim.TakeSuppressed("c1"));
        now += 1_001;                                          // window rolls over
        Assert.True(lim.TryAcquire("c1"));
        Assert.Equal(0, lim.TakeSuppressed("c1"));            // nothing suppressed since the last take
    }

    [Fact]
    public void Limiter_buckets_are_per_connection()
    {
        long now = 5_000;
        var lim = new global::Allegiance.Server.TrapHackRateLimiter(maxPerWindow: 1, windowMs: 1_000, nowMs: () => now);
        Assert.True(lim.TryAcquire("a"));
        Assert.False(lim.TryAcquire("a"));
        Assert.True(lim.TryAcquire("b"));      // a different source is not throttled by a's spam
        now += 1_001;
        Assert.Equal(1, lim.TakeSuppressed("a"));
        Assert.Equal(0, lim.TakeSuppressed("b"));
    }

    [Fact]
    public void Operator_line_carries_kind_check_site_player_ship_connection()
    {
        string line = global::Allegiance.Server.TrapHackEvents.FormatLine(
            global::Allegiance.Server.TrapHackKind.Boot,
            player: "BadPilot\r\nFAKE", shipId: 7, connection: "42",
            check: "pfsPlayer->GetIGCShip()->GetParentShip() == NULL",
            nativeSite: "FedSrv.CPP:6509");

        Assert.StartsWith("[traphack] kind=boot ", line);
        Assert.Contains("check=\"pfsPlayer->GetIGCShip()->GetParentShip() == NULL\"", line);
        Assert.Contains("site=FedSrv.CPP:6509", line);
        Assert.Contains("player=\"BadPilot", line);          // control chars escaped...
        Assert.DoesNotContain("\r", line);                    // ...never a raw CR (log-forging)
        Assert.Contains("ship=7", line);
        Assert.Contains("conn=42", line);
        Assert.Contains("action=log-and-ignore", line);       // the recorded design decision
    }

    [Fact]
    public void Operator_line_omits_absent_fields_and_supports_boot_no_player()
    {
        string line = global::Allegiance.Server.TrapHackEvents.FormatLine(
            global::Allegiance.Server.TrapHackKind.BootNoPlayer,
            player: null, shipId: null, connection: "9",
            check: "NULL != FM_VAR_REF(pfmLogon, CharacterName)",
            nativeSite: "FedSrv.CPP:5817");
        Assert.StartsWith("[traphack] kind=boot-no-player ", line);
        Assert.DoesNotContain("player=", line);
        Assert.DoesNotContain("ship=", line);
        Assert.Contains("conn=9", line);
    }

    // ---- integration: a live validation failure reaches the operator log ----

    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P855", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [1, 2, 3, 4],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p855",
    };

    private static void Inject(Srv s, ConnectionId from, byte[] packet)
    {
        var m = typeof(Srv).GetMethod("ProcessDatagram", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(s, new object[] { from, packet });
    }

    private static ConnectionId ConnOfShip(Srv s, short shipId)
    {
        var f = typeof(Srv).GetField("_peerShips", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var map = (System.Collections.IDictionary)f.GetValue(s)!;
        foreach (System.Collections.DictionaryEntry e in map)
        {
            var live = e.Value!;
            var ship = live.GetType().GetProperty("Ship")!.GetValue(live)!;
            int id = (short)ship!.GetType().GetProperty("Id")!.GetValue(ship)!;
            if (id == shipId) return (ConnectionId)e.Key;
        }
        throw new Exception($"no seated peer with ship {shipId}");
    }

    private static int Pump(Srv s, Func<bool> done, int max = 5000)
    {
        int n = 0;
        while (!done() && n < max) { s.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static List<string> CaptureConsole(Action body)
    {
        var log = new StringBuilder();
        var saved = Console.Out;
        Console.SetOut(new StringWriter(log));
        try { body(); }
        finally { Console.SetOut(saved); }
        return log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Out_of_range_wing_id_emits_one_traphack_event_and_flood_is_rate_limited()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "b0");
        bot.SendLogonRequest(Request("p855_hack0"));
        Pump(server, () => bot.Assignments.Count >= 1);
        short ship0 = bot.Assignments[0].Ship.ShipId;
        var conn0 = ConnOfShip(server, ship0);

        // Attack: 20 out-of-range SET_WINGID batches in one burst (native
        // TrapHackBoot(pfmSW->wingID < c_widMax), FedSrv.CPP:5324). The operator
        // log must show the structured event — but only the rate-limited burst,
        // not 20 lines.
        List<string> lines = CaptureConsole(() =>
        {
            for (int i = 0; i < 20; i++)
                Inject(server, conn0, FedPacket.Pack(new[] { new SetWingId { ShipID = ship0, WingId = 250 }.Encode() }));
            Pump(server, () => false, max: 50);
        });

        var events = lines.Where(l => l.StartsWith("[traphack] kind=boot ")).ToList();
        Assert.NotEmpty(events);
        Assert.True(events.Count <= 8, $"expected the burst to be rate-limited to <= 8 events, got {events.Count}");
        Assert.All(events, e => Assert.Contains("check=\"pfmSW->wingID < c_widMax\"", e));
        Assert.Contains("site=FedSrv.CPP:5324", events[0]);
        Assert.Contains("ship=" + ship0, events[0]);
        Assert.Contains("player=\"p855_hack0\"", events[0]);
    }
}
