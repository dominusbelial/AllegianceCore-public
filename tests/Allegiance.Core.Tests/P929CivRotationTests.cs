using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P929CivRotationTests
{
    [Fact]
    public void Off_is_the_default_and_alternate_shifts_one_side_per_match()
    {
        var game = JsonSerializer.Deserialize("""{"teams":2}""", ServerJsonContext.Default.GameConfig)!;
        Assert.Equal("off", game.CivRotation);
        int[] civs = [18, 4];
        Assert.Same(civs, MatchCivilizations.Rotate(game, civs, 2));
        Assert.Equal("", MatchCivilizations.Describe(game, 2));

        game = JsonSerializer.Deserialize("""{"teams":2,"civRotation":"alternate"}""", ServerJsonContext.Default.GameConfig)!;
        Assert.Same(civs, MatchCivilizations.Rotate(game, civs, 1));
        Assert.Equal([4, 18], MatchCivilizations.Rotate(game, civs, 2));
        Assert.Equal([18, 4], MatchCivilizations.Rotate(game, civs, 3));
        Assert.Equal(" civRotation=alternate civShift=1", MatchCivilizations.Describe(game, 2));

        game.Teams = 3;
        Assert.Equal([4, 5, 18], MatchCivilizations.Rotate(game, [18, 4, 5], 2));
        Assert.Equal([5, 18, 4], MatchCivilizations.Rotate(game, [18, 4, 5], 3));
        Assert.Equal([18, 4, 5], MatchCivilizations.Rotate(game, [18, 4, 5], 4));

        game.CivRotation = "typo";
        Assert.Throws<InvalidDataException>(() => MatchCivilizations.Rotate(game, civs, 1));
    }

    [SkippableTheory, Trait("Tier", "Live")]
    [InlineData(false)]
    [InlineData(true)]
    public void Alternate_swaps_civilizations_and_home_station_types_across_matches(bool generated)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter(); Console.SetOut(log);
        try
        {
            using var server = new Srv();
            var game = new GameConfig { Name = "P929", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], CivSeed = 929,
                SideNames = ["Alpha", "Bravo"], CivRotation = "alternate", Goals = new() { Flags = 10 } };
            if (generated) game.MapGen = new() { Type = "SingleRing", Seed = 929 };
            server.Boot(new() { Port = 0, LiveSettings = new() { AutoNextMatch = new() { Enabled = false } }, Games = [game] });
            for (int match = 1; match <= 3; match++)
            {
                short[] expected = match % 2 == 1 ? [25, 3] : [3, 25];
                var mission = server.Missions[0];
                foreach (var side in mission.Sides)
                {
                    Assert.Equal(expected[side.Id], side.Civilization.CivilizationId);
                    var restart = Assert.Single(mission.Sectors.SelectMany(s => s.StationRuntimes),
                        s => s.SideId == side.Id && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
                    Assert.Equal(side.Civilization.InitialStationTypeId, restart.Type.StationTypeId);
                }
                string line = log.ToString().Split('\n').Last(l => l.StartsWith("[matchup]"));
                Assert.Contains($"side0={expected[0]}:", line);
                Assert.Contains($"side1={expected[1]}:", line);
                Assert.Contains($"homeMatch={match} homes=0,1", line);
                Assert.Contains($"civRotation=alternate civShift={(match - 1) % 2}", line);
                mission.Stage = MissionStage.Started;
                typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [mission, (short)0, "P929 rotation"]);
                Assert.NotSame(mission, server.Missions[0]);
            }
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { Console.SetOut(saved); }
    }
}
