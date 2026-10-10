using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P521 — the REQUIRED failing-first LIVE test of the death-latch respawn fix. A
/// real <see cref="ConnectionSession"/> drives the REAL production own-ship stack
/// (<see cref="OwnShipSimulator"/> + <see cref="OwnShipServerClock"/> from REAL
/// FM_CS_PING echoes + <c>SendShipPose</c>) over a REAL TCP wire to a REAL
/// in-process <c>Allegiance.Server</c>, and the test replicates the production
/// freeze gate (<c>Game1.Prediction.UpdateOwnShipSimulation</c>: the local sim only
/// emits native SHIP_UPDATEs while the own ship is NOT frozen, i.e. not docked /
/// not dead / not gone).
///
/// Sequence (the brief's exact arc, repeated twice): fly → a REAL death (lethal
/// hull damage on the real combat path) → the server respawns the ship DOCKED as a
/// Lifepod at a friendly station (KILL_SHIP + SHIP_DELETE SDR_KILLED + docked
/// SHIP_STATUS → the client's <c>ShipEntity.IsDead</c> latches → the sim FREEZES
/// at "dead" and stops sending) → respawn + UNDOCK (the server's undock
/// SET_CLUSTER, P518) → the death latch CLEARS → the sim RESUMES → the server's
/// P507 <c>accepted</c> counter climbs again. Repeated for a second death.
///
/// FAILING-FIRST: on the unfixed code <c>ShipEntity.IsDead</c> is permanent, so
/// after death the own ship stays "dead" forever: the undock SET_CLUSTER does NOT
/// clear it, the sim stays frozen ("[sim] STOPPED sending native SHIP_UPDATE:
/// sim frozen … dead"), no native SHIP_UPDATEs flow, and the server's <c>accepted</c>
/// counter stays frozen where it was. This test is RED there (the post-respawn
/// "IsDead cleared + accepted climbed" assertions fail / time out) and GREEN on the
/// fix.
/// </summary>
[Collection("LiveNetwork")]
public sealed class DeadLatchRespawnLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static readonly FieldInfo PeerUpdatesField =
        typeof(Srv).GetField("_peerShipUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server._peerShipUpdate field not found");

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P521 Dead Latch", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 721u, DwCookie = 0x721u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8210000000000000000ul, DrmHash = "p521deadlatch",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Spawn the peer's ship as a LIFEPOD in open space well clear of the
    /// friendly garrison (mirror of SuicideLiveTests' already-a-lifepod trick, so a
    /// lethal hit is a REAL death — bLifepod ⇒ bKilled — not the P107 in-space
    /// eject). Offset far enough that the baseline flight never collides with the
    /// station. The friendly garrison is the respawn target (FindFriendlyStation).</summary>
    private static (Ship Ship, Sector Sector) OpenSpaceLifepod(Mission mission, short shipId)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
        var lifepod = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
        var spawn = StationPos(garrison) + new Vector3(0f, 0f, 1500f);
        var ship = new Ship(shipId, lifepod, spawn, Vector3.Zero, Orientation.Identity)
        {
            SideId = garrison.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    /// <summary>Pace the server to ~20 Hz against REAL time (P518's PacedTick) so
    /// the own-ship server clock's SimNow vs real-time invariant holds.</summary>
    private static void PacedTick(Srv server)
    {
        long t0 = Environment.TickCount64;
        server.TickSim();
        int toSleep = 48 - (int)(Environment.TickCount64 - t0);
        if (toSleep > 0) Thread.Sleep(toSleep);
    }

    private static double CalibrateClock(Srv server, ConnectionSession session, OwnShipServerClock clock, ref double localT)
    {
        for (int i = 0; i < 40; i++)
        {
            PacedTick(server);
            localT += 0.05;
            if (i % 10 == 0) session.SendPing();
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
        }
        if (!clock.Ready) return double.PositiveInfinity;
        return clock.ServerTimeMs(Environment.TickCount64) / 1000.0 - server.SimNow;
    }

    /// <summary>Adopt the own-ship cookie + apply any pending SHIP_RESET, under the
    /// gate (mirror of P518's SyncOwnShip).</summary>
    private static void SyncOwnShip(Srv server, ConnectionSession session, OwnShipSimulator sim, ref byte cookie)
    {
        lock (session.Gate)
        {
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
            {
                cookie = mine.Cookie;
                if (mine.HasPendingReset)
                {
                    sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                    session.World.ClearOwnShipResetFlag(me);
                }
            }
        }
    }

    /// <summary>The production freeze gate, replicated faithfully from
    /// <c>Game1.Prediction.UpdateOwnShipSimulation</c>: the sim only sends while the
    /// own ship is NOT docked and NOT dead/gone (ripcord/sector are not in play in
    /// this test). IsDead latched (post-death) OR StationId set (docked) ⇒ frozen ⇒
    /// no SHIP_UPDATE — exactly why the real client's <c>accepted</c> froze in the bug.</summary>
    private static bool SimFrozen(ConnectionSession session)
    {
        lock (session.Gate)
        {
            if (session.World.MyShipId is not { } me || !session.World.Ships.TryGetValue(me, out var mine))
                return true;   // no own ship yet — not flying
            return mine.StationId != -1 || mine.IsNotVisible;
        }
    }

    /// <summary>Emit a native SHIP_UPDATE for ONE paced tick — but only if the sim
    /// is NOT frozen (mirrors the real client's send eligibility). While frozen
    /// (docked / dead) it sends nothing, so the server's <c>accepted</c> cannot
    /// climb. When unfrozen it follows the live server pose to stay inside the
    /// 20 m acceptance window.</summary>
    private static void SendOneIfFlying(
        Srv server, ConnectionSession session, Ship serverShip, OwnShipSimulator sim, OwnShipServerClock clock,
        ref double localT, ref byte cookie, ref double lastPing)
    {
        bool frozen = SimFrozen(session);
        SyncOwnShip(server, session, sim, ref cookie);
        lock (session.Gate)
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                sim.Seed(serverShip.Position, serverShip.Velocity, up.Orientation, up.TurnRates);

        if (!frozen)
        {
            if (localT - lastPing >= 0.5) { session.SendPing(); lastPing = localT; }
            foreach (var (serverMs, recvMs) in session.TakePingEchoes())
                clock.AddSample(serverMs, recvMs);
            if (clock.Ready)
            {
                uint t = clock.ServerTimeMs(Environment.TickCount64);
                var payload = ClientShipUpdate.FromPose(
                    t, sim.Position, sim.Velocity, sim.Orientation, 0, ControlData.Neutral, sim.TurnRates, sim.Power);
                session.SendShipPose(t, cookie, payload);
            }
        }
        PacedTick(server);
        localT += 0.05;
    }

    /// <summary>Drive <see cref="SendOneIfFlying"/> until <paramref name="done"/>
    /// or <paramref name="maxSteps"/>. Returns ticks elapsed.</summary>
    private static int FlyUntil(
        Srv server, ConnectionSession session, Ship serverShip, OwnShipSimulator sim, OwnShipServerClock clock,
        ref double localT, ref byte cookie, ref double lastPing, Func<bool> done, int maxSteps)
    {
        int n = 0;
        while (!done() && n < maxSteps)
        {
            SendOneIfFlying(server, session, serverShip, sim, clock, ref localT, ref cookie, ref lastPing);
            n++;
        }
        return n;
    }

    /// <summary>Read a server per-peer P507 counter via reflection (P518's Pu).</summary>
    private static long Pu(Srv server, string field)
    {
        var dict = (IDictionary)PeerUpdatesField.GetValue(server)!;
        if (dict.Count == 0) return 0;
        var pu = dict.Values.Cast<object>().First();
        return (long)pu.GetType().GetField(field)!.GetValue(pu)!;
    }

    private static (bool Dead, short StationId) OwnState(ConnectionSession session)
    {
        lock (session.Gate)
        {
            if (session.World.MyShipId is not { } me || !session.World.Ships.TryGetValue(me, out var mine))
                return (false, -1);
            return (mine.IsDead, mine.StationId);
        }
    }

    /// <summary>Kill the real server ship on the real combat path: a lethal
    /// ReceiveDamage on the sim thread raises KillShipEvent → the server's
    /// BroadcastKillShip (KILL_SHIP + SHIP_DELETE SDR_KILLED to the sector's peers,
    /// incl. our own client) and the P105 lifepod respawn (docked at a friendly
    /// station, ENTER_LIFEPOD). Picks a projectile damage type whose armor factor
    /// against this hull's defense type is &gt; 0 (a zero-factor type would deal no
    /// damage and never kill) and a huge amount so the armor factor cannot save the
    /// hull. Returns whether the ship actually died (the bLifepod real-death path).</summary>
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
        // A large multiplier (not a modest 2×) so the armor factor dtm — which
        // scales the leaked damage — cannot save the hull; 2× survives any factor
        // < 0.5. fraction -= amount·dtm/maxHP = multiplier·fraction·dtm, so a big
        // multiplier kills for any dtm > 0 without overflowing the float.
        ship.ReceiveDamage(dmgType, ship.HitPoints * 1e6f, mission.LastUpdate, ship.Position, ship.Position, launcher: null);
        Console.WriteLine($"[P521-LIVE] dealt lethal damage type {dmgType} to ship {ship.Id} (hull {ship.HullType.Name}, dead={ship.Dead})");
        return ship.Dead;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock()
    {
        Require.Artwork();
        var server = new Srv();
        Ship? captured = null;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var (ship, sector) = OpenSpaceLifepod(mission, id);
            captured = ship;
            return (ship, sector);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        byte cookie = 0;
        double localT = 0.0, lastPing = -0.4;
        var sim = new OwnShipSimulator();
        var clock = new OwnShipServerClock();

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P521", "127.0.0.1");
        session.SendLogonRequest(Request("P521"));
        int acked = PumpUntil(server, () => session.Ack is not null, 8000);
        Assert.True(acked < 8000, "client never received LOGONACK");
        session.SendPositionReq(-1);
        int seated = PumpUntil(server, () => session.World.MyShipId is not null && captured is not null, 4000);
        Assert.True(seated < 4000, "client never seated");
        var ship = captured!;
        var mission = ship.Mission!;

        lock (session.Gate)
            if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
            {
                sim.SetHull(ShipDeadReckoner.HullStats.FromHull(ship.HullType), 0f);
                sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                cookie = mine.Cookie;
            }
        double calibErr = CalibrateClock(server, session, clock, ref localT);
        Assert.True(Math.Abs(calibErr) < 1.0, $"server-clock error {calibErr:F2}s — the ±1.5 s gate will reject poses");
        Console.WriteLine($"[P521-LIVE] client {session.World.MyShipId} seated; clock err {calibErr:+0.000;-0.000}s; initial cookie 0x{cookie:X2}");

        // ── Baseline: fly (follow the live server pose) a while — accepted must
        //    climb (the sim is alive and sending). ───────────────────────────────
        long accepted0 = Pu(server, "Accepted");
        int fly0 = FlyUntil(server, session, ship, sim, clock, ref localT, ref cookie, ref lastPing,
            () => Pu(server, "Accepted") >= accepted0 + 15, 1500);
        long acceptedBaseline = Pu(server, "Accepted");
        Assert.True(acceptedBaseline >= accepted0 + 15,
            $"baseline flying never advanced accepted (start {accepted0} -> {acceptedBaseline})");
        Console.WriteLine($"[P521-LIVE] baseline: flying, accepted -> {acceptedBaseline} ({fly0} ticks)");

        // ══ DEATH #1 ══ a real kill on the real combat path ─────────────────────
        Assert.True(KillShipForReal(server, ship), "the lethal hit must actually kill the ship (real death, no eject)");
        int died1 = PumpUntil(server, () => OwnState(session).Dead, 2500);
        Assert.True(died1 < 2500, $"death #1 never latched IsDead on the client (died={died1})");
        Assert.True(SimFrozen(session), "the own ship must be FROZEN (dead) after death — no SHIP_UPDATEs");
        int respawned1 = PumpUntil(server, () => ship.StationId is not null, 2500);
        Assert.True(respawned1 < 2500, "server never respawned the ship docked (lifepod) after death #1");
        long acceptedDied1 = Pu(server, "Accepted");
        Console.WriteLine($"[P521-LIVE] DEATH #1: client IsDead=true, sim frozen; respawned docked; accepted frozen at {acceptedDied1}");

        // ══ RESPAWN + UNDOCK #1 ══ the P518 undock SET_CLUSTER must clear IsDead ──
        session.SendDocked(-1);
        // The discriminator: on the FIXED code the undock SET_CLUSTER clears IsDead
        // and the periodic SHIP_STATUS clears StationId → unfrozen. On the UNFIXED
        // code IsDead stays true forever → this wait times out (failing-first).
        int undocked1 = PumpUntil(server, () => { var (d, s) = OwnState(session); return !d && s == -1; }, 3500);
        Assert.True(undocked1 < 3500, "death latch was NEVER cleared by the respawn+undock — the ship stayed dead on the client (the P521 bug)");
        Console.WriteLine($"[P521-LIVE] RESPAWN+UNDOCK #1: IsDead cleared + StationId=-1 after {undocked1} ticks");

        long acceptedPreResume1 = Pu(server, "Accepted");
        int fly1 = FlyUntil(server, session, ship, sim, clock, ref localT, ref cookie, ref lastPing,
            () => Pu(server, "Accepted") >= acceptedPreResume1 + 15, 1500);
        long acceptedRespawn1 = Pu(server, "Accepted");
        Assert.True(acceptedRespawn1 >= acceptedPreResume1 + 15,
            $"accepted did NOT climb after respawn+undock #1 (frozen at {acceptedPreResume1}) — sim never resumed");
        Assert.True(acceptedRespawn1 > acceptedDied1,
            $"accepted did NOT recover past the death (died1={acceptedDied1}, respawn1={acceptedRespawn1})");
        Console.WriteLine($"[P521-LIVE] AFTER #1: accepted {acceptedDied1} -> {acceptedRespawn1} (climbed {acceptedRespawn1 - acceptedDied1}); IsDead=false");

        // ══ DEATH #2 ══ (the ship is a lifepod again after respawn #1; killing it
        //    is a real death because the hull is a lifepod) ─────────────────────
        long acceptedBetween = Pu(server, "Accepted");
        Assert.True(KillShipForReal(server, ship), "the second lethal hit must actually kill the ship");
        int died2 = PumpUntil(server, () => OwnState(session).Dead, 2500);
        Assert.True(died2 < 2500, $"death #2 never latched IsDead on the client (died={died2})");
        Assert.True(SimFrozen(session), "the own ship must be FROZEN (dead) again after death #2");
        int respawned2 = PumpUntil(server, () => ship.StationId is not null, 2500);
        Assert.True(respawned2 < 2500, "server never respawned the ship docked after death #2");
        long acceptedDied2 = Pu(server, "Accepted");
        Console.WriteLine($"[P521-LIVE] DEATH #2: IsDead=true, sim frozen; accepted frozen at {acceptedDied2}");

        // ══ RESPAWN + UNDOCK #2 ══ repeat the fix ───────────────────────────────
        session.SendDocked(-1);
        int undocked2 = PumpUntil(server, () => { var (d, s) = OwnState(session); return !d && s == -1; }, 3500);
        Assert.True(undocked2 < 3500, "death latch was NEVER cleared by the respawn+undock #2 (the P521 bug)");
        int fly2 = FlyUntil(server, session, ship, sim, clock, ref localT, ref cookie, ref lastPing,
            () => Pu(server, "Accepted") >= acceptedDied2 + 15, 1500);
        long acceptedRespawn2 = Pu(server, "Accepted");
        Assert.True(acceptedRespawn2 >= acceptedDied2 + 15,
            $"accepted did NOT climb after respawn+undock #2 (frozen at {acceptedDied2}) — sim never resumed");
        long rejected = Pu(server, "Rejected");
        long droppedCookie = Pu(server, "DroppedCookie");
        Console.WriteLine($"[P521-LIVE] AFTER #2: accepted {acceptedDied2} -> {acceptedRespawn2} (climbed {acceptedRespawn2 - acceptedDied2})");
        Console.WriteLine($"[P521-LIVE] FINAL: accepted={acceptedRespawn2} rejected={rejected} dropped[cookie]={droppedCookie}");
        Assert.True(acceptedRespawn1 > acceptedDied1 && acceptedRespawn2 > acceptedDied2,
            "accepted must climb after BOTH respawn+undock arcs (sim resumes both times)");
        Assert.True(rejected == 0, $"server rejected {rejected} native poses across the death/respawn sequence");

        session.Shutdown();
    }

    /// <summary>⭐ Two-client live proof (brief verification #2): a REMOTE client B
    /// sees the victim A again after A dies and respawns. Both peers ride the same
    /// friendly garrison sector; A's real death latches <c>IsDead</c> on B (KILL_SHIP
    /// reaches same-sector peers), and when A respawns + undocks back into the shared
    /// sector, B's in-cluster HeavyShipsUpdate(176) clears the latch so B sees A
    /// again — the remote counterpart of the own-ship fix.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Remote_client_sees_victim_again_after_death_and_respawn()
    {
        Require.Artwork();
        var server = new Srv();
        var ships = new List<Ship>();
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
            var lifepod = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));
            var offset = ships.Count == 0 ? new Vector3(0f, 0f, 1500f) : new Vector3(0f, 0f, -1500f);
            var ship = new Ship(id, lifepod, StationPos(garrison) + offset, Vector3.Zero, Orientation.Identity)
            {
                SideId = garrison.SideId,
            };
            mission.PlaceShip(ship, sector);
            ships.Add(ship);
            return (ship, sector);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var alpha = new ConnectionSession();
        using var bravo = new ConnectionSession();
        alpha.Connect(server.BoundPort, "A", "127.0.0.1");
        bravo.Connect(server.BoundPort, "B", "127.0.0.1");
        alpha.SendLogonRequest(Request("ALPHA"));
        bravo.SendLogonRequest(Request("BRAVO"));
        int seated = PumpUntil(server, () =>
            alpha.World.MyShipId is not null && bravo.World.MyShipId is not null && ships.Count == 2, 5000);
        Assert.True(seated < 5000, "both clients must seat");

        short aid = alpha.World.MyShipId!.Value;
        short bid = bravo.World.MyShipId!.Value;
        Assert.NotEqual(aid, bid);
        var aShip = ships.First(s => s.Id == aid);
        var bShip = ships.First(s => s.Id == bid);
        Assert.Equal(aShip.Sector, bShip.Sector);   // both in the same (garrison) sector

        // B initially sees A alive.
        bool BravoSeesDead() { lock (bravo.Gate) return bravo.World.Ships.TryGetValue(aid, out var e) && e.IsDead; }
        Assert.False(BravoSeesDead(), "B should see A alive before the death");

        // ══ DEATH: A dies for real (lifepod → real death) — B must latch IsDead. ══
        Assert.True(KillShipForReal(server, aShip), "A's lethal hit must kill");
        int bSawDead = PumpUntil(server, BravoSeesDead, 2500);
        Assert.True(bSawDead < 2500, $"B never saw A's death (IsDead never latched on B) after {bSawDead} ticks");
        int aResp = PumpUntil(server, () => aShip.StationId is not null, 2500);
        Assert.True(aResp < 2500, "A never respawned docked after death");
        Console.WriteLine($"[P521-2C] B saw A die (IsDead latched) after {bSawDead} ticks; A respawned docked");

        // ══ RESPAWN + UNDOCK: A undocks back into the shared sector → B's 176 must
        //    clear the latch so B sees A again. ══
        alpha.SendDocked(-1);
        int bSawBack = PumpUntil(server, () => !BravoSeesDead(), 3500);
        Assert.True(bSawBack < 3500,
            $"B NEVER saw A again after A's death+respawn (IsDead stayed latched on B — the remote half of the P521 bug)");
        Console.WriteLine($"[P521-2C] B sees A again after A's respawn+undock ({bSawBack} ticks); IsDead cleared on B");

        alpha.Shutdown();
        bravo.Shutdown();
    }
}
