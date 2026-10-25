using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableTheory]
    [InlineData(0, 3)] [InlineData(1, 3)]
    [InlineData(0, 25)] [InlineData(1, 25)]
    public void P819_Flying_strike_replaces_its_old_aleph_rally_order(int side, int civ)
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var rig = new FinaleRig(new(Side: "both", Bombers: 1));
        int index = side == 0 ? 0 : 12;
        var bot = rig.Bots[index]; var cs = rig.Sessions[index];
        cs.World.Apply(new ResearchBriefing { TechMode = "research" });
        var core = Catalog.RepresentativeCore();
        var target = core.StationTypes[rig.Stations[side == 0 ? 1 : 0].StationTypeId];
        var accepted = TechLoadoutSelector.Select(core, (short)civ, Showcase.HullRole.Bomber,
            core.ComputeRequiredTechUnion(), core.Civilizations[(short)civ].InitialStationTypeId, target)!;
        Assert.NotNull(accepted.Strike);
        short id = Get<short>(bot, "ShipId");
        Set(bot, "PreviousLegalLoadout", accepted);
        Set(bot, "Granted", new BotManager.LoadoutGrant(accepted.HullId, accepted.Parts));
        Set(bot, "HullId", accepted.HullId); Set(bot, "Parts", accepted.Parts);
        cs.World.Apply(new ExportShip { ShipId = id, SideId = (short)side, HullId = accepted.HullId });
        var old = new QueuedCommandState(OrderChange.CidGoto, Allegiance.Core.IgcObjectType.Warp, 999);
        Set(bot, "OrderedCommand", old); Set(bot, "OrderedWarpOrigin", (short?)1);
        rig.Mature();
        Assert.False((bool)Call("TryDriveResearchFinaleRefit", rig.Manager, bot, cs, rig.Started + 1801)!);
        Assert.True(Get<bool>(bot, "FinaleBomberActive"));
        Assert.Null(Get<QueuedCommandState?>(bot, "OrderedCommand"));
        Assert.Null(Get<short?>(bot, "OrderedWarpOrigin"));
        // This runs before ordinary transit in DriveCombat. With the stale rally
        // it owns the tick forever (an unseen warp makes it loiter); the new
        // station objective never gets a chance to fly.
        Assert.False((bool)Call("DriveCommanderOrder", rig.Manager, bot, rig.Started + 1802)!);
        Assert.Equal(0, Get<int>(bot, "RelaunchAttempts"));
        Assert.Equal(id, Get<short>(bot, "ShipId"));
    }

    [Fact]
    public void P819_Showcase_defaults_to_equal_finale_duties_and_keeps_explicit_selection()
    {
        static Dictionary<string, System.Text.Json.JsonElement> Args(string value) =>
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(value)!;
        var defaults = McpTools.ReadFinaleSettings(Args("{}"), true);
        Assert.Equal("both", defaults.Side);
        Assert.Equal(new short[] { 0, 1 }, ShowcaseFinale.TriggerSides(defaults, 1800,
            [new(0, 10, 10, 100), new(1, 10, 10, 1)], 10));
        Assert.Equal("leader", McpTools.ReadFinaleSettings(Args("{\"finale_side\":\"leader\"}"), true).Side);
        Assert.Equal("1", McpTools.ReadFinaleSettings(Args("{\"finale_side\":1}"), true).Side);
        Assert.False(McpTools.ReadFinaleSettings(Args("{}"), false).Enabled);
    }
}
