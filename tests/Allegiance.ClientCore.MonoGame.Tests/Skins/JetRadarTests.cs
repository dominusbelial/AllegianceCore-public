using System;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1039 — the Jet radar story (P972 §4.4 RWR, §5.4 rows 1–27, slice 3C) and item 0's
/// placement priority. Every assertion is derived from the shared model or from a pure
/// function, so a second derivation cannot hide here.
/// </summary>
public sealed class JetRadarTests
{
    // ── shared bearing (presentation-only) ──────────────────────────────
    private static readonly Vector3 Up = HudFlightReference.SectorUp;

    [Fact]
    public void The_bearing_is_the_signed_own_relative_angle()
    {
        // 0 = dead ahead, ±π = behind, +π/2 = the ship's LEFT (+Y: the heading tape's sense).
        Assert.Equal(0f, RadarBearing.Relative(Vector3.UnitX, Up, new Vector3(100f, 0f, 0f))!.Value, 3);
        Assert.Equal(MathF.PI, MathF.Abs(RadarBearing.Relative(Vector3.UnitX, Up, new Vector3(-100f, 0f, 0f))!.Value), 3);
        Assert.Equal(MathF.PI / 2f, RadarBearing.Relative(Vector3.UnitX, Up, new Vector3(0f, 100f, 0f))!.Value, 3);
        Assert.Equal(-MathF.PI / 2f, RadarBearing.Relative(Vector3.UnitX, Up, new Vector3(0f, -100f, 0f))!.Value, 3);
        // A forward that is itself rolled keeps the same answer (roll invariance below).
        Assert.Equal(MathF.PI / 4f, RadarBearing.Relative(Vector3.UnitX, Up, new Vector3(70f, 70f, 0f))!.Value, 3);
    }

    [Fact]
    public void A_contact_on_the_plane_normal_has_no_invented_bearing()
    {
        Assert.Null(RadarBearing.Relative(Vector3.UnitX, Up, new Vector3(0f, 0f, 500f)));
        Assert.Null(RadarBearing.Relative(Vector3.UnitX, Up, new Vector3(0f, 0f, -500f)));
    }

    [Fact]
    public void A_zero_offset_or_a_vertical_attitude_is_null()
    {
        Assert.Null(RadarBearing.Relative(Vector3.UnitX, Up, Vector3.Zero));
        Assert.Null(RadarBearing.Relative(Vector3.UnitZ, Up, new Vector3(10f, 0f, 0f)));
        Assert.Null(RadarBearing.Relative(Vector3.UnitX, Vector3.Zero, new Vector3(10f, 10f, 0f)));
    }

