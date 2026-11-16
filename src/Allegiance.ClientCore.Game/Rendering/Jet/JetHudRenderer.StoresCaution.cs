using System;
using System.Globalization;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1038 — Jet story 4/9: the stores block, the master-caution panel and the F4 inventory
/// pane (P972 §4.3, §4.4 first paragraph, §4.5, §5.1/§5.5/§5.6; phase 3B).
/// <para><b>Presentation only.</b> Every reading is a shared-model value: the six flight
/// gauges (<see cref="HudGauge"/>), the own-ship status flags (<see cref="HudOwnStatus"/>),
/// the weapon-status rows (<see cref="HudFrame.WeaponSystems"/>), the world's
/// missile-track snapshot (<see cref="HudContext.MissileTrack"/>), the console badges and
/// the native inventory rows (<see cref="HudInventoryPane"/>). Nothing is derived a second
/// way, no zero is invented for an unknown value, and no timer, acknowledgement or sound
/// is owned here.</para>
/// </summary>
public sealed partial class JetHudRenderer
{
    private const string StoresHeader = "STORES";
    private const string GunWord = "GUN";
    private const string MissileWord = "MSL";
    private const string CautionHeader = "MASTER CAUTION";

    private readonly JetCaution[] _cautionBuffer = new JetCaution[JetCautionModel.MaxCautions];

    /// <summary>A tiny integer → string cache: a steady frame formats nothing.</summary>
    private struct IntCache
    {
        public int Key;
        public string? Text;
    }

    private readonly IntCache[] _storeCache = new IntCache[24];
    private readonly IntCache[] _quantityCache = new IntCache[40];
    private readonly string?[] _statKeys = new string?[40];
    private readonly HudInventoryRowKind[] _statKinds = new HudInventoryRowKind[40];
    private readonly JetInventoryPresentation.StatRun[][] _statRuns = new JetInventoryPresentation.StatRun[40][];

    /// <summary>A cached integer readout (percent when <paramref name="percent"/>).</summary>
    private string CachedNumber(int slot, int value, bool percent)
    {
        ref IntCache cell = ref _storeCache[slot];
        int key = percent ? -value - 1 : value;
        if (cell.Key == key && cell.Text is not null) return cell.Text;
        cell.Key = key;
        cell.Text = percent
            ? value.ToString(CultureInfo.InvariantCulture) + "%"
            : value.ToString(CultureInfo.InvariantCulture);
        return cell.Text;
    }

    /// <summary>The shared gauge of one kind, or null when the model has none (no
    /// telemetry ⇒ no store row, never an invented zero).</summary>
    private static HudGauge? GaugeOf(HudModel model, GaugeKind kind)
    {
        var gauges = model.Gauges;
        for (int i = 0; i < gauges.Count; i++)
            if (gauges[i].Kind == kind) return gauges[i];
        return null;
    }

