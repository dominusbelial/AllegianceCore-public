using System.Globalization;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1036 — Jet story 2/9: the quaternion flight symbology. Pins
/// <see cref="HudFlightReference"/> (own attitude + velocity + the native sector
/// plane), <see cref="JetFlightGeometry"/> (pitch/roll/heading, the vertical
/// singularity hold, the native velocity-indicator gate and the ladder/tape math)
/// and the renderer's new FPM / ladder / heading / speed / throttle tapes,
/// including the 800×480 ink-dominance fix for the near-black under-stroke.
/// <para>Headless: the renderer draws through a counting
/// <see cref="IJetVectorSurface"/>, so geometry, gate parity, smoke and the 0 B/frame
/// gate run without a graphics device.</para>
/// </summary>
public sealed class JetFlightSymbologyTests(ITestOutputHelper output)
{
    // ── HudFlightReference / attitude geometry ───────────────────────────
    [Fact]
    public void Level_reference_reports_zero_pitch_zero_roll_and_the_plane_heading()
    {
        JetAttitude attitude = JetFlightGeometry.Attitude(Reference(0f, 0f, 45f));
        Assert.True(attitude.Available);
        Assert.Equal(0f, attitude.PitchDegrees, 2);
        Assert.Equal(0f, attitude.RollDegrees, 2);
        Assert.Equal(45f, attitude.HeadingDegrees, 2);
        Assert.True(attitude.HeadingValid);
        Assert.True(attitude.RollValid);
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(-30f)]
    public void Pitched_reference_reports_the_signed_pitch(float pitch)
    {
        JetAttitude attitude = JetFlightGeometry.Attitude(Reference(pitch, 0f, 90f));
        Assert.Equal(pitch, attitude.PitchDegrees, 1);
        Assert.Equal(0f, attitude.RollDegrees, 1);
        Assert.Equal(90f, attitude.HeadingDegrees, 1);
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(-30f)]
    [InlineData(90f)]
    [InlineData(-90f)]
    public void Rolled_reference_round_trips_the_bank_about_forward(float roll)
    {
        JetAttitude attitude = JetFlightGeometry.Attitude(Reference(0f, roll, 120f));
        Assert.Equal(roll, attitude.RollDegrees, 1);
        Assert.Equal(0f, attitude.PitchDegrees, 1);
        Assert.Equal(120f, attitude.HeadingDegrees, 1);
        Assert.True(attitude.RollValid);
        Assert.True(attitude.HeadingValid);
    }

    [Fact]
    public void Inverted_flight_reports_the_inverse_bank_without_a_pitch_change()
    {
        JetAttitude attitude = JetFlightGeometry.Attitude(Reference(0f, 180f, 0f));
        Assert.Equal(0f, attitude.PitchDegrees, 1);
        Assert.Equal(180f, MathF.Abs(attitude.RollDegrees), 1);
        Assert.True(attitude.HeadingValid);   // forward stays in the plane
    }

    [Theory]
    [InlineData(89.9f)]
    [InlineData(-89.9f)]
    [InlineData(90f)]
    [InlineData(-90f)]
    public void Near_vertical_pitch_has_no_heading_or_roll_but_a_valid_pitch(float pitch)
    {
        JetAttitude attitude = JetFlightGeometry.Attitude(Reference(pitch, 0f, 0f));
        Assert.True(attitude.Available);
        Assert.Equal(pitch, attitude.PitchDegrees, 1);
        Assert.False(attitude.HeadingValid);
        Assert.False(attitude.RollValid);
    }

    [Fact]
    public void The_state_holder_keeps_the_last_valid_heading_across_the_singularity()
    {
        var state = new JetAttitudeState();
        state.Update(Reference(0f, 12f, 45f));
        Assert.Equal(45f, state.Current.HeadingDegrees, 2);
        Assert.Equal(12f, state.Current.RollDegrees, 2);

        // Through the vertical: heading and roll are marked unavailable but the
        // values do not jump to a random reading.
        state.Update(Reference(89.9f, 0f, 0f));
        Assert.False(state.Current.HeadingValid);
        Assert.False(state.Current.RollValid);
        Assert.Equal(45f, state.Current.HeadingDegrees, 2);
        Assert.Equal(12f, state.Current.RollDegrees, 2);
        Assert.Equal(45f, state.HeldHeadingDegrees, 2);

        // Back to level flight: the new reference's own heading is reported.
        state.Update(Reference(0f, 0f, 300f));
        Assert.True(state.Current.HeadingValid);
        Assert.Equal(300f, state.Current.HeadingDegrees, 2);
    }

