using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using Th = global::Allegiance.Server.TestHarnessConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P473 — the wire-level test-harness economy grant. Native has no money/tech
/// cheat (fCanCheat only relaxes rank limits/win checks, FedSrv/fsship.h:337-339,
/// fsmission.cpp:1877,3810), so this is a PORT EXTENSION: explicit, off by
/// default, and prefix-scoped so it can NEVER touch a real player. Three LIVE
/// loopback proofs over a real in-process server + real TCP bots:
///   (A) grant ON: a "mcp-" bot is granted the configured wallet at seat and —
///       despite being docked at a fresh garrison where the tech gate would
///       refuse it — buys a tech-gated hull, paying the real price through the
///       real debit path; a NON-prefixed ("plain") bot is refused the SAME hull;
///   (B) grant OFF / absent: the "mcp-" bot is refused the same hull too (the
///       grant's only effect is the explicit config — byte-identical to P441).
/// Plus a seat-time proof that ONLY the prefixed connection receives the wallet
/// grant (humans untouched).
/// </summary>
[Collection("LiveNetwork")]
public sealed class TestHarnessEconomyGrantLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private const int GrantMoney = 1_000_000;

    /// <summary>A two-team config with the harness grant enabled (prefix "mcp-",
    /// money GrantMoney, ignoreTechGate on).</summary>
    private static Cfg GrantConfig() => BaseConfig(new Ls { TestHarness = new Th { Enabled = true, CharacterNamePrefix = "mcp-", Money = GrantMoney, IgnoreTechGate = true } });

    /// <summary>A two-team config with the harness grant OFF (absent section —
    /// the P441 default).</summary>
    private static Cfg NoGrantConfig() => BaseConfig(null);

    private static Cfg BaseConfig(Ls? liveSettings) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = liveSettings ?? new Ls(),
        Games =
        [
            new Gm { Name = "P473 Harness Grant", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 973u, DwCookie = 0x973u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9199000000000000000ul, DrmHash = "grant473",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Push every mission's next-payday time to the far future so the
    /// P197 payday credit can't perturb exact wallet assertions during the
    /// (short) test window (Mission.NextPaydayTime is internal → reflection).</summary>
    private static void StopPayday(Srv server)
    {
        var prop = typeof(Mission).GetProperty("NextPaydayTime", BindingFlags.Instance | BindingFlags.NonPublic)
                   ?? throw new InvalidOperationException("Mission.NextPaydayTime property not found");
        foreach (var m in server.Missions)
            prop.SetValue(m, double.MaxValue);
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static Ship LiveShip(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static int WalletOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money;

    /// <summary>The (station type, side) of the first friendly garrison — the
    /// docked tech-gate context (same helper as BuyLoadoutPricingLiveTests).</summary>
    private static (DataStationTypeIGC st, Side side) Garrison(Mission mission)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        if (!mission.Core.StationTypes.TryGetValue(garrison.StationTypeId, out var st))
            throw new InvalidOperationException("garrison station type unresolvable");
        var side = mission.Sides.First(s => s.Id == garrison.SideId);
        return (st, side);
    }

    private static bool StationSells(DataStationTypeIGC st, Side side, DataHullTypeIGC hull) =>
        TechTreeBitMask.RequiredTechsBuyable(hull.TtbmRequired, st.TtbmLocal, side.DevelopmentTechs);

    /// <summary>A tech-gated hull the docked garrison CANNOT sell but that is a
    /// real player-flyable hull (GroupId >= 0 — a drone-only hull would be
    /// refused even WITH the grant, by the non-tech drone gate) and costs real
    /// money (Price > 0, so the debit is observable).</summary>
    private static Func<Mission, DataHullTypeIGC> TechGatedPaidHull => m =>
    {
        var (st, side) = Garrison(m);
        return m.Core.HullTypes.Values
            .Where(h => h.GroupId >= 0 && h.Price > 0 && !StationSells(st, side, h))
            .OrderBy(h => h.HullId).First();
    };

    /// <summary>Boot both bots DOCKED inside the garrison's DockRadius on the
    /// friendly side (the tech gate runs on a docked buyer), with one bot named
    /// "mcp-buyer" (eligible for the grant) and one "plain-buyer" (not).
    /// Identifies each bot's ship id via ShipAssignment AFTER seating (bots seat
    /// in non-deterministic order).</summary>
    private sealed class Harness : IDisposable
    {
        public Srv Server = null!;
        public FltBot Mcp = null!;
        public FltBot Plain = null!;
        public short McpId;
        public short PlainId;
        public void Dispose() { Server.Dispose(); Mcp.Dispose(); Plain.Dispose(); }
    }

    private static Harness BootDocked(Cfg config)
    {
        var h = new Harness { Server = new Srv() };
        h.Server.Boot(config);
        foreach (var m in h.Server.Missions) m.Stage = MissionStage.Started;
        StopPayday(h.Server);
        // Spawn both bots on a SELLABLE (base-tech starter) hull so the tech-gated
        // hull purchase is a real hull CHANGE, not a no-op.
        var (gSt, gSide) = Garrison(h.Server.Missions[0]);
        short spawnHullId = h.Server.Missions[0].Core.HullTypes.Values
            .Where(hh => StationSells(gSt, gSide, hh)).OrderBy(hh => hh.Price).First().HullId;
        int n = 0;
        h.Server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            var ship = new Ship(id, core.HullTypes[spawnHullId],
                DockTestPlacement.Inside(mission, station, core.HullTypes[spawnHullId]) + new Vector3(5f * n++, 0f, 0f), Vector3.Zero, Orientation.Identity)
            { SideId = station.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        h.Mcp = new FltBot(); h.Plain = new FltBot();
        h.Mcp.Connect(h.Server.BoundPort, "mcp-buyer"); h.Mcp.SendLogonRequest(Request("mcp-buyer"));
        h.Plain.Connect(h.Server.BoundPort, "plain-buyer"); h.Plain.SendLogonRequest(Request("plain-buyer"));
        int seat = PumpUntil(h.Server, () => h.Mcp.ShipAssignment is not null && h.Plain.ShipAssignment is not null, 6000);
        if (seat >= 6000) throw new InvalidOperationException("mcp/plain did not seat");
        h.McpId = h.Mcp.ShipAssignment!.Ship.ShipId;
        h.PlainId = h.Plain.ShipAssignment!.Ship.ShipId;

        int dock = PumpUntil(h.Server,
            () => LiveShip(h.Server, h.McpId).StationId is not null && LiveShip(h.Server, h.PlainId).StationId is not null, 8000);
        if (dock >= 8000) throw new InvalidOperationException("mcp/plain never latched docked");
        return h;
    }

    /// <summary>⭐ Scenario A — with the grant ON: (1) ONLY the "mcp-" bot is
    /// granted the wallet at seat (plain stays at 0); (2) the docked mcp bot
    /// buys a tech-gated hull despite the garrison's tech gate, and its wallet
    /// is debited the hull's real price through the real debit path; (3) the
    /// NON-prefixed plain bot is refused the SAME hull (ship unchanged).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GrantOn_McpBuysTechGatedHull_PaidRealDebit_PlainRefused()
    {
        Require.Artwork();
        using var h = BootDocked(GrantConfig());
        short mcpId = h.McpId, plainId = h.PlainId;
        var mission = h.Server.Missions[0];

        // (1) The seat-time grant went ONLY to the prefixed connection.
        Assert.Equal(GrantMoney, WalletOf(h.Server, mcpId));      // mcp- got the grant
        Assert.Equal(0, WalletOf(h.Server, plainId));             // a human/plain name never does

        short beforePlainHull = LiveShip(h.Server, plainId).HullType.HullId;
        var techHull = TechGatedPaidHull(mission);
        Assert.True(techHull.Price > 0, "need a paid tech-gated hull");

        // (2) The mcp bot buys the tech-gated hull through the real debit path.
        h.Mcp.SendBuyLoadout(techHull.HullId, fLaunch: false);
        int applied = PumpUntil(h.Server, () => LiveShip(h.Server, mcpId).HullType.HullId == techHull.HullId, 6000);
        Assert.True(applied < 6000, $"mcp bot never bought tech-gated hull {techHull.HullId} (grant should bypass the tech gate)");
        Assert.Equal(techHull.HullId, LiveShip(h.Server, mcpId).HullType.HullId);
        // Debit = ShipValue(new) - ShipValue(old) = the tech hull's real price
        // (the sellable starter spawn hull is price 0 and carries no parts), so
        // the wallet fell by exactly techHull.Price through the real debit path.
        Assert.Equal(GrantMoney - techHull.Price, WalletOf(h.Server, mcpId));

        // (3) The NON-prefixed plain bot is refused the SAME hull (ship unchanged).
        h.Plain.SendBuyLoadout(techHull.HullId, fLaunch: false);
        for (int i = 0; i < 1500; i++) h.Server.TickSim();
        Assert.Equal(beforePlainHull, LiveShip(h.Server, plainId).HullType.HullId);   // refused -> unchanged
        Assert.Equal(0, WalletOf(h.Server, plainId));

        Console.WriteLine($"GRANT-ON-LIVE: mcp-{mcpId} bought tech-gated hull {techHull.HullId}({techHull.Price}) at fresh garrison -> wallet {GrantMoney}->{WalletOf(h.Server, mcpId)}; plain {plainId} refused same hull (unchanged {beforePlainHull})");
    }

    /// <summary>⭐ Scenario B — with the grant DISABLED (absent section, the P441
    /// default), the SAME "mcp-" bot is refused the tech-gated hull: the grant's
    /// only effect is the explicit config, and it is never implicit.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GrantOff_McpBotAlsoRefusedTechGatedHull()
    {
        Require.Artwork();
        using var h = BootDocked(NoGrantConfig());
        short mcpId = h.McpId;
        var mission = h.Server.Missions[0];
        var techHull = TechGatedPaidHull(mission);

        // No grant: even the prefixed bot starts at 0 and is refused.
        Assert.Equal(0, WalletOf(h.Server, mcpId));
        short beforeHull = LiveShip(h.Server, mcpId).HullType.HullId;

        h.Mcp.SendBuyLoadout(techHull.HullId, fLaunch: false);
        for (int i = 0; i < 1500; i++) h.Server.TickSim();

        Assert.Equal(beforeHull, LiveShip(h.Server, mcpId).HullType.HullId);   // refused
        Assert.Equal(0, WalletOf(h.Server, mcpId));                            // nothing moved
        Console.WriteLine($"GRANT-OFF-LIVE: mcp-{mcpId} tried tech-gated hull {techHull.HullId} with NO grant -> refused, hull {beforeHull} unchanged");
    }

    /// <summary>⭐ The <see cref="FltBot"/> (Allegiance.Bot) decodes the real
    /// <c>FM_S_BUY_LOADOUT_ACK(5)</c> — P473's fix so a Bot-based tool can see a
    /// refusal/partial grant instead of fire-and-forgetting. A real loopback
    /// Bot seats, buys a (sellable) hull hull-only, and asserts the decoded ack
    /// round-trips the granted hull and the P441 hull-only
    /// <c>FBoughtEverything=false</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bot_Decodes_BuyLoadoutAck()
    {
        Require.Artwork();
        using var srv = new Srv();
        srv.Boot(NoGrantConfig());
        foreach (var m in srv.Missions) m.Stage = MissionStage.Started;
        StopPayday(srv);

        using var bot = new FltBot();
        bot.Connect(srv.BoundPort, "mcp-decoder");
        bot.SendLogonRequest(Request("mcp-decoder"));
        int seat = PumpUntil(srv, () => bot.ShipAssignment is not null, 6000);
        Assert.True(seat < 6000, "decoder bot never seated");

        // Buy a hull-only loadout of the hull the bot already sits on (a fresh
        // garrison sellable starter) — an ack always follows.
        short shipId = bot.ShipAssignment!.Ship.ShipId;
        short hullId = LiveShip(srv, shipId).HullType.HullId;
        bot.SendBuyLoadout(hullId, fLaunch: false);

        int got = PumpUntil(srv, () => bot.BuyLoadoutAcks.Count > 0, 6000);
        Assert.True(got < 6000, "bot never received a BUY_LOADOUT_ACK");
        var ack = bot.BuyLoadoutAcks[^1];
        Assert.Equal(hullId, ack.HullId);              // the granted hull round-trips
        Assert.False(ack.FLaunch);                     // matched the request
        Assert.False(ack.FBoughtEverything);           // hull-only (zero parts) stays false (P441)
        Console.WriteLine($"BOT-ACK-LIVE: Allegiance.Bot decoded BUY_LOADOUT_ACK hull {ack.HullId} fbought={ack.FBoughtEverything} fLaunch={ack.FLaunch} ({ack.Parts.Count} parts)");
    }
}
