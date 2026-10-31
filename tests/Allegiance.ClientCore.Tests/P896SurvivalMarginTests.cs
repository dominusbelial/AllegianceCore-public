using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P896 — the survival margin, behind a setting that defaults OFF.
///
/// <para><c>StrikeVolley.Compute</c> alone sizes a wave as "bare kill + one
/// spare". At <c>SurvivalMargin = 0</c> that is unchanged (the P801/AIX
/// baseline). Above 0 the wave also carries
/// <c>ceil(margin × max(last-wave losses, defenders visible in the target
/// sector))</c> extra bombers, bounded by the side's reserved wing
/// (<c>Settings.Bombers</c>) and by <see cref="StrikeVolley.MaxPlannedBombers"/>,
/// so a larger wave can still be assembled inside the P801 staging cap instead of
/// out-waiting the deadline.</para>
/// </summary>
public sealed class P896SurvivalMarginTests(ITestOutputHelper output)
{
    /// <summary>PCore001 garrison 315 (20k armor / 10k shield, 40/100 regen, defense
    /// 7/10) — the same real record P801's volley arithmetic asserts.</summary>
    private const short GarrisonType = 315;

    private static StrikeVolley.StationProfile Profile()
    {
        Allegiance.TestInfra.Require.Artwork();
        return StrikeVolley.Profile(Catalog.RepresentativeCore(), GarrisonType)
            ?? throw new InvalidOperationException("no garrison 315 in the representative core");
    }

    /// <summary>A real Stealth Bomber magazine: two SRM Anti-Base 2 rounds, 4 s apart.</summary>
    private static StrikeVolley.Loadout Ab2() => new(23, [new StrikeVolley.Mount(8, 500, 2, 4)], CloakCapable: true);

    [SkippableFact]
    public void P896_Margin_zero_is_exactly_the_todays_wave_arithmetic()
    {
        var off = StrikeVolley.Compute(Profile(), [Ab2()], hullsAvailable: 12);
        // P801's asserted numbers for a 20k/10k garrison and a 2-round AB2 magazine.
        Assert.Equal(4, off.KillBombers);
        Assert.Equal(5, off.BombersRequired);
        Assert.Equal(0, off.SurvivalBombers);
        // The extra inputs are ignored while the margin is off — no accidental
        // balance change on the rig's AIX-comparable configuration.
        var withInputs = StrikeVolley.Compute(Profile(), [Ab2()], hullsAvailable: 12,
            survivalMargin: 0, losses: 9, defenders: 9);
        Assert.Equal(off, withInputs);
    }

    [SkippableTheory]
    [InlineData(0.5, 4, 0, 2)]   // 4 losses × 0.5 = 2 extra bombers
    [InlineData(0.5, 0, 3, 2)]   // no losses yet, but 3 defenders seen → 1.5 → 2
    [InlineData(1.0, 2, 5, 5)]   // defenders dominate: 5 extra
    [InlineData(0.25, 0, 1, 1)]  // 0.25 rounds up to one hull
    public void P896_Margin_sizes_the_wave_from_observed_losses_or_defenders(
        double margin, int losses, int defenders, int expectedSurvival)
    {
        var on = StrikeVolley.Compute(Profile(), [Ab2()], hullsAvailable: 12,
            survivalMargin: margin, losses: losses, defenders: defenders);
        output.WriteLine($"{on} margin={margin} losses={losses} defenders={defenders}");
        Assert.Equal(expectedSurvival, on.SurvivalBombers);
        Assert.Equal(on.KillBombers + 1 + expectedSurvival, on.BombersRequired);
        Assert.True(on.BombersRequired <= StrikeVolley.MaxPlannedBombers);
    }

    [SkippableFact]
    public void P896_Margin_never_exceeds_the_reserved_wing_or_the_planning_cap()
    {
        // The reserved wing (Settings.Bombers) is the hard bound: a margin cannot
        // ask for a wave the side never bought.
        var small = StrikeVolley.Compute(Profile(), [Ab2()], hullsAvailable: 6,
            survivalMargin: 1, losses: 20, defenders: 20);
        Assert.Equal(6, small.BombersRequired);
        Assert.Equal(1, small.SurvivalBombers);   // kill(4)+spare(1)+survival clamped to 6
        // ...and MaxPlannedBombers caps the planner even for a huge roster.
        var huge = StrikeVolley.Compute(Profile(), [Ab2()], hullsAvailable: 200,
            survivalMargin: 2, losses: 200, defenders: 200);
        Assert.Equal(StrikeVolley.MaxPlannedBombers, huge.BombersRequired);
    }

    [Fact]
    public void P896_Survival_margin_settings_default_off_and_validate_their_range()
    {
        var defaults = new ShowcaseFinale.Settings();
        Assert.Equal(0, defaults.SurvivalMargin);
        defaults.Validate();
        new ShowcaseFinale.Settings(SurvivalMargin: 0.5).Validate();
        new ShowcaseFinale.Settings(SurvivalMargin: 2).Validate();
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(SurvivalMargin: -0.1).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(SurvivalMargin: 2.1).Validate());
        Assert.Throws<ArgumentException>(() => new ShowcaseFinale.Settings(SurvivalMargin: double.NaN).Validate());

        // The MCP surface parses and validates the same knob (showcase/skirmish only).
        var tools = McpTools.Definitions();
        var schema = JsonSerializer.SerializeToElement(tools.Single(t => t.Name == "spawn_scenario").InputSchema);
        Assert.True(schema.GetProperty("properties").TryGetProperty("finale_survival_margin", out _));
        Dictionary<string, JsonElement> Parse(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal(0.5, McpTools.ReadFinaleSettings(Parse("{\"finale_survival_margin\":0.5}"), true).SurvivalMargin);
        Assert.Equal(0, McpTools.ReadFinaleSettings(Parse("{}"), true).SurvivalMargin);
        Assert.Throws<ArgumentException>(() =>
            McpTools.ReadFinaleSettings(Parse("{\"finale_survival_margin\":3}"), true));
    }
}
