using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Flight;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P501 — fail-first, pure-logic tests for the Android touch flight controls
/// (<see cref="TouchLayout"/> + <see cref="TouchFlightInput"/> + <see cref="TouchTaps"/>).
/// Everything is MonoGame-free: a fixed viewport in, joystick axes / held /
/// edge buttons out. These pin down the layout geometry, the per-finger
/// ownership (a finger that starts on a control keeps it until lifted), the
/// floating-stick deflection → role axes mapping, and the held-vs-edge button
/// semantics — all before any touchscreen is involved.
/// </summary>
public class TouchFlightInputTests
{
    private static readonly (float W, float H) Phone = (2400f, 1080f);     // landscape phone

    // ── layout geometry ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(2400f, 1080f)]
    [InlineData(2560f, 1600f)]
    [InlineData(1920f, 1080f)]
    [InlineData(1080f, 2340f)]   // portrait phone (whatever the HUD does, controls must stay inside)
    public void All_zones_and_buttons_lie_inside_the_viewport(float w, float h)
    {
        var l = TouchLayout.Compute(w, h);

        AssertZoneInside(l.LeftStickZone, w, h);
        AssertZoneInside(l.RightStickZone, w, h);
        foreach (var kv in l.Buttons)
            AssertZoneInside(kv.Value, w, h);
    }

    private static void AssertZoneInside(TouchRect r, float w, float h)
    {
        Assert.True(r.X >= 0f && r.Y >= 0f, $"zone leaked negative: {r}");
        Assert.True(r.Right <= w && r.Bottom <= h, $"zone exceeded viewport {w}x{h}: {r}");
        Assert.True(r.Width > 0f && r.Height > 0f, "zone has no size");
    }

    [Theory]
    [InlineData(2400f, 1080f)]
    [InlineData(2560f, 1600f)]
    public void Action_buttons_do_not_overlap_each_other(float w, float h)
    {
        var l = TouchLayout.Compute(w, h);
        var rects = new List<TouchRect>();
        foreach (var b in System.Enum.GetValues<TouchButton>())
        {
            var r = l.ButtonRect(b);
            foreach (var other in rects)
                Assert.False(RectsOverlap(r, other),
                    $"button {b} overlaps another: {r} vs {other}");
            rects.Add(r);
        }
    }

    private static bool RectsOverlap(TouchRect a, TouchRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    [Theory]
    [InlineData(2400f, 1080f)]
    [InlineData(2560f, 1600f)]
    public void Stick_zones_are_in_the_two_bottom_corners_and_do_not_overlap(float w, float h)
    {
        var l = TouchLayout.Compute(w, h);
        Assert.True(l.LeftStickZone.CenterX < w / 2f, "left stick must be on the left half");
        Assert.True(l.RightStickZone.CenterX > w / 2f, "right stick must be on the right half");
        Assert.True(!RectsOverlap(l.LeftStickZone, l.RightStickZone));
    }

    // ── stick deflection → role axes ─────────────────────────────────────────

    [Fact]
    public void No_touches_yield_neutral_axes()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var r = new TouchFlightInput().Update(System.Array.Empty<TouchSample>(), l);
        Assert.Equal(0f, r.Axes.Yaw);
        Assert.Equal(0f, r.Axes.Pitch);
        Assert.Equal(0f, r.Axes.Roll);
        Assert.Equal(0f, r.Axes.Throttle);
        Assert.False(r.AnyActive);
    }

    [Fact]
    public void Left_stick_drag_right_rolls_and_drag_up_throttles_forward()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        float bx = l.LeftStickBase.X, by = l.LeftStickBase.Y;

        // Land on the left stick, then drag full-right and full-up across two frames.
        inp.Update(new[] { new TouchSample(1, bx, by) }, l);
        var dragged = inp.Update(new[]
        {
            new TouchSample(1, bx + l.StickRadius, by),   // full right -> +roll
        }, l);
        Assert.Equal(1f, dragged.Axes.Roll, precision: 4);
        Assert.Equal(0f, dragged.Axes.Throttle, precision: 4);

