using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests;

public sealed class P699cStationCorpusTests(ITestOutputHelper output)
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Npc_docking_thrusters_survive_idle_combat_and_clear_on_next_order()
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var mission = Mission.Assemble(core, MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "XXV_simple.igc"))),
            [new SideSpec(0, "Alpha", 25), new SideSpec(1, "Bravo", 3)], new NullIgcSite());
        var sector = mission.Sectors.Single(s => s.Id == 1052);
        sector.StationLandingBays = new StationLandingBayCache(name => CvhReader.Parse(File.ReadAllText(Path.Combine(art, name + ".cvh"))));
        var station = new Station(new([50000, 0, 0], [0, 1, 0], [0, 0, -1], [0, 0, 0, 1],
            sector.Id, 1, 1234, 44, 100, 100, "Garrison"), core.StationTypes[44]);
        mission.PlaceStation(station, sector);
        var orientation = new Orientation(); orientation.Set(new(0, 1, 0));
        var pod = new Ship(123, core.HullTypes[113], new(50000, 0, 1800), Vector3.Zero, orientation)
        {
            SideId = 1, IsNpc = true, PilotType = Ship.PilotTypeWingman,
            PlanTarget = station.Data,
        };
        mission.PlaceShip(pod, sector);
        pod.Update(.05);
        Assert.NotEqual(0, pod.StateM & ShipControlState.MoveButtonsMask);
        pod.PlanTarget = null;
        pod.Update(.1);
        Assert.Equal(0, pod.StateM & ShipControlState.MoveButtonsMask);
    }

    [Theory]
    [InlineData(.1f, 0f)]
    [InlineData(-.1f, 0f)]
    [InlineData(0f, .1f)]
    [InlineData(0f, -.1f)]
    public void Native_turn_controller_brakes_current_angular_motion(float yaw, float pitch)
    {
        var step = new DockApproach.Step(DockApproach.Mask.Pivot, 0, Vector3.Zero, new(0, 0, -1));
        var turn = new DockApproach.TurnState(10, 60, .7f, .7f, 17, 17, yaw, pitch);
        var control = DockApproach.Steer(step, Vector3.Zero, Orientation.Identity, Vector3.Zero,
            0, dt: .05f, turnState: turn);
        // Native stopping drift = |rate| * rate * mass / (2 * tm * torque),
        // tm=.5 and skill=.5 while stopped; divide by dt*maxRate (common.cpp:216-245).
        Assert.Equal(-MathF.Abs(yaw) * yaw * 5 / 17 / (.05f * .7f), control.Yaw, 5);
        Assert.Equal(-MathF.Abs(pitch) * pitch * 5 / 17 / (.05f * .7f), control.Pitch, 5);
        Assert.Equal(-1, control.Throttle);
    }

    [Theory]
    [InlineData(DockApproach.Mask.Pivot)]
    [InlineData(DockApproach.Mask.KillThrottle)]
    [InlineData(DockApproach.Mask.Enter)]
    [InlineData(DockApproach.Mask.Fast)]
    public void Rearward_goal_uses_native_full_yaw_instead_of_remaining_motionless(uint mask)
    {
        var orientation = new Orientation(); orientation.Set(new Vector3(1, 2, 3));
        var behind = -orientation.GetForward() * 100;
        var step = new DockApproach.Step(mask, 0, behind, behind);
        var controls = DockApproach.Steer(step, Vector3.Zero, orientation, Vector3.Zero, 0);
        // Native common.cpp:168-175 explicitly handles a target almost exactly behind.
        Assert.Equal(1, controls.Yaw);
        Assert.Equal(0, controls.Pitch);
    }

    [Theory]
    [InlineData(0u, 0f, 1f)]
    [InlineData(0u, 20f, 1f)]
    [InlineData(0u, 23f, -1f)]
    [InlineData(0u, 90f, -1f)]
    [InlineData(0u, 180f, -1f)]
    [InlineData(DockApproach.Mask.DodgeShips, 0f, 1f)]
    [InlineData(DockApproach.Mask.DodgeShips, 20f, 1f)]
    [InlineData(DockApproach.Mask.DodgeShips, 23f, -1f)]
    [InlineData(DockApproach.Mask.DodgeShips, 90f, -1f)]
    [InlineData(DockApproach.Mask.DodgeShips, 180f, -1f)]
    public void Seek_kills_forward_throttle_until_within_native_turn_angle(uint mask, float degrees, float expected)
    {
        // common.cpp:2231-2235,2309: throttle starts at -1 and is enabled only
        // below pi/8 radians. Exercise the bay-staging phase.
        float radians = degrees * MathF.PI / 180;
        var goal = new Vector3(MathF.Sin(radians) * 100, 0, -MathF.Cos(radians) * 100);
        var step = new DockApproach.Step(mask, 0, goal, Vector3.Zero);
        var controls = DockApproach.Steer(step, Vector3.Zero, Orientation.Identity, Vector3.Zero, 0,
            turnState: new DockApproach.TurnState(10, 60, .7f, .7f, 17, 17, 0, 0));
        Assert.Equal(expected, controls.Throttle);
    }

    [Theory]
    [InlineData(1000f, 100f, 0f, ShipControlState.RightButton)]
    [InlineData(1000f, 750f, 100f, ShipControlState.RightButton | ShipControlState.UpButton)]
    [InlineData(1000f, 750f, 600f, ShipControlState.RightButton | ShipControlState.UpButton | ShipControlState.BackwardButton)]
    [InlineData(-1000f, -100f, 0f, ShipControlState.LeftButton)]
    public void Native_wide_angle_seek_uses_dominant_axis_thrusters(float x, float y, float z, int expected)
    {
        var step = new DockApproach.Step(0, 0, new(x, y, z), Vector3.Zero);
        var turn = new DockApproach.TurnState(10, 60, .7f, .7f, 17, 17, 0, 0, 150);
        Assert.Equal(expected, DockApproach.MovementButtons(step, Vector3.Zero, Orientation.Identity, Vector3.Zero, turn));
        Assert.Equal(0, DockApproach.MovementButtons(step with { Mask = DockApproach.Mask.Pivot },
            Vector3.Zero, Orientation.Identity, Vector3.Zero, turn));
        // getDirection compensates velocity by 1/k, k=150/(10*60)=.25.
        Assert.Equal(ShipControlState.LeftButton, DockApproach.MovementButtons(
            step with { Goal = new(100, 0, 0) }, Vector3.Zero, Orientation.Identity, new(50, 0, 0), turn));
    }

    [Fact]
    public void Garage_uses_oriented_native_vertices_and_requires_every_subhull_inside()
    {
        // Native GetMinExtreme uses every sub-hull (stationIGC.cpp:396-430),
        // with vertex X flipped by HitTest::Load (HitTest.cpp:735-737).
        var vertices = (from x in new[] { -3f, -1f }
                        from y in new[] { -.2f, .2f }
                        from z in new[] { -.2f, .2f }
                        select new Vector3(x, y, z)).ToArray();
        CvhHull Shape(Vector3 offset) => new()
        {
            Center = Vector3.Zero, VertexCount = 8, AdjacencyCount = 56,
            Vertices = vertices.Select((v, i) => new CvhVertex(v + offset,
                Enumerable.Range(0, 8).Where(j => j != i).ToArray())).ToArray(),
        };
        CvhFile Model(params CvhHull[] shapes) => new()
        {
            Radius = 10, EllipseEquation = new(10, 10, 10), EllipseRadiusMultiplier = 1,
            Hulls = shapes, Frames = [],
        };
        var type = new DataStationTypeIGC { Radius = 10, StationTypeId = 1 };
        var station = new Station(new([0, 0, 0], [0, 1, 0], [0, 0, -1], [0, 0, 0, 1],
            0, 0, 1, 1, 100, 100, "bay"), type);
        var bay = new HullLandingBayGeometry
        {
            Scale = 1, LaunchSlots = [], CapitalLandSlots = 0,
            LandSlots = [new HullLandSlot(new HullLandPlane[]
            {
                new(new(0, 0, 0), new(1, 0, 0)), new(new(10, 0, 0), new(-1, 0, 0)),
                new(new(0, -1, 0), new(0, 1, 0)), new(new(0, 1, 0), new(0, -1, 0)),
                new(new(0, 0, -1), new(0, 0, 1)), new(new(0, 0, 1), new(0, 0, -1)),
            })],
        };
        var hull = new DataHullTypeIGC { Length = 20, HabmCapabilities = DataHullTypeIGC.HabmFighter };
        var ship = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { Hull = Model(Shape(Vector3.Zero)) };
        Assert.True(station.InGarage(ship, Vector3.Zero, bay));
        ship.Orientation.Yaw(MathF.PI / 2);
        Assert.False(station.InGarage(ship, Vector3.Zero, bay));
        ship.Orientation.Reset();
        ship.Hull = Model(Shape(Vector3.Zero), Shape(new(0, 2, 0)));
        Assert.False(station.InGarage(ship, Vector3.Zero, bay));
        ship.Hull = null; // native missing-model sphere fallback cannot fit this narrow bay
        Assert.False(station.InGarage(ship, Vector3.Zero, bay));
        ship.Hull = Model(Shape(new(0, 2, 0))); // stale fighter body after ejection
        ship.ChangeHull(hull with { ModelName = "current-pod" });
        ship.Sector = new Sector(default)
        {
            CollisionHulls = new CollisionHullCache(name =>
            {
                Assert.Equal("current-pod", name);
                return Model(Shape(Vector3.Zero));
            }),
        };
        Assert.True(station.InGarage(ship, Vector3.Zero, bay));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Every_dockable_station_slot_admits_all_ten_native_pods_and_civilization_fighters()
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        Assert.Equal(10, core.Civilizations.Count);
        var failures = new List<string>();
        int flights = 0;
        foreach (var civ in core.Civilizations.Values)
        {
            var home = core.StationTypes[civ.InitialStationTypeId];
            var techs = new ResearchSettings().StartingTechs(civ).Or(TechTreeBitMask.FromBytes(home.TtbmEffects));
            var fighters = core.HullTypes.Values.Where(h => h.HasCapability(DataHullTypeIGC.HabmFighter)
                && !h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.MaxFixedWeapons > 0 && h.Speed > 0
                && TechTreeBitMask.RequiredTechsBuyable(h.TtbmRequired, home.TtbmLocal, techs)).ToArray();
            Assert.NotEmpty(fighters);
            var hulls = fighters.Prepend(core.HullTypes[civ.Lifepod]).ToArray();
            foreach (var type in core.StationTypes.Values.Where(t => t.HasCapability(DataStationTypeIGC.SabmLand)))
            {
                var model = CvhReader.Parse(File.ReadAllText(Path.Combine(art, type.ModelName + ".cvh")));
                var bay = HullLandingBay.ReconstructStation(model, type.Radius);
                Assert.NotEmpty(bay.LandSlots);
                // Native capgarage prefix + total-index garage scan: stationtypeigc.cpp:71-144.
                for (int i = 0; i < bay.LandSlots.Count; i++)
                {
                    string prefix = i < bay.CapLandSlots ? "capgarage" : "garage";
                    var frames = model.Frames.Where(f => f.Name.StartsWith($"{prefix}{i + 1}", StringComparison.Ordinal)).ToArray();
                    Assert.Equal(frames.Length, bay.LandSlots[i].Planes.Count);
                }
                foreach (var hull in hulls)
                    for (int slot = 0; slot < bay.LandSlots.Count; slot++)
                    {
                        foreach (bool facingAway in new[] { false, true })
                        {
                            flights++;
                            var failure = Fly(type, hull, bay, slot, facingAway, art);
                            if (failure is not null) failures.Add($"civ={civ.CivilizationId} facingAway={facingAway} {failure}");
                        }
                    }
            }
            output.WriteLine($"civ={civ.CivilizationId} pod={civ.Lifepod} fighters={string.Join(',', fighters.Select(h => h.HullId))}");
        }
        output.WriteLine($"stationTypes={core.StationTypes.Count} flights={flights} failures={failures.Count}");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(0f, false)]
    [InlineData(90f, false)]
    [InlineData(180f, false)]
    [InlineData(270f, false)]
    [InlineData(0f, true)]
    [InlineData(90f, true)]
    [InlineData(180f, true)]
    [InlineData(270f, true)]
    public void Pod_recovers_from_the_recorded_staging_orbit_neighborhood(float roll, bool ga)
    {
        Require.Artwork();
        var art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var mission = Mission.Assemble(core, MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "XXV_simple.igc"))),
            [new SideSpec(0, "Alpha", 25), new SideSpec(1, "Bravo", 3)], new NullIgcSite());
        short typeId = (short)(ga ? 44 : 115);
        var type = core.StationTypes[typeId];
        var station = new Station(new(ga ? [-1840, -1800, 0] : [1120, 1560, 0], [0, 1, 0], [0, 0, -1], [0, 0, 0, 1],
            0, 0, 1, typeId, 100, 100, "Garrison"), type);
        var bay = HullLandingBay.ReconstructStation(CvhReader.Parse(File.ReadAllText(Path.Combine(art, type.ModelName + ".cvh"))), type.Radius);
        // Actual failed trip's position, velocity and forward. Roll and angular
        // rates were not logged; sweep four rolls with initially zero turn rates.
        var orientation = new Orientation(); orientation.Set(ga ? new(.35855898f, .8737404f, -.32865375f) : new(-.716708f, .6720835f, .18610094f), new(0, 0, 1));
        orientation.Roll(roll * MathF.PI / 180);
        var hull = core.HullTypes[(short)(ga ? 113 : 444)];
        var pod = new Ship(1, hull, ga ? new(-1433.419f, -1805.7692f, -6.575041f) : new(1413.1642f, 1877.825f, -2.3980446f),
            ga ? Vector3.Zero : new(-12.334151f, 9.600494f, -13.038739f), orientation)
        {
            SideId = 0,
            Hull = CvhReader.Parse(File.ReadAllText(Path.Combine(art, hull.ModelName + ".cvh"))),
        };
        mission.PlaceShip(pod, mission.Sectors[0]);
        var bays = bay.LandSlots.Select(s => new DockApproach.DockBay(s.Planes[0].Position,
            s.Planes[0].Direction.Normalize())).ToArray();
        for (double t = 0; t < 60; t += .05)
        {
            float speed = pod.Velocity.Length();
            float rest = DockApproach.StoppingDistance(speed, pod.EffectiveThrust(), pod.Mass,
                pod.EffectiveMaxSpeed(), hull.BackMultiplier);
            var step = DockApproach.DockStation(pod.Position, pod.Velocity, pod.Orientation, station.Center,
                bays, bays.Length, rest, speed > .1f ? pod.Position + pod.Velocity * (rest / speed) : pod.Position,
                type.Radius + hull.Length / 2 + 10, pod.TurnRates[ControlData.AxisYaw], pod.TurnRates[ControlData.AxisPitch], false, ga ? 20121 : 20075);
            pod.Controls = DockApproach.Steer(step, pod.Position, pod.Orientation, pod.Velocity, rest, dt: .05f, turnState: DockApproach.TurnState.FromShip(pod));
            pod.StateM = DockApproach.MovementButtons(step, pod.Position, pod.Orientation, pod.Velocity, DockApproach.TurnState.FromShip(pod));
            pod.ExecuteMove(t, t + .05); pod.Position += pod.Velocity * .05f;
            if ((pod.Position - station.Center).Length() <= type.Radius + hull.Length / 2
                && station.InGarage(pod, pod.Position, bay)) return;
        }
        Assert.Fail($"staging approach never entered a bay, ga={ga} roll={roll} end={pod.Position}");
    }

    private static string? Fly(DataStationTypeIGC type, DataHullTypeIGC hull, HullLandingBayGeometry bay, int slot, bool facingAway, string art)
    {
        // An off-origin rotated station catches local/world frame mistakes.
        var station = new Station(new([73, -91, 52], [0, 0, 1], [1, 1, 0], [0, 0, 0, 1],
            0, 0, 1, type.StationTypeId, 100, 100, type.Name), type);
        var p0 = bay.LandSlots[slot].Planes[0];
        var center = p0.Position * station.Orientation;
        var direction = p0.Direction.Normalize() * station.Orientation;
        var sideways = Vector3.CrossProduct(direction, new Vector3(1, 2, 3)).Normalize();
        var position = station.Center + center + direction * (type.Radius + 100) + sideways * (facingAway ? 0 : 60);
        var orientation = new Orientation();
        orientation.Set((station.Center + center - position).Normalize() * (facingAway ? -1 : 1));
        var ship = new Ship(1, hull, position, facingAway ? Vector3.Zero : orientation.GetForward() * hull.Speed, orientation)
        {
            Hull = CvhReader.Parse(File.ReadAllText(Path.Combine(art, hull.ModelName + ".cvh"))),
        };
        // Restrict the planner to this slot: every slot must be reachable, not just one per model.
        DockApproach.DockBay[] bays = [new(center, direction)];
        var selected = new HullLandingBayGeometry { Scale = bay.Scale, LaunchSlots = [],
            LandSlots = [bay.LandSlots[slot]], CapitalLandSlots = 1 };
        for (double t = 0; t < 300; t += .05)
        {
            float speed = ship.Velocity.Length();
            float rest = DockApproach.StoppingDistance(speed, ship.EffectiveThrust(), ship.Mass,
                ship.EffectiveMaxSpeed(), hull.BackMultiplier);
            var step = DockApproach.DockStation(ship.Position, ship.Velocity, ship.Orientation, station.Center,
                bays, 1, rest, speed > .1f ? ship.Position + ship.Velocity * (rest / speed) : ship.Position,
                type.Radius + hull.Length / 2 + 10, ship.TurnRates[ControlData.AxisYaw], ship.TurnRates[ControlData.AxisPitch], false);
            ship.Controls = DockApproach.Steer(step, ship.Position, ship.Orientation, ship.Velocity, rest, dt: .05f, turnState: DockApproach.TurnState.FromShip(ship));
            ship.StateM = DockApproach.MovementButtons(step, ship.Position, ship.Orientation, ship.Velocity, DockApproach.TurnState.FromShip(ship));
            ship.ExecuteMove(t, t + .05); ship.Position += ship.Velocity * .05f;
            if ((ship.Position - station.Center).Length() <= type.Radius + hull.Length / 2
                && station.InGarage(ship, ship.Position, selected)) return null;
        }
        return $"station={type.StationTypeId}/{type.ModelName} hull={hull.HullId} slot={slot} end={ship.Position}";
    }
}
