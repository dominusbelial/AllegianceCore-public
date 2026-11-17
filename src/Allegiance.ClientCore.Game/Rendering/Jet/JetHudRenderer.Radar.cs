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

    // ── P1039b — the placement pass's preallocated state (0 B/frame) ────

    /// <summary>Contacts the label/word planner handles (the glyph pass always walks the
    /// model's whole list; a 200v200 snapshot can carry ~400 contacts, and every one past
    /// this plan cap keeps its glyph, overlays and cluster word-free exactly like a hidden
    /// label).</summary>
    private const int MaxRadarPlan = 512;

    /// <summary>Word kinds one contact can carry: the class letter, SUB, the threat "!", the
    /// ripcord countdown and the command verb.</summary>
    private const int RadarWordKinds = 5;

    /// <summary>Obstacle rects the planner accumulates: the five reserved captions plus one
    /// label and five words per planned contact.</summary>
    private const int MaxRadarBlocks = 6 + MaxRadarPlan * (1 + RadarWordKinds);

    private readonly Vector2[] _radarPoints = new Vector2[MaxRadarPlan];
    private readonly int[] _radarOrder = new int[MaxRadarPlan];
    private readonly byte[] _radarLabelForm = new byte[MaxRadarPlan];      // 0 = hidden
    private readonly bool[] _radarLabelLeader = new bool[MaxRadarPlan];
    private readonly RectangleF[] _radarLabelBox = new RectangleF[MaxRadarPlan];
    private readonly byte[] _radarWordMask = new byte[MaxRadarPlan];       // bit k = word k drawn
    /// <summary>The planned top-left anchor of every marker word (kind-major per contact), so the
    /// draw pass uses the position the planner measured and reserved.</summary>
    private readonly Vector2[] _radarWordAt = new Vector2[MaxRadarPlan * RadarWordKinds];
    /// <summary>The row set the planner chose for this contact's block: bit 0 name, 1 range,
    /// 2 <c>EYE</c>, 3 the hull/shield bars, 4 the ore fill. The draw pass reads it instead of
    /// re-deriving the rows, so a row the planner dropped can never be drawn back.</summary>
    private readonly byte[] _radarLabelRows = new byte[MaxRadarPlan];
    private readonly RectangleF[] _radarBlocks = new RectangleF[MaxRadarBlocks];
    private readonly Vector2[] _ringCaptionAt = new Vector2[JetRadar.RingCount];
    private readonly int[] _ringCaptionValue = new int[JetRadar.RingCount];
    private int _ringCaptionCount;
    private Vector2 _modeCaptionAt;
    private bool _modeCaptionReserved;
    private int _radarBlockCount;
    private int _radarPlannedCount;

    /// <summary>P1039b — per-frame label counts (the proof records them per scene).</summary>
    public int RadarLabelsPlaced { get; private set; }
    public int RadarLabelsShortened { get; private set; }
    public int RadarLabelsHidden { get; private set; }
    /// <summary>Never-hidden labels that had to take a covered slot (must be 0).</summary>
    public int RadarLabelOverflows { get; private set; }
    /// <summary>Cluster/marker words dropped because no free place was left (the glyph stays).</summary>
    public int RadarWordsDropped { get; private set; }

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

        // P1039b — the compact scope keeps the bottom-right corner band: clear of the
        // flight-path column (left cap), the throttle tape/plate (right limit, measured
        // above), the sector mini-map (bottom) and the master-caution panel (top limit).
        float cautionBottom = _cautionPanel.Height > 0f ? _cautionPanel.Y + _cautionPanel.Height : 0f;
        RectangleF scope = JetRadar.Scope(w, h, scale, _throttleBandLeft, cautionBottom);
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

        // ── range bands + the reserved ring captions ───────────────────────
        // P1039b — every caption sits on ONE fixed spoke (the vertical axis, right of it,
        // above the ring it names); its rect is RESERVED before any contact label is placed,
        // so a label can never be drawn over a band reading, and a caption that would touch a
        // caption already on the spoke is skipped rather than stacked (at 800×480 the four
        // rings are closer together than one text line).
        float scopeRange = JetRadar.ScopeRange(model.Context.Contacts);
        var contacts = model.Context.Contacts;
        _radarBlockCount = 0;
        _ringCaptionCount = 0;
        _modeCaptionReserved = false;
        for (int ring = 1; ring <= JetRadar.RingCount; ring++)
        {
            float radius = JetRadar.RingRadius(scope, ring, round);
            if (radius <= 0f) continue;
            if (round)
                _painter.Circle(centre, radius, _painter.Hairline, _painter.Dim * 0.55f);
            else
                RingSquare(centre, radius, _painter.Hairline, _painter.Dim * 0.55f);
            // The band's value on the fixed vertical spoke, above the ring (no invented unit).
            int value = JetRadar.RingRange(ring, scopeRange);
            string text = RadarLabelModel.RangeText(value);
            Vector2 ink = _painter.Measure(text, large: false, scale);
            float labelY = centre.Y - radius - ink.Y - 1f * scale;
            if (labelY <= scope.Y) continue;                 // no room on this spoke
            var rect = new RectangleF(centre.X + 3f * scale, labelY, ink.X, ink.Y);
            if (JetRadarLabels.Covered(rect, RadarBlocks) > 0f) continue;   // the spoke is taken
            ReserveRadarBlock(rect);
            _ringCaptionAt[_ringCaptionCount] = new(rect.X, rect.Y);
            _ringCaptionValue[_ringCaptionCount] = value;
            _ringCaptionCount++;
        }

        // ── the mode label (same four modes, same words as native) ──────────
        if (!_radarCaptionText.EndsWith(RadarLod.Label(model.Context.RadarMode), StringComparison.Ordinal))
            _radarCaptionText = RadarCaption + RadarLod.Label(model.Context.RadarMode);
        // The mode word sits in the scope's bottom-right corner: it is reserved like the ring
        // captions, so no contact label or cluster word may be placed over it.
        Vector2 captionInk = _painter.Measure(_radarCaptionText, large: false, scale);
        _modeCaptionAt = new(scope.X + scope.Width - 4f * scale - captionInk.X,
            scope.Y + scope.Height - 3f * scale - captionInk.Y);
        ReserveRadarBlock(new RectangleF(_modeCaptionAt.X, _modeCaptionAt.Y, captionInk.X, captionInk.Y));
        _modeCaptionReserved = true;

        // ── the placement pass: plan every word and every label before drawing ──
        // Strokes always precede text in this pass order (frame → bands → glyphs → leaders →
        // captions → words → labels), so no stroke can ever read as a strikethrough.
        bool commandCamera = model.Context.CommandView;
        RadarMode mode = model.Context.RadarMode;
        PlanRadar(model, scope, scopeRange, scale, commandCamera, mode);

        // ── pass A: every glyph, overlay and overlay stroke ─────────────────
        for (int i = 0; i < contacts.Count; i++)
        {
            HudContact contact = contacts[i];
            Vector2 at = i < _radarPlannedCount ? _radarPoints[i]
                : JetRadar.ContactPoint(scope, contact, _viewportWidth, _viewportHeight, scopeRange);
            DrawRadarGlyph(contact, at, scope, scale, commandCamera, mode);
        }
        // ── pass B: the leader strokes of diagonally placed labels ──────────
        for (int i = 0; i < _radarPlannedCount; i++)
        {
            if (_radarLabelForm[i] == 0 || !_radarLabelLeader[i]) continue;
            var (from, to) = JetRadarLabels.Leader(_radarPoints[i],
                JetRadarLabels.GlyphClear * scale, _radarLabelBox[i], scale);
            if ((to - from).LengthSquared() < 1f) continue;
            _painter.Line(from, to, _painter.Hairline, _painter.Dim * 0.8f);
        }
        // ── pass C: the reserved band captions (text, after every stroke) ───
        for (int i = 0; i < _ringCaptionCount; i++)
            _painter.Text(RadarLabelModel.RangeText(_ringCaptionValue[i]), _ringCaptionAt[i],
                large: false, scale, _painter.Dim * 0.75f);
        if (_modeCaptionReserved)
            _painter.Text(_radarCaptionText, _modeCaptionAt, large: false, scale, _painter.Dim);
        // ── pass D: the cluster/marker LETTERS (M/P/T/!/SUB/RIP nn/ACC/QUE) ─
        for (int i = 0; i < _radarPlannedCount; i++)
        {
            HudContact contact = contacts[i];
            DrawRadarGlyphWords(i, contact, _radarPoints[i], scope, scale, commandCamera);
        }
        // ── pass E: the placed label blocks (never over a caption or another label) ──
        for (int i = 0; i < _radarPlannedCount; i++)
        {
            HudContact contact = contacts[i];
            DrawRadarLabel(i, contact, _radarPoints[i], scale, commandCamera, mode);
        }
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
    /// word, the threat flash's "!", the ripcord countdown and the command verb. P1039b places
    /// each of them in the planning pass (<see cref="PlanRadar"/>): a word that would leave the
    /// scope or land on a reserved caption / another word / a label is dropped (the glyph and
    /// its overlays stay), so no two reads of this element can ever stack. Every stroke of the
    /// frame (glyphs, leaders) is submitted before this pass, so no overlay line can run across
    /// a word.
    /// </summary>
    private void DrawRadarGlyphWords(int index, in HudContact contact, Vector2 at, RectangleF scope,
        float scale, bool commandCamera)
    {
        byte mask = _radarWordMask[index];
        if (mask == 0) return;
        for (int kind = 0; kind < RadarWordKinds; kind++)
        {
            if ((mask & (1 << kind)) == 0) continue;
            if (!RadarWordText(kind, contact, out string? text)) continue;
            Vector2 pos = _radarWordAt[index * RadarWordKinds + kind];
            Color ink = kind switch
            {
                2 => _painter.Hot,
                4 => contact.CommandAccepted ? _painter.Primary : _painter.Dim,
                1 => _painter.Dim,
                _ => contact.Threat || contact.ThreatFlash ? _painter.Hot : _painter.Primary,
            };
            _painter.Text(text!, pos, large: false, scale, ink);
        }
    }

    /// <summary>
    /// The text of one marker word. <paramref name="kind"/>: 0 = class letter, 1 = <c>SUB</c>,
    /// 2 = <c>!</c>, 3 = <c>RIP n</c>, 4 = the command verb. False when this contact does not
    /// carry that word.
    /// </summary>
    private bool RadarWordText(int kind, in HudContact contact, out string? text)
    {
        text = null;
        switch (kind)
        {
            case 0:
                text = contact.Kind switch
                {
                    HudContactKind.Missile => MissileGlyph,
                    HudContactKind.Probe => ProbeGlyph,
                    HudContactKind.Treasure => TreasureGlyph,
                    _ => null,
                };
                return text is not null;
            case 1:
                if (!contact.Subject) return false;
                text = SubjectWord;
                return true;
            case 2:
                if (!contact.ThreatFlash) return false;
                text = ThreatGlyph;
                return true;
            case 3:
                if (!contact.Ripcord || contact.RipcordSeconds is not > 0) return false;
                text = RipText(contact.RipcordSeconds.Value);
                return true;
            default:
                if (contact.CommandId is null) return false;
                text = contact.CommandAccepted ? AcceptedLabel : QueuedLabel;
                return true;
        }
    }

    /// <summary>Word placements one priority word may try (P1039b): its own side, the other
    /// side, below the glyph and above it — the same "slots around the glyph" idea the label
    /// pass uses, so the pilot's own <c>ACC</c>/<c>QUE</c> verb and the subject word cannot be
    /// pushed off a band caption or another reading.</summary>
    private const int RadarWordVariants = 4;

    /// <summary>
    /// The top-left anchor of one marker word in <paramref name="variant"/> (0 = the word's own
    /// position, 1 = the mirrored side, 2 = below the glyph, 3 = above it).
    /// <paramref name="width"/> is the measured text width (needed for mirrored slots).
    /// </summary>
    private Vector2 RadarWordPos(int kind, Vector2 at, RectangleF scope, float scale, int variant,
        float width)
    {
        float unit = 4.5f * scale;
        float bracket = unit + 4f * scale;
        float rowH = _painter.Measure("X", large: false, scale).Y;
        float top = MathF.Max(scope.Y + 1f * scale, at.Y - bracket - rowH);
        switch (kind)
        {
            case 0:
                return new(at.X + unit + 2f * scale, at.Y - unit * 0.6f);
            case 1:   // SUB: right of the bracket, mirrored left, below, above
                return variant switch
                {
                    1 => new(at.X - bracket - width, at.Y - bracket),
                    2 => new(at.X - width * 0.5f, at.Y + bracket),
                    3 => new(at.X - width * 0.5f, top - rowH),
                    _ => new(at.X + bracket + 2f * scale, at.Y - bracket),
                };
            case 2:
                return new(at.X - 2f * scale, at.Y - bracket - 12f * scale);
            case 3:
                return new(at.X + 2f * scale, at.Y - (bracket + 5f * scale) - 10f * scale);
            default:  // the command verb: left of the bracket, mirrored right, below, above
                return variant switch
                {
                    1 => new(at.X + 2f * scale, top),
                    2 => new(at.X - width * 0.5f, at.Y + bracket),
                    3 => new(at.X - width * 0.5f, top - rowH),
                    _ => new(at.X - bracket, top),
                };
        }
    }

    /// <summary>The <c>RIP n</c> word of a contact, cached by the countdown value so a steady
    /// frame formats nothing.</summary>
    private string RipText(int seconds)
    {
        if (seconds != _ripShown)
        {
            _ripShown = seconds;
            _ripText = RipWord + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return _ripText;
    }

    /// <summary>
    /// One contact's label block, drawn at the rectangle the placement pass chose
    /// (<see cref="PlanRadar"/>): the native per-kind LOD tier
    /// (<see cref="RadarLabelModel.Resolve"/>) in the form that fitted — the full block, the
    /// range line alone, or the name alone — faded by the shared level-driven range fade. A
    /// dropped label draws nothing here; its glyph, overlays and <c>ACC</c>/<c>QUE</c> word stay.
    /// </summary>
    private void DrawRadarLabel(int index, in HudContact contact, Vector2 at, float scale,
        bool commandCamera, RadarMode mode)
    {
        byte form = _radarLabelForm[index];
        if (form == 0) return;                       // hidden: no slot was free (the glyph stays)
        RadarLabel? label = RadarLabelModel.Resolve(contact, mode, commandCamera);
        if (label is not { } payload) return;

        // Range fade mapped to monochrome OPACITY (the level-driven fade, no sensor rule).
        HudLevelPreset preset = HudLevels.Preset(_settings.EffectiveLevel);
        float alpha = RadarLabelDeclutter.Fade(RadarLabelDeclutter.Distance(contact),
            preset.FadeNear, preset.FadeFar, 0.35f);
        Color ink = (contact.Highlight ? _painter.Hot : _painter.Primary)
            * Math.Clamp(alpha, 0.25f, 1f);

        float rowH = _painter.Measure("X", large: false, scale).Y + 1f * scale;
        float barW = 26f * scale, barH = 1.8f * scale;
        RectangleF box = _radarLabelBox[index];
        float x = box.X, y = box.Y;

        // The rows are the PLANNER's (it may have dropped the name because it is wider than
        // the scope); the draw pass never re-derives them, so a dropped row cannot come back.
        byte rows = _radarLabelRows[index];
        string? name = (rows & RadarRowName) != 0 ? payload.Name : null;
        // The metre line comes from the label model's own cache (one string per metre value,
        // never one per contact per frame).
        string? range = null;
        if (contact.Range is > 0)
        {
            // The full block keeps the `nnnn m` line; the shortened form is native's own bare
            // `_itoa` digits (radarimage.cpp:1209-1218), which is what fits a crowded scope.
            if ((rows & RadarRowRangeBare) != 0) range = RadarLabelModel.RangeText(contact.Range.Value);
            else if ((rows & RadarRowRange) != 0) range = RadarLabelModel.RangeLineText(contact.Range.Value);
        }
        string? eye = (rows & RadarRowEye) != 0 ? "EYE" : null;

        if (name is not null) { _painter.Text(name, new(x, y), large: false, scale, ink); y += rowH; }
        if (range is not null) { _painter.Text(range, new(x, y), large: false, scale, ink); y += rowH; }
        if ((rows & RadarRowBars) != 0)
        {
            if (payload.Hull is { } hull) { RadarBar(x, y, barW, barH, hull, ink, scale); y += barH + 2f * scale; }
            if (payload.Shield is { } shield) { RadarBar(x, y, barW, barH, shield, ink, scale); y += barH + 2f * scale; }
        }
        if ((rows & RadarRowFill) != 0 && payload.Fill is { } fill)
        {
            RadarBar(x, y, barW, barH, fill, ink, scale);
            y += barH + 3f * scale;
        }
        if (eye is not null) _painter.Text(eye, new(x, y), large: false, scale, ink);
    }

    // ── P1039b: the placement pass ──────────────────────────────────────

    private const byte RadarLabelFormFull = 1;
    private const byte RadarLabelFormRangeOnly = 2;
    private const byte RadarLabelFormNameOnly = 3;

    // The row set of a planned block (the draw pass reads these bits, never the payload again).
    private const byte RadarRowName = 1;
    private const byte RadarRowRange = 2;
    private const byte RadarRowEye = 4;
    private const byte RadarRowBars = 8;
    private const byte RadarRowFill = 16;
    /// <summary>The shortened range row: native's bare digits, no <c> m</c> suffix.</summary>
    private const byte RadarRowRangeBare = 32;

    /// <summary>The obstacle rects the planner has reserved this frame.</summary>
    private ReadOnlySpan<RectangleF> RadarBlocks => _radarBlocks.AsSpan(0, _radarBlockCount);

    private void ReserveRadarBlock(RectangleF box)
    {
        if (_radarBlockCount < _radarBlocks.Length) _radarBlocks[_radarBlockCount++] = box;
    }

    /// <summary>
    /// P1039b — plan every marker word and every native-eligible label of this frame BEFORE any
    /// of them is drawn, in the stated priority order: selected target, order subject,
    /// threat/missile, own command verb, then by nearest range (ties keep the model's order).
    /// The reserved caption rects are already in the obstacle list; a word or label takes the
    /// first slot that fits the scope's inner rectangle and touches nothing already reserved,
    /// then the next, and a non-priority label that finds no room is HIDDEN while its glyph
    /// stays. Allocation-free: preallocated arrays, index loops, cached strings.
    /// </summary>
    private void PlanRadar(HudModel model, RectangleF scope, float scopeRange, float scale,
        bool commandCamera, RadarMode mode)
    {
        var contacts = model.Context.Contacts;
        int count = Math.Min(contacts.Count, MaxRadarPlan);
        _radarPlannedCount = count;
        RadarLabelsPlaced = 0;
        RadarLabelsShortened = 0;
        RadarLabelsHidden = 0;
        RadarLabelOverflows = 0;
        RadarWordsDropped = 0;
        RadarPriorityLabelMisses = 0;
        RectangleF inner = JetRadarLabels.Inner(scope, scale);
        float clear = JetRadarLabels.GlyphClear * scale;

        for (int i = 0; i < count; i++)
        {
            _radarLabelForm[i] = 0;
            _radarLabelRows[i] = 0;
            _radarWordMask[i] = 0;
            _radarPoints[i] = JetRadar.ContactPoint(scope, contacts[i], _viewportWidth, _viewportHeight, scopeRange);
            _radarOrder[i] = i;
        }
        // Stable insertion sort by (rank, range): the queue the brief states.
        for (int i = 1; i < count; i++)
        {
            int item = _radarOrder[i];
            HudContact current = contacts[item];
            int j = i - 1;
            while (j >= 0)
            {
                HudContact other = contacts[_radarOrder[j]];
                if (!JetRadarLabels.Before(current, other)) break;
                _radarOrder[j + 1] = _radarOrder[j];
                j--;
            }
            _radarOrder[j + 1] = item;
        }
        // Words that belong to a NEVER-HIDDEN contact are placed first (the verb is a priority
        // reading), then the label blocks, then the other marker words: a label always wins a
        // contested cell over a droppable class letter.
        for (int pass = 0; pass < count; pass++)
        {
            int index = _radarOrder[pass];
            PlanRadarWords(index, contacts[index], scope, inner, scale, RadarWordPhasePriorityVerb);
        }
        for (int pass = 0; pass < count; pass++)
        {
            int index = _radarOrder[pass];
            PlanRadarLabel(index, contacts[index], inner, clear, scale, commandCamera, mode);
        }
        for (int pass = 0; pass < count; pass++)
        {
            int index = _radarOrder[pass];
            PlanRadarWords(index, contacts[index], scope, inner, scale, RadarWordPhaseRest);
        }
        // The brief's assertion, measured rather than assumed: no priority contact that native
        // labels may be left without a block.
        for (int i = 0; i < count; i++)
        {
            HudContact contact = contacts[i];
            if (!JetRadarLabels.NeverHidden(contact)) continue;
            if (RadarLabelModel.Resolve(contact, mode, commandCamera) is null) continue;
            if (_radarLabelForm[i] == 0) RadarPriorityLabelMisses++;
        }
    }

    /// <summary>Never-hidden labels the planner could not place (the audit asserts this is 0).</summary>
    public int RadarPriorityLabelMisses { get; private set; }

    /// <summary>P1039b — the planned label rectangles of this frame (the proof records them as
    /// evidence that each block sits where the placement pass put it).</summary>
    public ReadOnlySpan<RectangleF> RadarLabelBoxes => _radarLabelBox.AsSpan(0, _radarPlannedCount);

    /// <summary>The planned form of each label (<c>0</c> = hidden).</summary>
    public ReadOnlySpan<byte> RadarLabelForms => _radarLabelForm.AsSpan(0, _radarPlannedCount);

    private const byte RadarWordPhasePriorityVerb = 0;
    private const byte RadarWordPhaseRest = 1;

    /// <summary>
    /// Place this contact's marker words in <paramref name="phase"/>: phase 0 puts the command
    /// verb of a never-hidden contact down first (the brief's priority), phase 1 places every
    /// other word (the class letter, <c>SUB</c>, <c>!</c>, <c>RIP n</c> and the verb of a
    /// non-priority contact). A word that does not fit the scope's inner rect or touches a
    /// reserved block is DROPPED — its glyph and overlays stay and the label pass is untouched.
    /// </summary>
    private void PlanRadarWords(int index, in HudContact contact, RectangleF scope, RectangleF inner,
        float scale, byte phase)
    {
        byte mask = _radarWordMask[index];
        bool priorityVerb = JetRadarLabels.NeverHidden(contact);
        Vector2 at = _radarPoints[index];
        for (int kind = 0; kind < RadarWordKinds; kind++)
        {
            if (phase == RadarWordPhasePriorityVerb && (kind != 4 || !priorityVerb)) continue;
            if (phase == RadarWordPhaseRest && kind == 4 && priorityVerb) continue;
            if (!RadarWordText(kind, contact, out string? text)) continue;
            Vector2 size = _painter.Measure(text!, large: false, scale);
            // The verb and the subject word are readings, not decorations: they try the four
            // placements around the glyph before being dropped. A class letter, the "!" and
            // the ripcord countdown keep their one native-side position (droppable markers).
            int variants = kind is 1 or 4 ? RadarWordVariants : 1;
            bool placed = false;
            for (int variant = 0; variant < variants; variant++)
            {
                Vector2 pos = RadarWordPos(kind, at, scope, scale, variant, size.X);
                var box = new RectangleF(pos.X, pos.Y, size.X, size.Y);
                if (!JetRadarLabels.Fits(inner, box) || JetRadarLabels.Overlaps(box, RadarBlocks))
                    continue;
                _radarWordAt[index * RadarWordKinds + kind] = pos;
                mask |= (byte)(1 << kind);
                ReserveRadarBlock(box);
                placed = true;
                break;
            }
            if (!placed) RadarWordsDropped++;
        }
        _radarWordMask[index] = mask;
    }

    private void PlanRadarLabel(int index, in HudContact contact, RectangleF inner, float clear,
        float scale, bool commandCamera, RadarMode mode)
    {
        RadarLabel? label = RadarLabelModel.Resolve(contact, mode, commandCamera);
        if (label is not { } payload) return;        // native draws no label for this contact

        Vector2 at = _radarPoints[index];
        float rowH = _painter.Measure("X", large: false, scale).Y + 1f * scale;
        float barW = 26f * scale, barH = 1.8f * scale;
        bool hasBars = payload.Hull is not null || payload.Shield is not null;
        bool hasFill = payload.Fill is not null;

        string? name = payload.Name;
        if (name is not null && _painter.Measure(name, large: false, scale).X > inner.Width) name = null;
        string? range = contact.Range is > 0 ? RadarLabelModel.RangeLineText(contact.Range.Value) : null;
        if (range is not null && _painter.Measure(range, large: false, scale).X > inner.Width) range = null;
        // P1039b — the SHORT form is native's own bare `_itoa` digits (radarimage.cpp:1209-1218),
        // not the `nnnn m` line: narrower, so a crowded compact scope can still hold every
        // priority reading.
        int? rangeValue = range is not null ? contact.Range : null;
        string? eye = payload.Eye ? "EYE" : null;

        float width = 0f, rangeWidth = 0f, nameWidth = 0f;
        if (name is not null) nameWidth = _painter.Measure(name, large: false, scale).X;
        if (rangeValue is int metres) rangeWidth = _painter.Measure(RadarLabelModel.RangeText(metres), large: false, scale).X;
        float eyeWidth = eye is null ? 0f : _painter.Measure(eye, large: false, scale).X;
        width = MathF.Max(nameWidth, MathF.Max(
            range is null ? 0f : _painter.Measure(range, large: false, scale).X, eyeWidth));
        if (hasBars || hasFill) width = MathF.Max(width, barW);
        if (width <= 0f) return;
        int rows = (name is null ? 0 : 1) + (range is null ? 0 : 1) + (eye is null ? 0 : 1);
        float barsHeight = (hasBars ? barH * 2f + 2f * scale : 0f) + (hasFill ? barH + 2f * scale : 0f);
        float fullHeight = rows * rowH + barsHeight;
        byte fullRows = 0;
        if (name is not null) fullRows |= RadarRowName;
        if (range is not null) fullRows |= RadarRowRange;
        if (eye is not null) fullRows |= RadarRowEye;
        if (hasBars) fullRows |= RadarRowBars;
        if (hasFill) fullRows |= RadarRowFill;
        byte rangeRows = RadarRowRangeBare;
        byte nameRows = RadarRowName;

        // Fallback ladder: full block → range line alone → name alone. A form that is covered
        // (not clear) is remembered only as the least-obstruction fallback for a priority label.
        bool haveCovered = false;
        JetRadarLabels.Placement covered = default;
        byte coveredForm = 0;
        byte coveredRows = 0;
        if (fullHeight <= inner.Height)
        {
            var full = JetRadarLabels.Place(at, clear, width, fullHeight, scale, inner, RadarBlocks);
            if (full.Clear) { CommitRadarLabel(index, RadarLabelFormFull, full, fullRows); RadarLabelsPlaced++; return; }
            covered = full; coveredForm = RadarLabelFormFull; coveredRows = fullRows; haveCovered = true;
        }
        if (rangeValue is not null)
        {
            var shortForm = JetRadarLabels.Place(at, clear, rangeWidth, rowH, scale, inner, RadarBlocks);
            if (shortForm.Clear)
            {
                CommitRadarLabel(index, RadarLabelFormRangeOnly, shortForm, rangeRows);
                RadarLabelsPlaced++; RadarLabelsShortened++;
                return;
            }
            covered = shortForm; coveredForm = RadarLabelFormRangeOnly; coveredRows = rangeRows; haveCovered = true;
        }
        if (name is not null)
        {
            var nameForm = JetRadarLabels.Place(at, clear, nameWidth, rowH, scale, inner, RadarBlocks);
            if (nameForm.Clear)
            {
                CommitRadarLabel(index, RadarLabelFormNameOnly, nameForm, nameRows);
                RadarLabelsPlaced++; RadarLabelsShortened++;
                return;
            }
            covered = nameForm; coveredForm = RadarLabelFormNameOnly; coveredRows = nameRows; haveCovered = true;
        }

        if (!JetRadarLabels.NeverHidden(contact))
        {
            RadarLabelsHidden++;                     // the glyph, its overlays and words stay
            return;
        }
        // Never hidden, and every slot around the glyph is taken: the label takes the first
        // free cell of a scope-wide scan with a leader stroke back to its glyph (so a priority
        // reading is placed rather than stacked on another block).
        float scanWidth = rangeValue is not null ? rangeWidth : nameWidth;
        byte scanRows = rangeValue is not null ? RadarRowRangeBare : RadarRowName;
        byte scanForm = rangeValue is not null ? RadarLabelFormRangeOnly : RadarLabelFormNameOnly;
        if (scanWidth > 0f)
        {
            var cell = JetRadarLabels.Scan(inner, scanWidth, rowH, RadarBlocks);
            if (cell.Clear)
            {
                CommitRadarLabel(index, scanForm, cell, scanRows);
                RadarLabelsPlaced++;
                RadarLabelsShortened++;
                return;
            }
        }
        // The scope is genuinely full: keep the shortest form even over another block (reported
        // as an overflow so the audit can assert it is 0 in every scene).
        if (haveCovered)
        {
            CommitRadarLabel(index, coveredForm, covered, coveredRows);
            RadarLabelsPlaced++;
            if (coveredForm != RadarLabelFormFull) RadarLabelsShortened++;
            RadarLabelOverflows++;
            return;
        }
        // Nothing fitted at all (an extremely small scope): clamp the shortest available form
        // into the inner rectangle so a priority contact still carries its reading.
        float fallbackWidth = rangeValue is not null ? rangeWidth : nameWidth;
        byte fallbackForm = rangeValue is not null ? RadarLabelFormRangeOnly : RadarLabelFormNameOnly;
        byte fallbackRows = rangeValue is not null ? rangeRows : nameRows;
        if (fallbackWidth <= 0f) { RadarLabelsHidden++; return; }
        var fallbackBox = new RectangleF(inner.X, inner.Y, fallbackWidth, rowH);
        CommitRadarLabel(index, fallbackForm,
            new JetRadarLabels.Placement(fallbackBox, JetRadarLabels.Slot.Right, false, false), fallbackRows);
        RadarLabelsPlaced++;
        RadarLabelsShortened++;
        RadarLabelOverflows++;
    }

    private void CommitRadarLabel(int index, byte form, in JetRadarLabels.Placement placement, byte rows)
    {
        _radarLabelForm[index] = form;
        _radarLabelRows[index] = rows;
        _radarLabelLeader[index] = placement.Leader;
        _radarLabelBox[index] = placement.Box;
        ReserveRadarBlock(placement.Box);
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
