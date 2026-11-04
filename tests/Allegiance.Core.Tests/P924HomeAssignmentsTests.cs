using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P924HomeAssignmentsTests
{
    [Fact]
    public void Json_defaults_and_validation_are_explicit()
    {
        var game = JsonSerializer.Deserialize("{}", ServerJsonContext.Default.GameConfig)!;
        Assert.Equal("off", game.HomeSwap);
        Assert.False(HomeAssignments.Swapped(game, 1));
        Assert.False(HomeAssignments.Swapped(game, 1, debugMode: true));
        game = JsonSerializer.Deserialize("""{"homeSwap":"alternate","teams":2}""", ServerJsonContext.Default.GameConfig)!;
        Assert.False(HomeAssignments.Swapped(game, 1));
        Assert.True(HomeAssignments.Swapped(game, 2));
        Assert.False(HomeAssignments.Swapped(game, 3));
        game.HomeSwap = "typo";
        Assert.Throws<InvalidDataException>(() => HomeAssignments.Swapped(game, 1));
        game.HomeSwap = "on"; game.Teams = 3;
        Assert.Throws<InvalidDataException>(() => HomeAssignments.Swapped(game, 1));
        game.Teams = 2;
        Assert.Throws<InvalidDataException>(() => HomeAssignments.Swapped(game, 1, debugMode: true));
        game.HomeSwap = "alternate";
        Assert.Throws<InvalidDataException>(() => HomeAssignments.Swapped(game, 1, debugMode: true));
    }

    [SkippableFact]
    public void Off_preserves_bytes_and_swap_changes_only_ownership_of_every_station()
    {
        Require.Artwork();
        var path = Path.Combine(TestArtRoot.Value, "m2xspiderw.igc");
        var fileBytes = File.ReadAllBytes(path);
        var map = MissionMap.Load(IgcReader.ParseFile(path));
        // Add a second base per owner and a neutral station to catch first-home-only remaps.
        map = MissionMap.FromLists(map.Clusters, map.Warps,
            [.. map.Stations, map.Stations[0] with { StationId = 30000 },
                map.Stations[1] with { StationId = 30001 }, map.Stations[0] with { SideId = -1, StationId = 30002 }], map.Asteroids);
        var bytes = map.Stations.SelectMany(s => s.Encode()).ToArray();
        Assert.Same(map, HomeAssignments.Apply(map, false));
        Assert.Equal(bytes, HomeAssignments.Apply(map, false).Stations.SelectMany(s => s.Encode()).ToArray());
        var swapped = HomeAssignments.Apply(map, true);
        Assert.Same(map.Clusters, swapped.Clusters);
        Assert.Same(map.Warps, swapped.Warps);
        Assert.Same(map.Asteroids, swapped.Asteroids);
        for (int i = 0; i < map.Stations.Count; i++)
        {
            var before = map.Stations[i];
            Assert.Equal(before.SideId < 0 ? before.SideId : 1 - before.SideId, swapped.Stations[i].SideId);
            Assert.Equal(before.Encode(), (swapped.Stations[i] with { SideId = before.SideId }).Encode());
        }
        Assert.Equal(bytes, HomeAssignments.Apply(swapped, true).Stations.SelectMany(s => s.Encode()).ToArray());
        Assert.Equal(bytes, map.Stations.SelectMany(s => s.Encode()).ToArray());
        Assert.Equal(fileBytes, File.ReadAllBytes(path));
    }

    [SkippableTheory, Trait("Tier", "Live")]
    [InlineData("off", false)]
    [InlineData("on", false)]
    [InlineData("alternate", false)]
    [InlineData("on", true)]
    public void Assembly_and_rotation_move_homes_miners_flags_spawns_and_research_together(string mode, bool generated)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter(); Console.SetOut(log);
        try
        {
            using var server = new Srv();
            var game = new GameConfig { Name = "P924", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = [25, 3], CivSeed = 924, SideNames = ["Alpha", "Bravo"], HomeSwap = mode,
                ShowHomeSector = true, Goals = new() { Flags = 10 },
                ResearchPlanDirectory = Path.Combine(AppContext.BaseDirectory, "../../../../../data/showcase-plans"),
                ResearchPlans = new() { [0] = "plan_a", [1] = "plan_a" } };
            if (generated) game.MapGen = new() { Type = "SingleRing", Seed = 924 };
            server.Boot(new() { Port = 0, Showcase = new() { TechMode = "research" },
                LiveSettings = new() { AutoNextMatch = new() { Enabled = false } }, Games = [game] });
            short[] authoredHomes = generated ? [0, 2] : [1050, 1051];
            for (int match = 1; match <= 4; match++)
            {
                bool swapped = mode == "on" || (mode == "alternate" && match % 2 == 0);
                var mission = server.Missions[0];
                mission.SeedStartingVisibility();
                foreach (var side in mission.Sides)
                {
                    short expected = authoredHomes[swapped ? 1 - side.Id : side.Id];
                    var home = mission.Sectors.Single(s => s.Id == expected);
                    var station = Assert.Single(home.StationRuntimes, s => s.SideId == side.Id && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
                    Assert.Equal(side.Civilization.InitialStationTypeId, station.Type.StationTypeId);
                    Assert.All(mission.Sectors.SelectMany(s => s.StationRuntimes).Where(s => s.SideId == side.Id), s => Assert.Equal(expected, s.Data.ClusterId));
                    var miner = Assert.Single(mission.GetShips(), s => s.SideId == side.Id && s.PilotType == Ship.PilotTypeMiner);
                    Assert.Equal(expected, miner.Sector!.Id);
                    Assert.Equal(station.StationId, miner.StationId);
                    Assert.Equal(side.Civilization.CivilizationId, mission.ResearchPlans[side.Id].CivId);
                    Assert.All(home.Warps, w => Assert.True(w.SeenBySide(side.Id)));
                    foreach (var requirement in mission.ResearchPlans[side.Id].Steps.Where(s => s.Action == "station")
                        .GroupBy(s => s.AsteroidClass!.Value))
                        Assert.True(home.Asteroids.Count(a => (a.AabmCapabilities & requirement.Key) != 0) >= requirement.Count() + 1);
                    // Production side-selection repositioning must use the receiving base's geometry.
                    var ship = new Ship((short)(30000 + side.Id), miner.HullType, Vector3.Zero, Vector3.Zero, Orientation.Identity);
                    Assert.Same(home, server.RepositionShipToSideHome(ship, mission, side.Id));
                    Assert.Equal(station.Center + new Vector3(420, 0, 0), ship.Position);
                    home.Remove(ship);
                    Assert.Equal(2, home.Models.OfType<Treasure>().Count(t => t.Data.TreasureCode == TreasureCode.Flag && t.Data.TreasureId == side.Id));
                }
                string line = log.ToString().Split('\n').Last(l => l.StartsWith("[matchup]"));
                Assert.Contains("seed=", line);
                Assert.Contains($"homeSwap={mode} homeMatch={match} homes={(swapped ? "1,0" : "0,1")}", line);
                Assert.Contains($"home0={authoredHomes[swapped ? 1 : 0]} home1={authoredHomes[swapped ? 0 : 1]}", line);
                mission.Stage = MissionStage.Started;
                typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [mission, (short)0, "P924 rotation"]);
                Assert.NotSame(mission, server.Missions[0]);
            }
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { Console.SetOut(saved); }
    }
}