    // ── stores block (P972 §4.3, §5.1) ──────────────────────────────────
    /// <summary>
    /// The compact stores grouping: HULL, SHLD, ENG, FUEL (a pod's OXY with the same
    /// fraction), GUN ammo %/count plus the empty <c>A</c> cue, the launcher count and its
    /// shared arming state, KB, SIG and the own-status annunciators (EYE, CLK nn /
    /// DRAINED, AUTO, VLOCK, FLAG, ART). Each row exists only while its own shared gate is
    /// set; at 800×480 the block collapses to three rows (P972 §4.5).
    /// </summary>
    private void DrawStores(HudModel model, float w, float h, float scale)
    {
        // No telemetry ⇒ the shared model resolves no gauge at all: the block stays blank
        // rather than printing a header over invented zeroes.
        if (!model.InstrumentsVisible || model.Telemetry is null) return;
        bool compact = JetStoresPanel.Compact(h);
        float left = JetStoresPanel.Left(scale);
        float rowH = JetStoresPanel.RowPitch * scale;

        // Header.
        float headerTop = JetStoresPanel.HeaderTop(h, compact, scale);
        _painter.Text(StoresHeader, new(left, headerTop), large: false, scale, _painter.Dim);

        HudGauge? hull = GaugeOf(model, GaugeKind.Hull);
        HudGauge? shield = GaugeOf(model, GaugeKind.Shield);
        HudGauge? energy = GaugeOf(model, GaugeKind.Energy);
        HudGauge? fuel = GaugeOf(model, GaugeKind.Fuel);
        HudGauge? ammo = GaugeOf(model, GaugeKind.Ammo);
        var status = model.Status;

        if (compact)
        {
            // Row 0 — the two hull/shield readings on one line.
            float y0 = JetStoresPanel.RowTop(h, true, scale, 0);
            float cursor = left;
            cursor = StorePercentRun(cursor, y0, scale, "HULL", hull, 0, bold: true);
            StorePercentRun(cursor + 8f * scale, y0, scale, "SHLD", shield, 1, bold: false);

            // Row 1 — energy and the fuel / pod oxygen reading.
            float y1 = y0 + rowH;
            cursor = StorePercentRun(left, y1, scale, "ENG", energy, 2, bold: false);
            StorePercentRun(cursor + 8f * scale, y1, scale, fuel is { } f ? JetStoresPanel.FuelWord(f.Label) : "FUEL",
                fuel, 3, bold: false);

            // Row 2 — gun, missiles and the lit annunciators (word form only).
            float y2 = y1 + rowH;
            cursor = StorePercentRun(left, y2, scale, GunWord, ammo, 4, bold: true);
            if (GunCount(model) is { } gunCount)
            {
                cursor += 4f * scale;
                cursor = TextRun(cursor, y2, scale, CachedNumber(6, gunCount, false), _painter.Dim, ref cursor);
            }
            if (JetStoresPanel.GunEmpty(ammo?.Fraction))
                cursor = TextRun(cursor + 4f * scale, y2, scale, "A", _painter.Hot, ref cursor);
            if (MissileCount(model) is { } compactMissiles)
            {
                cursor += 6f * scale;
                _painter.Text(MissileWord, new(cursor, y2), large: false, scale, _painter.Primary);
                cursor += _painter.Measure(MissileWord, large: false, scale).X + 4f * scale;
                cursor = TextRun(cursor, y2, scale, CachedNumber(7, compactMissiles, false), _painter.Dim, ref cursor);
            }
            DrawCompactAnnunciators(cursor + 6f * scale, y2, scale, status);
            return;
        }

        // Row 0 — HULL with its segmented bar.
        float ry = JetStoresPanel.RowTop(h, false, scale, 0);
        StorePercentRun(left, ry, scale, "HULL", hull, 0, bold: true);
        if (hull is { } hullGauge && hullGauge.Visible)
            SegmentedBar(left, ry + 13f * scale, JetStoresPanel.BarWidth * scale, hullGauge.Fraction, _painter.Primary);

        // Row 1 — SHLD with its segmented bar.
        ry += rowH;
        StorePercentRun(left, ry, scale, "SHLD", shield, 1, bold: false);
        if (shield is { } shieldGauge && shieldGauge.Visible)
            SegmentedBar(left, ry + 13f * scale, JetStoresPanel.BarWidth * scale, shieldGauge.Fraction, _painter.Dim);

        // Row 2 — ENG.
        ry += rowH;
        StorePercentRun(left, ry, scale, "ENG", energy, 2, bold: false);

        // Row 3 — FUEL, or the pod's OXY with the same fraction.
        ry += rowH;
        StorePercentRun(left, ry, scale, fuel is { } fuelGauge ? JetStoresPanel.FuelWord(fuelGauge.Label) : "FUEL",
            fuel, 3, bold: false);

        // Row 4 — GUN % and the shared count, plus the empty-pool A cue.
        ry += rowH;
        float gunCursor = StorePercentRun(left, ry, scale, GunWord, ammo, 4, bold: true);
        if (GunCount(model) is { } count)
        {
            gunCursor += 4f * scale;
            TextRun(gunCursor, ry, scale, CachedNumber(6, count, false), _painter.Dim, ref gunCursor);
        }
        if (JetStoresPanel.GunEmpty(ammo?.Fraction))
            TextRun(gunCursor + 4f * scale, ry, scale, "A", _painter.Hot, ref gunCursor);

        // Row 5 — the launcher count and its shared arming state.
        ry += rowH;
        float missileCursor = left;
        _painter.Text(MissileWord, new(left, ry), large: false, scale, _painter.Dim);
        missileCursor = left + _painter.Measure(MissileWord, large: false, scale).X + 4f * scale;
        if (MissileCount(model) is { } missiles && MissileState(model) is { } state)
        {
            missileCursor = TextRun(missileCursor, ry, scale, CachedNumber(7, missiles, false), _painter.Primary, ref missileCursor);
            TextRun(missileCursor + 6f * scale, ry, scale, state, _painter.Dim, ref missileCursor);
        }

        // Row 6 — kill bonus and signature (the shared own-status readings).
        ry += rowH;
        float kbCursor = left;
        if (status.KillBonusPercent is { } killBonus)
        {
            _painter.Text("KB", new(left, ry), large: false, scale, _painter.Dim);
            kbCursor = left + _painter.Measure("KB", large: false, scale).X + 4f * scale;
            int rounded = (int)MathF.Round(killBonus);
            kbCursor = TextRun(kbCursor, ry, scale, CachedNumber(8, rounded, false), _painter.Primary, ref kbCursor);
            if (status.SignaturePercent is { } signature)
            {
                float sigX = left + JetStoresPanel.BarWidth * scale + 12f * scale;
                _painter.Text("SIG", new(sigX, ry), large: false, scale, _painter.Dim);
                float sigCursor = sigX + _painter.Measure("SIG", large: false, scale).X + 4f * scale;
                TextRun(sigCursor, ry, scale, CachedNumber(9, (int)MathF.Round(signature), false),
                    _painter.Primary, ref sigCursor);
            }
        }
        else
        {
            // SIG is independent of the kill bonus.
            if (status.SignaturePercent is { } signature)
            {
                _painter.Text("SIG", new(left, ry), large: false, scale, _painter.Dim);
                float sigCursor = left + _painter.Measure("SIG", large: false, scale).X + 4f * scale;
                TextRun(sigCursor, ry, scale, CachedNumber(9, (int)MathF.Round(signature), false),
                    _painter.Primary, ref sigCursor);
            }
        }

        // Row 7 — the own-status annunciators, each under its shared flag.
        ry += rowH;
        DrawAnnunciators(model, left, ry, scale);
    }

