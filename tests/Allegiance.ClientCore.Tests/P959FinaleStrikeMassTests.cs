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
/// P959 — the corrected strike requirement, the commit rule and the wing
/// preparation. Every case is a hand computation over REAL PCore001 station data
/// (garrison 315, a real outpost) and the REAL SRM Anti-Base loadout, so a
/// regression in the landing/margin arithmetic fails here rather than only on a
/// rig run. Pure decisions (commit/staging, wing cap, pre-arm and early-pressure
/// timing) are pinned as tables because those are what turned a wave of 2-4
/// bombers into the rig's 7-of-13 zero-kill finales.
/// </summary>
public sealed class P959FinaleStrikeMassTests(ITestOutputHelper output)
{
    // PCore001 real records (verified by the assertions below, not assumed).
    private const short StealthBomberHull = 23;
    private const short SrmAntiBase2 = 158;   // missile 40: power 500, load 4 s, 2-round magazine
    private const short GarrisonType = 315;   // 20k armor / 10k shield / 40 / 100, def 7/10

    private static StrikeVolley.Loadout Ab2() => new(StealthBomberHull,
        [new StrikeVolley.Mount(8, 500, 2, 4)], CloakCapable: true);

    private static StrikeVolley.Loadout Ab1() => new(StealthBomberHull,
        [new StrikeVolley.Mount(8, 400, 2, 4)], CloakCapable: true);

    /// <summary>A real 5-round heavy bomber magazine (hull 889, the P801 case).</summary>
    private static StrikeVolley.Loadout Heavy() => new(889,
        [new StrikeVolley.Mount(8, 500, 5, 4)]);

    private static StrikeVolley.StationProfile Profile(StaticCore core, short type)
        => StrikeVolley.Profile(core, type) ?? throw new InvalidOperationException($"no station type {type}");

    // ── 1. requirement math on real station data ─────────────────────────────

    [SkippableFact]
    public void Real_garrison_requirement_accounts_for_measured_landing_and_defenders()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var garrison = Profile(core, GarrisonType);
        Assert.Equal(20000f, garrison.MaxArmor);
        Assert.Equal(10000f, garrison.MaxShield);
        // Real cost of one AB2 round: 5000 shield and 5000 armor -> a garrison
        // shield absorbs exactly 0.5 per round and four hulls release 8 rounds.
        var (armor, shield) = StrikeVolley.RoundDamage(garrison, 8, 500);
        Assert.Equal(5000f, armor);
        Assert.Equal(5000f, shield);

