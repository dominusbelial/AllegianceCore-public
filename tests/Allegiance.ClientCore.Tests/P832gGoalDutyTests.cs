using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableFact]
    public void P832g_Mission_migration_clears_flag_artifact_conquest_and_kill_duties()
    {
        Require.Artwork();
        using var cs = Session(true);
        var bot = Bot(cs); var manager = Manager(bot);
        var goals = new MissionGoals[] { new() { Flags = 1 }, new() { Artifacts = 1 },
            new() { ConquestPercentage = 100 }, new() { TeamKills = 20 }, new() { TeamMoney = 1 }, new() };
        for (int match = 0; match < goals.Length; match++)
        {
            // Seed the previous world's live state, then use the production migration
            // detector (generation/cookie/ship identity), not a standalone reset helper.
            Set(bot, "LifeSession", cs);
            Set(bot, "LifeGeneration", cs.World.MissionGeneration);
            Set(bot, "LifeCookie", cs.World.Mission?.Cookie);
            Set(bot, "FlagTask", BotManager.FlagDuty.Return);
            Set(bot, "FlagAnchor", (short?)999);
            Set(bot, "FlagTrace", "Board:999");
            Set(bot, "ArtifactSearchRound", 42);
            Set(bot, "KillRecovering", true);
            Set(bot, "KillTrace", "Recover:999");
            Set(bot, "TargetId", (short?)999);
            cs.World.Reset();
            cs.World.Apply(new MissionDef { DwCookie = (uint)(832 + match), Goals = goals[match] });
            Seat(cs.World, id: (short)(match + 2));
            Call("RefreshLife", manager, bot, cs, (double)match);
            Assert.Equal(BotManager.FlagDuty.None, Get<BotManager.FlagDuty>(bot, "FlagTask"));
            Assert.Null(Get<short?>(bot, "FlagAnchor"));
            Assert.Null(Get<short?>(bot, "TargetId"));
            Assert.Equal("", Get<string>(bot, "FlagTrace"));
            Assert.Equal(0, Get<int>(bot, "ArtifactSearchRound"));
            Assert.False(Get<bool>(bot, "KillRecovering"));
            Assert.Equal("", Get<string>(bot, "KillTrace"));
            Assert.False(Get<bool>(bot, "ConquestStrike"));
            Assert.False(Get<bool>(bot, "FinaleBomberReserved"));
            Assert.False(Get<bool>(bot, "FinaleBomberActive"));
            Assert.Null(Get<object?>(bot, "Finale"));
            Assert.Equal(goals[match].IsFlagsGame, (bool)Call("FlagsGame", null, cs)!);
            Assert.Equal(goals[match].IsArtifactsGame, (bool)Call("ArtifactsGame", null, cs)!);
            Assert.Equal(goals[match].IsConquestGame, (bool)Call("ConquestGame", null, cs)!);
            Assert.Equal(goals[match].IsDeathMatchGame, (bool)Call("KillsGame", null, cs)!);
            Assert.Equal(goals[match], cs.World.MissionGoals);
            // Complete the fixture's join barrier, then exercise the SAME candidate
            // builder and strategy selector as the live supervisor/worker loop.
            Set(bot, "ObjectivesPending", false);
            ObjectivePass(manager, match * 2.0 + 1, bot);
            var duty = Get<BotManager.FlagDuty>(bot, "FlagTask");
            if (match < 4) Assert.NotEqual(BotManager.FlagDuty.None, duty);
            else Assert.Equal(BotManager.FlagDuty.None, duty);
            if (goals[match].IsConquestGame)
            {
                // Build the production temporary strike context. The next match
                // must discard its old station plan as well as its duty flags.
                cs.World.Apply(new ExportStation { Station = Station(90, 1, 1) });
                Set(bot, "FlagTask", BotManager.FlagDuty.Siege);
                Set(bot, "FlagAnchor", (short?)90);
                Call("UpdateConquestStrike", manager, bot, (double)match);
                Assert.True(Get<bool>(bot, "ConquestStrike"));
                Assert.NotNull(Get<object?>(bot, "Finale"));
            }
            if (goals[match].IsProsperityGame)
            {
                var core = Catalog.RepresentativeCore();
                var tree = new ResearchTree(core, 25);
                var initial = tree.Settings.StartingTechs(tree.Civ);
                var commander = new ResearchCommander(0, "p832g-fresh", tree, tree.Generate("PCore001"), initial, new());
                var snapshot = BotManager.ResearchSnapshot(cs, 0) with { Started = true, Wallet = 1_000_000,
                    Developments = initial, Stations = [tree.Civ.InitialStationTypeId] };
                var donation = Assert.Single(commander.Tick(snapshot), c => c.Kind == "donate");
                var goalBucket = BucketCatalog.Describe(core, initial, GlobalAttributeSet.FromArray(tree.Civ.GasBaseAttributes))
                    .Single(b => b.DevelopmentId == Mission.TeamMoneyDevelopmentId);
                Assert.Equal(goalBucket.Id, donation.Id);
            }
        }
    }
}
