using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P762 — the two commander/drone rows: the idle layer's least-targeted
/// any-cluster asteroid plan with the native cowardly filter
/// (Igc/shipIGC.h:2417-2434; Igc/common.cpp:355-420,693) and the builder's
/// building/drilling controls plus collision-created building effect
/// (Igc/shipIGC.cpp:871-893,2262-2300).
/// </summary>
public sealed class P762DroneRuleTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
    private static MissionMap Map() => MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
    private static DataHullTypeIGC Hull(StaticCore core) =>
        core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));

    private static Asteroid Rock(Mission mission, Sector sector, short id, Vector3 pos, short aabm = 0)
    {
        var rock = new Asteroid(new DataAsteroidIGC(1, [pos.X, pos.Y, pos.Z], [0, 1, 0], [1, 0, 0],
            [0, 1, 0, 0], 100, 100, aabm, id, 100, 60, "roid.mdl", "", "", sector.Id, $"rock{id}", 0));
        mission.PlaceAsteroid(rock, sector);
        return rock;
    }

    private static object? Invoke(Ship ship, string name, params object?[] args)
    {
        try { return typeof(Ship).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args); }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    /// <summary>An orientation whose nose faces <paramref name="dir"/> — the
    /// native charge gate (common.cpp:1302-1311) needs the builder facing the
    /// rock centre for the completion stand-in to latch.</summary>
    private static Orientation Facing(Vector3 dir)
    {
        var o = Orientation.Identity;
        o.SetForward(dir);
        o.SetUp(MathF.Abs(dir.Y) > 0.9f ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0));
        o.Renormalize();
        return o;
    }

    private static Mission EmptyMission(RecordingIgcSite site) =>
        Mission.Assemble(Core(), Map(), [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], site, 0);

    /// <summary>
    /// c_ttLeastTargeted — the deciding layer picks the rock no same-side layer
    /// is already committed to.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Layer_default_plan_picks_the_least_targeted_asteroid()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var hull = Hull(mission.Core);
        var busy = Rock(mission, sector, 7601, new Vector3(100, 0, 0));
        var free = Rock(mission, sector, 7602, new Vector3(300, 0, 0));

        var otherLayer = new Ship(7603, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeLayer, SideId = 0, LayerAsteroidTarget = busy };
        mission.PlaceShip(otherLayer, sector);
        var decider = new Ship(7604, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeLayer, SideId = 0 };
        mission.PlaceShip(decider, sector);

        var picked = (Asteroid?)Invoke(decider, "FindLeastTargetedAsteroidAnyCluster", new object?[] { null });
        Assert.Same(free, picked);
        Console.WriteLine("P762-LEAST-TARGETED: picked the rock with zero same-side contenders");
    }

    /// <summary>
    /// c_ttCowardly — the any-cluster search never expands into a cluster that
    /// is not friendly territory, even when that cluster's rock is less
    /// contested. Friendly here is the native side-territory base case: a
    /// same-side station and no enemy station (sideigc.cpp:436-461).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Layer_cowardly_search_skips_unfriendly_clusters()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var start = mission.Sectors.First(s => s.Warps.Count(w => w.Destination is not null) >= 2);
        var destinations = start.Warps.Select(w => w.Destination!).Where(d => d != start).Distinct().Take(2).ToArray();
        Assert.Equal(2, destinations.Length);
        var enemySector = destinations[0];
        var friendlySector = destinations[1];
        var hull = Hull(mission.Core);

        // Enemy cluster: one station (side 1) and an uncontested rock.
        var type = mission.Core.StationTypes.Values.First();
        var enemyStation = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        {
            StationId = 7610, SideId = 1, ClusterId = enemySector.Id, Position = [0, 0, 0],
        }, type);
        mission.PlaceStation(enemyStation, enemySector);
        // The cowardly filter is native Sector.IsFriendlyCluster (P753,
        // common.cpp:388-431): an enemy station blocks territory only when the
        // side has SEEN it. Mark it seen so the enemy cluster is unambiguously
        // hostile (and not "empty, surrounded by territory").
        enemyStation.SideVisibility.SetSideVisibility(0, true);
        var enemyRock = Rock(mission, enemySector, 7611, new Vector3(100, 0, 0));

        // Friendly cluster: a same-side station and a rock with one contender.
        var friendlyStation = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        {
            StationId = 7612, SideId = 0, ClusterId = friendlySector.Id, Position = [500, 0, 0],
        }, type);
        mission.PlaceStation(friendlyStation, friendlySector);
        var friendlyRock = Rock(mission, friendlySector, 7613, new Vector3(100, 0, 0));

        var contender = new Ship(7614, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeLayer, SideId = 0, LayerAsteroidTarget = friendlyRock };
        mission.PlaceShip(contender, friendlySector);
        var decider = new Ship(7615, hull, new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeLayer, SideId = 0 };
        mission.PlaceShip(decider, start);

        var picked = (Asteroid?)Invoke(decider, "FindLeastTargetedAsteroidAnyCluster", new object?[] { null });
        Assert.Same(friendlyRock, picked);
        Assert.NotSame(enemyRock, picked);
        Console.WriteLine("P762-COWARDLY: skipped the enemy-held sector and chose the friendly one");
    }

    /// <summary>
    /// Building state — native yaw 0 / pitch 0 / roll 1 / throttle -0.8
    /// (shipIGC.cpp:2264-2272), observed through the real per-tick Update path.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Building_builder_holds_native_roll_and_negative_throttle()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var builder = new Ship(7620, Hull(mission.Core), new Vector3(0, 0, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0, IsBuilding = true };
        mission.PlaceShip(builder, sector);
        builder.InitClock(1);
        builder.Update(1.05);

        Assert.Equal(0f, builder.Controls.Yaw);
        Assert.Equal(0f, builder.Controls.Pitch);
        Assert.Equal(1f, builder.Controls.Roll);
        Assert.Equal(-0.8f, builder.Controls.Throttle);
    }

    /// <summary>
    /// Drilling on-track — within 50 units² of the rock's rotation axis and
    /// inside r_ship + r_rock + 25: hold the axis with roll 1 / throttle 0
    /// (shipIGC.cpp:2274-2296). Off-track: the native clears the drilling mask
    /// and re-approaches; the port leaves the autopilot controls untouched.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Drilling_builder_holds_only_when_the_rock_axis_alignment_is_on_track()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var rockPos = new Vector3(100, 0, 0);
        var rock = Rock(mission, sector, 7621, rockPos);       // rotation axis (0,1,0)
        var builder = new Ship(7622, Hull(mission.Core) with { Length = 10 }, rockPos + new Vector3(0, 50, 0),
            Vector3.Zero, Facing(new Vector3(0, -1, 0)))
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0, BuilderAsteroidTarget = rock };
        mission.PlaceShip(builder, sector);
        builder.InitClock(1);
        builder.Update(1.05);
        Assert.Equal(1f, builder.Controls.Roll);
        Assert.Equal(0f, builder.Controls.Throttle);

        // Off-track: 20 units perpendicular to the Y axis → offset² = 400 > 50.
        builder.Position = rockPos + new Vector3(0, 0, 20);
        builder.Controls = builder.Controls with { Yaw = .37f, Pitch = -.21f, Roll = .11f, Throttle = .42f };
        Invoke(builder, "ApplyBuilderDrillingControls");
        Assert.Equal(.37f, builder.Controls.Yaw);
        Assert.Equal(-.21f, builder.Controls.Pitch);
        Assert.Equal(.11f, builder.Controls.Roll);
        Assert.Equal(.42f, builder.Controls.Throttle);
    }

    /// <summary>
    /// Collision while drilling creates the building effect exactly ONCE and
    /// raises the drilling hull-hit debris (shipIGC.cpp:871-893).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Drilling_collision_creates_one_building_effect_and_hull_hit_debris()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = EmptyMission(site);
        var sector = mission.Sectors[0];
        var type = mission.Core.StationTypes.Values.First(t => t.AabmBuild != 0);
        var rockPos = new Vector3(200, 0, 0);
        var rock = Rock(mission, sector, 7630, rockPos, type.AabmBuild);
        // On the rock's spin axis inside the drill sphere: the builder arrives
        // (the stopped/slow latch, shipIGC.cpp:2810-2817) and the drilling
        // COLLISION then registers the build. P769 — the collision branch is
        // drill-gated (native shipIGC.cpp:874): a fast fly-through never
        // builds, so the scenario is the native one — latch first, contact
        // second. The drill-gate itself keeps TryStartBuilderBuild (which
        // skips valid-axis rocks, SIM-0109c) out of the way.
        var builder = new Ship(7631, Hull(mission.Core) with { Length = 10 }, rockPos + new Vector3(0, 50, 0),
            Vector3.Zero, Facing(new Vector3(0, -1, 0)))
        {
            IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0,
            BuilderAsteroidTarget = rock, BuilderStationType = type,
        };
        mission.PlaceShip(builder, sector);
        builder.InitClock(1);

        builder.Update(1.05);
        Assert.True(builder.IsDrilling);   // the latch the collision branch is gated on

        sector.Update(1.10);
        Assert.Single(sector.BuildingEffects);
        Assert.Contains(site.Calls, c => c.StartsWith("HullHitEvent("));

        sector.Update(1.15);
        Assert.Single(sector.BuildingEffects);
        Console.WriteLine("P762-DRILL-EFFECT: one effect and drilling debris from the asteroid collision");
    }
}
