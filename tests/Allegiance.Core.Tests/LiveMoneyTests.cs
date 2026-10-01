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
/// P118 — the two economy-sync messages <c>MONEY_CHANGE</c>(60) and
/// <c>SET_MONEY</c>(115). Two live loopback proofs over a real in-process
/// server + real TCP bots:
///   (1) joining a mission sends the joining player <c>SET_MONEY</c> carrying
///       that player's OWN per-player wallet (P197 PlayerWallet; P442 fixed the
///       join-time SET_MONEY which previously sent the pooled side treasury —
///       native fsmission.cpp:4270-4299 sends the player's money, and this port
///       has had real per-player wallets since P197);
///   (2) a real refund at a wired site (board-surrendered hardware value,
///       Server.cs board-refund, FedSrv.CPP:2435-2441) broadcasts
///       <c>MONEY_CHANGE</c> with the correct dMoney/sidTo/sidFrom=<c>NA</c>
///       to every peer on the acting ship's side — and NOT to a peer on the
///       other side.
/// The codecs round-trip byte-exact in Wire.MoneyChangeSetMoneyWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveMoneyTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Concrete hulls from PCore001.igc (measured in P108, not guessed):
    //   792 = turret-capable carrier, price 4000 (board target)
    //   504 = 500-credit normal hull (the boarder surrenders exactly 500)
    private const short CarrierHullId = 792;
    private const short BoarderHullId = 504;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Money Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 881u, DwCookie = 0x881u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8788000000000000000ul, DrmHash = "moneytest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    // ── SET_MONEY: join-time per-player wallet sync ─────────────────────

    /// <summary>⭐ Scenario 1 — joining a mission sends exactly one
    /// <c>SET_MONEY(115)</c> to the joining player carrying that player's OWN
    /// per-player wallet (P197 PlayerWallet; 0 here because the harness bypasses
    /// the real start, so no starting money/payday has run) and the player's own
    /// ship id — provably NOT the side's pooled treasury, which is pre-seeded to
    /// a large, distinct sentinel (P442: the pre-P442 join-time SET_MONEY sent
    /// the side treasury, a stale pooled-design mapping with no per-player
    /// wallets that P197 had already made false).</summary>
    [Fact]
    public void Join_set_money_carries_the_joiners_wallet_not_the_side_treasury()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        // Pre-seed each side's pooled treasury to a distinct nonzero sentinel so
        // a "join SET_MONEY == side treasury" bug is unambiguously caught.
        var mission = server.Missions[0];
        short side0 = mission.Sides[0].Id, side1 = mission.Sides[1].Id;
        mission.Sides.Single(s => s.Id == side0).Money = 656565;
        mission.Sides.Single(s => s.Id == side1).Money = 848484;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "joiner");
        bot.SendLogonRequest(Request("joiner"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "joiner did not seat");

        short mySide = SideOf(server, bot);
        long treasury = mission.Sides.Single(s => s.Id == mySide).Money;
        Assert.True(treasury is > 0 and < int.MaxValue, $"seeded money {treasury} out of int32 range");

        int seen = PumpUntil(server, () => bot.SetMoneys.Count >= 1, 2000);
        Assert.True(seen < 2000, "SET_MONEY(115) never reached the joining bot");

        var sm = Assert.Single(bot.SetMoneys);
        // The joiner's OWN wallet (fresh PlayerWallet = 0 at this bypassed start),
        // NOT the side's pooled treasury sentinel.
        int joinerWallet = server.Ships.Single(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Wallet.Money;
        Assert.Equal(joinerWallet, sm.Money);
        Assert.NotEqual((int)treasury, sm.Money);                 // not the side treasury
        Assert.Equal(bot.ShipAssignment!.Ship.ShipId, sm.ShipId);  // the joining player's own ship

        Console.WriteLine($"SETMONEY-LIVE: joiner on side {mySide} (wallet {joinerWallet}, treasury {treasury}) -> SET_MONEY(115) money={sm.Money} shipID={sm.ShipId}");
    }

    // ── MONEY_CHANGE: side-wide refund broadcast ─────────────────────────

    private sealed class ThreeSpawn
    {
        public Ship Carrier = null!, Boarder = null!, Outsider = null!;
    }

    /// <summary>A three-ship spawn injection: the carrier (hull 792) and the
    /// boarder (hull 504) both on the first friendly station's side, the
    /// outsider on the OTHER side (placed in a no-friendly-station sector so
    /// it just flies there). The boarder's bare hull 504 surrenders exactly
    /// 500 credits on boarding.</summary>
    private static ThreeSpawn InjectSpawn(Srv server)
    {
        var sp = new ThreeSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            short friendly = station.SideId;
            var otherSide = mission.Sides.First(s => s.Id != friendly);

            Ship ship;
            if (n == 0)
            {
                ship = new Ship(id, core.HullTypes[CarrierHullId], StationPos(station) + new Vector3(25f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = friendly };
                sp.Carrier = ship;
            }
            else if (n == 1)
            {
                ship = new Ship(id, core.HullTypes[BoarderHullId], StationPos(station) + new Vector3(45f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = friendly };
                sp.Boarder = ship;
            }
            else
            {
                var lonerSector = mission.Sectors.First(s => !s.Stations.Any(st => st.SideId >= 0));
                ship = new Ship(id, core.HullTypes[BoarderHullId], new Vector3(0f, 830f, 980f), Vector3.Zero, Orientation.Identity) { SideId = otherSide.Id };
                mission.PlaceShip(ship, lonerSector);
                sp.Outsider = ship;
                return (ship, lonerSector);
            }
            n++;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        return sp;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>⭐ Scenario 2 — a real refund at the wired board site broadcasts
    /// <c>MONEY_CHANGE(60)</c> with dMoney = the surrendered 500, sidTo = the
    /// boarded ship, sidFrom = <c>NA</c> to BOTH same-side peers, and to NO
    /// peer on the other side.</summary>
    [Fact]
    public void Board_refund_broadcasts_money_change_sidewide_not_cross_side()
    {
        using var server = new Srv();
        var sp = InjectSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        a.Connect(server.BoundPort, "AA"); a.SendLogonRequest(Request("AA"));
        b.Connect(server.BoundPort, "BB"); b.SendLogonRequest(Request("BB"));
        c.Connect(server.BoundPort, "CC"); c.SendLogonRequest(Request("CC"));
        int seat = PumpUntil(server,
            () => a.ShipAssignment is not null && b.ShipAssignment is not null && c.ShipAssignment is not null, 6000);
        Assert.True(seat < 6000, "all three bots must seat");

        // Map each bot to its spawned ship by assigned id (robust to order):
        // whichever bot's ShipAssignment matches the carrier/boarder/outsider
        // spawn id owns that role.
        FltBot carrierBot = null!, boarderBot = null!, outsiderBot = null!;
        foreach (var bot in new[] { a, b, c })
        {
            short id = bot.ShipAssignment!.Ship.ShipId;
            if (id == sp.Carrier.Id) carrierBot = bot;
            else if (id == sp.Boarder.Id) boarderBot = bot;
            else if (id == sp.Outsider.Id) outsiderBot = bot;
        }
        Assert.NotNull(carrierBot);
        Assert.NotNull(boarderBot);
        Assert.NotNull(outsiderBot);
        short carrierId = sp.Carrier.Id, boarderId = sp.Boarder.Id, outsiderId = sp.Outsider.Id;

        // Sanity: carrier + boarder on one side, outsider on the OTHER.
        Assert.Equal(sp.Carrier.SideId, sp.Boarder.SideId);
        Assert.NotEqual(sp.Boarder.SideId, sp.Outsider.SideId);

        carrierBot.ResetMoneyChanges(); boarderBot.ResetMoneyChanges(); outsiderBot.ResetMoneyChanges();

        // P472 — the board handler REFUSES unless BOTH ships are docked
        // (Server.Docking.cs:90-95, FedSrv.CPP:2342-2345). The carrier and
        // boarder are spawned inside the station's dock radius and latch
        // StationId on an early Started tick, but the pre-P472 test sent Board
        // immediately after seating and intermittently RACED the dock — the
        // board was refused, no refund fired, and the MONEY_CHANGE wait below
        // timed out under load. Wait for the dock precondition first (the same
        // step the P108/win-condition boarding recipes take).
        int docked = PumpUntil(server,
            () => sp.Carrier.StationId is not null && sp.Boarder.StationId is not null, 6000);
        Assert.True(docked < 6000, "carrier and boarder did not dock before boarding");

        // The boarder boards the carrier (as an observer — no equipped turret,
        // but boarding still succeeds and the refund fires, FedSrv.CPP:2426-2443).
        boarderBot.SendBoard(carrierId);
        // P472 — condition-wait with a generous ceiling: the MONEY_CHANGE reaches
        // the two same-side peers asynchronously over their own socket receive
        // threads, which under full-suite CPU contention can lag the server-side
        // broadcast by well over a second. The cap is only a ceiling (the pump
        // returns the instant both observables land), so raising it absorbs that
        // load lag without slowing the happy path.
        int seen = PumpUntil(server,
            () => boarderBot.MoneyChanges.Count >= 1 && carrierBot.MoneyChanges.Count >= 1, 9000);
        Assert.True(seen < 9000, "MONEY_CHANGE(60) did not reach both same-side peers");

        // Both same-side peers saw the SAME frame: refund of hull 504 = 500,
        // sidTo = the boarded ship, sidFrom = NA (server-initiated pooled credit).
        var bmc = Assert.Single(boarderBot.MoneyChanges);
        var cmc = Assert.Single(carrierBot.MoneyChanges);
        Assert.Equal(500, bmc.DMoney);
        Assert.Equal(boarderId, bmc.SidTo);
        Assert.Equal((short)-1, bmc.SidFrom);                 // NA
        Assert.Equal(bmc.DMoney, cmc.DMoney);
        Assert.Equal(bmc.SidTo, cmc.SidTo);
        Assert.Equal(bmc.SidFrom, cmc.SidFrom);

        // The refund actually landed in the side treasury (independent check).
        var side = sp.Boarder.Mission!.Sides.First(s => s.Id == sp.Boarder.SideId!.Value);
        Assert.Equal(500L, side.Money);

        // Negative: the other side's peer saw NO MONEY_CHANGE.
        int neg = PumpUntil(server, () => outsiderBot.MoneyChanges.Count >= 1, 1500);
        Assert.True(neg == 1500, "other-side peer should NOT receive MONEY_CHANGE");
        Assert.Empty(outsiderBot.MoneyChanges);

        Console.WriteLine($"MONEYCHANGE-LIVE: ship {boarderId} boarded {carrierId} -> MONEY_CHANGE(60) dMoney=500 sidTo={boarderId} sidFrom=NA to 2 same-side peers; other-side peer {outsiderId} saw none");
    }
}
