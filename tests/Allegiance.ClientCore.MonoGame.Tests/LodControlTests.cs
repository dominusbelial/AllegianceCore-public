using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P759 — pure-logic tests for the runtime LOD slider (native
/// WinTrek.cpp:3179-3185, 9448-9453, 10415-10424, 3553-3555) and its bias step
/// (thinggeo.cpp:1624, 1750-1753). No graphics device involved.
/// </summary>
public class LodControlTests
{
    [Fact]
    public void Initial_value_and_bias_match_native_scroll_pane()
    {
        var c = new LodControl();
        // native CreateScrollPane(..., size=100, pageSize=10, lineSize=1, pos=90).
        Assert.Equal(90, c.Value);
        Assert.Equal(0.9f, c.Bias, 3);
        Assert.False(c.Visible);   // slider starts hidden (m_pwrapImageLOD empty)
    }

    [Fact]
    public void PageUp_and_PageDown_move_by_page_size_and_clamp()
    {
        var c = new LodControl();
        c.PageUp();                 // 90 -> 100 (clamped to max 100)
        Assert.Equal(LodControl.MaxValue, c.Value);
        c.PageUp();
        Assert.Equal(LodControl.MaxValue, c.Value);

        c.PageDown();               // 100 -> 90
        Assert.Equal(90, c.Value);
        for (int i = 0; i < 12; i++) c.PageDown();
        Assert.Equal(0, c.Value);   // clamps at 0 (native scroll pane lower bound)
        Assert.Equal(0f, c.Bias, 3);
    }

    [Fact]
    public void ToggleVisible_flips_the_native_slider_image_state()
    {
        var c = new LodControl();
        c.ToggleVisible();
        Assert.True(c.Visible);
        c.ToggleVisible();
        Assert.False(c.Visible);
    }

    [Fact]
    public void Bias_scales_projected_pixel_radius_like_native_SetLOD()
    {
        var p = new ShipLodProfile { LodBias = 0.5f };
        // native SetLOD(screenRadius * lodBias) — a 40 px radius with bias 0.5
        // behaves as a 20 px radius.
        Assert.Equal(20f, ShipLod.ApplyLodBias(40f, p), 3);
        p.LodBias = 2f;             // native ZAssert(lodBias <= 1) -> clamped
        Assert.Equal(40f, ShipLod.ApplyLodBias(40f, p), 3);
        p.LodBias = -1f;            // native ZAssert(lodBias >= 0) -> clamped
        Assert.Equal(0f, ShipLod.ApplyLodBias(40f, p), 3);
    }

    [Fact]
    public void Lower_bias_pushes_native_lod_level_coarser()
    {
        var p = new ShipLodProfile { LodBias = 1f };
        float[] radii = [30f, 10f];  // descending LODGeo thresholds (finest->coarsest)
        // 40 px unbiased -> level 0 (base); lower bias makes the same ship coarser.
        Assert.Equal(0, ShipLod.NativeLodLevelFor(ShipLod.ApplyLodBias(40f, p), radii));
        p.LodBias = 0.5f;            // 40 * 0.5 = 20 px -> level 1 (first coarse)
        Assert.Equal(1, ShipLod.NativeLodLevelFor(ShipLod.ApplyLodBias(40f, p), radii));
        p.LodBias = 0.25f;          // 40 * 0.25 = 10 px -> level 2 (both thresholds met)
        Assert.Equal(2, ShipLod.NativeLodLevelFor(ShipLod.ApplyLodBias(40f, p), radii));
    }
}
