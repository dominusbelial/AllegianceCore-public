using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class SC7CommanderOrdersTests
{
    [Fact]
    public void Strike_rallies_with_goto_then_attacks_one_shared_station()
    {
        var tactics = new ResearchTactics();
        // Rally = the aleph (warp 300, sector 1) leading INTO the target's sector (2),
        // our side of the border (friendly station 50 sits in sector 1).
        ResearchTactics.Target[] targets = [new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
            new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
            new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false)];
        ResearchTactics.Pilot[] farPilots = [new(1, 1, new(9000, 0, 0), false, true),
            new(2, 1, new(9000, 100, 0), false, true),
            new(3, 1, new(9000, -100, 0), false, true)];

        // Far from the rally: goto first (capped at 2 orders/tick), never attack.
        var first = tactics.Tick(0, true, farPilots, targets);
        Assert.Equal(2, first.Length);
        Assert.All(first, o => Assert.Equal(OrderChange.CidGoto, o.Command));
        Assert.All(first, o => Assert.Equal((short)300, o.Target));

        // Group gathers at the rally: attacks begin, everyone on the SAME station 100
        // (orders stay capped at 2/tick, so the remaining attacker joins next tick).
        ResearchTactics.Pilot[] gathered = [new(1, 1, new(1100, 0, 0), false, true),
            new(2, 1, new(1000, 100, 0), false, true),
            new(3, 1, new(1000, -100, 0), false, true)];
        var committed = tactics.Tick(2, true, gathered, targets);
        Assert.Equal(2, committed.Length);
        Assert.All(committed, o => Assert.Equal(OrderChange.CidAttack, o.Command));
        Assert.All(committed, o => Assert.Equal((short)100, o.Target));   // one shared target
        var remaining = tactics.Tick(4, true, gathered, targets);
        Assert.All(remaining, o =>
        {
            Assert.Equal(OrderChange.CidAttack, o.Command);
            Assert.Equal((short)100, o.Target);
        });
        Assert.Equal(3, committed.Concat(remaining).Select(o => o.Recipient).Distinct().Count());
    }

    [Fact]
    public void Strike_target_prefers_damaged_then_non_garrison_then_garrison_and_is_stable()
    {
        // Damaged station (even far, even Garrison-adjacent) beats pristine ones.
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] one = [new(1, 1, new(0, 0, 0), false, true)];
        ResearchTactics.Target[] targets = [new(100, IgcObjectType.Station, 2, new(100, 0, 0), false, Garrison: true),
            new(101, IgcObjectType.Station, 2, new(300, 0, 0), false),
            new(102, IgcObjectType.Station, 2, new(10000, 0, 0), false, true)];
        Assert.Contains(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Station, 102),
            tactics.Tick(0, true, one, targets));
        // Stability: the shared target does not flick across ticks.
        Assert.Contains(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Station, 102),
            tactics.Tick(16, true, one, targets));

        // No damaged: non-Garrison by distance wins over the Garrison.
        var t2 = new ResearchTactics();
        ResearchTactics.Target[] pristine = [new(100, IgcObjectType.Station, 2, new(100, 0, 0), false, Garrison: true),
            new(101, IgcObjectType.Station, 2, new(300, 0, 0), false),
            new(104, IgcObjectType.Station, 2, new(20000, 0, 0), false)];
        Assert.Contains(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Station, 101),
            t2.Tick(0, true, one, pristine));

        // Only Garrisons: nearest Garrison by distance.
        var t3 = new ResearchTactics();
        ResearchTactics.Target[] onlyGarrisons = [new(100, IgcObjectType.Station, 2, new(100, 0, 0), false, Garrison: true),
            new(105, IgcObjectType.Station, 2, new(50000, 0, 0), false, Garrison: true)];
        Assert.Contains(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Station, 100),
            t3.Tick(0, true, one, onlyGarrisons));
    }

    [Fact]
    public void Bot_accepts_goto_to_aleph_and_friendly_ship_and_station()
    {
        // The wire delivers a warp-target goto to a bot: it must be accepted and
        // carried as an ordered command (then flown-and-held by the SC7 goto law).
        var warpGoto = new QueuedCommandState(OrderChange.CidGoto, IgcObjectType.Warp, 300);
        var wd = BotManager.DecideAcceptTranslate(warpGoto, OrderChange.CidNone, null);
        Assert.True(wd.ShouldSendAccept);

        // A goto to a friendly ship (escort/follow) is accepted too.
        var shipGoto = new QueuedCommandState(OrderChange.CidGoto, IgcObjectType.Ship, 7);
        var sd = BotManager.DecideAcceptTranslate(shipGoto, OrderChange.CidNone, null);
        Assert.True(sd.ShouldSendAccept);

        // A friendly station goto remains accepted (existing dock/approach path).
        var stationGoto = new QueuedCommandState(OrderChange.CidGoto, IgcObjectType.Station, 50);
        var st = BotManager.DecideAcceptTranslate(stationGoto, OrderChange.CidNone, null);
        Assert.True(st.ShouldSendAccept);
    }
}
