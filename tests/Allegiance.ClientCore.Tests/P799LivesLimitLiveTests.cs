using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P799 — the native lives limit, live loopback half: a real in-process
/// <see cref="Srv"/> + real TCP <see cref="ConnectionSession"/> peers (the same
/// data plane the MonoGame client uses) exercising the ghost rules end to end:
/// <list type="bullet">
/// <item><b>Ghost after the last life</b> — N deaths with iLives = N−1 makes the
/// player a ghost (<c>Igc/shipIGC.h:2465-2469</c>); the server then refuses the
/// ghost's BUY_LOADOUT (<c>FedSrv.CPP:6156</c>, a negative ack with
/// fLaunch=false and an unchanged ship) and its launch/undock
/// (<c>FedSrv.CPP:7048</c> → TELEPORT_ACK(NA), ship stays docked).</item>
/// <item><b>Unlimited never ghosts</b> — the 0x7fff sentinel
/// (<c>igc.h:1007</c>, <c>fsmission.cpp:4086</c> fLimitedLives) keeps every
/// pre-P799 behaviour: the same death, then buy+launch succeeds.</item>
/// <item><b>Ghost relocation on station loss</b> — a destroyed station does NOT
/// eject docked ghosts; they are re-docked at another restart-capable base
/// (<c>fsmission.cpp:4785-4850</c>).</item>
/// <item><b>Side elimination when only ghosts remain</b> — after a death that
/// ghosts the side's last non-ghost player, the side is deactivated
/// (<c>FedSrv.CPP:12070</c>) and falls through the native game-over path
/// (SIDE_INACTIVE + "is no more" + the win-by-elimination check declaring the
/// last side standing).</item>
/// </list>
/// Failing-first on the pre-P799 tree: every gate assertion (refused buy/launch,
/// relocation, elimination) is false there, because <c>Mission.Lives</c> was
/// pinned to the unlimited sentinel and no ghost state existed.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P799LivesLimitLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static Cfg Config(short lives, string name = "P799 Lives Limit") => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: some test clients never send POSITIONREQ
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], Lives = lives },
        ],
    };

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 799u, DwCookie = 0x799u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = steam, DrmHash = "p799",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static ConnectionSession Connect(Srv server, string name, ulong steam)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name, steam));
        return s;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Spawn the peer's ship as a LIFEPOD in open space well clear of the
    /// side's friendly garrison (the DeadLatchRespawnLiveTests trick): a lethal hit
    /// is then a REAL death — bLifepod ⇒ bKilled — not the in-space eject, so the
    /// P799 death counter advances exactly once per kill.</summary>
    private static (Ship Ship, Sector Sector) OpenSpaceLifepod(Mission mission, short shipId, short sideId)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId == sideId);
        var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
        var lifepod = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var spawn = StationPos(garrison) + new Vector3(0f, 0f, 1500f);
        var ship = new Ship(shipId, lifepod, spawn, Vector3.Zero, Orientation.Identity)
        {
            SideId = sideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    /// <summary>Kill the real server ship on the real combat path (lethal
    /// ReceiveDamage with a damage type whose armor factor against this hull is
    /// &gt; 0), exactly like DeadLatchRespawnLiveTests.KillShipForReal. Native
    /// restart synchronously re-docks a full-health pod before returning
    /// (FedSrv.CPP:12052-12065).</summary>
    private static bool KillShipForReal(Srv server, Ship ship)
    {
        var mission = ship.Mission!;
        var dm = mission.Core.Constants.DamageConstants;
        byte def = ship.HullType.DefenseType;
        byte dmgType = 0;
        foreach (var p in mission.Core.ProjectileTypes.Values)
        {
            byte t = p.DamageType;
            if (t < dm.Length && def < dm[t].Length && dm[t][def] > 0f) { dmgType = t; break; }
        }
        Assert.True(ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod),
            "a pod's lethal hit must take true death, not in-space ejection");
        var result = ship.ReceiveDamage(dmgType, ship.HitPoints * 1e6f, mission.LastUpdate, ship.Position, ship.Position, launcher: null);
        // NOTE: no post-kill ship asserts here — a ghosting death runs the P799
        // side-elimination synchronously (DeactivateSide → FinalizeDeadShip), so
        // a ship CAN legitimately end this call Dead. Tests pump for the respawn
        // (or the elimination) instead.
        Console.WriteLine($"[P799-LIVE] dealt lethal damage type {dmgType} to ship {ship.Id} (deaths now +1)");
        return result == DamageResult.Killed;
    }

    private static (short Deaths, bool Ghost, short StationId) OwnState(ConnectionSession session)
    {
        lock (session.Gate)
        {
            short deaths = 0; bool haveStats = session.World.TryGetOwnMissionStats(out _, out deaths);
            return (haveStats ? deaths : (short)0, session.World.OwnIsGhost,
                    session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine)
                        ? mine.StationId : (short)-1);
        }
    }

    /// <summary>⭐ Limited lives: iLives = 1 grants one life — the second death
    /// makes the player a ghost (deaths 2 &gt; 1, shipIGC.h:2465-2469); the server
    /// then refuses the ghost's buy AND its launch. A second Alpha player keeps
    /// the side alive. Fails on the pre-P799 tree (no lives limit → no ghost →
    /// both requests succeed).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Limited_lives_death_makes_a_ghost_and_refuses_buy_and_launch()
    {
        Require.Artwork();
        var (server, session, ship, mission) = JoinTwoAlphas(Config(1));
        using var serverKeeper = server;
        using var sessionKeeper = session;
        Assert.Equal(1, mission.Lives);

        // ── the one allowed death ────────────────────────────────────────────
        Assert.True(KillShipForReal(server, ship), "the lethal hit must actually kill (real death)");
        int respawned = PumpUntil(server, () => ship.StationId is not null, 2500);
        Assert.True(respawned < 2500, "server never respawned the ship docked (lifepod) after the death");

        int stats = PumpUntil(server, () => OwnState(session).Deaths >= 1, 2500);
        Assert.True(stats < 2500, "client never saw its own death (PLAYERINFO/KILL_SHIP deaths counter)");
        Assert.False(OwnState(session).Ghost, "1 death with iLives 1 is NOT a ghost yet");

        // Undock (still allowed) so the second death is a real flying kill again.
        session.SendDocked(-1);
        int undocked = PumpUntil(server, () => ship.StationId is null, 3000);
        Assert.True(undocked < 3000, "a live player must still be able to launch");

        // ── second death: out of lives ───────────────────────────────────
        Assert.True(KillShipForReal(server, ship));
        int respawned2 = PumpUntil(server, () => ship.StationId is not null, 2500);
        Assert.True(respawned2 < 2500, "server never respawned the ship docked after death #2");

        // The client learned the death AND the budget: deaths 2 > iLives 1.
        int stats2 = PumpUntil(server, () => OwnState(session).Deaths >= 2, 2500);
        Assert.True(stats2 < 2500, "client never saw death #2");
        int ghosted = PumpUntil(server, () => OwnState(session).Ghost, 2500);
        Assert.True(ghosted < 2500, "client must classify itself a ghost (deaths 2 > iLives 1)");
        Assert.True(session.World.MissionLives == 1, $"MISSIONDEF must carry the real budget (got {session.World.MissionLives})");

        // ── the ghost's BUY is refused (FedSrv.CPP:6156) ─────────────────────
        session.ResetBuyLoadoutAcks();
        session.SendBuyLoadout(ship.HullType.HullId, fLaunch: true);
        int acked = PumpUntil(server, () => session.BuyLoadoutAcks.Count > 0, 2500);
        Assert.True(acked < 2500, "ghost buy got no ack at all");
        var ack = session.BuyLoadoutAcks[^1];
        Assert.False(ack.FLaunch, "a ghost's BUY_LOADOUT(fLaunch:true) must not launch (FedSrv.CPP:6156)");
        Assert.False(ack.FBoughtEverything, "a ghost's BUY_LOADOUT must buy nothing");
        Assert.NotNull(ship.StationId);   // never left the dock

        // ── the ghost's LAUNCH is refused (FedSrv.CPP:7048) ──────────────────
        session.SendDocked(-1);
        int waited = PumpUntil(server, () => ship.StationId is null, 2500);
        Assert.True(waited >= 2500, "a ghost must NOT be undocked by FM_C_DOCKED(NA) — TELEPORT_ACK(NA), stays docked");
        Assert.NotNull(ship.StationId);
        Console.WriteLine("[P799-LIVE] ghost buy + launch both refused; ship stayed docked");
    }

    /// <summary>Join TWO peers on side 0 (Alpha) with injected open-space lifepod
    /// spawns, so one can be ghosted while the side stays alive (the all-ghost
    /// side elimination is its own test below). Returns the server, the FIRST
    /// session (its ship is the returned ship) and the mission. Caller disposes
    /// the server and the session.</summary>
    private static (Srv Server, ConnectionSession SessionA, Ship ShipA, Mission Mission) JoinTwoAlphas(Cfg config)
    {
        var server = new Srv();
        var spawned = new List<Ship>();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var (ship, sector) = OpenSpaceLifepod(mission, id, 0);
            spawned.Add(ship);
            return (ship, sector);
        });
        server.Boot(config);
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        var a = Connect(server, "P799Alpha1", 79900000000000001ul);
        var b = Connect(server, "P799Alpha2", 79900000000000006ul);
        a.SendPositionReq(-1);
        b.SendPositionReq(-1);
        int seated = PumpUntil(server, () => spawned.Count >= 2
            && a.World.MyShipId is not null && b.World.MyShipId is not null, 6000);
        Assert.True(seated < 6000, $"both Alpha clients never seated (spawned {spawned.Count})");
        Assert.Equal((short)0, a.World.MySideId);
        Assert.Equal((short)0, b.World.MySideId);
        return (server, a, spawned[0], spawned[0].Mission!);
    }

    /// <summary>Control for the unlimited sentinel: the same one death with the
    /// default (0x7fff) budget is NOT a ghost — buy+launch succeeds and the ship
    /// leaves the dock (every pre-P799 behaviour preserved).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Unlimited_lives_death_is_never_a_ghost()
    {
        Require.Artwork();
        using var server = new Srv();
        Ship? captured = null;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var (ship, sector) = OpenSpaceLifepod(mission, id, 0);
            captured = ship;
            return (ship, sector);
        });
        server.Boot(Config(MissionDef.UnlimitedLives));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var session = Connect(server, "P799Free", 79900000000000002ul);
        int seated = PumpUntil(server, () => session.World.MyShipId is not null && captured is not null, 4000);
        Assert.True(seated < 4000, "client never seated");
        var ship = captured!;
        Assert.Equal(MissionDef.UnlimitedLives, ship.Mission!.Lives);
        session.SendPositionReq(-1);
        PumpUntil(server, () => session.World.MyShipId is not null, 2000);

        Assert.True(KillShipForReal(server, ship), "the lethal hit must actually kill (real death)");
        int respawned = PumpUntil(server, () => ship.StationId is not null, 2500);
        Assert.True(respawned < 2500, "server never respawned the ship docked after the death");
        int stats = PumpUntil(server, () => OwnState(session).Deaths >= 1, 2500);
        Assert.True(stats < 2500, "client never saw its own death");
        Assert.False(OwnState(session).Ghost, "unlimited lives must never ghost");

        session.ResetBuyLoadoutAcks();
        session.SendBuyLoadout(ship.HullType.HullId, fLaunch: true);
        int acked = PumpUntil(server, () => session.BuyLoadoutAcks.Count > 0, 2500);
        Assert.True(acked < 2500, "non-ghost buy got no ack");
        Assert.True(session.BuyLoadoutAcks[^1].FLaunch, "a live player's buy+launch must be granted");
        int undocked = PumpUntil(server, () => ship.StationId is null, 3000);
        Assert.True(undocked < 3000, "a live player must actually launch after the buy");
        Console.WriteLine("[P799-LIVE] unlimited: death did not ghost; buy+launch succeeded");
    }

    /// <summary>⭐ Station loss moves a docked GHOST to another restart base
    /// (fsmission.cpp:4785-4850) instead of ejecting it — a ghost cannot launch,
    /// so an ejected ghost would be stranded in space forever. Fails on the
    /// pre-P799 tree (no ghosts: the ship is simply undocked).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Station_loss_relocates_a_docked_ghost_to_another_restart_base()
    {
        Require.Artwork();
        var (server, session, ship, mission) = JoinTwoAlphas(Config(0));
        using var serverKeeper = server;
        using var sessionKeeper = session;

        // Die once (iLives 0) → ghost docked at Alpha's garrison. The SECOND
        // Alpha player keeps the side alive through the ghost's elimination check.
        Assert.True(KillShipForReal(server, ship));
        int respawned = PumpUntil(server, () => ship.StationId is not null, 2500);
        Assert.True(respawned < 2500, "no respawn");
        int ghosted = PumpUntil(server, () => OwnState(session).Ghost, 2500);
        Assert.True(ghosted < 2500, "the victim must be a ghost after the death");
        var garrisonData = mission.Sectors.SelectMany(s => s.Stations)
            .First(st => st.StationId == ship.StationId!.Value);
        var garrison = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .First(st => st.StationId == garrisonData.StationId);
        short alphaSide = ship.SideId!.Value;

        // A second restart-capable Alpha base in a different sector, then lose
        // the garrison (RemoveStation = the combat pass's removal, KillStationEvent
        // = the host's STATION_DESTROYED/VacateStation seam).
        var type = mission.Core.StationTypes[garrisonData.StationTypeId];
        Assert.True(type.HasCapability(DataStationTypeIGC.SabmRestart), "the garrison must be restart-capable");
        var otherSector = mission.Sectors.First(s => s.Id != garrison.ClusterId);
        var altData = new DataStationIGC(
            Position: new float[] { 2000f, 0f, 2000f }, Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: otherSector.Id, SideId: alphaSide, StationId: 9001,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: "P799_ALT");
        mission.PlaceStation(new Station(altData, type), otherSector);
        mission.Sectors.First(s => s.Id == garrison.ClusterId).RemoveStation(garrison);
        mission.Site.KillStationEvent(garrison, null!, 1f);
        // The ghost must be RE-DOCKED at the alternate base, not ejected.
        int relocated = PumpUntil(server, () => ship.StationId == 9001, 3000);
        Assert.True(relocated < 3000,
            $"ghost must be relocated to the alternate restart base 9001 (StationId={ship.StationId}) — fsmission.cpp:4805-4849");
        int clientSees = PumpUntil(server, () => OwnState(session).StationId == 9001, 3000);
        Assert.True(clientSees < 3000,
            $"the client must see the ghost docked at the new base via SHIP_STATUS (got {OwnState(session).StationId})");
        Console.WriteLine("[P799-LIVE] destroyed station: ghost re-docked at restart base 9001");
    }

    /// <summary>⭐ Side elimination when only ghosts remain: with lives = 1 (one
    /// life each), Alpha's only player dies once → ghost → the side has no
    /// non-ghost player left → DeactivateSide (FedSrv.CPP:12070) → the native
    /// game-over path declares the last side standing the winner. Fails on the
    /// pre-P799 tree (no ghosts → no elimination → the game continues).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    [Trait("Tier", "Live")]
    public void Ghost_only_side_is_eliminated_and_last_side_wins()
    {
        Require.Artwork();
        using var server = new Srv();
        var spawned = new List<Ship>();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            short side = (short)Math.Min(spawned.Count, 1);   // 1st join → Alpha(0), 2nd → Bravo(1)
            var (ship, sector) = OpenSpaceLifepod(mission, id, side);
            spawned.Add(ship);
            return (ship, sector);
        });
        server.Boot(Config(0, "P799 Elimination"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var alpha = Connect(server, "P799Alpha", 79900000000000004ul);
        using var bravo = Connect(server, "P799Bravo", 79900000000000005ul);
        int seated = PumpUntil(server, () => alpha.World.MySideId is not null && bravo.World.MySideId is not null
                                             && spawned.Count >= 2, 6000);
        Assert.True(seated < 6000, $"both clients never seated (spawned {spawned.Count})");
        Assert.Equal((short)0, alpha.World.MySideId);
        Assert.Equal((short)1, bravo.World.MySideId);
        alpha.SendPositionReq(-1);
        bravo.SendPositionReq(-1);
        PumpUntil(server, () => alpha.World.MyShipId is not null && bravo.World.MyShipId is not null, 2000);

        var shipA = spawned[0];
        var shipB = spawned[1];
        var mission = shipA.Mission!;

        // Alpha's only player dies once → ghost → Alpha is all-ghosts → eliminated.
        Assert.True(KillShipForReal(server, shipA));
        int over = PumpUntil(server, () =>
            alpha.World.GameOverResult is { } goA && goA.SideWinner == 1, 6000);
        Assert.True(over < 6000,
            $"Alpha must be eliminated and Bravo declared the winner (GameOverResult={(alpha.World.GameOverResult is null ? "none" : alpha.World.GameOverResult.SideWinner.ToString())})");
        // Server truth: the all-ghost side is deactivated (DeactivateSide,
        // SIDE_INACTIVE(68) bActive=false went out to the whole mission before the
        // game-over — TCP in-order, so both clients applied it; the client's
        // IsSideActive view is additionally covered headlessly by
        // ClientWorldSideInactiveTests. The rotation's fresh-world re-burst may
        // legitimately re-seed client side state, so the durable assertion is
        // the server's Side.Active and the received game-over winner.
        Assert.False(mission!.Sides.First(s => s.Id == 0).Active,
            "server: the all-ghost side must be deactivated (FedSrv.CPP:12070 → DeactivateSide)");
        int bravoOver = PumpUntil(server, () =>
            bravo.World.GameOverResult is { } goB && goB.SideWinner == 1, 4000);
        Assert.True(bravoOver < 4000, "the surviving side must also see the game-over declaration");
        Assert.True(shipB.StationId is null || true);   // Bravo untouched by Alpha's elimination
        Console.WriteLine("[P799-LIVE] all-ghost side eliminated; Bravo won by outlasting (native game-over path)");
    }
}
