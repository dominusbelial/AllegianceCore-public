using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableTheory]
    [InlineData("research", true, true)]
    [InlineData("research", false, false)]
    [InlineData("unlocked", true, false)]
    public void P733_Verified_available_strike_takes_station_duty_only_in_research(string mode, bool verified, bool attacks)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        const short flyingHull = 14;
        DefenseMap(cs.World);
        cs.World.Apply(new ExportShip { ShipId = 1, SideId = 0, HullId = flyingHull });
        var station = Station(101, 1, 1, 1000);
        cs.World.Apply(new ExportStation { Station = station });
        cs.World.Apply(new ResearchBriefing { TechMode = mode });
        DefenseEnemy(cs.World, 9, 15, 500);
        var bot = Bot(cs); Set(bot, "Role", "bomber"); Set(bot, "HullId", flyingHull);
        var core = Catalog.RepresentativeCore(); var parts = Catalog.NativePreferredLoadout(flyingHull, core.HullTypes[flyingHull].Name).Parts;
        var strike = new ShowcaseFinale.StrikeLoadout(flyingHull, "fixture", station.StationTypeId, "fixture", parts, new(1, 1), new(1, 1), 0, []);
        Set(bot, "PreviousLegalLoadout", new TechLoadoutSelector.Selection(flyingHull, "fixture", parts, 0, "fixture", strike));
        Set(bot, "Parts", parts); Set(bot, "FinaleBomberActive", true); Set(bot, "FinaleGrantVerified", verified);
        Set(bot, "TargetSector", (short?)1); DefenseNoSocketSender(bot);
        Call("EngageCombat", Manager(bot), bot, 10.0, null);
        Assert.Equal((short?)(attacks ? 101 : 9), Get<short?>(bot, "TargetId"));
    }
}
