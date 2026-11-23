using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P1077 — the PORT-ONLY constructor-stall safety net in the research commander. A station step whose
/// constructor makes no measurable progress toward its reserved rock for
/// <see cref="ResearchCommander.StationNoProgressSeconds"/> releases the reservation and retargets
/// another compatible rock; with no alternative it defers the step (logging it) and the plan moves on.
/// A rock that reports <c>Building</c> — the constructor is drilling/constructing — is never abandoned.
/// Native has no counterpart (a human commander re-tasks by hand, <c>FedSrv/FedSrv.CPP:3278-3285</c>).
/// </summary>
[Collection("P1077Stall")]
public sealed class P1077CommanderStallTests
{
    [CollectionDefinition("P1077Stall", DisableParallelization = true)]
    public sealed class StallCollection;

    private sealed class Fixture
    {
        internal readonly ResearchTree Tree = new(Catalog.RepresentativeCore(), 1);
        internal readonly ResearchPlan Plan;
        internal readonly Dictionary<short, short> Reservations = new();
        internal readonly ResearchCommander Commander;
        internal ResearchCommander.Snapshot World;
        internal short Station => Plan.Steps[1].Id;
        internal short DroneType => Tree.Core.StationTypes[Station].ConstructionDroneTypeId;

        internal Fixture()
        {
            var state = new ResearchState(Tree);
            var dev = Tree.Nodes.First(n => n.Kind == "develop" && state.CanApply(n));
            var develop = new ResearchStep { Action = "develop", Id = dev.Id, Name = dev.Name };
            state.Apply(develop);
            var station = Tree.Nodes.First(n => n.Kind == "station" && n.Id != Tree.Civ.InitialStationTypeId && state.CanApply(n));
            var build = new ResearchStep { Action = "station", Id = station.Id, Name = station.Name,
                AsteroidClass = (short)(station.AsteroidMask & -station.AsteroidMask) };
            state.Apply(build);
            var drone = Tree.Nodes.First(n => n.Kind == "drone" && state.CanApply(n));
            Plan = new() { Core = "PCore001", CoreVersion = Tree.Core.Version, CivId = 1, Name = "p1077",
                Steps = [develop, build, new() { Action = "drone", Id = drone.Id, Name = drone.Name },
                    new() { Action = "wait", Name = "hold", WaitSeconds = 10, RequiredTechs = state.Techs.ToBytes() }] };
            Commander = new(0, "epoch", Tree, Plan, Tree.Settings.StartingTechs(Tree.Civ), Reservations);
            World = new(1, true, 1000000, Tree.Settings.StartingTechs(Tree.Civ), [Tree.Civ.InitialStationTypeId], [], [],
                [new(10, station.AsteroidMask, true, Sector: 1, Position: [1000f, 0f, 0f]),
                 new(11, station.AsteroidMask, true, Sector: 1, Position: [-1000f, 0f, 0f])],
                new Dictionary<short, ResearchCommander.Funding>());
        }

        internal IReadOnlyList<ResearchCommander.Command> Tick() => Commander.Tick(World);

        /// <summary>Finish the plan's first (development) step and land on the station step at Time 4
        /// with one constructor and two compatible rocks, one tick away from the first BUILD order.
        /// <paramref name="withPosition"/> false models a snapshot with no wire pose.</summary>
        internal void ReachStation(bool withPosition = true, float x = 0f, float y = 0f, float z = 0f)
        {
            Assert.Contains(Tick(), c => c.Kind == "donate");
            var dev = Tree.Core.Developments[Plan.Steps[0].Id];
            World = World with { Time = 2, CompletedDevelopments = [dev.DevelopmentId],
                Developments = World.Developments.Or(TechTreeBitMask.FromBytes(dev.TtbmEffects)) };
            Tick();                                                          // observes the completed development
            Tick();                                                          // station step reserves rock 10
            World = World with { Time = 4, Units = [new(100, DroneType, Station, true, 1,
                withPosition ? [x, y, z] : null)] };
        }
    }

