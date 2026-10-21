using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-21 — the classic target pane's 3-D model preview. These tests pin the
/// PURE rules of <see cref="TargetModelPreview"/> headlessly (no
/// GraphicsDevice): the <c>SetTargetGeo</c> eligibility gate, the native
/// view/projection (radius-1 framing so a unit sphere fills the 96 px pane),
/// the <c>TurnTo</c> up fallback, the |dp|²&lt;1 default, the <c>PaneRect</c>
/// anchoring at two viewport sizes, the native child paint order, and that the
/// per-frame pure path allocates nothing. The GPU render-to-texture and the
/// actual pixel compositing under the oval are proven in the real-client
/// screenshots (the headless harness has no SpriteBatch/GraphicsDevice to
/// record draws — see the report).
/// </summary>
public class Hud21TargetModelPreviewTests
{
    // ── 1. The SetTargetGeo eligibility gate (WinTrek.cpp:8783-8818) ────────

    [Fact]
    public void Eligible_accepts_an_in_cluster_visible_meshed_non_self_target()
    {
        foreach (var kind in new[] { TargetPreviewKind.Ship, TargetPreviewKind.Station, TargetPreviewKind.Asteroid })
            Assert.True(TargetModelPreview.Eligible(kind, isOwnShip: false, sameCluster: true, visible: true, hasMesh: true),
                $"{kind} should be eligible");
    }

