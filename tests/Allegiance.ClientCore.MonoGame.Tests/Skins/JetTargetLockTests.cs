using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit.Abstractions;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1037 — Jet story 3/9: the target designator, closure, lock, lead/aim predictor
/// and order steering (P972 §4.2 + §4.3, phase 3B), plus the four P1036 review
/// layout follow-ups (heading tape, tape labels/captions, throttle rail, horizon
/// rung).
/// <para>Headless: every renderer assertion goes through a recording
/// <see cref="IJetVectorSurface"/>, so the geometry, the shared-model gate parity,
/// the lock flash phase, the skin-switch/no-audio rule, the layout invariants and
/// the 0 B/frame gate all run without a graphics device. The closure tests use the
/// real <see cref="ClientWorld"/> wire messages the shared model reads.</para>
/// </summary>
public sealed class JetTargetLockTests(ITestOutputHelper output)
{
    private const short Sector = 10;

    // ── closure: one derivation, one sign convention ─────────────────────
    [Fact]
    public void Closure_is_positive_when_the_separation_decreases()
    {
        // Own ship flies at 100 m/s toward a target 1000 m ahead that runs at 50 m/s
        // the same way: the range rate is +50 m/s.
        float? closing = TargetClosingSpeed.Compute(
            new SimVec(0f, 0f, 0f), new SimVec(100f, 0f, 0f),
            new SimVec(1000f, 0f, 0f), new SimVec(50f, 0f, 0f));
        Assert.Equal(50f, closing!.Value, 2);
    }

    [Fact]
    public void Closure_is_negative_when_the_target_recedes()
    {
        // Target outruns the own ship: the separation grows.
        float? closing = TargetClosingSpeed.Compute(
            new SimVec(0f, 0f, 0f), new SimVec(40f, 0f, 0f),
            new SimVec(1000f, 0f, 0f), new SimVec(120f, 0f, 0f));
        Assert.Equal(-80f, closing!.Value, 2);
    }

    [Fact]
    public void Closure_of_a_static_target_is_the_own_approach_component()
    {
        // A station/asteroid/warp carries no velocity: closing is the own velocity's
        // projection on the line of sight (the branch TargetPaneModel.TryTargetPose
        // supplies with a zero target velocity).
        float? closing = TargetClosingSpeed.Compute(
            new SimVec(0f, 0f, 0f), new SimVec(0f, 0f, -30f),
            new SimVec(0f, 0f, -900f), default);
        Assert.Equal(30f, closing!.Value, 2);
    }

    [Fact]
    public void Closure_is_unknown_at_zero_distance_instead_of_a_zero_reading()
    {
        Assert.Null(TargetClosingSpeed.Compute(new SimVec(10f, 0f, 0f), new SimVec(1f, 0f, 0f),
            new SimVec(10f, 0f, 0f), new SimVec(0f, 0f, 0f)));
    }

    [Fact]
    public void The_target_pane_reads_the_same_closure_as_the_projected_bracket_off_screen_too()
    {
        // The own ship approaches a target that is 1000 m away, well outside any view
        // frustum: the bracket's projection would return null there, but the shared
        // derivation (and therefore the pane) keeps the reading (P972 §4.3).
        ClientWorld world = World();
        AddShip(world, 2, Sector, (1500f, 0f, 0f), (-20f, 0f, 0f), hull: .6f, shield: .3f);

        float? expected = TargetClosingSpeed.Compute(new SimVec(0f, 0f, 0f), new SimVec(120f, 0f, 0f),
            new SimVec(1500f, 0f, 0f), new SimVec(-20f, 0f, 0f));
        Assert.Equal(140f, expected!.Value, 2);

        HudTargetPane pane = TargetPaneModel.Build(world, IgcObjectType.Ship, 2);
        Assert.True(pane.Present);
        Assert.Equal(expected, pane.ClosingSpeed);
        Assert.Equal(1500f, pane.Distance!.Value, 1);
    }

    // ── target designator sizing clamps ──────────────────────────────────
    [Theory]
    [InlineData(true, "HOSTILE")]
    [InlineData(false, "FRIEND")]
    [InlineData(null, "NEUTRAL")]
    public void The_designator_carries_the_relationship_by_word_and_shape(bool? enemy, string word)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var model = Model(
            target: new HudTarget(new(1160f, 362f), true, "target", .6f, .3f, enemy, null, 1250f, 43f, null, 60f),
            flightReference: Reference(0f, 0f, 45f));
        jet.Draw(model, DrawFrame());

