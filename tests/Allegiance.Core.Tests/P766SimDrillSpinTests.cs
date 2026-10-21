using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P766 — simulation wave: the constructor drill/arrival latch
/// (SIM-0109, <c>Igc/shipIGC.cpp:2262-2300,2810-2827,871-893</c>), goto
/// completion clearing the accepted command slot
/// (SIM-0119, <c>Igc/shipIGC.cpp:2802-2810</c>) and the model spin
/// (SIM-0078, <c>Igc/modelIGC.cpp:73-96</c>).
/// </summary>
public sealed class P766SimDrillSpinTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
    private static MissionMap Map() => MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
    private static DataHullTypeIGC Hull(StaticCore core) =>
        core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));

    private static Asteroid Rock(Mission mission, Sector sector, short id, Vector3 pos, float[]? rotation = null, short aabm = 0)
    {
        var rock = new Asteroid(new DataAsteroidIGC(1, [pos.X, pos.Y, pos.Z], [0, 1, 0], [1, 0, 0],
            rotation ?? [0, 0, 0, 0], 100, 100, aabm, id, 100, 60, "roid.mdl", "", "", sector.Id, $"rock{id}", 0));
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

    // ─────────────────────────── SIM-0109 ───────────────────────────

    /// <summary>
    /// The drilling latch: a builder flying its approach (not yet stopped
    /// inside the drill sphere) has NO drilling latch and gets no hold
    /// (native sets drillingMaskIGC only on gotoplan arrival,
    /// <c>shipIGC.cpp:2810-2817</c>); once arrived — stopped, on-track and
    /// facing the rock (the native charge gate, common.cpp:1302-1311) — the
    /// latch is set and the axis hold applies (<c>shipIGC.cpp:2274-2296</c>).
    /// A stopped but MISALIGNED builder pivots first (common.cpp:1312-1318)
    /// instead of latching — P769b: latching nose-away would thrust the drill
    /// hold out of the band.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Builder_latches_drilling_only_on_arrival_not_while_flying_the_approach()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var rockPos = new Vector3(100, 0, 0);
        // rotation (0,1,0,0): axis +Y, so a ship offset (0,50,0) is on-track
        var rock = Rock(mission, sector, 7661, rockPos, [0, 1, 0, 0]);
        var builder = new Ship(7662, Hull(mission.Core) with { Length = 10 }, rockPos + new Vector3(0, 50, 0),
            Vector3.Zero, Facing(new Vector3(0, -1, 0)))
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0, BuilderAsteroidTarget = rock };
        mission.PlaceShip(builder, sector);

        // Before arrival: still flying (velocity² >= 1) and on-track — the
        // hold must NOT engage and the latch must stay unset.
        builder.Velocity = new Vector3(30, 0, 0);
        builder.Controls = builder.Controls with { Yaw = .31f, Pitch = -.17f, Roll = .13f, Throttle = .77f };
        Assert.False(builder.IsDrilling);
        Invoke(builder, "ApplyBuilderDrillingControls");
        Assert.False(builder.IsDrilling);
        Assert.Equal(.31f, builder.Controls.Yaw);
        Assert.Equal(-.17f, builder.Controls.Pitch);
        Assert.Equal(.13f, builder.Controls.Roll);
        Assert.Equal(.77f, builder.Controls.Throttle);

        // Arrival: the same on-track spot, stopped (velocity² < 1) and facing
        // the rock — the port's gotoplan-completion stand-in (native's charge
        // gate, common.cpp:1302-1311) — latches drilling and holds yaw 0 /
        // pitch 0 / roll 1 / throttle 0 (shipIGC.cpp:2285-2293).
        builder.Velocity = Vector3.Zero;
        Invoke(builder, "ApplyBuilderDrillingControls");
        Assert.True(builder.IsDrilling);
        Assert.Equal(0f, builder.Controls.Yaw);
        Assert.Equal(0f, builder.Controls.Pitch);
        Assert.Equal(1f, builder.Controls.Roll);
        Assert.Equal(0f, builder.Controls.Throttle);

        // P769b — stopped but MISALIGNED (nose away from the rock): native
        // pivots to face the centre before charging (common.cpp:1312-1318) —
        // the stand-in turns the ship with the throttle killed and does NOT
        // latch.
        var misaligned = new Ship(7672, Hull(mission.Core) with { Length = 10 }, rockPos + new Vector3(0, 50, 0),
            Vector3.Zero, Facing(new Vector3(0, 1, 0)))
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0, BuilderAsteroidTarget = rock };
        mission.PlaceShip(misaligned, sector);
        Invoke(misaligned, "ApplyBuilderDrillingControls");   // stopped, on-track, nose +Y (away)
        Assert.False(misaligned.IsDrilling);
        Assert.Equal(-1f, misaligned.Controls.Throttle);      // the pivot kills the throttle
        Assert.NotEqual(0f, misaligned.Controls.Yaw);         // and turns onto the centre
    }

    /// <summary>
    /// Off-track while drilling clears the latch (native
    /// <c>SetStateM(0)</c>, <c>shipIGC.cpp:2303</c>) and a new plan clears it
    /// too (<c>shipIGC.h:755-757</c>); the building effect registering clears
    /// it as well (<c>buildingeffectigc.h:66</c> drilling|building).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Drilling_latch_clears_off_track_and_on_a_new_plan()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var rockPos = new Vector3(100, 0, 0);
        var rock = Rock(mission, sector, 7663, rockPos, [0, 1, 0, 0]);
        var builder = new Ship(7664, Hull(mission.Core) with { Length = 10 }, rockPos + new Vector3(0, 50, 0),
            Vector3.Zero, Facing(new Vector3(0, -1, 0)))
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0, BuilderAsteroidTarget = rock };
        mission.PlaceShip(builder, sector);
        Invoke(builder, "ApplyBuilderDrillingControls");   // arrival: latch + hold
        Assert.True(builder.IsDrilling);

        // Off-track: 20 units perpendicular to the Y axis → offset² = 400 > 50.
        builder.Position = rockPos + new Vector3(0, 0, 20);
        Invoke(builder, "ApplyBuilderDrillingControls");
        Assert.False(builder.IsDrilling);

        // Re-arrive (back on the axis, still stopped), then a NEW PLAN (a
        // goto) clears the latch and the target.
        builder.Position = rockPos + new Vector3(0, 50, 0);
        Invoke(builder, "ApplyBuilderDrillingControls");
        Assert.True(builder.IsDrilling);
        builder.SetGoto(new GotoOrder(GotoKind.Waypoint, sector.Id, rockPos + new Vector3(500, 0, 0)));
        Assert.False(builder.IsDrilling);
        Assert.Null(builder.BuilderAsteroidTarget);
    }

    /// <summary>
    /// Merge fixup: a builder that arrives at a rock another builder already
    /// claimed drops the target instead of latching drilling. A latched builder
    /// refuses every order (IsLegalCommand), so it would otherwise sit there
    /// forever.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Builder_arriving_at_a_rock_claimed_by_another_builder_drops_it_and_takes_orders()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var rockPos = new Vector3(100, 0, 0);
        var type = mission.Core.StationTypes.Values.First();
        var rock = Rock(mission, sector, 7667, rockPos, [0, 1, 0, 0], (short)type.AabmBuild);
        var hull = Hull(mission.Core) with { Length = 10 };
        var owner = new Ship(7668, hull, rockPos + new Vector3(0, -50, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0 };
        var late = new Ship(7669, hull, rockPos + new Vector3(0, 50, 0), Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0, BuilderAsteroidTarget = rock };
        mission.PlaceShip(owner, sector);
        mission.PlaceShip(late, sector);
        sector.StartBuildingEffect(owner, rock, type, 0);
        Assert.NotNull(rock.BuildingEffect);

        Invoke(late, "ApplyBuilderDrillingControls");   // stopped and on-track
        Assert.False(late.IsDrilling);
        Assert.Null(late.BuilderAsteroidTarget);
    }

    /// <summary>
    /// A builder that hits its rock in flight starts the building effect from
    /// the asteroid COLLISION (native <c>shipIGC.cpp:871-893</c>:
    /// m_bAutopilot && no effect yet → CreateBuildingEffect + drilling debris).
    /// Not native-exact: the port's collision branch is not gated on the
    /// drilling latch (native <c>shipIGC.cpp:874</c>), and a builder that stops
    /// first still builds through TryStartBuilderBuild (SIM-0109 stays partial). Proven by per-tick call
    /// recording: the effect's creation tick must carry the drilling
    /// HullHitEvent, which only the collision branch raises.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flying_builder_registers_the_build_from_the_collision_not_the_stopped_substitute()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = EmptyMission(site);
        var sector = mission.Sectors[0];
        var type = mission.Core.StationTypes.Values.First(t => t.ClassId == 0 &&
            (t.AabmBuild & DataAsteroidIGC.AabmBuildable) != 0);
        var rockPos = new Vector3(615, 425, 455);
        var rock = Rock(mission, sector, 7665, rockPos, [0, 1, 0, 0], type.AabmBuild);
        var builder = new Ship(7666, Hull(mission.Core), rockPos + new Vector3(300, 0, 0),
            Vector3.Zero, Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeBuilder, SideId = 0, BuilderStationType = type };
        mission.PlaceShip(builder, sector);
        builder.SetBuilderBuild(rock);
        builder.InitClock(1);

        int createTick = -1, firstHullHitTick = -1;
        var seen = new HashSet<string>(site.Calls);
        int tick = 1;
        for (; tick <= 4000 && createTick < 0; tick++)
        {
            double now = 1 + tick * 0.05;
            sector.Update(now);
            int creates = site.Calls.Count(c => c.StartsWith("CreateBuildingEffect(")) -
                          seen.Count(c => c.StartsWith("CreateBuildingEffect("));
            int hullHits = site.Calls.Count(c => c.StartsWith("HullHitEvent(")) -
                           seen.Count(c => c.StartsWith("HullHitEvent("));
            if (creates > 0 && createTick < 0)
            {
                createTick = tick;
                // The collision branch raises HullHitEvent in the SAME tick it
                // creates the effect (shipIGC.cpp:884-890). The stopped
                // substitute (TryStartBuilderBuild) never raises it — so a
                // hull hit in the creation tick proves the collision path won.
                Assert.True(hullHits > 0,
                    $"effect created on tick {tick} without drilling debris — the stopped-within-radius substitute fired first (velocity²={builder.Velocity.LengthSquared():0.##})");
            }
            if (hullHits > 0 && firstHullHitTick < 0) firstHullHitTick = tick;
            seen.Clear();
            seen.UnionWith(site.Calls);
        }

        Assert.True(createTick > 0, "builder never registered its build");
        Assert.Equal(createTick, firstHullHitTick);
        Assert.Single(sector.BuildingEffects);
        Console.WriteLine($"P766-DRILL: effect on tick {createTick} (first flight tick with drilling debris {firstHullHitTick}) — collision-created");
    }

    // ─────────────────────────── SIM-0119 ───────────────────────────

    /// <summary>
    /// Native gotoplan completion with <c>c_cidGoto</c> clears EVERY command
    /// slot whose target is the plan target — the accepted slot included
    /// (<c>shipIGC.cpp:2802-2810</c>). The port clears
    /// <see cref="Ship.AcceptedTarget"/>/<see cref="Ship.AcceptedCommandId"/>
    /// when the accepted goto names the same STATION OBJECT (by StationId,
    /// never by position); an accepted goto to a different station survives.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Goto_completion_clears_the_accepted_goto_to_the_same_station_only()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var type = mission.Core.StationTypes.Values.First();
        var stationA = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 7667, SideId = 0, ClusterId = sector.Id, Position = [3000f, 5400f, 7900f] }, type);
        var stationB = new Station(DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 7668, SideId = 0, ClusterId = sector.Id, Position = [3400f, 5400f, 7900f] }, type);
        mission.PlaceStation(stationA, sector);
        mission.PlaceStation(stationB, sector);

        var ship = new Ship(7669, Hull(mission.Core), new Vector3(3000f, 5400f, 7900f), Vector3.Zero,
            Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeLayer, SideId = 0 };
        mission.PlaceShip(ship, sector);
        ship.InitClock(1);

        // Accepted goto to station A; goto issued against the SAME station.
        ship.AcceptedTarget = stationA.Data;
        ship.AcceptedCommandId = OrderChange.CidGoto;
        ship.SetGoto(new GotoOrder(GotoKind.Waypoint, sector.Id, stationA.Center, stationA.StationId));

        sector.Update(1.05);   // the ship is already at the waypoint: arrival this tick

        Assert.Null(ship.GotoWaypoint);
        Assert.Null(ship.AcceptedTarget);                       // cleared with the plan target
        Assert.Equal(OrderChange.CidNone, ship.AcceptedCommandId);

        // Control: an accepted goto to a DIFFERENT station survives the same
        // completion (native clears only slots naming pmodelPlan).
        var ship2 = new Ship(7670, Hull(mission.Core), new Vector3(3000f, 5400f, 7900f), Vector3.Zero,
            Orientation.Identity)
        { IsNpc = true, PilotType = Ship.PilotTypeLayer, SideId = 0 };
        mission.PlaceShip(ship2, sector);
        ship2.InitClock(1);
        ship2.AcceptedTarget = stationB.Data;
        ship2.AcceptedCommandId = OrderChange.CidGoto;
        ship2.SetGoto(new GotoOrder(GotoKind.Waypoint, sector.Id, stationA.Center, stationA.StationId));
        sector.Update(1.10);
        Assert.Null(ship2.GotoWaypoint);
        Assert.Equal(stationB.Data, ship2.AcceptedTarget);
        Assert.Equal(OrderChange.CidGoto, ship2.AcceptedCommandId);
    }

    // ─────────────────────────── SIM-0078 ───────────────────────────

    /// <summary>
    /// A rock with a non-zero rotation angle post-rotates its sim orientation
    /// about the rotation axis by <c>angle·dt</c> every tick, renormalized
    /// (<c>Igc/modelIGC.cpp:85-99</c>; data seeded at
    /// <c>asteroidigc.cpp:41-45</c>). N ticks of rate ω about +Y turn the
    /// seeded forward (1,0,0) to (cos θ, 0, −sin θ) with θ = ω·N·dt — the
    /// native Matrix::SetRotate Rodrigues form (<c>zlib/matrix.cpp:408-452</c>).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rotating_asteroid_turns_its_orientation_by_angle_times_dt()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        const float rate = 0.5f;                       // rad/s about +Y
        var rock = Rock(mission, sector, 7671, new Vector3(100, 0, 0), [0, 1, 0, rate]);
        Assert.Equal(1f, rock.Orientation.GetForward().X);   // seeded from the record's forward
        Assert.Equal(0f, rock.Orientation.GetForward().Z);

        const int ticks = 40;
        const float dt = 0.05f;
        for (int i = 1; i <= ticks; i++) rock.Update(i * (double)dt, dt, 0f);

        float theta = rate * ticks * dt;               // 1.0 rad
        var fwd = rock.Orientation.GetForward();
        Assert.Equal(MathF.Cos(theta), fwd.X, 3f);
        Assert.Equal(-MathF.Sin(theta), fwd.Z, 3f);
        Assert.Equal(1f, fwd.Length(), 3f);            // renormalized
        Assert.Equal(0f, rock.Orientation.GetForward().Y, 5f);
    }

    /// <summary>
    /// A zero-angle rock (the native <c>m_rotation.angle() != 0</c> guard,
    /// <c>modelIGC.cpp:87</c>) never touches its orientation, and the spin
    /// step is allocation-free — the per-rock per-tick cost at 200v200 is a
    /// few float ops (zero-angle rocks: one array length check).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Zero_angle_asteroid_is_untouched_and_the_spin_step_allocates_nothing()
    {
        Require.Artwork();
        var mission = EmptyMission(new RecordingIgcSite());
        var sector = mission.Sectors[0];
        var rock = Rock(mission, sector, 7672, new Vector3(100, 0, 0), [0.6f, 0.8f, 0f, 0f]);
        var forwardBefore = rock.Orientation.GetForward();
        var upBefore = rock.Orientation.GetUp();
        for (int i = 1; i <= 50; i++) rock.Update(i * 0.05, 0.05f, 0f);
        Assert.Equal(forwardBefore, rock.Orientation.GetForward());
        Assert.Equal(upBefore, rock.Orientation.GetUp());

        // Allocation-free over the tick path (warmup first, then measure).
        var spinning = Rock(mission, sector, 7673, new Vector3(200, 0, 0), [0, 1, 0, 0.5f]);
        for (int i = 0; i < 100; i++) spinning.Update(i * 0.05, 0.05f, 0f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 100; i < 200; i++) spinning.Update(i * 0.05, 0.05f, 0f);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
