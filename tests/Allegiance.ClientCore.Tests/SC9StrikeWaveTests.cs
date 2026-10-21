using System.Diagnostics;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>SC9 — strike waves made of real anti-base hulls, aimed at the softest
/// observed station, escorted by native defend orders. The fixture mirrors SC7/SC8:
/// friendly station 50 and the rally aleph 300 in sector 1, the enemy Garrison 100
/// in sector 2.</summary>
public sealed class SC9StrikeWaveTests(ITestOutputHelper output)
{
    private static readonly ResearchTactics.Target[] BaseTargets =
    [
        new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
        new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
        new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false, Garrison: true),
    ];

    private static ResearchTactics.Target[] TargetsWith(params ResearchTactics.Target[] extra)
        => [.. BaseTargets, .. extra];

    private static ResearchTactics.Pilot Strike(short id, short sector, float x, float y = 0)
        => new(id, sector, new Vector3(x, y, 0), false, true);

    private static ResearchTactics.Pilot Fighter(short id, short sector, float x, float y = 0)
        => new(id, sector, new Vector3(x, y, 0), false, false);

    private static ResearchTactics.Pilot[] AtRally(params short[] ids)
        => ids.Select((id, i) => Strike(id, 1, 1000 + i * 50, i * 40)).ToArray();

    private static DataStationIGC Station(short id, short side, short sector, float x = 0)
        => new(Position: [x, 0f, 0f], Up: [0f, 1f, 0f], Forward: [0f, 0f, -1f],
            Rotation: [0f, 0f, 0f, 0f], ClusterId: sector, SideId: side, StationId: id,
            StationTypeId: 1, BpHull: 255, BpShield: 255, Name: "base");

    // ── 1. Capability + roster quorum ───────────────────────────────────────

    [SkippableFact]
    public void Capability_requires_the_hull_station_threat_flag_not_the_requested_role()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        short stationThreatHull = core.HullTypes.Values
            .First(h => h.HasCapability(DataHullTypeIGC.HabmThreatToStation)).HullId;
        short fighterHull = core.HullTypes.Values
            .First(h => h.HasCapability(DataHullTypeIGC.HabmFighter)
                && !h.HasCapability(DataHullTypeIGC.HabmThreatToStation)).HullId;

        // Role "bomber" alone is not enough: a temporary Scout/Fighter fallback
        // hull must be an ordinary (non-strike) pilot.
        Assert.True(BotManager.IsStrikeCapable(false, "bomber", stationThreatHull));
        Assert.False(BotManager.IsStrikeCapable(false, "bomber", fighterHull));
        // The finale flag does not bypass the hull capability either.
        Assert.False(BotManager.IsStrikeCapable(true, "bomber", fighterHull));
        // The role is still a gate: a capable hull flown by a non-bomber is not a
        // strike pilot.
        Assert.False(BotManager.IsStrikeCapable(false, "fighter", stationThreatHull));
    }

    [Fact]
    public void Roster_quorum_counts_the_whole_roster_not_the_launched_pilots()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] one = [Strike(1, 1, 1000)];
        tactics.Tick(0, true, one, BaseTargets, strikeRoster: 4);

        // One launched pilot, a four-bomber roster: no wave of one, ever — not at
        // the first timeout, nor the second, nor the third.
        for (int i = 1; i <= 3; i++)
        {
            var orders = tactics.Tick(ResearchTactics.RallyTimeoutSeconds * i + i, true, one, BaseTargets, strikeRoster: 4);
            Assert.DoesNotContain(orders, o => o.Command == OrderChange.CidAttack);
            Assert.Empty(tactics.TakeStrikeEvents());
        }
    }

    [Fact]
    public void A_roster_of_one_still_commits_its_lone_pilot_after_the_timeout()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] one = [Strike(1, 1, 1000)];
        tactics.Tick(0, true, one, BaseTargets, strikeRoster: 1);

        var timeout = tactics.Tick(ResearchTactics.RallyTimeoutSeconds + 1, true, one, BaseTargets, strikeRoster: 1);
        Assert.Contains(timeout, o => o.Command == OrderChange.CidAttack && o.Target == 100);
        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(1, commit.Members);
        Assert.Equal("timeout", commit.Reason);
    }

    // ── 2. Target re-pick at every wave boundary ────────────────────────────

    [Fact]
    public void The_next_wave_re_picks_a_soft_station_and_re_rallies_through_its_aleph()
    {
        var tactics = new ResearchTactics();
        var firstWave = AtRally(1, 2, 3);
        var opening = tactics.Tick(0, true, firstWave, BaseTargets);
        Assert.Contains(opening, o => o.Command == OrderChange.CidAttack && o.Target == 100);
        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal((short)100, commit.Target);   // the Garrison is the only station
        tactics.Tick(2, true, firstWave, BaseTargets);

        // The three reach the target sector, so the wave lives.
        tactics.Tick(10, true,
            [Strike(1, 2, 5000), Strike(2, 2, 5000, 80), Strike(3, 2, 5000, -80)], BaseTargets);
        Assert.Empty(tactics.TakeStrikeEvents());

        // The wave docks. A fresh pair re-forms at the old rally and gets the old
        // aleph goto (the target set has not changed yet).
        ResearchTactics.Pilot[] pair = AtRally(4, 5);
        tactics.Tick(20, true, pair, BaseTargets);
        Assert.Equal(ResearchTactics.StrikeEventKind.End, Assert.Single(tactics.TakeStrikeEvents()).Kind);

        // A non-Garrison station + its aleph now appear: the next boundary re-picks
        // the soft station and moves the rally to the aleph into its sector.
        var expanded = TargetsWith(
            new(301, IgcObjectType.Warp, 1, new(5000, 0, 0), true, DestinationSector: 3),
            new(101, IgcObjectType.Station, 3, new(100, 0, 0), false));
        var atBoundary = tactics.Tick(22, true, pair, expanded);
        Assert.DoesNotContain(atBoundary, o => o.Command == OrderChange.CidAttack);
        // The changed goto goes out at the boundary (2 s after the old one).
        Assert.Contains(atBoundary, o => o.Command == OrderChange.CidGoto && o.Target == 301);

        // The next commit is the new, softer station (after its own rally timeout).
        ResearchTactics.Pilot[] atNewRally = [Strike(4, 1, 4900), Strike(5, 1, 4900, 80)];
        var later = tactics.Tick(22 + ResearchTactics.RallyTimeoutSeconds + 1, true, atNewRally, expanded);
        Assert.Contains(later, o => o.Command == OrderChange.CidAttack && o.Target == 101);
        var next = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.Commit, next.Kind);
        Assert.Equal((short)101, next.Target);
        Assert.Equal((short)3, next.Sector);
        Assert.Equal("timeout", next.Reason);
    }

    [Fact]
    public void A_new_station_does_not_change_the_target_while_a_wave_is_in_flight()
    {
        var tactics = new ResearchTactics();
        var wave = AtRally(1, 2, 3);
        tactics.Tick(0, true, wave, BaseTargets);
        tactics.Tick(2, true, wave, BaseTargets);
        tactics.TakeStrikeEvents();

        var expanded = TargetsWith(
            new(301, IgcObjectType.Warp, 1, new(5000, 0, 0), true, DestinationSector: 3),
            new(101, IgcObjectType.Station, 3, new(100, 0, 0), false));
        // The wave is still at the rally (inside the trip grace); a latecomer gets
        // the OLD rally goto and nothing is ordered onto the new station.
        ResearchTactics.Pilot[] withLatecomer = [.. wave, Strike(4, 1, 9000)];
        var inFlight = tactics.Tick(10, true, withLatecomer, expanded);
        Assert.Contains(inFlight, o => o.Command == OrderChange.CidGoto && o.Target == 300);
        Assert.DoesNotContain(inFlight, o => o.Command == OrderChange.CidGoto && o.Target == 301);
        Assert.DoesNotContain(inFlight, o => o.Command == OrderChange.CidAttack && o.Target == 101);
        Assert.Empty(tactics.TakeStrikeEvents());

        // Once the wave is gone the new station is eligible, so the next order
        // moves the rally instead (the previous tick kept target 100).
        ResearchTactics.Pilot[] pair = AtRally(5, 6);
        tactics.Tick(20, true, pair, expanded);   // end members-gone, still old rally
        var rerally = tactics.Tick(22, true, pair, expanded);
        Assert.Contains(rerally, o => o.Command == OrderChange.CidGoto && o.Target == 301);
        Assert.DoesNotContain(rerally, o => o.Command == OrderChange.CidGoto && o.Target == 300);
    }

    [Fact]
    public void A_damaged_Garrison_does_not_outrank_an_undamaged_soft_station()
    {
        // A scratched Garrison next to the pilot vs a pristine, farther soft station:
        // the Garrison is a last resort, so the soft station wins (batch-15 lock).
        ResearchTactics.Target[] targets =
        [
            new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
            new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false, Damaged: true, Garrison: true),
            new(101, IgcObjectType.Station, 2, new(9000, 0, 0), false),
        ];
        ResearchTactics.Pilot[] one = [Strike(1, 2, 5000)];
        var orders = new ResearchTactics().Tick(0, true, one, targets, strikeRoster: 1);
        Assert.Contains(orders, o => o.Command == OrderChange.CidAttack && o.Type == IgcObjectType.Station && o.Target == 101);
        Assert.DoesNotContain(orders, o => o.Command == OrderChange.CidAttack && o.Target == 100);
    }

    [SkippableFact]
    public void A_shield_damaged_station_is_preferred_over_an_undamaged_one()
    {
        Require.Artwork();   // ObserveTacticalTargets loads the representative core
        // The policy prefers Target.Damaged; this pins the observation that makes a
        // shield-only wound count as damaged (hull 1.0, shield 0.3), from the same
        // live station state the research loop reads.
        var world = new ClientWorld();
        world.Apply(new ExportStation { Station = Station(101, 1, 2, 9000) });
        world.Apply(new ExportStation { Station = Station(102, 1, 2, 5000) });
        world.Apply(new StationsUpdate
        {
            Entries =
            [
                new StationsUpdateEntry(101, 240, 72),    // 1.0 hull / 0.3 shield
                new StationsUpdateEntry(102, 240, 240),   // pristine and closer
            ],
        });
        var targets = BotManager.ObserveTacticalTargets(world, 0);
        Assert.True(targets.Single(t => t.Id == 101).Damaged);
        Assert.False(targets.Single(t => t.Id == 102).Damaged);

        // The strike pilot sits at the PRISTINE station (sector 2), so distance alone
        // would pick 102; only the shield wound makes 101 the target.
        ResearchTactics.Pilot[] one = [Strike(1, 2, 5000)];
        Assert.Contains(new ResearchTactics().Tick(0, true, one, targets, strikeRoster: 1),
            o => o.Command == OrderChange.CidAttack && o.Type == IgcObjectType.Station && o.Target == 101);
    }

    [SkippableFact]
    public void Observed_station_health_marks_a_shield_damaged_station_damaged()
    {
        Require.Artwork();   // ObserveTacticalTargets loads the representative core
        var world = new ClientWorld();
        world.Apply(new ExportStation { Station = Station(100, 1, 2) });
        // 240/240 hull, 72/240 shield = 1.0 / 0.3 on the wire.
        world.Apply(new StationsUpdate { Entries = [new StationsUpdateEntry(100, 240, 72)] });

        var target = Assert.Single(BotManager.ObserveTacticalTargets(world, 0),
            t => t.Type == IgcObjectType.Station && t.Id == 100);
        Assert.True(target.Damaged);
        Assert.Equal(1f, target.Hull, 3);
        Assert.Equal(0.3f, target.Shield, 3);
    }

    // ── 3. Escorts ──────────────────────────────────────────────────────────

    private static (ResearchTactics.Pilot[] Pilots, ResearchTactics.Target[] Targets) EscortFixture()
    {
        var pilots = new List<ResearchTactics.Pilot>();
        pilots.AddRange(AtRally(1, 2, 3));
        // 8 non-strike fighters in the rally sector; the nearest six are 10..15.
        for (short i = 0; i < 8; i++) pilots.Add(Fighter((short)(10 + i), 1, 1200 + i * 50));
        var targets = TargetsWith(new ResearchTactics.Target(60, IgcObjectType.Ship, 1, new(400, 0, 0), false));
        return (pilots.ToArray(), targets);
    }

    [Fact]
    public void A_committed_wave_gets_the_nearest_non_strike_fighters_as_defend_escorts()
    {
        var (pilots, targets) = EscortFixture();
        var tactics = new ResearchTactics();
        var orders = new List<ResearchTactics.Order>();
        for (int t = 0; t <= 12; t += 2) orders.AddRange(tactics.Tick(t, true, pilots, targets));

        var defend = orders.Where(o => o.Command == OrderChange.CidDefend).ToArray();
        Assert.Equal(new short[] { 10, 11, 12, 13, 14, 15 },
            defend.Select(o => o.Recipient).Distinct().Order());
        Assert.All(defend, o =>
        {
            Assert.Equal(IgcObjectType.Ship, o.Type);       // defend a ship, not the station
            Assert.Contains(o.Target, new short[] { 1, 2, 3 });   // a live wave member
        });
        Assert.DoesNotContain(orders, o => o.Recipient is 16 or 17 && o.Command == OrderChange.CidDefend);
        // An escort does not also take the non-strike loop's attack order.
        Assert.DoesNotContain(orders, o => o.Recipient is >= 10 and <= 15 && o.Command == OrderChange.CidAttack);

        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(6, commit.Escorts);
    }

    [Fact]
    public void Escorts_are_released_when_the_wave_ends_and_get_ordinary_orders()
    {
        var (pilots, targets) = EscortFixture();
        var tactics = new ResearchTactics();
        for (int t = 0; t <= 12; t += 2) tactics.Tick(t, true, pilots, targets);

        // The wave docks; only the fighters remain. A distant strike pilot keeps the
        // strike loop alive so this is the natural "members gone" end, not the
        // no-strike-pilots branch.
        var fighters = pilots.Where(p => !p.Strike).Append(Strike(9, 0, 9000)).ToArray();
        var afterwards = new List<ResearchTactics.Order>();
        for (int t = 14; t <= 30; t += 2) afterwards.AddRange(tactics.Tick(t, true, fighters, targets));

        var end = Assert.Single(tactics.TakeStrikeEvents(), e => e.Kind == ResearchTactics.StrikeEventKind.End);
        Assert.Equal("members-gone", end.Reason);
        Assert.Equal(6, end.Escorts);
        Assert.DoesNotContain(afterwards, o => o.Recipient is >= 10 and <= 15 && o.Command == OrderChange.CidDefend);
        foreach (short id in new short[] { 10, 11, 12, 13, 14, 15 })
            Assert.Contains(afterwards, o => o.Recipient == id && o.Command == OrderChange.CidAttack);
    }

    [Fact]
    public void An_escort_re_points_to_a_live_member_when_its_protectee_leaves_the_wave()
    {
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots = [.. AtRally(1, 2, 3), Fighter(10, 1, 1200)];
        var orders = new List<ResearchTactics.Order>();
        for (int t = 0; t <= 2; t += 2) orders.AddRange(tactics.Tick(t, true, pilots, BaseTargets));

        var first = Assert.Single(orders, o => o.Recipient == 10 && o.Command == OrderChange.CidDefend);
        Assert.Equal((short)1, first.Target);

        // Member 1 docks; 2 and 3 live on. The escort must defend a live member.
        ResearchTactics.Pilot[] remaining = [Strike(2, 1, 1050), Strike(3, 1, 1100), Fighter(10, 1, 1200)];
        var repointed = tactics.Tick(4, true, remaining, BaseTargets);
        var changed = Assert.Single(repointed, o => o.Recipient == 10 && o.Command == OrderChange.CidDefend);
        Assert.Equal((short)2, changed.Target);
    }

    // ── 4. Bot-side guard: a defend order is not "engage that friendly" ─────

    [Fact]
    public void A_defend_ship_order_is_accepted_without_adopting_the_protectee_as_a_target()
    {
        var defend = new QueuedCommandState(OrderChange.CidDefend, IgcObjectType.Ship, 7);
        var d = BotManager.DecideAcceptTranslate(defend, OrderChange.CidNone, 42);
        Assert.True(d.ShouldSendAccept);
        Assert.Null(d.OrderedTarget);   // never "engage the friendly protectee"

        // A CidAttack ship order still adopts its target (focus-fire path).
        var attack = new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, 9);
        var a = BotManager.DecideAcceptTranslate(attack, OrderChange.CidNone, 42);
        Assert.True(a.ShouldSendAccept);
        Assert.Equal((short)9, a.OrderedTarget);
    }

    // ── 5. Measurement: arrival + log lines ─────────────────────────────────

    [Fact]
    public void A_wave_counts_arrived_and_peak_on_target_from_a_scripted_path()
    {
        var tactics = new ResearchTactics();
        var targets = BaseTargets.Select(t => t.Id == 100 ? t with { Hull = 1f, Shield = 0.5f } : t).ToArray();
        var wave = AtRally(1, 2, 3);

        tactics.Tick(0, true, wave, targets);
        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(1f, commit.StationHull, 3);
        Assert.Equal(0.5f, commit.StationShield, 3);
        Assert.Equal(0, commit.Escorts);

        // One member reaches the station, then a second, then all three.
        tactics.Tick(10, true, [Strike(1, 2, 5000), Strike(2, 1, 1050), Strike(3, 1, 1100)], targets);
        tactics.Tick(20, true, [Strike(1, 2, 5000), Strike(2, 2, 5000, 100), Strike(3, 1, 1100)], targets);
        tactics.Tick(30, true, [Strike(1, 2, 5000), Strike(2, 2, 5000, 100), Strike(3, 2, 5000, -100)], targets);
        Assert.Empty(tactics.TakeStrikeEvents());

        // A late new strike pilot sees the wave gone -> one end event with the
        // arrival record and the duration.
        tactics.Tick(40, true, [Strike(9, 1, 9000)], targets);
        var end = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.End, end.Kind);
        Assert.Equal(3, end.Members);
        Assert.Equal(3, end.Arrived);
        Assert.Equal(3, end.PeakOnTarget);
        Assert.Equal(40, end.DurationSeconds, 3);
        Assert.Equal("members-gone", end.Reason);
        Assert.Equal(1f, end.StationHull, 3);
        Assert.Equal(0.5f, end.StationShield, 3);
    }

    [Fact]
    public void Strike_log_lines_carry_escorts_and_station_health_and_wave_arrival()
    {
        Assert.Equal(
            "[strike] side=1 wave=2 commit members=3 atRally=3 target=station#100 sector=2 escorts=6 stationHull=0.812 stationShield=0.400 reason=full",
            BotManager.StrikeLogLine(1, new(ResearchTactics.StrikeEventKind.Commit, 2, 3, 3, 100, 2, "full",
                Escorts: 6, StationHull: 0.812f, StationShield: 0.4f)));
        Assert.Equal(
            "[strike] side=0 wave=2 end members=3 arrived=2 peakOnTarget=3 escorts=4 dur=45.0s stationHull=0.812 stationShield=0.400 reason=no-members-in-target",
            BotManager.StrikeLogLine(0, new(ResearchTactics.StrikeEventKind.End, 2, 3, 0, 100, 2, "no-members-in-target",
                Escorts: 4, StationHull: 0.812f, StationShield: 0.4f, Arrived: 2, PeakOnTarget: 3, DurationSeconds: 45)));
        // Unknown observed health stays the documented -1 sentinel.
        Assert.Contains("stationHull=-1 stationShield=-1",
            BotManager.StrikeLogLine(0, new(ResearchTactics.StrikeEventKind.Commit, 1, 3, 3, 100, 2, "full")));
    }

    // ── 6. Per-tick cost ────────────────────────────────────────────────────

    // Takes ~7 s (three configurations x 1000 ticks x 107 pilots); over the 10 s
    // Fast budget on a loaded runner, so it is a Live-tier measurement.
    [Fact]
    [Trait("Tier", "Live")]
    public void Per_tick_cost_with_107_pilots_and_300_targets_stays_within_the_base()
    {
        ResearchTactics.Pilot[] Fleet()
        {
            var pilots = new List<ResearchTactics.Pilot>();
            for (short i = 0; i < 100; i++) pilots.Add(Fighter((short)(100 + i), 0, i * 10));
            for (short i = 0; i < 7; i++) pilots.Add(Strike((short)(1 + i), 1, 9000 + i * 50));
            return pilots.ToArray();
        }
        // Same 107 pilots / 300 targets in both runs; the base has no enemy
        // station to pick (the pre-SC9 steady state, which never re-picked), the
        // new shape makes 100 of the 300 targets stations.
        ResearchTactics.Target[] NoStations()
        {
            var targets = new List<ResearchTactics.Target>
            {
                new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
                new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
            };
            for (short i = 0; i < 298; i++)
                targets.Add(new((short)(500 + i), IgcObjectType.Ship, 0, new(i * 10, 500, 0), false));
            return targets.ToArray();
        }
        ResearchTactics.Target[] WithStations(short damaged)
        {
            var targets = new List<ResearchTactics.Target>
            {
                new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
                new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
            };
            for (short i = 0; i < 198; i++)
                targets.Add(new((short)(500 + i), IgcObjectType.Ship, 0, new(i * 10, 500, 0), false));
            for (short i = 0; i < 100; i++)
                targets.Add(new((short)(1000 + i), IgcObjectType.Station, 0, new(i * 100, 1000, 0), false,
                    Damaged: i == damaged, Garrison: i % 10 == 0));
            return targets.ToArray();
        }
        static double Measure(ResearchTactics tactics, ResearchTactics.Pilot[] pilots,
            ResearchTactics.Target[] targets, int ticks)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++) tactics.Tick(i * 0.25, true, pilots, targets, strikeRoster: 7);
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }
        // Worst case: the observed damage changes every tick, so the signature cache
        // always misses and PickStrikeStation really runs on every tick.
        static double MeasureChanging(ResearchTactics tactics, ResearchTactics.Pilot[] pilots,
            ResearchTactics.Target[] a, ResearchTactics.Target[] b, int ticks)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < ticks; i++) tactics.Tick(i * 0.25, true, pilots, i % 2 == 0 ? a : b, strikeRoster: 7);
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }

        var fleet = Fleet();
        var noStations = NoStations();
        var stationsA = WithStations(3);
        var stationsB = WithStations(4);
        Measure(new ResearchTactics(), fleet, stationsA, 50);   // warm up the JIT

        double baseMs = double.MaxValue, cachedMs = double.MaxValue, repickMs = double.MaxValue;
        for (int sample = 0; sample < 3; sample++)
        {
            baseMs = Math.Min(baseMs, Measure(new ResearchTactics(), fleet, noStations, 1000));
            cachedMs = Math.Min(cachedMs, Measure(new ResearchTactics(), fleet, stationsA, 1000));
            repickMs = Math.Min(repickMs, MeasureChanging(new ResearchTactics(), fleet, stationsA, stationsB, 1000));
        }
        output.WriteLine($"SC9 per-tick cost: base(no enemy station)={baseMs:F1} ms, cached(100 stations)={cachedMs:F1} ms, " +
            $"repick-every-tick(100 stations)={repickMs:F1} ms for 1000 ticks / 107 pilots / 300 targets (fastest of 3, same run)");
        Assert.True(cachedMs <= baseMs * 1.5,
            $"SC9 cached per-tick cost regressed: base={baseMs:F1} ms, cached={cachedMs:F1} ms");
        // Recorded, not budgeted: the un-cached worst case (the damage set changes
        // every single tick) on the same run.
        Assert.True(repickMs <= baseMs * 3,
            $"SC9 un-cached re-pick cost regressed: base={baseMs:F1} ms, repick={repickMs:F1} ms");
    }
}
