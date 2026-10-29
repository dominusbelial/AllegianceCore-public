using System.Reflection;
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
/// P847 — certification tests for the "FedSrv mission rules (event-driven):
/// site docking, ripcord, warp, loadout, treasure, payday, chat, building"
/// batch. Each test pins a native branch the row certification found divergent
/// and fixed (SIM-0512 <c>GiveSideMoney</c>'s immediate <c>DoPayday</c> callers,
/// the donation block's stage gate, the non-leader <c>pfsNewLeader == NULL</c>
/// donation clear, the case-insensitive old-player save, and the third
/// <c>CreateTreasure</c> overload's amount switch).
/// </summary>
public class P847MissionRulesCertifyTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple's no-station fly sector (the same measured id the P123/P847
    // treasure tests use) — nothing there can dock the pickup ship.
    private const short FlySector = 1052;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P847 Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, byte seed = 1) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, seed],
        CDKey = $"P847ABCDEFGHI{seed}", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = (uint)(847 + seed), DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8470000000000000000ul + seed, DrmHash = "p847",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    private static short Seat(Srv server, FltBot bot, string name, byte seed = 1)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name, seed));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static Side SideOf(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    /// <summary>A guaranteed-stationless sector of XXV_simple, so a placed ship
    /// can never dock (which would skip the treasure-pickup pass).</summary>
    private static Sector FlySectorOf(Mission mission) =>
        mission.Sectors.FirstOrDefault(s => s.Id == FlySector)
        ?? mission.Sectors.First(s => s.Stations.Count == 0);

    // ── SIM-0512 / SIM-0675 — GiveSideMoney's immediate DoPayday ───────────

    /// <summary>P847 — an NPC cash picker must run native
    /// <c>GiveSideMoney(ship-&gt;GetSide(), Money(amount))</c>
    /// (<c>FedSrv.CPP:13612-13615</c>), which is an accrual <b>plus an immediate
    /// <c>DoPayday(pside)</c></b> (<c>fsmission.cpp:1777-1787</c>) — not a bare
    /// treasury credit. Here an ownerless ship (a <c>FLAGSHIP</c>-class hull with
    /// <c>c_habmRescue</c>, pilot type <c>c_ptPlayer</c>, no <c>_peerShips</c>
    /// entry, so the handler's NPC branch) touches a 700-credit cash treasure while
    /// one player is seated on the same side: that player must receive
    /// <c>PAYDAY(114)</c> for exactly the amount on this tick. Red on unfixed code
    /// (the credits used to sit in <see cref="Side.Money"/> until the next natural
    /// payday, 60 s later: no PAYDAY inside the pump window).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Npc_cash_pickup_pays_the_side_out_immediately()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var observer = new FltBot();
        short obsId = Seat(server, observer, "P847NPS");
        var mission = server.Missions[0];
        short sideId = ShipOf(server, obsId).SideId!.Value;
        mission.Stage = MissionStage.Started;             // GiveSideMoney/DoPayday's operating stage

        var fly = FlySectorOf(mission);
        var pickupHull = mission.Core.HullTypes.Values
            .First(h => h.HasCapability(DataHullTypeIGC.HabmRescue));   // treasureIGC.h:86-87
        var npc = new Ship(9001, pickupHull, new Vector3(120f, 340f, 560f), Vector3.Zero, Orientation.Identity)
        {
            SideId = sideId,
            PilotType = Ship.PilotTypePlayer,             // pilot type gate passes; not a peer → NPC branch
        };
        mission.PlaceShip(npc, fly);

        const short Amount = 700;
        var data = new DataTreasureIGC(
            P0: [npc.Position.X, npc.Position.Y, npc.Position.Z],
            V0: [0f, 0f, 0f], Lifespan: 9999f, Time0: 0u,
            ObjectId: mission.NextObjectId(), TreasureId: Amount,
            ClusterId: fly.Id, Amount: Amount, TreasureCode: TreasureCode.Cash, CreateNow: true);
        fly.Add(new Treasure(data.ObjectId, fly, data, 0.0, mission));

        Assert.Equal(0L, SideOf(server, sideId).Money);    // fresh mission treasury
        observer.ResetPaydays();

        int pumped = PumpUntil(server, () => observer.Paydays.Any(p => p.DMoney == Amount), 200);   // 10 s sim < the 60 s payday cadence
        Assert.True(pumped < 200,
            $"an NPC cash pickup must run GiveSideMoney → DoPayday at once (fsmission.cpp:1777-1787); a PAYDAY(114) of {Amount} never reached the side's player");
        Assert.Equal(0L, SideOf(server, sideId).Money);    // delta·np deducted, remainder 0 (fsmission.cpp:2309)
        Console.WriteLine($"NPC-CASH-PAYDAY: NPC pickup of {Amount} paid the seated player immediately (PAYDAY(114) delta={Amount}, treasury drained)");
    }

    // ── SIM-0512 — the donation block's stage gate ──────────────────────────

    /// <summary>P847 — native keeps the whole auto-donate re-point block inside
    /// <c>if (STAGE_STARTED == stage || STAGE_STARTING == stage)</c>
    /// (<c>fsmission.cpp:1103-1115</c>). Before the start there is no income to
    /// redirect, so a lobby-time leader exit must leave every donor's
    /// relationship exactly as it was. Red on unfixed code (the port re-pointed
    /// the donor onto the newly promoted leader).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lobby_time_leader_exit_leaves_donations_untouched()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        short side0 = server.Missions[0].Sides[0].Id;
        short aId = Seat(server, a, "P847LB1", 1);
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        short bId = Seat(server, b, "P847LB2", 2);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));

        var leaderShip = ShipOf(server, aId);
        var donorLive = server.Ships.First(s => s.Ship.Id == bId);
        donorLive.Wallet.AutoDonateTarget = leaderShip;
        b.ResetAutoDonateUpdates();
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);   // the lobby

        a.Dispose();   // leader drops while the mission has not started
        int pumped = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == aId), 800);
        Assert.True(pumped < 800, "the departing peer was never retired");

        Assert.Same(leaderShip, donorLive.Wallet.AutoDonateTarget);      // fsmission.cpp:1103 — untouched
        Assert.DoesNotContain(b.AutoDonateUpdates, u => u.SidDonateBy == bId);
        Console.WriteLine("LOBBY-DONATE: a pre-start leader exit left the donor's auto-donate relationship untouched (native stage gate)");
    }

    /// <summary>P847 — the stage-gated other half: in a STARTED mission a leader
    /// exit promotes a successor and re-points every donor of the leaver onto
    /// that successor (<c>fsmission.cpp:1103-1115</c>), while the new leader's own
    /// donation to the leaver is cleared. Pins the contract the stage gate must
    /// not break.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Started_leader_exit_repoints_donors_to_the_promoted_leader()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        short side0 = server.Missions[0].Sides[0].Id;
        short aId = Seat(server, a, "P847ST1", 1);
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        short bId = Seat(server, b, "P847ST2", 2);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));
        short cId = Seat(server, c, "P847ST3", 3);
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));

        var leaderShip = ShipOf(server, aId);
        var bLive = server.Ships.First(s => s.Ship.Id == bId);
        var cLive = server.Ships.First(s => s.Ship.Id == cId);
        server.Missions[0].Stage = MissionStage.Started;
        bLive.Wallet.AutoDonateTarget = leaderShip;    // both donate to the leaver
        cLive.Wallet.AutoDonateTarget = leaderShip;
        Flush(server);

        a.Dispose();
        int pumped = PumpUntil(server, () => cLive.Wallet.AutoDonateTarget is not null
                                              && !ReferenceEquals(cLive.Wallet.AutoDonateTarget, leaderShip), 800);
        Assert.True(pumped < 800, "the donor was never re-pointed after the leader left");

        var successor = bLive.Ship;                    // deterministic first-other fallback (no donation winner)
        Assert.Equal(bId, SideOf(server, side0).LeaderShipId);
        Assert.Same(successor, cLive.Wallet.AutoDonateTarget);   // re-pointed to the new leader
        Assert.Null(bLive.Wallet.AutoDonateTarget);              // the new leader's own donation cleared
        Console.WriteLine("STARTED-DONATE: leader exit re-pointed the donor to the promoted leader and cleared the leader's own donation");
    }

    /// <summary>P847 — native's re-point target is <c>pfsNewLeader</c>, which is
    /// <b>NULL unless the leaver was the side's leader</b>
    /// (<c>fsmission.cpp:918-990</c> assigns it only inside the leader-succession
    /// block). A donor pointing at a departing <em>non-leader</em> therefore gets
    /// <c>SetAutoDonate(NULL, 0, false)</c> — stopped, not handed to the current
    /// leader (<c>fsmission.cpp:1106-1115</c>). Red on unfixed code (the port
    /// re-pointed the donor onto the sitting leader, redirecting real income).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Started_non_leader_exit_clears_donations_instead_of_repointing()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        short side0 = server.Missions[0].Sides[0].Id;
        short aId = Seat(server, a, "P847NL1", 1);
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A stays the leader
        short bId = Seat(server, b, "P847NL2", 2);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));
        short cId = Seat(server, c, "P847NL3", 3);
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));

        var nonLeaderShip = ShipOf(server, bId);
        var cLive = server.Ships.First(s => s.Ship.Id == cId);
        server.Missions[0].Stage = MissionStage.Started;
        cLive.Wallet.AutoDonateTarget = nonLeaderShip;   // C donates to member B, not the leader
        Flush(server);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);

        b.Dispose();   // a NON-leader leaves
        int pumped = PumpUntil(server, () => cLive.Wallet.AutoDonateTarget is null, 800);
        Assert.True(pumped < 800,
            "a donor of a departing non-leader must be cleared (pfsNewLeader == NULL, fsmission.cpp:1106-1115) — not re-pointed to the sitting leader");
        Assert.Null(cLive.Wallet.AutoDonateTarget);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);   // leadership untouched
        Console.WriteLine("NONLEADER-DONATE: donor cleared when the leaver was not the leader (native pfsNewLeader == NULL)");
    }

    // ── SIM-0521 family — case-insensitive old-player save ──────────────────

    /// <summary>P847 — native's <c>SaveAsOldPlayer</c> updates the entry
    /// <c>GetOldPlayerLink</c> finds by <c>_stricmp</c> name comparison
    /// (<c>fsmission.cpp:2624-2631</c>, <c>fsmission.h:390-398</c>), so a mission
    /// keeps exactly ONE saved score per player name however the name is cased.
    /// The cache key must therefore compare the name case-insensitively on
    /// INSERT too, so a second save under a different casing overwrites instead
    /// of duplicating (native's list only ever holds one <c>OldPlayerLink</c> per
    /// <c>_stricmp</c>-equal name, and a rejoin restores that one). Red on unfixed
    /// code: the default tuple comparer created a second entry.
    /// <para>Driven structurally (two saves for one name) because the logon gate
    /// refuses two simultaneously-seated peers whose names differ only in case
    /// (<see cref="P655DuplicateIdentityTests"/>), so the duplicate is only
    /// reachable through the cache itself.</para>
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Old_player_cache_keeps_one_entry_per_name_ignoring_case()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        var mission = server.Missions[0];
        var field = typeof(Srv).GetField("_oldPlayerScores", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Server._oldPlayerScores");
        var cache = field.GetValue(server)!;
        var map = (System.Collections.IDictionary)cache;

        var older = new global::Allegiance.Server.PlayerScore { Kills = 5 };
        var newer = new global::Allegiance.Server.PlayerScore { Kills = 9 };
        map[(mission, "P847Case")] = older;    // first departure
        map[(mission, "p847case")] = newer;    // second departure, other casing

        Assert.Equal(1, OldPlayerCacheCount(server));   // one entry per name (_stricmp)
        Console.WriteLine("OLDPLAYER-CASE: two differently-cased saves collapsed into one cache entry");
    }

    /// <summary>P847 — main-contract pin: a rejoin under a different casing
    /// consumes the saved score (native <c>GetOldPlayerLink</c>'s
    /// <c>_stricmp</c> lookup, <c>fsmission.cpp:378-401</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rejoining_player_under_a_different_case_restores_the_saved_score()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var first = new FltBot();
        short firstId = Seat(server, first, "P847OldCase", 1);
        var score = server.TestPlayerScore(firstId) ?? throw new InvalidOperationException("no score object");
        score.Kills = 7;
        score.Deaths = 3;
        first.Dispose();
        PumpUntil(server, () => server.TestPlayerScore(firstId) is null, 4000);

        using var second = new FltBot();
        short secondId = Seat(server, second, "P847OLDCASE", 2);
        var rejoined = server.TestPlayerScore(secondId) ?? throw new InvalidOperationException("rejoined peer has no score object");
        Assert.Equal(7, rejoined.Kills);
        Assert.Equal(3, rejoined.Deaths);
        Console.WriteLine("OLDPLAYER-REJOIN: a differently-cased rejoin restored the saved score (native _stricmp lookup)");
    }

    private static int OldPlayerCacheCount(Srv server)
    {
        var field = typeof(Srv).GetField("_oldPlayerScores", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Server._oldPlayerScores");
        var dict = field.GetValue(server)!;
        return (int)dict.GetType().GetProperty("Count")!.GetValue(dict)!;
    }

    // ── SIM-0512 — the side-leave payday pays out immediately ───────────────

    /// <summary>P847 — native's side-leave payday runs
    /// <c>GiveSideMoney(psideOld, payday)</c> (<c>fsmission.cpp:1145</c>), i.e. the
    /// treasury accrual AND an immediate <c>DoPayday</c> split to the players left
    /// on that side (<c>fsmission.cpp:1777-1787</c>). The team-lobby move (the
    /// squad-boot / side-quit path) credited the treasury only, so the leaver's
    /// money waited for the next natural payday. Red on unfixed code (no
    /// PAYDAY(114) inside the pump window).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side_leave_pays_the_leavers_money_out_immediately()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var stayer = new FltBot();
        using var leaver = new FltBot();
        short side0 = server.Missions[0].Sides[0].Id;
        short stayId = Seat(server, stayer, "P847SL1", 1);
        Assert.True(server.TryJoinSide(ShipOf(server, stayId), side0));
        short leaveId = Seat(server, leaver, "P847SL2", 2);
        Assert.True(server.TryJoinSide(ShipOf(server, leaveId), side0));
        server.Missions[0].Stage = MissionStage.Started;

        var leaveLive = server.Ships.First(s => s.Ship.Id == leaveId);
        const int Money = 400;
        leaveLive.Wallet.Money = Money;
        Assert.Equal(0L, SideOf(server, side0).Money);
        stayer.ResetPaydays();

        MoveToTeamLobby(server, leaveLive.Ship);
        int pumped = PumpUntil(server, () => stayer.Paydays.Any(p => p.DMoney == Money), 200);
        Assert.True(pumped < 200,
            $"the side-leave payday must run GiveSideMoney → DoPayday at once (fsmission.cpp:1145,1777-1787); a PAYDAY(114) of {Money} never reached the remaining player");
        Assert.Equal(0, leaveLive.Wallet.Money);           // pfsPlayer->SetMoney(0) (fsmission.cpp:1068)
        Assert.Equal(0L, SideOf(server, side0).Money);      // paid out, remainder 0
        Console.WriteLine($"SIDELEAVE-PAYDAY: leaver's {Money} paid the remaining player immediately (PAYDAY(114))");
    }

    private static void MoveToTeamLobby(Srv server, Ship ship)
    {
        var mi = typeof(Srv).GetMethod("MoveToTeamLobby", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Server.MoveToTeamLobby");
        mi.Invoke(server, [ship.Mission!, ship, QuitSideReason.SquadChange]);
    }

    // ── SIM-0781 — ErasePart / MatchPT against the desired loadout ──────────

    /// <summary>P847 — native's dock restock consumes a desired-loadout entry with
    /// <c>ErasePart</c>, whose match is <c>MatchPT</c>
    /// (<c>FedSrv.CPP:12617-12653</c>): an equipped part satisfies the desired
    /// entry when it IS that type or ANY part in its successor chain. So a ship
    /// already carrying the upgraded successor must not be handed a second base
    /// part. Red on unfixed code (the exact-PartId comparison treated the upgraded
    /// part as "missing", so a duplicate was mounted).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Dock_restock_does_not_re_buy_a_desired_type_already_carried_as_its_successor()
    {
        Require.Artwork();
        using var server = new Srv();
        // A hull with two weapon mounts, so the "re-buy the missing desired part"
        // leg really has a free slot to mount into (a single-hardpoint hull would
        // silently refuse and mask the branch).
        short sideId = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            sideId = mission.Sides[0].Id;
            var hull = core.HullTypes.Values
                .Where(h => h.Hardpoints.Length >= 2)
                .OrderBy(h => h.HullId).First();
            var fly = FlySectorOf(mission);
            var ship = new Ship(shipId, hull, new Vector3(320f, 640f, 960f), Vector3.Zero, Orientation.Identity)
            {
                SideId = mission.Sides[0].Id,
            };
            mission.PlaceShip(ship, fly);
            return (ship, fly);
        };
        server.Boot(Config());
        using var bot = new FltBot();
        short shipId = Seat(server, bot, "P847MPT");
        var live = server.Ships.First(s => s.Ship.Id == shipId);
        var ship = live.Ship;
        var core = ship.Mission!.Core;

        // A real successor pair of the same equipment kind, mountable at TWO mounts.
        var pairs = core.PartTypes.Values
            .Where(p => p.SuccessorPartId >= 0 && core.PartTypes.ContainsKey(p.SuccessorPartId))
            .Select(p => (Base: p, Upgraded: core.PartTypes[p.SuccessorPartId]))
            .Where(x => x.Base.Type.GetType() == x.Upgraded.Type.GetType()
                     && ship.HullType.CanMountPart(x.Upgraded, 0)
                     && ship.HullType.CanMountPart(x.Upgraded, 1))
            .ToList();
        Skip.If(pairs.Count == 0, "corpus has no mountable part successor pair with two free mounts");
        var (basePart, upgradedPart) = pairs[0];

        Assert.True(ship.MountPart(upgradedPart, 1, 0, byte.MaxValue) is not null, "precondition: mount the successor part");
        live.Wallet.DesiredLoadout = [basePart];    // the player's saved base-type wish
        live.Wallet.Money = 1_000_000;
        ship.StationId = 1;                          // RestockOnDock's dock gate

        var mi = typeof(Srv).GetMethod("RestockOnDock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Server.RestockOnDock");
        mi.Invoke(server, [live]);

        Assert.DoesNotContain(ship.Parts, p => p.Type.PartId == basePart.PartId);   // ErasePart consumed it
        Assert.Single(ship.Parts, p => p.Type.PartId == upgradedPart.PartId);       // no duplicate mount
        Console.WriteLine($"RESTOCK-MATCHPT: base {basePart.PartId} satisfied by its equipped successor {upgradedPart.PartId} — no duplicate mounted");
    }

    // ── SIM-0776 / SIM-0838 — CreateTreasure's amount switch ────────────────

    private static DataPartTypeIGC ChaffLauncherType(StaticCore core)
    {
        var chaff = core.ChaffTypes.Values.OrderBy(c => c.ExpendableTypeId).First();
        return new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "chaff", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 906, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ChaffLauncher(chaff.ExpendableTypeId));
    }

    private static DataPartTypeIGC WeaponType(StaticCore core) =>
        core.PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Weapon)
            .OrderBy(p => p.PartId)
            .First();

    /// <summary>P847 — the native third <c>CreateTreasure</c> overload switches on
    /// the dropped part's object type (<c>FedSrv.CPP:4380-4396</c>): only
    /// MAGAZINE / DISPENSER / PACK carry <c>GetAmount()</c>; everything else drops
    /// with <c>amount = 0</c> (<c>:4392</c>). A dropped weapon therefore produces a
    /// 0-amount part treasure, which the old port spliced to the part's own
    /// quantity. Red on unfixed code (amount 1 for a single weapon).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Dropped_weapon_treasure_carries_native_zero_amount()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();
        short shipId = Seat(server, bot, "P847DRW");
        var live = server.Ships.First(s => s.Ship.Id == shipId);
        var ship = live.Ship;
        server.Missions[0].Stage = MissionStage.Started;

        var weapon = WeaponType(ship.Mission!.Core);
        Assert.True(ship.MountPart(weapon, 1, 0, byte.MaxValue) is not null, "precondition: mount the weapon");
        ship.StationId = null;   // the fresh seat starts docked; DROP_PART needs a flying ship (FedSrv.CPP:6302)
        var sector = ship.Sector!;
        int before = sector.Models.OfType<Treasure>().Count();

        bot.SendDropPart(EquipmentType.Weapon, 0);
        int pumped = PumpUntil(server, () => sector.Models.OfType<Treasure>().Count() > before, 200);
        Assert.True(pumped < 200, "a dropped weapon must still leave a part treasure (FedSrv.CPP:6323)");
        var treasure = sector.Models.OfType<Treasure>().Last();
        Assert.Equal(weapon.PartId, (short)treasure.Data.TreasureId);
        Assert.Equal(0, treasure.Data.Amount);   // default: amount = 0 (FedSrv.CPP:4392)
        Console.WriteLine("DROPPART-WEAPON: dropped weapon treasure carries TreasureId=PartId and the native amount 0");
    }

    /// <summary>P847 — an EMPTY magazine/dispenser launcher (a real
    /// <c>ET_Dispenser</c> mine launcher here, fired down to zero rounds) makes
    /// the native third <c>CreateTreasure</c> overload return NULL
    /// (<c>FedSrv.CPP:4385-4387</c>), so no treasure spawns. Note native's
    /// <c>IlauncherIGC::IsLauncher</c> is <c>OT_magazine | OT_dispenser</c> ONLY
    /// (<c>Igc/igc.h:3993-3999</c>) — a chaff launcher (its own object type) falls
    /// to the switch's <c>default: amount = 0</c> and DOES drop. Red on unfixed
    /// code (a 0-amount launcher treasure was created).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Dropped_empty_launcher_creates_no_treasure()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();
        short shipId = Seat(server, bot, "P847DRL");
        var ship = server.Ships.First(s => s.Ship.Id == shipId).Ship;
        server.Missions[0].Stage = MissionStage.Started;

        var mineType = ship.Mission!.Core.MineTypes.Values.OrderBy(m => m.ExpendableTypeId).First();
        var launcher = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "mine", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 907, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MineLauncher(mineType.ExpendableTypeId));
        Assert.True(ship.MountPart(launcher, 1, 0, byte.MaxValue) is not null, "precondition: mount the mine launcher");
        ship.SetPartAmount(ship.Parts.First(p => p.MountId == 0), 0);   // fired down to zero rounds
        Assert.Equal(0, ship.Parts.First(p => p.MountId == 0).Amount);
        ship.StationId = null;   // the fresh seat starts docked; DROP_PART needs a flying ship (FedSrv.CPP:6302)
        var sector = ship.Sector!;
        int before = sector.Models.OfType<Treasure>().Count();

        bot.SendDropPart(EquipmentType.Dispenser, 0);
        PumpUntil(server, () => false, 60);

        Assert.Equal(before, sector.Models.OfType<Treasure>().Count());
        Console.WriteLine("DROPPART-LAUNCHER: dropping an empty launcher spawned no treasure (native NULL / DROP_PART gate)");
    }
}
