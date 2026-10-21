using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>SC8 — strike waves that arrive together and stop re-sending identical
/// orders on a 15 s timer. The rally/target set mirrors SC7's fixture: friendly
/// station 50 and the rally aleph 300 in sector 1, enemy station 100 in sector 2.</summary>
public sealed class SC8StrikeWaveTests
{
    private static readonly ResearchTactics.Target[] Targets =
    [
        new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
        new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
        new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false),
    ];

    private static ResearchTactics.Pilot Strike(short id, short sector, float x, float y = 0)
        => new(id, sector, new Vector3(x, y, 0), false, true);

    private static ResearchTactics.Pilot[] AtRally(params short[] ids)
        => ids.Select((id, i) => Strike(id, 1, 1000 + i * 50, i * 40)).ToArray();

    [Fact]
    public void A_wave_commits_only_the_pilots_at_the_rally_and_latecomers_keep_rallying()
    {
        var tactics = new ResearchTactics();
        // 1-3 are gathered at the aleph; 4-6 are still far out in the rally sector.
        ResearchTactics.Pilot[] pilots =
        [
            .. AtRally(1, 2, 3),
            Strike(4, 1, 9000), Strike(5, 1, 9000, 100), Strike(6, 1, 9000, -100),
        ];

        var orders = new List<ResearchTactics.Order>();
        for (int t = 0; t <= 6; t += 2) orders.AddRange(tactics.Tick(t, true, pilots, Targets));

        // Only the three at the rally attack; the other three keep the rally goto.
        Assert.Equal(new short[] { 1, 2, 3 }, orders.Where(o => o.Command == OrderChange.CidAttack)
            .Select(o => o.Recipient).Distinct().Order());
        Assert.Equal(new short[] { 4, 5, 6 }, orders.Where(o => o.Command == OrderChange.CidGoto)
            .Select(o => o.Recipient).Distinct().Order());
        Assert.All(orders, o => Assert.Equal(o.Command == OrderChange.CidAttack ? (short)100 : (short)300, o.Target));

        // One summary line per commit, not one per tick: wave 1, three members.
        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.Commit, commit.Kind);
        Assert.Equal(1, commit.Wave);
        Assert.Equal(3, commit.Members);
        Assert.Equal(3, commit.AtRally);
        Assert.Equal((short)100, commit.Target);
        Assert.Equal((short)2, commit.Sector);
        Assert.Equal("full", commit.Reason);
        Assert.Empty(tactics.TakeStrikeEvents());
    }

    [Fact]
    public void A_pilot_that_undocks_after_the_commit_gets_the_rally_goto_not_the_attack()
    {
        var tactics = new ResearchTactics();
        var gathered = AtRally(1, 2, 3);
        tactics.Tick(0, true, gathered, Targets);
        Assert.Single(tactics.TakeStrikeEvents());

        // A bomber launches after the wave committed: it is not a member.
        ResearchTactics.Pilot[] withLatecomer = [.. gathered, Strike(4, 1, 9000)];
        var late = tactics.Tick(2, true, withLatecomer, Targets);

        var order = Assert.Single(late, o => o.Recipient == 4);
        Assert.Equal(OrderChange.CidGoto, order.Command);
        Assert.Equal((short)300, order.Target);
        Assert.DoesNotContain(late, o => o.Recipient == 4 && o.Command == OrderChange.CidAttack);
        Assert.Empty(tactics.TakeStrikeEvents());
    }

    [Fact]
    public void A_member_that_docks_and_relaunches_rejoins_the_rally_not_the_station()
    {
        // Merge fixup: bombers dock to reload and relaunch under the same ship id.
        var tactics = new ResearchTactics();
        var wave = AtRally(1, 2, 3);
        tactics.Tick(0, true, wave, Targets);
        Assert.Single(tactics.TakeStrikeEvents());

        // 2 and 3 fly on into the target sector; 1 docks (leaves the snapshot).
        ResearchTactics.Pilot[] inTarget = [Strike(2, 2, 4000), Strike(3, 2, 4000, 50)];
        tactics.Tick(4, true, inTarget, Targets);
        // 1 relaunches at home (sector 1, near the friendly station).
        var relaunch = tactics.Tick(8, true, [.. inTarget, Strike(1, 1, 0)], Targets);

        var order = Assert.Single(relaunch, o => o.Recipient == 1);
        Assert.Equal(OrderChange.CidGoto, order.Command);
        Assert.Equal((short)300, order.Target);
        Assert.Empty(tactics.TakeStrikeEvents());   // the wave goes on with 2 and 3
    }

    [Fact]
    public void A_wave_whose_members_are_alive_but_out_of_the_target_sector_ends_after_the_trip_time()
    {
        var tactics = new ResearchTactics();
        tactics.Tick(0, true, AtRally(1, 2, 3), Targets);
        tactics.TakeStrikeEvents();
        ResearchTactics.Pilot[] strayed = [Strike(1, 1, 9000), Strike(2, 1, 9000, 50), Strike(3, 1, 9000, -50)];
        tactics.Tick(ResearchTactics.RallyTimeoutSeconds - 1, true, strayed, Targets);
        Assert.Empty(tactics.TakeStrikeEvents());   // still inside the trip allowance
        tactics.Tick(ResearchTactics.RallyTimeoutSeconds + 1, true, strayed, Targets);
        Assert.Equal("no-members-in-target", Assert.Single(tactics.TakeStrikeEvents()).Reason);
    }

    [Fact]
    public void A_wave_whose_members_all_leave_the_snapshot_ends_once()
    {
        var tactics = new ResearchTactics();
        tactics.Tick(0, true, AtRally(1, 2, 3), Targets);
        tactics.TakeStrikeEvents();
        tactics.Tick(4, true, [Strike(4, 1, 9000)], Targets);   // all three docked
        var end = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.End, end.Kind);
        Assert.Equal("members-gone", end.Reason);
        tactics.Tick(6, true, [Strike(4, 1, 9000)], Targets);
        Assert.Empty(tactics.TakeStrikeEvents());
    }

    [Fact]
    public void Timeout_with_one_at_the_rally_refuses_to_commit_a_wave_of_one()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots = [Strike(1, 1, 1000), Strike(2, 1, 9000), Strike(3, 1, 9100)];
        tactics.Tick(0, true, pilots, Targets);
        tactics.Tick(2, true, pilots, Targets);

        var timeout = tactics.Tick(ResearchTactics.RallyTimeoutSeconds + 1, true, pilots, Targets);
        Assert.DoesNotContain(timeout, o => o.Command == OrderChange.CidAttack);
        Assert.Empty(tactics.TakeStrikeEvents());
    }

    [Fact]
    public void Timeout_with_two_at_the_rally_commits_both()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots = [.. AtRally(1, 2), Strike(3, 1, 9000)];
        tactics.Tick(0, true, pilots, Targets);
        tactics.Tick(2, true, pilots, Targets);

        var timeout = tactics.Tick(ResearchTactics.RallyTimeoutSeconds + 1, true, pilots, Targets);
        Assert.Equal(new short[] { 1, 2 }, timeout.Select(o => o.Recipient).Order());
        Assert.All(timeout, o => Assert.Equal(OrderChange.CidAttack, o.Command));

        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.Commit, commit.Kind);
        Assert.Equal(1, commit.Wave);
        Assert.Equal(2, commit.Members);
        Assert.Equal(2, commit.AtRally);
        Assert.Equal("timeout", commit.Reason);
    }

    [Fact]
    public void A_side_with_one_strike_pilot_still_attacks_after_the_timeout()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] one = [Strike(1, 1, 1000)];
        // SC9 — the quorum is stated against the whole strike roster, not the
        // launched pilots, so a one-bomber roster is what permits a wave of one.
        var first = tactics.Tick(0, true, one, Targets, strikeRoster: 1);
        Assert.Contains(first, o => o.Command == OrderChange.CidGoto && o.Target == 300);

        var timeout = tactics.Tick(ResearchTactics.RallyTimeoutSeconds + 1, true, one, Targets, strikeRoster: 1);
        var order = Assert.Single(timeout);
        Assert.Equal(OrderChange.CidAttack, order.Command);
        Assert.Equal((short)100, order.Target);
        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(1, commit.Members);
        Assert.Equal("timeout", commit.Reason);
    }

    [Fact]
    public void Never_commits_with_nobody_at_the_rally_even_after_two_timeouts()
    {
        // The dropped SC7 "commit with nobody" branch would have fired here.
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots = [Strike(1, 1, 9000), Strike(2, 1, 9100), Strike(3, 1, 9200)];
        for (int t = 0; t <= 2 * ResearchTactics.RallyTimeoutSeconds; t += 10)
            Assert.DoesNotContain(tactics.Tick(t, true, pilots, Targets), o => o.Command == OrderChange.CidAttack);
        Assert.Empty(tactics.TakeStrikeEvents());
    }

    [Fact]
    public void A_progressing_goto_is_sent_once_over_120_seconds_a_stalled_one_is_resent()
    {
        var tactics = new ResearchTactics();
        var orders = new List<ResearchTactics.Order>();
        for (int t = 0; t <= 120; t += 10)
        {
            // Pilot 1 closes from 20 km to 10 km (25% per 60 s); pilot 2 never moves.
            float moving = 20000 - 10000 * (t / 120f);
            ResearchTactics.Pilot[] pilots = [Strike(1, 1, moving), Strike(2, 1, 25000, 500)];
            orders.AddRange(tactics.Tick(t, true, pilots, Targets));
        }

        Assert.Single(orders, o => o.Recipient == 1);          // the goto went out once
        Assert.All(orders.Where(o => o.Recipient == 1), o => Assert.Equal(OrderChange.CidGoto, o.Command));
        Assert.Equal(3, orders.Count(o => o.Recipient == 2));   // t=0, then every 60 s with no progress
        Assert.All(orders, o => Assert.Equal(OrderChange.CidGoto, o.Command));
        Assert.Empty(tactics.TakeStrikeEvents());               // never gathered, never committed
    }

    [Fact]
    public void A_wave_ends_when_its_members_leave_the_target_sector_and_re_forms_at_the_rally()
    {
        var tactics = new ResearchTactics();
        var firstWave = AtRally(1, 2, 3);
        tactics.Tick(0, true, firstWave, Targets);
        Assert.Single(tactics.TakeStrikeEvents());

        // The committed three reach the target sector and stay there: the wave lives.
        ResearchTactics.Pilot[] onTarget =
        [
            Strike(1, 2, 5000, 100), Strike(2, 2, 5000, -100), Strike(3, 2, 4800),
        ];
        for (int t = 10; t <= 80; t += 10) tactics.Tick(t, true, onTarget, Targets);
        Assert.Empty(tactics.TakeStrikeEvents());

        // They dock/are gone; three fresh bombers are at the rally -> end + new wave.
        ResearchTactics.Pilot[] next = AtRally(4, 5, 6);
        var orders = new List<ResearchTactics.Order>();
        orders.AddRange(tactics.Tick(100, true, next, Targets));
        orders.AddRange(tactics.Tick(102, true, next, Targets));

        var events = tactics.TakeStrikeEvents();
        Assert.Equal(2, events.Length);
        Assert.Equal(ResearchTactics.StrikeEventKind.End, events[0].Kind);
        Assert.Equal(1, events[0].Wave);
        Assert.Equal("members-gone", events[0].Reason);
        Assert.Equal(ResearchTactics.StrikeEventKind.Commit, events[1].Kind);
        Assert.Equal(2, events[1].Wave);
        Assert.Equal(3, events[1].Members);
        Assert.Equal(3, events[1].AtRally);
        Assert.Equal("full", events[1].Reason);

        Assert.Equal(new short[] { 4, 5, 6 }, orders.Where(o => o.Command == OrderChange.CidAttack)
            .Select(o => o.Recipient).Distinct().Order());
        Assert.DoesNotContain(orders, o => o.Recipient is 1 or 2 or 3);
    }

    [Fact]
    public void A_wave_ends_when_the_target_station_is_gone()
    {
        var tactics = new ResearchTactics();
        tactics.Tick(0, true, AtRally(1, 2, 3), Targets);
        Assert.Single(tactics.TakeStrikeEvents());

        // Station 100 dies while the wave is still at the rally (before the trip grace).
        ResearchTactics.Target[] withoutStation = Targets.Where(t => t.Id != 100).ToArray();
        tactics.Tick(50, true, AtRally(1, 2, 3), withoutStation);

        var end = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.End, end.Kind);
        Assert.Equal(1, end.Wave);
        Assert.Equal("station-gone", end.Reason);
    }

    [Fact]
    public void A_pair_that_re_forms_after_a_wave_end_waits_its_own_timeout()
    {
        var tactics = new ResearchTactics();
        tactics.Tick(0, true, AtRally(1, 2, 3), Targets);
        Assert.Single(tactics.TakeStrikeEvents());

        // The first wave ends at the trip grace with two fresh bombers at the rally:
        // a pair is only allowed to commit after its own 90 s rally timeout.
        var pair = AtRally(4, 5);
        var atEnd = tactics.Tick(ResearchTactics.RallyTimeoutSeconds, true, pair, Targets);
        Assert.Equal(ResearchTactics.StrikeEventKind.End, Assert.Single(tactics.TakeStrikeEvents()).Kind);
        Assert.DoesNotContain(atEnd, o => o.Command == OrderChange.CidAttack);

        Assert.DoesNotContain(tactics.Tick(ResearchTactics.RallyTimeoutSeconds + 1, true, pair, Targets),
            o => o.Command == OrderChange.CidAttack);
        var later = tactics.Tick(2 * ResearchTactics.RallyTimeoutSeconds + 1, true, pair, Targets);
        Assert.Equal(new short[] { 4, 5 }, later.Select(o => o.Recipient).Order());
        Assert.All(later, o => Assert.Equal(OrderChange.CidAttack, o.Command));
        Assert.Equal("timeout", Assert.Single(tactics.TakeStrikeEvents()).Reason);
    }

    [Fact]
    public void Strike_wave_log_lines_match_the_rig_format()
    {
        // SC9 — both edges now carry the escort count and the observed station health
        // (-1 = unknown); the end edge adds members/arrived/peakOnTarget/dur.
        Assert.Equal("[strike] side=1 wave=2 commit members=3 atRally=3 target=station#100 sector=2 escorts=0 stationHull=-1 stationShield=-1 reason=full",
            BotManager.StrikeLogLine(1, new(ResearchTactics.StrikeEventKind.Commit, 2, 3, 3, 100, 2, "full")));
        Assert.Equal("[strike] side=0 wave=2 end members=3 arrived=0 peakOnTarget=0 escorts=0 dur=-1s stationHull=-1 stationShield=-1 reason=no-members-in-target",
            BotManager.StrikeLogLine(0, new(ResearchTactics.StrikeEventKind.End, 2, 3, 0, 100, 2, "no-members-in-target")));
    }

    [Fact]
    public void Non_strike_attack_orders_are_not_resent_on_a_timer_while_in_the_target_sector()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] fighter = [new(1, 0, Vector3.Zero, false, false)];
        ResearchTactics.Target[] local = [new(20, IgcObjectType.Ship, 0, new(100, 0, 0), false)];

        Assert.Equal(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Ship, 20),
            Assert.Single(tactics.Tick(0, true, fighter, local)));
        Assert.Empty(tactics.Tick(15, true, fighter, local));
        Assert.Empty(tactics.Tick(120, true, fighter, local));   // in the target sector = progress
    }

    [Fact]
    public void Non_strike_attack_orders_are_resent_when_the_pilot_makes_no_progress()
    {
        var tactics = new ResearchTactics();
        // A bomber threatening the home station in sector 1: the fighter sits still in
        // sector 0, so after 60 s without a sector change the order is re-sent.
        ResearchTactics.Pilot[] fighter = [new(1, 0, Vector3.Zero, false, false)];
        ResearchTactics.Target[] targets =
        [
            new(51, IgcObjectType.Station, 1, new(0, 0, 0), true),
            new(60, IgcObjectType.Ship, 1, new(1000, 0, 0), false, Bomber: true),
        ];

        Assert.Equal(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Ship, 60),
            Assert.Single(tactics.Tick(0, true, fighter, targets)));
        Assert.Empty(tactics.Tick(15, true, fighter, targets));
        Assert.Equal(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Ship, 60),
            Assert.Single(tactics.Tick(ResearchTactics.ProgressWindowSeconds + 1, true, fighter, targets)));

        // A sector change is progress and restarts the 60 s window at the tick it is
        // seen: the pilot leaves sector 0 for sector 2 at t=30, so the next re-send is
        // 60 s later (t=91), not 60 s after the original send.
        var changed = new ResearchTactics();
        Assert.Equal(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Ship, 60),
            Assert.Single(changed.Tick(0, true, fighter, targets)));
        ResearchTactics.Pilot[] moved = [new(1, 2, Vector3.Zero, false, false)];
        Assert.Empty(changed.Tick(30, true, moved, targets));
        Assert.Empty(changed.Tick(60, true, moved, targets));
        Assert.Equal(new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Ship, 60),
            Assert.Single(changed.Tick(ResearchTactics.ProgressWindowSeconds + 31, true, moved, targets)));
    }
}
