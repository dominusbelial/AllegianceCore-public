using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
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
[Trait("Tier", "Live")]
public sealed class P699HumanPodHomeLiveTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);

    internal static Game1 CreateAutopilot(ConnectionSession session, OwnShipSimulator sim, string art, Game1.AutopilotTarget target)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_session", session); Set(game, "_ownSim", sim); Set(game, "_ownShipPrediction", true);
        Set(game, "_shopCatalog", ShopCatalog.TryLoad(art)!); Set(game, "_assets", new DirectoryAssetSource(art));
        Set(game, "_dockBaysByStationType", new Dictionary<short, HullLandingBayGeometry?>());
        Set(game, "_autoTarget", target);
        return game;
    }

    internal static ControlData AutopilotControls(Game1 game, float dt = .05f)
    {
        Set(game, "_autopilotDeltaSeconds", dt);
        var args = new object[] { ControlData.Neutral };
        Assert.True((bool)typeof(Game1).GetMethod("TryGetAutopilotControls", Private)!.Invoke(game, args)!);
        return (ControlData)args[0];
    }

    internal static int AutopilotMovement(Game1 game) =>
        (int)typeof(Game1).GetField("_autopilotMovementButtons", Private)!.GetValue(game)!;

    [SkippableTheory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [Trait("Requires", "Artwork")]
    public void Human_eject_autopilot_flies_two_jumps_and_docks_without_oxygen_respawn(double timeScale)
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        Ship ship = null!;
        Set(server, "ShipSpawner", (Func<StaticCore, Mission, short, (Ship, Sector)>)((core, m, id) =>
        {
            var start = m.Sectors.Single(s => s.Id == 1051);
            var mouth = start.Warps.Single(w => w.Destination!.Id == 1052).Warp;
            ship = new Ship(id, core.HullTypes[504], new Vector3(mouth.Position[0], mouth.Position[1], mouth.Position[2] + mouth.Radius + 350),
                Vector3.Zero, Orientation.Identity) { SideId = 0 };
            m.PlaceShip(ship, start); return (ship, start);
        }));
        server.Boot(new ServerConfig
        {
            ArtPath = art, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Games = [new() { Name = "P699 human", Map = "XXV_simple.igc", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P699-human");
        session.SendLogonRequest(new() { CharacterName = "P699-human", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", Time = 1, DwCookie = 699,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 69901 });
        void Drain(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 5)
            { lock (session.Gate) if (done()) return; server.TickSim(); Thread.Sleep(1); }
            Assert.Fail("client did not receive the expected wire transition");
        }
        Drain(() => session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var own) && own.Latest is not null);
        typeof(Srv).GetMethod("SetTimeScale", Private)!.Invoke(server, [timeScale]);
        Drain(() => Math.Abs(session.World.TimeScale - timeScale) < .001);
        ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        Drain(() => session.World.Ships[ship.Id].HullId == 444);
        double deadline = ship.LifepodTimeToDie!.Value, began = server.SimNow;
        var home = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == 0 && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        Assert.NotEqual(home.Data.ClusterId, ship.Sector!.Id);
        var sim = new OwnShipSimulator();
        sim.Seed(ship.Position, ship.Velocity, ship.Orientation, ship.TurnRates.ToArray());
        Game1.AutopilotTarget? target;
        lock (session.Gate)
        {
            var catalog = ShopCatalog.TryLoad(art)!;
            target = Game1.ChooseEjectRescueTarget(session.World, 0, ship.Position,
                id => catalog.StationTypes!.TryGetValue(id, out var type) ? type.SabmCapabilities : (short)0,
                _ => 0);
        }
        Assert.NotNull(target); Assert.Equal(home.StationId, target.Value.Id);
        var game = CreateAutopilot(session, sim, art, target.Value);
        var sectors = new List<short> { ship.Sector.Id };
        byte seededCookie;
        lock (session.Gate) seededCookie = session.World.Ships[ship.Id].Cookie;
        var wall = Stopwatch.StartNew();
        var sendCadence = new ShipUpdateCadence();
        while (ship.StationId is null && server.SimNow < deadline && wall.Elapsed.TotalSeconds < 100)
        {
            // The real human autopilot reads its own prediction and the wire world;
            // send native SHIP_UPDATE, never the MCP controls-only command.
            byte cookie;
            lock (session.Gate)
            {
                var own = session.World.Ships[ship.Id];
                // Use the same side-adjusted hull inputs as Game1's prediction.
                var ga = session.World.GlobalAttributesFor(own.SideId);
                sim.SetHull(ShipDeadReckoner.HullStats.FromHull(ship.HullType,
                    ga.Get(GlobalAttributeSet.Thrust), ga.Get(GlobalAttributeSet.MaxSpeed),
                    ga.Get(GlobalAttributeSet.TurnRate), ga.Get(GlobalAttributeSet.TurnTorque)), 0);
                if (own.SectorId != ship.Sector!.Id) { Thread.Sleep(1); continue; }
                // SHIP_STATUS may name the destination before SET_CLUSTER's
                // new cookie and pose arrive. Adopt that authoritative transition
                // together; sector-only reseeding can retain the old-sector pose.
                if (sectors[^1] != own.SectorId && own.Cookie == seededCookie)
                { Thread.Sleep(1); continue; }
                cookie = own.Cookie;
                // Like Game1.UpdateOwnShipSimulation, apply native corrections
                // before continuing prediction. This accelerated test uses a
                // synthetic clock; P540b separately checks the real ping clock
                // with zero rejects/resets throughout the same two-hop flight.
                if (own.HasPendingReset || cookie != seededCookie)
                {
                    sim.Seed(own.Latest!.Position, own.Latest.Velocity, own.Latest.Orientation, own.Latest.TurnRates);
                    session.World.ClearOwnShipResetFlag(ship.Id);
                    seededCookie = cookie;
                    if (sectors[^1] != own.SectorId) sectors.Add(own.SectorId);
                }
            }
            var controls = AutopilotControls(game);
            // Match Game1's update order: predict the frame, then publish it.
            sim.Step(.05f * (float)timeScale, controls, AutopilotMovement(game));
            uint stamp = (uint)(server.SimNow * 1000);
            if (sendCadence.Update(server.SimNow))
                session.SendShipPose(stamp, cookie, ClientShipUpdate.FromPose(stamp, sim.Position, sim.Velocity,
                    sim.Orientation, AutopilotMovement(game), controls, sim.TurnRates, sim.Power));
            Thread.Sleep(2); server.TickSim();
        }
        Drain(() => session.World.Ships[ship.Id].StationId >= 0);
        lock (session.Gate)
            Assert.DoesNotContain(session.World.Events.Kills, k => k.ShipId == ship.Id && k.BDeathCredit);
        var counters = (System.Collections.IDictionary)typeof(Srv).GetField("_peerShipUpdate", Private)!.GetValue(server)!;
        foreach (var item in counters.Values)
        {
            long accepted = (long)item!.GetType().GetField("Accepted")!.GetValue(item)!;
            long rejected = (long)item.GetType().GetField("Rejected")!.GetValue(item)!;
            output.WriteLine($"POSE accepted={accepted} rejected={rejected}");
            Assert.True(accepted > 100, "the server must accept native poses throughout the flight");
        }
        output.WriteLine($"HUMAN route={string.Join(" -> ", sectors)} elapsed={server.SimNow - began:F2} oxygen={deadline - began:F2} scale={timeScale:F2} station={ship.StationId}");
        Assert.Equal(home.StationId, ship.StationId);
        Assert.True(server.SimNow < deadline, "oxygen respawn is not successful flight");
        Assert.True(sectors.Count >= 3, "at least two physical warp transits required");
        Assert.Equal(home.Data.ClusterId, sectors[^1]);
    }
}
