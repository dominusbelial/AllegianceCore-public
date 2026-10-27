using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-19 — centre-HUD indicator blend, colours and toggles:
/// <list type="bullet">
/// <item>the additive (One/One) centre-indicator pass and its tint pre-scale
/// (native BlendModeAdd, engine/RasterD3D9.cpp:295-302);</item>
/// <item>the ShowJoystickIndicator toggle (HudSettings → HudModelAdapter →
/// HudContext → JoystickIndicatorVisible) and its Graphics/HUD overlay row;</item>
/// <item>the native joystickindicator.mdl chevron layout (y-up) and rasterizer;</item>
/// <item>the lock flower as four native DrawExpandedBlip quadrants.</item>
/// </list>
/// Headless where possible; source-content guards (same discipline as
/// <see cref="Hud16RadarIconTests"/>) where drawing needs a graphics device.
/// </summary>
public class Hud19CentreHudTests
{
    // ── 1. additive pass: tint pre-scale (One/One on premultiplied textures) ──

    [Fact]
    public void Additive_tint_pre_scales_rgb_by_alpha()
    {
        // Native BlendModeAdd (SRCALPHA/ONE on straight alpha) equals One/One
        // on the premultiplied textures, with tint.rgb pre-multiplied by the
        // native colour's alpha (indicator.cpp:149-150, 268-276, 361-362, 399-400).
        Assert.Equal(new Color(0.16f, 0.16f, 0.16f, 1f), HudRenderer.AdditiveTint(0.4f, 0.4f, 0.4f, 0.4f));
        Assert.Equal(new Color(0.165f, 0.165f, 0.165f, 1f), HudRenderer.AdditiveTint(0.33f, 0.33f, 0.33f, 0.5f));
        // (1-g, g, 0) with alpha 1 is unchanged.
        Assert.Equal(new Color(0.3f, 0.7f, 0f, 1f), HudRenderer.AdditiveTint(0.3f, 0.7f, 0f, 1f));
    }

    [Fact]
    public void Reticle_and_aim_crosshairs_draw_additively_but_velocity_and_lead_do_not()
    {
        // Wire guard (source-content, no graphics device needed): the reticle
        // and every centeroutbmp draw go through one One/One pass that is NOT
        // used by the velocity marker or lead surfaces (which stay on alpha).
        var src = Src(Path.Combine("Rendering", "HudRenderer.cs"));
        // The additive helper exists and uses the shared One/One state.
        Assert.Contains("private void DrawCentreIndicatorsAdditive(HudModel model, Viewport viewport, bool firing, bool turretView)", src);
        Assert.Contains("_sb.Begin(SpriteSortMode.Deferred, AdditiveOneOne)", src);
        // Expected pre-scaled tints (0.4·0.4 reticle; 0.33·0.5 grey crosshair).
        Assert.Contains("AdditiveTint(0.4f, 0.4f, 0.4f, 0.4f)", src);
        Assert.Contains("AdditiveTint(0.33f, 0.33f, 0.33f, 0.5f)", src);
        // The velocity marker and lead surfaces are drawn through DrawCentered /
        // DrawLine (alpha), never inside the additive helper: the helper only
        // references the reticle + CrosshairOut textures.
        var helper = Method(src, "private void DrawCentreIndicatorsAdditive(");
        Assert.Contains("Reticle", helper);
        Assert.Contains("CrosshairOut", helper);
        Assert.DoesNotContain("VelocityMarker", helper);
        Assert.DoesNotContain("LeadIn", helper);
        // The gauge pass shares the same One/One state (HUD-14c).
        Assert.Contains("_sb.Begin(SpriteSortMode.Deferred, AdditiveOneOne)", src);
    }

    // ── 2. ShowJoystickIndicator toggle ──────────────────────────────────────

    [Fact]
    public void ShowJoystickIndicator_defaults_to_on()
    {
        // native ctor default true (WinTrek.cpp:2739); LoadPreference default 1 (WinTrek.cpp:3059).
        Assert.True(new HudSettings().ShowJoystickIndicator);
        Assert.True(new HudSettings { ShowJoystickIndicator = true }.ShowJoystickIndicator);
    }

