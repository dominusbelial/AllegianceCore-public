using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P695HumanShipUpdatesLiveTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static object Counters(Srv server)
        => ((IDictionary)typeof(Srv).GetField("_peerShipUpdate", Private)!.GetValue(server)!).Values.Cast<object>().Single();
    private static long Count(object counters, string name)
        => (long)counters.GetType().GetField(name)!.GetValue(counters)!;

    // No graphics device is needed: run the actual Game1 clock pump, local flight
    // step and native send method, with the real session, clock, simulator and
    // cadence. Avoid the bot controls-only path and any test-side wire timestamp.
    private static Game1 HeadlessFlight(ConnectionSession session, OwnShipSimulator sim, OwnShipServerClock clock)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        // No Game/graphics constructor ran, so its disposal finalizer cannot run.
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_ownSim", sim);
        Set(game, "_ownServerClock", clock);
        Set(game, "_shipSendCadence", new ShipUpdateCadence());
        Set(game, "_ownShipSendArmed", true);
        Set(game, "_ownClockConnectionGeneration", -1L);
        return game;
    }

    // Keep the ten-minute duration in the real suite too. A short straight-line
    // flight never reached the stale-anchor failure seen after minutes of play.
    [SkippableFact]
    public void Human_flies_for_ten_minutes_with_turns_boost_tidi_and_recovers_from_correction()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var catalog = ShopCatalog.TryLoad(art)!;
        var booster = catalog.Parts.First(p => p.Type is DataPartTypeIGC.Afterburner && p.GroupId >= 0);
        Ship? player = null;
        Set(server, "ShipSpawner", (Func<StaticCore, Mission, short, (Ship, Sector)>)((core, mission, id) =>
        {
            var hull = core.HullTypes[15]; // the human's real hull in clash54
            var sector = mission.Sectors[0];
            var orientation = new Orientation();
            orientation.Set(new Vector3(0, -1, 0), new Vector3(0, 0, 1));
            player = new Ship(id, hull, new Vector3(500, 620, 10), Vector3.Zero, orientation) { SideId = 0 };
            mission.PlaceShip(player, sector);
            Assert.NotNull(player.MountPart(booster with { PartMask = 0 }, 1, 0, 255));
            return (player, sector);
        }));
        server.Boot(new ServerConfig
        {
            Port = 0, UpdatesPerSecond = 20, ArtPath = art, StaticCore = "PCore001.igc",
            LiveSettings = new() { TimeDilation = "off" },
            Games = [new() { Name = "P695b-soak", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        server.Missions[0].Stage = MissionStage.Started;
        var attributes = Enumerable.Repeat(1f, GlobalAttributeSet.Count).ToArray();
        attributes[GlobalAttributeSet.Thrust] = 2;
        server.Missions[0].Sides[0].GlobalAttributes = new GlobalAttributeSet(attributes);
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P695b-human");
        session.SendLogonRequest(new()
        {
            CharacterName = "P695b-human", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            SteamAuthTicket = new byte[16], CrcFileList = -1,
        });
        var sim = new OwnShipSimulator();
        var game = HeadlessFlight(session, sim, new OwnShipServerClock());
        Set(game, "_shopCatalog", catalog);
        Set(game, "_flashTracker", new DamageFlashTracker());
        Set(game, "_ownSimLastSectorId", (short)-1);
        var startup = Stopwatch.StartNew();
        bool seated = false;
        while (startup.Elapsed.TotalSeconds < 5)
        {
            Call(game, "PumpServerClock");
            server.Tick();
            lock (session.Gate)
                seated = player is not null && session.World.MyShipId is { } id
                    && session.World.Ships.TryGetValue(id, out var own) && own.Latest is not null;
            if (seated) break;
            if (session.Ack is not null && player is null) session.SendPositionReq(-1);
        }
        Assert.True(seated);
        var ship = player!;
        var peers = (IDictionary)typeof(Srv).GetField("_peerShipUpdate", Private)!.GetValue(server)!;
        var peer = peers.Keys.Cast<object>().Single();
        // Deliver the fixture's mounted booster through the real owner loadout
        // wire path; Game1 resolves its physics and reset power/fuel normally.
        typeof(Srv).GetMethod("SendPeer", Private, [peer.GetType(), typeof(byte[])])!.Invoke(server,
            [peer, FedPacket.Pack([new BuyLoadoutAck
        {
            HullId = ship.HullType.HullId, Parts = [new(booster.PartId, 1, 0, 255)],
        }.Encode()])]);
        bool injected = false;
        long lastAccepted = 0, lastRejected = 0;
        int stagnantFrames = 0, boostedFrames = 0, resets = 0;
        double wall = 0;
        double previousScale = 1;
        var flight = Stopwatch.StartNew();
        while (flight.Elapsed.TotalSeconds < 605)
        {
            int phase = (int)(wall / 200);
            double scale = phase == 0 ? 1 : phase == 1 ? .85 : .5;
            if (scale != previousScale) { Call(server, "SetTimeScale", scale); previousScale = scale; }
            Call(game, "PumpServerClock");
            lock (session.Gate)
            {
                var own = session.World.Ships[session.World.MyShipId!.Value];
                if (own.HasPendingReset) resets++;
                Call(game, "UpdateOwnShipSimulation", session.World, own, own.Latest!);
            }
            if (!injected && wall >= 30)
            {
                sim.Seed(sim.Position + new Vector3(500, 0, 0), sim.Velocity, sim.Orientation, sim.TurnRates);
                injected = true;
            }
            // Continuous curved flight stays away from stations/rocks/warps;
            // throttle changes and periodic boost exercise real fuel exhaustion.
            var controls = new ControlData(.6f, 0, 0, wall % 40 < 30 ? 1 : -.8f);
            int state = wall % 30 < 5 ? ShipControlState.AfterburnerButton : 0;
            Call(game, "StepOwnShipSimulation", .05, controls, state);
            if (sim.Power > .1) boostedFrames++;
            wall += .05;
            Set(game, "_nowSeconds", wall);
            Call(game, "MaybeSendOwnShipUpdate");
            server.Tick();
            var counters = Counters(server);
            long accepted = Count(counters, "Accepted"), rejected = Count(counters, "Rejected");
            stagnantFrames = accepted == lastAccepted ? stagnantFrames + 1 : 0;
            Assert.True(stagnantFrames < 100, $"acceptance stalled at {wall:F1}s: accepted={accepted} rejected={rejected}");
            Assert.True(rejected < 20, $"runaway corrections at {wall:F1}s: accepted={accepted} rejected={rejected}");
            if ((int)(wall * 20) % 1200 == 0)
                output.WriteLine($"wall={wall:F1}s sim={server.SimNow:F1}s scale={scale} accepted={accepted} rejected={rejected} resets={resets}");
            lastAccepted = accepted;
            lastRejected = rejected;
        }
        output.WriteLine($"P695b soak: real-wall={flight.Elapsed.TotalSeconds:F1}s flight-wall={wall:F1}s accepted={lastAccepted} rejected={lastRejected} resets={resets} boost-frames={boostedFrames}");
        Assert.True(injected && resets > 0, "the real Game1 correction path must be exercised");
        Assert.True(boostedFrames > 20, "the fitted afterburner must actually fire");
        Assert.True(lastAccepted > 3000 && lastAccepted > lastRejected * 100);
        Assert.Equal(0, Count(Counters(server), "OutOfDate"));
        Assert.Equal(0, Count(Counters(server), "Invalid"));
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableTheory]
    [InlineData(0, 1.0)]
    [InlineData(1, 0.85)]
    [InlineData(2, 0.5)]
    public void Human_joins_moves_and_warps_after_automatic_rotations(int rotations, double scale)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            Ship? ship = null;
            Vector3 spawn = default;
            Set(server, "ShipSpawner", (Func<StaticCore, Mission, short, (Ship, Sector)>)((core, mission, id) =>
            {
                var sector = mission.Sectors.First(s => s.Warps.Count > 0);
                var warp = sector.Warps.First();
                var pos = warp.Warp.Position!;
                var hull = core.HullTypes[504];
                spawn = new Vector3(pos[0], pos[1], pos[2] + hull.Speed * 8);
                ship = new Ship(id, hull, spawn, Vector3.Zero, Orientation.Identity) { SideId = 0 };
                mission.PlaceShip(ship, sector);
                return (ship, sector);
            }));
            server.Boot(new ServerConfig
            {
                Port = 0, UpdatesPerSecond = 20,
                ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
                LiveSettings = new()
                {
                    TimeDilation = "off", TimeScale = scale,
                    AutoNextMatch = new() { Enabled = true, IntermissionSeconds = 0.1 },
                },
                Games = [new() { Name = "P695", Map = "XXV_simple.igc", Teams = 2,
                    CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            // A third-match join must use the process simulation epoch, not a
            // fresh mission's age or a carried-over client clock from another server.
            if (rotations > 0)
            {
                Set(server, "_simNow", 2636.0);
                server.Missions[0].Update(server.SimNow);
            }
            server.Missions[0].Stage = MissionStage.Started;
            for (int r = 0; r < rotations; r++)
            {
                var old = server.Missions[0];
                Call(server, "DeclareGameOver", old, (short)0, "P695 conquest fixture");
                Assert.NotSame(old, server.Missions[0]);
                var deadline = Stopwatch.StartNew();
                while (server.Missions[0].Stage != MissionStage.Started && deadline.Elapsed.TotalSeconds < 10)
                    server.TickSim();
                Assert.Equal(MissionStage.Started, server.Missions[0].Stage);
                Assert.Equal(server.SimNow, server.Missions[0].LastUpdate);
            }

            using var session = new ConnectionSession();
            session.Connect(server.BoundPort, "P695-human");
            session.SendLogonRequest(new()
            {
                CharacterName = "P695-human", FedsrvVer = LogonRequest.MsgVer,
                ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
                Time = 1, DwCookie = 695, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 69501,
            });
            var sim = new OwnShipSimulator();
            var clock = new OwnShipServerClock();
            var game = HeadlessFlight(session, sim, clock);
            var startup = Stopwatch.StartNew();
            bool seated = false;
            while (startup.Elapsed.TotalSeconds < 2)
            {
                Call(game, "PumpServerClock");
                server.Tick();
                lock (session.Gate)
                    seated = ship is not null && session.World.MyShipId is { } id
                        && session.World.Ships.TryGetValue(id, out var own) && own.Latest is not null;
                if (seated && clock.Ready) break;
                if (session.Ack is not null && ship is null) session.SendPositionReq(-1);
            }
            Assert.True(seated && clock.Ready, "real game clock must bootstrap before flight, without waiting six seconds");
            var player = ship!;
            var source = player.Sector!;
            sim.SetHull(ShipDeadReckoner.HullStats.FromHull(player.HullType), 0);
            sim.Seed(player.Position, player.Velocity, player.Orientation, new float[3]);
            double wall = 0;
            var flight = Stopwatch.StartNew();
            while (flight.Elapsed.TotalSeconds < 40 && ReferenceEquals(player.Sector, source))
            {
                Call(game, "PumpServerClock");
                lock (session.Gate)
                {
                    var own = session.World.Ships[session.World.MyShipId!.Value];
                    Set(game, "_ownShipCookie", own.Cookie);
                    Assert.False(own.HasPendingReset, "normal human flight must not need correction");
                }
                Call(game, "StepOwnShipSimulation", 0.05, new ControlData(0, 0, 0, 1), 0);
                wall += 0.05;
                Set(game, "_nowSeconds", wall);
                Call(game, "MaybeSendOwnShipUpdate");
                server.Tick();
            }
            var counters = Counters(server);
            string stats = $"rotations={rotations} scale={scale} accepted={Count(counters, "Accepted")} rejected={Count(counters, "Rejected")} out-of-date={Count(counters, "OutOfDate")} invalid={Count(counters, "Invalid")}";
            output.WriteLine(stats);
            Assert.True(Count(counters, "Accepted") > 20, stats);
            Assert.Equal(0, Count(counters, "OutOfDate"));
            Assert.Equal(0, Count(counters, "Rejected"));
            Assert.Equal(0, Count(counters, "Invalid"));
            Assert.True((player.Position - spawn).Length() > 100, "server ship must actually move");
            Assert.NotSame(source, player.Sector);
            Assert.True(session.KeepAlivePingsSent > 0, "exercise the actual six-second session ping cadence");
            // Drain the real warp SET_CLUSTER before checking the client's sector.
            var drain = Stopwatch.StartNew();
            while (drain.Elapsed.TotalSeconds < 2)
            {
                lock (session.Gate)
                    if (session.World.Ships[session.World.MyShipId!.Value].SectorId == player.Sector!.Id) break;
                Thread.Sleep(5);
            }
            lock (session.Gate)
                Assert.Equal(player.Sector!.Id, session.World.Ships[session.World.MyShipId!.Value].SectorId);

            // Regression for the production summary, including a hostile future-dated
            // packet. The client never supplies the counters or summary values.
            byte cookie;
            lock (session.Gate) cookie = session.World.Ships[session.World.MyShipId!.Value].Cookie;
            uint stale = (uint)((server.SimNow + 10) * 1000);
            session.SendShipPose(stale, cookie, ClientShipUpdate.FromPose(stale, player.Position,
                player.Velocity, player.Orientation, 0, ControlData.Neutral, new float[3], 0));
            var staleWait = Stopwatch.StartNew();
            while (Count(counters, "OutOfDate") == 0 && staleWait.Elapsed.TotalSeconds < 2) server.Tick();
            Assert.Equal(1, Count(counters, "OutOfDate"));
            counters.GetType().GetField("LastSummaryAt")!.SetValue(counters, server.SimNow - 10);
            server.Tick();
            var summary = Regex.Matches(log.ToString(), @"ship-update summary:.*accepted=(\d+) rejected=(\d+) out-of-date=(\d+) invalid=(\d+)")[^1];
            Assert.Equal(Count(counters, "Accepted").ToString(), summary.Groups[1].Value);
            Assert.Equal("0", summary.Groups[2].Value);
            Assert.Equal("1", summary.Groups[3].Value);
            Assert.Equal("0", summary.Groups[4].Value);
            output.WriteLine(summary.Value);
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableFact]
    public void Reconnecting_to_a_younger_server_replaces_the_real_game_clock_epoch()
    {
        Require.Artwork();
        static Srv Boot(double age)
        {
            var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(new ServerConfig
            {
                Port = 0,
                ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
                LiveSettings = new() { TimeDilation = "off" },
                Games = [new() { Name = "P695-reconnect", Map = "XXV_simple.igc", Teams = 2,
                    CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            Set(server, "_simNow", age);
            server.Missions[0].Update(age);
            return server;
        }
        using var oldServer = Boot(5000);
        using var newServer = Boot(0);
        using var session = new ConnectionSession();
        var clock = new OwnShipServerClock();
        var game = HeadlessFlight(session, new OwnShipSimulator(), clock);
        void Calibrate(Srv server)
        {
            var wait = Stopwatch.StartNew();
            while (!clock.Ready && wait.Elapsed.TotalSeconds < 2)
            {
                Call(game, "PumpServerClock");
                server.Tick();
            }
            Assert.True(clock.Ready);
        }
        session.Connect(oldServer.BoundPort, "old-epoch");
        Calibrate(oldServer);
        Assert.InRange(clock.ServerTimeMs(Environment.TickCount64), 5_000_000u, 5_001_000u);
        long generation = session.ConnectionGeneration;
        session.Connect(newServer.BoundPort, "new-epoch");
        Assert.Equal(generation + 1, session.ConnectionGeneration);
        Assert.Empty(session.TakePingEchoes());
        Call(game, "PumpServerClock");
        Assert.False(clock.Ready, "a reused Game1 must not stamp the previous server's epoch");
        Calibrate(newServer);
        Assert.InRange(clock.ServerTimeMs(Environment.TickCount64), 0u, 1000u);
    }
}
