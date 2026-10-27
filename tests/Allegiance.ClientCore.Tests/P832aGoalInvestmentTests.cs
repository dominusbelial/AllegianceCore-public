using System.Globalization;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P832aGoalInvestmentTests
{
    [SkippableFact]
    public void Generic_invest_uses_the_configured_prosperity_price_received_over_tcp()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = P660NextMatchLiveTests.Config(false);
        config.Games[0].Goals = new() { TeamMoney = 2.5f };
        config.Games[0].MaxTeamImbalance = 0x7fff; // one-side investment fixture; no opposing pilot needed
        config.LiveSettings!.TestHarness!.CharacterNamePrefix = "research-"; // fund the real generic-invest commander
        server.Boot(config);
        server.Missions[0].Stage = MissionStage.Started;
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var bucket = server.Missions[0].Sides[0].Buckets.Single(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId);
            var result = manager.Invest(0, bucket.Id.ToString(CultureInfo.InvariantCulture), null, 1.5);
            Assert.Equal((int)(2.5f * server.Missions[0].Core.Constants.FloatConstants[12]), result.Price);
            Assert.Equal(result.Price, result.AmountRequested);
            Assert.Equal(result.Price, result.InvestedMoney);
            Assert.Equal(result.Price, bucket.InvestedMoney);
        }
        finally { stop.Cancel(); ticker.Join(); }
    }
}
