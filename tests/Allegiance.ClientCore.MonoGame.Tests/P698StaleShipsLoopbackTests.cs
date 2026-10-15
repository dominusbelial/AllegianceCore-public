using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public class P698StaleShipsLoopbackTests
{
    private sealed class FixedRandom : ITrailRandom
    {
        public float Range(float min, float max) => (min + max) * .5f;
        public Vector3 Direction() => new(1, 0, 0);
    }
    private static void Pump(Srv server, Func<bool> done, string description, double seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed.TotalSeconds < seconds)
        {
            server.TickSim();
            Thread.Sleep(2);
        }
        Assert.True(done(), description);
    }

    private static ConnectionSession Connect(Srv server, string name, ulong steam, short side)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(new LogonRequest
        {
            CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
            MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamID = steam, DrmHash = "p698",
        });
        s.SendPositionReq(side);
        Pump(server, () => { lock (s.Gate) return s.World.MyShipId is not null; }, "peer seated");
        return s;
    }

    // This is Game1's hull/effect gather predicate, under the real receive gate.
    private static bool Draws(ConnectionSession s, short id)
    {
        lock (s.Gate)
        {
            var w = s.World;
            return w.MyShipId is { } me && w.Ships.TryGetValue(me, out var own)
                && w.Ships.TryGetValue(id, out var ship) && ship.CanDrawInSector(own.SectorId);
        }
    }

    [Theory]
    [InlineData("warp-and-die")]
    [InlineData("detection-loss")]
    [InlineData("teammate-pod-dock")]
    [Trait("Requires", "Artwork")]
    public void Departed_ship_leaves_hull_strobe_and_effect_gather_on_real_loopback(string scenario)
    {
        Require.Artwork();
        using var server = new Srv();
        Ship? subject = null;
        Sector? origin = null;
        ResolvedWarp? warp = null;
        Vector3 centre = default, direction = default, stationPos = default;
        int placed = 0;
        bool dock = scenario == "teammate-pod-dock";
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(server, (Func<StaticCore, Mission, short, (Ship, Sector)>)((core, mission, id) =>
        {
            short ownSide = mission.Sides[0].Id;
            if (origin is null)
            {
                if (dock)
                {
                    var station = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId == ownSide);
                    origin = mission.Sectors.First(s => s.Id == station.ClusterId);
                    stationPos = new(station.Position[0], station.Position[1], station.Position[2]);
                    centre = stationPos + new Vector3(0, 0, 1500);
                }
                else
                {
                    origin = mission.Sectors[0];
                    warp = origin.Warps.First(w => w.Destination is not null && w.Partner() is not null);
                    var pos = warp.Warp.Position;
                    centre = new(pos[0], pos[1], pos[2]);
                    var f = warp.Warp.Forward;
                    direction = new Vector3(f[0], f[1], f[2]).Normalize();
                    if (scenario == "detection-loss") centre = new(6000, 0, 0);
                }
            }
            bool viewer = placed++ == 0;
            var hull = dock && !viewer
                ? core.HullTypes[mission.Sides[0].Civilization.Lifepod]
                : core.HullTypes.Values.First(h => h.ScannerRange > 1000 && h.Length < 100);
            var spawn = dock ? centre + new Vector3(viewer ? 500 : 0, 0, 0)
                : scenario == "warp-and-die"
                    ? centre + direction * (viewer ? 2000 : 3 * (warp!.Warp.Radius + hull.Length / 2))
                    : centre + new Vector3(viewer ? 0 : 200, 0, 0);
            var ship = new Ship(id, hull, spawn, Vector3.Zero, Orientation.Identity)
            { SideId = viewer || dock ? ownSide : mission.Sides[1].Id };
            mission.PlaceShip(ship, origin);
            if (!viewer) subject = ship;
            return (ship, origin);
        }));
        server.Boot(new Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
            Port = 0, UpdatesPerSecond = 20,
            Games = [new Allegiance.Server.GameConfig { Name = "P698", Map = "XXV_simple.igc",
                Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        using var viewer = Connect(server, "P698-viewer", 69800000000000001, 0);
        using var remote = Connect(server, "P698-subject", 69800000000000002, dock ? (short)0 : (short)1);
        server.Missions[0].Stage = MissionStage.Started;
        short id = subject!.Id;
        Pump(server, () => Draws(viewer, id) && (dock || subject.SeenBySide(server.Missions[0].Sides[0].Id)), "viewer has a detected live in-sector pose");
        Assert.NotEmpty(NavStrobeLayout.ShipAnchors(subject.Position, subject.Orientation, subject.HullType.Length / 2));
        var particles = new FlightEffectPools();
        particles.BeginView(viewer.World.SectorGeneration, origin!.Id);
        particles.Damage.Emit(subject.Position, new Vector3(10, 0, 0), subject.Orientation,
            20, id, 3, .016f, new FixedRandom());
        particles.Engines.Emit(new EngineTrailShip(id, subject.Position, new Vector3(10, 0, 0),
            subject.Orientation, 20, 1), .016f, new FixedRandom());
        Assert.NotEmpty(particles.Damage.Enumerate());
        Assert.NotEmpty(particles.Engines.Enumerate());
        if (scenario == "warp-and-die")
        {
            subject.Position = centre + direction * (.9f * (warp!.Warp.Radius + subject.HullType.Length / 2));
            subject.Velocity = -direction * 20;
            Pump(server, () => subject.Sector != origin, "subject really warped");
            subject.Mission!.Site.KillShipEvent(0, subject, null, -1, subject.Position, Vector3.Zero);
        }
        else if (dock)
            subject.Position = DockTestPlacement.Inside(subject.Mission!,
                origin!.Stations.First(s => s.SideId == subject.SideId), subject.HullType);
        else
            subject.Position = new(-6000, 0, 0);

        // Native retirement is an event, not a pose-age timeout. Allow one
        // 10-pass visibility sweep (0.5 s at 20 Hz) plus local scheduling slack.
        Pump(server, () => !Draws(viewer, id), "departed ship excluded from actual gather gate", 2);
        particles.Advance(DamageDebrisParticles.DurationSeconds);
        Assert.Empty(particles.Damage.Enumerate());
        particles.Advance(2); // independent fire/smoke expires at its native duration
        Assert.Empty(particles.Engines.Enumerate());
        if (dock) Assert.NotNull(subject.StationId);
        if (scenario == "detection-loss")
        {
            Assert.False(subject.SeenBySide(server.Missions[0].Sides[0].Id));
            lock (viewer.Gate) Assert.True(viewer.World.Ships[id].Unknown);
        }
        // Refreshes must not revive the departed pose or its attached strobes.
        var until = Stopwatch.StartNew();
        while (until.Elapsed.TotalSeconds < .75)
        {
            server.TickSim();
            Thread.Sleep(2);
            Assert.False(Draws(viewer, id));
        }
        lock (viewer.Gate)
        {
            var w = viewer.World;
            short sector = w.Ships[w.MyShipId!.Value].SectorId;
            var gathered = w.Ships.Values.Where(s => s.ShipId != w.MyShipId && s.CanDrawInSector(sector)).ToList();
            Assert.DoesNotContain(gathered, s => s.ShipId == id);
            Assert.Empty(gathered.Where(s => s.ShipId == id).SelectMany(s => NavStrobeLayout.ShipAnchors(s.Latest!.Position, s.Latest.Orientation, 20)));
        }
    }
}
