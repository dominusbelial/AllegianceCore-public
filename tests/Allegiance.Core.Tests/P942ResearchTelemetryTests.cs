using System.Globalization;
using System.Reflection;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P942ResearchTelemetryTests
{
    [SkippableFact]
    public void The_winning_team_money_completion_is_in_its_own_game_over_summary()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter(); Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(new ServerConfig
            {
                Port = 0,
                Games = [new() { Name = "P942-money", Map = "m2xspiderw.igc", Teams = 2,
                    CivIds = [18, 4], SideNames = ["A", "B"], Goals = new() { TeamMoney = 1 } }],
            });
            var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
            var bucket = mission.Sides[0].Buckets.Single(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId);
            bucket.AddMoney(bucket.Price); bucket.Update(bucket.TimeToBuild + 1);
            Assert.Equal(MissionStage.Over, mission.Stage);
            var trace = log.ToString();
            Assert.Contains("[research-done] side 0 civ 18 'Gigacorp' tech 1 ", trace);
            Assert.Contains("side 0 civ 18: 1 developments, first at ", trace);
            Assert.True(trace.IndexOf("[research-done]", StringComparison.Ordinal) < trace.IndexOf("[research] match", StringComparison.Ordinal));
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableFact]
    public void Real_bucket_completions_log_once_and_summaries_are_scoped_to_each_world_and_rotation()
    {
        Require.Artwork();
        var saved = Console.Out;
        var culture = CultureInfo.CurrentCulture;
        using var log = new StringWriter(); Console.SetOut(log);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(new ServerConfig
            {
                Port = 0,
                Games = [new() { Name = "P942-a", Map = "m2xspiderw.igc", Teams = 2, CivIds = [18, 4], SideNames = ["A", "B"] },
                    new() { Name = "P942-b", Map = "m2xspiderw.igc", Teams = 2, CivIds = [18, 4], SideNames = ["A", "B"] }],
            });
            var mission = server.Missions[0]; mission.Stage = MissionStage.Started;
            var side = mission.Sides[0];
            var first = side.Buckets.Single(b => b.Development?.DevelopmentId == 222);
            var second = side.Buckets.Single(b => b.Development?.DevelopmentId == 75);
            foreach (var (bucket, now) in new[] { (first, 20.125), (second, 200.75) })
            {
                Assert.True(side.CanBuy(bucket));
                bucket.AddMoney(bucket.Price); bucket.Update(now); bucket.Update(now + 1);
                Assert.True(bucket.Completed);
            }
            Assert.Equal(2, log.ToString().Split('\n').Count(l => l.StartsWith("[research-done]")));
            Assert.Contains("[research-done] side 0 civ 18 'Gigacorp' tech 222 'Weedcore 03' at sim-t 20.125", log.ToString());
            Assert.Contains("[research-done] side 0 civ 18 'Gigacorp' tech 75 'EW Disruptor 1' at sim-t 200.75", log.ToString());

            // An obsolete alternative is not an actual completion callback.
            var obsolete = side.Buckets.First(b => b.Development is { } d && d.DevelopmentId != 222 && d.DevelopmentId != 75);
            obsolete.SetTimeAndMoney((long)obsolete.TimeToBuild * 1000, obsolete.Price);
            obsolete.Update(500);
            Assert.Equal(2, log.ToString().Split('\n').Count(l => l.StartsWith("[research-done]")));

            End(server, mission); End(server, mission);
            var summaries = log.ToString().Split('\n').Where(l => l.StartsWith("[research] match ")).ToArray();
            Assert.Equal(2, summaries.Length);
            Assert.Contains(summaries, l => l.Contains("side 0 civ 18: 2 developments, first at 20.125, last at 200.75"));
            Assert.Contains(summaries, l => l.Contains("side 1 civ 4: 0 developments, first at none, last at none"));
            // The other mission and the freshly rebuilt match have no receipts.
            End(server, server.Missions[1]); End(server, server.Missions[0]);
            var later = log.ToString().Split('\n').Where(l => l.StartsWith("[research] match ")).Skip(2).ToArray();
            Assert.Equal(4, later.Length);
            Assert.All(later, l => Assert.Contains("0 developments, first at none, last at none", l));
        }
        finally { CultureInfo.CurrentCulture = culture; Console.SetOut(saved); }
    }

    private static void End(Srv server, Mission mission) => typeof(Srv)
        .GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(server, [mission, (short)0, "P942 telemetry fixture"]);
}