        Assert.Contains(word, surface.Seen);
        int cornerSegments = surface.linesNear(new Vector2(1160f - 60f * 1.05f, 362f - 60f * 1.05f), 30f);
        if (enemy == false)
            // Friend: the corner arms are dashed/open, so one corner is several short
            // strokes rather than the two solid bracketing arms.
            Assert.True(cornerSegments > 2, $"expected dashed corner arms, got {cornerSegments} segments");
        else
            Assert.True(cornerSegments >= 2);
        if (enemy == true)
            // Hostile: an inner diagonal tick at each corner is the extra shape cue.
            Assert.Contains(surface.Segments(), s => IsDiagonalTick(s.A, s.B));
    }

    private static bool IsDiagonalTick(Vector2 a, Vector2 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        return MathF.Abs(MathF.Abs(dx) - MathF.Abs(dy)) < 0.6f && MathF.Abs(dx) > 2f;
    }

    [Fact]
    public void The_designator_half_span_clamps_to_its_floor_at_800x480_and_its_ceiling_at_4K()
    {
        // 800×480 floor (scale 0.667) and the 4K reference density (scale 1.0) both
        // keep a tiny projected radius at the physical floor.
        Assert.Equal(JetTargetGeometry.MinimumHalfPx, JetTargetGeometry.DesignatorHalf(0.5f, 2f / 3f, 1000f), 2);
        Assert.Equal(JetTargetGeometry.MinimumHalfPx, JetTargetGeometry.DesignatorHalf(0.5f, 1f, 2000f), 2);
        // A hull close to the camera cannot fill the screen at either size.
        Assert.Equal(JetTargetGeometry.MaximumHalfPx, JetTargetGeometry.DesignatorHalf(9000f, 2f / 3f, 1200f), 2);
        Assert.Equal(JetTargetGeometry.MaximumHalfPx, JetTargetGeometry.DesignatorHalf(9000f, 1f, 4000f), 2);
    }

    [Fact]
    public void An_unknown_screen_radius_falls_back_to_the_fixed_half_span_scaled_by_density()
    {
        Assert.Equal(JetTargetGeometry.DefaultHalf, JetTargetGeometry.DesignatorHalf(null, 1f, 1000f), 2);
        Assert.Equal(JetTargetGeometry.DefaultHalf * 2f / 3f, JetTargetGeometry.DesignatorHalf(null, 2f / 3f, 500f), 2);
    }

    [Fact]
    public void The_designator_is_clipped_to_the_viewport_edge_like_the_native_bracket()
    {
        // Native clips radiusBracket to distanceToEdge + c_flBorderSide
        // (radarimage.cpp:308-319); the Jet designator reuses that gutter.
        Assert.Equal(40f, JetTargetGeometry.DesignatorHalf(120f, 1f, 30f), 2);
    }

    // ── lock: none / partial / full + flash phase ────────────────────────
    [Fact]
    public void No_lock_draws_nothing()
    {
        var none = new Recorder();
        using (var jet = Jet(none))
            jet.Draw(Model(lockState: new HudLock(new(900f, 400f), MissileLockFeedback.LockState.None, 1.0)), DrawFrame());
        var absent = new Recorder();
        using (var jet = Jet(absent))
            jet.Draw(Model(), DrawFrame());

        Assert.DoesNotContain(LockCaption, none.Seen);
        Assert.Equal(absent.TextRuns.Count, none.TextRuns.Count);
        Assert.Equal(absent.LineCount, none.LineCount);
    }

    [Fact]
    public void A_partial_lock_is_open_corner_arcs_with_its_percentage()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var lockState = new HudLock(new(900f, 400f), MissileLockFeedback.LockState.Partial, 1.0, .62f);
        jet.Draw(Model(lockState: lockState), DrawFrame());

        Assert.Contains(LockCaption, surface.Seen);
        Assert.Contains("62%", surface.Seen);
        // P1038c — four corner arcs, every other of the four dashes inked: 8 arc pieces,
        // each a 30°·¼ = 7.5° dash at the cue's corner distance.
        float half = JetTargetGeometry.DesignatorHalf(null, 1f, 900f);
        float distance = JetTargetGeometry.LockCueCornerDistance(half, .62f, 1f);
        Assert.Equal(8, surface.CueArcs.Count);
        foreach (var arc in surface.CueArcs)
        {
            Assert.Equal(distance, arc.Radius, 2);
            Assert.Equal(JetTargetGeometry.LockCornerSweep / JetTargetGeometry.LockCornerDashes,
                arc.Sweep, 4);
            Assert.Equal(900f, arc.Center.X, 2);
            Assert.Equal(400f, arc.Center.Y, 2);
        }
        // The readout block is reported for the cue-vs-own-readout audit.
        Assert.Single(surface.CueReadouts);
        Assert.DoesNotContain(surface.SegmentColors, color => color == Hot);
    }

    [Fact]
    public void A_full_lock_is_closed_and_hot()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var lockState = new HudLock(new(900f, 400f), MissileLockFeedback.LockState.Full, 1.0, 1f);
        jet.Draw(Model(lockState: lockState), DrawFrame());

        Assert.Contains(LockCaption, surface.Seen);
        Assert.DoesNotContain("100%", surface.Seen);   // a full lock has no percentage readout
        // P1038c — one closed 30° arc per corner (no dashes), at the full-lock distance.
        float half = JetTargetGeometry.DesignatorHalf(null, 1f, 900f);
        float distance = JetTargetGeometry.LockCueCornerDistance(half, 1f, 1f);
        Assert.Equal(4, surface.CueArcs.Count);
        foreach (var arc in surface.CueArcs)
        {
            Assert.Equal(distance, arc.Radius, 2);
            Assert.Equal(JetTargetGeometry.LockCornerSweep, arc.Sweep, 4);
        }
        Assert.Contains(surface.SegmentColors, color => color == Hot);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 0)]
    [InlineData(1.5, 1)]
    public void The_partial_lock_dash_phase_comes_from_the_shared_clock(double nowSeconds, int phase)
    {
        Assert.Equal(phase, JetTargetGeometry.LockPhase(nowSeconds));
    }

    [Fact]
    public void The_partial_lock_dashes_march_between_the_two_phases()
    {
        int firstPhase = 0, secondPhase = 0;
        for (int i = 0; i < JetTargetGeometry.LockCornerDashes * JetTargetGeometry.LockCornerCount; i++)
        {
            if (JetTargetGeometry.LockSegmentVisible(i, JetTargetGeometry.LockCornerDashes,
                    MissileLockFeedback.LockState.Partial, 0.0)) firstPhase++;
            if (JetTargetGeometry.LockSegmentVisible(i, JetTargetGeometry.LockCornerDashes,
                    MissileLockFeedback.LockState.Partial, 0.5)) secondPhase++;
        }
        Assert.Equal(8, firstPhase);
        Assert.Equal(8, secondPhase);
        Assert.True(JetTargetGeometry.LockSegmentVisible(0, JetTargetGeometry.LockCornerDashes,
            MissileLockFeedback.LockState.Partial, 0.0)
            != JetTargetGeometry.LockSegmentVisible(0, JetTargetGeometry.LockCornerDashes,
                MissileLockFeedback.LockState.Partial, 0.5));
        // Full/none never depend on the clock.
        Assert.True(JetTargetGeometry.LockSegmentVisible(3, 4, MissileLockFeedback.LockState.Full, 0.0));
        Assert.True(JetTargetGeometry.LockSegmentVisible(3, 4, MissileLockFeedback.LockState.Full, 0.5));
        Assert.False(JetTargetGeometry.LockSegmentVisible(3, 4, MissileLockFeedback.LockState.None, 0.5));
    }

    [Fact]
    public void Switching_skins_mid_lock_produces_no_extra_feedback_or_audio_event()
    {
        // The lock tone and the transition tips are shared-state driven: the tone gate
        // is MissileLockState.IsFullLock (world data) and the tip is a pure function of
        // the (prev, curr, targetChanged) lock states — a renderer/skin is not an input
        // to either (MissileLockFeedback.cs:51-60, SoundManager.cs:1222-1233).
        var worldLock = new MissileLockState(1f, 7, SimVec.Zero, "SRM");
        Assert.True(worldLock.IsFullLock);
        Assert.Null(MissileLockFeedback.TipFor(MissileLockFeedback.LockState.Full,
            MissileLockFeedback.LockState.Full, targetChanged: false, "SRM"));

        var surface = new Recorder();
        var resources = new Counting();
        var model = Model(lockState: new HudLock(new(900f, 400f), MissileLockFeedback.LockState.Full, 1.0, 1f));

        // Classic → Jet → Modern with the lock held. Each draw is one shared-pass set;
        // the lock cue appears exactly once per frame (9 keyline+ink submissions for a
        // single text run), never twice because a switch "restarted" it.
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        var jet = new JetHudRenderer(surface, settings, resources.Resources,
            HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!);
        var switcher = new HudSkinSwitcher(settings, new Stub(HudSkinIds.Classic),
            new Stub(HudSkinIds.Modern), jet);
        Assert.Same(jet, switcher.Active.Renderer);

        jet.Draw(model, DrawFrame());
        Assert.Equal(9, surface.count(LockCaption));
        // P1039 — Jet draws its OWN radar scope (P972 §4.4 RWR): the shared Classic radar pass
        // is no longer composed for the Jet skin, so the resource stays untouched.
        Assert.Equal(0, resources.Radar);

        Assert.True(switcher.Request(HudSkinIds.Classic));
        Assert.True(switcher.ApplyPending());
        Assert.Same(switcher.Active.Renderer, switcher.Active.Renderer);
        jet.Draw(model, DrawFrame());
        Assert.Equal(18, surface.count(LockCaption));   // one cue per frame, no replay
        Assert.Equal(0, resources.Radar);              // no shared radar pass, ever (P1039)
        Assert.Equal(2, surface.Frames);               // no second batch session per frame
        // The renderer has no audio/feedback dependency at all: the lock tone lives
        // in SoundManager behind the world's IsFullLock gate, not in a skin.
        Assert.DoesNotContain(typeof(JetHudRenderer).GetMembers(), m => m.Name.Contains("Sound"));
        switcher.Dispose();
    }

    // ── lead / aim predictor gate parity ─────────────────────────────────
    public static TheoryData<CameraMode, bool, bool, bool> LeadGates => new()
    {
        { CameraMode.Cockpit, true, true, true },
        { CameraMode.Cockpit, true, true, false },
        { CameraMode.Cockpit, false, true, true },   // granted gate fails
        { CameraMode.Cockpit, true, false, true },   // projection gate fails
        { CameraMode.Chase, false, true, true },
        { CameraMode.Chase, false, true, false },
    };

    [Theory]
    [MemberData(nameof(LeadGates))]
    public void The_lead_marker_follows_the_shared_LeadMarkerVisible_gate(
        CameraMode camera, bool granted, bool visible, bool inRange)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var lead = new HudLead(new(1000f, 300f), new(1200f, 340f), inRange, .8f, 1.0,
            Granted: granted, Visible: visible);
        var model = Model(camera: camera, lead: lead);
        jet.Draw(model, DrawFrame());

        Vector2 center = new(1000f, 300f);
        int ringSegments = surface.circleSegments(center, JetTargetGeometry.LeadRadius);
        Assert.Equal(model.LeadMarkerVisible, ringSegments > 0);
        Assert.Equal(model.LeadMarkerVisible && inRange, surface.Seen.Contains(InRangeLabel));
    }

    [Theory]
    [MemberData(nameof(LeadGates))]
    public void The_aim_predictor_follows_the_shared_AimPredictorVisible_gate(
        CameraMode camera, bool granted, bool visible, bool inRange)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var lead = new HudLead(new(1000f, 300f), new(1200f, 340f), inRange, .8f, 1.0,
            Granted: granted, Visible: visible);
        var model = Model(camera: camera, lead: lead);
        jet.Draw(model, DrawFrame(new Viewport(0, 0, 1920, 1080)));

        // In chase the aim point is the projected lead point; in cockpit it is the
        // boresight (native indicator.cpp:334-362) — both share the one gate.
        Vector2 center = camera == CameraMode.Cockpit ? new(960f, 540f) : new(1000f, 300f);
        int brackets = surface.cornerBracketSegments(center, JetTargetGeometry.AimRadius);
        Assert.Equal(model.AimPredictorVisible, brackets > 0);
    }

    // ── order steering ───────────────────────────────────────────────────
    public static TheoryData<string, bool, bool, bool> OrderCombinations => new()
    {
        { "accepted", true, false, false },
        { "current", false, true, false },
        { "both", true, true, false },
        { "same", true, false, true },
        { "none", false, false, false },
    };

    [Theory]
    [MemberData(nameof(OrderCombinations))]
    public void Every_order_arrow_combination_draws_the_native_directions(
        string kind, bool accepted, bool current, bool sameTarget)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var arrows = new HudOrderArrows(
            accepted || sameTarget ? new HudPoint(1f, 0f) : null,
            current ? new HudPoint(0f, 1f) : null, sameTarget);
        var model = Model(orderArrows: arrows);
        jet.Draw(model, DrawFrame());

        Vector2 center = new(960f, 540f);
        Vector2 right = JetTargetGeometry.OrderAnchor(center, new Vector2(1f, 0f), 1f);
        Vector2 down = JetTargetGeometry.OrderAnchor(center, new Vector2(0f, 1f), 1f);
        bool acceptedArrow = surface.linesNear(right, 24f) > 0;
        bool currentArrow = surface.linesNear(down, 24f) > 0;

        Assert.True(kind is "accepted" or "current" or "both" or "same" or "none");
        Assert.Equal(model.OrderArrowsVisible, acceptedArrow || currentArrow);
        Assert.Equal(accepted || sameTarget, acceptedArrow);
        Assert.Equal(current, currentArrow);
        if (sameTarget)
            Assert.Contains(SameTargetLabel, surface.Seen);
        else
        {
            Assert.Equal(accepted, surface.Seen.Contains(AcceptedLabel));
            Assert.Equal(current, surface.Seen.Contains(CurrentLabel));
        }
    }

    // ── target data block ────────────────────────────────────────────────
    [Fact]
    public void The_target_block_renders_every_supplied_field()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var pane = new HudTargetPane(Present: true, Name: "Enemy fighter", TypeDescription: "Fighter",
            SideName: "Rixian", SideColor: new HudColor(1f, 0f, 0f), HullFraction: .6f, ShieldFraction: .3f,
            Distance: 1250f, Speed: 43f, OreFraction: null, KillBonusPercent: 15f, InRange: true, IsShip: true,
            TimeToIntercept: "04.1 s", ClosingSpeed: 43f);
        var model = Model(targetPane: pane);
        jet.Draw(model, DrawFrame(new Viewport(0, 0, 1920, 1080)));

        Assert.Contains("Enemy fighter", surface.Seen);
        Assert.Contains("Fighter", surface.Seen);
        Assert.Contains("Rixian", surface.Seen);
        Assert.Contains("1250 m", surface.Seen);
        Assert.Contains("+43", surface.Seen);
        Assert.Contains("060", surface.Seen);
        Assert.Contains("030", surface.Seen);
        Assert.Contains("43 mps", surface.Seen);
        Assert.Contains("15", surface.Seen);
        Assert.Contains("04.1 s", surface.Seen);
        Assert.Contains(InRangeLabel, surface.Seen);
    }

    [Fact]
    public void The_target_block_never_prints_a_zero_for_an_unknown_field()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        // Native pane sentinels: hull/shield 0 = not visible/live, speed -1 = unseen,
        // no range -> LOCATION: sector fallback (consoledata.cpp:202-212, dialog.mdl:4053-4060).
        var pane = new HudTargetPane(Present: true, Name: "Ghost hull", TypeDescription: null,
            SideName: null, SideColor: TargetPaneModel.Neutral, HullFraction: 0f, ShieldFraction: 0f,
            Distance: null, Speed: -1f, OreFraction: null, KillBonusPercent: null, InRange: false,
            IsShip: true, SectorName: "Alystra", TimeToIntercept: null);
        var model = Model(targetPane: pane);
        jet.Draw(model, DrawFrame(new Viewport(0, 0, 1920, 1080)));

        Assert.Contains("LOC Alystra", surface.Seen);
        // P1038 — the stores block draws its own HULL/SHLD rows from the gauges, so the
        // target-block assertion is confined to the data block's own band (x ≈ w/2+70).
        var blockRuns = surface.TextRuns.Where(r => r.X >= 1020f && r.X < 1400f && r.Y > 540f).ToList();
        foreach (var run in blockRuns)
        {
            Assert.NotEqual(HullWord, run.Text);
            Assert.NotEqual(ShieldWord, run.Text);
            Assert.NotEqual(TargetSpeedWord, run.Text);
            Assert.NotEqual("-1", run.Text);
            Assert.DoesNotContain("000", run.Text);
        }
        Assert.DoesNotContain(InRangeLabel, surface.Seen);
    }

    // ── off-screen target ────────────────────────────────────────────────
    [Fact]
    public void An_off_screen_target_gets_an_edge_chevron_from_the_shared_bearing()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var pane = new HudTargetPane(Present: true, Name: "Far hull", TypeDescription: "Fighter",
            SideName: "Rixian", SideColor: TargetPaneModel.Neutral, HullFraction: .5f, ShieldFraction: .4f,
            Distance: 9000f, Speed: 10f, OreFraction: null, KillBonusPercent: 0f, InRange: false, IsShip: true,
            ClosingSpeed: -25f);
        var model = Model(targetPane: pane, bearing: new HudPoint(0.8f, -0.2f));
        jet.Draw(model, DrawFrame());

        Assert.Null(model.Context.Target);            // nothing projects into view
        Vector2 anchor = JetTargetGeometry.EdgeChevronAnchor(new HudPoint(0.8f, -0.2f), 1920f, 1080f, 1f);
        Assert.InRange(anchor.X, 1860f, 1920f);       // on the right edge, inside the inset
        Assert.True(surface.linesNear(anchor, 20f) > 0);
        Assert.Contains("9000 m", surface.Seen);      // the pane keeps the reading
        Assert.Contains("-25", surface.Seen);
    }

    // ── P1036 review follow-ups ──────────────────────────────────────────
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(800, 480)]
    public void Heading_labels_clear_the_top_screen_edge(int width, int height)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var model = Model(flightReference: Reference(0f, 0f, 45f));
        jet.Draw(model, DrawFrame(new Viewport(0, 0, width, height)));

        float scale = ModernHudScale.Core(1f, 1f, height);
        Assert.DoesNotContain(surface.TextRuns, run => run.Y < 2f * scale);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(800, 480)]
    public void The_heading_caret_sits_outside_the_readout_box(int width, int height)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var model = Model(flightReference: Reference(0f, 0f, 45f));
        jet.Draw(model, DrawFrame(new Viewport(0, 0, width, height)));

        RectangleF plate = surface.PlateAt(width * 0.5f);
        Assert.True(plate.Width > 0f && plate.Y < 200f);
        // The only line allowed inside the plate is the readout's own keyline tick; a
        // segment that crosses a plate edge (the caret, a label) is a failure.
        foreach (var (a, b) in surface.Segments())
            Assert.False(CrossesBorder(plate, a, b), $"segment {a}-{b} crosses the HDG plate {plate}");
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(800, 480)]
    public void Tape_labels_never_overlap_a_caption_or_the_readout_plate(int width, int height)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var model = Model(flightReference: Reference(0f, 0f, 45f), speed: 260f, throttle: -.6f);
        jet.Draw(model, DrawFrame(new Viewport(0, 0, width, height)));

        RectangleF speedCaption = surface.Caption(SpeedCaptionText);
        RectangleF throttleCaption = surface.Caption(ThrottleCaptionText);
        foreach (var run in surface.TextRuns)
        {
            if (run.Text != SpeedCaptionText)
                Assert.False(Overlaps(speedCaption, run), $"label {run.Text} overlaps the SPD caption");
            if (run.Text != ThrottleCaptionText)
                Assert.False(Overlaps(throttleCaption, run), $"label {run.Text} overlaps the THR caption");
        }
        // Every plate on the frame may only contain its own readout ink.
        foreach (RectangleF plate in surface.Plates())
            foreach (var run in surface.TextRuns)
                Assert.False(Overlaps(plate, run) && !Contains(plate, run),
                    $"label {run.Text} overlaps plate {plate}");
    }

    [Fact]
    public void At_800x480_the_throttle_rail_is_clipped_to_its_scale()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var model = Model(flightReference: Reference(0f, 0f, 45f), throttle: -.6f, speed: 40f);
        jet.Draw(model, DrawFrame(new Viewport(0, 0, 800, 480)));

        float scale = ModernHudScale.Core(1f, 1f, 480);
        float tapeX = 800f - 76f * scale;
        float cy = 240f;
        float halfSpan = 480f * 0.30f;
        float perPercent = JetFlightGeometry.ThrottleTapeScale(480f);
        int percent = JetFlightGeometry.ThrottlePercent(-.6f);
        float zeroRow = cy - (0f - percent) * perPercent;

        // The rail is the long vertical stroke at the tape column; it must end at the
        // lowest DRAWN tick (the 000 row), not at the nominal span bottom.
        float railBottom = float.MinValue;
        foreach (var (a, b) in surface.Segments())
        {
            if (MathF.Abs(a.X - tapeX) > 1.5f || MathF.Abs(b.X - tapeX) > 1.5f) continue;
            railBottom = MathF.Max(railBottom, MathF.Max(a.Y, b.Y));
        }
        Assert.True(railBottom <= zeroRow + 2f, $"rail bottom {railBottom} passes the 000 row {zeroRow}");
        Assert.True(railBottom < cy + halfSpan - 2f);
    }

    [Fact]
    public void Level_flight_keeps_the_horizon_rung_split_around_the_gun_cross()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var model = Model(flightReference: Reference(0f, 0f, 45f));
        jet.Draw(model, DrawFrame());

        float scale = ModernHudScale.Core(1f, 1f, 1080);
        float cx = 960f, cy = 540f;
        // Two horizon halves, each entirely outside the centre gap.
        int halves = 0;
        foreach (var (a, b) in surface.Segments())
        {
            if (MathF.Abs(a.Y - cy) > 1.5f || MathF.Abs(b.Y - cy) > 1.5f) continue;
            float inner = MathF.Min(MathF.Abs(a.X - cx), MathF.Abs(b.X - cx));
            float outer = MathF.Max(MathF.Abs(a.X - cx), MathF.Abs(b.X - cx));
            if (inner >= JetTargetGeometryEdgeGap(scale) - 1f && outer >= 40f * scale) halves++;
        }
        Assert.True(halves >= 2, "the 0° rung is missing at level flight");
    }

    private static float JetTargetGeometryEdgeGap(float scale)
    {
        // The horizon gap half-width is 27 × scale (JetHudRenderer.HorizonGapHalfWidth);
        // expose it through a segment the renderer submits: read the smallest inner
        // half-width observed on the split horizon instead of duplicating the constant.
        return 27f * scale;
    }

    // ── allocation gate ──────────────────────────────────────────────────
    [Fact]
    public void Draw_with_a_locked_leading_ordered_target_allocates_nothing()
    {
        // A counter-only surface: the recorder above stores every submission, which
        // would itself allocate inside the measured loop.
        var surface = new Counter();
        var noop = new HudSkinResources(_ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });
        using var jet = Jet(surface, noop);
        var pane = new HudTargetPane(Present: true, Name: "Enemy fighter", TypeDescription: "Fighter",
            SideName: "Rixian", SideColor: TargetPaneModel.Neutral, HullFraction: .6f, ShieldFraction: .3f,
            Distance: 1250f, Speed: 43f, OreFraction: .4f, KillBonusPercent: 15f, InRange: true, IsShip: true,
            TimeToIntercept: "04.1 s", ClosingSpeed: 43f);
        var model = Model(
            target: new HudTarget(new(1160f, 362f), true, "Enemy fighter", .6f, .3f, true, .4f, 1250f, 43f,
                new HudColor(1f, 0f, 0f), 60f),
            targetPane: pane,
            lockState: new HudLock(new(1160f, 362f), MissileLockFeedback.LockState.Partial, 1.0, .62f, 120f),
            lead: new HudLead(new(1000f, 450f), new(1160f, 362f), true, .8f, 1.0, Granted: true, Visible: true),
            orderArrows: new HudOrderArrows(new(1f, 0f), new(0f, 1f), false),
            flightReference: Reference(0f, 0f, 45f), bearing: new HudPoint(.8f, -.2f));
        var frame = DrawFrame();

        void Draw(int count) { for (int i = 0; i < count; i++) jet.Draw(model, frame); }
        Draw(10_000);
        Assert.True(surface.Texts > 0 && surface.Lines > 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Draw(100_000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P1037 JetHudRenderer.Draw (target+lock+lead+orders+block): {bytes} B / 100000 frames = {bytes / 100000d} B/frame");
        Assert.Equal(0, bytes);
    }

    // ── draw smoke for every HudModel state ─────────────────────────────
    public static TheoryData<string> States => new()
    {
        "cockpit", "chase", "command", "docked", "lifepod", "dead", "ghost", "turret", "training",
        "hidden", "no-telemetry", "no-reference", "target", "locked", "lock-full", "lead", "lead-out",
        "orders", "offscreen", "friendly", "asteroid-ore",
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Jet_draws_every_state_without_throwing_and_never_invents_values(string state)
    {
        var surface = new Recorder();
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
            Assert.Empty(surface.Segments());
            return;
        }
        Assert.Contains(JetHudRenderer.TagText, surface.Seen);
        // A target designator exists exactly when the shared model projects one AND
        // the skin's option (selection bracket) is on.
        bool bracket = model.FlightVisible && model.Context.SelectionBracket && model.Context.Target is not null;
        Assert.Equal(bracket, surface.Seen.Contains(JetTargetGeometry.RelationshipWord(
            JetTargetGeometry.Relationship(model.Context.Target?.IsEnemy))));
        // Lock text exists exactly under the lock gate and a non-None state.
        bool lockCue = model.IndicatorsVisible && model.Context.Lock is { } l
            && l.State != MissileLockFeedback.LockState.None;
        Assert.Equal(lockCue, surface.Seen.Contains(LockCaption));
        // No fabricated zero or native sentinel leaks into a target readout.
        Assert.DoesNotContain(surface.TextRuns, run => run.Text == "-1");
    }

    // ── fixtures ─────────────────────────────────────────────────────────
    private const string LockCaption = "LOCK";
    private const string InRangeLabel = "IN RANGE";
    private const string AcceptedLabel = "ACC";
    private const string CurrentLabel = "CUR";
    private const string SameTargetLabel = "ACC / CUR";
    private const string HullWord = "HULL";
    private const string ShieldWord = "SHLD";
    private const string SpeedCaptionText = "SPD / MPS";
    private const string ThrottleCaptionText = "THR";
    private const string TargetSpeedWord = "SPD";
    private static readonly Color Hot = new(0xC9, 0xFF, 0xE3);

    private static HudFrame Frame(float speed = 260f, float throttle = .6f) => new(
        .8f, .6f, .4f, .3f, .2f, 1f, speed, throttle, false, false, true, 0,
        false, 0f, false, false, 0f, 0f, Array.Empty<WeaponStatusRow>(), false, 0);

    private static HudModel Model(
        CameraMode camera = CameraMode.Cockpit,
        HudTarget? target = null,
        HudTargetPane? targetPane = null,
        HudLock? lockState = null,
        HudLead? lead = null,
        HudOrderArrows? orderArrows = null,
        HudPoint? bearing = null,
        HudFlightReference? flightReference = null,
        HudPoint? velocityMarker = null,
        bool selectionBracket = true,
        bool targetHudVisible = true,
        float speed = 260f,
        float throttle = .6f,
        bool hidden = false,
        bool docked = false,
        bool dead = false,
        bool commandView = false,
        bool ghost = false,
        bool turret = false,
        int trainingOverlayId = 0)
        => HudModel.Create(Frame(speed, throttle), new HudContext
        {
            Camera = camera,
            Hidden = hidden,
            Docked = docked,
            Dead = dead,
            CommandView = commandView,
            Ghost = ghost,
            Turret = turret,
            TrainingOverlayId = trainingOverlayId,
            SelectionBracket = selectionBracket,
            TargetHudVisible = targetHudVisible,
            Target = target,
            TargetPane = targetPane,
            TargetBearing = bearing,
            Lock = lockState,
            Lead = lead,
            OrderArrows = orderArrows,
            FlightReference = flightReference,
            VelocityMarker = velocityMarker ?? new HudPoint(900f, 560f),
        });

    private static HudDrawFrame DrawFrame(Viewport? viewport = null)
        => new(viewport ?? new Viewport(0, 0, 1920, 1080), null, false);

    private static HudModel State(string state) => state switch
    {
        "cockpit" => Model(flightReference: Reference(0f, 0f, 45f),
            orderArrows: new(new(0.4f, 0f), new(-0.4f, 0f), false)),
        "chase" => Model(camera: CameraMode.Chase, flightReference: Reference(6f, 3f, 90f)),
        "command" => Model(camera: CameraMode.Command, flightReference: Reference(0f, 0f, 45f)),
        "docked" => Model(flightReference: Reference(0f, 0f, 45f), docked: true),
        "lifepod" => Model(flightReference: Reference(0f, 0f, 45f), speed: 60f, throttle: 0f),
        "dead" => Model(flightReference: Reference(0f, 0f, 45f), dead: true),
        "ghost" => Model(flightReference: Reference(0f, 0f, 45f), ghost: true),
        "turret" => Model(camera: CameraMode.ExternalTurret, flightReference: Reference(-20f, 0f, 45f),
            speed: 0f, throttle: -1f, turret: true),
        "training" => Model(flightReference: Reference(0f, 0f, 45f), trainingOverlayId: 3),
        "hidden" => Model(flightReference: Reference(0f, 0f, 45f), hidden: true),
        "no-telemetry" => HudModel.Create(null, new HudContext { FlightReference = Reference(0f, 0f, 45f) }),
        "no-reference" => Model(),
        "target" => TargetModel("Enemy fighter", .6f, .3f, enemy: true, lockState: null),
        "locked" => TargetModel("Enemy fighter", .6f, .3f, enemy: true,
            lockState: new HudLock(new(1160f, 362f), MissileLockFeedback.LockState.Partial, 1.0, .62f)),
        "lock-full" => TargetModel("Enemy fighter", .6f, .3f, enemy: true,
            lockState: new HudLock(new(1160f, 362f), MissileLockFeedback.LockState.Full, 1.0, 1f)),
        "lead" => Model(flightReference: Reference(0f, 0f, 45f),
            lead: new(new(1000f, 450f), new(1160f, 362f), true, .8f, 1.0, Granted: true, Visible: true)),
        "lead-out" => Model(flightReference: Reference(0f, 0f, 45f),
            lead: new(new(1000f, 450f), new(1160f, 362f), false, .3f, 1.0, Granted: true, Visible: true)),
        "orders" => Model(flightReference: Reference(0f, 0f, 45f),
            orderArrows: new(new(1f, 0f), new(0f, 1f), false)),
        "offscreen" => Model(flightReference: Reference(0f, 0f, 45f),
            targetPane: Pane("Far hull", 9000f, -25f, "Rixian"), bearing: new HudPoint(.8f, -.2f)),
        "friendly" => TargetModel("Friendly pilot", .5f, .4f, enemy: false, lockState: null),
        "asteroid-ore" => Model(flightReference: Reference(0f, 0f, 45f),
            target: new HudTarget(new(1160f, 362f), false, "asteroid 200", .45f, null, null, .42f, 600f, -12f),
            targetPane: new HudTargetPane(Present: true, Name: "asteroid 200", TypeDescription: "asteroid",
                SideName: null, SideColor: TargetPaneModel.Neutral, HullFraction: .45f, ShieldFraction: null,
                Distance: 600f, Speed: 0f, OreFraction: .42f, KillBonusPercent: null, InRange: false, IsShip: false,
                ClosingSpeed: -12f)),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown state"),
    };

    private static HudModel TargetModel(string name, float hull, float shield, bool enemy, HudLock? lockState)
        => Model(flightReference: Reference(0f, 0f, 45f),
            target: new HudTarget(new(1160f, 362f), true, name, hull, shield, enemy, null, 1250f, 43f,
                new HudColor(1f, 0f, 0f), 60f),
            targetPane: Pane(name, 1250f, 43f, "Rixian"),
            lockState: lockState);

    private static HudTargetPane Pane(string name, float distance, float closing, string side)
        => new(Present: true, Name: name, TypeDescription: "Fighter", SideName: side,
            SideColor: TargetPaneModel.Neutral, HullFraction: .6f, ShieldFraction: .3f, Distance: distance,
            Speed: 43f, OreFraction: null, KillBonusPercent: 15f, InRange: true, IsShip: true,
            TimeToIntercept: "04.1 s", ClosingSpeed: closing);

    /// <summary>The sim attitude used by the flight scenes (the same fixture shape the
    /// P1036 suite uses).</summary>
    private static HudFlightReference Reference(float pitchDegrees, float rollDegrees, float headingDegrees)
    {
        float heading = headingDegrees * MathF.PI / 180f;
        float pitch = pitchDegrees * MathF.PI / 180f;
        float roll = rollDegrees * MathF.PI / 180f;
        var forward = new SimVec(MathF.Cos(heading) * MathF.Cos(pitch), MathF.Sin(heading) * MathF.Cos(pitch), MathF.Sin(pitch));
        var levelUp = new SimVec(-MathF.Cos(heading) * MathF.Sin(pitch), -MathF.Sin(heading) * MathF.Sin(pitch), MathF.Cos(pitch));
        var orientation = new Orientation();
        orientation.Set(forward, levelUp);
        if (roll != 0f) orientation.PostRotate(forward, roll);
        return HudFlightReference.FromSim(orientation, forward * 260f, HudFlightReference.SectorUp);
    }

    private static ClientWorld World()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = Pose(1, (0f, 0f, 0f), (120f, 0f, 0f)) });
        return w;
    }

    private static ServerShipUpdate Pose(short id, (float x, float y, float z) pos, (float x, float y, float z) vel,
        float hull = 1f, float shield = 1f) =>
        ServerShipUpdate.FromValues(id, 0, new ControlData(0f, 0f, 0f, 1f), 1u,
            new SimVec(pos.x, pos.y, pos.z), Orientation.Identity, new SimVec(vel.x, vel.y, vel.z),
            [0f, 0f, 0f], hull, shield, 1f, 1f, 1f, 0f, 1f);

    private static void AddShip(ClientWorld w, short id, short sector, (float x, float y, float z) pos,
        (float x, float y, float z) vel, float hull, float shield, short side = 1)
    {
        w.Apply(new SingleShipUpdate { OidTarget = id, Ship = Pose(id, pos, vel, hull, shield) });
        w.Apply(new ShipStatusMessage { ShipId = id, SectorId = sector, StationId = -1 });
        w.Apply(new ExportShip { ShipId = id, SideId = side, CharacterName = $"ship {id}", HullId = 501 });
    }

    private static JetHudRenderer Jet(IJetVectorSurface surface, HudSkinResources? resources = null)
        => new(surface, new HudSettings { Skin = HudSkinIds.Jet },
            resources ?? new Counting().Resources, HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!);

    private static bool Overlaps(RectangleF box, TextRun run)
        => run.X < box.X + box.Width - 1f && box.X < run.X + run.Width - 1f
        && run.Y < box.Y + box.Height - 1f && box.Y < run.Y + run.Height - 1f;

    private static bool Overlaps(RectangleF a, RectangleF b)
        => a.X < b.X + b.Width - 1f && b.X < a.X + a.Width - 1f
        && a.Y < b.Y + b.Height - 1f && b.Y < a.Y + a.Height - 1f;

    private static bool Contains(RectangleF outer, TextRun inner)
        => inner.X >= outer.X - 1.5f && inner.X + inner.Width <= outer.X + outer.Width + 1.5f
        && inner.Y >= outer.Y - 1.5f && inner.Y + inner.Height <= outer.Y + outer.Height + 1.5f;

    /// <summary>True when the segment crosses a plate border (it starts outside and
    /// ends inside, or vice versa) — the readout's own tick lives fully inside.</summary>
    private static bool CrossesBorder(RectangleF plate, Vector2 a, Vector2 b)
    {
        static bool Inside(RectangleF box, Vector2 p)
            => p.X >= box.X - 0.5f && p.X <= box.X + box.Width + 0.5f
            && p.Y >= box.Y - 0.5f && p.Y <= box.Y + box.Height + 0.5f;
        bool aIn = Inside(plate, a), bIn = Inside(plate, b);
        if (aIn == bIn) return false;
        // A segment with one end inside the plate crosses its border.
        return true;
    }

    public readonly record struct TextRun(string Text, float X, float Y, float Width, float Height);

    /// <summary>Records every submission (delegating nothing — the metrics are a fixed
    /// 6×12 grid so the geometry tests are deterministic and device-free).</summary>
    private sealed class Recorder : IJetVectorSurface, IJetCueAudit
    {
        private readonly List<(Vector2 A, Vector2 B, Color Color, float Width)> _segments = new();
        private readonly List<(Vector2 Min, Vector2 Size, Color Color)> _fills = new();
        private readonly List<TextRun> _runs = new();
        private readonly List<(Vector2 Center, float Radius, float Start, float Sweep)> _cues = new();
        private readonly List<RectangleF> _cueReadouts = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public int Frames { get; private set; }
        public IReadOnlyList<TextRun> TextRuns => _runs;
        public IEnumerable<Color> SegmentColors => _segments.Select(s => s.Color);
        public IReadOnlySet<string> Seen => _seen;
        /// <summary>P1038c — the lock-cue arcs the renderer reported.</summary>
        public IReadOnlyList<(Vector2 Center, float Radius, float Start, float Sweep)> CueArcs => _cues;
        /// <summary>P1038c — the lock readout blocks the renderer reported.</summary>
        public IReadOnlyList<RectangleF> CueReadouts => _cueReadouts;

        public int count(string text) => _counts.TryGetValue(text, out int n) ? n : 0;

        public int LineCount => _segments.Count;
        public int Texts => _runs.Count;

        public void Begin() => Frames++;
        public void End() { }

        public void Line(Vector2 a, Vector2 b, float width, Color color) => _segments.Add((a, b, color, width));

        public void Fill(Vector2 min, Vector2 size, Color color) => _fills.Add((min, size, color));

        /// <summary>P1038c — the cue arcs and readout block the renderer reports.</summary>
        public void CueArc(Vector2 center, float radius, float start, float sweep)
            => _cues.Add((center, radius, start, sweep));

        public void CueReadout(RectangleF box) => _cueReadouts.Add(box);

        public void Text(string text, Vector2 position, bool large, float scale, Color color)
        {
            Vector2 size = Measure(text, large, scale);
            _counts[text] = count(text) + 1;
            _seen.Add(text);
            for (int i = _runs.Count - 1; i >= 0; i--)
            {
                TextRun run = _runs[i];
                if (run.Text == text && MathF.Abs(run.X - position.X) <= 2.5f && MathF.Abs(run.Y - position.Y) <= 2.5f)
                {
                    _runs[i] = new TextRun(text, position.X, position.Y, size.X, size.Y);   // keep the ink run
                    return;
                }
            }
            _runs.Add(new TextRun(text, position.X, position.Y, size.X, size.Y));
        }

        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 8f : 6f) * scale, (large ? 16f : 12f) * scale);

        public IEnumerable<(Vector2 A, Vector2 B)> Segments() => _segments.Select(s => (s.A, s.B));

        /// <summary>Segments whose two endpoints both lie within <paramref name="radius"/>
        /// of <paramref name="center"/> (a marker drawn at a point).</summary>
        public int linesNear(Vector2 center, float radius)
        {
            int n = 0;
            foreach (var (a, b) in Segments())
                if (Vector2.Distance(a, center) <= radius && Vector2.Distance(b, center) <= radius) n++;
            return n;
        }

        /// <summary>Segments that follow a circle of <paramref name="radius"/> at
        /// <paramref name="center"/> (the lead ring).</summary>
        public int circleSegments(Vector2 center, float radius)
        {
            int n = 0;
            foreach (var (a, b) in Segments())
            {
                float da = Vector2.Distance(a, center), db = Vector2.Distance(b, center);
                if (MathF.Abs(da - radius) <= 2f && MathF.Abs(db - radius) <= 2f) n++;
            }
            return n;
        }

        /// <summary>Axis-aligned corner-bracket strokes at ±<paramref name="half"/> from
        /// <paramref name="center"/> (the aim-predictor signature).</summary>
        public int cornerBracketSegments(Vector2 center, float half)
        {
            // Corner-bracket arms sit at x = ±half (vertical) or y = ±half (horizontal)
            // and span at most ~half outward from the corner, so the other coordinate
            // must stay inside the bracket box — that excludes a marker circle/wing
            // stroke that merely happens to lie on the same row.
            float arm = half * 0.6f + 2f;
            int n = 0;
            foreach (var (a, b) in Segments())
            {
                bool vertical = MathF.Abs(a.X - b.X) < 0.01f && MathF.Abs(MathF.Abs(a.X - center.X) - half) <= 2f
                    && MathF.Max(MathF.Abs(a.Y - center.Y), MathF.Abs(b.Y - center.Y)) <= half + arm;
                bool horizontal = MathF.Abs(a.Y - b.Y) < 0.01f && MathF.Abs(MathF.Abs(a.Y - center.Y) - half) <= 2f
                    && MathF.Max(MathF.Abs(a.X - center.X), MathF.Abs(b.X - center.X)) <= half + arm;
                if (vertical || horizontal) n++;
            }
            return n;
        }

        public RectangleF Caption(string text)
        {
            foreach (TextRun run in _runs)
                if (run.Text == text) return new RectangleF(run.X, run.Y, run.Width, run.Height);
            return new RectangleF(-1f, -1f, 0f, 0f);
        }

        /// <summary>Every readout plate reconstructed from its five fills (the plate plus
        /// its four border strips); thin fraction bars are not plates.</summary>
        public IEnumerable<RectangleF> Plates()
        {
            for (int i = 0; i + 4 < _fills.Count; i++)
            {
                // FillPlate submits plate then top, bottom, left, right borders.
                var plate = _fills[i];
                var top = _fills[i + 1];
                var bottom = _fills[i + 2];
                bool sameWidth = MathF.Abs(top.Size.X - plate.Size.X) < 0.01f
                    && MathF.Abs(bottom.Size.X - plate.Size.X) < 0.01f;
                bool sameStart = MathF.Abs(top.Min.X - plate.Min.X) < 0.01f
                    && MathF.Abs(bottom.Min.X - plate.Min.X) < 0.01f;
                if (!sameWidth || !sameStart || plate.Size.Y < 8f) continue;
                yield return new RectangleF(plate.Min.X, plate.Min.Y, plate.Size.X, plate.Size.Y);
            }
        }

        /// <summary>The readout plate centred nearest <paramref name="x"/>.</summary>
        public RectangleF PlateAt(float x)
        {
            RectangleF best = default;
            float distance = float.MaxValue;
            foreach (RectangleF plate in Plates())
            {
                float d = MathF.Abs(plate.X + plate.Width * 0.5f - x);
                if (d < distance) { distance = d; best = plate; }
            }
            return best;
        }

        public RectangleF LargestPlate()
        {
            RectangleF best = default;
            float area = 0f;
            foreach (RectangleF plate in Plates())
                if (plate.Width * plate.Height > area) { area = plate.Width * plate.Height; best = plate; }
            return best;
        }
    }

    /// <summary>A surface that only counts submissions (no lists, no strings), for the
    /// allocation gate.</summary>
    private sealed class Counter : IJetVectorSurface
    {
        public int Lines, Fills, Texts;
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) => Lines++;
        public void Fill(Vector2 min, Vector2 size, Color color) => Fills++;
        public void Text(string text, Vector2 position, bool large, float scale, Color color) => Texts++;
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 8f : 6f) * scale, (large ? 16f : 12f) * scale);
    }

    private sealed class Stub(string id) : IHudSkinRenderer
    {
        public string Id { get; } = id;
        public IReadOnlySet<string> Coverage { get; } = new HashSet<string>(StringComparer.Ordinal);
        public void Draw(HudModel model, in HudDrawFrame frame) { }
        public void DrawCommand(HudModel model) { }
        public void DrawConsole(HudModel model) { }
        public void SetRadarIcons(RadarIconTextures? icons) { }
        public Rectangle DrawOverlayText(string text, Vector2 position, Color color) => default;
        public void Dispose() { }
    }

    /// <summary>The shared-pass counters (HudSkinResources is a record of callbacks).
    /// <see cref="Feedback"/> would be any extra feedback/audio dispatch: Jet has no
    /// such surface, so it must stay 0 (P1037 lock-tone rule).</summary>
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
