using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P801 — bomber massing. Real PCore001 station/loadout numbers feed a pure
/// volley requirement; the strike policy must hold a mass at the staging point
/// (visible element + up to two cloaked lone wolves on their own lanes) and
/// release everybody on one shared go time, aborting instead of trickling.
///
/// The loopback proof at the bottom replays the SAME real station data through
/// the port's native damage/regen laws: three bombers releasing together kill a
/// garrison, the same bombers released one at a time never do.
/// </summary>
public sealed class P801StrikeMassTests(ITestOutputHelper output)
{
    // PCore001 real records (verified by the test itself, not hard-coded blindly).
    private const short StealthBomberHull = 23;
    private const short SrmAntiBase1 = 157;   // missile 39: power 400, load 4 s, magazine 2 on hull 23
    private const short SrmAntiBase2 = 158;   // missile 40: power 500, load 4 s, magazine 2 on hull 23
    private const short GarrisonType = 315;   // 20k armor / 10k shield / 40 / 100, def 7/10
    private const short StrGarrisonType = 410; // 25k armor / 15k shield / 50 / 150, def 7/10

    private static StrikeVolley.Loadout Ab2Stealth() => new(StealthBomberHull,
        [new StrikeVolley.Mount(8, 500, 2, 4)], CloakCapable: true);

    private static StrikeVolley.Loadout Ab1Stealth() => new(StealthBomberHull,
        [new StrikeVolley.Mount(8, 400, 2, 4)], CloakCapable: true);

    private static StrikeVolley.Loadout Ab2Heavy() => new(889,
        [new StrikeVolley.Mount(8, 500, 5, 4)]);

    private static StrikeVolley.StationProfile Profile(StaticCore core, short type)
        => StrikeVolley.Profile(core, type) ?? throw new InvalidOperationException($"no station type {type}");

    // ── 1. Volley requirement on real core data ───────────────────────────

