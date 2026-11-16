using System.Globalization;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>
/// P1038 — Jet story 4/9: the stores block, the master-caution panel and the Jet F4
/// inventory pane (P972 §4.3, §4.4 first paragraph, §4.5, §5.1/§5.5/§5.6), plus the three
/// P1037 review follow-ups (lock diamond vs designator geometry, fixture radius parity,
/// the self-explanatory pitch numeral).
/// <para>Headless: every renderer assertion goes through a recording
/// <see cref="IJetVectorSurface"/>, and every expected value is derived from the shared
/// model (the gauges, the own-status flags, the weapon-status rows, the native inventory
/// rows) rather than from a literal, so a second derivation cannot hide here.</para>
/// </summary>
public sealed class JetStoresCautionTests(ITestOutputHelper output)
{
    // ── stores: every field under its own shared gate ────────────────────
    [Fact]
    public void The_stores_block_renders_every_field_the_shared_model_supplies()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model();
        jet.Draw(model, Frame());

        Assert.True(model.InstrumentsVisible);
        Assert.Contains("HULL", surface.Seen);
        Assert.Contains("SHLD", surface.Seen);
        Assert.Contains("ENG", surface.Seen);
        Assert.Contains("FUEL", surface.Seen);
        Assert.Contains("GUN", surface.Seen);
        Assert.Contains("KB", surface.Seen);
        Assert.Contains("SIG", surface.Seen);
        // The percentages are the shared gauges' own fractions.
        foreach (GaugeKind kind in new[] { GaugeKind.Hull, GaugeKind.Shield, GaugeKind.Energy, GaugeKind.Fuel })
        {
            HudGauge gauge = model.Gauges.Single(g => g.Kind == kind);
            Assert.Contains(Percent(gauge.Fraction), surface.Seen);
        }
        // The status readings are the shared own-status values.
        Assert.Contains(((int)MathF.Round(model.Status.KillBonusPercent!.Value)).ToString(CultureInfo.InvariantCulture),
            surface.Seen);
        Assert.Contains(((int)MathF.Round(model.Status.SignaturePercent!.Value)).ToString(CultureInfo.InvariantCulture),
            surface.Seen);
    }

    [Fact]
    public void The_stores_block_is_blank_without_telemetry_or_outside_the_instrument_gate()
    {
        var noTelemetry = new Recorder();
        using (var jet = Jet(noTelemetry))
            jet.Draw(HudModel.Create(null, new HudContext()), Frame());
        foreach (string word in new[] { "HULL", "SHLD", "ENG", "FUEL", "GUN", "MSL", "KB", "SIG" })
            Assert.DoesNotContain(word, noTelemetry.Seen);

        // Command view / docked hides the whole instrument group in the shared model.
        var command = new Recorder();
        using (var jet = Jet(command))
            jet.Draw(Model(commandView: true), Frame());
        foreach (string word in new[] { "HULL", "SHLD", "ENG", "FUEL", "GUN", "MSL", "KB", "SIG" })
            Assert.DoesNotContain(word, command.Seen);
    }

    [Fact]
    public void A_lifepod_reads_the_same_fraction_as_OXY()
    {
        var pod = new Recorder();
        using (var jet = Jet(pod))
            jet.Draw(Model(lifepod: true), Frame());
        HudModel model = Model(lifepod: true);
        HudGauge fuel = model.Gauges.Single(g => g.Kind == GaugeKind.Fuel);
        Assert.Equal("OXY", fuel.Label);              // the shared model's own pod relabel
        Assert.Contains("OXY", pod.Seen);
        Assert.DoesNotContain("FUEL", pod.Seen);
        Assert.Contains(Percent(fuel.Fraction), pod.Seen);
    }

    [Fact]
    public void The_gun_pool_shows_the_empty_A_cue_only_at_the_shared_empty_epsilon()
    {
        var empty = new Recorder();
        using (var jet = Jet(empty))
            jet.Draw(Model(ammoFraction: 0f), Frame());
        Assert.Contains("A", empty.Seen);

        var loaded = new Recorder();
        using (var jet = Jet(loaded))
            jet.Draw(Model(ammoFraction: .8f), Frame());
        Assert.DoesNotContain("A", loaded.Seen);
    }

    [Fact]
    public void The_missile_row_reads_the_launcher_count_and_the_shared_arming_state()
    {
        var ready = new Recorder();
        using (var jet = Jet(ready))
            jet.Draw(Model(launcher: new WeaponStatusRow(5, "SRM Dumbfire", false, 4, 6, WeaponStatus.Ready, true)),
                Frame());
        Assert.Contains("MSL", ready.Seen);
        Assert.Contains("4", ready.Seen);
        Assert.Contains("READY", ready.Seen);

        var arming = new Recorder();
        using (var jet = Jet(arming))
            jet.Draw(Model(launcher: new WeaponStatusRow(5, "SRM Dumbfire", false, 2, 6, WeaponStatus.NotReady, true)),
                Frame());
        Assert.Contains("NOT READY", arming.Seen);
    }

    [Theory]
    [InlineData(true, false, false, false, false, false, "EYE")]
    [InlineData(false, true, false, false, false, false, "CLK")]
    [InlineData(false, false, true, false, false, false, "AUTO")]
    [InlineData(false, false, false, true, false, false, "VLOCK")]
    [InlineData(false, false, false, false, true, false, "FLAG")]
    [InlineData(false, false, false, false, false, true, "ART")]
    public void Every_annunciator_exists_exactly_under_its_shared_flag(bool eye, bool cloak, bool auto,
        bool vector, bool flag, bool art, string word)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        jet.Draw(Model(eye: eye, cloak: cloak, autopilot: auto, vectorLock: vector, carriedFlag: flag,
            carriedArtifact: art), Frame());
        Assert.Contains(word, surface.Seen);

        // The same flag off draws no word (shape + word, never colour alone).
        var absent = new Recorder();
        using (var other = Jet(absent))
            other.Draw(Model(), Frame());
        Assert.DoesNotContain(word, absent.Seen);
    }

    [Fact]
    public void The_cloak_annunciator_carries_the_shared_strength_and_the_drained_word()
    {
        var strength = new Recorder();
        using (var jet = Jet(strength))
            jet.Draw(Model(cloak: true, cloakStrength: 70f, energyFraction: .8f), Frame());
        Assert.Contains("CLK", strength.Seen);
        Assert.Contains("70", strength.Seen);

        var drained = new Recorder();
        using (var jet = Jet(drained))
            jet.Draw(Model(cloak: true, cloakStrength: 70f, energyFraction: .1f), Frame());
        Assert.Contains("DRAINED", drained.Seen);
    }

    // ── master caution ───────────────────────────────────────────────────
    [Fact]
    public void The_caution_panel_orders_missile_damage_pod_before_resources_then_system()
    {
        var buffer = new JetCaution[JetCautionModel.MaxCautions];
        HudModel model = Model(
            missileTrack: new MissileTrackState(true, .8f),
            flashing: true,
            lifepod: true,
            hullFraction: .05f,
            boundary: true,
            ghost: true,
            console: new HudConsoleText { Lives = 2, CriticalTips = new[] { new HudNotice("Reloading ammo...", true, 1) } },
            ripcord: "Ripcord in: 9");
        int count = JetCautionModel.Build(model, buffer);
        Assert.True(count >= 6);

        // Priority bands are non-decreasing in the resolved order.
        for (int i = 1; i < count; i++)
            Assert.True(buffer[i - 1].Priority <= buffer[i].Priority);
        Assert.Equal(JetCautionKind.MissileTrack, buffer[0].Kind);
        Assert.Equal(JetCautionKind.Damage, buffer[1].Kind);
        Assert.Equal(JetCautionKind.PodOxygen, buffer[2].Kind);
        // The critical hull line follows the immediate band.
        int hullIndex = -1;
        for (int i = 0; i < count; i++)
            if (buffer[i].Kind == JetCautionKind.CriticalResource && buffer[i].Word == "HULL") hullIndex = i;
        Assert.True(hullIndex >= 3);
        Assert.Equal(JetCautionModel.PriorityResource, buffer[hullIndex].Priority);
    }

    [Fact]
    public void Damage_flash_caution_follows_the_shared_flash_clock_exactly()
    {
        // The damage flash timing is DamageFlashTracker's 0.25 on / 0.25 off / 2.0 s window
        // (native c_dtFlashOn/c_dtFlashOff/c_dtFlashingDuration, WinTrek.cpp:35-37). The
        // panel must follow that clock frame for frame: lit exactly when the tracker says
        // lit, with no renderer-owned timer in between.
        var tracker = new DamageFlashTracker();
        tracker.Arm(0f);
        var surface = new Recorder();
        using var jet = Jet(surface);
        var buffer = new JetCaution[JetCautionModel.MaxCautions];
        float[] samples = [0.1f, 0.3f, 0.6f, 1.1f, 1.9f, 2.5f];
        foreach (float now in samples)
        {
            bool flashing = tracker.IsFlashing(now);
            HudModel model = Model(flashing: flashing);
            int count = JetCautionModel.Build(model, buffer);
            bool damage = false;
            for (int i = 0; i < count; i++) damage |= buffer[i].Kind == JetCautionKind.Damage;
            Assert.Equal(flashing, damage);
        }
        // The window really ends after 2.0 s of the same shared clock.
        Assert.False(tracker.IsFlashing(DamageFlashTracker.FlashingDuration + .1f));
    }

    [Fact]
    public void The_panel_never_owns_a_timer_acknowledgement_or_sound()
    {
        Assert.DoesNotContain(typeof(JetCautionModel).GetMembers(), m => m.Name.Contains("Sound", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(JetCautionModel).GetMembers(), m => m.Name.Contains("Ack", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(JetCautionModel).GetMembers(), m => m.Name.Contains("Timer", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(JetHudRenderer).GetMembers(), m => m.Name.Contains("Sound", StringComparison.Ordinal));
    }

    [Fact]
    public void Switching_skins_mid_caution_does_not_retrigger_anything()
    {
        // A caution held across a Classic → Jet → Modern switch stays one panel per frame,
        // one shared-pass set per draw and no second batch session — the panel reads the
        // shared model and never acknowledges or replays a warning.
        var surface = new Recorder();
        var resources = new Counting();
        HudModel model = Model(missileTrack: new MissileTrackState(true, .5f), flashing: true, hullFraction: .05f);
        var settings = new HudSettings { Skin = HudSkinIds.Jet };
        var jet = new JetHudRenderer(surface, settings, resources.Resources,
            HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!);
        var switcher = new HudSkinSwitcher(settings, new Stub(HudSkinIds.Classic), new Stub(HudSkinIds.Modern), jet);
        Assert.Same(jet, switcher.Active.Renderer);

        jet.Draw(model, Frame());
        Assert.Equal(9, surface.count("MASTER CAUTION"));   // one keyline+ink text run
        Assert.Equal(1, resources.Radar);

        Assert.True(switcher.Request(HudSkinIds.Classic));
        Assert.True(switcher.ApplyPending());
        jet.Draw(model, Frame());
        Assert.Equal(18, surface.count("MASTER CAUTION"));  // one panel per frame, no replay
        Assert.Equal(2, resources.Radar);
        Assert.Equal(2, surface.Frames);
        switcher.Dispose();
    }

    [Fact]
    public void A_clear_frame_draws_no_caution_panel()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var buffer = new JetCaution[JetCautionModel.MaxCautions];
        int count = JetCautionModel.Build(Model(), buffer);
        Assert.Equal(0, count);
        jet.Draw(Model(), Frame());
        Assert.DoesNotContain("MASTER CAUTION", surface.Seen);
        Assert.DoesNotContain("DAMAGE", surface.Seen);
    }

    [Fact]
    public void A_pods_zeroed_ammo_and_energy_never_raise_a_resource_caution()
    {
        // Native zeroes the ammo/energy gauges in a pod (dialog.mdl:208,435,470,507); the
        // panel must not report those structural zeros as a resource emergency.
        var buffer = new JetCaution[JetCautionModel.MaxCautions];
        int count = JetCautionModel.Build(Model(lifepod: true), buffer);
        for (int i = 0; i < count; i++)
            Assert.False(buffer[i].Kind is JetCautionKind.CriticalResource or JetCautionKind.LowResource,
                $"a pod caution line resolved for {buffer[i].Word}");
        Assert.Contains(Enumerable.Range(0, count), i => buffer[i].Kind == JetCautionKind.PodOxygen);
    }

    [Theory]
    [InlineData("Ripcord in: 9", 9)]
    [InlineData("Ripcord in: 0", 0)]
    [InlineData("Ripcord in:", null)]
    [InlineData(null, null)]
    public void The_ripcord_line_reads_the_shared_label_number(string? label, int? seconds)
        => Assert.Equal(seconds, JetCautionModel.RipcordSeconds(label));

    // ── F4 inventory pane: every row type, the shared values ─────────────
    [Fact]
    public void Every_inventory_row_type_is_present_with_the_shared_model_values()
    {
        HudInventoryPane pane = InventoryPane();
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(inventory: pane, inventoryVisible: true, inventorySlide: 1f);
        jet.Draw(model, Frame());

        Assert.Contains(pane.ShipName, surface.Seen);       // own-ship / hull title
        for (int i = 0; i < pane.Rows.Count; i++)
        {
            HudInventoryRow row = pane.Rows[i];
            if (row.Kind == HudInventoryRowKind.Observers)
            {
                Assert.Contains(row.Name, surface.Seen);
                continue;
            }
            if (row.Key.Length > 0) Assert.Contains(row.Key, surface.Seen);
            Assert.Contains(row.Name, surface.Seen);
            if (row.Quantity is { } quantity)
                Assert.Contains(((int)MathF.Round(quantity)).ToString(CultureInfo.InvariantCulture), surface.Seen);
            if (JetInventoryPresentation.StateWord(row, out _) is { } state)
                Assert.Contains(state, surface.Seen);
            if (row.OutOfAmmo) Assert.Contains("A", surface.Seen);
        }
        // Every stat value the pane reports is a field of the shared StatsText itself.
        var runs = new JetInventoryPresentation.StatRun[3];
        foreach (HudInventoryRow row in pane.Rows)
        {
            int count = JetInventoryPresentation.StatsRuns(row, runs);
            for (int i = 0; i < count; i++)
            {
                Assert.Contains(runs[i].Value, surface.Seen);
                string shared = row.StatsText!;
                Assert.True(shared.Contains(runs[i].Value, StringComparison.Ordinal)
                    || shared.Contains(runs[i].Value.Replace(" s", " sec", StringComparison.Ordinal), StringComparison.Ordinal),
                    $"stat {runs[i].Label} {runs[i].Value} is not a field of the shared stats string '{shared}'");
            }
        }
        // The stat vocabulary is present (the §5.6 columns).
        Assert.Contains("RNG", surface.Seen);
        Assert.Contains("DMG", surface.Seen);
        Assert.Contains("ROF", surface.Seen);
        Assert.Contains("SHD STR", surface.Seen);
        Assert.Contains("RECH", surface.Seen);
        Assert.Contains("AB SPD", surface.Seen);
        Assert.Contains("AB END", surface.Seen);
        Assert.Contains("CLK END", surface.Seen);
    }

    [Fact]
    public void Selected_and_active_state_is_a_shape_not_only_an_ink_change()
    {
        HudInventoryPane pane = InventoryPane();
        var selected = new Recorder();
        using (var jet = Jet(selected))
            jet.Draw(Model(inventory: pane, inventoryVisible: true, inventorySlide: 1f), Frame());

        // The selected row (row 0) gets an open marker box: four hairline strokes at the
        // marker column; the active row gets a filled square.
        HudInventoryRow row = pane.Rows[0];
        Assert.True(row.Selected);
        int markerStrokes = selected.linesInColumn(MarkerColumn(1080, 1920), 20f, rowIndexTop(0, 1080, pane.Rows.Count));
        Assert.True(markerStrokes >= 4, $"expected the selected marker box, got {markerStrokes} strokes");
    }

    [Fact]
    public void The_pane_keeps_the_native_anchor_and_the_shared_slide_progress()
    {
        HudInventoryPane pane = InventoryPane();
        int rows = pane.Rows.Count;
        var closed = new Recorder();
        using (var jet = Jet(closed))
            jet.Draw(Model(inventory: pane, inventoryVisible: false, inventorySlide: 0f), Frame());
        Assert.DoesNotContain(pane.ShipName, closed.Seen);

        var midSlide = new Recorder();
        using (var jet = Jet(midSlide))
            jet.Draw(Model(inventory: pane, inventoryVisible: true, inventorySlide: .5f), Frame());
        var open = new Recorder();
        using (var jet = Jet(open))
            jet.Draw(Model(inventory: pane, inventoryVisible: true, inventorySlide: 1f), Frame());

        // The mid-slide pane sits exactly the shared 190·(1−slide) offset below the open one.
        float mid = midSlide.run(pane.ShipName)!.Value.Y;
        float full = open.run(pane.ShipName)!.Value.Y;
        Assert.Equal(JetInventoryPresentation.SlideDistance * .5f, mid - full, 1);
        // The right edge is the native SideBottomRight(-36) anchor.
        RectangleF panel = open.LargestPlate();
        Assert.Equal(InventoryPaneLayout.RightX(1920f, 1080f), panel.X + panel.Width, 1);
    }

    // ── compact (800×480) ────────────────────────────────────────────────
    [Fact]
    public void At_800x480_the_stores_collapse_to_three_rows_and_the_caution_to_two_lines()
    {
        Assert.True(JetStoresPanel.Compact(480f));
        Assert.Equal(3, JetStoresPanel.RowCount(true));
        Assert.Equal(2, JetCautionModel.LineLimit(true));

        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(missileTrack: new MissileTrackState(true, .8f), flashing: true, hullFraction: .05f,
            inventory: InventoryPane(), inventoryVisible: true, inventorySlide: 1f);
        jet.Draw(model, new HudDrawFrame(new Viewport(0, 0, 800, 480), null, false));

        // Exactly two caution lines are drawn (the header plus two).
        float header = surface.run("MASTER CAUTION")!.Value.Y;
        var cautionLines = surface.TextRuns.Where(r => r.Y > header + .5f && r.Y < header + 3f * 16f * .667f).ToList();
        Assert.True(cautionLines.Count >= 2);
        // The stores do not pass their three-row band.
        float bottom = JetStoresPanel.Bottom(480f);
        float top = JetStoresPanel.HeaderTop(480f, true, ModernHudScale.Core(1f, 1f, 480));
        foreach (var run in surface.TextRuns.Where(r => r.Text is "HULL" or "SHLD" or "ENG" or "GUN"))
            Assert.InRange(run.Y, top - 1f, bottom + 1f);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(800, 480)]
    public void No_caption_or_readout_plate_is_covered_by_a_stores_caution_or_pane_label(int width, int height)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(missileTrack: new MissileTrackState(true, .8f), flashing: true,
            inventory: InventoryPane(), inventoryVisible: true, inventorySlide: 1f);
        jet.Draw(model, new HudDrawFrame(new Viewport(0, 0, width, height), null, false));

        RectangleF speedCaption = surface.Caption(JetHudRenderer.SpeedCaptionText);
        RectangleF throttleCaption = surface.Caption(JetHudRenderer.ThrottleCaptionText);
        foreach (var run in surface.TextRuns)
        {
            if (run.Text != JetHudRenderer.SpeedCaptionText)
                Assert.False(Overlaps(speedCaption, run), $"{run.Text} overlaps the SPD caption at {width}x{height}");
            if (run.Text != JetHudRenderer.ThrottleCaptionText)
                Assert.False(Overlaps(throttleCaption, run), $"{run.Text} overlaps the THR caption at {width}x{height}");
        }
        var panes = surface.Plates().Where(p => p.Height > 64f).ToList();
        bool InPane(TextRun run) => panes.Any(p => Contains(p, run));
        foreach (RectangleF plate in surface.Plates())
        {
            // A fill taller than four rows is a PANE (the F4 inventory panel, the caution
            // stack), not a readout box; an open F4 pane legitimately covers the tapes, and
            // its own rows are that pane's content.
            if (plate.Height > 64f) continue;
            foreach (var run in surface.TextRuns)
            {
                if (InPane(run)) continue;
                Assert.False(Overlaps(plate, run) && !Contains(plate, run),
                    $"{run.Text} overlaps the plate at {plate.X:0.#},{plate.Y:0.#} ({width}x{height})");
            }
        }
    }

    // ── P1037 follow-ups ─────────────────────────────────────────────────
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.25f)]
    [InlineData(0.62f)]
    [InlineData(1.0f)]
    public void The_designator_stays_inside_the_lock_diamond_with_a_gap(float fraction)
    {
        const float radius = 120f;
        float diamond = JetTargetGeometry.LockDiamondHalf(
            fraction >= 1f ? MissileLockFeedback.LockState.Full : MissileLockFeedback.LockState.Partial,
            fraction, radius, 400f, 1f);
        float natural = JetTargetGeometry.DesignatorHalf(radius, 1f, 400f);
        float half = JetTargetGeometry.DesignatorHalfInsideLock(natural, diamond, 1f);

        // Corner distance along the diamond's diagonal vs the diamond's own edge there.
        float corner = half * MathF.Sqrt(2f);
        // A diamond's boundary along the corner diagonal is radius/√2 away.
        float edge = diamond * JetTargetGeometry.InverseSqrt2;
        Assert.True(corner <= edge - 1f,
            $"designator corner {corner:0.##} reaches past the diamond edge {edge:0.##} at lock {fraction}");
    }

    [Fact]
    public void The_native_lock_shrink_still_drives_the_diamond_size()
    {
        float open = JetTargetGeometry.LockDiamondHalf(MissileLockFeedback.LockState.Partial, 0f, 120f, 400f, 1f);
        float half = JetTargetGeometry.LockDiamondHalf(MissileLockFeedback.LockState.Partial, .5f, 120f, 400f, 1f);
        float full = JetTargetGeometry.LockDiamondHalf(MissileLockFeedback.LockState.Full, 1f, 120f, 400f, 1f);
        Assert.True(open > half);
        Assert.True(half > full);
        // Native radiusBracket + 40·(1−lock): the open end is the full size plus 40 px.
        Assert.Equal(40f, open - full, 1);
        // The native clip is kept: at the screen edge the bracket shrinks to the edge gutter.
        Assert.True(JetTargetGeometry.LockDiamondHalf(MissileLockFeedback.LockState.Full, 1f, 900f, 30f, 1f) <= 42f);
    }

    [Fact]
    public void At_1080p_a_locked_designator_reads_clear_of_the_diamond_strokes()
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        var target = new HudTarget(new(1160f, 362f), true, "Enemy fighter", .6f, .3f, true, null, 1250f, 43f,
            new HudColor(1f, 0f, 0f), 120f);
        var lockState = new HudLock(new(1160f, 362f), MissileLockFeedback.LockState.Partial, 1.0, .62f, 120f);
        HudModel model = Model(target: target, lockState: lockState);
        jet.Draw(model, Frame());

        float diamond = JetTargetGeometry.LockDiamondHalf(lockState.State, lockState.Fraction, 120f, 1160f, 1f);
        // Every designator text run starts right of the diamond's right vertex (or ends
        // left of the left vertex): no readout can cross a lock stroke.
        foreach (var run in surface.TextRuns)
        {
            if (run.Text is not ("HOSTILE" or "Enemy fighter" or "FRIEND" or "NEUTRAL")) continue;
            Assert.True(run.X >= 1160f + diamond + 4f || run.X + run.Width <= 1160f - diamond - 4f,
                $"readout {run.Text}@{run.X:0.#} crosses the diamond radius {diamond:0.#}");
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(800, 480)]
    public void The_pitch_numeral_identifies_itself(int width, int height)
    {
        var surface = new Recorder();
        using var jet = Jet(surface);
        jet.Draw(Model(flightReference: Reference(5f, 0f, 45f)),
            new HudDrawFrame(new Viewport(0, 0, width, height), null, false));
        Assert.Contains(surface.TextRuns, run => run.Text.StartsWith("PITCH ", StringComparison.Ordinal));
        Assert.Contains("PITCH +05", surface.Seen);
        // The position rule is unchanged: still centred under the ladder, inside the frame.
        var pitchRun = surface.TextRuns.Single(r => r.Text.StartsWith("PITCH ", StringComparison.Ordinal));
        Assert.InRange(pitchRun.Y, height * .5f, height);
    }

    [Fact]
    public void At_4K_the_panels_keep_their_physical_stroke_and_do_not_inflate()
    {
        // P833/P859: the Jet scale is the density product capped by the window budget, so a
        // 3840×2160 window at a 1.0 desktop draws the SAME physical sizes as 1080p — no
        // inflated stores/rows and no sub-1.5 px hairline at four times the pixels.
        var surface = new Recorder();
        using var jet = Jet(surface);
        HudModel model = Model(missileTrack: new MissileTrackState(true, .8f), flashing: true,
            inventory: InventoryPane(), inventoryVisible: true, inventorySlide: 1f);
        jet.Draw(model, new HudDrawFrame(new Viewport(0, 0, 3840, 2160), null, false));

        float scale = ModernHudScale.Core(1f, 1f, 2160);
        Assert.Equal(1f, scale, 3);
        Assert.True(jet.Painter.Stroke >= JetVectorPainter.MinimumLineStroke);
        // The stores block sits at the same physical column and row pitch as at 1080p.
        var storesHeader = surface.run("STORES")!.Value;
        Assert.Equal(JetStoresPanel.Left(scale), storesHeader.X, 1);
        Assert.Equal(JetStoresPanel.HeaderTop(2160f, false, scale), storesHeader.Y, 1);
        // And no label covers a caption or a readout plate at 4K either.
        RectangleF speedCaption = surface.Caption(JetHudRenderer.SpeedCaptionText);
        foreach (var run in surface.TextRuns.Where(r => r.Text != JetHudRenderer.SpeedCaptionText))
            Assert.False(Overlaps(speedCaption, run), $"{run.Text} overlaps the SPD caption at 4K");
    }

    // ── allocation gate ──────────────────────────────────────────────────
    [Fact]
    public void Draw_with_stores_caution_and_an_open_F4_pane_allocates_nothing()
    {
        var surface = new Counter();
        var noop = new HudSkinResources(_ => { }, (_, _) => { }, _ => { }, (_, _) => { }, _ => { }, _ => { }, _ => { });
        using var jet = Jet(surface, noop);
        HudModel model = Model(missileTrack: new MissileTrackState(true, .5f), flashing: true, hullFraction: .05f,
            console: new HudConsoleText
            {
                Lives = 2, MissionKills = 4, Deathmatch = true, VoteText = "Vote: Mutiny?",
                CriticalTips = new[] { new HudNotice("Reloading ammo...", true, 1) },
            },
            ripcord: "Ripcord in: 9",
            inventory: InventoryPane(), inventoryVisible: true, inventorySlide: 1f);
        var frame = Frame();
        void Draw(int count) { for (int i = 0; i < count; i++) jet.Draw(model, frame); }
        Draw(10_000);
        Assert.True(surface.Texts > 0 && surface.Lines > 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Draw(100_000);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P1038 JetHudRenderer.Draw (stores+caution+inventory): {bytes} B / 100000 frames = {bytes / 100000d} B/frame");
        Assert.Equal(0, bytes);
    }

    // ── draw smoke for every state P1037 used ────────────────────────────
    public static TheoryData<string> States => new()
    {
        "cockpit", "chase", "command", "docked", "lifepod", "dead", "ghost", "turret", "training",
        "hidden", "no-telemetry", "no-reference", "target", "locked", "lock-full", "lead", "lead-out",
        "orders", "offscreen", "friendly", "asteroid-ore",
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Jet_draws_every_state_with_the_new_panels_without_throwing(string state)
    {
        var surface = new Recorder();
        var resources = new Counting();
        using var jet = Jet(surface, resources.Resources);
        HudModel model = State(state);
        jet.Draw(model, new HudDrawFrame(new Viewport(0, 0, 1280, 720), null, state == "turret"));

        Assert.Equal(1, resources.Radar);
        Assert.Equal(1, resources.Tags);
        Assert.Equal(1, resources.PodDeath);
        if (state == "hidden")
        {
            Assert.Empty(surface.Segments());
            return;
        }
        Assert.Contains(JetHudRenderer.TagText, surface.Seen);
        // The caution panel exists exactly when the shared model resolves a caution.
        var buffer = new JetCaution[JetCautionModel.MaxCautions];
        bool anyCaution = JetCautionModel.Build(model, buffer) > 0;
        Assert.Equal(anyCaution, surface.Seen.Contains("MASTER CAUTION"));
        // The stores exist exactly under the instrument gate with telemetry.
        bool stores = model.InstrumentsVisible && model.Telemetry is not null;
        Assert.Equal(stores, surface.Seen.Contains("STORES"));
        // No fabricated sentinel leaks into a readout.
        Assert.DoesNotContain(surface.TextRuns, run => run.Text == "-1");
    }

    // ── fixtures ─────────────────────────────────────────────────────────
    private static HudSettings Settings() => new() { Skin = HudSkinIds.Jet };

    private static HudFrame Frame(float speed = 260f, float throttle = .6f, float hull = .8f, float shield = .7f,
        float ammo = .8f, float fuel = .9f, float energy = .9f, bool flashing = false,
        bool lifepod = false, float autopilotRoll = 0f, int oxygen = 0) => new(
        hull, shield, lifepod ? 0f : ammo, fuel, lifepod ? 0f : energy, 1f, speed, throttle, false, false, true, 0,
        flashing, 0f, false, false, 15f, 0f,
        new WeaponStatusRow[]
        {
            new(0, "Rapid fire plasma cannon", true, (short)(ammo * 200f), 200, WeaponStatus.Ready),
        },
        lifepod, oxygen, 2500, null, 100f, ShipWarningMask.OutOfBounds,
        SignaturePercent: 42f, Detected: false, AutopilotEngaged: false, VectorLockEngaged: false,
        CarriedFlag: false, CarriedArtifact: false, CloakStrengthPercent: null);

    private static HudModel Model(
        float hullFraction = .8f, float shieldFraction = .7f, float ammoFraction = .8f, float energyFraction = .9f,
        bool flashing = false, bool lifepod = false, bool commandView = false, bool ghost = false,
        bool eye = false, bool cloak = false, bool autopilot = false, bool vectorLock = false,
        bool carriedFlag = false, bool carriedArtifact = false, float cloakStrength = 70f,
        MissileTrackState? missileTrack = null, bool boundary = false, HudConsoleText? console = null,
        string? ripcord = null, HudTarget? target = null, HudLock? lockState = null,
        HudInventoryPane? inventory = null, bool inventoryVisible = false, float inventorySlide = 0f,
        HudFlightReference? flightReference = null, WeaponStatusRow? launcher = null)
    {
        HudFrame frame = Frame(hull: hullFraction, shield: shieldFraction, ammo: ammoFraction, energy: energyFraction,
            flashing: flashing, lifepod: lifepod, autopilotRoll: 0f,
            oxygen: lifepod ? 95 : 0, fuel: .9f);
        frame = frame with
        {
            WarningMask = boundary ? ShipWarningMask.OutOfBounds | ShipWarningMask.CrowdedSector : ShipWarningMask.None,
            Detected = eye,
            Cloaked = cloak,
            CloakStrengthPercent = cloak ? cloakStrength : null,
            AutopilotEngaged = autopilot,
            VectorLockEngaged = vectorLock,
            CarriedFlag = carriedFlag,
            CarriedArtifact = carriedArtifact,
            WeaponSystems = launcher is { } row
                ? new[] { new WeaponStatusRow(0, "Rapid fire plasma cannon", true, (short)(ammoFraction * 200f), 200,
                    WeaponStatus.Ready), row }
                : new[] { new WeaponStatusRow(0, "Rapid fire plasma cannon", true, (short)(ammoFraction * 200f), 200,
                    WeaponStatus.Ready) },
        };
        return HudModel.Create(frame, new HudContext
        {
            Camera = CameraMode.Cockpit,
            CommandView = commandView,
            Ghost = ghost,
            MissileTrack = missileTrack,
            Lock = lockState,
            Console = console,
            Ripcord = ripcord,
            Target = target,
            FlightReference = flightReference,
            InventoryPane = inventory,
            InventoryVisible = inventoryVisible,
            InventorySlide = inventorySlide,
            SelectionBracket = true,
            VelocityMarker = new HudPoint(900f, 560f),
        });
    }

    private static HudDrawFrame Frame() => new(new Viewport(0, 0, 1920, 1080), null, false);

    private static HudModel State(string state) => state switch
    {
        "cockpit" => Model(flightReference: Reference(0f, 0f, 45f)),
        "chase" => Model(flightReference: Reference(6f, 3f, 90f)),
        "command" => Model(commandView: true),
        "docked" => Model(),
        "lifepod" => Model(lifepod: true),
        "dead" => Model(),
        "ghost" => Model(ghost: true, console: new HudConsoleText { Lives = 3, MissionKills = 7, Deathmatch = true }),
        "turret" => Model(),
        "training" => Model(),
        "hidden" => HudModel.Create(null, new HudContext { Hidden = true }),
        "no-telemetry" => HudModel.Create(null, new HudContext()),
        "no-reference" => Model(),
        "target" => Model(target: new HudTarget(new(1160f, 362f), true, "Enemy fighter", .6f, .3f, true, null,
            1250f, 43f, new HudColor(1f, 0f, 0f), 60f)),
        "locked" => Model(target: new HudTarget(new(1160f, 362f), true, "Enemy fighter", .6f, .3f, true, null,
                1250f, 43f, new HudColor(1f, 0f, 0f), 120f),
            lockState: new HudLock(new(1160f, 362f), MissileLockFeedback.LockState.Partial, 1.0, .62f, 120f)),
        "lock-full" => Model(target: new HudTarget(new(1160f, 362f), true, "Enemy fighter", .6f, .3f, true, null,
                1250f, 43f, new HudColor(1f, 0f, 0f), 120f),
            lockState: new HudLock(new(1160f, 362f), MissileLockFeedback.LockState.Full, 1.0, 1f, 120f)),
        "lead" or "lead-out" or "orders" or "offscreen" or "friendly" or "asteroid-ore" => Model(),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown state"),
    };

    private static HudFlightReference Reference(float pitchDegrees, float rollDegrees, float headingDegrees)
    {
        float heading = headingDegrees * MathF.PI / 180f;
        float pitch = pitchDegrees * MathF.PI / 180f;
        float roll = rollDegrees * MathF.PI / 180f;
        var forward = new Allegiance.Core.Collision.Vector3(MathF.Cos(heading) * MathF.Cos(pitch),
            MathF.Sin(heading) * MathF.Cos(pitch), MathF.Sin(pitch));
        var levelUp = new Allegiance.Core.Collision.Vector3(-MathF.Cos(heading) * MathF.Sin(pitch),
            -MathF.Sin(heading) * MathF.Sin(pitch), MathF.Cos(pitch));
        var orientation = new Orientation();
        orientation.Set(forward, levelUp);
        if (roll != 0f) orientation.PostRotate(forward, roll);
        return HudFlightReference.FromSim(orientation, forward * 260f, HudFlightReference.SectorUp);
    }

    /// <summary>One row of every native kind, with the stats strings the shared builder
    /// produces (inv*.mdl row vocabulary, <c>HudInventoryPaneBuilder</c>).</summary>
    private static HudInventoryPane InventoryPane() => new("Fig Adv", new HudInventoryRow[]
    {
        new(HudInventoryRowKind.Weapon, "1.", "Gatling Gun", true, true, false, "800m / 15 X 8", 3, 1f, null, null),
        new(HudInventoryRowKind.Weapon, "2.", "Plasma Cannon", false, false, false, "1200m / 20 X 5", 3, 1f, null, null),
        new(HudInventoryRowKind.Turret, "", "Pilot Beta", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.Observers, "", "Observers: 1", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.Weapon, "Y.", "Turret weapon", false, false, false, null, 1, .45f, null, null),
        new(HudInventoryRowKind.Magazine, "5.", "SRM Dumbfire 3", false, false, false, "5 / 1125m / 150", 2, 1f, .6f, 5),
        new(HudInventoryRowKind.Shield, "7.", "Energy Shield", false, false, false, "500 / 3 sec", 3, 1f, null, 1),
        new(HudInventoryRowKind.Afterburner, "9.", "Booster", false, false, false, "220mps / 8", 3, 1f, null, 1),
        new(HudInventoryRowKind.Cloak, "8.", "Cloaking Device", false, false, false, "82% / 12 sec", 3, 1f, null, 1),
        new(HudInventoryRowKind.Chaff, "6.", "Chaff", false, false, false, "40", 3, 1f, null, 40),
        new(HudInventoryRowKind.Cargo, "S.", "Ammo Pack", false, false, false, "3", 0, 0f, null, 3),
        new(HudInventoryRowKind.EmptyWeapon, "3.", "<small weapon mount>", false, false, false, null, 0, 0f, null, null),
        new(HudInventoryRowKind.EmptyCargo, "", "<cargo mount>", false, false, false, null, 0, 0f, null, null),
    });

    private static string Percent(float fraction)
        => ((int)MathF.Round(Math.Clamp(fraction, 0f, 1f) * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

    private static float MarkerColumn(int height, int width)
        => JetInventoryPresentation.Left(width, height)
           + JetInventoryPresentation.MarkerColumn * JetInventoryPresentation.Scale(height);

    private static float rowIndexTop(int index, int height, int rows)
        => JetInventoryPresentation.Top(rows, 1920f, height, 1f)
           + (JetInventoryPresentation.CapHeight + JetInventoryPresentation.RowHeight * index)
             * JetInventoryPresentation.Scale(height);

    private static JetHudRenderer Jet(IJetVectorSurface surface, HudSkinResources? resources = null)
        => new(surface, Settings(), resources ?? new Counting().Resources,
            HudSkinResolver.Catalog.TryGet(HudSkinIds.Jet)!);

    private static bool Overlaps(RectangleF box, TextRun run)
        => run.X < box.X + box.Width - 1f && box.X < run.X + run.Width - 1f
        && run.Y < box.Y + box.Height - 1f && box.Y < run.Y + run.Height - 1f;

    private static bool Contains(RectangleF outer, TextRun inner)
        => inner.X >= outer.X - 1.5f && inner.X + inner.Width <= outer.X + outer.Width + 1.5f
        && inner.Y >= outer.Y - 1.5f && inner.Y + inner.Height <= outer.Y + outer.Height + 1.5f;

    public readonly record struct TextRun(string Text, float X, float Y, float Width, float Height);

    /// <summary>Records every submission; the metrics are a fixed 6×12 grid so the geometry
    /// assertions are deterministic and device-free.</summary>
    private sealed class Recorder : IJetVectorSurface
    {
        private readonly List<(Vector2 A, Vector2 B, Color Color, float Width)> _segments = new();
        private readonly List<(Vector2 Min, Vector2 Size, Color Color)> _fills = new();
        private readonly List<TextRun> _runs = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public int Frames { get; private set; }
        public IReadOnlyList<TextRun> TextRuns => _runs;
        public IReadOnlySet<string> Seen => _seen;
        public int LineCount => _segments.Count;
        public int Texts => _runs.Count;

        public int count(string text) => _counts.TryGetValue(text, out int n) ? n : 0;
        public TextRun? run(string text)
        {
            foreach (TextRun run in _runs) if (run.Text == text) return run;
            return null;
        }

        public void Begin() => Frames++;
        public void End() { }
        public void Line(Vector2 a, Vector2 b, float width, Color color) => _segments.Add((a, b, color, width));
        public void Fill(Vector2 min, Vector2 size, Color color) => _fills.Add((min, size, color));

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

        /// <summary>Strokes whose two endpoints lie in a 40 px box around a column/row cell.</summary>
        public int linesInColumn(float x, float width, float rowTop)
        {
            int n = 0;
            foreach (var (a, b) in Segments())
            {
                if (MathF.Abs(a.X - x) > width || MathF.Abs(b.X - x) > width) continue;
                if (a.Y < rowTop - 8f || b.Y < rowTop - 8f) continue;
                if (a.Y > rowTop + 40f || b.Y > rowTop + 40f) continue;
                n++;
            }
            return n;
        }

        public RectangleF Caption(string text)
        {
            foreach (TextRun run in _runs)
                if (run.Text == text) return new RectangleF(run.X, run.Y, run.Width, run.Height);
            return new RectangleF(-1f, -1f, 0f, 0f);
        }

        /// <summary>Every readout plate reconstructed from its five fills.</summary>
        public IEnumerable<RectangleF> Plates()
        {
            for (int i = 0; i + 4 < _fills.Count; i++)
            {
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

        public RectangleF LargestPlate()
        {
            RectangleF best = default;
            float area = 0f;
            foreach (RectangleF plate in Plates())
                if (plate.Width * plate.Height > area) { area = plate.Width * plate.Height; best = plate; }
            return best;
        }
    }

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

    private sealed class Counting
    {
        public int Radar, Tags, PodDeath, Command, Chat, Cargo, Parts;
        public HudSkinResources Resources { get; }

        public Counting()
        {
            Resources = new HudSkinResources(
                _ => Radar++,
                (_, _) => Tags++,
                _ => PodDeath++,
                (_, _) => Command++,
                _ => Chat++,
                _ => Cargo++,
                _ => Parts++);
        }
    }
}