    /// <summary>One <c>WORD 082</c> store run (nothing at all when the gauge is absent or
    /// hidden by the shared instrument gate).</summary>
    private float StorePercentRun(float x, float y, float scale, string word, HudGauge? gauge, int slot, bool bold)
    {
        if (gauge is not { Visible: true } visible) return x;
        Color ink = bold ? _painter.Primary : _painter.Dim;
        _painter.Text(word, new(x, y), large: false, scale, ink);
        float cursor = x + _painter.Measure(word, large: false, scale).X + 4f * scale;
        int percent = (int)MathF.Round(Math.Clamp(visible.Fraction, 0f, 1f) * 100f);
        _painter.Text(CachedNumber(slot, percent, true), new(cursor, y), large: false, scale, ink);
        return cursor + _painter.Measure(CachedNumber(slot, percent, true), large: false, scale).X;
    }

    /// <summary>One plain text run; returns the cursor after it.</summary>
    private float TextRun(float x, float y, float scale, string text, Color ink, ref float cursor)
    {
        _painter.Text(text, new(x, y), large: false, scale, ink);
        cursor = x + _painter.Measure(text, large: false, scale).X;
        return cursor;
    }

    /// <summary>A segmented fraction bar (eight segments, shape not hue).</summary>
    private void SegmentedBar(float x, float y, float width, float fraction, Color ink)
    {
        float segWidth = width / JetStoresPanel.BarSegments;
        int lit = Math.Clamp((int)MathF.Round(Math.Clamp(fraction, 0f, 1f) * JetStoresPanel.BarSegments),
            0, JetStoresPanel.BarSegments);
        float gap = MathF.Max(0.5f, _painter.Hairline * 0.5f);
        float height = MathF.Max(1f, _painter.Hairline * 2f);
        for (int i = 0; i < JetStoresPanel.BarSegments; i++)
            _painter.Surface.Fill(new(x + i * segWidth, y), new(MathF.Max(1f, segWidth - gap), height),
                i < lit ? ink : _painter.Dim * 0.35f);
    }

