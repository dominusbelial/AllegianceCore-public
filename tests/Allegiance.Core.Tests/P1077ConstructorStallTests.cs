using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1077 — a constructor that never reaches its rock stalled a perpetual 5v5 commander for the whole
/// match (match 13: seed 1498799628, <c>gen:SingleRing</c> <c>mapSeed=1932285663</c>, Ga'Taraan vs Iron
/// Coalition). These tests regenerate that exact map, pin asteroid 52 and the side-1 home Garrison, and
/// run the production build-order path for the recorded constructor with and without the commander's
/// 5 s re-send.
/// <para><b>Native baseline.</b> A repeated identical BUILD keeps the trip: native's plan write re-points
/// the same waypoint target and <c>Waypoint</c> holds no progress (<c>Igc/igc.h:5324-5335</c>,
/// <c>Igc/igc.h:5235-5257</c>), so only the per-order latches reset (<c>Igc/shipIGC.h:750-767</c>). The
/// port already behaves the same: <see cref="Ship.SetBuilderBuild"/> keeps the same rock target (its
/// setter is a no-op for the same rock) and replaces any competing plan slot, as native's single plan
/// write does; a different rock retargets.</para>
/// </summary>
public sealed class P1077ConstructorStallTests
{
    // The match-13 map parameters exactly as the server draws them: rotation seed 927, cycle mode,
    // 13th match -> SingleRing entry + draw 1932285663; the logged civilizations are Ga'Taraan (3) on
    // side 0 and Iron Coalition (25) on side 1 (MapRotationConfig + MapGenParams defaults).
    private const int Match13MapSeed = 1932285663;
    private const short IronCoalition = 25;
    private const short Gatarran = 3;
    private const short Asteroid52 = 52;
    // Iron Coalition 'Teleport (Hvy)' station type and 'Constructor (Sml)' drone type (the log's ship
    // 8453: drone type 427, hull 'Sm Std Constructor').
    private const short TeleportHeavy = 147;
    private const short ConstructorSmall = 427;

