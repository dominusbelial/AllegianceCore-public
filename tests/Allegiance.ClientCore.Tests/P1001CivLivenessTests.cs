using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [Theory, Trait("Requires", "Artwork")]
    [InlineData(false)] [InlineData(true)]
    public void P1001_Flight_goal_behind_the_ship_recovers_without_changing_OFF_or_restoring_fire(bool enabled)
    {
        using var reads = new DefenseLiveReads();
        using var cs = Session(true);
        if (enabled)
            typeof(ClientWorld).GetProperty("ResearchBriefing")!.SetValue(cs.World, new ResearchBriefing
            { Plan = new ResearchPlan { CivId = 25, Name = "civ-strategy/0.15/0/" + new string('A', 64) } });
        var bot = Bot(cs); P700Weapon(bot); var sender = P700Sender(bot);
        Set(bot, "TargetId", (short?)9); Set(bot, "LastMoveAt", 0d); Set(bot, "LastAiDecision", 2.1);
        P707Motion(cs.World, 1, Vector3.Zero, 0);
        Call("CombatFlyTo", null, bot, new Vector3(0, 0, 1000), 15f, 200f, 0);
        var first = P700LastControls(sender).Payload;
        int lateral = ShipControlState.LeftButton | ShipControlState.RightButton;
        Assert.Equal(enabled, first.Controls.Yaw != 0);
        Assert.Equal(enabled, (first.StateM & lateral) != 0);
        Assert.Equal(0, first.StateM & ShipControlState.AllWeapons);
        Call("CombatFlyTo", null, bot, new Vector3(0, 0, 1000), 15f, 200f, ShipControlState.LeftButton);
        var dodge = P700LastControls(sender).Payload;
        Assert.Equal(enabled ? ShipControlState.RightButton : ShipControlState.LeftButton, dodge.StateM & lateral);
        Assert.Equal(0, dodge.StateM & ShipControlState.AllWeapons);
        P707Motion(cs.World, 1, Vector3.Zero, 1);
        Set(bot, "LastMoveAt", 2.15); Set(bot, "LastAiDecision", 2.15);
        Call("CombatFlyTo", null, bot, new Vector3(0, 0, 1000), 15f, 200f, 0);
        Assert.Equal(enabled, (P700LastControls(sender).Payload.StateM & lateral) != 0);
        Set(bot, "LastAiDecision", 2.7);
        Call("CombatFlyTo", null, bot, new Vector3(0, 0, 1000), 15f, 200f, 0);
        Assert.Equal(0, P700LastControls(sender).Payload.StateM & lateral);
        Set(bot, "TargetId", null); Set(bot, "LastMoveAt", 0d); Set(bot, "LastAiDecision", 6d);
        P707Motion(cs.World, 1, Vector3.Zero, 0);
        Call("CombatFlyTo", null, bot, new Vector3(0, 0, 1000), 15f, 200f, 0);
        Assert.Equal(0, P700LastControls(sender).Payload.StateM & lateral);
    }
}