    /// <summary>The mounted gun pool count: the first ammo-consuming weapon-status row's
    /// own <c>Ammo</c> reading (Null when the own loadout is not known).</summary>
    private static int? GunCount(HudModel model)
    {
        if (model.Telemetry is not { } frame) return null;
        var rows = frame.WeaponSystems;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].ConsumesAmmo) return rows[i].Ammo;
        return null;
    }

    /// <summary>The total loaded missiles: the sum of the launcher rows' own
    /// <c>Ammo</c> readings. Null when no launcher is mounted (nothing is invented).</summary>
    private static int? MissileCount(HudModel model)
    {
        if (model.Telemetry is not { } frame) return null;
        var rows = frame.WeaponSystems;
        int? total = null;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsLauncher) total = (total ?? 0) + rows[i].Ammo;
        return total;
    }

    /// <summary>The shared arming state of the first mounted launcher.</summary>
    private static string? MissileState(HudModel model)
    {
        if (model.Telemetry is not { } frame) return null;
        var rows = frame.WeaponSystems;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsLauncher) return JetStoresPanel.MissileStateWord(rows[i].Status);
        return null;
    }

    /// <summary>The full annunciator row: glyph + word per lit flag, in the native
    /// order (P972 §5.1).</summary>
    private void DrawAnnunciators(HudModel model, float x, float y, float scale)
    {
        var status = model.Status;
        JetStoresPanel.Annunciators set = JetStoresPanel.AnnunciatorsOf(status);
        if (!JetStoresPanel.AnyAnnunciator(set)) return;
        float cursor = x;
        float gap = 8f * scale;
        if ((set & JetStoresPanel.Annunciators.Eye) != 0)
            cursor = AnnunciatorGlyph(cursor, y, scale, JetStoresPanel.Annunciators.Eye, "EYE", _painter.Dim) + gap;
        if ((set & JetStoresPanel.Annunciators.Cloak) != 0)
        {
            Color cloakInk = status.CloakDrained ? _painter.Hot : _painter.Dim;
            cursor = AnnunciatorGlyph(cursor, y, scale, JetStoresPanel.Annunciators.Cloak, "CLK", cloakInk) + gap;
            if (status.CloakDrained)
                cursor = TextRun(cursor, y, scale, "DRAINED", _painter.Hot, ref cursor) + gap;
            else if (status.CloakStrengthPercent is { } strength)
                cursor = TextRun(cursor, y, scale,
                    CachedNumber(10, (int)MathF.Round(Math.Clamp(strength, 0f, 100f)), false),
                    _painter.Dim, ref cursor) + gap;
        }
        if ((set & JetStoresPanel.Annunciators.Autopilot) != 0)
            cursor = AnnunciatorGlyph(cursor, y, scale, JetStoresPanel.Annunciators.Autopilot, "AUTO", _painter.Dim) + gap;
        if ((set & JetStoresPanel.Annunciators.VectorLock) != 0)
            cursor = AnnunciatorGlyph(cursor, y, scale, JetStoresPanel.Annunciators.VectorLock, "VLOCK", _painter.Dim) + gap;
        if ((set & JetStoresPanel.Annunciators.Flag) != 0)
            cursor = AnnunciatorGlyph(cursor, y, scale, JetStoresPanel.Annunciators.Flag, "FLAG", _painter.Primary) + gap;
        if ((set & JetStoresPanel.Annunciators.Artifact) != 0)
            AnnunciatorGlyph(cursor, y, scale, JetStoresPanel.Annunciators.Artifact, "ART", _painter.Primary);
    }

    /// <summary>The compact form: only the lit annunciator words, no glyphs (P972 §4.5 —
    /// the state stays a word so it never vanishes).</summary>
    private void DrawCompactAnnunciators(float x, float y, float scale, HudOwnStatus status)
    {
        JetStoresPanel.Annunciators set = JetStoresPanel.AnnunciatorsOf(status);
        if (!JetStoresPanel.AnyAnnunciator(set)) return;
        float cursor = x;
        float gap = 6f * scale;
        if ((set & JetStoresPanel.Annunciators.Eye) != 0)
            cursor = TextRun(cursor, y, scale, "EYE", _painter.Dim, ref cursor) + gap;
        if ((set & JetStoresPanel.Annunciators.Cloak) != 0)
        {
            string word = status.CloakDrained ? "DRAINED" : "CLK";
            cursor = TextRun(cursor, y, scale, word, status.CloakDrained ? _painter.Hot : _painter.Dim, ref cursor) + gap;
        }
        if ((set & JetStoresPanel.Annunciators.Autopilot) != 0)
            cursor = TextRun(cursor, y, scale, "AUTO", _painter.Dim, ref cursor) + gap;
        if ((set & JetStoresPanel.Annunciators.VectorLock) != 0)
            cursor = TextRun(cursor, y, scale, "VLOCK", _painter.Dim, ref cursor) + gap;
        if ((set & JetStoresPanel.Annunciators.Flag) != 0)
            cursor = TextRun(cursor, y, scale, "FLAG", _painter.Primary, ref cursor) + gap;
        if ((set & JetStoresPanel.Annunciators.Artifact) != 0)
            TextRun(cursor, y, scale, "ART", _painter.Primary, ref cursor);
    }

    /// <summary>One annunciator: a small shape glyph plus its word. Returns the cursor
    /// after the word.</summary>
    private float AnnunciatorGlyph(float x, float y, float scale,
        JetStoresPanel.Annunciators which, string word, Color ink)
    {
        float size = 6f * scale;
        float mid = y + 6f * scale;
        float stroke = _painter.Stroke;
        switch (which)
        {
            case JetStoresPanel.Annunciators.Eye:
                _painter.Arc(new(x + size, mid), size, -MathF.PI * 0.5f, MathF.PI, _painter.Hairline, ink);
                _painter.Line(new(x + size * 0.6f, mid), new(x + size * 1.4f, mid), _painter.Hairline, ink);
                break;
            case JetStoresPanel.Annunciators.Cloak:
                _painter.Line(new(x, mid), new(x + size * 0.5f, y), stroke, ink);
                _painter.Line(new(x + size * 0.5f, y), new(x + size, mid), stroke, ink);
                break;
            case JetStoresPanel.Annunciators.Autopilot:
                _painter.Line(new(x, mid + size * 0.4f), new(x + size, mid + size * 0.4f), _painter.Hairline, ink);
                _painter.Line(new(x + size * 0.5f, mid + size * 0.4f), new(x + size * 0.5f, y), _painter.Hairline, ink);
                break;
            case JetStoresPanel.Annunciators.VectorLock:
                _painter.Line(new(x, y), new(x + size, y), _painter.Hairline, ink);
                _painter.Line(new(x + size, y), new(x + size, y + size), _painter.Hairline, ink);
                break;
            case JetStoresPanel.Annunciators.Flag:
                _painter.Line(new(x, y), new(x, y + size), stroke, ink);
                _painter.Line(new(x, y), new(x + size, y + size * 0.4f), stroke, ink);
                _painter.Line(new(x + size, y + size * 0.4f), new(x, y + size * 0.7f), stroke, ink);
                break;
            default:
                _painter.Line(new(x + size * 0.5f, y), new(x + size, mid), _painter.Hairline, ink);
                _painter.Line(new(x + size, mid), new(x + size * 0.5f, y + size), _painter.Hairline, ink);
                _painter.Line(new(x + size * 0.5f, y + size), new(x, mid), _painter.Hairline, ink);
                _painter.Line(new(x, mid), new(x + size * 0.5f, y), _painter.Hairline, ink);
                break;
        }
        float textX = x + size + 4f * scale;
        _painter.Text(word, new(textX, y), large: false, scale, ink);
        return textX + _painter.Measure(word, large: false, scale).X;
    }

    // ── master caution panel (P972 §4.4, §5.5) ──────────────────────────
    /// <summary>
    /// One panel that aggregates only existing warnings, in the P972 §4.4 priority order:
    /// missile track / damage / pod oxygen, then critical (and low) resources, then
    /// boundary, crowd, ghost, critical tips, ripcord and the lives/kills/vote badges. The
    /// lines are read from the shared model every frame; the panel never acknowledges,
    /// suppresses, delays or retriggers a warning and owns no timer or sound.
    /// </summary>
    private void DrawCaution(HudModel model, float w, float h, float scale)
    {
        if (!model.FlightVisible) return;
        int count = JetCautionModel.Build(model, _cautionBuffer);
        if (count == 0) return;
        bool compact = JetStoresPanel.Compact(h);
        int limit = JetCautionModel.LineLimit(compact);
        if (count > limit) count = limit;

        float right = JetCautionModel.Right(w, scale);
        // The line pitch can never be shorter than a glyph line, or rows would overlap.
        float textHeight = _painter.Measure("X", large: false, scale).Y;
        float pitch = MathF.Max(JetCautionModel.LinePitch * scale, textHeight + 3f * scale);

        // Measure first, then draw a plate behind the panel so it reads over bright space.
        var headerSize = _painter.Measure(CautionHeader, large: false, scale);
        float widest = headerSize.X;
        float[] widths = _cautionWidths;
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            float width = MeasureCaution(_cautionBuffer[i], i, model, scale);
            widths[i] = width;
            if (width > widest) widest = width;
            drawn++;
        }
        float pad = 6f * scale;
        float panelHeight = headerSize.Y + 2f * scale + drawn * pitch + pad * 2f;
        // The panel hangs from the top-right but can never reach the throttle tape's own
        // band (its top is cy − halfSpan; the THR caption sits just above that).
        float throttleBandTop = h * 0.2f - textHeight - 2f * scale;
        float top = MathF.Max(2f, throttleBandTop - panelHeight);
        var panel = new RectangleF(right - widest - pad * 2f, top, widest + pad * 2f, panelHeight);
        _painter.FillPlate(panel, 0.62f);
        _painter.Text(CautionHeader, new(panel.X + pad, panel.Y + pad), large: false, scale, _painter.Primary);

        float y = panel.Y + pad + headerSize.Y + 2f * scale;
        for (int i = 0; i < drawn; i++)
        {
            JetCaution caution = _cautionBuffer[i];
            float textWidth = widths[i] - CautionGlyphWidth(scale);
            DrawCautionGlyph(new(right - widths[i], y), scale, caution.Hot);
            DrawCautionText(caution, i, new(right - textWidth, y), scale);
            y += pitch;
        }
    }

    private readonly float[] _cautionWidths = new float[JetCautionModel.MaxCautions];

    private static float CautionGlyphWidth(float scale) => 10f * scale;

    /// <summary>The width of one caution line: glyph + text, measured without allocating
    /// (the composed lines are cached by kind and value).</summary>
    private float MeasureCaution(in JetCaution caution, int line, HudModel model, float scale)
    {
        string text = CautionText(caution, line);
        return CautionGlyphWidth(scale) + _painter.Measure(text, large: false, scale).X;
    }

    /// <summary>
    /// The words of one caution line. Numeric values are composed into a cached string (a
    /// steady frame allocates nothing); pass-through text (a critical tip, the vote) is the
    /// model's own string, never rebuilt.
    /// </summary>
    private string CautionText(in JetCaution caution, int line)
    {
        switch (caution.Kind)
        {
            case JetCautionKind.MissileTrack:
                return CachedLine(11, PercentOf(caution.Fraction), "MSL TRACK ", "%");
            case JetCautionKind.Damage:
                return "DAMAGE";
            case JetCautionKind.PodOxygen:
                return CachedLine(12, caution.Seconds ?? 0, "POD OXY ", " s");
            case JetCautionKind.CriticalResource:
            case JetCautionKind.LowResource:
                return CachedLine(13 + ResourceSlot(caution.Word), PercentOf(caution.Fraction),
                    caution.Word ?? "", "%");
            case JetCautionKind.Boundary:
                return "BOUNDARY";
            case JetCautionKind.Crowd:
                return "CROWD";
            case JetCautionKind.Ghost:
                return "PILOT DOWN / GHOST";
            case JetCautionKind.CriticalTip:
                return caution.Text ?? "";
            case JetCautionKind.Ripcord:
                return CachedLine(18, caution.Seconds ?? 0, "RIP ", "");
            case JetCautionKind.Lives:
                return CachedLine(19, caution.Count ?? 0, "LIVES ", "");
            case JetCautionKind.Kills:
                return CachedLine(20, caution.Count ?? 0, "KILLS ", "");
            case JetCautionKind.Vote:
                return caution.Text ?? "VOTE";
            default:
                _ = line;
                return "";
        }
    }

    /// <summary>A stable cache slot per store word (a critical line keeps its own slot, so
    /// two different resources cannot thrash one composed string).</summary>
    private static int ResourceSlot(string? word) => word switch
    {
        "HULL" => 0,
        "SHLD" => 1,
        "ENG" => 2,
        "OXY" => 3,
        "FUEL" => 4,
        _ => 5,
    };

    /// <summary><c>word + value + suffix</c>, rebuilt only when the value (or the word that
    /// owns this slot) changes — the one allocation a caution line may ever make.</summary>
    private string CachedLine(int slot, int value, string word, string suffix)
    {
        ref IntCache cell = ref _storeCache[slot];
        if (cell.Key == value && cell.Text is not null) return cell.Text;
        cell.Key = value;
        cell.Text = word.Length == 0
            ? value.ToString(CultureInfo.InvariantCulture) + suffix
            : word + " " + value.ToString(CultureInfo.InvariantCulture) + suffix;
        return cell.Text;
    }

    private static int PercentOf(float? fraction)
        => (int)MathF.Round(Math.Clamp(fraction ?? 0f, 0f, 1f) * 100f);

    private void DrawCautionText(in JetCaution caution, int line, Vector2 at, float scale)
    {
        Color ink = caution.Hot ? _painter.Hot : _painter.Dim;
        _painter.Text(CautionText(caution, line), at, large: false, scale, ink);
    }

    /// <summary>The caution's icon: a hot "!" mark or a quiet open square — shape, never a
    /// second hue (P972 §4.1).</summary>
    private void DrawCautionGlyph(Vector2 at, float scale, bool hot)
    {
        float size = 6f * scale;
        if (hot)
        {
            _painter.Line(new(at.X + size * 0.5f, at.Y), new(at.X + size * 0.5f, at.Y + size * 0.7f),
                _painter.Stroke, _painter.Hot);
            _painter.Surface.Fill(new(at.X + size * 0.35f, at.Y + size * 0.9f),
                new(size * 0.3f, size * 0.3f), _painter.Hot);
            return;
        }
        _painter.Line(new(at.X, at.Y + size * 0.1f), new(at.X + size * 0.7f, at.Y + size * 0.1f),
            _painter.Hairline, _painter.Dim);
        _painter.Line(new(at.X + size * 0.7f, at.Y + size * 0.1f), new(at.X + size * 0.7f, at.Y + size * 0.9f),
            _painter.Hairline, _painter.Dim);
        _painter.Line(new(at.X + size * 0.7f, at.Y + size * 0.9f), new(at.X, at.Y + size * 0.9f),
            _painter.Hairline, _painter.Dim);
        _painter.Line(new(at.X, at.Y + size * 0.9f), new(at.X, at.Y + size * 0.1f),
            _painter.Hairline, _painter.Dim);
    }

    // ── F4 inventory pane, Jet presentation (P972 §4.3, §5.6) ───────────
    /// <summary>
    /// The full native inventory pane as a Jet vector table: the same model
    /// (<see cref="HudInventoryPane"/>), the same anchor and clamp
    /// (<see cref="InventoryPaneLayout"/>), the same 0.5 s slide progress
    /// (<see cref="HudContext.InventorySlide"/>), the same open/close gate and the same
    /// keys — only the glyphs change. Every row type carries its shared columns: SLOT/KEY/
    /// PART, selected/active by shape, QTY and the empty <c>A</c>, MOUNT/ARM progress and
    /// the launcher READY state, and the split RNG/DMG/ROF · SHD STR/RECH · AB SPD/END ·
    /// CLK STR/END stats. Cargo rows keep the shared order/amounts/S key, a gunner sees the
    /// carrier's loadout title, and an empty mount keeps its open outline and EMPTY word.
    /// </summary>
    private void DrawInventory(HudModel model, float w, float h, float scale)
    {
        if (!model.FlightVisible) return;
        if (!model.Context.InventoryVisible && model.Context.InventorySlide <= 0f) return;
        if (model.Context.InventoryPane is not { } pane) return;
        int rows = pane.Rows.Count;
        if (rows == 0) return;

        float slide = model.Context.InventorySlide;
        float paneScale = JetInventoryPresentation.Scale(h);
        float left = JetInventoryPresentation.Left(w, h);
        float top = JetInventoryPresentation.Top(rows, w, h, slide);
        float width = JetInventoryPresentation.Width(w, h);
        float height = JetInventoryPresentation.Height(rows, h);
        float cap = JetInventoryPresentation.CapHeight * paneScale;
        float rowH = JetInventoryPresentation.RowHeight * paneScale;

        // Panel: a plate with a keyline frame (the native pane's dark line background).
        var panel = new RectangleF(left, top, width, height);
        _painter.FillPlate(panel, 0.78f);

        // Header: own-ship / hull title centred in the top cap, with the column caption in
        // the bottom cap (the native ShipNameImage owns the top cap, dialog.mdl:1803-1807).
        if (pane.ShipName.Length > 0)
        {
            Vector2 nameSize = _painter.Measure(pane.ShipName, large: false, paneScale);
            _painter.Text(pane.ShipName, new(left + (width - nameSize.X) * 0.5f,
                top + (cap - nameSize.Y) * 0.5f), large: false, paneScale, _painter.Primary);
        }
        Vector2 headerSize = _painter.Measure(InventoryHeader, large: false, paneScale);
        _painter.Text(InventoryHeader, new(left + JetInventoryPresentation.KeyColumn * paneScale,
            top + height - cap + (cap - headerSize.Y) * 0.5f), large: false, paneScale, _painter.Dim);

        for (int i = 0; i < rows; i++)
        {
            HudInventoryRow row = pane.Rows[i];
            float rowTop = top + cap + rowH * i;
            float textY = rowTop + (rowH - 12f * paneScale) * 0.5f;
            DrawInventoryRow(row, i, left, textY, rowTop, rowH, paneScale, width);
        }
    }

    private const string InventoryHeader = "SLOT KEY PART            QTY  STATE    STATS";

    private void DrawInventoryRow(in HudInventoryRow row, int index, float left, float textY,
        float rowTop, float rowH, float paneScale, float width)
    {
        // The row text uses the table font (a fraction of the pane scale) so the §5.6 stat
        // columns fit the native anchor; the title keeps the full pane scale.
        float rowScale = paneScale * JetInventoryPresentation.RowTextScale;
        float Column(float nativeUnits) => left + nativeUnits * paneScale;

        if (row.Kind == HudInventoryRowKind.Observers)
        {
            // Native invobserver.mdl: "Observers: N" in the key column, no icon, no key.
            _painter.Text(row.Name, new(Column(JetInventoryPresentation.KeyColumn), textY),
                large: false, rowScale, _painter.Primary);
            return;
        }

        // Selected / active markers: an open box for selected, a solid box for active.
        float markerX = Column(JetInventoryPresentation.MarkerColumn);
        float markerY = textY + 1f * rowScale;
        float markerSize = 5f * rowScale;
        if (row.Selected)
        {
            _painter.Line(new(markerX, markerY), new(markerX + markerSize, markerY), _painter.Hairline, _painter.Primary);
            _painter.Line(new(markerX + markerSize, markerY), new(markerX + markerSize, markerY + markerSize),
                _painter.Hairline, _painter.Primary);
            _painter.Line(new(markerX + markerSize, markerY + markerSize), new(markerX, markerY + markerSize),
                _painter.Hairline, _painter.Primary);
            _painter.Line(new(markerX, markerY + markerSize), new(markerX, markerY), _painter.Hairline, _painter.Primary);
        }
        if (row.Active)
            _painter.Surface.Fill(new(markerX + markerSize + 3f * rowScale, markerY),
                new(markerSize, markerSize), _painter.Hot);

        // Key.
        if (row.Key.Length > 0)
            _painter.Text(row.Key, new(Column(JetInventoryPresentation.KeyColumn), textY),
                large: false, rowScale, row.Active ? _painter.Primary : _painter.Dim);

        // Part name (the selected row is brighter — never the only cue), measured-fit to
        // the slot before the quantity column (native's own measured-fit rule).
        Color nameInk = row.IsEmpty ? _painter.Dim : row.Selected ? _painter.Primary : _painter.Dim;
        float nameWidth = _painter.Measure(row.Name, large: false, rowScale).X;
        float nameSlot = (JetInventoryPresentation.QuantityColumn - JetInventoryPresentation.NameColumn - 4f) * paneScale;
        float nameFit = nameWidth <= nameSlot || nameWidth <= 0f ? 1f : nameSlot / nameWidth;
        _painter.Text(row.Name, new(Column(JetInventoryPresentation.NameColumn), textY),
            large: false, rowScale * nameFit, nameInk);

        // Empty-mount affordance: an open dashed outline around the row plus the EMPTY
        // state word (a shape, never colour alone).
        if (row.IsEmpty)
        {
            float x0 = Column(3f), x1 = left + width - 3f * paneScale;
            float y0 = rowTop + 1f * paneScale, y1 = rowTop + rowH - 1f * paneScale;
            DashedLine(new(x0, y0), new(x1, y0), _painter.Hairline, _painter.Dim);
            DashedLine(new(x0, y1), new(x1, y1), _painter.Hairline, _painter.Dim);
        }

        // Quantity and the native out-of-ammo A cue.
        float quantityX = Column(JetInventoryPresentation.QuantityColumn);
        if (row.Quantity is { } quantity)
        {
            int rounded = (int)MathF.Round(quantity);
            _painter.Text(CachedQuantity(index, rounded), new(quantityX, textY),
                large: false, rowScale, _painter.Dim);
        }
        if (row.OutOfAmmo)
            _painter.Text("A", new(quantityX + 30f * paneScale, textY), large: false, rowScale, _painter.Hot);

        // State: MOUNT/ARM with a segmented progress bar, READY for an armed launcher.
        float stateX = Column(JetInventoryPresentation.StateColumn);
        if (JetInventoryPresentation.StateWord(row, out float? progress) is { } state)
        {
            _painter.Text(state, new(stateX, textY), large: false, rowScale,
                row.ReadyState is 1 or 2 ? _painter.Dim : _painter.Primary);
            if (progress is { } fraction)
                SegmentedBar(stateX, rowTop + rowH * 0.62f,
                    JetInventoryPresentation.ProgressBarWidth * paneScale, fraction, _painter.Primary);
        }

        // Stats columns: the row's own StatsText split into the Jet vocabulary. A run is
        // drawn only while it fits the pane: at 800×480 the tail of a long stat stack is the
        // one abbreviation the compact pane makes (the value stays in the shared model).
        float statsX = Column(JetInventoryPresentation.StatsColumn);
        float cursor = statsX;
        float limit = left + width - 4f * paneScale;
        int runs = CachedStats(index, row);
        for (int i = 0; i < runs; i++)
        {
            JetInventoryPresentation.StatRun run = _statRuns[index][i];
            float labelWidth = _painter.Measure(run.Label, large: false, rowScale).X;
            float valueWidth = _painter.Measure(run.Value, large: false, rowScale).X;
            if (cursor + labelWidth + valueWidth + 3f * rowScale > limit) break;
            _painter.Text(run.Label, new(cursor, textY), large: false, rowScale, _painter.Dim);
            cursor += labelWidth + 3f * rowScale;
            _painter.Text(run.Value, new(cursor, textY), large: false, rowScale, _painter.Primary);
            cursor += valueWidth + 7f * rowScale;
        }
    }

    /// <summary>A cached quantity readout per row slot.</summary>
    private string CachedQuantity(int slot, int value)
    {
        ref IntCache cell = ref _quantityCache[slot % _quantityCache.Length];
        if (cell.Key == value && cell.Text is not null) return cell.Text;
        cell.Key = value;
        cell.Text = value.ToString(CultureInfo.InvariantCulture);
        return cell.Text;
    }

    /// <summary>Split a row's shared stats string once per (kind, text) pair: the native
    /// builder allocates a fresh but equal string every frame, so the cache compares
    /// content, never references.</summary>
    private int CachedStats(int slot, in HudInventoryRow row)
    {
        int index = slot % _statRuns.Length;
        if (_statKeys[index] is { } cached && _statKinds[index] == row.Kind
            && string.Equals(cached, row.StatsText, StringComparison.Ordinal))
            return _statRuns[index].Length;

        _statKeys[index] = row.StatsText;
        _statKinds[index] = row.Kind;
        _statRuns[index] ??= new JetInventoryPresentation.StatRun[3];
        int count = JetInventoryPresentation.StatsRuns(row, _statRuns[index]);
        if (count != _statRuns[index].Length)
        {
            var exact = new JetInventoryPresentation.StatRun[count];
            Array.Copy(_statRuns[index], exact, count);
            _statRuns[index] = exact;
        }
        return count;
    }
}