    [SkippableFact]
    public void Real_garrison_data_matches_the_native_damage_arithmetic()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        foreach (short id in new short[] { 44, 115, 315 })
        {
            var st = Profile(core, id);
            Assert.Equal(20000f, st.MaxArmor);
            Assert.Equal(10000f, st.MaxShield);
            Assert.Equal(40f, st.ArmorRegen);
            Assert.Equal(100f, st.ShieldRegen);
            Assert.Equal(7, st.ArmorDefense);
            Assert.Equal(10, st.ShieldDefense);
            // Half a shield refills in 5000/100 = 50 s: the window beyond which a
            // trickle can never accumulate.
            Assert.Equal(50, StrikeVolley.ShieldRefillSeconds(st), 3);
        }
        // Missile 40 (part 158): 500 power x damage row 8, armor def 7 = 10.0 and
        // shield def 10 = 10.0 -> one round is 5000 shield / 5000 armor points,
        // i.e. 1.0 -> 0.5 of the garrison shield (the rig's measured hit).
        var (armor, shield) = StrikeVolley.RoundDamage(Profile(core, GarrisonType), 8, 500);
        Assert.Equal(5000f, armor);
        Assert.Equal(5000f, shield);
        var (a1, s1) = StrikeVolley.RoundDamage(Profile(core, GarrisonType), 8, 400);
        Assert.Equal(4000f, a1);
        Assert.Equal(4000f, s1);
    }

    [SkippableFact]
    public void Requirement_scales_with_station_and_loadout_not_with_race()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var garrison = Profile(core, GarrisonType);
        var strGarrison = Profile(core, StrGarrisonType);

        var ab2 = StrikeVolley.Compute(garrison, [Ab2Stealth()], hullsAvailable: 12);
        output.WriteLine($"AB2/garrison: {ab2}");
        // 2 shield rounds + 5 hull rounds: the 6th round leaves ~4% armour because
        // 40/s armour regen runs during the 4 s magazine spacing, so the exact
        // kill is 7 rounds = 4 bombers; one spare hull is the margin.
        Assert.Equal(4, ab2.KillBombers);
        Assert.Equal(5, ab2.BombersRequired);
        Assert.Equal(7, ab2.ShotsToKill);
        Assert.Equal(5000f, ab2.ShieldDamagePerRound);
        Assert.Equal(5000f, ab2.ArmorDamagePerRound);

        var ab1 = StrikeVolley.Compute(garrison, [Ab1Stealth()], hullsAvailable: 12);
        output.WriteLine($"AB1/garrison: {ab1}");
        Assert.True(ab1.KillBombers >= 4, $"AB1 needs more rounds: {ab1}");

        var heavyGarrison = StrikeVolley.Compute(strGarrison, [Ab2Stealth()], hullsAvailable: 12);
        output.WriteLine($"AB2/str-garrison: {heavyGarrison}");
        Assert.True(heavyGarrison.KillBombers > ab2.KillBombers, "the Str garrison is tougher");

        // The same real loadout on a heavy bomber (5-round magazine) needs fewer
        // hulls than the 2-round stealth bomber: the requirement is data-driven.
        var hvy = StrikeVolley.Compute(garrison, [Ab2Heavy()], hullsAvailable: 12);
        output.WriteLine($"AB2 heavy/garrison: {hvy}");
        Assert.True(hvy.KillBombers < ab2.KillBombers, $"heavy bomber: {hvy}");

        // Every class-0 garrison across the core's civ station sets resolves and
        // asks for a real mass (never a solo/duo trickle).
        var garrisonTypes = core.StationTypes.Values.Where(s => s.ClassId == 0).Select(s => s.StationTypeId).ToArray();
        Assert.True(garrisonTypes.Length >= 4);
        foreach (short type in garrisonTypes)
        {
            var r = StrikeVolley.Compute(Profile(core, type), [Ab2Stealth()], hullsAvailable: 12);
            Assert.True(r.KillBombers >= 2, $"station {type}: {r}");
            Assert.True(r.BombersRequired >= r.KillBombers);
        }
    }

    [SkippableFact]
    public void The_same_bombers_massed_kill_and_trickled_never_do()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var station = Profile(core, GarrisonType);
        var bomber = Ab2Stealth();

        // Massed: four bombers on one go time, two 4 s-spaced rounds each
        // (8 rounds = the exact 7-round kill plus one round of margin).
        var massed = StrikeVolley.Simulate(station, [bomber, bomber, bomber, bomber], StrikeVolley.VolleyWindowSeconds);
        output.WriteLine($"massed: {massed}");
        Assert.True(massed.Killed);
        Assert.Equal(7, massed.ShotsFired);   // the 7th round is the kill
        Assert.Equal(5000f, massed.FirstShieldDamage);
        Assert.Equal(0f, massed.ShieldAfter);
        Assert.Equal(0f, massed.ArmorAfter);

        // Trickle: the SAME four bombers, released one minute apart (the old
        // finale relaunch stream). The shield refills between arrivals, so the
        // armour never reaches zero.
        var trickle = new List<(double At, StrikeVolley.Loadout Owner, StrikeVolley.Shot Shot)>();
        for (int wave = 0; wave < 4; wave++)
            foreach (var shot in StrikeVolley.Schedule(bomber))
                trickle.Add((wave * 60 + shot.Time, bomber, shot));
        var streamed = StrikeVolley.Replay(station, trickle, horizon: 240);
        output.WriteLine($"trickle: {streamed}");
        Assert.False(streamed.Killed);
        Assert.True(streamed.ArmorAfter > .1f,
            $"a 60 s trickle must not accumulate armour damage: {streamed}");
    }

    // ── 2. Staging hold / commit / abort ──────────────────────────────────

    private static ResearchTactics.Target[] Targets(StaticCore core) =>
    [
        new(50, IgcObjectType.Station, 1, new(0, 0, 0), true),
        new(300, IgcObjectType.Warp, 1, new(1000, 0, 0), true, DestinationSector: 2),
        new(301, IgcObjectType.Warp, 3, new(2000, 0, 0), true, DestinationSector: 2),
        new(100, IgcObjectType.Station, 2, new(5000, 0, 0), false, Garrison: true,
            StationTypeId: GarrisonType, Profile: StrikeVolley.Profile(core, GarrisonType)),
    ];

    private static ResearchTactics.Pilot Wolf(short id, short sector, float x, bool cloak = true)
        => new(id, sector, new(x, 0, 0), false, true, Ab2Stealth(), cloak);

    private static ResearchTactics.Pilot Plain(short id, short sector, float x)
        => new(id, sector, new(x, 0, 0), false, true, Ab2Heavy(), false);

    [SkippableFact]
    public void Massed_wave_holds_until_the_required_volley_assembles_then_releases()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var targets = Targets(core);
        var tactics = new ResearchTactics();
        // Four heavy bombers available -> requirement 3 (2 kill + one spare).
        ResearchTactics.Pilot[] pilots =
        [
            Plain(1, 1, 1050), Plain(2, 1, 9000), Plain(3, 1, 9200), Plain(4, 1, 9400),
        ];
        var early = tactics.Tick(0, true, pilots, targets, strikeRoster: 4);
        // One of four at the rally: order a goto, never an attack.
        Assert.DoesNotContain(early, o => o.Command == OrderChange.CidAttack);
        Assert.Contains(early, o => o.Command == OrderChange.CidGoto && o.Target == 300);
        Assert.Empty(tactics.TakeStrikeEvents());
        // Still short after 15 s: no attack.
        Assert.DoesNotContain(tactics.Tick(15, true, pilots, targets, strikeRoster: 4),
            o => o.Command == OrderChange.CidAttack);

        // Two more arrive at the rally; the three-bomber quota is met.
        pilots[1] = Plain(2, 1, 1100);
        pilots[2] = Plain(3, 1, 1200);
        var ready = tactics.Tick(20, true, pilots, targets, strikeRoster: 4);
        Assert.DoesNotContain(ready, o => o.Command == OrderChange.CidAttack);
        // Go time is 5 s after assembly; nobody attacks before it.
        Assert.DoesNotContain(tactics.Tick(24, true, pilots, targets, strikeRoster: 4),
            o => o.Command == OrderChange.CidAttack);
        var release = new List<ResearchTactics.Order>();
        release.AddRange(tactics.Tick(25, true, pilots, targets, strikeRoster: 4));
        release.AddRange(tactics.Tick(26, true, pilots, targets, strikeRoster: 4));
        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.Commit, commit.Kind);
        Assert.Equal("massed", commit.Reason);
        Assert.Equal(3, commit.Required);
        Assert.Equal(3, commit.Assembled);
        Assert.Equal(3, commit.Members);
        Assert.Equal(0, commit.LoneWolves);
        Assert.Equal(3, release.Count(o => o.Command == OrderChange.CidAttack)); // 2 orders/tick cap
        Assert.Contains(release, o => o.Command == OrderChange.CidAttack && o.Target == 100);
    }

    [SkippableFact]
    public void An_unassemblable_volley_aborts_instead_of_trickling_in()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var targets = Targets(core);
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots = [Plain(1, 1, 1050), Plain(2, 1, 9000)];   // one at the rally
        tactics.Tick(0, true, pilots, targets, strikeRoster: 4);
        var orders = tactics.Tick(ResearchTactics.StagingHardCapSeconds + 1, true, pilots, targets, strikeRoster: 4);
        Assert.DoesNotContain(orders, o => o.Command == OrderChange.CidAttack);
        var abort = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(ResearchTactics.StrikeEventKind.Abort, abort.Kind);
        Assert.Equal("insufficient-volley", abort.Reason);
        Assert.Equal(3, abort.Required);
        Assert.Equal(1, abort.Assembled);
        Assert.True(abort.WaitSeconds >= ResearchTactics.StagingHardCapSeconds);
    }

    [SkippableFact]
    public void Lone_wolves_take_their_own_aleph_and_join_only_the_shared_go_time()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var targets = Targets(core);
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots =
        [
            Wolf(1, 3, 2000), Wolf(2, 3, 2100),   // two cloak-capable wolves on warp 301's lane
            Plain(3, 1, 1050), Plain(4, 1, 1100),
        ];
        var early = new List<ResearchTactics.Order>();
        for (int t = 0; t <= 4; t++) early.AddRange(tactics.Tick(t, true, pilots, targets, strikeRoster: 4));
        // Nobody attacks before the shared go time (first commit is at t=5).
        Assert.DoesNotContain(early, o => o.Command == OrderChange.CidAttack);
        var orders = new List<ResearchTactics.Order>(early);
        for (int t = 5; t <= 8; t++) orders.AddRange(tactics.Tick(t, true, pilots, targets, strikeRoster: 4));
        var events = tactics.TakeStrikeEvents();
        var commit = Assert.Single(events);
        Assert.Equal(ResearchTactics.StrikeEventKind.Commit, commit.Kind);
        Assert.Equal(4, commit.Members);
        Assert.Equal(2, commit.Visible);
        Assert.Equal(2, commit.LoneWolves);

        // Each wolf is routed to the OTHER aleph into the target sector, never the
        // visible element's rally lane.
        Assert.Contains(orders, o => o.Recipient == 1 && o.Command == OrderChange.CidGoto && o.Target == 301);
        Assert.Contains(orders, o => o.Recipient == 2 && o.Command == OrderChange.CidGoto && o.Target == 301);
        Assert.DoesNotContain(orders, o => (o.Recipient == 1 || o.Recipient == 2)
            && o.Command == OrderChange.CidGoto && o.Target == 300);
        Assert.Contains(orders, o => o.Recipient == 3 && o.Command == OrderChange.CidGoto && o.Target == 300);
        // On the go tick all four are in the wave, wolves included.
        Assert.Contains(orders, o => (o.Recipient == 1 || o.Recipient == 2)
            && o.Command == OrderChange.CidAttack && o.Target == 100);
    }

    [SkippableFact]
    public void Without_a_cloak_hull_the_strike_is_all_visible()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var targets = Targets(core);
        var tactics = new ResearchTactics();
        ResearchTactics.Pilot[] pilots = [Plain(1, 1, 1050), Plain(2, 1, 1100), Plain(3, 1, 1200), Plain(4, 1, 1300)];
        for (int t = 0; t <= 6; t++) tactics.Tick(t, true, pilots, targets, strikeRoster: 4);
        var commit = Assert.Single(tactics.TakeStrikeEvents());
        Assert.Equal(4, commit.Members);
        Assert.Equal(4, commit.Visible);
        Assert.Equal(0, commit.LoneWolves);
    }

    // ── 3. Finale feed ───────────────────────────────────────────────────

    [SkippableFact]
    public void Finale_bombers_wait_for_the_shared_go_and_a_relaunch_waits_for_the_next_wave()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var loadout = StrikeVolley.FromParts(core, StealthBomberHull,
            [new ExpandedPartData(SrmAntiBase2, 2, 0, ExpandedPartData.FullyMounted)]);
        Assert.NotNull(loadout);
        Assert.Single(loadout!.Mounts);
        Assert.Equal(2, loadout.Mounts[0].Shots);

        var requirement = StrikeVolley.Compute(Profile(core, GarrisonType), [loadout], hullsAvailable: 3);
        Assert.Equal(3, requirement.KillBombers);

        // A bomber in the committed wave does not release before the go time.
        Assert.False(StrikeVolley.ShouldRelease(now: 10, goTime: 15, inCommittedWave: true, alreadyReleased: false));
        Assert.True(StrikeVolley.ShouldRelease(now: 15, goTime: 15, inCommittedWave: true, alreadyReleased: false));
        // A relaunch that missed the wave (not in GoMembers) never attacks alone.
        Assert.False(StrikeVolley.ShouldRelease(now: 100, goTime: 15, inCommittedWave: false, alreadyReleased: false));
        // Already-released ships are not held again.
        Assert.True(StrikeVolley.ShouldRelease(now: 5, goTime: 15, inCommittedWave: true, alreadyReleased: true));
    }
}