    [Fact]
    public void ShowJoystickIndicator_round_trips_through_save_and_load()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hud19-settings-" + Guid.NewGuid());
        string path = Path.Combine(dir, "hud.json");
        // HUD-19c — explicit-path Save/Load seam only; the process-global
        // ALLEGIANCE_HUD_SETTINGS env var is NOT touched (it raced other
        // parallel test classes that read HudSettings.DefaultPath).
        try
        {
            var s = new HudSettings { ShowJoystickIndicator = false };
            s.Save(path);
            var restored = HudSettings.Load(path, true);
            Assert.False(restored.ShowJoystickIndicator);
            Assert.True(new HudSettings { ShowJoystickIndicator = true }.ShowJoystickIndicator);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Adapter_threads_the_setting_into_JoystickIndicatorVisible()
    {
        // The adapter carries HudSettings.ShowJoystickIndicator into
        // HudContext.ShowJoystickIndicator, which gates JoystickIndicatorVisible.
        var frame = Frame();
        var on = HudModelAdapter.Create(frame, CameraMode.Cockpit, showJoystickIndicator: true);
        Assert.True(on.JoystickIndicatorVisible);
        Assert.True(on.Context.ShowJoystickIndicator);
        var off = HudModelAdapter.Create(frame, CameraMode.Cockpit, showJoystickIndicator: false);
        Assert.False(off.JoystickIndicatorVisible);
        Assert.False(off.Context.ShowJoystickIndicator);
        // Modern and classic renderers both gate on JoystickIndicatorVisible.
        var renderer = Src(Path.Combine("Rendering", "HudRenderer.cs"));
        Assert.Contains("if (model.JoystickIndicatorVisible", renderer);
    }

    [Fact]
    public void Graphics_overlay_has_the_joystick_row_and_it_toggles()
    {
        // Native Graphics Options "Joystick Indicator: On/Off" (WinTrek.cpp:4507,
        // :4526). P805 (row 470) added the Center HUD / Target HUD / Chat History
        // rows (13 → 16), P828 added the three radar label declutter rows (16 → 19)
        // and P836 added the HUD-level row (19 → 20), so Up/Down wrap on
        // GraphicsRowCount = 20.
        var gpu = Src("Game1.GpuBudget.cs");
        Assert.Contains("\"Joystick indicator: ", gpu);
        Assert.Contains("GraphicsRowCount = 20", gpu);
        Assert.Contains("case 12: _hudSettings.ShowJoystickIndicator", gpu);
        Assert.Contains("case 13: _hudSettings.ShowCenterHud", gpu);
        Assert.Contains("case 14: _hudSettings.ShowTargetHud", gpu);
        Assert.Contains("case 15: _chatHistoryVisible", gpu);
        Assert.Contains("case 16: _hudSettings.RadarDeclutter", gpu);

        // Live reflection: the row toggles the setting (and it round-trips).
        const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        void Set(string n, object v) => typeof(Game1).GetField(n, f)!.SetValue(game, v);
        object? Call(string n, params object[] a) => typeof(Game1).GetMethod(n, f)!.Invoke(game, a);
        var settings = new HudSettings { ShowJoystickIndicator = true };
        Set("_hudSettings", settings); Set("_upscaling", new UpscalingSettings());
        Set("_resolutionController", new DynamicResolutionController());
        Set("_flightGc", new FlightGcLatency(false));
        Set("_graphicsSettingsVisible", true); Set("_graphicsRow", 12);
        Set("_graphicsPrevious", new KeyboardState());
        Call("UpdateGraphicsSettings", new KeyboardState(Keys.Right));
        Assert.False(settings.ShowJoystickIndicator);
    }

    // ── 3. native joystick chevron layout + rasterizer ───────────────────────

    [Fact]
    public void Joystick_layout_matches_native_y_up_math()
    {
        // (yaw,pitch) = (0,+0.5): position (cx, cy+50), apex pointing DOWN.
        var centre = new Vector2(960f, 540f);
        var p = JoystickLayout.Position(centre, 0f, 0.5f);
        Assert.Equal(960f, p.X, 3);
        Assert.Equal(540f + 50f, p.Y, 3);
        var down = JoystickLayout.ApexDirection(0f, 0.5f);
        Assert.Equal(0f, down.X, 3);
        Assert.Equal(1f, down.Y, 3);       // pointing down on screen

        // (yaw,pitch) = (+0.5,0): position (cx−50, cy), apex pointing LEFT.
        var yaw = JoystickLayout.Position(centre, 0.5f, 0f);
        Assert.Equal(960f - 50f, yaw.X, 3);
        Assert.Equal(540f, yaw.Y, 3);
        var left = JoystickLayout.ApexDirection(0.5f, 0f);
        Assert.Equal(-1f, left.X, 3);
        Assert.Equal(0f, left.Y, 3);       // pointing left on screen
    }

    [Theory]
    // Native indicator.cpp:159-162: angle = atan(pitch/yaw) + π/2; if (yaw<0) angle += π.
    [InlineData(-0.5f, 0f, 3f * MathF.PI / 2f)]                    // atan(-0)=0 → π/2+π
    [InlineData(-0.5f, 0.25f, 4.2487414f)]                          // atan(-0.5)+π/2+π
    [InlineData(0.5f, 0.25f, 2.0344439f)]                           // atan(0.5)+π/2
    [InlineData(0f, 0.5f, MathF.PI)]                                // atan(+∞)=π/2 → π
    [InlineData(0f, -0.5f, 0f)]                                     // atan(−∞)=−π/2 → 0
    public void Joystick_angle_is_the_native_atan_form(float yaw, float pitch, float expected)
    {
        Assert.Equal(expected, JoystickLayout.Angle(yaw, pitch), 4);
    }

    [Fact]
    public void Joystick_apex_for_negative_yaw_points_right()
    {
        // yaw<0 (stick right of centre in the native translate centre − 100·yaw):
        // the chevron sits RIGHT of centre and, at angle 3π/2, its apex points
        // RIGHT on screen — the HUD-19 double π correction pointed it LEFT.
        var centre = new Vector2(960f, 540f);
        Assert.Equal(960f + 50f, JoystickLayout.Position(centre, -0.5f, 0f).X, 3);
        var apex = JoystickLayout.ApexDirection(-0.5f, 0f);
        Assert.Equal(1f, apex.X, 3);
        Assert.Equal(0f, apex.Y, 3);
    }

    [Fact]
    public void Modern_joystick_uses_the_classic_native_layout()
    {
        // HUD-19c — the Modern glyph's position/pointing come from the same
        // JoystickLayout as classic (screen y = centre.Y + 100·pitch), so the
        // parity matrix "both skins" claim holds; it is not mirrored.
        var modern = Method(Src(Path.Combine("Rendering", "ModernHudRenderer.cs")),
            "protected override void DrawJoystickIndicator(Vector2 center, float yaw, float pitch)");
        Assert.Contains("JoystickLayout.Position(center, yaw, pitch)", modern);
        Assert.Contains("JoystickLayout.ApexDirection(yaw, pitch)", modern);
        Assert.DoesNotContain("100f * pitch", modern);
        Assert.DoesNotContain("Atan2", modern);
    }

    [Fact]
    public void Additive_reticle_is_not_drawn_outside_the_cockpit()
    {
        // HUD-19c — native draws directioninbmp only under cmCockpit
        // (WinTrek/indicator.cpp:132, :149-150) ⇒ HudModel.ReticleVisible.
        var cockpit = HudModelAdapter.Create(Frame(), CameraMode.Cockpit, showJoystickIndicator: true);
        var chase = HudModelAdapter.Create(Frame(), CameraMode.Chase, showJoystickIndicator: true);
        Assert.True(cockpit.ReticleVisible);
        Assert.False(chase.ReticleVisible);
        Assert.True(HudRenderer.DrawsAdditiveReticle(cockpit, turretView: false, hasArtwork: true));
        Assert.False(HudRenderer.DrawsAdditiveReticle(chase, turretView: false, hasArtwork: true));
        Assert.False(HudRenderer.DrawsAdditiveReticle(cockpit, turretView: true, hasArtwork: true));
        Assert.False(HudRenderer.DrawsAdditiveReticle(cockpit, turretView: false, hasArtwork: false));
        // The additive pass actually uses the gate.
        var helper = Method(Src(Path.Combine("Rendering", "HudRenderer.cs")),
            "private void DrawCentreIndicatorsAdditive(");
        Assert.Contains("bool hasReticle = DrawsAdditiveReticle(model, turretView, _indicators?.Reticle is not null);", helper);
    }

    [Fact]
    public void Shadow_tint_is_the_native_overflowed_green_pre_scaled_for_one_one()
    {
        // HUD-19c — MakeD3DCOLOR OR-overflow of Color(45,36,59)*0.65 gives
        // ARGB(157,55,111,51) (engine/context.h:327-333); intent alpha 0.55 is
        // pre-multiplied into rgb because the gauge pass is One/One.
        var tint = (Color)typeof(HudRenderer).GetField("HUDShadowTint",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(HudRenderer.AdditiveTint(55f / 255f, 111f / 255f, 51f / 255f, 0.55f), tint);
        Assert.True(tint.G > tint.R && tint.G > tint.B);   // green-dominant, not red
    }

    [Fact]
    public void Joystick_scale_and_magnitude_match_native()
    {
        Assert.Equal(0.5f + 0.5f * 0.707f, JoystickLayout.Scale(0.5f, 0.5f), 3);
        Assert.Equal(1f, JoystickLayout.Scale(1f, 1f), 3);   // |control| ≥ 1 clamps
        Assert.Equal(1f, JoystickLayout.Magnitude(2f, 0f), 3);
    }

    [Fact]
    public void Joystick_mesh_rasterizes_the_chevron_premultiplied()
    {
        byte[] rgba = JoystickMeshRasterizer.RasterizePremultiplied(8);
        Assert.Equal(JoystickMeshRasterizer.Width * JoystickMeshRasterizer.Height * 4, rgba.Length);
        Assert.Equal(18, JoystickMeshRasterizer.Width);
        Assert.Equal(8, JoystickMeshRasterizer.Height);
        int lit = 0, maxAlpha = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.Equal(rgba[i + 3], rgba[i]);            // premultiplied white: rgb == a
            Assert.Equal(rgba[i], rgba[i + 1]);
            Assert.Equal(rgba[i], rgba[i + 2]);
            if (rgba[i + 3] > 0) lit++;
            if (rgba[i + 3] > maxAlpha) maxAlpha = rgba[i + 3];
        }
        // The chevron has substantial coverage but is bounded by the 0.70 texture alpha.
        Assert.True(lit >= 20, $"too little chevron coverage ({lit} lit texels)");
        Assert.InRange(maxAlpha, (int)(255 * JoystickMeshRasterizer.TextureAlpha) - 1,
            (int)Math.Ceiling(255 * JoystickMeshRasterizer.TextureAlpha));
    }

    // ── 4. lock flower quadrants ─────────────────────────────────────────────

    [Fact]
    public void Lock_flower_quadrants_move_apart_centred_at_delta()
    {
        // Native DrawExpandedBlip (radarimage.cpp:255-277): each QUARTER is
        // centred at (±delta, ±delta); the quarters keep their native size and
        // move apart as the radius grows.
        var centre = new Vector2(960f, 540f);
        const int w = 64, h = 64;
        // Partial lock at fraction 0.5 with screen radius 34, far from the edge.
        float radius = LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Partial, 0.5f, 34f, 1000f);
        float delta = RadarBracketRenderer.ExpandedBlipDelta(w, h, radius);
        Assert.Equal(34f * 1.05f + 40f * 0.5f, radius);
        Assert.Equal(radius / MathF.Sqrt(2f), delta, 3);   // not floored at this size
        Assert.True(delta >= w / 4f);

        var tl = HudRenderer.ExpandedBlipQuadrantDest(0, w, h, centre, delta);
        var br = HudRenderer.ExpandedBlipQuadrantDest(2, w, h, centre, delta);
        // Quarter centres sit at (±delta, ±delta) (destination is int-rounded).
        Assert.Equal((int)MathF.Round(centre.X - delta), tl.Center.X);
        Assert.Equal((int)MathF.Round(centre.Y - delta), tl.Center.Y);
        Assert.Equal((int)MathF.Round(centre.X + delta), br.Center.X);
        Assert.Equal((int)MathF.Round(centre.Y + delta), br.Center.Y);
        // Each quadrant is the native half-texture, unscaled.
        Assert.Equal(32, tl.Width); Assert.Equal(32, tl.Height);
    }

    [Fact]
    public void Lock_flower_delta_is_floored_at_xsize_over_four()
    {
        // Native floors delta at m = xsizeMid/2 = xsize/4 (radarimage.cpp:262-264).
        float small = RadarBracketRenderer.ExpandedBlipDelta(64, 64, 5f);
        Assert.Equal(64f / 4f, small);
        float large = RadarBracketRenderer.ExpandedBlipDelta(64, 64, 60f);
        Assert.Equal(60f / MathF.Sqrt(2f), large, 3);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static HudFrame Frame() => new(0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 1f,
        120f, -1f, false, false, true, 0, false, 0f, false, false,
        0f, 0f, System.Array.Empty<WeaponStatusRow>(), false, 0,
        TurnYaw: 0.5f, TurnPitch: 0.4f);

    private static string Src(string relative)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", relative));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new InvalidOperationException("unterminated " + signature);
    }
}
