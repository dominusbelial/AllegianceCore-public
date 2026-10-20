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

    private static readonly ResearchTactics.Target[] RallyTargets =
    [
        new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
        new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
        new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false),
    ];

    [Fact]
    public void Orders_stay_capped_per_tick_across_strike_and_other_pilots()
    {
        // Claude SC-7 review: the second loop's "== 2" never fired once the strike
        // loop had filled the cap, so 3 strike + 30 others gave 32 orders a tick.
        var tactics = new ResearchTactics();
        var pilots = new List<ResearchTactics.Pilot>
        {
            new(1, 1, new(9000, 0, 0), false, true), new(2, 1, new(9000, 50, 0), false, true),
            new(3, 1, new(9000, -50, 0), false, true),
        };
        var targets = RallyTargets.ToList();
        for (short i = 0; i < 30; i++)
        {
            pilots.Add(new((short)(10 + i), 2, new(i * 10, 0, 0), false, false));
            targets.Add(new((short)(500 + i), IgcObjectType.Ship, 2, new(i * 10, 5, 0), false));
        }
        for (int tick = 0; tick < 5; tick++)
            Assert.True(tactics.Tick(tick * 2, true, pilots.ToArray(), targets.ToArray()).Length <= ResearchTactics.MaxOrdersPerTick);
    }

    [Fact]
    public void A_short_strike_group_commits_after_the_rally_timeout()
    {
        // Two launched bombers (< StrikeGroupSize) used to rally forever.
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] two = [new(1, 1, new(1000, 0, 0), false, true), new(2, 1, new(1000, 80, 0), false, true)];
        Assert.All(tactics.Tick(0, true, two, RallyTargets), o => Assert.Equal(OrderChange.CidGoto, o.Command));
        Assert.DoesNotContain(tactics.Tick(30, true, two, RallyTargets), o => o.Command == OrderChange.CidAttack);
        var later = tactics.Tick(ResearchTactics.RallyTimeoutSeconds + 1, true, two, RallyTargets);
        Assert.NotEmpty(later);
        Assert.All(later, o => { Assert.Equal(OrderChange.CidAttack, o.Command); Assert.Equal((short)100, o.Target); });
    }

    [Fact]
    public void Gathering_and_on_target_checks_respect_the_sector()
    {
        // Pilots in sector 0 that happen to sit at the aleph's / station's
        // coordinates are NOT at the rally (per-sector coordinates).
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] elsewhere = [new(1, 0, new(1000, 0, 0), false, true),
            new(2, 0, new(1000, 50, 0), false, true), new(3, 0, new(5000, 0, 0), false, true)];
        var orders = tactics.Tick(0, true, elsewhere, RallyTargets);
        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.Equal(OrderChange.CidGoto, o.Command));
    }

    [Fact]
    public void Rally_prefers_an_aleph_outside_enemy_held_sectors()
    {
        var tactics = new ResearchTactics();
        // No friendly station next to either aleph; warp 301 (lower id) sits in
        // sector 3, which holds an enemy station; warp 302 in sector 4 does not.
        ResearchTactics.Target[] targets = [new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
            new(301, IgcObjectType.Warp, 3, new(0, 0, 0), true, DestinationSector: 2),
            new(302, IgcObjectType.Warp, 4, new(0, 0, 0), true, DestinationSector: 2),
            new(110, IgcObjectType.Station, 3, new(4000, 0, 0), false),
            new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false, true)];
        ResearchTactics.Pilot[] three = [new(1, 1, new(9000, 0, 0), false, true),
            new(2, 1, new(9000, 50, 0), false, true), new(3, 1, new(9000, -50, 0), false, true)];
        var orders = tactics.Tick(0, true, three, targets);
        Assert.All(orders, o => { Assert.Equal(OrderChange.CidGoto, o.Command); Assert.Equal((short)302, o.Target); });
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
