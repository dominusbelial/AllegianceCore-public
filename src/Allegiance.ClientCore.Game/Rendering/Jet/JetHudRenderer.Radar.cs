using System;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1039 — the Jet radar renderer (P972 §4.4 RWR paragraph, §4.5 compact, §5.4 all 27 radar
/// rows, phase slice 3C). One scope for the SAME native-radar-allowed contact snapshot the
/// shared model already carries: square tactical scope when the shared round-radar option is
/// off, circular RWR when it is on (<see cref="JetRadar"/>).
/// <para><b>Every reading is shared data.</b> Contact presence comes from the model's own
/// list (already reduced by <see cref="RadarLod"/>); the label payload comes from
/// <see cref="RadarLabelModel.Resolve"/> — that is, the native per-kind LOD label tier
/// (radarimage.cpp:703-961,1199-1441) — and the overlays from the contact's own flags
/// (selected / accepted / queued order / subject / threat / flag / artifact / ripcord /
/// nearest enemy / highlight / detection eye). The radar itself never adds a contact, never
/// widens a tier and never identifies who locked the pilot.</para>
/// <para><b>State without colour.</b> Every state is carried by SHAPE, line pattern, an
/// attached word (<c>ACC</c>, <c>QUE</c>, <c>SUB</c>, <c>RIP n</c>, <c>M</c>, <c>P</c>,
/// <c>T</c>, <c>!</c>) or a filled/hatched bar, so nothing depends on the single Jet hue.
/// </para>
/// <para><b>0 B/frame.</b> The contact loop is index-based, every string it draws is a
/// literal, a cached label-model string or the model's own string, and the range labels come
/// from <see cref="RadarLabelModel.RangeText"/>'s cache.</para>
/// </summary>
public sealed partial class JetHudRenderer
{
    /// <summary>The scope's LOD label (<c>RDR All|Default|Target|None</c>), the same four
    /// modes and label native's <c>c_szRadarLODs</c> prints (radarimage.cpp:2-5).</summary>
    private const string RadarCaption = "RDR ";

    /// <summary>Command verbs and overlay words (ASCII only: the HUD fonts bake 32…126).</summary>
    private const string SubjectWord = "SUB";
    private const string RipWord = "RIP ";
    private const string QueuedLabel = "QUE";
    private const string MissileGlyph = "M";
    private const string ProbeGlyph = "P";
    private const string TreasureGlyph = "T";
    private const string ThreatGlyph = "!";

    private string _radarCaptionText = RadarCaption + "Default";

    /// <summary>
    /// Draw the radar scope of this frame. Gated exactly like the rest of Jet's flight
    /// furniture (<see cref="HudModel.FlightVisible"/>); suppressed while the F4 inventory
    /// pane is present, because that modal table owns the same bottom-right band (the native
    /// inventory pane is drawn over the radar).
    /// </summary>
    private void DrawRadar(HudModel model, float w, float h, float scale)
    {
        if (!model.FlightVisible) return;
        if (model.Context.InventoryVisible || model.Context.InventorySlide > 0f) return;

        RectangleF scope = JetRadar.Scope(w, h, scale, _throttleBandLeft);
        if (scope.Width < 40f || scope.Height < 40f) return;
        bool round = _settings.RoundRadar;
        _radarScope = scope;
        // The scope is fixed furniture: no readout may be placed over the instrument.
        AddFurniture(scope);
        _painter.Element("radar");

        Vector2 centre = JetRadar.Centre(scope);
        Color frameInk = _painter.Dim;

        // ── frame: square tactical scope / circular RWR (the one shared option) ──
        if (round)
        {
            float radius = MathF.Min(scope.Width, scope.Height) * 0.5f;
            _painter.Circle(centre, radius, _painter.Hairline, frameInk);
            // Radial tick marks at the four axes: the RWR's own bearing reference.
            for (int i = 0; i < 4; i++)
            {
                float angle = i * MathHelper.PiOver2;
                Vector2 outer = new(centre.X + MathF.Cos(angle) * radius,
                    centre.Y + MathF.Sin(angle) * radius);
                Vector2 inner = new(centre.X + MathF.Cos(angle) * (radius - 6f * scale),
                    centre.Y + MathF.Sin(angle) * (radius - 6f * scale));
                _painter.Line(inner, outer, _painter.Stroke, _painter.Primary);
            }
        }
        else
        {
            _painter.CornerBrackets(scope, 14f * scale, _painter.Stroke, _painter.Primary);
            float x0 = scope.X, y0 = scope.Y, x1 = scope.X + scope.Width, y1 = scope.Y + scope.Height;
            _painter.Line(new(x0, y0), new(x1, y0), _painter.Hairline, frameInk);
            _painter.Line(new(x1, y0), new(x1, y1), _painter.Hairline, frameInk);
            _painter.Line(new(x1, y1), new(x0, y1), _painter.Hairline, frameInk);
            _painter.Line(new(x0, y1), new(x0, y0), _painter.Hairline, frameInk);
            // Axis cross: the bearing reference of the square scope.
            _painter.Line(new(centre.X - 5f * scale, centre.Y), new(centre.X + 5f * scale, centre.Y),
                _painter.Hairline, frameInk);
            _painter.Line(new(centre.X, centre.Y - 5f * scale), new(centre.X, centre.Y + 5f * scale),
                _painter.Hairline, frameInk);
        }

        // ── range bands ────────────────────────────────────────────────────
        float scopeRange = JetRadar.ScopeRange(model.Context.Contacts);
        var contacts = model.Context.Contacts;
        Vector2 size = _painter.Measure("00000", large: false, scale);
        for (int ring = 1; ring <= JetRadar.RingCount; ring++)
        {
            float radius = JetRadar.RingRadius(scope, ring, round);
            if (radius <= 0f) continue;
            if (round)
                _painter.Circle(centre, radius, _painter.Hairline, _painter.Dim * 0.55f);
            else
                RingSquare(centre, radius, _painter.Hairline, _painter.Dim * 0.55f);
            // The band's value on the vertical axis, above the ring (no invented unit).
            string text = RadarLabelModel.RangeText(JetRadar.RingRange(ring, scopeRange));
            Vector2 ink = _painter.Measure(text, large: false, scale);
            float labelY = centre.Y - radius - ink.Y - 1f * scale;
            if (labelY > scope.Y)
                _painter.Text(text, new(centre.X + 3f * scale, labelY), large: false, scale,
                    _painter.Dim * 0.75f);
        }

        // ── the mode label (same four modes, same words as native) ──────────
        if (!_radarCaptionText.EndsWith(RadarLod.Label(model.Context.RadarMode), StringComparison.Ordinal))
            _radarCaptionText = RadarCaption + RadarLod.Label(model.Context.RadarMode);
        // The mode word sits in the scope's bottom-right corner: the boresight-centred flight
        // symbology (the ballistic lead, the order words) never reaches that corner, so the
        // fixed caption cannot be drawn over a world-anchored word.
        Vector2 captionInk = _painter.Measure(_radarCaptionText, large: false, scale);
        _painter.Text(_radarCaptionText,
            new(scope.X + scope.Width - 4f * scale - captionInk.X, scope.Y + scope.Height - 3f * scale - captionInk.Y),
            large: false, scale, _painter.Dim);

        // ── contacts ───────────────────────────────────────────────────────
        bool commandCamera = model.Context.CommandView;
        RadarMode mode = model.Context.RadarMode;
        // P1039 — two passes. Pass A draws every icon, overlay and overlay WORD; pass B draws
        // every label block. A stroke drawn after a text would read as a strikethrough across
        // it (the audit's stroke-vs-text rule), so no contact glyph may be submitted after a
        // label has started — the whole glyph field is laid down first.
        for (int i = 0; i < contacts.Count; i++)
        {
            HudContact contact = contacts[i];
            Vector2 at = JetRadar.ContactPoint(scope, contact, _viewportWidth, _viewportHeight, scopeRange);
            DrawRadarGlyph(contact, at, scope, scale, commandCamera, mode);
        }
        // Pass A2: the glyph LETTERS (M/P/T/!/SUB/RIP nn/ACC/QUE) after every stroke, so no
        // contact's overlay line can be submitted over another contact's word.
        for (int i = 0; i < contacts.Count; i++)
        {
            HudContact contact = contacts[i];
            Vector2 at = JetRadar.ContactPoint(scope, contact, _viewportWidth, _viewportHeight, scopeRange);
            DrawRadarGlyphWords(contact, at, scope, scale);
        }
        for (int i = 0; i < contacts.Count; i++)
        {
            HudContact contact = contacts[i];
            Vector2 at = JetRadar.ContactPoint(scope, contact, _viewportWidth, _viewportHeight, scopeRange);
            DrawRadarLabel(contact, at, scope, scale, commandCamera, mode);
        }
        _ = size;
        _ = captionInk;
    }

    /// <summary>A square range ring (the square scope's band; the round scope draws circles).</summary>
    private void RingSquare(Vector2 centre, float radius, float width, Color ink)
    {
        float x0 = centre.X - radius, x1 = centre.X + radius;
        float y0 = centre.Y - radius, y1 = centre.Y + radius;
        _painter.Line(new(x0, y0), new(x1, y0), width, ink);
        _painter.Line(new(x1, y0), new(x1, y1), width, ink);
        _painter.Line(new(x1, y1), new(x0, y1), width, ink);
        _painter.Line(new(x0, y1), new(x0, y0), width, ink);
    }

    /// <summary>
    /// One contact: its kind glyph, its relationship pattern, every overlay the shared contact
    /// flags carry, and the native-LOD label block (name / range / command verb / ripcord /
    /// hull-shield bars / ore fill) clipped to the scope.
    /// </summary>
    private void DrawRadarGlyph(in HudContact contact, Vector2 at, RectangleF scope,
        float scale, bool commandCamera, RadarMode mode)
    {
        if (at.X < scope.X || at.Y < scope.Y || at.X > scope.X + scope.Width || at.Y > scope.Y + scope.Height)
            return;
        float unit = 4.5f * scale;
        Color ink = contact.Threat ? _painter.Hot : contact.ThreatFlash ? _painter.Hot : _painter.Primary;
        Color dim = _painter.Dim;
        float stroke = _painter.Stroke;

        // ── kind glyph (P972 §5.4 rows 2-9: distinct shape per contact class) ──
        switch (contact.Kind)
        {
            case HudContactKind.Ship:
                ShipGlyph(at, unit, ink, contact.IsEnemy, contact.IsSameSide, contact.IsSelf, stroke);
                break;
            case HudContactKind.Station:
                // Station square with a centre tick.
                SquareGlyph(at, unit, ink, stroke, closed: true);
                _painter.Line(new(at.X - unit * 0.4f, at.Y), new(at.X + unit * 0.4f, at.Y), _painter.Hairline, ink);
                break;
            case HudContactKind.Warp:
                // Aleph: an open circle with four radial ticks.
                _painter.Circle(at, unit, stroke, ink);
                for (int i = 0; i < 4; i++)
                {
                    float angle = i * MathHelper.PiOver2;
                    _painter.Line(new(at.X + MathF.Cos(angle) * unit, at.Y + MathF.Sin(angle) * unit),
                        new(at.X + MathF.Cos(angle) * (unit + 3f * scale), at.Y + MathF.Sin(angle) * (unit + 3f * scale)),
                        _painter.Hairline, ink);
                }
                break;
            case HudContactKind.Asteroid:
                RockGlyph(at, unit, ink, stroke);
                break;
            case HudContactKind.Missile:
                DartGlyph(at, unit, ink, stroke);
                break;
            case HudContactKind.Probe:
                _painter.Line(new(at.X - unit, at.Y), new(at.X + unit, at.Y), stroke, ink);
                _painter.Line(new(at.X, at.Y - unit), new(at.X, at.Y + unit), stroke, ink);
                break;
            case HudContactKind.Mine:
                DiamondGlyph(at, unit * 0.9f, ink, stroke);
                break;
            case HudContactKind.Treasure:
                DiamondGlyph(at, unit * 0.9f, ink, stroke);
                break;
            default: // Tech: a double diamond (distinct from a single treasure diamond).
                DiamondGlyph(at, unit, ink, stroke);
                DiamondGlyph(at, unit * 0.55f, ink, stroke);
                break;
        }

        // ── overlays: selected / accepted / queued / subject / threat / flag / artifact ──
        float bracket = unit + 4f * scale;
        var box = new RectangleF(at.X - bracket, at.Y - bracket, bracket * 2f, bracket * 2f);
        if (contact.Selected)
            _painter.CornerBrackets(box, bracket * 0.45f, _painter.Stroke, _painter.Hot);
        if (contact.AcceptedOrder)
        {
            _painter.CornerBrackets(box, bracket * 0.5f, _painter.Stroke, _painter.Primary);
        }
        if (contact.QueuedOrder)
            DashedBracket(box, bracket * 0.5f, scale);
        if (contact.NearestEnemy)
        {
            // Double bracket: the nearest-enemy tier (a shape, not a colour).
            var outer = new RectangleF(box.X - 2f * scale, box.Y - 2f * scale,
                box.Width + 4f * scale, box.Height + 4f * scale);
            _painter.CornerBrackets(outer, bracket * 0.35f, _painter.Hairline, _painter.Hot);
        }
        if (contact.Subject)
            SquareGlyph(at, bracket * 0.75f, _painter.Dim, _painter.Hairline, closed: true);
        if (contact.ThreatFlash)
        {
            // Hot dashed flash + the "!" mark (existing threat timing, shape + word).
            DashedCircle(at, bracket, scale);
        }
        if (contact.CarriedFlag)
        {
            Vector2 pole = new(at.X + bracket + 2f * scale, at.Y - bracket);
            _painter.Line(pole, new(pole.X, pole.Y + 8f * scale), _painter.Stroke, _painter.Primary);
            _painter.Line(pole, new(pole.X + 5f * scale, pole.Y + 2f * scale), _painter.Stroke, _painter.Primary);
            _painter.Line(new(pole.X + 5f * scale, pole.Y + 2f * scale), new(pole.X, pole.Y + 4f * scale),
                _painter.Stroke, _painter.Primary);
        }
        if (contact.CarriedArtifact)
            DiamondGlyph(new(at.X + bracket + 5f * scale, at.Y - bracket + 4f * scale), 3f * scale,
                _painter.Primary, _painter.Hairline);
        if (contact.Ripcord)
        {
            float len = bracket + 5f * scale;
            _painter.Line(new(at.X, at.Y), new(at.X, at.Y - len), _painter.Hairline, _painter.Dim);

        }

    }

    /// <summary>
    /// The LETTERS of one contact's markers — the class glyph (M/P/T), the subject square's
    /// word, the threat flash's "!", the ripcord countdown and the command verb. Drawn after
    /// every stroke of the frame so an overlay line can never run across another marker's
    /// word (the audit's stroke-vs-text rule), and before the label blocks for the same
    /// reason.
    /// </summary>
    private void DrawRadarGlyphWords(in HudContact contact, Vector2 at, RectangleF scope, float scale)
    {
        if (at.X < scope.X || at.Y < scope.Y || at.X > scope.X + scope.Width || at.Y > scope.Y + scope.Height)
            return;
        float unit = 4.5f * scale;
        float bracket = unit + 4f * scale;
        float rowH = _painter.Measure("X", large: false, scale).Y;
        Color ink = contact.Threat || contact.ThreatFlash ? _painter.Hot : _painter.Primary;
        switch (contact.Kind)
        {
            case HudContactKind.Missile:
                _painter.Text(MissileGlyph, new(at.X + unit + 2f * scale, at.Y - unit * 0.6f),
                    large: false, scale, ink);
                break;
            case HudContactKind.Probe:
                _painter.Text(ProbeGlyph, new(at.X + unit + 2f * scale, at.Y - unit * 0.6f),
                    large: false, scale, ink);
                break;
            case HudContactKind.Treasure:
                _painter.Text(TreasureGlyph, new(at.X + unit + 2f * scale, at.Y - unit * 0.6f),
                    large: false, scale, ink);
                break;
        }
        if (contact.Subject)
        {
            _painter.Text(SubjectWord, new(at.X + bracket + 2f * scale, at.Y - bracket),
                large: false, scale, _painter.Dim);
        }
        if (contact.ThreatFlash)
        {
            _painter.Text(ThreatGlyph, new(at.X - 2f * scale, at.Y - bracket - 12f * scale),
                large: false, scale, _painter.Hot);
        }
        if (contact.Ripcord && contact.RipcordSeconds is > 0)
        {
            float len = bracket + 5f * scale;
            // Cached by the countdown value so a steady frame formats nothing.
            int seconds = contact.RipcordSeconds.Value;
            if (seconds != _ripShown)
            {
                _ripShown = seconds;
                _ripText = RipWord + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            _painter.Text(_ripText, new(at.X + 2f * scale, at.Y - len - 10f * scale),
                large: false, scale, _painter.Dim);
        }
        if (contact.CommandId is not null)
        {
            string verb = contact.CommandAccepted ? AcceptedLabel : QueuedLabel;
            float verbY = MathF.Max(scope.Y + 1f * scale, at.Y - bracket - rowH);
            _painter.Text(verb, new(at.X - bracket, verbY),
                large: false, scale, contact.CommandAccepted ? _painter.Primary : _painter.Dim);
        }
    }

    /// <summary>
    /// One contact's label block: the native per-kind LOD tier
    /// (<see cref="RadarLabelModel.Resolve"/>) drawn monochrome, faded by the shared
    /// level-driven range fade, and CLIPPED to the scope's inner rectangle. A row that does
    /// not fit is dropped rather than spilling over the tapes or the scope frame (native
    /// clips its radar labels at the image edge the same way).
    /// </summary>
    private void DrawRadarLabel(in HudContact contact, Vector2 at, RectangleF scope,
        float scale, bool commandCamera, RadarMode mode)
    {
        RadarLabel? label = RadarLabelModel.Resolve(contact, mode, commandCamera);
        if (label is not { } payload) return;

        // Range fade mapped to monochrome OPACITY (the level-driven fade, no sensor rule).
        HudLevelPreset preset = HudLevels.Preset(_settings.EffectiveLevel);
        float alpha = RadarLabelDeclutter.Fade(RadarLabelDeclutter.Distance(contact),
            preset.FadeNear, preset.FadeFar, 0.35f);
        Color ink = (contact.Highlight ? _painter.Hot : _painter.Primary)
            * Math.Clamp(alpha, 0.25f, 1f);

        float rowH = _painter.Measure("X", large: false, scale).Y + 1f * scale;
        float innerLeft = scope.X + 2f * scale;
        float innerRight = scope.X + scope.Width - 2f * scale;
        float innerTop = scope.Y + 2f * scale;
        float innerBottom = scope.Y + scope.Height - 2f * scale;
        float maxWidth = MathF.Max(0f, innerRight - innerLeft);
        float barW = 26f * scale, barH = 1.8f * scale;
        bool hasBars = payload.Hull is not null || payload.Shield is not null;
        bool hasFill = payload.Fill is not null;

        string? name = payload.Name;
        if (name is not null && _painter.Measure(name, large: false, scale).X > maxWidth) name = null;
        // The metre line comes from the label model's own cache (one string per metre value,
        // never one per contact per frame).
        string? range = contact.Range is > 0 ? RadarLabelModel.RangeLineText(contact.Range.Value) : null;
        if (range is not null && _painter.Measure(range, large: false, scale).X > maxWidth) range = null;
        string? eye = payload.Eye ? "EYE" : null;

        float width = 0f;
        if (name is not null) width = MathF.Max(width, _painter.Measure(name, large: false, scale).X);
        if (range is not null) width = MathF.Max(width, _painter.Measure(range, large: false, scale).X);
        if (eye is not null) width = MathF.Max(width, _painter.Measure(eye, large: false, scale).X);
        if (hasBars || hasFill) width = MathF.Max(width, barW);
        if (width <= 0f) return;

        int rows = (name is null ? 0 : 1) + (range is null ? 0 : 1) + (eye is null ? 0 : 1);
        float barsHeight = (hasBars ? barH * 2f + 2f * scale : 0f) + (hasFill ? barH + 2f * scale : 0f);
        float blockHeight = rows * rowH + barsHeight;
        if (blockHeight > innerBottom - innerTop) return;   // does not fit the instrument

        float x = at.X + 4.5f * scale + 3f * scale;
        if (x + width > innerRight) x = at.X - 4.5f * scale - 3f * scale - width;
        x = Math.Clamp(x, innerLeft, MathF.Max(innerLeft, innerRight - width));
        float y = Math.Clamp(at.Y - rowH, innerTop, MathF.Max(innerTop, innerBottom - blockHeight));

        if (name is not null) { _painter.Text(name, new(x, y), large: false, scale, ink); y += rowH; }
        if (range is not null) { _painter.Text(range, new(x, y), large: false, scale, ink); y += rowH; }
        if (payload.Hull is { } hull) { RadarBar(x, y, barW, barH, hull, ink, scale); y += barH + 2f * scale; }
        if (payload.Shield is { } shield) { RadarBar(x, y, barW, barH, shield, ink, scale); y += barH + 2f * scale; }
        if (payload.Fill is { } fill) { RadarBar(x, y, barW, barH, fill, ink, scale); y += barH + 3f * scale; }
        if (eye is not null) _painter.Text(eye, new(x, y), large: false, scale, ink);
    }

    /// <summary>The <c>"nnnn m"</c> range line of a label, cached by the label model's own
    /// metre string so a steady frame formats nothing.</summary>
    private string RadarRangeLine(string metres)
    {
        if (!string.Equals(_radarRangeSource, metres, StringComparison.Ordinal))
        {
            _radarRangeSource = metres;
            _radarRangeText = metres + " m";
        }
        return _radarRangeText;
    }

    private string _radarRangeSource = "";
    private int _ripShown = int.MinValue;
    private string _ripText = RipWord;
    private string _radarRangeText = "";

    /// <summary>A ship triangle: hostile = closed with an inward tick, friendly = dashed,
    /// own ship = a double triangle at the scope centre (shape + pattern, never a hue).</summary>
    private void ShipGlyph(Vector2 at, float unit, Color ink, bool enemy, bool sameSide, bool self, float stroke)
    {
        Vector2 nose = new(at.X, at.Y - unit);
        Vector2 left = new(at.X - unit * 0.9f, at.Y + unit * 0.7f);
        Vector2 right = new(at.X + unit * 0.9f, at.Y + unit * 0.7f);
        if (sameSide || self)
        {
            if (self)
            {
                _painter.Line(nose, left, stroke, ink);
                _painter.Line(left, right, stroke, ink);
                _painter.Line(right, nose, stroke, ink);
                _painter.Line(new(at.X, at.Y - unit * 0.5f), new(at.X - unit * 0.45f, at.Y + unit * 0.35f),
                    _painter.Hairline, ink);
                _painter.Line(new(at.X, at.Y - unit * 0.5f), new(at.X + unit * 0.45f, at.Y + unit * 0.35f),
                    _painter.Hairline, ink);
                return;
            }
            // Friendly: dashed sides, no closed fill.
            DashedLine(nose, right, _painter.Hairline, ink);
            DashedLine(right, left, _painter.Hairline, ink);
            DashedLine(left, nose, _painter.Hairline, ink);
            return;
        }
        _painter.Line(nose, left, stroke, ink);
        _painter.Line(left, right, stroke, ink);
        _painter.Line(right, nose, stroke, ink);
        if (enemy)
        {
            float tick = unit * 0.45f;
            _painter.Line(new(at.X, at.Y + unit * 0.7f), new(at.X, at.Y + unit * 0.7f - tick),
                _painter.Hairline, ink);
        }
    }

    private void SquareGlyph(Vector2 at, float half, Color ink, float stroke, bool closed)
    {
        float x0 = at.X - half, x1 = at.X + half, y0 = at.Y - half, y1 = at.Y + half;
        if (closed)
        {
            _painter.Line(new(x0, y0), new(x1, y0), stroke, ink);
            _painter.Line(new(x1, y0), new(x1, y1), stroke, ink);
            _painter.Line(new(x1, y1), new(x0, y1), stroke, ink);
            _painter.Line(new(x0, y1), new(x0, y0), stroke, ink);
            return;
        }
        DashedLine(new(x0, y0), new(x1, y0), _painter.Hairline, ink);
        DashedLine(new(x1, y0), new(x1, y1), _painter.Hairline, ink);
        DashedLine(new(x1, y1), new(x0, y1), _painter.Hairline, ink);
        DashedLine(new(x0, y1), new(x0, y0), _painter.Hairline, ink);
    }

    /// <summary>A rock: a small open pentagon (never a station square).</summary>
    private void RockGlyph(Vector2 at, float unit, Color ink, float stroke)
    {
        for (int i = 0; i < 5; i++)
        {
            float a0 = i * MathHelper.TwoPi / 5f - MathHelper.PiOver2;
            float a1 = (i + 1) * MathHelper.TwoPi / 5f - MathHelper.PiOver2;
            _painter.Line(new(at.X + MathF.Cos(a0) * unit * 0.8f, at.Y + MathF.Sin(a0) * unit * 0.8f),
                new(at.X + MathF.Cos(a1) * unit * 0.8f, at.Y + MathF.Sin(a1) * unit * 0.8f),
                stroke, ink);
        }
    }

    /// <summary>A missile dart: a narrow triangle with a tail stroke.</summary>
    private void DartGlyph(Vector2 at, float unit, Color ink, float stroke)
    {
        Vector2 tip = new(at.X, at.Y - unit);
        Vector2 left = new(at.X - unit * 0.55f, at.Y + unit * 0.6f);
        Vector2 right = new(at.X + unit * 0.55f, at.Y + unit * 0.6f);
        _painter.Line(tip, left, stroke, ink);
        _painter.Line(left, right, stroke, ink);
        _painter.Line(right, tip, stroke, ink);
        _painter.Line(new(at.X, at.Y + unit * 0.6f), new(at.X, at.Y + unit * 1.1f), _painter.Hairline, ink);
    }

    private void DiamondGlyph(Vector2 at, float half, Color ink, float stroke)
    {
        _painter.Line(new(at.X, at.Y - half), new(at.X + half, at.Y), stroke, ink);
        _painter.Line(new(at.X + half, at.Y), new(at.X, at.Y + half), stroke, ink);
        _painter.Line(new(at.X, at.Y + half), new(at.X - half, at.Y), stroke, ink);
        _painter.Line(new(at.X - half, at.Y), new(at.X, at.Y - half), stroke, ink);
    }

    /// <summary>A dashed corner bracket (the queued-order tier: same corners, open pattern).</summary>
    private void DashedBracket(RectangleF box, float arm, float scale)
    {
        float x0 = box.X, y0 = box.Y, x1 = box.X + box.Width, y1 = box.Y + box.Height;
        DashedLine(new(x0, y0), new(x0 + arm, y0), _painter.Hairline, _painter.Primary);
        DashedLine(new(x0, y0), new(x0, y0 + arm), _painter.Hairline, _painter.Primary);
        DashedLine(new(x1, y0), new(x1 - arm, y0), _painter.Hairline, _painter.Primary);
        DashedLine(new(x1, y0), new(x1, y0 + arm), _painter.Hairline, _painter.Primary);
        DashedLine(new(x0, y1), new(x0 + arm, y1), _painter.Hairline, _painter.Primary);
        DashedLine(new(x0, y1), new(x0, y1 - arm), _painter.Hairline, _painter.Primary);
        DashedLine(new(x1, y1), new(x1 - arm, y1), _painter.Hairline, _painter.Primary);
        DashedLine(new(x1, y1), new(x1, y1 - arm), _painter.Hairline, _painter.Primary);
        _ = scale;
    }

    /// <summary>A dashed circle (the threat-flash tier).</summary>
    private void DashedCircle(Vector2 at, float radius, float scale)
    {
        const int segments = 8;
        for (int i = 0; i < segments; i += 2)
        {
            float start = i * MathHelper.TwoPi / segments;
            _painter.Arc(at, radius, start, MathHelper.TwoPi / segments * 0.6f, _painter.Hairline, _painter.Hot);
        }
        _ = scale;
    }

    /// <summary>
    /// A fraction bar drawn with FILLS only (rail, fill and tick marks): the label pass runs
    /// after the glyph pass, so any line submitted here would read as a strikethrough across
    /// an earlier contact's label (the audit's stroke-vs-text rule). The ticks keep the
    /// reading independent of the hue (a segmented shape, exactly like the stores block).
    /// </summary>
    private void RadarBar(float x, float y, float width, float height, float fraction, Color ink, float scale)
    {
        float filled = width * Math.Clamp(fraction, 0f, 1f);
        _painter.Surface.Fill(new(x, y), new(width, height), _painter.Dim * 0.3f);
        if (filled > 0f) _painter.Surface.Fill(new(x, y), new(filled, height), ink);
        // The bar is a plain fill: a 1 px tick submitted here would be recorded as a stroke
        // (thin in both axes) and would read as a strikethrough across an earlier label.
        _ = scale;
    }
}