        // Ideal case (the P801 arithmetic): kill 4 + 1 spare = 5.
        var ideal = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18);
        output.WriteLine($"[p959] ideal: {ideal}");
        Assert.Equal(4, ideal.KillBombers);
        Assert.Equal(5, ideal.BombersRequired);
        Assert.Equal(1.0, ideal.LandingShare, 3);

        // Hand computation: half the rounds land -> 5 / 0.5 = 10 hulls.
        var half = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18, landingShare: .5);
        output.WriteLine($"[p959] landing 0.5: {half}");
        Assert.Equal(10, half.BombersRequired);
        Assert.Equal(.5, half.LandingShare, 3);
        Assert.Equal(5, half.SurvivalBombers);   // the extra 5 hulls the landing share demands

        // Hand computation: landing 0.5 AND ten defenders seen -> 10 + 10 = 20,
        // clamped to the wing cap (18). This is the case the rig shows: a
        // full-health garrison with a defended sector is never a 2-4 hull job.
        var defended = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18,
            survivalMargin: 1, defenders: 10, landingShare: .5);
        output.WriteLine($"[p959] landing 0.5 + 10 defenders: {defended}");
        Assert.Equal(18, defended.BombersRequired);
        Assert.Equal(13, defended.SurvivalBombers);   // 18 - the 5 ideal hulls

        // Observed losses size the wave the same way, and the bigger of the two
        // (losses vs defenders) wins.
        var losses = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18,
            survivalMargin: 1, losses: 7, defenders: 3, landingShare: 1);
        Assert.Equal(12, losses.BombersRequired);     // 5 + max(7, 3)
        Assert.Equal(7, losses.SurvivalBombers);
    }

    [SkippableFact]
    public void Real_outpost_needs_fewer_hulls_under_the_same_corrected_law()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var garrison = Profile(core, GarrisonType);
        // A real outpost-class station from the same core (class 1), the smallest
        // id for determinism. Its real numbers are printed, not assumed.
        var outpostType = core.StationTypes.Values.Where(s => s.ClassId == 1)
            .OrderBy(s => s.StationTypeId).First();
        var outpost = Profile(core, outpostType.StationTypeId);
        output.WriteLine($"[p959] outpost {outpostType.StationTypeId}: armor={outpost.MaxArmor} "
            + $"shield={outpost.MaxShield} regen={outpost.ArmorRegen}/{outpost.ShieldRegen} "
            + $"def={outpost.ArmorDefense}/{outpost.ShieldDefense}");
        Assert.True(outpost.MaxArmor < garrison.MaxArmor, "an outpost is lighter than a garrison");

        var ideal = StrikeVolley.Compute(outpost, [Ab2()], hullsAvailable: 18);
        output.WriteLine($"[p959] outpost ideal: {ideal}");
        Assert.True(ideal.KillBombers <= 6, $"outpost ideal kill: {ideal}");
        Assert.Equal(ideal.KillBombers + 1, ideal.BombersRequired);   // one spare when the roster allows

        // Hand computation of the corrected law on the SAME real data: divide the
        // ideal hull count by the landing share, then add the defenders.
        var corrected = StrikeVolley.Compute(outpost, [Ab2()], hullsAvailable: 18,
            survivalMargin: 1, defenders: 4, landingShare: .5);
        int expected = Math.Min(18, (int)Math.Ceiling((ideal.KillBombers + 1) / .5) + 4);
        output.WriteLine($"[p959] outpost corrected: {corrected} (expected {expected})");
        Assert.Equal(expected, corrected.BombersRequired);
        Assert.True(corrected.KillBombers <= ideal.KillBombers);
    }

    [SkippableFact]
    public void Landing_share_is_floored_and_never_raises_the_ideal_requirement()
    {
        Require.Artwork();
        var garrison = Profile(Catalog.RepresentativeCore(), GarrisonType);
        // A wave that lands nothing measurable saturates at the floor instead of
        // demanding an unassembleable wing.
        var dead = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18, landingShare: 0);
        Assert.Equal(StrikeVolley.LandingFloor, dead.LandingShare, 3);
        Assert.Equal(18, dead.BombersRequired);      // clamped by the wing cap
        // Above 1 (bad input) never shrinks below the ideal arithmetic.
        var clamped = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18, landingShare: 2.5);
        Assert.Equal(1.0, clamped.LandingShare, 3);
        Assert.Equal(5, clamped.BombersRequired);
        // NaN keeps the ideal case (unmeasured).
        var nan = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18, landingShare: double.NaN);
        Assert.Equal(1.0, nan.LandingShare, 3);
        Assert.Equal(5, nan.BombersRequired);
    }

    [SkippableFact]
    public void Margin_zero_and_a_full_landing_share_reproduce_todays_wave_arithmetic()
    {
        Require.Artwork();
        var garrison = Profile(Catalog.RepresentativeCore(), GarrisonType);
        var plain = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 12);
        var legacy = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 12,
            survivalMargin: 0, losses: 9, defenders: 9, landingShare: 1);
        Assert.Equal(plain.BombersRequired, legacy.BombersRequired);
        Assert.Equal(plain.KillBombers, legacy.KillBombers);
        Assert.Equal(plain.ShotsToKill, legacy.ShotsToKill);
        Assert.Equal(plain.SurvivalBombers, legacy.SurvivalBombers);
        Assert.Equal(5, legacy.BombersRequired);
    }

    [SkippableFact]
    public void Shield_regeneration_outlasts_the_volley_window_so_only_a_massed_wave_accumulates()
    {
        Require.Artwork();
        var garrison = Profile(Catalog.RepresentativeCore(), GarrisonType);
        // The real garrison refills half its shield in 50 s; the volley window is
        // 20 s, so a wave that cannot land inside one window is spent and the next
        // wave starts from a regenerated shield (native armour-first regen).
        Assert.Equal(50, StrikeVolley.ShieldRefillSeconds(garrison), 3);
        Assert.True(StrikeVolley.VolleyWindowSeconds < StrikeVolley.ShieldRefillSeconds(garrison));
        var requirement = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 18);
        Assert.Equal(StrikeVolley.VolleyWindowSeconds, requirement.WindowSeconds);
        Assert.Equal(7, requirement.ShotsToKill);
    }

    [Fact]
    public void Measured_rounds_come_from_the_real_pool_drops()
    {
        // Guards measured 1.0000 -> 0.7333 on a 10k shield: 2667 points.
        double rounds = BotManager.FinaleRoundsOf(shieldDrop: .2667f, hullDrop: 0,
            maxShield: 10000, maxArmor: 20000, shieldPerRound: 5000, armorPerRound: 5000);
        Assert.Equal(.5334, rounds, 3);
        // The overflow round that took 0.2713 of a 20k armour pool while the shield
        // was already empty is a full round plus the overflow share.
        double overflow = BotManager.FinaleRoundsOf(0, .2713f, 10000, 20000, 5000, 5000);
        Assert.Equal(1.0852, overflow, 3);
        // No damage, no rounds.
        Assert.Equal(0, BotManager.FinaleRoundsOf(0, 0, 10000, 20000, 5000, 5000));
    }

    [SkippableFact]
    public void A_mixed_wing_is_sized_for_its_weakest_usable_loadout()
    {
        Require.Artwork();
        var garrison = Profile(Catalog.RepresentativeCore(), GarrisonType);
        var ab2 = StrikeVolley.Compute(garrison, [Ab2()], hullsAvailable: 12);
        var heavy = StrikeVolley.Compute(garrison, [Heavy()], hullsAvailable: 12);
        // The real mixed case: a 5-round heavy magazine beside the 2-round SRM
        // Anti-Base stealth hull the wing actually flies.
        var mixed = StrikeVolley.Compute(garrison, [Heavy(), Ab2()], hullsAvailable: 12);
        output.WriteLine($"[p959] AB2 {ab2.KillBombers}/{ab2.BombersRequired}, "
            + $"heavy {heavy.KillBombers}/{heavy.BombersRequired}, mixed {mixed.KillBombers}/{mixed.BombersRequired}");
        Assert.True(heavy.KillBombers < ab2.KillBombers, "the 5-round magazine needs fewer hulls");
        // The mixed wing must be sized like an all-stealth wing: the rig's planes
        // asked for 3 hulls (the heavy-magazine answer) while the wave that formed
        // carried 2-round magazines.
        Assert.Equal(ab2.KillBombers, mixed.KillBombers);
        Assert.Equal(ab2.BombersRequired, mixed.BombersRequired);
    }

    // ── 2. commit / staging decision table ───────────────────────────────────

    [Fact]
    public void A_wave_commits_when_the_corrected_requirement_is_met_or_the_deadline_forces_it()
    {
        // Assembled the corrected requirement: go.
        Assert.Equal("massed", BotManager.FinaleCommitReason(15, 15, stagingSince: 100, now: 101));
        Assert.Equal("massed", BotManager.FinaleCommitReason(5, 5, 100, 5000));
        // Short of it, with plenty of match left: HOLD. The P801 flat 180 s cap is
        // gone, so this is the case that used to send two bombers at a full garrison.
        Assert.Null(BotManager.FinaleCommitReason(2, 15, 100, 100 + BotManager.FinaleStagingHardCapSeconds));
        Assert.Null(BotManager.FinaleCommitReason(2, 15, 100, 100 + 3600));
        // Short and holding for a wave that cannot be assembled inside the window:
        // the deadline math forces the biggest assembled mass it can.
        // lead 5 + window 20 + tail 15 = 40 s of flight/landing before the deadline.
        Assert.Equal("deadline-forced", BotManager.FinaleCommitReason(4, 15, 100, now: 260, deadlineAt: 300));
        Assert.Null(BotManager.FinaleCommitReason(4, 15, 100, now: 240, deadlineAt: 300));  // window still fits
        // Never a single hull, even deadline-forced.
        Assert.Null(BotManager.FinaleCommitReason(1, 15, 100, now: 290, deadlineAt: 300));
        // A one-bomber requirement still releases immediately (unchanged P801 path).
        Assert.Equal("massed", BotManager.FinaleCommitReason(1, 1, 100, 100));
        // No assembled hull / no staging clock: nothing to commit.
        Assert.Null(BotManager.FinaleCommitReason(0, 5, 100, 5000));
        Assert.Null(BotManager.FinaleCommitReason(5, 5, null, 5000));
        // An early pressure wave has no server deadline: only the requirement can
        // release it (double.PositiveInfinity is not a deadline).
        Assert.Null(BotManager.FinaleCommitReason(2, 15, 100, 10000, double.PositiveInfinity));
    }

    // ── 3. wing growth cap ──────────────────────────────────────────────────

    [Fact]
    public void The_wing_grows_to_the_corrected_requirement_and_never_past_its_cap()
    {
        Assert.Equal(15, BotManager.FinaleWingTarget(required: 15, reserved: 12, cap: 18));
        Assert.Equal(18, BotManager.FinaleWingTarget(20, 12, 18));    // capped
        Assert.Equal(12, BotManager.FinaleWingTarget(3, 12, 18));     // never shrinks the wing
        Assert.Equal(1, BotManager.FinaleWingTarget(0, 0, 18));       // at least one hull
        Assert.Equal(1, BotManager.FinaleWingTarget(15, 0, 0));       // degenerate cap
        Assert.Equal(9, BotManager.FinaleWingTarget(9, 2, 9));
    }

    [SkippableFact]
    public void A_wing_member_with_real_strike_hardware_flies_it_and_a_fighter_refits_first()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var garrison = Profile(core, GarrisonType);
        // The real AB2 stealth loadout is station-damaging hardware: the member can
        // fly it straight from the arm.
        var bomber = StrikeVolley.FromParts(core, StealthBomberHull,
            [new ExpandedPartData(SrmAntiBase2, 2, 0, ExpandedPartData.FullyMounted)]);
        Assert.NotNull(bomber);
        Assert.True(StrikeVolley.DamagesStation(garrison, bomber!));
        Assert.True(BotManager.FinaleWingMemberReady(true, grantedHullMatches: true));
        // Hardware that is not granted on the hull it is mounted on cannot be flown.
        Assert.False(BotManager.FinaleWingMemberReady(true, grantedHullMatches: false));
        // A hull with no station-damaging mount is not a strike hull at all.
        var empty = new StrikeVolley.Loadout(StealthBomberHull, []);
        Assert.False(StrikeVolley.DamagesStation(garrison, empty));
        Assert.False(BotManager.FinaleWingMemberReady(StrikeVolley.DamagesStation(garrison, empty), true));
        // The refit-first rule is an ordinary legal purchase, so the member docked at
        // home is exactly the bot whose granted hull does not match yet.
        Assert.False(BotManager.FinaleWingMemberReady(
            StrikeVolley.DamagesStation(garrison, bomber!), grantedHullMatches: false));
        // Real magazine math for the wave denominator: the AB2 hull puts 2 rounds in
        // one 20 s window that really reach this station.
        Assert.Equal(2, StrikeVolley.RoundsInWindow(bomber!, garrison));
    }

    // ── 4. pre-arm and the early wave, off by default ───────────────────────

    [Fact]
    public void Early_pressure_is_off_by_default_and_when_off_the_timeline_is_unchanged()
    {
        var defaults = new ShowcaseFinale.Settings();
        Assert.Equal(0, defaults.EarlyPressureMinutes);   // off: no pre-finale wave
        Assert.Equal(0, defaults.PrearmMinutes);          // pre-arm lead OFF by default (opt-in)
        Assert.Equal(1, defaults.SurvivalMargin);         // one hull per seen defender/loss
        Assert.Equal(18, defaults.MaxWing);
        Assert.Equal(900, defaults.DeadlineSeconds);

        // Off means the tick never fires, whatever the match age is.
        Assert.False(BotManager.FinaleEarlyPressureDue(0, 30, 1200, alreadyArmed: false));
        Assert.False(BotManager.FinaleEarlyPressureDue(0, 0.25, 20, alreadyArmed: false));
        // On: fires once, inside its window, and never after the finale itself.
        Assert.True(BotManager.FinaleEarlyPressureDue(18, 30, 1080, false));
        Assert.False(BotManager.FinaleEarlyPressureDue(18, 30, 1079, false));
        Assert.False(BotManager.FinaleEarlyPressureDue(18, 30, 1080, alreadyArmed: true));
        Assert.False(BotManager.FinaleEarlyPressureDue(18, 30, 1800, false));   // finale owns it
        Assert.False(BotManager.FinaleEarlyPressureDue(18, 0.25, 20, false));   // early >= finale
    }

    [Fact]
    public void Prearm_opens_only_inside_its_lead_and_never_for_zero()
    {
        // Finale at 30 min, 2 min lead: opens at 28 min.
        Assert.False(BotManager.FinalePrearmDue(2, 30, 1679));
        Assert.True(BotManager.FinalePrearmDue(2, 30, 1680));
        Assert.True(BotManager.FinalePrearmDue(2, 30, 1799));
        Assert.False(BotManager.FinalePrearmDue(0, 30, 1799));       // off
        // A lead longer than the match would open at t=0 but is clamped, not invalid.
        Assert.True(BotManager.FinalePrearmDue(60, 30, 0));
        // Short local finales (finale_after_minutes 0.25) still get a lead at t=0.
        Assert.True(BotManager.FinalePrearmDue(2, 0.25, 0));
    }

    [Fact]
    public void The_selection_predicate_arms_the_sides_the_finale_will_use()
    {
        Assert.True(ShowcaseFinale.SideSelected("all", 0));
        Assert.True(ShowcaseFinale.SideSelected("both", 1));
        Assert.True(ShowcaseFinale.SideSelected("2", 2));
        Assert.False(ShowcaseFinale.SideSelected("2", 0));
        Assert.True(ShowcaseFinale.SideSelected("leader", 1));    // leader is decided at the trigger
    }

    [Fact]
    public void Settings_validate_the_new_knobs()
    {
        new ShowcaseFinale.Settings(EarlyPressureMinutes: 18, PrearmMinutes: 3, MaxWing: 18,
            DeadlineSeconds: 900).Validate();
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(MaxWing: 0).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(MaxWing: 19).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(DeadlineSeconds: -1).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(PrearmMinutes: -1).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(EarlyPressureMinutes: -1).Validate());
    }
}