        var up = inp.Update(new[]
        {
            new TouchSample(1, bx, by - l.StickRadius),   // full up -> +throttle (forward)
        }, l);
        Assert.Equal(1f, up.Axes.Throttle, precision: 4);
        Assert.Equal(0f, up.Axes.Roll, precision: 4);
    }

    [Fact]
    public void Right_stick_drag_right_yaws_and_drag_up_pitches_up()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        float bx = l.RightStickBase.X, by = l.RightStickBase.Y;

        inp.Update(new[] { new TouchSample(1, bx, by) }, l);
        var right = inp.Update(new[]
        {
            new TouchSample(1, bx + l.StickRadius, by),
        }, l);
        Assert.Equal(1f, right.Axes.Yaw, precision: 4);   // raw positive yaw for right

        var up = inp.Update(new[]
        {
            new TouchSample(1, bx, by - l.StickRadius),
        }, l);
        // Default layout inverts pitch (to cancel MapJoystick's negation); the
        // raw value for stick-up is therefore NEGATIVE, and the controller maps
        // it to a positive ControlData.Pitch = pitch up. Pin the raw sign.
        Assert.True(up.Axes.Pitch < 0f, $"expected negative raw pitch for up, got {up.Axes.Pitch}");
    }

    [Fact]
    public void Two_fingers_own_the_two_sticks_independently()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        var lx = l.LeftStickBase.X; var ly = l.LeftStickBase.Y;
        var rx = l.RightStickBase.X; var ry = l.RightStickBase.Y;

        inp.Update(new[] { new TouchSample(1, lx, ly), new TouchSample(2, rx, ry) }, l);
        var both = inp.Update(new[]
        {
            new TouchSample(1, lx, ly - l.StickRadius),   // left thumb up -> throttle
            new TouchSample(2, rx + l.StickRadius, ry),   // right thumb right -> yaw
        }, l);

        Assert.Equal(1f, both.Axes.Throttle, precision: 4);
        Assert.Equal(1f, both.Axes.Yaw, precision: 4);
        Assert.Equal(0f, both.Axes.Roll, precision: 4);
    }

    [Fact]
    public void A_finger_keeps_its_stick_even_when_it_drags_outside_the_zone()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        float bx = l.LeftStickBase.X, by = l.LeftStickBase.Y;

        inp.Update(new[] { new TouchSample(1, bx, by) }, l);
        // Drag far beyond the zone edge AND beyond the clamp radius -> saturate.
        var far = inp.Update(new[] { new TouchSample(1, bx + 10f * l.StickRadius, by) }, l);
        Assert.Equal(1f, far.Axes.Roll, precision: 4);
    }

    [Fact]
    public void Lifting_the_stick_finger_returns_axes_to_zero()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        float bx = l.LeftStickBase.X, by = l.LeftStickBase.Y;

        inp.Update(new[] { new TouchSample(1, bx, by) }, l);
        inp.Update(new[] { new TouchSample(1, bx + l.StickRadius, by) }, l);
        var released = inp.Update(System.Array.Empty<TouchSample>(), l);
        Assert.Equal(0f, released.Axes.Roll, precision: 4);
        Assert.False(released.AnyActive);
    }

    [Fact]
    public void DragToAxes_partial_deflection_scales_and_clamps()
    {
        // Half radius -> 0.5; full radius+ -> clamped to 1.
        var half = TouchFlightInput.DragToAxes(0.5f, 0f, 1f);
        Assert.Equal(0.5f, half.X, precision: 4);
        Assert.Equal(0f, half.Y, precision: 4);

        var up = TouchFlightInput.DragToAxes(0f, -1f, 1f);
        Assert.Equal(1f, up.Y, precision: 4);   // -dy (screen down) -> +y-up

        var over = TouchFlightInput.DragToAxes(3f, 0f, 1f);
        Assert.Equal(1f, over.X, precision: 4);
    }

    // ── buttons: held vs edge ────────────────────────────────────────────────

    [Fact]
    public void Fire_and_afterburner_are_held_while_the_finger_stays_down()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        var fire = l.ButtonRect(TouchButton.Fire);
        var ab = l.ButtonRect(TouchButton.Afterburner);

        inp.Update(new[]
        {
            new TouchSample(1, fire.CenterX, fire.CenterY),
            new TouchSample(2, ab.CenterX, ab.CenterY),
        }, l);
        var held = inp.Update(new[]
        {
            new TouchSample(1, fire.CenterX, fire.CenterY),
            new TouchSample(2, ab.CenterX, ab.CenterY),
        }, l);
        Assert.True(held.FireHeld);
        Assert.True(held.AfterburnerHeld);

        // Lift fire; afterburner stays held.
        var lifted = inp.Update(new[]
        {
            new TouchSample(2, ab.CenterX, ab.CenterY),
        }, l);
        Assert.False(lifted.FireHeld);
        Assert.True(lifted.AfterburnerHeld);
    }

    [Fact]
    public void Edge_button_fires_once_on_finger_down_not_repeatedly_while_held()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        var target = l.ButtonRect(TouchButton.TargetCycle);

        var first = inp.Update(new[] { new TouchSample(1, target.CenterX, target.CenterY) }, l);
        Assert.True(first.TargetCycleEdge);

        var second = inp.Update(new[] { new TouchSample(1, target.CenterX, target.CenterY) }, l);
        Assert.False(second.TargetCycleEdge);   // still down, but already fired

        // A NEW finger taps the same control again -> edge again.
        var third = inp.Update(new[]
        {
            new TouchSample(1, target.CenterX, target.CenterY),
            new TouchSample(2, target.CenterX, target.CenterY),
        }, l);
        Assert.True(third.TargetCycleEdge);
    }

    [Fact]
    public void Missile_throttle_and_dock_edges_report_once()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        var missile = l.ButtonRect(TouchButton.Missile);
        var tu = l.ButtonRect(TouchButton.ThrottleUp);
        var td = l.ButtonRect(TouchButton.ThrottleDown);
        var dock = l.ButtonRect(TouchButton.DockOrMenu);

        var r = inp.Update(new[]
        {
            new TouchSample(1, missile.CenterX, missile.CenterY),
            new TouchSample(2, tu.CenterX, tu.CenterY),
            new TouchSample(3, td.CenterX, td.CenterY),
            new TouchSample(4, dock.CenterX, dock.CenterY),
        }, l);

        Assert.True(r.MissileEdge);
        Assert.True(r.ThrottleUpEdge);
        Assert.True(r.ThrottleDownEdge);
        Assert.True(r.DockOrMenuEdge);
    }

    [Fact]
    public void Autopilot_edge_ignites_and_a_second_tap_again_reports()
    {
        var l = TouchLayout.Compute(Phone.W, Phone.H);
        var inp = new TouchFlightInput();
        var ap = l.ButtonRect(TouchButton.Autopilot);

        var a = inp.Update(new[] { new TouchSample(1, ap.CenterX, ap.CenterY) }, l);
        Assert.True(a.AutopilotEdge);
        // release
        var b = inp.Update(System.Array.Empty<TouchSample>(), l);
        Assert.False(b.AutopilotEdge);
        // new tap
        var c = inp.Update(new[] { new TouchSample(2, ap.CenterX, ap.CenterY) }, l);
        Assert.True(c.AutopilotEdge);
    }

    // ── TouchTaps (menu-screen tap positions) ────────────────────────────────

    [Fact]
    public void TouchTaps_reports_only_new_finger_positions()
    {
        var tt = new TouchTaps();
        var first = tt.NewTaps(new[]
        {
            new TouchSample(1, 10f, 20f),
            new TouchSample(2, 30f, 40f),
        });
        Assert.Equal(2, first.Count);

        var second = tt.NewTaps(new[] { new TouchSample(1, 10f, 20f) });   // 1 still down
        Assert.Empty(second);

        var third = tt.NewTaps(new[]
        {
            new TouchSample(1, 10f, 20f),
            new TouchSample(3, 50f, 60f),   // new finger
        });
        Assert.Single(third);
        Assert.Equal((3, 50f, 60f), third[0]);
    }
}

