using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableTheory]
    [Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Pod_outcome_counts_a_true_death_restart_separately_from_flying_home(bool trueDeath)
    {
        Require.Artwork();
        using var cs = Session();
        var bot = Bot(cs);
        cs.World.Apply(new BuyLoadoutAck { HullId = 444, Parts = [] });
        cs.World.Apply(new KillShip { ShipId = 1, IdCredit = -1, BDeathCredit = false });
        Call("UpdatePodState", null, bot, cs, 1.0);
        if (trueDeath) cs.World.Apply(new KillShip { ShipId = 1, IdCredit = -1, BDeathCredit = true });
        cs.World.Apply(new DockedNotify { StationId = 23 });
        Call("UpdatePodState", null, bot, cs, 2.0);
        Call("UpdatePodState", null, bot, cs, 3.0); // notification is counted once
        var summary = Manager(bot).GetCombatSummary();
        Assert.Equal(trueDeath ? 0 : 1, summary.PodsDocked);
        Assert.Equal(trueDeath ? 1 : 0, summary.PodsRestarted);
        Assert.True(Get<bool>(bot, "PodDocked")); // both may use normal purchase/relaunch
    }
}