    private static MapGenParams Match13Params => new()
    {
        Type = MapGenType.SingleRing,
        Seed = Match13MapSeed,
        Teams = 2,
        CivIds = [Gatarran, IronCoalition],
    };

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));

    private static MissionMatch13 Assembly(StaticCore core, IIgcSite site)
    {
        var map = MissionMapGenerator.Generate(Match13Params, core, 0);
        var mission = Mission.Assemble(core, map,
            [new SideSpec(0, "Ga'Taraan", Gatarran), new SideSpec(1, "Iron Coalition", IronCoalition)], site);
        mission.Stage = MissionStage.Started;
        mission.NativeGotoPlanEnabled = true;
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
        return new(mission, map);
    }

    private sealed record MissionMatch13(Mission Mission, MissionMap Map);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static Vector3 PositionOf(Asteroid rock) =>
        new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);

    private static (Sector Home, Station Garrison, Asteroid Rock, Ship Constructor) Fixture(
        Srv server, MissionMatch13 match13)
    {
        var mission = match13.Mission;
        var side = mission.Sides.First(s => s.Id == 1);
        // Bucket purchase -> the constructor spawns at the side's home Garrison, exactly like the log
        // ("spawns at the home Garrison ... waits-for-build").
        Invoke(server, "CompleteStationType", mission, side, mission.Core.StationTypes[TeleportHeavy], 0d);
        var constructor = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Single();
        var sector = constructor.Sector!;
        var garrison = sector.StationRuntimes.First(st => st.SideId == 1);
        var rock = sector.Mineable.First(a => a.Data.AsteroidId == Asteroid52);
        rock.SideVisibility.RevealToSide(1);   // the live side had scouted its own home sector
        return (sector, garrison, rock, constructor);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Match_13_map_regenerates_and_asteroid_52_is_the_first_home_sector_buildable_rock()
    {
        Require.Artwork();
        var core = Core();
        var match13 = Assembly(core, new NullIgcSite());

        // The logged census: 4 sectors / 8 warps / 2 stations / 80 asteroids.
        Assert.Equal(4, match13.Map.Clusters.Count);
        Assert.Equal(8, match13.Map.Warps.Count);
        Assert.Equal(2, match13.Map.Stations.Count);
        Assert.Equal(80, match13.Map.Asteroids.Count);

        var rock = Assert.Single(match13.Map.Asteroids, a => a.AsteroidId == Asteroid52);
        Assert.Equal("2845", rock.Name);
        Assert.Equal((short)2, rock.ClusterId);                      // side 1's home sector 'Lemminakean'
        Assert.Equal((short)317, rock.Radius);
        Assert.Equal((short)0x8, (short)(rock.AabmCapabilities & 0x8));   // buildable
        Assert.Equal(0, rock.Ore);                                   // ore 0: a build site, no He3
        Assert.Equal(-2738.62f, rock.Position[0], 1);
        Assert.Equal(2552.12f, rock.Position[1], 1);
        Assert.Equal(-180.41f, rock.Position[2], 1);

        // Side 1's home Garrison and the distance to asteroid 52 (the trip that never finished).
        var home = Assert.Single(match13.Map.Stations, s => s.SideId == 1);
        Assert.Equal((short)2, home.ClusterId);
        Assert.Equal((short)115, home.StationTypeId);                // Iron Coalition 'Garrison'
        var distance = new Vector3(rock.Position[0], rock.Position[1], rock.Position[2]).Length();
        Assert.Equal(3748f, distance, 0);                            // 3747.78 units
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Nothing_obstructs_the_home_Garrison_to_asteroid_52_line()
    {
        Require.Artwork();
        var core = Core();
        var match13 = Assembly(core, new NullIgcSite());
        var sector = match13.Mission.Sectors.First(s => s.Id == 2);
        var home = sector.StationRuntimes.First(st => st.SideId == 1);
        var rock = sector.Mineable.First(a => a.Data.AsteroidId == Asteroid52);
        var target = PositionOf(rock);
        var direction = (target - home.Center) / (target - home.Center).Length();
        var length = (target - home.Center).Length();

        float nearest = float.MaxValue;
        short nearestId = -1;
        foreach (var other in sector.Mineable)
        {
            if (other.Data.AsteroidId == Asteroid52 || other.Radius <= 0) continue;
            var position = PositionOf(other);
            float along = (position - home.Center) * direction;
            if (along <= 0 || along >= length) continue;
            float clearance = (position - (home.Center + direction * along)).Length() - other.Radius;
            if (clearance < nearest) { nearest = clearance; nearestId = other.Data.AsteroidId; }
        }
        // The nearest neighbour on the line keeps ~480 units of clearance (`He8036`, r 174): the rock
        // itself is the only obstruction, matching the report's finding that geography is not the cause.
        Assert.Equal((short)48, nearestId);
        Assert.True(nearest > 400, $"nearest clearance {nearest:0} on the home->52 line");
    }

    /// <summary>The recorded arc: a 'Teleport (Hvy)' constructor spawns at the side-1 home Garrison,
    /// is ordered to BUILD asteroid 52 through the production <c>StartBuildIfLegal</c> path, and is
    /// re-ordered every 5 s (the commander's cadence) or exactly once. Both must reach the rock, latch
    /// drilling and register the building effect inside a bounded time.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Constructor_reaches_asteroid_52_and_builds(bool resendEveryFiveSeconds, bool startDocked)
    {
        Require.Artwork();
        using var server = new Srv();
        var match13 = Assembly(Core(), new global::Allegiance.Server.ServerHost());
        SetField(server, "_missions", new List<Mission> { match13.Mission });
        SetField(server, "_config", new global::Allegiance.Server.ServerConfig());
        var (sector, garrison, rock, constructor) = Fixture(server, match13);
        Assert.Equal(Ship.PilotTypeBuilder, constructor.PilotType);
        Assert.Equal("Sm Std Constructor", constructor.HullType.Name);
        Assert.Equal(ConstructorSmall, constructor.DroneTypeId);
        Assert.Equal(TeleportHeavy, constructor.BuilderStationType!.Value.StationTypeId);
        if (startDocked)
        {
            // The log's shape: the constructor docks at the Garrison and auto-undocks 10 s later.
            constructor.Position = garrison.Center;
            constructor.Velocity = Vector3.Zero;
            constructor.StationId = garrison.StationId;
            constructor.LastDockTime = 0;
        }

        double now = 0, lastOrder = double.NegativeInfinity, builtAt = double.NaN;
        int orders = 0;
        for (int tick = 1; tick <= 20 * 300; tick++)
        {
            now = tick * 0.05;
            SetField(server, "_simNow", now);
            Invoke(server, "RunNpcAutoLaunch", now);
            bool due = orders == 0 ? now >= 12 : resendEveryFiveSeconds && now - lastOrder >= 5;
            if (due && constructor.StationId is null
                && !sector.BuildingEffects.Any(e => ReferenceEquals(e.Target, rock)))
            {
                bool accepted = (bool)Invoke(server, "StartBuildIfLegal", ConnectionId.Me, constructor, constructor,
                    new OrderChange
                    {
                        ShipID = constructor.Id, CommandId = OrderChange.CidBuild,
                        Command = OrderChange.CmdAccepted, ObjectType = IgcObjectType.Asteroid,
                        ObjectID = rock.Data.AsteroidId,
                    })!;
                Assert.True(accepted, $"BUILD refused at {now:0}s");
                orders++; lastOrder = now;
            }
            sector.Update(now);
            if (sector.BuildingEffects.Count > 0) { builtAt = now; break; }
            Assert.False(constructor.Dead, $"constructor died at {now:0}s");
        }

        Assert.False(double.IsNaN(builtAt), $"no building effect after {now:0}s, orders={orders}, " +
            $"position={constructor.Position}, target={PositionOf(rock)}, drilling={constructor.IsDrilling}");
        var effect = Assert.Single(sector.BuildingEffects);
        Assert.Same(constructor, effect.Builder);
        Assert.Same(rock, effect.Target);
        Assert.True(constructor.IsBuilding);
        // The P1077 commander safety net assumes a healthy trip is well inside 180 s; the measured
        // reproductions are 98 s (flying spawn) and 112 s (docked spawn + 10 s auto-undock).
        Assert.True(builtAt <= 150, $"build started at {builtAt:0}s — outside the measured 98-112 s window");
        if (resendEveryFiveSeconds) Assert.True(orders >= 5, $"only {orders} orders sent");
    }

    /// <summary>Native's repeated identical BUILD is a fresh plan write (<c>FedSrv/FedSrv.CPP:3278-3285</c>
    /// cascading to <c>c_cmdPlan</c>, <c>Igc/shipIGC.h:750-776</c>): the single plan slot names the rock again,
    /// so any competing plan (here the damaged drone's hide-at-station plan, <c>Igc/shipIGC.h:2230-2244</c>) is
    /// replaced, while the rock target itself is unchanged. The port keeps the builder target and clears
    /// the competing slots; a different rock retargets.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Repeated_identical_build_keeps_the_rock_and_replaces_a_competing_plan()
    {
        Require.Artwork();
        using var server = new Srv();
        var match13 = Assembly(Core(), new NullIgcSite());
        SetField(server, "_missions", new List<Mission> { match13.Mission });
        SetField(server, "_config", new global::Allegiance.Server.ServerConfig());
        var (sector, garrison, rock, constructor) = Fixture(server, match13);
        var other = sector.Mineable.First(a => a.Data.AsteroidId != Asteroid52
            && (a.Data.AabmCapabilities & rock.Data.AabmCapabilities) != 0);

        constructor.SetBuilderBuild(rock);
        // A competing plan the repeat must replace (native single c_cmdPlan), and an idle build
        // reservation it must release (P652c).
        constructor.PlanTarget = garrison.Data;
        constructor.PlanCommandId = OrderChange.CidGoto;
        constructor.IsBuilding = true;
        Assert.Same(rock, constructor.BuilderAsteroidTarget);

        constructor.SetBuilderBuild(rock);

        Assert.Same(rock, constructor.BuilderAsteroidTarget);                 // the trip target is kept
        Assert.Null(constructor.PlanTarget);                                   // the hide plan is replaced
        Assert.False(constructor.IsBuilding);                                  // the reservation is released
        Assert.False(constructor.IsDrilling);

        constructor.SetBuilderBuild(other);

        Assert.Same(other, constructor.BuilderAsteroidTarget);                 // a different rock retargets
    }
}