/// <summary>P501 — pure tests for the pre-flight tap hit-geometry
/// (<see cref="Rendering.PreFlightTaps"/>), the touch path for the side-select
/// and ready-check screens that were keyboard-only.</summary>
public class PreFlightTapsTests
{
    [Fact]
    public void SideBand_maps_taps_to_row_index_across_the_list()
    {
        const float w = 2400f, h = 1080f;
        float bandH = Rendering.PreFlightTaps.SideBandHeight(h);
        float top = Rendering.PreFlightTaps.SideListTop(h);
        float midX = w / 2f;

        for (int i = 0; i < 4; i++)
        {
            float ty = top + i * bandH + bandH / 2f;
            Assert.Equal(i, Rendering.PreFlightTaps.SideBand(w, h, 4, midX, ty));
        }
    }

    [Fact]
    public void SideBand_rejects_out_of_list_or_below_the_list()
    {
        const float w = 2400f, h = 1080f;
        Assert.Equal(-1, Rendering.PreFlightTaps.SideBand(w, h, 0, w / 2f, h / 2f)); // no sides
        // Off to the side (outside the band width).
        Assert.Equal(-1, Rendering.PreFlightTaps.SideBand(w, h, 3, 0.05f * w, 0.45f * h));
        // Below the last row.
        float top = Rendering.PreFlightTaps.SideListTop(h);
        Assert.Equal(-1, Rendering.PreFlightTaps.SideBand(w, h, 2, w / 2f, top + 2 * Rendering.PreFlightTaps.SideBandHeight(h) + 5f));
    }