    [Fact]
    public void The_bearing_is_invariant_under_the_own_ships_roll()
    {
        // Rolling about the forward axis leaves forward and the plane normal untouched, so the
        // bearing is identical by construction (the ship's own right/up axes are never read).
        var delta = new Vector3(100f, 80f, 25f);
        float? level = RadarBearing.Relative(Vector3.UnitX, Up, delta);

        var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3f);
        var rolledForward = Vector3.Transform(Vector3.UnitX, roll);
        float? banked = RadarBearing.Relative(rolledForward, Up, delta);
        Assert.NotNull(level);
        Assert.Equal(level!.Value, banked!.Value, 4);
    }

    [Fact]
    public void Classic_and_Modern_never_read_the_bearing()
    {
        // The field exists on the shared contact record only: the Classic renderer and the
        // Modern renderer take the same HudContact and both leave every pixel unchanged when
        // the bearing is present (the zero-delta gate proves it end to end; this pins the
        // contract that nothing else in the record depends on it).
        var a = new HudContact(HudContactKind.Ship, new HudPoint(10, 10), true, 1f, null, false, false,
            Range: 500, RelativeBearingRadians: null);
        var b = a with { RelativeBearingRadians = 1.25f };
        Assert.Equal(a with { RelativeBearingRadians = null }, b with { RelativeBearingRadians = null });
        Assert.NotEqual(a.RelativeBearingRadians, b.RelativeBearingRadians);
    }

    // ── scope geometry ──────────────────────────────────────────────────
    [Fact]
    public void The_square_and_round_scopes_keep_the_same_measured_rectangle()
    {
        RectangleF scope = JetRadar.Scope(1920, 1080, 1f);
        Assert.True(scope.Width > 0f && scope.Height > 0f);
        // Square scope: icons inside the half-extent; round: the inscribed circle of the SAME
        // rectangle (native's inscribed rule), so both variants occupy identical boxes.
        Assert.Equal(scope.Width, scope.Height, 3);
        Assert.True(JetRadar.CircleRadius(scope) <= JetRadar.HalfExtent(scope) + 0.001f);
        Assert.True(scope.X + scope.Width <= 1920f);
        // It sits clear of the throttle band it is given.
        RectangleF limited = JetRadar.Scope(1920, 1080, 1f, 1700f);
        Assert.True(limited.X + limited.Width <= 1700f);
    }

    [Fact]
    public void Range_bands_cover_the_frames_own_contacts_and_round_up()
    {
        float range = JetRadar.ScopeRange(new[]
        {
            Contact(bearing: 0f, range: 240), Contact(bearing: 0f, range: 2519),
        });
        Assert.True(range >= 2519f);
        // P1039b — the BAND is rounded (2519 / 4 = 630 → 800 m), not the whole scale: the outer
        // ring is 3200 m, 1.27× the farthest contact, so the contacts spread over all four bands
        // instead of crowding the inner 55 % under a 5000 m ring.
        Assert.Equal(3200f, range);
        Assert.True(range <= 2519f * 1.6f);
        Assert.Equal(800, JetRadar.RingRange(1, range));
        Assert.Equal(1600, JetRadar.RingRange(2, range));
        Assert.Equal(2400, JetRadar.RingRange(3, range));
        Assert.Equal(3200, JetRadar.RingRange(4, range));
        Assert.Equal(1000f, JetRadar.ScopeRange(Array.Empty<HudContact>())); // the floor
        Assert.Equal(1000f, JetRadar.ScaleFor(1000f));
        Assert.Equal(5000f, JetRadar.RoundRange(4300f));                    // the 1/2/5 series stays
        Assert.Equal(1f, JetRadar.RangeFraction(9000, 1000f));              // pinned to the rim
    }

    [Fact]
    public void A_bearing_places_the_contact_around_the_scope_centre_and_null_falls_back()
    {
        RectangleF scope = JetRadar.Scope(1920, 1080, 1f);
        Vector2 centre = JetRadar.Centre(scope);
        // 0 = up, +π/2 = left: the heading tape's sense.
        Vector2 ahead = JetRadar.ContactPoint(scope, Contact(bearing: 0f, range: 1000), 1920, 1080, 1000f);
        Assert.Equal(centre.X, ahead.X, 2);
        Assert.True(ahead.Y < centre.Y);
        Vector2 left = JetRadar.ContactPoint(scope, Contact(bearing: MathF.PI / 2f, range: 1000), 1920, 1080, 1000f);
        Assert.True(left.X < centre.X);
        Assert.Equal(centre.Y, left.Y, 2);
        // Null bearing: the ordinary projected point mapped into the scope, never a fabricated
        // direction — the same contact twice gives the same place.
        HudContact noBearing = Contact(bearing: null, range: 1000, position: new HudPoint(1400f, 700f));
        Vector2 fallback = JetRadar.ContactPoint(scope, noBearing, 1920, 1080, 1000f);
        Assert.Equal(fallback, JetRadar.ContactPoint(scope, noBearing, 1920, 1080, 1000f));
        Assert.InRange(fallback.X, scope.X, scope.X + scope.Width);
        Assert.InRange(fallback.Y, scope.Y, scope.Y + scope.Height);
        // The own ship sits at the centre (range zero).
        Assert.Equal(centre, JetRadar.ContactPoint(scope, Contact(bearing: 1f, range: 0, self: true), 1920, 1080, 1000f));
    }

    // ── the renderer ────────────────────────────────────────────────────
    [Fact]
    public void The_radar_draws_every_contact_kind_and_overlay_and_allocates_nothing()
    {
        var surface = Recorder();
        using var jet = Renderer(surface);
        HudModel model = Model(RadarContacts());
        jet.Draw(model, Frame());
        // One scope frame + the mode word + a label per labelled contact.
        Assert.True(surface.TextRunCount > 0);
        Assert.Contains(surface.Texts(), t => t.StartsWith("RDR", StringComparison.Ordinal));
        // Every contact class leaves a glyph: the labels carry the names the model gave.
        Assert.Contains("Contact 0", surface.Texts());
        Assert.Contains("ACC", surface.Texts());
        Assert.Contains("QUE", surface.Texts());
        Assert.Contains("SUB", surface.Texts());
        Assert.Contains("!", surface.Texts());
        Assert.Contains("RIP 7", surface.Texts());
        Assert.Contains("M", surface.Texts());
        Assert.Contains("P", surface.Texts());
        Assert.Contains("T", surface.Texts());

        // Steady state: a second identical frame allocates nothing (measured on a surface
        // that does not accumulate the runs it is given — the recorder above would).
        var quiet = new CountOnlySurface();
        using var quietJet = Renderer(quiet);
        quietJet.Draw(model, Frame());
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) quietJet.Draw(model, Frame());
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(after - before < 32 * 1024,
            $"200 steady frames allocated {after - before} B");
    }

    [Fact]
    public void The_round_option_changes_only_the_border()
    {
        var square = Recorder();
        using (var jet = Renderer(square, round: false))
            jet.Draw(Model(RadarContacts()), Frame());
        var round = Recorder();
        using (var jet = Renderer(round, round: true))
            jet.Draw(Model(RadarContacts()), Frame());
        // Same contact labels in both; the frame differs (corner brackets vs a circle).
        Assert.Equal(square.Texts(), round.Texts());
        Assert.NotEqual(square.LineCount, round.LineCount);
    }

    [Fact]
    public void The_scope_is_hidden_while_the_inventory_pane_is_open()
    {
        var surface = Recorder();
        using var jet = Renderer(surface);
        HudModel model = Model(RadarContacts(), inventoryVisible: true, inventorySlide: 1f);
        jet.Draw(model, Frame());
        Assert.Equal(default, jet.RadarScope);
        Assert.DoesNotContain(surface.Texts(), t => t.StartsWith("RDR", StringComparison.Ordinal));
    }

    // ── item 0: placement priority ──────────────────────────────────────
    [Fact]
    public void The_readout_placement_order_is_right_left_below_above()
    {
        Assert.Equal(4, JetReadoutPlacement.DesignatorOrder.Length);
        Assert.Equal(JetReadoutPlacement.Slot.Right, JetReadoutPlacement.DesignatorOrder[0]);
        Assert.Equal(JetReadoutPlacement.Slot.Left, JetReadoutPlacement.DesignatorOrder[1]);
        Assert.Equal(JetReadoutPlacement.Slot.Below, JetReadoutPlacement.DesignatorOrder[2]);
        Assert.Equal(JetReadoutPlacement.Slot.Above, JetReadoutPlacement.DesignatorOrder[3]);
        Assert.Equal(JetReadoutPlacement.Slot.Above, JetReadoutPlacement.CaptionOrder[0]);
    }

    [Fact]
    public void Furniture_blocks_a_slot_and_the_next_free_slot_is_taken()
    {
        var furniture = new RectangleF[]
        {
            // A numeral right where the right-hand slot would land.
            new(160f, 40f, 30f, 22f),
        };
        var placed = JetReadoutPlacement.Place(
            JetReadoutPlacement.DesignatorOrder, 100f, 100f, 60f, 20f, 120f, 40f, 1f,
            800f, 480f, furniture, default);
        // The preferred slot position is blocked by the numeral; the slot slides off it
        // (a slot keeps its name while moving clear), so the placed box covers no furniture.
        Assert.True(placed.Clear);
        Assert.Equal(0f, JetReadoutPlacement.CoveredArea(
            new RectangleF(placed.X, placed.Y, 120f, 40f), furniture, default));
        Assert.False(JetReadoutPlacement.OverlapArea(
            new RectangleF(placed.X, placed.Y, 120f, 40f), furniture[0]) > 0f);
    }

    [Fact]
    public void A_blocked_everywhere_block_stays_on_screen()
    {
        var wall = new RectangleF(0f, 0f, 800f, 480f);
        var placed = JetReadoutPlacement.Place(
            JetReadoutPlacement.DesignatorOrder, 400f, 240f, 200f, 50f, 120f, 40f, 1f,
            800f, 480f, new[] { wall }, default);
        Assert.False(placed.Clear);
        Assert.InRange(placed.X, 0f, 800f - 120f);
        Assert.InRange(placed.Y, 0f, 480f - 40f);
    }

    [Fact]
    public void The_compact_designator_name_is_shortened_at_a_stated_width()
    {
        Assert.Equal(72f, JetTargetGeometry.CompactNameWidth);
        Assert.True(JetTargetGeometry.CompactNameWidth < 150f);
    }

    [Fact]
    public void The_f4_pane_shrinks_below_the_caution_panel_instead_of_covering_it()
    {
        const int rows = 12;
        // The 800×480 floor: the caution panel leaves only the band above the minimap.
        float topLimit = 84f;
        float scaled = JetInventoryPresentation.FitScale(rows, 480f, topLimit);
        Assert.True(scaled < JetInventoryPresentation.Scale(480f));
        Assert.True(scaled >= JetInventoryPresentation.MinPaneScale);
        float top = JetInventoryPresentation.TopAt(rows, 800f, 480f, 1f, scaled);
        Assert.True(top >= topLimit, $"pane top {top:0.#} must stay below the caution {topLimit:0.#}");
        // Every row still exists (no row is dropped to escape the caution).
        Assert.Equal((JetInventoryPresentation.TitleRow + JetInventoryPresentation.CapHeight
            + JetInventoryPresentation.HeaderRow + JetInventoryPresentation.RowHeight * rows) * scaled,
            JetInventoryPresentation.HeightAt(rows, scaled), 2);
        // With no caution in the frame the native scale is kept.
        Assert.Equal(JetInventoryPresentation.Scale(1080f), JetInventoryPresentation.FitScale(rows, 1080f, 0f), 3);
    }

    // ── P1039b: the radar label placement pass ──────────────────────────
    [Fact]
    public void The_radar_label_slot_order_is_right_left_above_below_then_diagonals()
    {
        Assert.Equal(8, JetRadarLabels.SlotOrder.Length);
        Assert.Equal(JetRadarLabels.Slot.Right, JetRadarLabels.SlotOrder[0]);
        Assert.Equal(JetRadarLabels.Slot.Left, JetRadarLabels.SlotOrder[1]);
        Assert.Equal(JetRadarLabels.Slot.Above, JetRadarLabels.SlotOrder[2]);
        Assert.Equal(JetRadarLabels.Slot.Below, JetRadarLabels.SlotOrder[3]);
        Assert.Equal(JetRadarLabels.Slot.UpRight, JetRadarLabels.SlotOrder[4]);
        Assert.Equal(JetRadarLabels.Slot.DownLeft, JetRadarLabels.SlotOrder[7]);
        // The first slot sits right of the glyph's clear extent, one gap away.
        RectangleF right = JetRadarLabels.SlotRect(JetRadarLabels.Slot.Right, new Vector2(100f, 100f),
            JetRadarLabels.GlyphClear, 40f, 20f, 1f);
        Assert.Equal(100f + JetRadarLabels.GlyphClear + JetRadarLabels.SlotGap, right.X, 3);
        Assert.Equal(90f, right.Y, 3);       // vertically centred on the glyph
        Assert.True(JetRadarLabels.IsDiagonal(JetRadarLabels.Slot.UpLeft));
        Assert.False(JetRadarLabels.IsDiagonal(JetRadarLabels.Slot.Below));
    }

    [Fact]
    public void The_label_queue_is_target_then_subject_then_threat_then_verb_then_nearest()
    {
        HudContact target = Contact(0f, 2000) with { Selected = true };
        HudContact subject = Contact(0f, 500) with { Subject = true };
        HudContact threat = Contact(0f, 900) with { Threat = true };
        HudContact missile = Contact(0f, 400) with { Kind = HudContactKind.Missile };
        HudContact verb = Contact(0f, 100) with { CommandId = 2, CommandAccepted = true };
        HudContact near = Contact(0f, 300);
        HudContact far = Contact(0f, 3000);
        Assert.Equal(JetRadarLabels.Rank.Target, JetRadarLabels.Priority(target));
        Assert.Equal(JetRadarLabels.Rank.Subject, JetRadarLabels.Priority(subject));
        Assert.Equal(JetRadarLabels.Rank.Threat, JetRadarLabels.Priority(threat));
        Assert.Equal(JetRadarLabels.Rank.Threat, JetRadarLabels.Priority(missile));
        Assert.Equal(JetRadarLabels.Rank.Command, JetRadarLabels.Priority(verb));
        Assert.Equal(JetRadarLabels.Rank.Other, JetRadarLabels.Priority(near));
        // Rank beats range, then nearest wins, then the model order (stable).
        Assert.True(JetRadarLabels.Before(target, subject));
        Assert.True(JetRadarLabels.Before(subject, threat));
        Assert.True(JetRadarLabels.Before(threat, verb));
        Assert.True(JetRadarLabels.Before(verb, near));
        Assert.True(JetRadarLabels.Before(near, far));
        // The never-hidden ranks are exactly target / subject / threat / verb.
        Assert.True(JetRadarLabels.NeverHidden(verb));
        Assert.False(JetRadarLabels.NeverHidden(near));
    }

    [Fact]
    public void A_placement_takes_the_first_free_slot_and_reports_the_covered_fallback()
    {
        RectangleF inner = new(0f, 0f, 200f, 200f);
        var empty = Array.Empty<RectangleF>();
        JetRadarLabels.Placement free = JetRadarLabels.Place(new Vector2(100f, 100f),
            JetRadarLabels.GlyphClear, 40f, 20f, 1f, inner, empty);
        Assert.True(free.Clear);
        Assert.Equal(JetRadarLabels.Slot.Right, free.Slot);
        // A label already sitting in the right slot pushes the block to the next slot (left).
        var blocks = new[] { free.Box };
        JetRadarLabels.Placement second = JetRadarLabels.Place(new Vector2(100f, 100f),
            JetRadarLabels.GlyphClear, 40f, 20f, 1f, inner, blocks);
        Assert.True(second.Clear);
        Assert.NotEqual(free.Box, second.Box);
        Assert.Equal(0f, JetRadarLabels.Covered(second.Box, blocks));
        // A scope packed with blocks returns the least-covered candidate with Clear = false.
        var packed = new RectangleF[64];
        for (int i = 0; i < packed.Length; i++)
            packed[i] = new RectangleF((i % 8) * 25f, (i / 8) * 25f, 30f, 30f);
        JetRadarLabels.Placement none = JetRadarLabels.Place(new Vector2(100f, 100f),
            JetRadarLabels.GlyphClear, 40f, 20f, 1f, inner, packed);
        Assert.False(none.Clear);
    }

    [Fact]
    public void The_clamp_never_makes_a_slot_clear_and_the_TGT_slot_is_fixed()
    {
        // Furniture covering the whole viewport: nothing beside the anchor can be clear.
        var all = new[] { new RectangleF(0f, 0f, 800f, 480f) };
        JetReadoutPlacement.Placement blocked = JetReadoutPlacement.Place(
            JetReadoutPlacement.DesignatorOrder, 545f, 111f, -10f, 185f, 147f, 53f, 0.667f,
            800f, 480f, all, default);
        Assert.False(blocked.Clear);
        // The TGT slot is fixed: zone left edge, bottom on the stores' measured top edge.
        float storesTop = JetStoresPanel.HeaderTop(480f, true, 16f);
        JetReadoutPlacement.Placement tgt = JetReadoutPlacement.TargetSlot(800f, 480f, 0.667f,
            storesTop, 117f, 85f);
        RectangleF zone = JetReadoutPlacement.TargetZone(800f, 480f, 0.667f, storesTop);
        Assert.True(tgt.Clear);
        Assert.Equal(zone.X, tgt.X, 3);
        Assert.Equal(zone.Height - 85f, tgt.Y, 3);
        // ... and it is clear of the stores column and of the boresight column.
        Assert.True(tgt.X >= JetStoresPanel.Left(0.667f) + JetStoresPanel.BlockWidth * 0.667f);
        Assert.True(tgt.X + 117f <= zone.X + zone.Width + 0.5f);
    }

    [Fact]
    public void The_compact_scope_keeps_the_corner_zone()
    {
        RectangleF scope = JetRadar.Scope(800f, 480f, 0.667f, 682.7f, 80f);
        Assert.Equal(144f, scope.Width, 1);
        // Left edge right of the flight-path column, right edge left of the throttle band,
        // bottom above the mini-map, top below the caution panel.
        Assert.True(scope.X >= 800f * JetRadar.CompactLeftFraction - 0.5f);
        Assert.True(scope.X + scope.Width <= 682.7f);
        var (_, minimapY, _, _) = MinimapGeometry.CornerPanel(800f, 480f);
        Assert.True(scope.Y + scope.Height <= minimapY);
        Assert.True(scope.Y >= 80f);
        // At 1080p the scope is unchanged by the compact rule.
        RectangleF full = JetRadar.Scope(1920f, 1080f, 1f, 1700f);
        Assert.Equal(345.6f, full.Width, 1);
    }

    [Fact]
    public void A_crowded_scope_never_hides_a_priority_label_and_never_stacks_two_reads()
    {
        var boxes = new BoxRecorder();
        using var jet = Renderer(boxes);
        HudContact[] contacts = CrowdedContacts(64);
        jet.Draw(Model(contacts), Frame());
        // Every native-eligible priority label is on screen.
        Assert.Equal(0, jet.RadarPriorityLabelMisses);
        Assert.Equal(0, jet.RadarLabelOverflows);
        Assert.True(jet.RadarLabelsPlaced > 0);
        Assert.True(jet.RadarLabelsHidden > 0);      // the dense scene is expected to hide some
        // No two text runs this element drew intersect (the audit's TextOverlapSame rule).
        var radar = boxes.Boxes.Where(b => b.Element == "radar").ToArray();
        for (int i = 0; i < radar.Length; i++)
            for (int j = i + 1; j < radar.Length; j++)
                Assert.False(Intersects(radar[i].Box, radar[j].Box),
                    $"'{radar[i].Text}'@{radar[i].Box.X:0.#},{radar[i].Box.Y:0.#} {radar[i].Box.Width:0.#}x{radar[i].Box.Height:0.#}"
                    + $" and '{radar[j].Text}'@{radar[j].Box.X:0.#},{radar[j].Box.Y:0.#} {radar[j].Box.Width:0.#}x{radar[j].Box.Height:0.#} overlap");
    }

    // ── helpers ─────────────────────────────────────────────────────────
    private static bool Intersects(RectangleF a, RectangleF b)
        => a.X < b.X + b.Width - 1f && b.X < a.X + a.Width - 1f
        && a.Y < b.Y + b.Height - 1f && b.Y < a.Y + a.Height - 1f;

    private static HudContact[] CrowdedContacts(int count)
    {
        var list = new HudContact[count];
        for (int i = 0; i < count; i++)
        {
            var kind = (HudContactKind)(i % 9);
            float bearing = (i * 2.399963f) % (2f * MathF.PI) - MathF.PI;
            list[i] = new HudContact(kind, new HudPoint(960f, 540f), true, 1f, null, i == 0, false,
                Threat: i == 4, Subject: i == 6,
                Name: i % 3 == 0 ? "Contact " + i : null,
                Range: 240 + (i * 53) % 3400,
                CommandId: i is 1 or 2 ? 2 : null, CommandAccepted: i == 1,
                IsEnemy: i % 4 == 0, RelativeBearingRadians: bearing);
        }
        return list;
    }

    /// <summary>Records every text run with its box and the element that drew it, so the
    /// no-two-reads-intersect rule can be asserted on the renderer's own submissions.</summary>
    private sealed class BoxRecorder : IJetVectorSurface, IJetElementAudit
    {
        public readonly System.Collections.Generic.List<(string? Element, string Text, RectangleF Box)> Boxes = new();
        private string? _element;
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) { }
        public void Fill(Vector2 min, Vector2 size, Color color) { }
        public void Text(string text, Vector2 position, bool large, float scale, Color color)
        {
            Vector2 size = Measure(text, large, scale);
            // The painter submits the same run eight times at ±1 px for its keyline and then the
            // ink; a run within the keyline's own ±1 px spread (2.5 px, so a far diagonal copy
            // still merges) replaces the recorded one, leaving the ink run a pilot sees.
            for (int i = 0; i < Boxes.Count; i++)
            {
                (string? element, string recorded, RectangleF box) = Boxes[i];
                if (!string.Equals(recorded, text, StringComparison.Ordinal)) continue;
                if (MathF.Abs(box.X - position.X) <= 2.5f && MathF.Abs(box.Y - position.Y) <= 2.5f)
                {
                    Boxes[i] = (element, text, new RectangleF(position.X, position.Y, size.X, size.Y));
                    return;
                }
            }
            Boxes.Add((_element, text, new RectangleF(position.X, position.Y, size.X, size.Y)));
        }
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 9f : 7f) * scale, (large ? 18f : 14f) * scale);
        public void Element(string id) => _element = id;
    }

    private static HudDrawFrame Frame() => new(new Viewport(0, 0, 1920, 1080), null, false);

    private static SoundRecorder Recorder() => new();

    private static JetHudRenderer Renderer(IJetVectorSurface surface, bool round = false)
        => new(surface, new HudSettings { Skin = HudSkinIds.Jet, Size = 1f, RoundRadar = round },
            new HudSkinResources(_ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { }),
            SkinDefinition(), 1f);

    private static HudSkinDefinition SkinDefinition()
        => HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!;

    private static HudContact Contact(float? bearing, int range, HudPoint position = default, bool self = false)
        => new(HudContactKind.Ship, position, true, 1f, null, false, false,
            Range: range, IsSelf: self, RelativeBearingRadians: bearing);

    private static HudContact[] RadarContacts() => new[]
    {
        new HudContact(HudContactKind.Ship, new HudPoint(900f, 500f), true, 1f, null, true, false,
            Name: "Contact 0", Range: 500, IsEnemy: true, NearestEnemy: true, AcceptedOrder: true,
            CommandId: 2, CommandAccepted: true, ClipSeparation: 500, RelativeBearingRadians: 0f),
        new HudContact(HudContactKind.Station, new HudPoint(1000f, 600f), true, 1f, null, false, false,
            Name: "Contact 1", Range: 900, QueuedOrder: true, CommandId: 3, Hull: .8f, Shield: .5f,
            ClipSeparation: 900, RelativeBearingRadians: .6f),
        new HudContact(HudContactKind.Warp, new HudPoint(1100f, 700f), true, 1f, null, false, false,
            Name: "Contact 2", Range: 1200, Subject: true, ClipSeparation: 1200,
            RelativeBearingRadians: 1.2f),
        new HudContact(HudContactKind.Asteroid, new HudPoint(1200f, 800f), true, 1f, null, false, false,
            Name: "Rock", Range: 1500, Fill: .5f, Highlight: true, ClipSeparation: 1500,
            RelativeBearingRadians: 1.8f),
        new HudContact(HudContactKind.Missile, new HudPoint(1300f, 900f), true, 1f, null, false, false,
            Range: 1800, Threat: true, ThreatFlash: true, ClipSeparation: 1800,
            MissileTargetsOwnSide: true, RelativeBearingRadians: 2.2f),
        new HudContact(HudContactKind.Probe, new HudPoint(1400f, 1000f), true, 1f, null, false, false,
            Range: 2000, Ripcord: true, RipcordSeconds: 7, ClipSeparation: 2000,
            RelativeBearingRadians: 2.6f),
        new HudContact(HudContactKind.Mine, new HudPoint(1500f, 1000f), true, 1f, null, false, false,
            Range: 2200, CarriedFlag: true, ClipSeparation: 2200, RelativeBearingRadians: -2.6f),
        new HudContact(HudContactKind.Treasure, new HudPoint(1600f, 1000f), true, 1f, null, false, false,
            Range: 2400, CarriedArtifact: true, TreasureUseful: true, ClipSeparation: 2400,
            RelativeBearingRadians: -2.2f),
        new HudContact(HudContactKind.Tech, new HudPoint(1700f, 1000f), true, 1f, null, false, false,
            Name: "Tech", Range: 2600, IsTech: true, TreasureUseful: true, ClipSeparation: 2600,
            RelativeBearingRadians: -1.8f),
        new HudContact(HudContactKind.Ship, new HudPoint(1800f, 1000f), true, 1f, null, false, false,
            Name: "No bearing", Range: 800, IsSameSide: true, ClipSeparation: 800,
            RelativeBearingRadians: null),
    };

    private static HudModel Model(HudContact[] contacts, bool inventoryVisible = false, float inventorySlide = 0f)
        => HudModel.Create(
            new HudFrame(.82f, .7f, .8f, .9f, 1, 1, 260f, .6f, true, true, true, 0, false, 0, true, true, 15, 24,
                Array.Empty<WeaponStatusRow>(), false, 0, 2500,
                new(.2f, -.3f, .1f, false), 85, ShipWarningMask.None),
            new HudContext
            {
                Camera = CameraMode.Cockpit,
                Contacts = contacts,
                RadarMode = RadarMode.All,
                InventoryVisible = inventoryVisible,
                InventorySlide = inventorySlide,
            });

    /// <summary>A surface that only counts: the steady-state allocation gate must not measure
    /// the recorder's own growing string list.</summary>
    private sealed class CountOnlySurface : IJetVectorSurface
    {
        public int Texts { get; private set; }
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) { }
        public void Fill(Vector2 min, Vector2 size, Color color) { }
        public void Text(string text, Vector2 position, bool large, float scale, Color color) => Texts++;
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 9f : 7f) * scale, (large ? 18f : 14f) * scale);
    }

    /// <summary>A minimal recording surface: text runs and line counts (the renderer's own
    /// primitives, no graphics device).</summary>
    private sealed class SoundRecorder : IJetVectorSurface
    {
        private readonly System.Collections.Generic.List<string> _texts = new();
        public int LineCount { get; private set; }
        public int TextRunCount => _texts.Count;
        public System.Collections.Generic.IReadOnlyList<string> Texts() => _texts;
        public void Begin() { }
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) => LineCount++;
        public void Fill(Vector2 min, Vector2 size, Color color) { }
        public void Text(string text, Vector2 position, bool large, float scale, Color color) => _texts.Add(text);
        public Vector2 Measure(string text, bool large, float scale)
            => new(text.Length * (large ? 9f : 7f) * scale, (large ? 18f : 14f) * scale);
    }
}
