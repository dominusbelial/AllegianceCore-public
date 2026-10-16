using System.Linq;
using System.Reflection;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P540b — LIVE proof that a cross-sector autopilot target is reached THROUGH
/// WARPS, not by flying at the target's foreign-sector coordinates (the P540
/// "middle of nowhere" eject bug). A real in-process server seats a real ejecting
/// player and a cross-side shooter; lethal damage ejects the player's ship into a
/// lifepod in a sector EXACTLY TWO REAL WARP HOPS from the side's rescue
/// garrison (the nearest friendly rescue target — the production
/// <see cref="Game1.ChooseEjectRescueTarget"/> pick selects it). A faithful client
/// pilot then flies the pod with the PRODUCTION Game1 autopilot, including
/// cross-sector routing and the station landing-bay entry maneuver,
/// driving the REAL local sim (<see cref="OwnShipSimulator"/>) and emitting native
/// <c>SHIP_UPDATE(24)</c> poses the server accepts (P508). The pod transits BOTH
/// warps and docks via the server's rescue/land path, with ZERO SHIP_RESETs and
/// ZERO [ctrlak]-equivalent pose rejects.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P540bPodAutopilotCrossSectorLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static readonly FieldInfo PeerUpdatesField =
        typeof(Srv).GetField("_peerShipUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server._peerShipUpdate field not found");

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P540b Cross-Sector Pod", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 902u, DwCookie = 0x902u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97200000000000902ul, DrmHash = "p540b",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void PacedTick(Srv server)
    {
        long t0 = Environment.TickCount64;
        server.TickSim();
        int toSleep = 48 - (int)(Environment.TickCount64 - t0);
        if (toSleep > 0) Thread.Sleep(toSleep);
    }

    private static long Pu(Srv server, string field)
    {
        var dict = (System.Collections.IDictionary)PeerUpdatesField.GetValue(server)!;
        if (dict.Count == 0) return 0;
        var pu = dict.Values.Cast<object>().First();
        return (long)pu.GetType().GetField(field)!.GetValue(pu)!;
    }

    private static Ship ServerShip(Srv server, short id) =>
        server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>())
            .First(s => s.Id == id);

    private static SimVec Pos(ResolvedWarp w) =>
        new(w.Warp.Position![0], w.Warp.Position[1], w.Warp.Position[2]);

    private static SimVec ClearPoint(Sector sector, SimVec anchor, float distance)
    {
        var warpPositions = sector.Warps
            .Where(w => w.Warp.Position is { Length: 3 })
            .Select(Pos).ToList();
        SimVec best = anchor;
        float bestMin = -1f;
        for (int xi = -1; xi <= 1; xi++)
        for (int yi = -1; yi <= 1; yi++)
        for (int zi = -1; zi <= 1; zi++)
        {
            if (xi == 0 && yi == 0 && zi == 0) continue;
            var dir = new SimVec(xi, yi, zi).Normalize();
            var cand = anchor + dir * distance;
            float mn = float.MaxValue;
            foreach (var p in warpPositions) mn = MathF.Min(mn, (cand - p).Length());
            if (mn > bestMin) { bestMin = mn; best = cand; }
        }
        return best;
    }

    /// <summary>Flatten a station's position triple into a <see cref="SimVec"/>.</summary>
    private static SimVec Pos2(float[] p) => new(p[0], p[1], p[2]);

    /// <summary>Flatten the CLIENT world's warp table into the router's
    /// ground-truth records, resolving the destination SECTOR from the warp's
    /// PARTNER (a warp's raw DestinationId is the partner warp id, not the
    /// destination sector — see Game1.AllWarps).</summary>
    private static List<StarmapGotoRouter.AllWarp> AllWarps(ClientWorld world)
    {
        var warps = new List<StarmapGotoRouter.AllWarp>();
        foreach (var w in world.Warps.Values)
        {
            if (w.Position is not { Length: 3 }) continue;
            short destSector = world.Warps.TryGetValue(w.DestinationId, out var partner) ? partner.ClusterId : w.DestinationId;
            warps.Add(new StarmapGotoRouter.AllWarp(
                new System.Numerics.Vector3(w.Position[0], w.Position[1], w.Position[2]),
                w.ClusterId, destSector));
        }
        return warps;
    }

    /// <summary>Undirected BFS hop count over the CLIENT world's warp table
    /// (same source the router navigates by). -1 when unreachable.</summary>
    private static int RealHopCount(IReadOnlyList<StarmapGotoRouter.AllWarp> allWarps, short from, short to)
    {
        if (from == to) return 0;
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in allWarps)
        {
            if (!adj.TryGetValue(w.SourceSector, out var l)) adj[w.SourceSector] = l = new();
            if (!l.Contains(w.DestSector)) l.Add(w.DestSector);
            if (!adj.TryGetValue(w.DestSector, out var l2)) adj[w.DestSector] = l2 = new();
            if (!l2.Contains(w.SourceSector)) l2.Add(w.SourceSector);
        }
        var dist = new Dictionary<short, int> { [from] = 0 };
        var q = new Queue<short>(); q.Enqueue(from);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            foreach (var nb in adj[c]) if (!dist.ContainsKey(nb)) { dist[nb] = dist[c] + 1; q.Enqueue(nb); }
        }
        return dist.TryGetValue(to, out var d) ? d : -1;
    }

    private static void ResolveBothSessions(Srv server, ConnectionSession a, ConnectionSession b,
        out ConnectionSession victim, out ConnectionSession shooter)
    {
        short SideOf(ConnectionSession s)
        {
            lock (s.Gate)
                return s.World.MyShipId is { } id && s.World.Ships.TryGetValue(id, out var e) ? e.SideId : (short)-1;
        }
        if (SideOf(a) == 0) { victim = a; shooter = b; }
        else if (SideOf(b) == 0) { victim = b; shooter = a; }
        else throw new InvalidOperationException($"neither session on side 0 (a={SideOf(a)}, b={SideOf(b)})");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EjectedPod_routes_through_two_warps_and_docks_at_rescue_garrison_zero_resets()
    {
        Require.Artwork();
        using var server = new Srv();
        const short EjectSectorId = 1051;   // exactly TWO real warps from the side-0 garrison's sector 1053
        short spawnCount = 0;
        ShipSpawnerField.SetValue(server, (StaticCore core, Mission mission, short shipId) =>
        {
            var A = mission.Sectors.First(s => s.Id == EjectSectorId);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var wp = A.Warps.FirstOrDefault(w => w.Warp.Position is { Length: 3 });
            SimVec anchor = wp is null ? SimVec.Zero : Pos(wp);
            SimVec clear = ClearPoint(A, anchor, 600f);   // clear of every warp: no pre-eject transit
            SimVec pos = spawnCount == 0 ? clear : clear + new SimVec(0f, 0f, -300f);
            var ship = new Ship(shipId, hull, pos, SimVec.Zero, Orientation.Identity)
            { SideId = (short)(spawnCount == 0 ? 0 : 1) };
            mission.PlaceShip(ship, A);
            spawnCount++;
            return (ship, A);
        });
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var sessA = new ConnectionSession();
        using var sessB = new ConnectionSession();
        sessA.Connect(server.BoundPort, "P540BV"); sessB.Connect(server.BoundPort, "P540BS");
        sessA.SendLogonRequest(Request("P540BV")); sessB.SendLogonRequest(Request("P540BS"));
        int seated = PumpUntil(server,
            () => sessA.World.MyShipId is { } && sessB.World.MyShipId is { }
               && sessA.World.MySideId is { } && sessB.World.MySideId is { },
            4000);
        Assert.True(seated < 4000, "sessions never seated");
        ResolveBothSessions(server, sessA, sessB, out var victim, out var shooter);
        short victimId = victim.World.MyShipId!.Value;
        short victimSide = victim.World.MySideId!.Value;
        Assert.Equal((short)0, victimSide);

        // ── Plan the trip in the CLIENT world's warp table (the router's truth). ──
        short ownSector;
        short garrisonId;
        short targetSector; SimVec targetPos;
        lock (victim.Gate)
        {
            ownSector = victim.World.Ships[victimId].SectorId;
            garrisonId = victim.World.Stations.Values.First(st => st.SideId == victimSide).StationId;
            targetSector = victim.World.Stations[garrisonId].ClusterId;
            targetPos = Pos2(victim.World.Stations[garrisonId].Position);
        }
        Assert.Equal(EjectSectorId, ownSector);
        var allWarps = AllWarps(victim.World);
        Assert.True(RealHopCount(allWarps, ownSector, targetSector) == 2,
            $"eject sector {ownSector} must be exactly 2 warps from the garrison {targetSector}");
        // The client router's first warp mouth toward the garrison (the exact call
        // the autopilot makes each frame) — so the pod starts aimed at the warp it
        // will actually fly to, and the test can never disagree with in-flight routing.
        Assert.True(StarmapGotoRouter.TryNextWaypointFromWarps(ownSector, targetSector, allWarps, out var wp),
            "router found no first waypoint from the eject sector");
        short midSector = allWarps.First(w => w.SourceSector == ownSector
            && new System.Numerics.Vector3(wp.X, wp.Y, wp.Z) == w.Position).DestSector;
        Console.WriteLine($"[p540b] plan: eject sector {ownSector} -> {midSector} -> garrison sector {targetSector} (2 warps), first warp mouth {wp}, garrison @ {targetPos}");

        // Reposition the pre-eject victim near the router's first warp mouth (clear
        // of the collision volume) so leg 1 is short and the pod starts at the right warp.
        var ejectSector = ServerShip(server, victimId).Sector!;
        SimVec start = ClearPoint(ejectSector, new SimVec(wp.X, wp.Y, wp.Z), 350f);
        ServerShip(server, victimId).Position = start;
        ServerShip(server, victimId).Velocity = SimVec.Zero;

        // ── EJECT: deal lethal cross-side damage → the victim becomes a lifepod. ──
        var victimShip = ServerShip(server, victimId);
        var shooterShip = ServerShip(server, shooter.World.MyShipId!.Value);
        SimVec ejectPos = victimShip.Position;
        victimShip.ReceiveDamage(0, victimShip.HitPoints * 2f, server.Missions[0].LastUpdate, ejectPos, SimVec.Zero, shooterShip);
        int applied = PumpUntil(server,
            () => { lock (victim.Gate) return victim.World.Ships.TryGetValue(victimId, out var e) && e.HasPendingEject; },
            4000);
        Assert.True(applied < 4000, "victim client never applied EJECT");
        short lifepodHull = 0;
        lock (victim.Gate)
        {
            var ent = victim.World.Ships[victimId];
            lifepodHull = ent.HullId;
            Console.WriteLine($"[eject] own ship {victimId} became lifepod hull {ent.HullId} at {ent.Latest!.Position}");
        }
        Assert.True(ServerShip(server, victimId).HullType.HasCapability(DataHullTypeIGC.HabmLifepod),
            $"server pod hull {lifepodHull} is not a lifepod");

        // ── PRODUCTION rescue-target pick (real world + real catalogue) ──
        var cat = ShopCatalog.TryLoad(ArtRoot);
        Assert.NotNull(cat);
        SimVec pickFrom;
        lock (victim.Gate) pickFrom = victim.World.Ships[victimId].Latest!.Position;
        short StationSabm(short tid) => cat!.StationTypes is { } sts && sts.TryGetValue(tid, out var st) ? st.SabmCapabilities : (short)0;
        short ProbeEabm(short tid) => cat!.ProbeTypes is { } pts && pts.TryGetValue(tid, out var p) ? p.EabmCapabilities : (short)0;
        var target = Game1.ChooseEjectRescueTarget(victim.World, victimSide, pickFrom, StationSabm, ProbeEabm);
        Assert.NotNull(target);
        Assert.Equal(Game1.TargetKind.Station, target!.Value.Kind);
        Assert.Equal(garrisonId, target.Value.Id);
        Assert.NotEqual(ownSector, targetSector);   // the rescue target is in ANOTHER sector (cross-sector)
        Console.WriteLine($"[eject] autopilot target chosen: {target.Value.Name} ({target.Value.Kind}#{target.Value.Id}) in sector {targetSector} (garrison, {RealHopCount(allWarps, ownSector, targetSector)} warps away)");

        // ── Client pilot: fly the pod with the PRODUCTION cross-sector law. ──
        var sim = new OwnShipSimulator();
        var pilot = P699HumanPodHomeLiveTests.CreateAutopilot(victim, sim, ArtRoot, target.Value);
        var clock = new OwnShipServerClock();
        double localT = 0.0, lastPing = -0.4;
        byte cookie = 0;
        long clientResets = 0;
        short lastSector = -1;
        long rejectsBefore = Pu(server, "Rejected");
        {
            var podHull = ServerShip(server, victimId).HullType;
            sim.SetHull(ShipDeadReckoner.HullStats.FromHull(podHull), 0f);
        }
        // Clock calibration (real-time paced so stamps track server SimNow).
        for (int i = 0; i < 30; i++)
        {
            PacedTick(server); localT += 0.049;
            if (i % 10 == 0) victim.SendPing();
            foreach (var (sm, lm) in victim.TakePingEchoes()) clock.AddSample(sm, lm);
        }
        // Seed the sim from the CURRENT server pod pose (the pod drifted during
        // calibration), not the stale eject-time pose — a pose seeded from the
        // eject pose would be ~100 units off the drifted pod and rejected on the
        // first SHIP_UPDATE.
        lock (victim.Gate)
        {
            if (victim.World.MyShipId is { } me && victim.World.Ships.TryGetValue(me, out var ent) && ent.Latest is { } up)
            {
                sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                cookie = ent.Cookie;
                lastSector = ent.SectorId;
            }
        }

        var transits = new List<short> { lastSector };
        bool docked = false;
        int ticks = 0;
        const int Cap = 11000;

        while (!docked && ticks < Cap)
        {
            // Reseed/adopt on any pending reset or sector change (transit) BEFORE
            // computing a goal or sending — a stale pre-transit pose would be rejected.
            short currentSector;
            lock (victim.Gate)
            {
                if (victim.World.MyShipId is { } me && victim.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } up)
                {
                    cookie = mine.Cookie;
                    if (mine.HasPendingReset)
                    {
                        sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                        victim.World.ClearOwnShipResetFlag(me);
                        clientResets++;
                    }
                    if (mine.SectorId != lastSector)
                    {
                        lastSector = mine.SectorId;
                        sim.Seed(up.Position, up.Velocity, up.Orientation, up.TurnRates);
                        if (transits.Count == 0 || transits[^1] != lastSector) transits.Add(lastSector);
                    }
                    currentSector = mine.SectorId;
                }
                else currentSector = -1;
            }

            // Only send a pose when the client world has CAUGHT UP to the server's
            // sector (no unseen warp transit in flight). After a transit the server
            // has already placed the pod in the new sector; until the client world
            // reflects that, we must not emit a pose from the old sector (reject).
            short srvSec = ServerShip(server, victimId).Sector?.Id ?? -1;
            if (srvSec == currentSector && currentSector != -1)
            {
                // P699: fly the real Game1 autopilot, including its bay entry;
                // steering only at the center hid oxygen respawns as "docks".
                var controls = P699HumanPodHomeLiveTests.AutopilotControls(pilot, .049f);
                sim.Step(0.049f, controls, P699HumanPodHomeLiveTests.AutopilotMovement(pilot));

                // Send a native SHIP_UPDATE pose (real cadence; clock-tracked server time).
                if (localT - lastPing >= 0.5) { victim.SendPing(); lastPing = localT; }
                foreach (var (sm, lm) in victim.TakePingEchoes()) clock.AddSample(sm, lm);
                if (clock.Ready)
                {
                    uint t = clock.ServerTimeMs(Environment.TickCount64);
                    var payload = ClientShipUpdate.FromPose(t, sim.Position, sim.Velocity, sim.Orientation,
                        P699HumanPodHomeLiveTests.AutopilotMovement(pilot), controls, sim.TurnRates, sim.Power);
                    victim.SendShipPose(t, cookie, payload);
                }
            }

            PacedTick(server); localT += 0.047;
            ticks++;

            if (ServerShip(server, victimId).StationId is not null) docked = true;
        }

        long rejectsAfter = Pu(server, "Rejected");
        bool podDocked = ServerShip(server, victimId).StationId is not null;
        short finalSector = ServerShip(server, victimId).Sector?.Id ?? -1;

        Console.WriteLine($"[p540b] transits {string.Join(" -> ", transits)}; final sector {finalSector}; docked={podDocked}; rejects {rejectsAfter - rejectsBefore}; SHIP_RESETs {clientResets}; ticks {ticks}/{Cap}");
        Console.WriteLine($"[rescue] ejected pod {victimId} docked (landed) at station {ServerShip(server, victimId).StationId} '{ServerGarrison(server).Name}' via the server's station dock/rescue path after {(transits.Count - 1)} warp transit(s)");

        Assert.True(podDocked, $"pod never docked at the garrison after {ticks} ticks (final sector {finalSector})");
        lock (victim.Gate)
            Assert.DoesNotContain(victim.World.Events.Kills, k => k.ShipId == victimId && k.BDeathCredit);
        // Both warps crossed: eject sector -> intermediate -> garrison sector.
        Assert.Equal(ownSector, transits[0]);
        Assert.True(transits.Count >= 3, $"expected 2 warp transits (route {string.Join(" -> ", transits)})");
        Assert.Equal(midSector, transits[1]);
        Assert.Equal(targetSector, transits[^1]);
        Assert.Equal(0, rejectsAfter - rejectsBefore);   // zero [ctrlak]-equivalent rejects
        Assert.Equal(0, clientResets);                    // zero SHIP_RESETs

        Console.WriteLine($"[p540b] LIVE-P540B: pod {victimId} ejected in {ownSector}, flew through {midSector} to garrison {targetSector} ({transits.Count - 1} real warp transits) and docked — {rejectsAfter - rejectsBefore} rejects, {clientResets} SHIP_RESETs — RESULT PASS");
    }

    private static DataStationIGC ServerGarrison(Srv server) =>
        server.Missions[0].Sectors.SelectMany(s => s.Stations).First(st => st.SideId == 0);
}
