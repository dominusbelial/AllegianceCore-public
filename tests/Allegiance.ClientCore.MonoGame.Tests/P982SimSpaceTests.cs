using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P982SimSpaceTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1.5f, 160)]
    [InlineData(1f, 240)]
    [InlineData(0.8f, 300)]
    [InlineData(0.001f, 300)]
    public void Shell_budget_tracks_native_debris_level(float density, int count)
    {
        var dust = new SubtleDust(); dust.Step(Vector3.Zero, density);
        Assert.Equal(count, dust.Grains.Count);
        dust.Step(Vector3.Zero, 0); Assert.Empty(dust.Grains);
    }

    [Fact]
    public void Shell_is_seeded_hollow_and_stationary_grains_do_not_churn()
    {
        var a = new SubtleDust(17); var b = new SubtleDust(17);
        var eye = new Vector3(1234, 5678, -9000);
        a.Step(eye, 1); b.Step(eye, 1);
        Assert.Equal(a.Grains, b.Grains);
        var original = a.Grains.ToArray();
        for (int i = 0; i < 1000; i++) a.Step(eye, 1);
        Assert.Equal(original, a.Grains);
        Assert.Equal(1, a.Refills);
        Assert.All(a.Grains, g =>
        {
            Assert.InRange(Vector3.Distance(g.Position, eye), SubtleDust.InnerRadius - .001f, SubtleDust.OuterRadius + .001f);
            Assert.InRange(g.HalfExtent, .025f, .045f);
            Assert.InRange(g.Brightness, .16f, .28f);
        });
    }

    [Theory]
    [InlineData(14, 0)]
    [InlineData(15, 1)]
    [InlineData(40, 1)]
    [InlineData(60, .5f)]
    [InlineData(80, 0)]
    [InlineData(100, 0)]
    public void Shell_distance_fade_is_bounded(float distance, float expected)
        => Assert.Equal(expected, SubtleDust.DistanceFade(distance), 5);

    [Fact]
    public void Shell_recycles_only_out_of_range_grains_on_outer_surface()
    {
        var dust = new SubtleDust(6); dust.Step(Vector3.Zero, 1);
        var original = dust.Grains.ToArray(); var eye = new Vector3(25, 0, 0);
        dust.Step(eye, 1);
        int recycled = 0;
        for (int i = 0; i < original.Length; i++)
        {
            float distance = Vector3.Distance(original[i].Position, eye);
            if (distance >= SubtleDust.InnerRadius && distance <= SubtleDust.OuterRadius)
                Assert.Equal(original[i], dust.Grains[i]);
            else
            {
                recycled++;
                Assert.InRange(Vector3.Distance(dust.Grains[i].Position, eye), 79.999f, 80.001f);
            }
        }
        Assert.True(recycled > 0); Assert.Equal(1, dust.Refills);
    }

    [Fact]
    public void Shell_refills_on_jump_cut_level_change_and_reenable()
    {
        var dust = new SubtleDust(); dust.Step(Vector3.Zero, 1);
        dust.Step(new Vector3(100, 0, 0), 1); Assert.Equal(1, dust.Refills);
        dust.Step(new Vector3(201, 0, 0), 1); Assert.Equal(2, dust.Refills);
        dust.Step(new Vector3(201, 0, 0), 1, cameraCut: true); Assert.Equal(3, dust.Refills);
        dust.Step(new Vector3(201, 0, 0), 1.5f); Assert.Equal(160, dust.Grains.Count);
        dust.Step(Vector3.Zero, 0); dust.Step(Vector3.Zero, 1); Assert.Equal(5, dust.Refills);
    }

    [Theory]
    [InlineData((short)0, 0)]
    [InlineData((short)20, 80)]
    [InlineData((short)500, 500)]
    public void Sparse_layer_uses_native_sector_count_with_a_cap(short authored, int expected)
        => Assert.Equal(expected, SimStarMath.SectorCount(authored));

    [Fact]
    public void Sim_stars_are_neutral_seeded_bounded_unit_directions()
    {
        var stars = SimStarMath.Generate(123, 5000);
        Assert.Equal(500, stars.Length); Assert.True(stars.Length <= SimStarMath.BlackTierCount);
        Assert.Equal(stars, SimStarMath.Generate(123, 5000));
        Assert.NotEqual(stars, SimStarMath.Generate(124, 5000));
        Assert.Empty(SimStarMath.Generate(1, 0));
        Assert.Equal(12, SimStarMath.Generate(1, 12).Length);
        Assert.All(stars, s =>
        {
            Assert.InRange(s.Direction.Length(), .99999f, 1.00001f);
            Assert.Equal(s.Color.R, s.Color.G); Assert.Equal(s.Color.R, s.Color.B);
            Assert.InRange(s.Color.R, (byte)24, (byte)160);
        });
        Assert.True(stars.Select(s => s.Color.R).Distinct().Count() > 100);
    }

    private static Matrix Projection => Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), 16f / 9, 1, 80000);
    private static Vector2 Viewport => new(960, 540);

    [Fact]
    public void Translating_eye_does_not_move_a_direction_star_or_make_a_tail()
    {
        var view = Matrix.CreateRotationY(.1f);
        var translated = view; translated.Translation = new Vector3(10000, -55000, 24000);
        Assert.True(SimStarMath.Project(-Vector3.UnitZ, view, Projection, Viewport, out var a));
        Assert.True(SimStarMath.Project(-Vector3.UnitZ, translated, Projection, Viewport, out var b));
        Assert.Equal(a, b);
        var exposure = SimStarMath.ExposureView(view, translated, 1f / 60);
        Assert.Equal(Vector2.Zero, SimStarMath.Tail(-Vector3.UnitZ, translated, exposure, Projection, Viewport));
    }

    [Theory]
    [InlineData(0.1f, 0, 0)]
    [InlineData(0, .5f, 0)]
    [InlineData(0, 0, .5f)]
    [InlineData(.3f, .4f, .2f)]
    public void Streaks_use_the_same_exposure_at_30_60_and_144_hz(float x, float y, float z)
    {
        var axis = Vector3.Normalize(new Vector3(x, y, z));
        var now = Matrix.CreateFromAxisAngle(axis, .3f);
        var star = Vector3.Normalize(new Vector3(.2f, .1f, -1));
        Vector2? expected = null;
        foreach (float fps in new[] { 30f, 60f, 144f })
        {
            var previous = Matrix.CreateFromAxisAngle(axis, .3f - .6f / fps);
            var exposure = SimStarMath.ExposureView(previous, now, 1 / fps);
            var tail = SimStarMath.Tail(star, now, exposure, Projection, Viewport);
            Assert.InRange(tail.Length(), .1f, SimStarMath.MaxTailPixelsAt540 + .001f);
            if (expected is { } e) Assert.InRange(Vector2.Distance(e, tail), 0, .002f);
            expected = tail;
        }
    }

    [Fact]
    public void Streak_cap_scales_with_viewport_and_rejects_behind_eye()
    {
        var exposure = Matrix.CreateRotationY(.5f);
        var tail = SimStarMath.Tail(-Vector3.UnitZ, Matrix.Identity, exposure, Projection, Viewport);
        Assert.Equal(8f, tail.Length(), 3);
        Assert.Equal(16f, SimStarMath.Tail(-Vector3.UnitZ, Matrix.Identity, exposure, Projection, Viewport * 2).Length(), 3);
        Assert.Equal(Vector2.Zero, SimStarMath.Tail(Vector3.UnitZ, Matrix.Identity, exposure, Projection, Viewport));
        Assert.Equal(Vector2.Zero, SimStarMath.Tail(-Vector3.UnitZ, Matrix.Identity, Matrix.Identity, Projection, Viewport));
    }

    [Fact]
    public void Motion_history_cuts_on_sector_camera_jump_roll_and_time_gap()
    {
        var motion = new SpaceVisualMotion(); var options = new VisualOptions();
        Assert.True(motion.Step(options, Matrix.Identity, Vector3.Zero, 0, 1).Cut);
        Assert.False(motion.Step(options, Matrix.Identity, Vector3.Zero, .02, 1).Cut);
        Assert.True(motion.Step(options, Matrix.Identity, Vector3.Zero, .04, 2).Cut);
        Assert.True(motion.Step(options, Matrix.Identity, new Vector3(101, 0, 0), .06, 2).Cut);
        Assert.True(motion.Step(options, Matrix.CreateRotationZ(1), new Vector3(101, 0, 0), .08, 2).Cut);
        Assert.True(motion.Step(options, Matrix.Identity, Vector3.Zero, 2, 2).Cut);
    }

    [Fact]
    public void Duplicate_stationary_draw_does_not_refill_dust()
    {
        var motion = new SpaceVisualMotion(); var options = new VisualOptions(); var dust = new SubtleDust();
        for (int i = 0; i < 100; i++)
        {
            var frame = motion.Step(options, Matrix.Identity, Vector3.Zero, 1, 0);
            dust.Step(Vector3.Zero, 1, frame.Cut);
        }
        Assert.Equal(1, dust.Refills);
    }

    [Fact]
    public void Glare_is_angular_and_ghosts_only_live_in_the_central_cone()
    {
        Assert.Equal(.25f, SimGlare.Alpha);
        Assert.Equal(0, SimGlare.GhostAlpha(.7f)); Assert.Equal(0, SimGlare.GhostAlpha(-1));
        Assert.Equal(.25f, SimGlare.GhostAlpha(1), 5);
        Assert.InRange(SimGlare.GhostAlpha(.85f), .12499f, .12501f);
        Assert.Equal(SimGlare.DiscHalfPixels(2, 540) * 2, SimGlare.DiscHalfPixels(2, 1080), 4);
        Assert.InRange(SimGlare.DiscHalfPixels(2, 540), 3.2f, 3.4f);
    }

    [Theory]
    [InlineData(VisualFlags.SubtleDustKey, 'S')]
    [InlineData(VisualFlags.SimStarsKey, 'T')]
    [InlineData(VisualFlags.SimGlareKey, 'G')]
    [InlineData(VisualFlags.StarStreaksKey, 'K')]
    public void Each_flag_roundtrips_overrides_logs_and_has_a_live_menu_row(string key, char hotkey)
    {
        var profile = new VisualOptions(); Assert.False(profile.IsOn(key));
        profile.SetToggle(key, true);
        var restored = JsonSerializer.Deserialize<IntroPreferences>(JsonSerializer.Serialize(new IntroPreferences { Visuals = profile }));
        Assert.True(restored!.Visuals!.IsOn(key));
        var options = VisualOptions.Resolve(restored.Visuals, key + "=off", out var warnings);
        Assert.Empty(warnings); Assert.False(options.IsOn(key)); Assert.True(restored.Visuals.IsOn(key));
        Assert.Contains(key + "=off", options.Describe());
        var enabled = VisualOptions.Parse(key + "=on", out warnings);
        Assert.Empty(warnings); Assert.True(enabled.IsOn(key));
        foreach (var style in new[] { HudStyle.Classic, HudStyle.Modern })
        {
            Assert.Equal(style, new HudSettings { Style = style }.Style);
            var state = new OptionsMenuModel.OptionsState(true, true, false, false, true, .1f, Visuals: options);
            var menu = new OptionsMenuModel();
            menu.Row0 = menu.Rows(state).ToList().FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenVisualsMenu);
            menu.Activate(state);
            menu.Row0 = menu.Rows(state).ToList().FindIndex(r => r.Hotkey == hotkey);
            Assert.True(menu.Row0 >= 0); Assert.Equal(key, menu.VisualKeyAt(menu.Row0, state));
            menu.Row0 = 0; // hotkey from a different flag's row must resolve its own key
            Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, menu.CommandForHotkey(hotkey, state));
            Assert.Equal(key, menu.VisualKeyAt(menu.Row0, state));
            Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, menu.Activate(state));
            options.SetToggle(key, true);
            Assert.Contains(menu.Rows(state), r => r.Hotkey == hotkey && r.Label.Contains(": on - "));
            options.SetToggle(key, false);
        }
    }

    [Theory]
    [InlineData(360f)]
    [InlineData(480f)]
    [InlineData(540f)]
    [InlineData(1080f)]
    public void Eight_table_rows_and_navigation_hint_fit_both_styles(float height)
    {
        foreach (var style in new[] { HudStyle.Classic, HudStyle.Modern })
        {
            Assert.Equal(style, new HudSettings { Style = style }.Style);
            var (first, step) = OptionsMenuModel.LayoutRows(height, 8);
            Assert.True(first >= 108);
            Assert.InRange(step, 24, 40);
            Assert.True(first + 8 * step + 18 + 24 <= height);
        }
        if (height >= 480)
        {
            var (first, step) = OptionsMenuModel.LayoutRows(height, 4);
            Assert.Equal(height * .36f, first); Assert.Equal(40f, step);
        }
    }

    [Fact]
    public void Hd_tails_follow_the_baked_textures_actual_uv_mapping()
    {
        var pixels = Enumerable.Repeat(Color.Black, 32 * 32).ToArray(); pixels[8 * 32 + 8] = Color.White;
        VertexPositionColorTexture[] vertices = [new(new Vector3(-1, 1, -2), Color.White, Vector2.Zero),
            new(new Vector3(1, 1, -2), Color.White, Vector2.UnitX), new(new Vector3(-1, -1, -2), Color.White, Vector2.UnitY)];
        var sample = Assert.Single(HdStarSamples.Extract(pixels, 32, 32, vertices, [0, 1, 2]));
        var expected = Vector3.Normalize(new Vector3(-1 + 17f / 32, 1 - 17f / 32, -2));
        Assert.InRange(Vector3.Distance(expected, sample.Direction), 0, .00001f);
    }
}