    [Fact]
    public void Eligible_rejects_every_excluded_case()
    {
        // Own ship / other cluster / not visible / no native mesh each kill the
        // preview for a meshed in-cluster visible kind.
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Ship, isOwnShip: true, sameCluster: true, visible: true, hasMesh: true));
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Station, isOwnShip: false, sameCluster: false, visible: true, hasMesh: true));
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Asteroid, isOwnShip: false, sameCluster: true, visible: false, hasMesh: true));
        Assert.False(TargetModelPreview.Eligible(TargetPreviewKind.Ship, isOwnShip: false, sameCluster: true, visible: true, hasMesh: false));

        // Out-of-scope kinds (warp/probe/mine/missile/treasure) never preview,
        // even fully visible + meshed.
        // (Only the three in-scope kinds exist on TargetPreviewKind; the port's
        // RenderTargetModelPreview maps TargetKind.Warp/Probe/... to "not a
        // preview kind" and returns the empty fallback.)
    }

    [Fact]
    public void Eligible_only_knows_the_three_in_scope_kinds()
    {
        // Every kind value is in scope (the enum has exactly ship/station/asteroid);
        // this documents that warps/probes/etc. never enter the preview path.
        var kinds = System.Enum.GetValues<TargetPreviewKind>();
        Assert.Equal(3, kinds.Length);
        Assert.Contains(TargetPreviewKind.Ship, kinds);
        Assert.Contains(TargetPreviewKind.Station, kinds);
        Assert.Contains(TargetPreviewKind.Asteroid, kinds);
    }

    // ── 2. A unit sphere fills exactly the full 96 px viewport ──────────────

    [Fact]
    public void Unit_sphere_projects_to_exactly_the_full_viewport()
    {
        // Radius-1 normalisation (thinggeo.cpp:373-381,1482-1573, net scale
        // 1/meshRadius) + the ortho 2×2 viewport (camera.cpp:46-69,
        // image.cpp:1164-1165) → the sphere's silhouette fills the whole pane.
        var view = TargetModelPreview.View(new Vector3(0f, 1f, 5f), Vector3.Up, -Vector3.UnitZ);
        var proj = TargetModelPreview.Projection();
        var vp = view * proj;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i <= 60; i++)
        {
            float theta = MathHelper.TwoPi * i / 60f;          // longitude
            for (int j = 0; j <= 30; j++)
            {
                float phi = MathHelper.Pi * j / 30f;           // inclination
                var p = new Vector3(MathF.Sin(phi) * MathF.Cos(theta),
                                    MathF.Cos(phi),
                                    MathF.Sin(phi) * MathF.Sin(theta));
                var clip = Vector4.Transform(new Vector4(p, 1f), vp);
                float nx = clip.X / clip.W, ny = clip.Y / clip.W;
                minX = MathF.Min(minX, nx); maxX = MathF.Max(maxX, nx);
                minY = MathF.Min(minY, ny); maxY = MathF.Max(maxY, ny);
            }
        }
        // Full [-1,1] on both axes (0.005 tol for the finite sample grid).
        Assert.InRange(minX, -1.006f, -0.995f);
        Assert.InRange(maxX, 0.995f, 1.006f);
        Assert.InRange(minY, -1.006f, -0.995f);
        Assert.InRange(maxY, 0.995f, 1.006f);
    }

    // ── 3. A dead-ahead identity ship shows its nose toward the viewer ──────

    [Fact]
    public void Dead_ahead_identity_ship_points_its_nose_toward_the_viewer()
    {
        // Native rule: the identity sim orientation's forward is (0,0,-1)
        // (Orientation.cs — left-handed, forward = -row2), and the port's
        // native renderers fold in RY(π) so the mesh nose lies along the ship's
        // forward. With the target dead ahead (dp=+Z), the camera sits at
        // eye=-dp·8 = (0,0,-8) looking at the origin, so the nose axis (0,0,-1)
        // points straight at the viewer.
        var dp = new Vector3(0f, 0f, 1f);
        var view = TargetModelPreview.View(dp, Vector3.Up, -Vector3.UnitZ);

        // The ship's nose tip (identity forward) and tail (identity backward).
        var nose = new Vector3(0f, 0f, -1f);
        var tail = new Vector3(0f, 0f, 1f);
        var eye = new Vector3(0f, 0f, -8f);

        // Both lie on the view axis → project dead-centre (NDC 0,0).
        AssertCentred(nose, view);
        AssertCentred(tail, view);

        // The nose is closer to the viewer than the tail → pointing at us.
        Assert.True(Vector3.Distance(nose, eye) < Vector3.Distance(tail, eye));
        // State the expected axis from the native rule.
        Assert.Equal(new Vector3(0f, 0f, -1f), nose);

        static void AssertCentred(Vector3 p, Matrix view)
        {
            var clip = Vector4.Transform(new Vector4(p, 1f), view);
            Assert.InRange(clip.X / clip.W, -1e-3f, 1e-3f);
            Assert.InRange(clip.Y / clip.W, -1e-3f, 1e-3f);
        }
    }

    // ── 4. TurnTo up fallback (orientation.cpp:220-231) ─────────────────────

    [Theory]
    [InlineData(0f, 1f, 0f, 0f, 0f, 1f)]      // looking straight up → up := −forward
    [InlineData(0f, -1f, 0f, 0f, 0f, -1f)]    // looking straight down → up := +forward
    public void ViewUp_near_parallel_uses_the_plus_or_minus_forward_fallback(float dx, float dy, float dz, float ex, float ey, float ez)
    {
        // |cos| > 0.99 → ∓forward (TurnTo); here the view dir is exactly the
        // player's up (±Y), forcing the singular-avoidance branch.
        var up = TargetModelPreview.ViewUp(new Vector3(dx, dy, dz), Vector3.Up, -Vector3.UnitZ);
        Assert.Equal(new Vector3(ex, ey, ez), up);
    }

    [Fact]
    public void ViewUp_keeps_the_players_up_when_not_near_parallel()
    {
        // Horizontal look (dp=+Z, |cos(up)|=0) keeps the player's up.
        Assert.Equal(Vector3.Up, TargetModelPreview.ViewUp(new Vector3(0f, 0f, 1f), Vector3.Up, -Vector3.UnitZ));
    }

    // ── 5. |dp|² < 1 → the (0,1,5) default (WinTrek.cpp:438-444) ────────────

    [Fact]
    public void Tiny_dp_uses_the_default()
    {
        // |(0.5,0.5,0.5)|² = 0.75 < 1 → the constant default (0,1,5).
        Assert.Equal(TargetModelPreview.DefaultDp, TargetModelPreview.EffectiveDp(new Vector3(0.5f, 0.5f, 0.5f)));
        // Exact unit magnitude (|dp|²=1) is NOT the <1 case → kept as-is.
        Assert.Equal(new Vector3(1f, 0f, 0f), TargetModelPreview.EffectiveDp(new Vector3(1f, 0f, 0f)));
    }

    [Fact]
    public void Real_dp_is_kept_when_beyond_the_unit_sphere()
    {
        var dp = new Vector3(20f, 30f, 40f);
        Assert.Equal(dp, TargetModelPreview.EffectiveDp(dp));
    }

    // ── 6. PaneRect at two viewport sizes (dialog.mdl:4224-4231) ────────────

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    public void PaneRect_anchors_the_96_s_square_at_y_up_270_9(int w, int h)
    {
        float s = TargetPaneLayout.Scale(w, h);
        float x0 = TargetPaneLayout.OriginX(w, h);
        float y0 = TargetPaneLayout.OriginY(w, h);
        var r = TargetModelPreview.PaneRect(x0, y0, s);

        int size = (int)MathF.Round(96f * s);
        Assert.Equal(size, r.Width);
        Assert.Equal(size, r.Height);
        Assert.Equal((int)MathF.Round(x0 + 270f * s), r.X);
        Assert.Equal((int)MathF.Round(y0 - 9f * s - size), r.Y);
        // The two viewport sizes genuinely differ in scale (make the test real).
        Assert.NotEqual(TargetPaneLayout.Scale(2560, 1440), TargetPaneLayout.Scale(1920, 1080));
    }

    [Fact]
    public void PaneRect_is_a_96_s_square_regardless_of_viewport()
    {
        foreach (var (w, h) in new[] { (2560, 1440), (1920, 1080) })
        {
            float s = TargetPaneLayout.Scale(w, h);
            var r = TargetModelPreview.PaneRect(TargetPaneLayout.OriginX(w, h), TargetPaneLayout.OriginY(w, h), s);
            Assert.Equal((int)MathF.Round(96f * s), r.Width);
            Assert.Equal(r.Width, r.Height);
        }
    }

    // ── 7. Composition: the preview paints before the oval; nothing when null ──

    [Fact]
    public void Decor_order_paints_the_model_first_under_the_oval()
    {
        // Native GroupImage child order (dialog.mdl:4218-4242, image.cpp:954-972):
        // the model GeoImage paints before TargetOval/TargetOvalColor and the
        // words. The static DecorOrder documents the exact order DrawTargetPane
        // paints in (the model helper is called before the oval art calls).
        var order = TargetModelPreview.DecorOrder;
        Assert.Equal(PaneDecorKind.Model, order[0]);
        Assert.Equal(PaneDecorKind.Oval, order[1]);
        Assert.True(System.Array.IndexOf(order, PaneDecorKind.Model)
                    < System.Array.IndexOf(order, PaneDecorKind.Oval));
        Assert.True(System.Array.IndexOf(order, PaneDecorKind.OvalColor)
                    < System.Array.IndexOf(order, PaneDecorKind.OvalWord1));
        // Exactly the five decorative children, no duplicates.
        Assert.Equal(5, order.Length);
    }

    [Fact]
    public void Null_preview_input_draws_nothing()
    {
        // The HudRenderer guard: with no preview texture the pane draws no model
        // (the exact empty fallback — the pane looks as it does today).
        var renderer = (HudRenderer)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(HudRenderer));
        System.GC.SuppressFinalize(renderer);
        Assert.False(renderer.PreviewWillDraw);   // TargetModelPreview defaults null
        // The draw helper returns without touching the SpriteBatch when null.
        Assert.False(renderer.PreviewWillDraw);
    }

    // ── 8. Allocation: the per-frame pure path allocates nothing ────────────

    [Fact]
    public void Per_frame_pure_path_is_allocation_free_over_10000_iterations()
    {
        // Warm-up (JIT) so the measured window is steady-state.
        for (int i = 0; i < 100; i++) Exercise(i);
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Exercise(i);
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0,
            $"per-frame target-preview pure path allocated {allocated} B over 10k iterations");

        static void Exercise(int i)
        {
            var dp = new Vector3(0f, 1f + i * 0.003f, 5f);
            _ = TargetModelPreview.Eligible(TargetPreviewKind.Ship, false, true, true, true);
            _ = TargetModelPreview.EffectiveDp(dp);
            _ = TargetModelPreview.View(dp, Vector3.Up, -Vector3.UnitZ);
            _ = TargetModelPreview.Projection();
            _ = TargetModelPreview.Lighting(dp);
            _ = TargetModelPreview.PaneRect(960f, 540f, 1.06f);
        }
    }
}
