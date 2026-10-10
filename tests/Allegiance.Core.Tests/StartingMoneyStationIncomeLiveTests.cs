using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P443 — starting money + station income. Three proofs:
///   (1) a <b>real</b> start (ready-up countdown → Started) seeds each side with
///       16000 × (fStartingMoney + civ.BonusMoney) and pays it out <b>immediately</b>
///       to the seated players via the periodic payday seam (fsmission.cpp:2278),
///       then the <b>next</b> payday is 60s later — never immediately again;
///   (2) a side owning real PCore001 stations earns exactly
///       600 × (1 + IncomeMoney) + Σ station.Type.Income per periodic payday
///       (fsmission.cpp:2536 + 2538-2546);
///   (3) when a station changes owner (<see cref="Station.Capture"/>), its income
///       moves to the new owner on the next payday (ownership is read from the
///       live runtime stations).
/// The start money / immediate payout deliberately fires only through the real
/// Started-transition path (Server.CompleteStartCountdown), so the many tests
/// that flip Mission.Stage directly to Started are unaffected.
/// </summary>
[Collection("LiveNetwork")]
public class StartingMoneyStationIncomeLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Real PCore001 station types (verified against the core, not guessed):
    //   id 23 = SpecMine (Si), Income = 2000
    //   id 73 = [T]ransceiver, Income = 180
    // Garrisons (type 15) and every other standard type carry Income = 0.
    private const short SpecMineSiTypeId = 23;   // Income 2000
    private const short TransceiverTypeId = 73;  // Income 180

    /// <summary>Native <c>c_fStartingMoney = 16000</c> (missionigc.cpp:2752), the
    /// base each side's seed is computed from: <c>16000 × (fStartingMoney(=1.0) +
    /// civ.BonusMoney)</c>. Literal here (not <c>Mission.StartingMoneyPerSide</c>)
    /// so this test compiles against the pre-fix code too — the honest pre-fix
    /// evidence is a runtime failure, not a compile error.</summary>
    private const long NativeStartingMoney = 16000;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P443 Money Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 944u, DwCookie = 0x944u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9940000000000000000ul, DrmHash = "p443money",
    };

    private static int PumpTo(Srv server, double targetSimNow, int max)
    {
        int n = 0;
        while (server.SimNow < targetSimNow && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Thread.Sleep(60);
        return n;
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Thread.Sleep(60);
        return n;
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    private static int WalletOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Wallet.Money;
    }

    /// <summary>Place a real PCore001 station type, owned by <paramref name="sideId"/>,
    /// in a sector that has no other stations (avoids dock interference with the
    /// bots). Returns the runtime station so a test can <see cref="Station.Capture"/> it.</summary>
    private static Station PlaceType(Mission mission, short stationTypeId, short sideId, int seq)
    {
        var core = mission.Core;
        var type = core.StationTypes[stationTypeId];
        var sector = mission.Sectors.First(s => !s.StationRuntimes.Any());
        var data = new DataStationIGC(
            Position: [seq * 120f, 0f, -950f],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id, SideId: sideId, StationId: (short)(7000 + seq),
            StationTypeId: stationTypeId, BpHull: 1, BpShield: 1, Name: $"P443_{stationTypeId}_{seq}");
        var station = new Station(data, type);
        mission.PlaceStation(station, sector);
        return station;
    }

    // ── (1) real-start starting money + immediate payday + 60s-next ────────

    /// <summary>⭐ A real start. Two bots seat during the pre-game hold, both
    /// sides ready up, the countdown runs to Started. Right at Started each side
    /// is seeded with 16000 × (1 + civ.BonusMoney) and the money is paid straight
    /// out: each bot receives exactly one FM_S_PAYDAY(114) whose dMoney equals its
    /// side's starting seed, and its server wallet equals that seed. The next
    /// payday arrives 60s later (the periodic one), not immediately.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_start_seeds_starting_money_pays_immediately_then_next_at_60s()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];

        using var botA = new FltBot();
        using var botB = new FltBot();
        botA.Connect(server.BoundPort, "p443_a");
        botA.SendLogonRequest(Request("p443_a"));
        botB.Connect(server.BoundPort, "p443_b");
        botB.SendLogonRequest(Request("p443_b"));

        int seat = PumpUntil(server,
            () => botA.ShipAssignment is not null && botB.ShipAssignment is not null, 4000);
        Assert.True(botA.ShipAssignment is not null && botB.ShipAssignment is not null,
            $"both bots must seat during the pre-game hold; ticks={seat}");
        Assert.Equal(MissionStage.NotStarted, mission.Stage);   // still pre-game

        short sideA = SideOf(server, botA);
        short sideB = SideOf(server, botB);
        Assert.NotEqual(sideA, sideB);

        // Both sides ready → the real auto-start countdown begins; pump past it to Started.
        botA.SendPlayerReady(true, botA.ShipAssignment!.Ship.ShipId);
        botB.SendPlayerReady(true, botB.ShipAssignment!.Ship.ShipId);
        int started = PumpUntil(server, () => mission.Stage == MissionStage.Started, 4000);
        Assert.True(started < 4000, $"mission must reach Started via the countdown; stage={mission.Stage}");
        double startedAt = server.SimNow;

        var sa = mission.Sides.Single(s => s.Id == sideA);
        var sb = mission.Sides.Single(s => s.Id == sideB);
        long expA = (long)(NativeStartingMoney * (1.0f + sa.Civilization.BonusMoney));
        long expB = (long)(NativeStartingMoney * (1.0f + sb.Civilization.BonusMoney));
        Assert.True(expA > 0 && expB > 0, "nonzero starting seeds required");

        // Immediate payday: exactly one PAYDAY each, value = the side's starting seed.
        int settled = PumpUntil(server, () => botA.Paydays.Count >= 1 && botB.Paydays.Count >= 1, 2000);
        Assert.True(botA.Paydays.Count >= 1 && botB.Paydays.Count >= 1,
            $"immediate payday must reach both bots; ticks={settled}");
        Assert.Equal(expA, botA.Paydays[0].DMoney);
        Assert.Equal(expB, botB.Paydays[0].DMoney);

        // Server-side player wallet credited with the full seed (1 player per side).
        Assert.Equal((int)expA, WalletOf(server, botA));
        Assert.Equal((int)expB, WalletOf(server, botB));

        // Not immediately again: 10s later, still exactly the one immediate payday each.
        botA.ResetPaydays(); botB.ResetPaydays();
        PumpTo(server, startedAt + 10, 1200);
        Assert.Empty(botA.Paydays);
        Assert.Empty(botB.Paydays);

        // The next payday is the periodic one ~60s after the real start.
        PumpTo(server, startedAt + 62, 2000);
        Assert.Single(botA.Paydays);
        Assert.Single(botB.Paydays);
        long periodicA = (long)(Mission.IncomePerPayday * (1.0f + sa.Civilization.IncomeMoney));
        long periodicB = (long)(Mission.IncomePerPayday * (1.0f + sb.Civilization.IncomeMoney));
        Assert.Equal(periodicA, botA.Paydays[0].DMoney);
        Assert.Equal(periodicB, botB.Paydays[0].DMoney);

        Console.WriteLine($"P443-LIVE-START: sides {sideA}({sa.Civilization.Name}, start {expA})/{sideB}({sb.Civilization.Name}, start {expB}) -> " +
                          $"immediate PAYDAY at Started, wallet={WalletOf(server, botA)}/{WalletOf(server, botB)}, next periodic at +60s = {periodicA}/{periodicB}");
    }

    // ── (2) station income in the periodic payday ──────────────────────────

    /// <summary>⭐ A side owning real PCore001 stations earns exactly
    /// 600 × (1 + IncomeMoney) + Σ Type.Income in its periodic payday — i.e. more
    /// than the pre-fix base-only amount. Driven with a direct Stage=Started flip
    /// (no start money), so the periodic payday's per-player share is purely
    /// income. Place SpecMine(Si) ×2 (2000 each) + Transceiver ×1 (180) for the
    /// bot's side.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owned_stations_add_their_income_to_the_periodic_payday()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // no start money path here

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p443_si");
        bot.SendLogonRequest(Request("p443_si"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(bot.ShipAssignment is not null, "bot must seat");
        short mySide = SideOf(server, bot);

        // Place three real income-bearing stations for the bot's side, well before the payday.
        PlaceType(mission, SpecMineSiTypeId, mySide, 1);
        PlaceType(mission, SpecMineSiTypeId, mySide, 2);
        PlaceType(mission, TransceiverTypeId, mySide, 3);

        var side = mission.Sides.Single(s => s.Id == mySide);
        long placedIncome = 2000 + 2000 + 180;   // real SpecMine(Si)×2 + Transceiver×1
        long baseIncome = (long)(Mission.IncomePerPayday * (1.0f + side.Civilization.IncomeMoney));
        long expected = baseIncome + placedIncome;

        bot.ResetPaydays();
        PumpTo(server, 61.0, 2000);              // first periodic payday
        var pd = Assert.Single(bot.Paydays);
        Assert.Equal(expected, pd.DMoney);                                   // base + station income
        Assert.Equal(placedIncome, expected - baseIncome);                   // the station term is real
        Assert.True(expected > baseIncome, "station income must make payday exceed the pre-fix base-only value");

        Console.WriteLine($"P443-LIVE-STATION-INCOME: side {mySide} owns SpecMine(Si)×2+Transceiver " +
                          $"(Σ {placedIncome}) -> periodic PAYDAY dMoney={pd.DMoney} = base {baseIncome} + station {placedIncome}");
    }

    // ── (3) capture re-sides a station's income ────────────────────────────

    /// <summary>⭐ Ownership read from the live runtime stations: a SpecMine(Si)
    /// (income 2000) owned by side A contributes to A's payday; after
    /// <see cref="Station.Capture"/> it contributes to side B's payday instead,
    /// and no longer to A's — the income moves with the owner on the next payday.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Captured_station_moves_its_income_to_the_new_owner_on_next_payday()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];
        short sideA = mission.Sides[0].Id;
        short sideB = mission.Sides[1].Id;

        var station = PlaceType(mission, SpecMineSiTypeId, sideA, 1);   // owned by A
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var botA = new FltBot();
        using var botB = new FltBot();
        botA.Connect(server.BoundPort, "p443_ca"); botA.SendLogonRequest(Request("p443_ca"));
        botB.Connect(server.BoundPort, "p443_cb"); botB.SendLogonRequest(Request("p443_cb"));
        int seat = PumpUntil(server, () => botA.ShipAssignment is not null && botB.ShipAssignment is not null, 4000);
        Assert.True(botA.ShipAssignment is not null && botB.ShipAssignment is not null, "both bots must seat");

        FltBot onA = SideOf(server, botA) == sideA ? botA : botB;
        FltBot onB = onA == botA ? botB : botA;
        var sideA0 = mission.Sides.Single(s => s.Id == sideA);
        var sideB0 = mission.Sides.Single(s => s.Id == sideB);
        long baseA = (long)(Mission.IncomePerPayday * (1.0f + sideA0.Civilization.IncomeMoney));
        long baseB = (long)(Mission.IncomePerPayday * (1.0f + sideB0.Civilization.IncomeMoney));

        onA.ResetPaydays(); onB.ResetPaydays();
        PumpTo(server, 61.0, 2000);              // first payday: mine still A's
        Assert.Equal(baseA + 2000, onA.Paydays.Single().DMoney);   // A earns the mine's income
        Assert.Equal(baseB, onB.Paydays.Single().DMoney);          // B does not

        // Capture the station to side B.
        station.Capture(sideB);
        Assert.Equal(sideB, station.SideId);     // ownership really moved

        PumpTo(server, 121.0, 2000);             // second payday: mine now B's
        Assert.Equal(baseA, onA.Paydays[1].DMoney);                // A lost the mine's income
        Assert.Equal(baseB + 2000, onB.Paydays[1].DMoney);         // B gained it

        Console.WriteLine($"P443-LIVE-CAPTURE-INCOME: SpecMine(2000) {sideA}->{sideB}: A payday {baseA+2000}->{baseA}, B payday {baseB}->{baseB+2000}");
    }
}
