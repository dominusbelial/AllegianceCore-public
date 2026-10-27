using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Core.Transport;
using Allegiance.ClientCore.Session;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

public sealed class P832bProsperityTests(ITestOutputHelper output)
{
    private static (ResearchCommander Commander, ResearchCommander.Snapshot World, ResearchTree Tree) Fixture(float multiplier = .01f)
    {
        var tree = new ResearchTree(Catalog.RepresentativeCore(), 25);
        var techs = tree.Settings.StartingTechs(tree.Civ);
        var plan = tree.Generate("PCore001");
        return (new(0, "p832b", tree, plan, techs, new()),
            new(0, true, 1_000_000, techs, [tree.Civ.InitialStationTypeId], [], [], [], new Dictionary<short, ResearchCommander.Funding>(),
                new() { TeamMoney = multiplier }), tree);
    }

    [SkippableFact]
    public void Goal_funding_uses_configured_price_keeps_defence_and_waits_for_wallet_ack()
    {
        Require.Artwork(); var (commander, world, tree) = Fixture();
        var buckets = BucketCatalog.Describe(tree.Core, world.Developments, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes), world.Goals!.TeamMoney);
        var goal = buckets.Single(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        var donation = Assert.Single(commander.Tick(world), c => c.Kind == "donate");
        Assert.Equal(goal.Id, donation.Id); Assert.Equal(goal.Price, donation.Amount);
        Assert.True(world.Wallet - donation.Amount > 0);
        Assert.Empty(commander.Tick(world));
        var statusOnly = world with { Time = 1, FundingByBucket = new Dictionary<short, ResearchCommander.Funding> { [goal.Id] = new(donation.Amount, 0) } };
        Assert.Empty(commander.Tick(statusOnly));
        Assert.Contains("acknowledgement", commander.Telemetry!.Status);
        commander.Tick(statusOnly with { Time = 2, Wallet = world.Wallet - donation.Amount });
        Assert.Equal(world.Wallet - donation.Amount, commander.Telemetry!.Money);
        Assert.DoesNotContain(commander.Tick(statusOnly with { Time = 3, Wallet = 0 }), c => c.Kind == "donate");
    }