    [Fact]
    public void Ready_and_force_buttons_are_inside_the_viewport()
    {
        foreach (var (w, h) in new[] { (2400f, 1080f), (2560f, 1600f), (1080f, 2340f) })
        {
            var toggle = Rendering.PreFlightTaps.ReadyToggleRect(w, h);
            Assert.True(toggle.X >= 0f && toggle.Right <= w && toggle.Y >= 0f && toggle.Bottom <= h);
            var force = Rendering.PreFlightTaps.ForceReadyRect(w, h);
            Assert.True(force.X >= 0f && force.Right <= w && force.Y >= 0f && force.Bottom <= h);
            Assert.False(toggle.X < force.Right && force.X < toggle.Right &&
                         toggle.Y < force.Bottom && force.Y < toggle.Bottom);
        }
    }
}

/// <summary>P501 — pure tests for the docked-screen touch zones
/// (<see cref="Rendering.DockedTouchZones"/>): verb buttons inside the viewport
/// and row-band hit mapping.</summary>
public class DockedTouchZonesTests
{
    [Fact]
    public void Verb_buttons_are_inside_the_viewport_and_disjoint()
    {
        foreach (var (w, h) in new[] { (2400f, 1080f), (2560f, 1600f), (1080f, 2340f) })
        {
            var launch = Rendering.DockedTouchZones.LaunchRect(w, h);
            var undock = Rendering.DockedTouchZones.UndockRect(w, h);
            var tel = Rendering.DockedTouchZones.TeleportRect(w, h);
            var inv = Rendering.DockedTouchZones.InvestRect(w, h);
            foreach (var r in new[] { launch, undock, tel, inv })
                Assert.True(r.X >= 0f && r.Right <= w && r.Y >= 0f && r.Bottom <= h, $"zone out of viewport: {r}");
            Assert.False(Overlaps(launch, undock));
            Assert.False(Overlaps(tel, inv));
        }
    }

    private static bool Overlaps(TouchRect a, TouchRect b)
        => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    [Fact]
    public void RowIndex_maps_taps_into_rows_and_rejects_outside()
    {
        const float w = 2400f, h = 1080f;
        float bandH = Rendering.DockedTouchZones.RowBandHeight(h);
        float top = Rendering.DockedTouchZones.RowListTop(h);
        for (int i = 0; i < 5; i++)
            Assert.Equal(i, Rendering.DockedTouchZones.RowIndex(w, h, 5, w / 2f, top + i * bandH + bandH / 2f));
        Assert.Equal(-1, Rendering.DockedTouchZones.RowIndex(w, h, 5, 0.05f * w, top + bandH / 2f)); // off left
        Assert.Equal(-1, Rendering.DockedTouchZones.RowIndex(w, h, 0, w / 2f, top));                  // no rows
        Assert.Equal(-1, Rendering.DockedTouchZones.RowIndex(w, h, 2, w / 2f, top + 2 * bandH + 5f)); // below
    }
}
