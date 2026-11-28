using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Trait("Tier", "Fast")]
public sealed class P1104SustainableRaidTests
{
    private static readonly StrikeVolley.StationProfile Profile = new(100, 100, 2, 3, 0, 0, [[1]]);
    private static readonly ResearchTactics.Target Enemy = new(91, IgcObjectType.Station, 2, default,
        false, Hull: 1, Shield: 1, Side: 1, Profile: Profile);
    private static readonly SustainableRaid.Observation Sight = new(10, [91], false, _ => null, "opening");
    private static ResearchTactics.Pilot Pilot(short id, int power, bool home = false, bool scout = false)
        => new(id, home ? (short)0 : (short)1, default, false, false,
            Raid: new(new(17, [new(0, power, 1, 1)]), new() { [91] = 0 }, home ? (short)10 : null, scout));
    private static ResearchTactics.Pilot[] Fleet() => [Pilot(1, 100), Pilot(2, 101), Pilot(3, 10, home: true)];
    private static SustainableRaid.Wing? Ready(ResearchTactics.Target[] targets, SustainableRaid.Observation sight,
        ResearchTactics.Pilot[] pilots) => new SustainableRaid().Ready(10, sight, targets, pilots);

    [Fact]
    public void No_enemy_target_has_no_launch()
    {
        var policy = new ResearchTactics();
        var friendly = Enemy with { Friendly = true, Side = 0 };
        Assert.Empty(policy.Tick(10, true, Fleet(), [friendly], raid: Sight));
        Assert.Null(Ready([], Sight, Fleet()));
        Assert.Null(Ready([friendly], Sight, Fleet()));
    }

    [Fact]
    public void Stale_or_withdrawn_visibility_invalidates_remembered_export()
    {
        Assert.Null(Ready([Enemy], Sight with { At = 7 }, Fleet()));
        Assert.Null(Ready([Enemy], Sight with { Visible = [] }, Fleet()));
        Assert.Null(Ready([Enemy], Sight with { Destroyer = _ => 21 }, Fleet()));
    }

    [Fact]
    public void Each_member_needs_a_known_route_to_this_station()
    {
        var pilots = Fleet();
        pilots[0] = pilots[0] with { Raid = pilots[0].Raid! with { TravelSeconds = new() { [90] = 0 } } };
        Assert.Null(Ready([Enemy], Sight, pilots));
        pilots[0] = pilots[0] with { Raid = pilots[0].Raid! with { TravelSeconds = new() { [91] = 25 } } };
        Assert.Null(Ready([Enemy], Sight, pilots));
    }

