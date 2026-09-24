using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the P363 pure ship side-coloured motion-trail ribbon pool
/// (<see cref="ShipMotionTrailRibbons"/>). These exercise the pool's own
/// mechanics — the native <c>TrailGeo</c> ribbon (efgeo.cpp:492-661): the
/// 0.5 s section-advance cadence, the head-anchored geometry, the
/// right-vector-freezes-when-not-moving rule, the stop-collapses-the-trail
/// behaviour, the 16-section cap, per-ship colour independence, and pruning of
/// vanished ships — and never touch a graphics device (the pool is
/// MonoGame-free by design).
/// </summary>
public class ShipMotionTrailRibbonsTests
{
    private static readonly SimVec Right = new(5f, 0f, 0f);        // native m_radius·0.25 along the ship's right
    private static readonly SimVec RightWide = new(9f, 0f, 0f);    // a different (wider) right

    /// <summary>Feed one ship a frame: advance the pool clock by
    /// <paramref name="dtBeforeAdvance"/> (if &gt; 0), move the ship +X by
    /// <paramref name="dx"/> (0 = stationary) and set its anchor/right/colour.
    /// When <paramref name="dtBeforeAdvance"/> ≥ 0.5 this crosses the native
    /// TimeDelta and fires a section break.</summary>
    private static void Fly(ShipMotionTrailRibbons pool, short id, ref float x, float dx,
        SimVec right, float dtBeforeAdvance = 0f, (float r, float g, float b)? colour = null)
    {
        if (dtBeforeAdvance > 0f) pool.Advance(dtBeforeAdvance);
        x += dx;
        var (r, g, b) = colour ?? (0.741f, 0.628f, 0f);   // gold (side 0), whatever
        pool.SetAnchor(id, new SimVec(x, 0f, 0f), right, r, g, b);
    }

    [Fact]
    public void Section_advances_on_the_native_half_second_cadence()
    {
        var pool = new ShipMotionTrailRibbons();
        float x = 0f;
        Fly(pool, 1, ref x, 12f, Right);                    // first anchor, t=0
        Fly(pool, 1, ref x, 12f, Right, dtBeforeAdvance: 0.082f);   // t≈0.082, still < 0.5
        // No section has broken yet → nothing to draw (native guards m_section != 0).
        Assert.Empty(pool.Enumerate());
        // Cross the 0.5 s boundary → one section break.
        Fly(pool, 1, ref x, 12f, Right, dtBeforeAdvance: 0.090f);
        Fly(pool, 1, ref x, 12f, Right, dtBeforeAdvance: 0.632f);   // cumulative past 0.5 since t=0
        Assert.Equal(1, pool.Enumerate().Single().Sections);
        // Another 0.5 s → a second break; history shifted back one slot.
        Fly(pool, 1, ref x, 12f, Right, dtBeforeAdvance: 0.633f);
        Assert.Equal(2, pool.Enumerate().Single().Sections);
    }

    [Fact]
    public void Head_always_tracks_the_latest_anchor()
    {
        var pool = new ShipMotionTrailRibbons();
        float x = 0f;
        Fly(pool, 7, ref x, 10f, Right);
        Fly(pool, 7, ref x, 10f, Right, dtBeforeAdvance: 0.039f);
        Fly(pool, 7, ref x, 998f, Right, dtBeforeAdvance: 0.048f);   // hop far → x = 1018
        Fly(pool, 7, ref x, 0f, Right, dtBeforeAdvance: 0.625f);     // force a break; ship still at 1018
        var strip = pool.Enumerate().Single();
        Assert.Equal(new SimVec(1018f, 0f, 0f), strip.Centres[0]);   // head = last anchor
    }

    [Fact]
    public void Right_vector_freezes_while_not_moving_then_updates_on_move()
    {
        var pool = new ShipMotionTrailRibbons();
        float x = 0f;
        Fly(pool, 3, ref x, 10f, Right);                            // moving, right = Right
        Fly(pool, 3, ref x, 10f, Right, dtBeforeAdvance: 0.036f);
        // Stop, but the caller now supplies a DIFFERENT right (orientation changed)…
        Fly(pool, 3, ref x, 0f, RightWide, dtBeforeAdvance: 0.047f);   // stopped
        Fly(pool, 3, ref x, 0f, RightWide, dtBeforeAdvance: 0.649f);   // stopped (break fires)
        var stopped = pool.Enumerate().Single();
        // … the ribbon keeps the LAST MOVING right (native m_vecRightLast is only
        // refreshed while bMoving, thinggeo.cpp:1166-1168).
        Assert.Equal(Right, stopped.Rights[0]);
        // Moving again with the new right → it adopts it.
        Fly(pool, 3, ref x, 10f, RightWide, dtBeforeAdvance: 0.058f);
        Fly(pool, 3, ref x, 10f, RightWide, dtBeforeAdvance: 0.663f);  // break while moving
        var moved = pool.Enumerate().Single();
        Assert.Equal(RightWide, moved.Rights[0]);
    }

