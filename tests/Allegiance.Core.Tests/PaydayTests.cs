using System.Collections;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
// 'Server' and 'Bot' are ALSO namespace names (Allegiance.Server, Allegiance.Bot)
// reachable from this namespace, so the simple names are ambiguous with the
// namespaces. Aliased to distinct, unambiguous identifiers instead.
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// Proves the P51 deliverable: payday runs on a flat 60s cadence, accrues the
/// reference formula <c>600 · (1 + IncomeMoney)</c> per side, and broadcasts a
/// real <c>FM_S_PAYDAY(114)</c> to each side's own connected peers. Verified
/// three ways — the codec round-trips (units), the mission accrual fires once
/// per boundary at the exact cadence (unit via a <see cref="RecordingIgcSite"/>),
/// and the full stack emits the wire message over real loopback TCP to a
/// decoding <see cref="FltBot"/> whose per-side <c>dMoney</c> matches an
/// independently computed formula. The zero-player side is covered: a side with
/// no connected peers accrues but never pays, matching the reference
/// <c>if (np != 0)</c> guard (<c>fsmission.cpp:2298-2299</c>) — no crash, no
/// fabrication.
/// </summary>
[Collection("LiveNetwork")]
public class PaydayTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // ── the wire codec ─────────────────────────────────────────────────────

    /// <summary><c>FM_S_PAYDAY(114)</c> round-trips exactly — header plus the
    /// single 4-byte <c>Money dMoney</c> field, cbmsg = 8 (Messages.h:747).</summary>
    [Fact]
    public void Payday_message_round_trips()
    {
        var msg = new Payday { DMoney = 637 };
        var d = Payday.Decode(msg.Encode());
        Assert.Equal(Payday.Fmid, d.GetFmid());
        Assert.Equal(637, d.DMoney);
    }

    /// <summary><c>FM_S_PAYDAY(114)</c> emits the exact 8 declared bytes,
    /// hand-composed from <c>Messages.h:747</c> + <c>AGCIDL.idl:246</c> (Money =
    /// native 32-bit long, so the payload is a 4-byte signed int32, not a C#
    /// long). A negative value tests sign handling, since a valid Money is a
    /// signed 32-bit integer.</summary>
    [Fact]
    public void Payday_message_emits_exact_declared_bytes()
    {
        var msg = new Payday { DMoney = -500 };
        var got = msg.Encode();

        void u16(int off, int v) { got[off] = (byte)(v & 0xFF); got[off + 1] = (byte)((v >> 8) & 0xFF); }
        void i32(int off, int v)
        {
            got[off] = (byte)(v & 0xFF); got[off + 1] = (byte)((v >> 8) & 0xFF);
            got[off + 2] = (byte)((v >> 16) & 0xFF); got[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        Assert.Equal(8, got.Length);               // sizeof(FMD_S_PAYDAY)
        u16(0, 8);                                 // cbmsg
        u16(2, Payday.Fmid);                       // fmid = 114
        i32(4, -500);                              // dMoney
        Assert.Equal(got, msg.Encode());           // construction is its own reference
    }

    // ── mission-level accrual (no network) ────────────────────────────────
    // A deterministic, TCP-free check of cadence + formula + single-fire, using
    // the same observation seam the host bridges into the wire broadcast.

    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static Mission NewStartedMission(IIgcSite site, int sides)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(sides).ToList();
        IReadOnlyList<SideSpec> specs = [];
        for (int i = 0; i < civs.Count; i++)
            specs = specs.Append(new SideSpec((short)i, $"Team {i + 1}", civs[i].CivilizationId)).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>Two sides, driven at exactly-representable <c>0.25s</c> steps
    /// (<c>Mission.TimeIncrement</c>; 0.25 is a power of two, so no float
    /// drift). The first Started tick (t=0.25) seeds <c>NextPaydayTime = 60.25</c>,
    /// so the first payday must fire on the first Update whose <c>now</c>
    /// reaches 60.25 — exactly one <c>PaydayEvent</c> per side — and never
    /// before 60s nor twice in an interval. The accrued <c>Side.Money</c>
    /// equals the reference formula computed independently from each civ's
    /// <c>IncomeMoney</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mission_accrues_once_per_boundary_at_the_flat_cadence()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording, sides: 2);
        const double step = 0.25;

        void Pump(int n)
        {
            for (int i = 0; i < n; i++) mission.Update(mission.LastUpdate + step);
        }

        // t = 0.25·k after k updates. Boundary crossing: t >= 60.25, i.e. k=241.
        Pump(239);                       // t = 59.75 → well before the boundary
        Assert.Equal(0, PaydayEvents(recording));

        Pump(1);                         // t = 60.0, still < 60.25
        Assert.Equal(0, PaydayEvents(recording));

        Pump(1);                         // t = 60.25 → fires exactly once per side
        Assert.Equal(2, PaydayEvents(recording));

        var side0 = mission.Sides[0];
        var side1 = mission.Sides[1];
        long expected0 = (long)(mission.IncomePerPayday * (1.0f + side0.Civilization.IncomeMoney));
        long expected1 = (long)(mission.IncomePerPayday * (1.0f + side1.Civilization.IncomeMoney));
        Assert.Equal(expected0, side0.Money);
        Assert.Equal(expected1, side1.Money);

        // Second boundary is t >= 120.25 → k=481. Pump to k=480 (t=120.0) and
        // confirm no second fire inside the interval, then k=481 for the second.
        Pump(239);                       // k=480, t=120.0
        Assert.Equal(2, PaydayEvents(recording));   // still exactly the first fire

        Pump(1);                         // k=481, t=120.25 → second fire, once per side
        Assert.Equal(4, PaydayEvents(recording));
        Assert.Equal(expected0 * 2, side0.Money);
        Assert.Equal(expected1 * 2, side1.Money);
    }

    private static int PaydayEvents(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("PaydayEvent("));

    // ── loopback wire (real TCP) ──────────────────────────────────────────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Payday Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 444u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 44000000000000000ul, DrmHash = "paydaytest",
    };

    /// <summary>Pump to a fixed simulation time. Receipt is synchronized
    /// separately, without consuming more of the payday clock.</summary>
    private static int PumpTo(Srv server, double targetSimNow, int max)
    {
        int n = 0;
        while (server.SimNow < targetSimNow && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Assert.True(server.SimNow >= targetSimNow, "simulation must reach the requested payday sample");
        return n;
    }

    private static void Seat(Srv server, params FltBot[] bots)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            server.TickSim();
            return bots.All(b => b.ShipAssignment is not null);
        }, TimeSpan.FromSeconds(30)), "all payday peers must finish seating before the clock starts");
    }

    private static readonly FieldInfo PeerShipsField = typeof(Srv)
        .GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void ReceiveThroughCurrentTick(Srv server, uint marker, params FltBot[] bots)
    {
        // Inject an ordered server marker AFTER the real payday publications.
        // No request/reply throttle or additional simulation ticks are involved.
        server.FlushBroadcastSnapshots();
        var peers = (IDictionary)PeerShipsField.GetValue(server)!;
        byte[] packet = FedPacket.Pack(new Ping { TimeClient = marker }.Encode());
        foreach (ConnectionId peer in peers.Keys) server.Host.Send(peer, packet);
        Assert.True(SpinWait.SpinUntil(() => bots.All(b => b.Pings.Any(p => p.TimeClient == marker)),
            TimeSpan.FromSeconds(30)), "all payday peers must decode the ordered sample marker");
    }

    /// <summary>Authoritative side for a bot: map its assigned ship id through
    /// the server's peer-ship table (read via InternalsVisibleTo), never guessing
    /// round-robin order.</summary>
    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    /// <summary>The reference per-player payout for a side with one player:
    /// <c>floor(600 · (1 + IncomeMoney))</c> computed independently in the test
    /// from the civ's decoded <c>IncomeMoney</c> (<c>fsmission.cpp:2536</c>,
    /// divided by playerCount == 1).</summary>
    private static long ExpectedDelta(Mission m, short sideId)
    {
        var side = m.Sides.Single(s => s.Id == sideId);
        return (long)(m.IncomePerPayday * (1.0f + side.Civilization.IncomeMoney));
    }

    /// <summary>⭐ Two bots on two sides. Across two payday boundaries the wire
    /// emits exactly one <c>FM_S_PAYDAY(114)</c> per side per boundary (none
    /// early, none doubled), and each bot decodes a <c>dMoney</c> equal to its
    /// own side's independently computed formula value — proving the cadence,
    /// the formula, and the per-side broadcast scoping over real loopback.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void WirePayday_fires_on_cadence_with_correct_per_side_value()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var botA = new FltBot();
        using var botB = new FltBot();

        botA.Connect(server.BoundPort, "pd_a");
        botA.SendLogonRequest(Request("pd_a"));
        botB.Connect(server.BoundPort, "pd_b");
        botB.SendLogonRequest(Request("pd_b"));

        // Wait until both are seated (each side now has exactly one player).
        Seat(server, botA, botB);
        botA.ResetPaydays(); botB.ResetPaydays();

        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started; // manual stage: test periodic paydays without the initial start grant
        server.TickSim();                    // seed the first deadline, after every peer is registered
        double origin = server.SimNow;
        short sideA = SideOf(server, botA);
        short sideB = SideOf(server, botB);
        Assert.NotEqual(sideA, sideB);               // the two bots are on different sides
        long expA = ExpectedDelta(mission, sideA);
        long expB = ExpectedDelta(mission, sideB);
        Assert.True(expA > 0 && expB > 0, "nonzero-income civs required for a meaningful delta");

        // Not early: 50s in, still zero paydays on both bots.
        PumpTo(server, origin + 50.0, 2000);
        ReceiveThroughCurrentTick(server, 0x51a, botA, botB);
        Assert.Empty(botA.Paydays);
        Assert.Empty(botB.Paydays);

        // First boundary: exactly one per bot, matching its side's formula.
        PumpTo(server, origin + 65.0, 800);
        ReceiveThroughCurrentTick(server, 0x51b, botA, botB);
        Assert.Single(botA.Paydays);
        Assert.Single(botB.Paydays);
        Assert.Equal(expA, botA.Paydays[0].DMoney);
        Assert.Equal(expB, botB.Paydays[0].DMoney);

        // Second boundary: exactly one more each (not doubled).
        PumpTo(server, origin + 125.0, 1500);
        ReceiveThroughCurrentTick(server, 0x51c, botA, botB);
        Assert.Equal(2, botA.Paydays.Count);
        Assert.Equal(2, botB.Paydays.Count);
        Assert.Equal(expA, botA.Paydays[1].DMoney);
        Assert.Equal(expB, botB.Paydays[1].DMoney);

        // Treasuries drained to < playerCount each payday (remainder law,
        // fsmission.cpp:2309) with one player per side ⇒ balance < 1.
        Assert.True(mission.Sides.Single(s => s.Id == sideA).Money < 1);
        Assert.True(mission.Sides.Single(s => s.Id == sideB).Money < 1);
    }

    /// <summary>A side with zero connected players accrues every payday but
    /// never pays out — no divide-by-zero, no crash (the reference
    /// <c>if (np != 0)</c> guard, fsmission.cpp:2298-2299). The bot on the
    /// populated side still receives its normal paydays.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ZeroPlayerSide_does_not_crash_and_accrues_without_paying()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var botA = new FltBot();

        botA.Connect(server.BoundPort, "pd_z");
        botA.SendLogonRequest(Request("pd_z"));
        Seat(server, botA);
        botA.ResetPaydays();

        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        server.TickSim();
        double origin = server.SimNow;
        short sideA = SideOf(server, botA);
        var loneSide = mission.Sides.Single(s => s.Id != sideA);   // the empty side

        // Pump past one full boundary. The server must run the whole crossing
        // without throwing (a divide-by-zero here would fail the test).
        PumpTo(server, origin + 65.0, 2000);
        ReceiveThroughCurrentTick(server, 0x51d, botA);
        Assert.Single(botA.Paydays);                              // its side pays normally

        // The empty side accrued (treasury grew) but was never paid out — it
        // keeps its money instead of crashing on / 0.
        Assert.True(loneSide.Money > 0, $"empty side should accrue; got {loneSide.Money}");
        // The populated side, by contrast, was drained (< playerCount == 1).
        Assert.True(mission.Sides.Single(s => s.Id == sideA).Money < 1);
    }
}
