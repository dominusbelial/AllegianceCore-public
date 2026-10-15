using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
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
/// The "and bots" half: an in-process server + a real <see cref="Bot"/> over
/// the loopback TCP transport proving a client can be assigned a ship, command
/// it, and observe it fly. The centerpiece is a trajectory match: the server's
/// ship positions (the exact raw floats put on the wire by
/// <c>SINGLE_SHIP_UPDATE</c>) equal an <b>independent</b>
/// <see cref="Ship.ExecuteMove"/> rollout computed by the test from the same
/// hull, spawn, command and cadence — proving the server is simulating, not
/// echoing the client's (zero-valued) position predictions back.
/// </summary>
[Collection("LiveNetwork")]
public class BotFlightTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // A hull with sane flight numbers so the test's independent reference is
    // straightforward (same one GameMessageRoundTripTests uses).
    private static readonly Lazy<DataHullTypeIGC> DemoHull = new(() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 535f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.666f, 4.444f, 5.022f],
        TurnTorques: [5100f, 4900f, 4700f],
        Thrust: 52800f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 950f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 507, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 515f, HardpointOffset: 560,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []));

    private static readonly Vector3 Spawn = new(500f, 610f, 815f);

    private static Cfg Config(int ups = 20, int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm
            {
                Name = "Test Mission", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static LogonRequest SampleRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 888u, DwCookie = 0xFACEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 71000000000000000ul, DrmHash = "botflt",
    };

    /// <summary>Wire a deterministic spawn (DemoHull at <see cref="Spawn"/>, straight
    /// ahead) into the server so the test's independent reference shares the hull + spawn.</summary>
    private static Func<Ship?> InjectKnownSpawn(Srv server, out Vector3 spawnPos)
    {
        spawnPos = Spawn;
        Ship? placed = null;
        server.ShipSpawner = (core, mission, id) =>
        {
            var ship = new Ship(id, DemoHull.Value, Spawn, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, mission.Sectors[0]);
            placed = ship;
            return (ship, mission.Sectors[0]);
        };
        return () => placed;   // null until the server places it (during a drained logon)
    }

    /// <summary>Forward thrust + nothing else: identity-nose ⇒ −z cruise command.</summary>
    private static readonly ControlData FullForward = new(0f, 0f, 0f, 1f);

    /// <summary>Drive in-process ticks until a condition holds (the ship appears),
    /// giving the async bot a beat to connect/logon/command. The physics only
    /// depends on tick index, not on real pacing; pacing just lets the socket
    /// keep up. Returns how many SimPasses ran.</summary>
    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static bool Near(Vector3 a, Vector3 b, float tol) => (a - b).Length() <= tol;

    /// <summary>
    /// ⭐ The centrepiece: the server's authoritative ship trajectory equals an
    /// independent <c>Ship.ExecuteMove</c> rollout, and the bot observed exactly
    /// those positions on the wire. Deterministic — the reference is seeded from
    /// the same hull/spawn/command/cadence and rolled forward itself, never from
    /// the server's output, so a passing match proves the server is simulating.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bot_trajectory_matches_independent_ExecuteMove_rollout()
    {
        Require.Artwork();
        using var server = new Srv();
        var placedGetter = InjectKnownSpawn(server, out _);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        double p = 1.0 / server.Config.UpdatesPerSecond;
        Assert.True(p > 0);

        var bot = new FltBot();
        try
        {
            bot.Connect(server.BoundPort, "TRAJ");
            bot.SendLogonRequest(SampleRequest("TRAJ"));
            var bootTicks = PumpUntil(server, () => placedGetter() is not null, 3000);
            var ship = placedGetter() ?? throw new InvalidOperationException("ship not placed");
            Assert.True(bootTicks < 3000, "ship was never assigned within the bootstrap window");

            // The independent reference: a separate Ship, same hull + spawn,
            // rolled forward itself with the same per-tick dt and controls.
            var refShip = new Ship((short)32000, DemoHull.Value, Spawn, Vector3.Zero, Orientation.Identity);
            refShip.Controls = ControlData.Neutral;
            double prevNow = server.SimNow;      // ship's model clock enters the measured phase here

            bot.CommandControls(FullForward, 0);  // the only thing over the wire: command forward

            const int N = 180;                    // ~9 s of sim, paced so the bot keeps up
            var expected = new List<(int Tick, Vector3 Pos)>();
            var actual = new List<(int Tick, Vector3 Pos)>();
            int controlOnsetIdx = -1;             // measured-loop index where thrust first applied
            for (int i = 0; i < N; i++)
            {
                server.TickSim();
                double now = server.SimNow;
                int tick = (int)server.LoopIterations;
                // Deferred control sync: refShip uses the controls the server
                // applied THIS tick (read right after the tick). Constant ⇒ exact.
                refShip.Controls = ship.Controls.Throttle >= 0.999f ? FullForward : ControlData.Neutral;
                if (controlOnsetIdx < 0 && ship.Controls.Throttle >= 0.999f) controlOnsetIdx = i;
                refShip.ExecuteMove(prevNow, now);
                refShip.Position += refShip.Velocity * (float)(now - prevNow);
                prevNow = now;
                expected.Add((tick, refShip.Position));
                actual.Add((tick, ship.Position));
                Thread.Sleep(1);
            }

            Assert.True(controlOnsetIdx >= 0, "the server must have applied the bot's control input");
            Assert.True(N - controlOnsetIdx > 60, "most of the run must be under thrust");

            const float tol = 1e-3f;   // float-identical ops; tolerance is generous headroom
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.True(Near(expected[i].Pos, actual[i].Pos, tol),
                    $"tick {expected[i].Tick}: independent rollout {expected[i].Pos} ≠ server {actual[i].Pos} " +
                    $"(Δ {(actual[i].Pos - expected[i].Pos).Length():R}) — the server is not reproducing ExecuteMove");
            }

            // commanded direction: identity nose is −z, so forward thrust dominates -z displacement
            var disp = ship.Position - Spawn;
            Assert.True(disp.Length() > 5f, "ship must have flown a measurable distance");
            Assert.True(disp.Z < -5f, $"commanded forward (−z) must dominate; got displacement Z={disp.Z}");

            // wire fidelity: the bot's observed SINGLE_SHIP_UPDATE positions match
            // the same reference at their tick stamps.
            Thread.Sleep(400);  // let the bot drain the tail of broadcasts
            var obs = bot.Observations;
            Assert.True(obs.Count >= N / 5, $"bot observed only {obs.Count} positions; expected a substantial feed");
            foreach (var o in obs)
            {
                var e = expected.Find(e => e.Tick == (int)o.Tick);
                if (e == default) continue;   // a bootstrap/settling frame with no reference slot
                Assert.True(Near(e.Pos, o.Position, tol),
                    $"wire position at tick {o.Tick} {o.Position} ≠ independent rollout {e.Pos}");
            }
        }
        finally
        {
            bot.Shutdown();
            server.Shutdown();
        }
    }

    /// <summary>Zero thrust ⇒ no movement, and (P332) exactly ONE initial
    /// SINGLE_SHIP_UPDATE placing the stationary ship, then silence. This is
    /// the corrected contract: a ship that never moves must still have its
    /// position broadcast at least once to each viewer (otherwise it stays
    /// invisible forever), but the dirty-check must NOT then spam re-sends for
    /// a genuinely unchanged position — so exactly one observation, no more.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Zero_thrust_leaves_the_ship_at_rest_and_quiet()
    {
        Require.Artwork();
        using var server = new Srv();
        var placedGetter = InjectKnownSpawn(server, out _);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bot = new FltBot();
        try
        {
            bot.Connect(server.BoundPort, "DRIFT");
            bot.SendLogonRequest(SampleRequest("DRIFT"));
            PumpUntil(server, () => placedGetter() is not null, 3000);
            var ship = placedGetter() ?? throw new InvalidOperationException("ship not placed");

            bot.CommandControls(ControlData.Neutral, 0);   // explicitly nothing
            for (int i = 0; i < 120; i++) { server.TickSim(); Thread.Sleep(1); }

            Assert.True(Near(ship.Position, Spawn, 1e-3f), $"zero thrust must not move the ship; got {ship.Position}");
            Assert.True(Near(ship.Velocity, Vector3.Zero, 1e-4f), "velocity must stay zero");

            Thread.Sleep(150);
            // P332: exactly ONE forced placement observation (the at-least-once
            // guarantee), parked at spawn with zero velocity — then the ship
            // settles into silence (no dirty terms) and stays there.
            var obs = Assert.Single(bot.Observations);
            Assert.True(Near(obs.Position, Spawn, 1e-3f), $"first broadcast must place the ship at spawn; got {obs.Position}");
            Assert.True(Near(obs.Velocity, Vector3.Zero, 1e-4f), "first broadcast of a resting ship must carry zero velocity");
        }
        finally
        {
            bot.Shutdown();
            server.Shutdown();
        }
    }

    /// <summary>Two bots get different ships and move independently: different
    /// ids, different spawns, and (A thrusting, B idle) they diverge cleanly.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Two_bots_get_distinct_ships_and_fly_independently()
    {
        Require.Artwork();
        using var server = new Srv();     // default spawner: staggered spawns, first hull
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var a = new FltBot(); using var b = new FltBot();
        a.Connect(server.BoundPort, "ALPHA"); b.Connect(server.BoundPort, "BRAVO");
        a.SendLogonRequest(SampleRequest("ALPHA")); b.SendLogonRequest(SampleRequest("BRAVO"));

        var shipA = PumpForShip(server, a);
        var shipB = PumpForShip(server, b);
        Assert.NotEqual(shipA.Ship.Id, shipB.Ship.Id);
        Assert.False(Near(shipA.Ship.Position, shipB.Ship.Position, 0.001f),
            "distinct spawns for distinct ships");

        a.SendDocked();
        Assert.True(PumpUntil(server, () => shipA.Ship.StationId is null, 3000) < 3000);
        shipA.Ship.Velocity = Vector3.Zero; // isolate commanded thrust from the launch impulse
        Vector3 aStart = shipA.Ship.Position, bStart = shipB.Ship.Position;
        a.CommandControls(FullForward, 0);
        b.CommandControls(ControlData.Neutral, 0);

        for (int i = 0; i < 150; i++) { server.TickSim(); Thread.Sleep(1); }

        // A flew forward; B idled at its own spawn.
        Assert.True((shipA.Ship.Position - aStart).Length() > 5f, "bot A must have flown");
        Assert.True(shipA.Ship.Position.Z < aStart.Z - 5f, "A's commanded −z displacement must dominate");
        Assert.True(Near(shipB.Ship.Position, bStart, 1e-3f), "bot B (idle) must not move");
        Assert.False(Near(shipA.Ship.Position, shipB.Ship.Position, 1f),
            "the two ships must be well separated at the end");

        // Distinct, independently-driven object ids (not the same object twice).
        Assert.NotSame(shipA.Ship, shipB.Ship);
    }

    /// <summary>A bot disconnect leaves the server ticking and serving new ships.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bot_disconnect_leaves_the_server_ticking()
    {
        Require.Artwork();
        using var server = new Srv();
        var placedGetter = InjectKnownSpawn(server, out _);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bot = new FltBot();
        try
        {
            bot.Connect(server.BoundPort, "BYE");
            bot.SendLogonRequest(SampleRequest("BYE"));
            PumpUntil(server, () => placedGetter() is not null, 3000);
            var ship = placedGetter() ?? throw new InvalidOperationException("ship not placed");
            long before = server.LoopIterations;

            bot.CommandControls(FullForward, 0);
            bot.Shutdown();                    // drop mid-flight
            for (int i = 0; i < 60; i++) { server.TickSim(); Thread.Sleep(1); }   // still ticks

            Assert.True(server.LoopIterations > before + 40, "server must keep ticking after the bot left");
            // P128: the abandoned ship is retired on disconnect — released from the
            // broadcast set AND removed from its sector, so no zombie keeps flying
            // under the tick (this supersedes the old "stays and keeps moving" note).
            Assert.False(server.Ships.Any(s => s.Ship == ship), "departed bot's ship must be released from the broadcast set");
            Assert.False(
                server.Missions[0].Sectors.SelectMany(s => s.Models).OfType<Ship>().Any(s => s.Id == ship.Id),
                "departed bot's ship must be removed from the sector (P128)");

            // and a fresh bot still gets served — a distinct placed ship object.
            using var fresh = new FltBot();
            fresh.Connect(server.BoundPort, "FRESH");
            fresh.SendLogonRequest(SampleRequest("FRESH"));
            PumpUntil(server, () => fresh.ShipAssignment is not null, 3000);
            Assert.NotNull(fresh.ShipAssignment);
            Assert.NotSame(fresh.ShipAssignment!.Ship, ship);
        }
        finally
        {
            bot.Shutdown();
            server.Shutdown();
        }
    }

    /// <summary>Three real peers flying, one dropped abruptly mid-tick while a
    /// broadcast is in flight: the send-race must not kill the server (the fix),
    /// and the other two must keep getting their broadcasts the same window.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Abrupt_disconnect_during_broadcast_leaves_server_alive_and_others_served()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bots = new List<FltBot>();
        try
        {
            // three real loopback peers, all assigned moving ships
            for (int i = 0; i < 3; i++)
            {
                var b = new FltBot();
                bots.Add(b);
                b.Connect(server.BoundPort, $"DROP{i}");
                b.SendLogonRequest(SampleRequest($"DROP{i}"));
            }
            int waited;
            for (waited = 0; waited < 3000 && bots.Any(b => b.ShipAssignment is null); waited++)
            { server.TickSim(); Thread.Sleep(1); }
            Assert.True(waited < 3000, "all three peers must be assigned ships");

            // all flying → every ship is dirty → BroadcastShipStates targets all
            // of _peerShips on every tick. Establish a flowing feed first.
            foreach (var b in bots) b.SendDocked();
            Assert.True(PumpUntil(server, () => server.Ships.All(l => l.Ship.StationId is null), 3000) < 3000);
            foreach (var b in bots) b.CommandControls(FullForward, 0);
            PumpUntil(server, () => bots.All(b => b.Observations.Count > 0), 1500);
            bots[1].ResetObservations(); bots[2].ResetObservations();
            long before = server.LoopIterations;

            // Abruptly kill peer 0's connection: the server read loop hits EOF,
            // reaps _peers and raises PeerLeftEvt. Crucially we do NOT settle —
            // we hammer the sim so a broadcast fires inside the window where peer 0
            // is still in _peerShips (broadcast set) but already gone from _peers,
            // which used to throw and kill the whole server.
            bots[0].Shutdown();
            for (int i = 0; i < 900; i++) { server.TickSim(); Thread.Sleep(0); }  // no exception may escape

            Assert.True(server.LoopIterations > before + 880,
                "server must keep ticking after the abrupt mid-broadcast drop");
            Assert.True(bots[1].Observations.Count > 0 && bots[2].Observations.Count > 0,
                "the other, still-connected peers must keep receiving broadcasts after the drop");
        }
        finally
        {
            foreach (var b in bots) b.Shutdown();
            server.Shutdown();
        }
    }

    private static Srv.LiveShip PumpForShip(Srv server, FltBot bot)
    {
        PumpUntil(server, () => bot.ShipAssignment is not null, 3000);
        var set = bot.ShipAssignment ?? throw new InvalidOperationException("no SET_CLUSTER");
        return server.Ships.Single(s => s.Ship.Id == set.Ship.ShipId);
    }
}
