using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Srv = global::Allegiance.Server.Server;
using FltBot = global::Allegiance.Bot.Bot;

/// <summary>
/// P841 — certification tests for the "FedSrv mission rules (event-driven):
/// players &amp; sides" batch (SIM-0484, SIM-0490, SIM-0502, SIM-0521,
/// SIM-0761). Each test pins a native body branch the row certification found
/// divergent and fixed, or (where the row was already equal) the row's main
/// contract.
/// </summary>
public class P841MissionRulesCertifyTests
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
            new Gm { Name = "P841 Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "P841ABCDEFGHIJ", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 847u, DwCookie = 0x847u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8490000000000000000ul, DrmHash = "p841",
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

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    /// <summary>Place every spawned ship in mission.Sectors[0] on side 0, in
    /// the cheapest PAID hull (Price &gt; 0), so the multi-peer tests share one
    /// sector and one side regardless of the default round-robin, and the
    /// departing-ship value test has a measurable hull price.</summary>
    private static void InjectSameSectorSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var paid = core.HullTypes.Values.Where(h => h.GroupId >= 0 && h.Price > 0).OrderBy(h => h.HullId).ToList();
            var hull = paid.Count > 0 ? paid[0] : core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 780f, 880f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            seq++;
            return (ship, mission.Sectors[0]);
        };
    }

    // ── SIM-0484 — CFSMission::InitSide (fsmission.cpp:33-62) ─────────────

    /// <summary>P841 — every <see cref="Side"/> a fresh mission assembles with
    /// must carry native InitSide's seed values: auto-accept ON
    /// (<c>m_misdef.rgfAutoAccept[sideID] = true</c>, fsmission.cpp:58), ready/
    /// force-ready OFF (:56-57), active ON (:59), allies NA (:60), leader NA
    /// (:52), no players and zero treasury (:53,61). The auto-accept seed was
    /// previously <c>false</c>, which made a POSITIONREQ to a LED side pend for
    /// leader approval where native seats the requester immediately
    /// (RequestPosition's <c>rgfAutoAccept[sideID]</c> branch,
    /// fsmission.cpp:4759).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side_init_seeds_native_InitSide_defaults()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var m = server.TestMission ?? throw new InvalidOperationException("no mission");
        Assert.True(m.Sides.Count >= 2, "test config must produce at least two sides");
        foreach (var side in m.Sides)
        {
            Assert.True(side.AutoAccept, $"side {side.Id}: InitSide seeds rgfAutoAccept=true (fsmission.cpp:58)");
            Assert.False(side.Ready);       // fsmission.cpp:56
            Assert.False(side.ForceReady);  // fsmission.cpp:57
            Assert.True(side.Active);       // fsmission.cpp:59
            Assert.Null(side.AllyGroup);    // fsmission.cpp:60 (rgfAllies = NA)
            Assert.Null(side.LeaderShipId); // fsmission.cpp:52 (rgShipIDLeaders = NA)
            Assert.Equal(0L, side.Money);   // fsmission.cpp:61
        }
    }

    // ── SIM-0761 — GetExpMult (fsmission.cpp:3144-3158) ───────────────────

    /// <summary>P841 — the pure port of native's static
    /// <c>GetExpMult</c> must reproduce every branch: the
    /// <c>(nSides &lt; 2) || (sideExp &lt;= 0)</c> 1.0 guard, the
    /// <c>sideExp &lt;= avgExp</c> 1.0 guard, the <c>avgExp / sideExp</c>
    /// ratio, and the <c>minExpMult = 0.1</c> floor.</summary>
    [Fact]
    public void GetExpMult_matches_native_branches()
    {
        // nSides < 2 → 1.0 (fsmission.cpp:3148).
        Assert.Equal(1.0f, Allegiance.Server.PlayerScore.GetExpMult(100f, 50f, 1));
        // sideExp <= 0 → 1.0 (fsmission.cpp:3148).
        Assert.Equal(1.0f, Allegiance.Server.PlayerScore.GetExpMult(100f, 0f, 2));
        // sideExp <= avgExp = (total - side)/(nSides-1) → 1.0 (fsmission.cpp:3152).
        Assert.Equal(1.0f, Allegiance.Server.PlayerScore.GetExpMult(90f, 10f, 2));    // avg = 80 >= 10
        Assert.Equal(1.0f, Allegiance.Server.PlayerScore.GetExpMult(160f, 80f, 2));   // avg = 80, equal
        // sideExp above the other-side average → avgExp / sideExp (fsmission.cpp:3155).
        Assert.Equal(0.25f, Allegiance.Server.PlayerScore.GetExpMult(100f, 80f, 2));  // 20/80
        Assert.Equal(0.5f, Allegiance.Server.PlayerScore.GetExpMult(120f, 80f, 2));   // 40/80
        // Ratio floored at minExpMult = 0.1 (fsmission.cpp:3157).
        Assert.Equal(0.1f, Allegiance.Server.PlayerScore.GetExpMult(81f, 80f, 2));    // 1/80 = 0.0125
    }

    // ── SIM-0490 — RemovePlayerFromSide departure effects ─────────────────

    /// <summary>P841 — a departing flag carrier drops their flag exactly as
    /// native <c>RemovePlayerFromSide</c> does (fsmission.cpp:875-893): the
    /// carrier stops carrying, a <c>c_tcFlag</c> treasure spawns at the ship's
    /// position (inside the universe radius), the sector hears
    /// <c>TREASURE_SPAWN</c>, and the mission hears
    /// <c>GAIN_FLAG(NA, ship)</c>. Before the fix the flag simply vanished
    /// with the ship.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Departing_flag_carrier_drops_flag_treasure()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSameSectorSpawn(server);
        server.Boot(Config());
        using var observer = new FltBot();
        using var carrier = new FltBot();
        short obsId = Seat(server, observer, "P841OBS");
        short carId = Seat(server, carrier, "P841FLG");
        var sh = ShipOf(server, carId);
        Assert.True(sh.Flag is null, "precondition: the carrier holds no flag yet");
        sh.Flag = 1;   // carrying side 1's flag (native m_sidFlag)
        observer.ResetTreasureSpawnsReceived();
        observer.ResetGainFlags();

        carrier.Dispose();   // abrupt drop while carrying the flag
        int pumped = PumpUntil(server,
            () => observer.TreasureSpawns.Any(t => t.Data.TreasureCode == TreasureCode.Flag)
               && observer.GainFlags.Any(g => g.ShipidRecipient == carId && g.SideidFlag == GainFlag.NA),
            4000);
        Assert.True(pumped < 4000, "departing carrier's flag was neither dropped as treasure nor announced");
        Assert.Null(sh.Flag);   // carrier stopped carrying (fsmission.cpp:893 SetFlag(NA))
        var drop = observer.TreasureSpawns.Single(t => t.Data.TreasureCode == TreasureCode.Flag);
        Assert.Equal((short)1, drop.Data.TreasureId);
        Assert.Contains(observer.GainFlags, g => g.ShipidRecipient == carId && g.SideidFlag == GainFlag.NA);
    }

    /// <summary>P841 — when the game is running, a departing player's money
    /// plus their ship's full hardware value goes to the old side's treasury
    /// (native <c>RemovePlayerFromSide</c>, fsmission.cpp:1042-1063 +
    /// <c>GiveSideMoney</c>, fsmission.cpp:1777-1787), whose immediate
    /// <c>DoPayday</c> redistributes it to the remaining peers — observed here
    /// as the surviving side-mate's <c>PAYDAY(114)</c> carrying exactly the
    /// departed ship's value (wallet is 0: starting money is paid at a real
    /// countdown start, which this test bypasses). Pump stays under one payday
    /// cadence (60 s of sim time) so no natural payday can produce the
    /// message. Before the fix the departing peer's value simply vanished.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Departing_player_credits_hull_value_to_side_treasury()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSameSectorSpawn(server);
        server.Boot(Config());
        using var observer = new FltBot();
        using var leaver = new FltBot();
        Seat(server, observer, "P841OBS");
        short leaverId = Seat(server, leaver, "P841PAY");
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // the payday branch needs a running game
        var ship = ShipOf(server, leaverId);
        long expected = ship.HullType.Price
            + ship.Parts.Sum(p => p.Type.Type is DataPartTypeIGC.Pack ? 0 : p.Type.Price)
            + ship.CargoHold.Sum(p => p is null || p.Type.Type is DataPartTypeIGC.Pack ? 0 : p.Type.Price);
        Assert.True(expected > 0, "precondition: the departed ship must carry value");
        observer.ResetPaydays();

        leaver.Dispose();
        int pumped = PumpUntil(server,
            () => observer.Paydays.Any(p => p.DMoney == expected), 400);   // 400 ticks = 20 s sim < 60 s payday cadence
        Assert.True(pumped < 400,
            $"expected a PAYDAY(114) of exactly the departed ship's value {expected} to the remaining peer (fsmission.cpp:1042-1063 + DoPayday)");
    }

    // ── SIM-0521 — SaveAsOldPlayer (fsmission.cpp:2624-2680) ──────────────

    /// <summary>P841 — a player who departs a mission and rejoins it under the
    /// same name restores their cached score object (native
    /// <c>AddPlayerToMission</c>'s GetOldPlayerLink branch,
    /// fsmission.cpp:378-401; the cache is written by
    /// <c>SaveAsOldPlayer</c>, fsmission.cpp:2624-2680, and consumed once).
    /// Before the fix every rejoin started from a fresh score.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rejoining_player_restores_cached_score()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var first = new FltBot();
        short firstId = Seat(server, first, "P841OLD");
        var score = server.TestPlayerScore(firstId) ?? throw new InvalidOperationException("no score object for seated peer");
        score.Kills = 3;
        score.Deaths = 2;

        first.Dispose();   // departs → SaveAsOldPlayer caches the score object
        PumpUntil(server, () => server.TestPlayerScore(firstId) is null, 4000);

        using var second = new FltBot();
        short secondId = Seat(server, second, "P841OLD");   // same character name, new seat
        var rejoined = server.TestPlayerScore(secondId) ?? throw new InvalidOperationException("rejoined peer has no score object");
        Assert.Equal(3, rejoined.Kills);    // fsmission.cpp:387 SetKills
        Assert.Equal(2, rejoined.Deaths);   // fsmission.cpp:388 SetDeaths
    }

    // ── SIM-0502 — GetBase (fsmission.cpp:1433-1447) ──────────────────────

    /// <summary>P841 — spawn placement at a side's home anchors on native
    /// <c>GetBase(pside)</c>'s answer: the side's first restart-capable
    /// (<c>c_sabmRestart</c>) station, not merely the first side-owned
    /// station. A synthetic mission places a plain station in sector 1 and a
    /// restart-capable one in sector 2, both owned by side 0; the reposition
    /// must land the ship in sector 2.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Home_reposition_prefers_restart_capable_station()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc")));
        var restartType = core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRestart));
        var plainType = core.StationTypes.Values.First(t => !t.HasCapability(DataStationTypeIGC.SabmRestart));
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        Assert.True(civs.Count >= 2, "core must offer two civilizations");

        var clusters = new List<DataClusterIGC>
        {
            new(1u, [0f, 0f, 1f], 0u, 0f, 0f, 1, 0, 0, "PlainSector", "", "", 0, 0, 0, false, false),
            new(2u, [0f, 0f, 1f], 0u, 0f, 0f, 2, 0, 0, "RestartSector", "", "", 0, 0, 0, false, false),
        };
        var stations = new List<DataStationIGC>
        {
            // Mission order matters: the plain station comes first, so the old
            // first-owned-station pick would anchor sector 1 (the bug).
            new([0f, 0f, 0f], [0f, 1f, 0f], [0f, 0f, 1f], [1f, 0f, 0f, 0f], 1, 0, 1, plainType.StationTypeId, 0, 0, "PlainPost"),
            new([0f, 0f, 0f], [0f, 1f, 0f], [0f, 0f, 1f], [1f, 0f, 0f, 0f], 2, 0, 2, restartType.StationTypeId, 0, 0, "RestartBase"),
        };
        var map = MissionMap.FromLists(clusters, [], stations, []);
        var mission = Mission.Assemble(core, map,
            [new SideSpec(0, "Alpha", civs[0].CivilizationId), new SideSpec(1, "Bravo", civs[1].CivilizationId)],
            new NullIgcSite());

        using var server = new Srv();
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(1, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        var home = server.RepositionShipToSideHome(ship, mission, 0);
        Assert.Equal(2, home.Id);   // GetBase's restart-capable station's sector (fsmission.cpp:1440-1444)
        Assert.Equal((short)0, ship.SideId);
    }
}
