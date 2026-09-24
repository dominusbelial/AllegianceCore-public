using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;
using SimVec = Allegiance.Core.Collision.Vector3;
using Orientation = Allegiance.Core.Sim.Orientation;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Tests for the P367 navigation strobe system — a faithful port of native
/// <c>LightsGeo</c> (effect/efapp.cpp:99-286). Verifies the real
/// <c>LightsGeo::Render</c> pulse envelope (ramp-up → hold → ramp-down → off,
/// efapp.cpp:177-206) at exact times with native's default timing, the long
/// off-interval of a blink (not an always-on or instant-toggle glow), the period
/// wrap, and the alternating red-port/green-starboard phases that give the
/// nav-light look. Purely arithmetic — no graphics device.
/// </summary>
public class NavStrobeTests
{
    private const float Tol = 1e-3f;

    // Native default timing (efapp.cpp:507-511): period 1.25, phase 0,
    // rampUp 0.1, hold 0, rampDown 0.05 (seconds).
    private static NavStrobeTiming Defaults() => NavStrobeTiming.Native;

    private static float PulseAt(float t) => NavStrobeMath.Pulse(t, Defaults());

    [Fact]
    public void Pulse_ramps_up_from_zero_at_start()
    {
        // At t=0 the value is 0; it climbs linearly over the 0.1s rampUp.
        Assert.InRange(PulseAt(0f), -Tol, Tol);
        Assert.InRange(PulseAt(0.030f), 0.30f - Tol, 0.30f + Tol);
        Assert.InRange(PulseAt(0.060f), 0.60f - Tol, 0.60f + Tol);
        Assert.InRange(PulseAt(0.099f), 0.990f - Tol, 0.995f);
    }

    [Fact]
    public void Pulse_hits_full_immediately_at_end_of_ramp()
    {
        // value = 0.1 is not < rampUp 0.1, so it drops into the (zero-length, hold
        // 0) plateau and then the ramp-down floor — native computes exactly 1 here.
        Assert.InRange(PulseAt(0.10f), 1f - Tol, 1f + Tol);
    }

    [Fact]
    public void Pulse_ramps_down_symmetrically_to_zero()
    {
        // Ramp-down spans [0.10, 0.15]: 1 → 0 linearly.
        Assert.InRange(PulseAt(0.115f), 0.700f - Tol, 0.700f + Tol);
        Assert.InRange(PulseAt(0.125f), 0.500f - Tol, 0.500f + Tol);
        Assert.InRange(PulseAt(0.135f), 0.300f - Tol, 0.300f + Tol);
        Assert.InRange(PulseAt(0.145f), 0.100f - Tol, 0.100f + Tol);
    }

    [Fact]
    public void Pulse_returns_to_zero_and_stays_off_between_flashes()
    {
        // The blink is a brief ~0.15s flash; for the remaining ~1.1s of the 1.25s
        // period the light is fully off (native's value>0 render gate suppresses it).
        // A naive always-on glow would fail this — the discriminating envelope test.
        Assert.InRange(PulseAt(0.150f), -Tol, Tol);
        Assert.InRange(PulseAt(0.20f), -Tol, Tol);
        Assert.InRange(PulseAt(0.50f), -Tol, Tol);
        Assert.InRange(PulseAt(0.997f), -Tol, Tol);
        Assert.InRange(PulseAt(1.10f), -Tol, Tol);
    }

    [Fact]
    public void Pulse_wraps_and_restarts_next_period()
    {
        // t = 1.25 is a fresh cycle (same shape as t = 0), so it restarts the ramp.
        Assert.InRange(PulseAt(1.25f), -Tol, Tol);
        Assert.InRange(PulseAt(1.30f), 0.50f - Tol, 0.50f + Tol);
        Assert.InRange(PulseAt(1.35f), 1f - Tol, 1f + Tol);
    }