    [SkippableFact]
    public void Purchase_without_a_current_firing_mount_or_remaining_round_cannot_count()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var part = new ExpandedPartData(158, 2, 0, ExpandedPartData.FullyMounted);
        var grant = new BotManager.LoadoutGrant(23, [part]);
        StrikeVolley.Loadout? Mount(ExpandedPartData[] parts, long fired = 0, short hull = 23)
            => SustainableRaid.Mounted(core, hull, grant, parts, fired, 1, 1, GlobalAttributeSet.Identity);
        Assert.Null(Mount([]));
        Assert.Null(Mount([part with { FractionMounted = 239 }]));
        Assert.Null(Mount([part with { MountId = 255 }]));
        Assert.Null(Mount([part], fired: 2));
        Assert.Null(Mount([part], hull: 889));
        Assert.Equal(1, Assert.Single(Mount([part], fired: 1)!.Mounts).Shots);
    }

    [Fact]
    public void Smallest_mixed_granted_volley_can_launch_without_a_bomber_role()
    {
        Assert.Null(Ready([Enemy], Sight, [Pilot(1, 101), Pilot(1, 101), Pilot(3, 10, home: true)]));
        var pilots = Fleet().Append(Pilot(4, 201)).ToArray();
        Assert.Equal(new short[] { 4 }, Ready([Enemy], Sight, pilots)!.Members);
        Assert.Equal(new short[] { 1, 2 }, Ready([Enemy], Sight, Fleet())!.Members);
        var delayed = new StrikeVolley.Loadout(17, [new(0, 101, 2, 21)]);
        pilots = [Pilot(1, 0) with { Raid = new(delayed, new() { [91] = 0 }, null, false) }, Pilot(3, 10, home: true)];
        Assert.Null(Ready([Enemy], Sight, pilots));
    }

    [Fact]
    public void Leave_a_defender_at_home_and_the_scout_on_its_lane()
    {
        var pilots = new[] { Pilot(1, 100), Pilot(2, 201, home: true), Pilot(3, 201, scout: true) };
        Assert.Null(Ready([Enemy], Sight, pilots));
        pilots = [.. pilots, Pilot(4, 110)];
        var wing = Ready([Enemy], Sight, pilots)!;
        Assert.Equal(new short[] { 1, 4 }, wing.Members);
        Assert.Equal(2, wing.Defender);
        Assert.Equal(10, wing.Home);
        var orders = new ResearchTactics().Tick(10, true, pilots, [Enemy], raid: Sight);
        Assert.Contains(orders, o => o == new ResearchTactics.Order(2, OrderChange.CidDefend, IgcObjectType.Station, 10));
        Assert.DoesNotContain(orders, o => o.Recipient == 3);
    }

    [Fact]
    public void Strike_role_pilots_receive_only_the_raid_orders_within_the_shared_budget()
    {
        var policy = new ResearchTactics();
        var pilots = Fleet().Select(p => p with { Strike = true }).ToArray();
        var orders = policy.Tick(10, true, pilots, [Enemy], raid: Sight);
        Assert.Equal(new[] { new ResearchTactics.Order(3, OrderChange.CidDefend, IgcObjectType.Station, 10),
            new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Station, 91) }, orders);
        Assert.Equal(new ResearchTactics.Order(2, OrderChange.CidAttack, IgcObjectType.Station, 91),
            Assert.Single(policy.Tick(11, true, pilots, [Enemy], raid: Sight with { At = 11 })));
        Assert.Empty(policy.TakeStrikeEvents());
    }

    [Fact]
    public void Off_preserves_the_ordinary_command_transcript_even_with_a_feasible_wing()
    {
        var policy = new ResearchTactics();
        var targets = new[] { Enemy, new ResearchTactics.Target(49, IgcObjectType.Ship, 1, default, false) };
        var expected = new[] { new ResearchTactics.Order(1, OrderChange.CidAttack, IgcObjectType.Ship, 49),
            new ResearchTactics.Order(2, OrderChange.CidAttack, IgcObjectType.Ship, 49) };
        Assert.Equal(expected, policy.Tick(10, true, Fleet(), targets));
        Assert.Empty(policy.Tick(11, true, Fleet(), targets));
        Assert.Empty(policy.TakeRaidEvents());
        Assert.False(DriverOptions.Parse([System.Net.IPAddress.Loopback.ToString(), "1", "--backfill-seats", "5"]).SustainableRaid);
        Assert.True(DriverOptions.Parse([System.Net.IPAddress.Loopback.ToString(), "1", "--backfill-seats", "5", "--sustainable-raid", "true"]).SustainableRaid);
    }

    [Fact]
    public void Game_over_keeps_the_credited_launcher_available_to_the_last_raid_observation()
    {
        using var world = new ClientWorld();
        world.Apply(new StationsUpdate { Entries = [new(91, 240, 240)] });
        world.Apply(new StationDestroyed { StationId = 91, Launcher = 21 });
        short? lastLauncher = null;
        world.GameOverReceived += () => lastLauncher = world.StationDestroyer(91);
        world.Apply(new GameOver());
        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageOver });
        Assert.Equal((short)21, lastLauncher);
        Assert.Null(world.StationDestroyer(91));
        Assert.Null(world.LatestStationObservation.Batch);
    }

    [Theory]
    [InlineData("credited-kill")]
    [InlineData("capture")]
    [InlineData("repulse")]
    [InlineData("withdrawn")]
    [InlineData("other-killer")]
    [InlineData("timeout")]
    public void Outcome_requires_observed_member_or_target_evidence(string outcome)
    {
        var commander = new ResearchTactics();
        var pilots = Fleet();
        commander.Tick(10, true, pilots, [Enemy], raid: Sight);
        Assert.Equal(new[] { "sighting", "ready", "launch" }, commander.TakeRaidEvents().Select(e => e.Stage));
        pilots[0] = pilots[0] with { Sector = Enemy.Sector };
        commander.Tick(11, true, pilots, [Enemy with { Shield = .8f }], raid: Sight with { At = 11 });
        Assert.Equal(new[] { "arrival", "hit" }, commander.TakeRaidEvents().Select(e => e.Stage));
        // A missing snapshot is ambiguous. Even a partial, positive loss
        // receipt leaves an unresolved wing until every member is accounted for.
        commander.Tick(12, true, [], [], raid: Sight with { Members = new() { [1] = new(SustainableRaid.MemberState.Lost, 1) } });
        Assert.Empty(commander.TakeRaidEvents());
        var observation = Sight with { At = 13,
            Destroyer = _ => outcome switch { "credited-kill" => 1, "other-killer" => 17, _ => null },
            Members = outcome is "repulse" or "withdrawn" ? new() {
                [2] = new(outcome == "repulse" ? SustainableRaid.MemberState.Lost : SustainableRaid.MemberState.Withdrawn, 0) } : null };
        var station = outcome == "capture" ? Enemy with { Friendly = true, Side = 0 } : Enemy;
        commander.Tick(outcome == "timeout" ? 280 : 13, true, [], [station], raid: observation);
        var end = Assert.Single(commander.TakeRaidEvents());
        Assert.Equal("outcome", end.Stage);
        Assert.Contains("result=" + (outcome == "other-killer" ? "withdrawn" : outcome), end.Detail);
        commander.Tick(300, true, Fleet(), [Enemy], raid: Sight with { At = 300 });
        Assert.Empty(commander.TakeRaidEvents());
    }

    [Fact]
    public void A_partial_raid_leaves_the_committed_wave_and_support_in_its_sector()
    {
        var policy = new ResearchTactics();
        var legacy = Enemy with { Id = 80, Profile = null, Sector = 3 };
        ResearchTactics.Pilot[] fleet = [.. Fleet().Select(p => p with { Strike = true }),
            Pilot(4, 1) with { Raid = null, Strike = true }, Pilot(5, 1) with { Raid = null, Strike = true }];
        policy.Tick(8, true, fleet, [legacy], raid: Sight with { At = 8 });
        Assert.Equal(5, Assert.Single(policy.TakeStrikeEvents()).Members);
        var orders = new List<ResearchTactics.Order>();
        for (int t = 10; t <= 12; t++)
        {
            var tick = policy.Tick(t, true, fleet, [legacy, Enemy], raid: Sight with { At = t });
            Assert.True(tick.Length <= ResearchTactics.MaxOrdersPerTick);
            orders.AddRange(tick);
            Assert.True(policy.StrikeWaveActive);
            Assert.Equal(3, policy.StrikeSector);
            Assert.Empty(policy.TakeStrikeEvents());
        }
        Assert.Equal(new short[] { 1, 2 }, orders.Where(o => o.Target == 91).Select(o => o.Recipient));
        Assert.Equal(new short[] { 4, 5 }, orders.Where(o => o.Target == 80).Select(o => o.Recipient));
        var solo = new ResearchTactics();
        solo.Tick(10, true, Fleet(), [Enemy], raid: Sight);
        Assert.NotNull(solo.ActiveRaid);
        Assert.False(solo.StrikeWaveActive);
        Assert.Equal(-1, solo.StrikeSector);
    }

    // Generated by executing this scenario against pre-P1104 revision 22a595651.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strike_decisions_match_the_pre_raid_commander(bool enabled)
    {
        Assert.Equal("""
            0 support=False/2
            1 5 Warp 300
            2 5 Warp 300
            2 support=False/2
            3 5 Warp 300
            4 5 Warp 300
            91 support=True/2
            1 1 Station 100
            2 1 Station 100
            [strike] side=0 wave=1 commit members=2 atRally=2 target=station#100 sector=2 escorts=0 stationHull=-1 stationShield=-1 reason=timeout
            92 support=True/2
            3 5 Warp 300
            4 5 Warp 300
            94 support=False/2
            [strike] side=0 wave=1 end members=2 arrived=0 peakOnTarget=0 escorts=0 dur=3.0s stationHull=-1 stationShield=-1 reason=members-gone
            96 support=True/2
            2 1 Station 100
            3 1 Station 100
            [strike] side=0 wave=2 commit members=3 atRally=3 target=station#100 sector=2 escorts=0 stationHull=-1 stationShield=-1 reason=full
            98 support=True/2
            4 1 Station 100
            """, WaveTranscript(enabled));
    }

    private static string WaveTranscript(bool raidEnabled)
    {
        var policy = new ResearchTactics();
        ResearchTactics.Target[] targets = [
            new(50, IgcObjectType.Station, 1, default, true),
            new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
            new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false)];
        ResearchTactics.Pilot Strike(short id, float x) => new(id, 1, new(x, 0, 0), false, true);
        ResearchTactics.Pilot[] fleet = [Strike(1, 1000), Strike(2, 1000), Strike(3, 9000), Strike(4, 9000)];
        var lines = new List<string>();
        foreach (int t in new[] { 0, 2, 91, 92, 94, 96, 98 })
        {
            if (t == 94) fleet = [Strike(3, 9000), Strike(4, 9000)];
            if (t == 96) fleet = [Strike(2, 1000), Strike(3, 1000), Strike(4, 1000)];
            var orders = policy.Tick(t, true, fleet, targets, raid: raidEnabled ? Sight with { At = t } : null);
            lines.Add($"{t} support={policy.StrikeWaveActive}/{policy.StrikeSector}");
            lines.AddRange(orders.Select(o => $"{o.Recipient} {o.Command} {o.Type} {o.Target}"));
            lines.AddRange(policy.TakeStrikeEvents().Select(w => BotManager.StrikeLogLine(0, w)));
        }
        return string.Join('\n', lines);
    }

    [Fact]
    public void Dense_failed_replays_share_one_budget_and_advance_to_other_stations()
    {
        var targets = Enumerable.Range(91, 5).Select(id => Enemy with { Id = (short)id,
            Profile = Profile with { ArmorDefense = 1, DamageConstants = [[1, .00001f]] } }).ToArray();
        var pilots = Enumerable.Range(0, 130).Select(i => Pilot((short)i, 201, home: i == 0)).ToArray();
        foreach (var pilot in pilots)
            foreach (var t in targets) pilot.Raid!.TravelSeconds[t.Id] = pilot.Id * .001;
        var sight = Sight with { Visible = targets.Select(t => t.Id).ToHashSet() };
        var policy = new SustainableRaid();
        for (int i = 0; i < targets.Length; i++)
        {
            Assert.Null(policy.Ready(10, sight, targets, pilots));
            Assert.Equal(SustainableRaid.CandidateBudget, policy.CandidatesExamined);
        }
        Assert.Null(policy.Ready(10, sight, targets, pilots));
        Assert.Equal(0, policy.CandidatesExamined);
        Assert.Null(policy.Ready(11, sight with { At = 11 }, targets, pilots));
        Assert.Equal(SustainableRaid.CandidateBudget, policy.CandidatesExamined);

        var rapid = new StrikeVolley.Loadout(17, [new(0, .031f, 400, .05f)]);
        var guns = pilots.Select(p => p with { Raid = p.Raid! with { Weapons = rapid } }).ToArray();
        var regenerating = targets.Select(t => t with { Profile = Profile with { ArmorRegen = 200, ShieldRegen = 300 } }).ToArray();
        Assert.Null(policy.Ready(12, sight with { At = 12 }, regenerating, guns));
        Assert.Equal(SustainableRaid.RoundBudget, policy.RoundsReplayed);
        Assert.InRange(policy.CandidatesExamined, 1, SustainableRaid.CandidateBudget);

        // Singles look sufficient by the optimistic bound. Only a mixed
        // group can kill. The reserved last replay must actually try it.
        targets[0] = targets[0] with { Profile = Profile with { ArmorDefense = 1, DamageConstants = [[1, .10f]] } };
        var wing = new SustainableRaid().Ready(10, sight, targets, pilots);
        Assert.NotNull(wing);
        Assert.InRange(wing.Members.Length, 2, StrikeVolley.MaxPlannedBombers);
        Assert.DoesNotContain(wing.Defender, wing.Members);
    }
}
