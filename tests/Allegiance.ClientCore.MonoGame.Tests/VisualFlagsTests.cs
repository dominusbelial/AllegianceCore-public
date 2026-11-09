using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P979 — the <c>visuals.*</c> flag framework: one table drives the profile
/// group, the <c>--visuals</c> launch override, the options page and the
/// startup log. Every flag defaults OFF (the native look stays the default
/// path); unknown profile keys are ignored, and a missing group resolves to the
/// defaults.
/// </summary>
public sealed class VisualFlagsTests
{
    [Fact]
    public void Every_flag_defaults_off_and_the_table_is_well_formed()
    {
        var options = new VisualOptions();
        Assert.False(options.RadialVisibility);
        Assert.False(options.RemoteTimeline);
        Assert.Equal(10000f, options.ViewRange);
        Assert.Contains(VisualFlags.Definitions, d => d.Key == VisualFlags.RadialVisibilityKey);
        Assert.Contains(VisualFlags.Definitions, d => d.Key == VisualFlags.ViewRangeKey);
        Assert.Contains(VisualFlags.Definitions, d => d.Key == VisualFlags.RemoteTimelineKey);
        // A table of definitions, not code: every row is complete enough for the
        // options page to render it without special-casing the flag.
        foreach (var definition in VisualFlags.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Key));
            Assert.False(string.IsNullOrWhiteSpace(definition.Label));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            if (definition.Kind == VisualFlagKind.Toggle) Assert.False(definition.DefaultOn);
            else Assert.True(definition.MaxValue > definition.MinValue);
        }
        Assert.Equal("radialVisibility=off remoteTimeline=off viewRange=10000", options.Describe());
    }

    [Theory]
    [InlineData("radialVisibility=on", true, 10000f)]
    [InlineData("radialVisibility=ON", true, 10000f)]
    [InlineData("RadialVisibility=true", true, 10000f)]
    [InlineData("radialVisibility=1", true, 10000f)]
    [InlineData("radialVisibility", true, 10000f)]           // bare key = on
    [InlineData("radialVisibility=off", false, 10000f)]
    [InlineData("radialVisibility=false,viewRange=12000", false, 12000f)]
    [InlineData(" radialVisibility = on , viewRange = 18000 ", true, 18000f)]
    public void Launch_override_parses_toggles_and_numbers(string spec, bool expectedOn, float expectedRange)
    {
        var options = VisualOptions.Parse(spec, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(expectedOn, options.RadialVisibility);
        Assert.Equal(expectedRange, options.ViewRange);
    }

    [Theory]
    [InlineData("remoteTimeline", true)]
    [InlineData("remoteTimeline=on", true)]
    [InlineData("RemoteTimeline=true", true)]
    [InlineData("remoteTimeline=off", false)]
    public void Remote_timeline_launch_override_is_a_p979_visual_flag(string spec, bool expected)
    {
        var options = VisualOptions.Parse(spec, out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(expected, options.RemoteTimeline);
        Assert.False(options.RadialVisibility);
        Assert.Equal(10000f, options.ViewRange);
    }

    [Fact]
    public void Unknown_or_malformed_override_entries_warn_and_leave_other_keys_applied()
    {
        var options = VisualOptions.Parse(
            "skybox=on,radialVisibility=maybe,viewRange=twelve,radialVisibility=on", out var warnings);
        Assert.True(options.RadialVisibility);
        Assert.Equal(10000f, options.ViewRange);
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("unknown key 'skybox'"));
        Assert.Contains(warnings, w => w.Contains("not on/off"));
        Assert.Contains(warnings, w => w.Contains("not a number"));
    }

    [Fact]
    public void Numeric_flags_clamp_to_the_table_bounds_with_a_warning()
    {
        var high = VisualOptions.Parse("viewRange=99999", out var highWarnings);
        Assert.Equal(50000f, high.ViewRange);
        Assert.Single(highWarnings);
        var low = VisualOptions.Parse("viewRange=10", out var lowWarnings);
        Assert.Equal(1000f, low.ViewRange);
        Assert.Single(lowWarnings);
        // Direct setters clamp the same way (the options page steps go through it).
        var direct = new VisualOptions();
        direct.SetValue(VisualFlags.ViewRangeKey, 1f);
        Assert.Equal(1000f, direct.ViewRange);
        direct.SetValue(VisualFlags.ViewRangeKey, float.NaN);
        Assert.Equal(10000f, direct.ViewRange);
    }

    [Fact]
    public void Launch_override_wins_over_the_profile_and_never_aliases_it()
    {
        var profile = new VisualOptions();
        profile.SetToggle(VisualFlags.RadialVisibilityKey, true);
        profile.SetValue(VisualFlags.ViewRangeKey, 14000f);

        var effective = VisualOptions.Resolve(profile, "radialVisibility=off", out var warnings);
        Assert.Empty(warnings);
        Assert.False(effective.RadialVisibility);
        Assert.Equal(14000f, effective.ViewRange);
        // Resolve works on a copy: the profile keeps what the player saved.
        Assert.True(profile.RadialVisibility);
        Assert.Equal(14000f, profile.ViewRange);
    }

    [Fact]
    public void Missing_group_and_unknown_json_keys_are_ignored()
    {
        var missing = JsonSerializer.Deserialize<VisualOptions>("null") ?? new VisualOptions();
        Assert.False(missing.RadialVisibility);
        Assert.Equal(10000f, missing.ViewRange);

        var json = JsonSerializer.Deserialize<VisualOptions>(
            """{ "radialVisibility": true, "remoteTimeline": true, "viewRange": 13000, "futureFlag": true, "tonemap": "film" }""");
        Assert.NotNull(json);
        Assert.True(json!.RadialVisibility);
        Assert.True(json.RemoteTimeline);
        Assert.Equal(13000f, json.ViewRange);

        var garbage = JsonSerializer.Deserialize<VisualOptions>("""{ "viewRange": "wide" }""");
        Assert.Equal(10000f, garbage!.ViewRange);
    }

    [Fact]
    public void Player_profile_round_trips_the_visuals_group()
    {
        string path = Path.Combine(Path.GetTempPath(), "p979-visuals-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new IntroPreferences
            {
                Visuals = new VisualOptions(),
            };
            prefs.Visuals.SetToggle(VisualFlags.RadialVisibilityKey, true);
            prefs.Visuals.SetToggle(VisualFlags.RemoteTimelineKey, true);
            prefs.Visuals.SetValue(VisualFlags.ViewRangeKey, 16000f);
            prefs.Save(path);

            string written = File.ReadAllText(path);
            Assert.Contains("\"visuals\"", written);
            Assert.Contains("\"radialVisibility\": true", written);
            Assert.Contains("\"viewRange\": 16000", written);
            Assert.Contains("\"remoteTimeline\": true", written);

            var loaded = IntroPreferences.Load(path);
            Assert.NotNull(loaded.Visuals);
            Assert.True(loaded.Visuals!.RadialVisibility);
            Assert.True(loaded.Visuals.RemoteTimeline);
            Assert.Equal(16000f, loaded.Visuals.ViewRange);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_profile_without_the_group_loads_the_table_defaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "p979-visuals-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var prefs = new IntroPreferences();
            Assert.Null(prefs.Visuals);
            prefs.Save(path);
            // The group stays absent until the Visuals page is first used, so a
            // legacy profile loads exactly the table defaults (every flag OFF).
            var loaded = IntroPreferences.Load(path);
            Assert.Null(loaded.Visuals);
            Assert.False((loaded.Visuals ?? new VisualOptions()).RadialVisibility);
            var resolved = VisualOptions.Resolve(loaded.Visuals, null, out var warnings);
            Assert.Empty(warnings);
            Assert.False(resolved.RadialVisibility);
            Assert.False(resolved.RemoteTimeline);
            Assert.Equal(10000f, resolved.ViewRange);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