    [Fact]
    public void Stopped_ship_stops_extending_and_collapses_to_the_anchor()
    {
        var pool = new ShipMotionTrailRibbons();
        float x = 0f;
        // Lay a real trail: several section breaks spread across the +X path.
        for (int i = 0; i < 8; i++)
            Fly(pool, 5, ref x, 10f, Right, dtBeforeAdvance: 0.647f);
        var before = pool.Enumerate().Single();
        Assert.True(before.Centres[before.Sections].X < before.Centres[0].X - 20f, "trail spans behind the ship");

        // Stop: hold the anchor while the clock keeps running past many more
        // breaks — new sections pile at the stationary anchor and the older
        // spread slides toward the (dim) tail, collapsing the trail back into
        // the ship (native fades ~16·0.5 s after stopping).
        for (int i = 0; i < 24; i++)
            Fly(pool, 5, ref x, 0f, Right, dtBeforeAdvance: 0.653f);
        var after = pool.Enumerate().Single();
        for (int k = 0; k <= after.Sections; k++)
            Assert.Equal(after.Centres[0], after.Centres[k]);
    }

    [Fact]
    public void Sections_cap_at_MaxSections()
    {
        var pool = new ShipMotionTrailRibbons();
        float x = 0f;
        for (int i = 0; i < ShipMotionTrailRibbons.MaxSections + 8; i++)
            Fly(pool, 9, ref x, 10f, Right, dtBeforeAdvance: 0.666f);
        var strip = pool.Enumerate().Single();
        Assert.Equal(ShipMotionTrailRibbons.MaxSections, strip.Sections);
    }

    [Fact]
    public void Per_ship_ribbons_and_colours_are_independent()
    {
        var pool = new ShipMotionTrailRibbons();
        float x1 = 0f, x2 = 0f;
        for (int i = 0; i < 3; i++)
        {
            // Break-guaranteeing advances so each ship lays a real ribbon.
            Fly(pool, 11, ref x1, 10f, Right, dtBeforeAdvance: i == 0 ? 0f : 0.711f, colour: (0.930f, 0.030f, 0.025f));  // red-ish
            Fly(pool, 12, ref x2, 10f, Right, dtBeforeAdvance: i == 0 ? 0f : 0.717f, colour: (0.027f, 0.034f, 0.975f));  // blue-ish
        }
        var all = pool.Enumerate().ToDictionary(s => s.ShipId);
        Assert.Equal(2, all.Count);
        Assert.True(all[11].R > 0.9f, "ship 11 tint is red-dominant");
        Assert.True(all[12].B > 0.9f, "ship 12 tint is blue-dominant");
        Assert.True(all[11].R > all[12].R, "per-ship colours are independent, not shared");
    }

    [Fact]
    public void PruneUntouched_removes_vanished_ships_but_keeps_active_ones()
    {
        var pool = new ShipMotionTrailRibbons();
        float a = 0f, b = 0f;
        // Lay real ribbons (fire a section break) for both ships so Enumerate sees them.
        Fly(pool, 21, ref a, 10f, Right);
        Fly(pool, 22, ref b, 10f, Right);
        Fly(pool, 21, ref a, 10f, Right, dtBeforeAdvance: 0.672f);   // 21 breaks
        Fly(pool, 22, ref b, 10f, Right, dtBeforeAdvance: 0.674f);   // 22 breaks
        pool.PruneUntouched();                                        // end of frame 1: both active
        Assert.Equal(2, pool.Enumerate().Count());
        Fly(pool, 21, ref a, 10f, Right, dtBeforeAdvance: 0.676f);    // frame 2: only 21 visible
        pool.PruneUntouched();
        var ids = pool.Enumerate().Select(s => s.ShipId).ToList();
        Assert.Contains((short)21, ids);
        Assert.DoesNotContain((short)22, ids);
    }

    [Fact]
    public void Brightness_taper_is_head_bright_tail_transparent()
    {
        // Native UpdateColors (efgeo.cpp:572-589): bright = 1 − dtime − k/16.
        // With dtime near 0 (just after an advance) the head is ~1 and the
        // tail (k=16) is ~0 — a linear fade down the strip.
        Assert.True(ShipMotionTrailRibbons.BrightnessFor(dTime: 0f, k: 0) > 0.996f);
        Assert.True(ShipMotionTrailRibbons.BrightnessFor(dTime: 0f, k: ShipMotionTrailRibbons.MaxSections) <= 0.063f);
        for (int k = 1; k < ShipMotionTrailRibbons.MaxSections; k++)
            Assert.True(ShipMotionTrailRibbons.BrightnessFor(0f, k) < ShipMotionTrailRibbons.BrightnessFor(0f, k - 1),
                $"section {k} must be dimmer than {k - 1}");
        // dtime climbing dims the whole strip; clamped ≥ 0.
        Assert.Equal(0f, ShipMotionTrailRibbons.BrightnessFor(dTime: 2f, k: 0));
    }

    [Fact]
    public void Reset_drops_all_ribbons_and_the_clock()
    {
        var pool = new ShipMotionTrailRibbons();
        float x = 0f;
        for (int i = 0; i < 5; i++)
            Fly(pool, 31, ref x, 10f, Right, dtBeforeAdvance: 0.777f);
        Assert.NotEmpty(pool.Enumerate());
        pool.Reset();
        Assert.Empty(pool.Enumerate());
        Assert.Equal(0, pool.RibbonCount);
        Assert.Equal(0f, pool.Elapsed);
    }
}
