using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P769/SIM-0109 — the constructor build path after the collision gate:
/// <list type="bullet">
/// <item>the building effect is created ONLY by a DRILLING builder colliding
/// with its rock (native <c>shipIGC.cpp:871-893</c>, drill-masked at :874) —
/// proven by the drilling <c>HullHitEvent</c> riding the creation tick;</item>
/// <item>the drilling latch is reached through the native axis approach
/// (<c>shipIGC.cpp:4014-4016</c> c_oEnter → DoApproach, common.cpp:1385-1429)
/// — a constructor flies from its station to a rock and the station gets
/// built;</item>
/// <item>the stopped-sphere substitute (Ship.TryStartBuilderBuild) is reduced
/// to the documented P652 PARKED-RESERVATION case: a constructor parked beside
/// its rock — spawned at contact + 20, OFF the drill axis, without thrust —
/// can never latch drilling, so the fallback builds it (and the fallback only:
/// no collision, no debris, no latch).</item>
/// </list>
/// </summary>
public sealed class P769BuilderCollisionBuildTests
{
    private static object? Invoke(Srv server, string method, params object[] args) =>
        typeof(Srv).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, args);

    private static void Set(Srv server, string field, object value) =>
        typeof(Srv).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, value);

    private static Mission Mission(IIgcSite site)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        var mission = Allegiance.Core.Model.Mission.Assemble(core, map,
            [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], site);
        mission.Stage = MissionStage.Started;
        CvhFile? Load(string name)
        {
            var path = Path.Combine(TestArtRoot.Value, name + ".cvh");
            return File.Exists(path) ? CvhReader.Parse(File.ReadAllText(path)) : null;
        }
        foreach (var sector in mission.Sectors)
        {
            sector.StationLandingBays = new StationLandingBayCache(Load);
            sector.CollisionHulls = new CollisionHullCache(Load);
        }
        return mission;
    }

    private static Asteroid Rock(Mission mission, Sector sector, DataStationTypeIGC type, Vector3 position,
        float radius = 60f, float[]? rotation = null)
    {
        // rotation [0,1,0,0]: spin axis +Y (native asteroids always rotate;
        // the build approach lines the ship up on this axis, common.cpp:1390-1402).
        var rock = new Asteroid(new DataAsteroidIGC(1, [position.X, position.Y, position.Z],
            [0, 1, 0], [1, 0, 0], rotation ?? [0, 1, 0, 0], 0, 0, type.AabmBuild,
            6901, 0, (short)radius, "roid.mdl", "", "", sector.Id, "P769 build target", 0));
        mission.PlaceAsteroid(rock, sector);
        return rock;
    }

    private static void Order(Srv server, Ship ship, Asteroid rock) =>
        Assert.True((bool)Invoke(server, "StartBuildIfLegal", ConnectionId.Me, (short)999, ship,
            new OrderChange { ShipID = ship.Id, CommandId = OrderChange.CidBuild,
                Command = OrderChange.CmdAccepted, ObjectType = IgcObjectType.Asteroid,
                ObjectID = rock.Data.AsteroidId })!);

    private static int Count(IEnumerable<string> calls, string prefix) => calls.Count(c => c.StartsWith(prefix));

    /// <summary>
    /// The headline SIM-0109 flow: a bucket-spawned constructor UNDOCKS FROM ITS
    /// STATION, flies to the ordered rock, latches drilling on the axis
    /// approach and the station gets built — with the effect created by the
    /// drilling COLLISION (hull hit in the creation tick), not by the
    /// stopped-sphere substitute.
    /// </summary>
    /// <remarks>
    /// P769b — a THEORY over the rock radii the reviewer swept: native
    /// buildable rocks span radius 200-400 (NativeAsteroidTypes.cs:62-63), so
    /// 60 alone proves nothing. Each radius must build within a bounded time
    /// AND latch drilling no more than a few units inside the rock surface —
    /// native latches on plan completion at the strip rest point OUTSIDE the
    /// rock (Igc/common.cpp:1187, Igc/shipIGC.cpp:2810-2817) and then drifts
    /// into contact; a latch deep inside the rock is a parity break.
    /// </remarks>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(60)]
    [InlineData(200)]
    [InlineData(400)]
    public void Constructor_flies_from_its_station_and_builds_through_the_drilling_collision(int rockRadius)
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        using var server = new Srv();
        var mission = Mission(site);
        Set(server, "_missions", new List<Mission> { mission });
        Set(server, "_config", new global::Allegiance.Server.ServerConfig());
        var side = mission.Sides[0];
        var type = side.Buckets.First(b => b.ItemType == BucketItemType.BitStationType).StationType!.Value;
        Invoke(server, "CompleteStationType", mission, side, type, 0d);
        var ship = Assert.Single(mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()));
        var sector = ship.Sector!;
        void Tick(double now)
        {
            Set(server, "_simNow", now);
            Invoke(server, "RunNpcAutoLaunch", now);
            sector.Update(now);
        }
        for (int i = 1; i <= 200; i++) Tick(i * 0.05);
        Assert.True(ship.WaitsForBuildOrder); Assert.False(ship.IsBuilding);
        Assert.Empty(sector.BuildingEffects);
        if (ship.StationId is not null) Invoke(server, "UndockShipState", ship, sector);
        Assert.Null(ship.StationId);

        var rock = Rock(mission, sector, type, ship.Position + new Vector3(rockRadius + 400, 0, 0), rockRadius);
        var rockPos = new Vector3(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);
        Order(server, ship, rock);

        int createTick = -1;
        float? latchPenetration = null;   // rock surface penetration at the FIRST drilling tick
        var seen = new List<string>();
        for (int i = 201; i <= 8000 && createTick < 0; i++)
        {
            double now = i * 0.05;
            Tick(now);
            var fresh = site.Calls.Skip(seen.Count).ToList();
            if (Count(fresh, "CreateBuildingEffect(") > 0)
            {
                createTick = i;
                // The collision branch raises the drilling HullHitEvent in the
                // SAME tick it creates the effect (shipIGC.cpp:884-890); the
                // stopped-sphere substitute never does.
                Assert.True(Count(fresh, "HullHitEvent(") > 0,
                    $"effect created on tick {i} without drilling debris — not the collision path");
            }
            seen.AddRange(fresh);
            if (createTick < 0 && latchPenetration is null && ship.IsDrilling)
                latchPenetration = rockRadius - (ship.Position - rockPos).Length();
        }

        Assert.True(createTick > 0, $"builder never registered its build (pos={ship.Position}, vel={ship.Velocity}, " +
            $"target={ship.BuilderAsteroidTarget?.Data.AsteroidId}, drilling={ship.IsDrilling}, running={ship.IsRunningAway})");
        // Native latches drilling on plan completion at the strip rest point
        // OUTSIDE the rock (Igc/common.cpp:1187, Igc/shipIGC.cpp:2810-2817)
        // and then drifts into contact — the latch must not happen deep inside
        // the rock surface (P769b; the P769 code latched ~16 units inside at
        // radius 60 and never at 200+).
        Assert.NotNull(latchPenetration);
        Assert.True(latchPenetration <= 5f,
            $"drilling latched {latchPenetration:0.##} units inside the rock surface (radius {rockRadius})");
        // (The drilling latch itself is implied by the debris assertion above:
        // the collision branch is drill-gated (shipIGC.cpp:874) — no latch, no
        // effect. The latch flag is consumed within the creation tick — the
        // registered effect's building state clears it — so it is not sampled
        // here.)
        var effect = Assert.Single(sector.BuildingEffects);
        Assert.Same(ship, effect.Builder); Assert.Same(rock, effect.Target);
        Assert.True(ship.IsBuilding);
        // The station actually gets built: the effect completes into a runtime
        // (continue the same tick index — a time jump would terminate the
        // effect past its EndTime instead of completing it).
        for (int i = createTick + 1; i <= createTick + 1200 && sector.BuildingEffects.Count > 0; i++)
            Tick(i * 0.05);
        Assert.Empty(sector.BuildingEffects);
        Assert.DoesNotContain(rock, sector.Mineable);
        Assert.Contains(sector.StationRuntimes, s => s.Data.Position.SequenceEqual(rock.Data.Position) &&
            s.Type.StationTypeId == type.StationTypeId && s.SideId == 0);
    }

    /// <summary>
    /// The documented P652 PARKED-RESERVATION case — the ONLY thing
    /// <c>Ship.TryStartBuilderBuild</c>'s stopped-sphere start still serves
    /// after SIM-0109 (c): a constructor PARKED beside its rock (spawned at
    /// contact + 20, OFF the drill axis, hull thrust 0) can never fly the axis
    /// approach and never latches drilling, so the fallback starts its build —
    /// and the fallback alone: no collision, no drilling debris, no latch.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Parked_reservation_constructor_builds_through_the_stopped_sphere_fallback_only()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        using var server = new Srv();
        var mission = Mission(site);
        Set(server, "_missions", new List<Mission> { mission });
        var sector = mission.Sectors[0];
        var type = mission.Core.StationTypes.Values.First(t => t.ClassId == 0 &&
            (t.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);
        var drone = mission.Core.DroneTypes.Values.First(t => t.PilotType == Ship.PilotTypeBuilder);
        var position = new Vector3(615, 425, 455);
        // Axis-LESS rock (zero rotation, the P652 test-rock shape): the drill
        // latch can never fire for it, so the parked-reservation fallback is
        // its only build path — exactly what the reduced
        // Ship.TryStartBuilderBuild still serves.
        var rock = Rock(mission, sector, type, position, 60f, [0, 0, 0, 0]);
        var ship = server.SpawnNpcShip(mission, sector, 0,
            position + new Vector3(rock.Radius + 0.5f * mission.Core.HullTypes[drone.HullTypeId].Length + 20, 0, 0),
            drone.DroneTypeId);
        ship.BuilderStationType = type;
        Order(server, ship, rock);
        sector.Update(0.05);

        // Built through the parked-reservation fallback: the effect registered
        // with NO drilling debris (the collision branch is drill-gated now) and
        // the ship never latched drilling (it is off the drill axis).
        var effect = Assert.Single(sector.BuildingEffects);
        Assert.Same(ship, effect.Builder); Assert.Same(rock, effect.Target);
        Assert.True(ship.IsBuilding); Assert.Null(ship.BuilderAsteroidTarget);
        Assert.Equal(0, Count(site.Calls, "HullHitEvent("));
        Assert.False(ship.IsDrilling);
    }
}