    private static string Capture(Action action)
    {
        var previous = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            action();
            return writer.ToString();
        }
        finally { Console.SetOut(previous); }
    }

    [SkippableFact]
    public void Stalled_constructor_releases_the_rock_and_retargets_another_compatible_rock()
    {
        Require.Artwork();
        var f = new Fixture();
        f.ReachStation();
        Assert.Equal((short)0, f.Reservations[10]);                        // first home-sector rock reserved
        Assert.Equal((short)10, Assert.Single(f.Tick(), c => c.Kind == "build").Target);

        // The constructor never moves: 180 s without progress moves the reservation to rock 11.
        IReadOnlyList<ResearchCommander.Command> commands = [];
        f.World = f.World with { Time = 185 };
        string log = Capture(() => commands = f.Tick());
        Assert.Contains(commands, c => c.Kind == "build" && c.Target == 11);
        Assert.False(f.Reservations.ContainsKey(10));
        Assert.Equal((short)0, f.Reservations[11]);
        Assert.Contains("[constructor-stall]", log);
        Assert.Contains("side=0", log);
        Assert.Contains("constructor=100", log);
        Assert.Contains("rock=10", log);
        Assert.Contains("no-progress=18", log);
    }

    [SkippableFact]
    public void Shrinking_distance_is_progress_and_never_triggers_the_net()
    {
        Require.Artwork();
        var f = new Fixture();
        f.ReachStation(x: 100000f);
        Assert.Equal((short)10, Assert.Single(f.Tick(), c => c.Kind == "build").Target);
        // Close 100 units per sample for well past the window, never arriving (rock 10 sits at x=1000).
        for (int i = 0; i <= 400; i++)
        {
            float x = 100000f - i * 100f;
            f.World = f.World with
            {
                Time = 4 + i * 5,
                Units = [new(100, f.DroneType, f.Station, true, 1, [x, 0f, 0f])],
            };
            var commands = f.Tick();
            Assert.DoesNotContain(commands, c => c.Kind == "build" && c.Target == 11);
            Assert.Equal((short)0, f.Reservations[10]);
        }
    }

    [SkippableFact]
    public void A_rock_reporting_building_is_never_abandoned()
    {
        Require.Artwork();
        var f = new Fixture();
        f.ReachStation();
        Assert.Equal((short)10, Assert.Single(f.Tick(), c => c.Kind == "build").Target);
        f.World = f.World with { Rocks = [f.World.Rocks[0] with { Building = true }, f.World.Rocks[1]], Time = 6 };
        f.Tick();
        string log = Capture(() =>
        {
            for (int i = 0; i < 80; i++)
            {
                f.World = f.World with { Time = 10 + i * 60 };
                Assert.DoesNotContain(f.Tick(), c => c.Kind == "build");   // drilling: never re-ordered/abandoned
            }
        });
        Assert.DoesNotContain("[constructor-stall]", log);
        Assert.Equal((short)0, f.Reservations[10]);
        Assert.False(f.Reservations.ContainsKey(11));
    }

    [SkippableFact]
    public void No_alternative_rock_defers_the_step_then_retries_it_once_later()
    {
        Require.Artwork();
        var f = new Fixture();
        f.ReachStation();
        f.World = f.World with { Rocks = [f.World.Rocks[0]] };             // only one compatible rock exists
        Assert.Equal((short)10, Assert.Single(f.Tick(), c => c.Kind == "build").Target);

        string log = Capture(() => { f.World = f.World with { Time = 185 }; f.Tick(); });
        Assert.Contains("[constructor-stall]", log);
        Assert.Contains("no other compatible rock", log);
        Assert.Contains("deferred", log);
        Assert.Empty(f.Reservations);                                     // the reservation is released
        f.World = f.World with { Time = 187 }; f.Tick();
        Assert.Equal(f.Plan.Steps[2].Name, f.Commander.Telemetry!.Name);   // the plan moved past the step

        // After StationRetrySeconds the deferred station step is retried once through the
        // unbuilt-station path: it re-reserves the rock and re-orders the constructor.
        f.World = f.World with { Time = 185 + ResearchCommander.StationRetrySeconds + 2 };
        var retry = f.Tick();
        Assert.Equal("station", f.Commander.Telemetry!.Action);
        Assert.Equal(f.Tree.Core.StationTypes[f.Station].Name, f.Commander.Telemetry.Name);
        Assert.Equal((short)10, Assert.Single(retry, c => c.Kind == "build").Target);

        // A second failure spends the retry budget: the step is dropped, never re-armed.
        log = Capture(() => { f.World = f.World with { Time = 185 + ResearchCommander.StationRetrySeconds + 200 }; f.Tick(); });
        Assert.Contains("retry already spent", log);
        f.World = f.World with { Time = 185 + 3 * ResearchCommander.StationRetrySeconds }; f.Tick();
        Assert.Empty(f.Reservations);
    }

    [SkippableFact]
    public void Without_wire_positions_only_the_hard_step_cap_moves_the_step()
    {
        Require.Artwork();
        var f = new Fixture();
        f.ReachStation(withPosition: false);
        f.World = f.World with
        {
            Rocks = [f.World.Rocks[0] with { Position = null }, f.World.Rocks[1] with { Position = null }],
        };
        Assert.Equal((short)10, Assert.Single(f.Tick(), c => c.Kind == "build").Target);
        f.World = f.World with { Time = 400 };
        Assert.DoesNotContain(f.Tick(), c => c.Kind == "build" && c.Target == 11);   // 180 s timer is unarmed
        string log = Capture(() => { f.World = f.World with { Time = 610 }; f.Tick(); });
        Assert.Contains("[constructor-stall]", log);
        Assert.Contains("no-progress=", log);
        Assert.Equal((short)0, f.Reservations[11]);
    }
}