    [Fact]
    public void A_null_or_degenerate_reference_clears_the_attitude_without_inventing_values()
    {
        var state = new JetAttitudeState();
        state.Update(null);
        Assert.False(state.Current.Available);
        Assert.False(state.Current.HeadingValid);

        // A zero quaternion is rejected rather than producing a random heading.
        var degenerate = new HudFlightReference(new Quaternion(0f, 0f, 0f, 0f),
            new Vector3(1f, 0f, 0f), HudFlightReference.SectorUp);
        Assert.False(degenerate.HasAttitude);
        state.Update(degenerate);
        Assert.False(state.Current.Available);
    }

    [Theory]
    [InlineData(359.5f, 359.5f)]
    [InlineData(-1f, 359f)]
    [InlineData(360f, 0f)]
    [InlineData(720.25f, 0.25f)]
    [InlineData(0f, 0f)]
    public void Heading_wraps_into_0_to_360(float heading, float expected)
        => Assert.Equal(expected, JetFlightGeometry.WrapHeading(heading), 3);

    [Fact]
    public void The_sector_plane_is_the_native_command_grid_plane()
    {
        // Native builds the command grid at zGrid = 0 (WinTrek/WinTrek.cpp:3099) in
        // the XY plane (WinTrek/cmdview.cpp:17-115); the normal is world +Z.
        Assert.Equal(new Vector3(0f, 0f, 1f), HudFlightReference.SectorUp);
        // A ship pointing along +X in the plane has heading 0 and pitch 0.
        JetAttitude east = JetFlightGeometry.Attitude(Reference(0f, 0f, 0f));
        Assert.Equal(0f, east.HeadingDegrees, 2);
        // 90° in the plane is +Y.
        Assert.Equal(90f, JetFlightGeometry.Attitude(Reference(0f, 0f, 90f)).HeadingDegrees, 2);
    }