    [SkippableFact]
    public void Observed_He3_grows_miners_first_and_losses_trigger_replacement()
    {
        Require.Artwork(); var (commander, world, tree) = Fixture();
        world = world with { Rocks = [new(1, DataAsteroidIGC.AabmMineHe3, true)] };
        var catalog = BucketCatalog.Describe(tree.Core, world.Developments, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes));
        var buy = Assert.Single(commander.Tick(world), c => c.Kind == "donate");
        var miner = catalog.Single(b => b.Id == buy.Id);
        Assert.Equal(Ship.PilotTypeMiner, miner.DroneType!.Value.PilotType);
        Assert.Equal(miner.Price, buy.Amount);
        world = world with { Time = 1, Wallet = world.Wallet - buy.Amount,
            Units = [new(100, miner.DroneTypeId, -1, true), new(101, miner.DroneTypeId, -1, true)] };
        Assert.Equal(catalog.Single(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId).Id,
            Assert.Single(commander.Tick(world), c => c.Kind == "donate").Id);
        world = world with { Time = 7, Units = [new(100, miner.DroneTypeId, -1, true), new(101, miner.DroneTypeId, -1, false)] };
        Assert.Equal(miner.Id, Assert.Single(commander.Tick(world), c => c.Kind == "donate").Id);
        var (dryCommander, dry, _) = Fixture();
        dry = dry with { Rocks = [new(1, DataAsteroidIGC.AabmMineHe3, true, Ore: 0)] };
        Assert.NotEqual(miner.Id, Assert.Single(dryCommander.Tick(dry), c => c.Kind == "donate").Id);
        using var observer = new ConnectionSession();
        void Feed(IFedMessage message) => ((ITransportSite)observer).OnReceive(ConnectionId.Server, FedPacket.Pack(message.Encode()));
        Feed(new ExportAsteroid { Asteroid = new(1, [0,0,0], [0,1,0], [1,0,0], [0,0,0,1],
            100, 100, DataAsteroidIGC.AabmMineHe3, 10, 1000, 60, "rock", "", "", 0, "He3", 240) });
        Assert.Equal(100, Assert.Single(BotManager.ResearchSnapshot(observer, 0).Rocks).Ore);
        Feed(new AsteroidsUpdate { Entries = [new(10, CompactOrientation.Identity, 0, 240)] });
        Assert.Equal(0, Assert.Single(BotManager.ResearchSnapshot(observer, 0).Rocks).Ore);
    }

    [SkippableFact]
    public void Far_goal_banks_income_without_draining_the_reserve_on_repeated_ticks()
    {
        Require.Artwork(); var (commander, world, tree) = Fixture(9);
        world = world with { Wallet = 0 };
        Assert.DoesNotContain(commander.Tick(world), c => c.Kind == "donate");
        var goal = BucketCatalog.Describe(tree.Core, world.Developments, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes))
            .Single(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        for (int i = 1; i <= 40; i++)
        {
            world = world with { Time = i * 10, Wallet = world.Wallet + 100 };
            foreach (var command in commander.Tick(world).Where(c => c.Kind == "donate"))
            {
                Assert.NotEqual(goal.Id, command.Id);
                world = world with { Wallet = world.Wallet - command.Amount,
                    FundingByBucket = new Dictionary<short, ResearchCommander.Funding>(world.FundingByBucket)
                        { [command.Id] = new((world.FundingByBucket.GetValueOrDefault(command.Id)?.Money ?? 0) + command.Amount, 0) } };
            }
        }
        Assert.True(world.Wallet > 2000, $"Reserve/savings were drained: {world.Wallet}");
    }

    [Fact]
    public void Enemy_progress_redirects_raiders_to_visible_miners_but_keeps_home_interception_and_support()
    {
        ResearchTactics.Pilot[] pilots = [new(2, 0, default, false, false), new(3, 0, default, false, false), new(4, 0, default, true, false)];
        ResearchTactics.Target[] targets = [new(10, IgcObjectType.Ship, 0, new(10,0,0), false),
            new(11, IgcObjectType.Ship, 1, default, false, Miner: true),
            new(12, IgcObjectType.Ship, 0, default, true, Damaged: true)];
        var baseline = new ResearchTactics().Tick(0, true, pilots, targets);
        Assert.Equal((short)10, Assert.Single(baseline, o => o.Recipient == 2).Target);
        var reacting = new ResearchTactics();
        var raid = reacting.Tick(0, true, pilots, targets, enemyProsperityProgress: .1);
        Assert.True(reacting.EconomicRaidActive);
        Assert.Equal((short)11, Assert.Single(raid, o => o.Recipient == 2).Target);
        Assert.Equal((short)10, Assert.Single(raid, o => o.Recipient == 3).Target);
        var support = reacting.Tick(3, true, [pilots[2]], targets, enemyProsperityProgress: .1);
        Assert.Equal(OrderChange.CidRepair, Assert.Single(support).Command);
        var threat = new ResearchTactics.Target(13, IgcObjectType.Ship, 0, default, false, Bomber: true);
        var station = new ResearchTactics.Target(14, IgcObjectType.Station, 0, default, true);
        var defending = new ResearchTactics().Tick(0, true, [pilots[0], pilots[1]], [.. targets, threat, station], enemyProsperityProgress: 1);
        Assert.All(defending, order => Assert.Equal((short)13, order.Target));
        output.WriteLine(JsonSerializer.Serialize(new { baseline, raid, support, defending }));
    }

    [SkippableFact]
    public void Enemy_progress_uses_received_mission_and_bucket_messages_and_ignores_allies_and_other_goals()
    {
        Require.Artwork();
        var (_, world, tree) = Fixture();
        using var peer = new ConnectionSession();
        void Feed(IFedMessage message) => ((ITransportSite)peer).OnReceive(ConnectionId.Server, FedPacket.Pack(message.Encode()));
        Feed(new MissionDef { Goals = world.Goals! });
        Feed(new ResearchBriefing { SideId = 0, Plan = tree.Generate("PCore001") });
        Feed(new ChangeTeamCiv { SideId = 1, CivId = 3 });
        var enemy = tree.Core.Civilizations[3];
        var bucket = BucketCatalog.Describe(tree.Core, tree.Settings.StartingTechs(enemy), GlobalAttributeSet.FromArray(enemy.GasBaseAttributes), .01f)
            .Single(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId);
        Assert.Equal(0, BotManager.ObserveEnemyProsperity(peer.World, 0, tree.Core));
        Feed(new BucketStatus { SideId = 1, IBucket = bucket.Id, MoneyTotal = bucket.Price / 2 });
        Assert.Equal(.5, BotManager.ObserveEnemyProsperity(peer.World, 0, tree.Core));
        Assert.Equal(0, BotManager.ObserveEnemyProsperity(peer.World, 1, tree.Core));
        Feed(new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] });
        Assert.Equal(0, BotManager.ObserveEnemyProsperity(peer.World, 0, tree.Core));
        Feed(new ChangeAlliances { Allies = [-1, -1, -1, -1, -1, -1] });
        Feed(new MissionDef { Goals = new() { Flags = 1 } });
        Assert.Equal(0, BotManager.ObserveEnemyProsperity(peer.World, 0, tree.Core));
        peer.World.Reset();
        Assert.Equal(0, BotManager.ObserveEnemyProsperity(peer.World, 0, tree.Core));
    }

    [SkippableFact]
    public void Other_goals_keep_the_same_commander_trace_as_absent_goals()
    {
        Require.Artwork(); var (_, source, tree) = Fixture();
        string Trace(MissionGoals? goals)
        {
            var commander = new ResearchCommander(0, "other-goals", tree, tree.Generate("PCore001"), source.Developments, new());
            return string.Join('\n', Enumerable.Range(0, 20).Select(i => JsonSerializer.Serialize(
                new { Commands = commander.Tick(source with { Time = i, Goals = goals }), commander.Telemetry })));
        }
        string baseline = Trace(null);
        Assert.Equal(baseline, Trace(new()));
        Assert.Equal(baseline, Trace(new() { Flags = 1, Artifacts = 2, TeamKills = 3, ConquestPercentage = 100, GameLengthSeconds = 600 }));
    }

    // This test also runs against the untouched dispatch-base MCP binary. Keep
    // the original Snapshot constructor and pre-P832b members in this trace.
    [SkippableFact]
    public void No_goal_decision_trace_matches_dispatch_base()
    {
        Require.Artwork();
        var tree = new ResearchTree(Catalog.RepresentativeCore(), 25);
        var plan = tree.Generate("PCore001");
        var techs = tree.Settings.StartingTechs(tree.Civ);
        var commander = new ResearchCommander(0, "trace", tree, plan, techs, new());
        var rows = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            var world = new ResearchCommander.Snapshot(i * 2, i > 0, i < 3 ? 0 : 10000, techs,
                [tree.Civ.InitialStationTypeId], [], [], [], new Dictionary<short, ResearchCommander.Funding>());
            rows.Add(JsonSerializer.Serialize(new { Commands = commander.Tick(world), commander.Telemetry }));
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
        output.WriteLine($"dispatch-base trace SHA256={hash}");
        Assert.Equal("1DFA06A9A3DDFF950DE89ABBC20FFCF913C9065297C3D56300B473B6D08FBD64", hash);
    }
}