    [Fact]
    public void Phase_offsets_shift_the_envelope_in_time()
    {
        // Native's ReadStrobe assigns per-light phase (efapp.cpp:476-481). A light
        // with phase 0.5 has its flash DELAYED half a second (flash at t≈0.6, not
        // t≈0.1).
        var shifted = new NavStrobeTiming(1.25f, 0.50f, 0.1f, 0f, 0.05f);
        Assert.InRange(NavStrobeMath.Pulse(0.599f, shifted), 0.988f, 1.003f);   // near its peak
        Assert.InRange(NavStrobeMath.Pulse(0.810f, shifted), -Tol, Tol);        // its off interval
    }

    [Fact]
    public void Nonpositive_period_is_defensively_zero()
    {
        // A degenerate/zero period can't blink; never divide by zero or NaN out.
        var bad = new NavStrobeTiming(0f, 0f, 0.1f, 0f, 0.05f);
        Assert.Equal(0f, NavStrobeMath.Pulse(1f, bad));
    }

    [Fact]
    public void Port_and_starboard_wingtips_alternate_out_of_phase()
    {
        // The layout gives port phase 0 and starboard phase = half the period, so
        // when the port light is at full flash the starboard is off and vice-versa —
        // the alternating red/green nav-light blink (the port phase is the
        // per-light-independent-timing mechanism native uses to stagger a hull's
        // lights). An always-on or in-unison glow would fail this.
        var port = NavStrobeTiming.Native;                                   // phase 0
        var star = new NavStrobeTiming(1.25f, 1.25f * 0.5f, 0.1f, 0f, 0.05f); // phase = half period

        // Port full flash (t=0.10) → starboard off.
        Assert.InRange(NavStrobeMath.Pulse(0.10f, port), 1f - Tol, 1f + Tol);
        Assert.InRange(NavStrobeMath.Pulse(0.10f, star), -Tol, Tol);
        // Starboard full flash (t=0.725) → port off.
        Assert.InRange(NavStrobeMath.Pulse(0.725f, star), 1f - Tol, 1f + Tol);
        Assert.InRange(NavStrobeMath.Pulse(0.725f, port), -Tol, Tol);
    }

    [Fact]
    public void Ship_layout_has_red_port_green_starboard_white_tail()
    {
        var o = new Orientation();   // identity: forward -Z, right +X, up +Y
        var anchors = NavStrobeLayout.ShipAnchors(SimVec.Zero, o, 10f).ToList();
        Assert.Equal(3, anchors.Count);

        // Distinguish the three lights by colour: red-dominant, green-dominant, white.
        var port = anchors.First(a => a.R > a.G && a.R > 0.5f);
        var star = anchors.First(a => a.G > a.R && a.G > 0.5f);
        var tail = anchors.First(a => a.R > 0.9f && a.G > 0.9f && a.B > 0.9f);

        // Port sits on the left wingtip (-right), starboard on the right (+right),
        // tail astern (+backward). Anchor at ~0.95× radius.
        Assert.True(port.Position.X < -8f, $"port should be on the left wingtip, got {port.Position}");
        Assert.True(star.Position.X > 8f, $"starboard should be on the right wingtip, got {star.Position}");
        Assert.True(tail.Position.Z > 8f, $"tail should be astern, got {tail.Position}");

        // Port phase 0, starboard phase = half period → they alternate.
        Assert.Equal(0f, port.Timing.Phase);
        Assert.Equal(1.25f * 0.5f, star.Timing.Phase);
    }

    [Fact]
    public void Station_layout_has_beacons_on_opposite_sides_plus_top()
    {
        var o = new Orientation();   // identity: right +X, up +Y
        var anchors = NavStrobeLayout.StationAnchors(SimVec.Zero, o, 100f).ToList();
        Assert.Equal(3, anchors.Count);
        // Anchored on the ring at the real structure radius: left/right/top extremes.
        Assert.Contains(anchors, a => a.Position.X < -95f);
        Assert.Contains(anchors, a => a.Position.X > 95f);
        Assert.Contains(anchors, a => a.Position.Y > 95f);
    }
}