    // ── native velocity-indicator (FPM) gate ─────────────────────────────
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.2f, 0)]
    [InlineData(0.49f, 0)]
    [InlineData(0.5f, 1)]     // native (int)(len + 0.5): 0.5 rounds to a moving ship
    [InlineData(1f, 1)]
    [InlineData(260f, 260)]
    public void Rounded_speed_matches_the_native_truncated_round(float speed, int expected)
        => Assert.Equal(expected, JetFlightGeometry.RoundedSpeed(speed));

    [Fact]
    public void The_flight_path_gate_matches_the_native_rule()
    {
        // WinTrek/indicator.cpp:132 camera, :138 speed != 0, :140 projection,
        // :144 rect.Inside.
        Assert.False(JetFlightGeometry.FlightPathMarkerVisible(false, 200f, true));   // chase: no marker
        Assert.False(JetFlightGeometry.FlightPathMarkerVisible(true, 0f, true));      // stopped
        Assert.False(JetFlightGeometry.FlightPathMarkerVisible(true, 0.49f, true));   // rounds to 0
        Assert.True(JetFlightGeometry.FlightPathMarkerVisible(true, 0.5f, true));     // rounding threshold
        Assert.False(JetFlightGeometry.FlightPathMarkerVisible(true, 200f, false));   // behind camera / outside
        Assert.True(JetFlightGeometry.FlightPathMarkerVisible(true, 200f, true));
    }

    [Fact]
    public void The_flight_path_gate_agrees_with_the_shared_model_gate()
    {
        foreach (float speed in new[] { 0f, 0.2f, 0.49f, 0.5f, 1f, 42f, 260f })
        {
            var model = HudModelAdapter.Create(Frame(speed), CameraMode.Cockpit, velocityMarker: new(900, 500));
            bool geometry = JetFlightGeometry.FlightPathMarkerVisible(model.ReticleVisible, speed, true);
            Assert.Equal(model.VelocityMarkerVisible, geometry);
            // A missing projection is the same as the native failure.
            bool noProjection = JetFlightGeometry.FlightPathMarkerVisible(model.ReticleVisible, speed, false);
            Assert.False(noProjection);
        }
    }

    // ── throttle tape mapping ────────────────────────────────────────────
    [Theory]
    [InlineData(-1f, 0)]      // turret forced −1
    [InlineData(-0.6f, 20)]   // reverse, below the idle detent
    [InlineData(0f, 50)]      // idle
    [InlineData(0.5f, 75)]
    [InlineData(1f, 100)]     // full forward
    public void Throttle_percent_maps_the_shared_axis(float throttle, int percent)
        => Assert.Equal(percent, JetFlightGeometry.ThrottlePercent(throttle));

    [Fact]
    public void Throttle_reads_the_shared_model_overrides_for_afterburner_and_turret()
    {
        // The model latches the movement/afterburner override to +1 (HudModel.cs:376-381).
        var burner = HudModelAdapter.Create(Frame(200f, throttle: .2f) with { AfterburnerActive = true },
            CameraMode.Cockpit);
        Assert.Equal(1f, burner.Telemetry!.Value.Throttle);
        Assert.Equal(100, JetFlightGeometry.ThrottlePercent(burner.Telemetry.Value.Throttle));

        // A turret gunner's forced −1 reads 0 % (native axis, unchanged).
        var turret = HudModelAdapter.Create(Frame(0f, throttle: .8f), CameraMode.ExternalTurret, turret: true);
        Assert.Equal(-1f, turret.Telemetry!.Value.Throttle);
        Assert.Equal(0, JetFlightGeometry.ThrottlePercent(turret.Telemetry.Value.Throttle));

        // Idle (axis 0) is exactly the 50 % detent.
        var idle = HudModelAdapter.Create(Frame(200f, throttle: 0f), CameraMode.Cockpit);
        Assert.Equal(50, JetFlightGeometry.ThrottlePercent(idle.Telemetry!.Value.Throttle));
    }

    // ── ladder / tape projection math ────────────────────────────────────
    [Fact]
    public void Ladder_rungs_land_above_and_below_the_boresight_and_rotate_with_bank()
    {
        // A rung below the nose sits below the boresight (+y, screen y-down).
        Vector2 below = JetFlightGeometry.LadderOffset(0f, -10f, 0f, 8f);
        Assert.Equal(0f, below.X, 2);
        Assert.Equal(80f, below.Y, 2);
        Vector2 above = JetFlightGeometry.LadderOffset(0f, 10f, 0f, 8f);
        Assert.Equal(-80f, above.Y, 2);
        // Banked right 90°, the same below-plane rung appears to the right.
        Vector2 banked = JetFlightGeometry.LadderOffset(0f, -10f, 90f, 8f);
        Assert.Equal(80f, banked.X, 1);
        Assert.Equal(0f, banked.Y, 1);
    }

    [Theory]
    [InlineData(0f, false, false)]
    [InlineData(60f, true, false)]
    [InlineData(-60f, false, true)]
    [InlineData(30f, false, false)]
    public void Off_scale_chevrons_fire_only_past_the_ladder_limit(float pitch, bool top, bool bottom)
    {
        Assert.Equal(top, JetFlightGeometry.LadderOffScaleTop(pitch, JetFlightGeometry.LadderHalfSpanDegrees));
        Assert.Equal(bottom, JetFlightGeometry.LadderOffScaleBottom(pitch, JetFlightGeometry.LadderHalfSpanDegrees));
    }

    [Fact]
    public void The_800x480_floor_uses_a_shorter_ladder_span()
        => Assert.True(JetFlightGeometry.CompactLadderHalfSpanDegrees < JetFlightGeometry.LadderHalfSpanDegrees);

    // ── the renderer ─────────────────────────────────────────────────────
    [Fact]
    public void Jet_coverage_names_the_five_drawn_elements()
    {
        using var jet = Jet(new CountingSurface());
        // P1036's five flight ids are still there; P1037 adds the target, lock,
        // lead/aim and order families; P1038 adds the stores block, the caution
        // panel and the F4 inventory pane on top (64 ids total).
        Assert.Equal(64, JetHudRenderer.ElementIds.Count);
        Assert.Equal(JetHudRenderer.ElementIds.OrderBy(id => id, StringComparer.Ordinal),
            jet.Coverage!.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains("Velocity direction marker", jet.Coverage);
        Assert.Contains("Selected target bracket", jet.Coverage);
        Assert.Contains("Missile lock flower", jet.Coverage);
    }

    [Fact]
    public void The_heading_tape_boxes_the_heading_and_falls_back_to_dashes()
    {
        var surface = new CountingSurface();
        using var jet = Jet(surface);
        var model = FlightModel(Reference(0f, 0f, 45f));
        jet.Draw(model, new(new(0, 0, 1920, 1080), null, false));
        Assert.Contains("HDG 045", surface.Seen);

        // No reference (or the vertical singularity) reports HDG -- instead of
        // keeping a stale number on the tape.
        var surface2 = new CountingSurface();
        using var jet2 = Jet(surface2);
        jet2.Draw(FlightModel(null), new(new(0, 0, 1920, 1080), null, false));
        Assert.Contains("HDG --", surface2.Seen);

        var surface3 = new CountingSurface();
        using var jet3 = Jet(surface3);
        jet3.Draw(FlightModel(Reference(89.9f, 0f, 0f)), new(new(0, 0, 1920, 1080), null, false));
        Assert.Contains("HDG --", surface3.Seen);
    }

    [Fact]
    public void Tapes_and_ladder_are_hidden_where_classic_hides_the_instrument_group()
    {
        foreach (var (name, model) in new[]
        {
            ("command", HudModelAdapter.Create(Frame(260f), CameraMode.Command, commandView: true,
                flightReference: Reference(0f, 0f, 45f))),
            ("docked", HudModelAdapter.Create(Frame(260f), CameraMode.Cockpit, docked: true,
                flightReference: Reference(0f, 0f, 45f))),
            ("dead", HudModelAdapter.Create(Frame(260f), CameraMode.Cockpit, dead: true,
                flightReference: Reference(0f, 0f, 45f))),
        })
        {
            var surface = new CountingSurface();
            using var jet = Jet(surface);
            jet.Draw(model, new(new(0, 0, 1920, 1080), null, false));
            Assert.False(surface.Seen.Contains("SPD / MPS"), name + " must not show the speed tape");
            Assert.False(surface.Seen.Contains("THR"), name + " must not show the throttle tape");
            Assert.DoesNotContain(surface.Seen, text => text.StartsWith("HDG", StringComparison.Ordinal));
            Assert.DoesNotContain(surface.Seen, text => text.StartsWith("+", StringComparison.Ordinal)
                || text.StartsWith("-", StringComparison.Ordinal));
        }

        // A lifepod keeps the tapes: pod speed is the same shared field.
        var pod = HudModelAdapter.Create(Frame(60f, throttle: 0f) with { IsAdriftLifepod = true },
            CameraMode.Cockpit, flightReference: Reference(0f, 0f, 45f));
        var podSurface = new CountingSurface();
        using var podJet = Jet(podSurface);
        podJet.Draw(pod, new(new(0, 0, 800, 480), null, false));
        Assert.Contains("SPD / MPS", podSurface.Seen);
        Assert.Contains("THR", podSurface.Seen);
        Assert.Contains("HDG 045", podSurface.Seen);
        Assert.Contains("60", podSurface.Seen);
        Assert.Contains("50%", podSurface.Seen);
    }

    [Fact]
    public void The_flight_path_marker_appears_only_with_the_native_velocity_gate()
    {
        var still = new CountingSurface();
        using (var jet = Jet(still))
            jet.Draw(HudModelAdapter.Create(Frame(0f), CameraMode.Cockpit, velocityMarker: new(900, 560),
                flightReference: Reference(0f, 0f, 45f)), new(new(0, 0, 1920, 1080), null, false));
        var moving = new CountingSurface();
        using (var jet = Jet(moving))
            jet.Draw(HudModelAdapter.Create(Frame(200f), CameraMode.Cockpit, velocityMarker: new(900, 560),
                flightReference: Reference(0f, 0f, 45f)), new(new(0, 0, 1920, 1080), null, false));
        Assert.True(moving.Lines > still.Lines, "the moving frame draws the FPM circle, wings and tail");

        // Chase view: the native gate hides it even at speed.
        var chase = new CountingSurface();
        using (var jet = Jet(chase))
            jet.Draw(HudModelAdapter.Create(Frame(200f), CameraMode.Chase, velocityMarker: new(900, 560),
                flightReference: Reference(0f, 0f, 45f)), new(new(0, 0, 1920, 1080), null, false));
        Assert.True(chase.Lines < moving.Lines);
    }

    [Fact]
    public void The_reverse_throttle_draws_below_the_idle_detent()
    {
        var surface = new CountingSurface();
        using var jet = Jet(surface);
        var model = HudModelAdapter.Create(Frame(40f, throttle: -.6f), CameraMode.Cockpit,
            flightReference: Reference(-10f, 12f, 90f));
        jet.Draw(model, new(new(0, 0, 1920, 1080), null, false));
        Assert.Contains("20%", surface.Seen);
        Assert.Contains("REV", surface.Seen);
        Assert.Contains("IDLE", surface.Seen);
        // The 800×480 floor keeps the detent strokes but drops the extra words.
        var compact = new CountingSurface();
        using var compactJet = Jet(compact);
        compactJet.Draw(model, new(new(0, 0, 800, 480), null, false));
        Assert.Contains("20%", compact.Seen);
        Assert.DoesNotContain("REV", compact.Seen);
        Assert.DoesNotContain("IDLE", compact.Seen);
    }

    // ── ink dominance (P1035 review follow-up) ────────────────────────────
    [Theory]
    [InlineData(0.25f)]   // 800×480 density: ink floors at 1 px
    [InlineData(1f)]      // 1080p reference
    [InlineData(4f)]      // 4K density
    public void The_under_stroke_never_dominates_the_ink(float scale)
    {
        var surface = new CountingSurface();
        var painter = new JetVectorPainter(surface, Color.White, Color.Gray, Color.White, Color.Black, Color.Black);
        painter.Scale = scale;
        float ink = painter.Stroke;
        painter.Line(new(0, 0), new(100, 0), ink, Color.White);
        Assert.Equal(2, surface.Lines);
        Assert.Equal(Color.Black, surface.FirstLineColor);
        Assert.True(surface.FirstLineWidth > surface.SecondLineWidth);
        // Dark allowance on each side ≤ half the ink, so the ink is the dominant
        // stroke; and the ink never resolves below one physical pixel.
        float allowance = painter.LineKeylineAllowance(ink);
        Assert.True(allowance <= ink * 0.5f + 1e-4f);
        Assert.Equal(ink, surface.SecondLineWidth);
        Assert.True(surface.SecondLineWidth >= 1f);
        output.WriteLine($"scale {scale}: ink {ink:0.00} px, keyline {allowance:0.00} px/side, " +
            $"dark {surface.FirstLineWidth:0.00} px");
    }

    [Fact]
    public void The_800x480_keyline_is_no_wider_than_the_ink()
    {
        var painter = new JetVectorPainter(new CountingSurface(), Color.White, Color.Gray, Color.White,
            Color.Black, Color.Black);
        painter.Scale = ModernHudScale.Core(1f, 1f, 480f);
        // 1.5 px × 0.667 would be 1 px, which splits across two rows at ~50 %
        // coverage; the P1036 floor keeps the ink at 1.5 px so one row is fully
        // covered and the green is dominant on a bright backdrop.
        Assert.Equal(JetVectorPainter.MinimumLineStroke, painter.Stroke);
        Assert.Equal(0.75f, painter.LineKeylineAllowance(JetVectorPainter.MinimumLineStroke));
        Assert.Equal(3f, JetVectorPainter.MinimumLineStroke
            + 2f * painter.LineKeylineAllowance(JetVectorPainter.MinimumLineStroke));
    }

    // ── allocation + smoke, every HudModel state ─────────────────────────
    [Fact]
    public void Jet_draw_allocates_zero_bytes_per_frame_with_the_full_symbology()
    {
        var surface = new CountingSurface();
        var noop = new HudSkinResources(_ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });
        using var jet = Jet(surface, noop);
        var model = HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit,
            velocityMarker: new(900, 560), flightReference: Reference(18f, -25f, 210f),
            contacts: new[]
            {
                new HudContact(HudContactKind.Ship, new(600, 400), true, 1f, new HudColor(.5f, .5f, 1f),
                    false, false, Name: "Friendly", Range: 800, IsSameSide: true),
            },
            trainingOverlayId: 2, notices: new[] { new HudNotice("TIP: welcome", false, 1f) });
        var frame = new HudDrawFrame(new Viewport(0, 0, 1920, 1080), null, true);
        void Draw(int count)
        {
            for (int i = 0; i < count; i++) jet.Draw(model, frame);
        }
        Draw(10_000);                       // warm the text/tick caches
        Assert.True(surface.Lines > 0);
        Assert.Contains("HDG 210", surface.Seen);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Draw(100_000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P1036 JetHudRenderer.Draw (ladder+tapes+FPM): {bytes} B / 100000 frames = {bytes / 100000d} B/frame");
        Assert.Equal(0, bytes);
    }

    public static TheoryData<string> States => new()
    {
        "cockpit", "chase", "command", "docked", "lifepod", "dead", "ghost", "turret", "training",
        "hidden", "no-telemetry", "no-reference",
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Jet_draws_every_state_without_throwing_and_never_invents_values(string state)
    {
        var surface = new CountingSurface();
        var resources = new Counting();
        using var jet = Jet(surface, resources.Resources);
        var model = State(state);
        var frame = new HudDrawFrame(new Viewport(0, 0, 1280, 720), null, state == "turret");
        jet.Draw(model, frame);
        jet.DrawCommand(model);
        jet.DrawConsole(model);

        // P1039 — Jet draws its OWN radar scope (P972 §4.4 RWR): the shared Classic radar pass
        // is no longer composed for the Jet skin, so the resource stays untouched.
        Assert.Equal(0, resources.Radar);
        Assert.Equal(1, resources.Tags);
        Assert.Equal(1, resources.PodDeath);
        Assert.Equal(1, resources.Chat);
        Assert.Equal(1, resources.Cargo);
        Assert.Equal(1, resources.Parts);
        Assert.Equal(1, resources.Command);
        Assert.True(resources.CommandWasClassicComposition);
        Assert.False(resources.TagsWereModern);

        if (state == "hidden")
        {
            Assert.Equal(0, surface.Lines + surface.Fills + surface.Texts);
            return;
        }

        Assert.Contains(JetHudRenderer.TagText, surface.Seen);
        bool instruments = model.InstrumentsVisible && model.Telemetry is not null;
        Assert.Equal(instruments, surface.Seen.Contains("SPD / MPS"));
        Assert.Equal(instruments, surface.Seen.Contains("THR"));
        // The heading readout exists exactly with the instrument group; without a
        // reference it is the honest "HDG --".
        bool headingBox = surface.Seen.Any(text => text.StartsWith("HDG", StringComparison.Ordinal));
        Assert.Equal(model.InstrumentsVisible, headingBox);
        if (model.InstrumentsVisible && model.Context.FlightReference is null)
            Assert.Contains("HDG --", surface.Seen);
        if (!model.InstrumentsVisible)
            Assert.DoesNotContain(surface.Seen, text => text.StartsWith("HDG", StringComparison.Ordinal));
        // Never a fabricated ladder numeral when there is no reference.
        if (!model.InstrumentsVisible || model.Context.FlightReference is null)
            Assert.DoesNotContain("+10", surface.Seen);
    }

    // ── fixtures ─────────────────────────────────────────────────────────
    /// <summary>The sim attitude used by the flight scenes: forward at
    /// <paramref name="headingDegrees"/> in the plane, pitched, then rolled about
    /// forward with the sim's own <see cref="Orientation.PostRotate"/>.</summary>
    private static HudFlightReference Reference(float pitchDegrees, float rollDegrees, float headingDegrees)
    {
        float heading = headingDegrees * MathF.PI / 180f;
        float pitch = pitchDegrees * MathF.PI / 180f;
        float roll = rollDegrees * MathF.PI / 180f;
        var forward = new Allegiance.Core.Collision.Vector3(
            MathF.Cos(heading) * MathF.Cos(pitch),
            MathF.Sin(heading) * MathF.Cos(pitch),
            MathF.Sin(pitch));
        var levelUp = new Allegiance.Core.Collision.Vector3(
            -MathF.Cos(heading) * MathF.Sin(pitch),
            -MathF.Sin(heading) * MathF.Sin(pitch),
            MathF.Cos(pitch));
        var orientation = new Orientation();
        orientation.Set(forward, levelUp);
        if (roll != 0f) orientation.PostRotate(forward, roll);
        return HudFlightReference.FromSim(orientation, forward * 260f, HudFlightReference.SectorUp);
    }

    private static HudFrame Frame(float speed, float throttle = .5f) => new(
        .8f, .6f, .4f, .3f, .2f, 1f, speed, throttle, false, false, true, 0,
        false, 0f, false, false, 0f, 0f, Array.Empty<WeaponStatusRow>(), false, 0);

    private static HudModel FlightModel(HudFlightReference? reference)
        => HudModelAdapter.Create(Frame(260f), CameraMode.Cockpit, velocityMarker: new(900, 560),
            flightReference: reference);

    private static HudModel State(string state) => state switch
    {
        "cockpit" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit, velocityMarker: new(940, 520),
            flightReference: Reference(0f, 0f, 45f), orderArrows: new(new(0.4f, 0f), new(-0.4f, 0f), false)),
        "chase" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Chase,
            flightReference: Reference(6f, 3f, 90f)),
        "command" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Command, commandView: true,
            flightReference: Reference(0f, 0f, 45f)),
        "docked" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit, docked: true,
            flightReference: Reference(0f, 0f, 45f)),
        "lifepod" => HudModelAdapter.Create(Frame(60f, 0f) with { IsAdriftLifepod = true, OxygenSecondsRemaining = 42 },
            CameraMode.Cockpit, flightReference: Reference(0f, 0f, 45f)),
        "dead" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit, dead: true,
            flightReference: Reference(0f, 0f, 45f)),
        "ghost" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit, ghost: true,
            flightReference: Reference(0f, 0f, 45f)),
        "turret" => HudModelAdapter.Create(Frame(0f, -1f), CameraMode.ExternalTurret, turret: true,
            flightReference: Reference(-20f, 0f, 45f)),
        "training" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit, trainingOverlayId: 3,
            flightReference: Reference(0f, 0f, 45f)),
        "hidden" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit, hidden: true,
            flightReference: Reference(0f, 0f, 45f)),
        "no-telemetry" => HudModelAdapter.Create(null, CameraMode.Cockpit, flightReference: Reference(0f, 0f, 45f)),
        "no-reference" => HudModelAdapter.Create(Frame(260f, .6f), CameraMode.Cockpit, velocityMarker: new(900, 560)),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown state"),
    };

    private static JetHudRenderer Jet(IJetVectorSurface surface, HudSkinResources? resources = null)
    {
        var definition = HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!;
        return new JetHudRenderer(surface, new HudSettings { Skin = HudSkinIds.Jet },
            resources ?? new Counting().Resources, definition);
    }

    /// <summary>Allocation-free surface: counts submissions and remembers the text.</summary>
    private sealed class CountingSurface : IJetVectorSurface
    {
        public int Lines, Fills, Texts, Begins, Ends;
        public Color FirstLineColor;
        public float FirstLineWidth, SecondLineWidth;
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);

        public void Begin() => Begins++;
        public void End() => Ends++;
        public void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            if (Lines == 0) { FirstLineColor = color; FirstLineWidth = width; }
            if (Lines == 1) SecondLineWidth = width;
            Lines++;
        }
        public void Fill(Vector2 min, Vector2 size, Color color) => Fills++;
        public void Text(string text, Vector2 position, bool large, float scale, Color color)
        {
            Texts++;
            Seen.Add(text);
        }
        public Vector2 Measure(string text, bool large, float scale) => new(text.Length * 6f * scale, 12f * scale);
    }

    /// <summary>The shared-pass counters (HudSkinResources is a record of callbacks).</summary>
    private sealed class Counting
    {
        public int Radar, Tags, PodDeath, Command, Chat, Cargo, Parts;
        public bool TagsWereModern, CommandWasClassicComposition;
        public HudSkinResources Resources { get; }

        public Counting()
        {
            Resources = new HudSkinResources(
                _ => Radar++,
                (_, modern) => { Tags++; TagsWereModern |= modern; },
                _ => PodDeath++,
                (_, classic) => { Command++; CommandWasClassicComposition = classic; },
                _ => Chat++,
                _ => Cargo++,
                _ => Parts++);
        }
    }
}
