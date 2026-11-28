using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

public partial class ModernHudRenderer
{
    private readonly List<Rectangle> _ownBounds = new(32);
    private readonly List<Rectangle> _targetBounds = new(12);
    private readonly List<(string Text, Vector2 Position, Color Color, float Scale)> _targetLines = new(8);
    private int _beforeArcs;
    private Vector2 _hullArc, _shieldArc;
    // P1107 — the compact hull/shield bars (screen space, before the native slide).
    private Rectangle _hullBar, _shieldBar;
    private Vector2? _inRange;
    private float _targetScale;
    private Rectangle _preview;

    // Round outwards to include antialiased SDF edges at fractional positions.
    private static Rectangle Extent(Vector2 p, Vector2 size, int pad = 1)
    {
        int x = (int)MathF.Floor(p.X) - pad, y = (int)MathF.Floor(p.Y) - pad;
        return new(x, y, (int)MathF.Ceiling(p.X + size.X) + pad - x, (int)MathF.Ceiling(p.Y + size.Y) + pad - y);
    }

    /// <summary>Reserve the box an own-ship readout will occupy. P1107 — the draw
    /// scale is a parameter (secondary text is one font step down when compact is on)
    /// and <paramref name="scaleCentre"/> mirrors <c>DrawMeasuredText</c>'s own
    /// centre correction so the reservation covers the glyphs that are really drawn.</summary>
    private void ReserveOwnText(string text, Vector2 position, float drawScale, bool scaleCentre = true)
    {
        var size = _small.MeasureString(text);
        if (scaleCentre) position.X += size.X * (1 - drawScale) / 2; // DrawMeasuredText's centre correction
        var bounds = Extent(position, size * drawScale);
        bounds.Inflate(3, 1); // the backing plate
        _ownBounds.Add(Rectangle.Union(bounds, _smallSdf.InkBounds(text, position, drawScale)));
    }

    private void PrepareTarget(HudModel model, Viewport v)
    {
        if (!model.TargetPaneVisible || model.Context.TargetPane is not { } tp) return;
        _ownBounds.Clear();
        bool compact = CompactHud;
        float secondary = SecondaryScale;
        // P1107 — the compact target geometry draws as soon as the pane exists; the
        // ring geometry is only used by the flag-off path (and by a compact pane that
        // had to fall back to its one-row crowded layout).
        bool bars = compact;
        if (model.CentreHudVisible)
        {
            foreach (var g in model.Gauges)
            {
                if (!g.Visible) continue;
                var c = GaugeCenter(g, v);
                float radius = CompactHudLayout.RingRadius(_scale, compact);
                if (compact)
                {
                    float extent = CompactHudLayout.RingExtent(_scale, true);
                    float half = MathF.Max(extent, 40 * _scale);
                    float height = 2 * extent + CompactHudLayout.LabelGap(_scale, true) + _small.LineSpacing * secondary;
                    _ownBounds.Add(new Rectangle((int)(c.X - half), (int)(c.Y - extent), (int)(2 * half), (int)height));
                }
                else _ownBounds.Add(Extent(c - new Vector2(35 * _scale), new Vector2(70 * _scale)));
                string percent = HudStringCache.Percent(g.Fraction);
                ReserveOwnText(percent, c - _small.MeasureString(percent) / 2, _scale, scaleCentre: !compact);
                float labelTop = compact
                    ? c.Y + radius + CompactHudLayout.LabelGap(_scale, true)
                    : c.Y + 40 * _scale;
                ReserveOwnText(g.Label, new(c.X - _small.MeasureString(g.Label).X / 2, labelTop),
                    compact ? secondary : _scale, scaleCentre: !compact);
                if (g.Kind != GaugeKind.Thrust && g.Fraction <= (g.Kind == GaugeKind.Energy ? .05f : .15f))
                    ReserveOwnText("!", c + new Vector2(32, -32) * _scale, compact ? secondary : _scale);
            }
            VisitStatus(model, v, reserve: true);
        }

        float s = TargetPaneLayout.Scale(v.Width, v.Height, _scale);
        float x0 = TargetPaneLayout.OriginX(v.Width, v.Height, _scale);
        float slide = TargetPaneLayout.SlideOffset(model.Context.TargetPaneSlide, s);
        float y0 = TargetPaneLayout.OriginY(v.Width, v.Height, _scale) + slide;
        var side = new Color(tp.SideColor.R, tp.SideColor.G, tp.SideColor.B, tp.SideColor.A);
        if (_settings.ColorBlindSafe) side = HudPalette.Side(side);
        // P1107 — the target NAME stays the pane's primary readout; the type suffix
        // is secondary text when the compact HUD is on (it used to share one run with
        // the name, so it could never be a font step smaller).
        string name = compact ? tp.Name ?? "unknown" : TargetCaption(tp);
        string? type = compact && tp.TypeDescription is { } typeDescription ? ": " + typeDescription : null;
        string? range = tp.Distance is { } d ? "RANGE: " + HudFormat.FormatNativeNumber(d)
            : tp.SectorName is { } sector ? HudStringCache.Location(sector) : null;
        string? speed = tp.Speed is { } sp ? "SPEED: " + HudFormat.FormatNativeNumber(sp) : null;
        string? tti = tp.TimeToIntercept is { } t ? HudStringCache.Tti(t) : null;
        string? kb = tp.IsShip && tp.KillBonusPercent is { } n ? HudStringCache.KillBonusBadge(n) : null;

        _targetBounds.Clear();
        _targetLines.Clear();
        Vector2 Size(string text) => PaneTextSize(_small, text);
        float detail = compact ? secondary : _scale;
        // Native pane anchors (Artwork/dialog.mdl:4043-4081,4192-4242).
        float nameHeight = Size(name).Y;
        float nameTop = y0 - 125 * s - nameHeight;
        if (type is null)
        {
            Line(name, new(x0 + 320 * s - Size(name).X / 2, nameTop), side, _scale);
        }
        else
        {
            // The caption is name + ": type": the name at the primary size, the type
            // one font step down, the PAIR centred on the native pane centre.
            float nameWidth = Size(name).X;
            float typeHeight = _small.LineSpacing * secondary;
            float typeWidth = _small.MeasureString(type).X * secondary;
            float left = x0 + 320 * s - (nameWidth + typeWidth) / 2;
            Line(name, new(left, nameTop), side, _scale);
            Line(type, new(left + nameWidth, nameTop + (nameHeight - typeHeight) / 2), side, secondary);
        }
        if (range != null) Line(range, new(x0 + 400 * s, y0 - 33 * s - Size(range).Y * detail / _scale), Muted, detail);
        if (speed != null) Line(speed, new(x0 + 242 * s - Size(speed).X * detail / _scale, y0 - 33 * s - Size(speed).Y * detail / _scale), Muted, detail);
        _beforeArcs = _targetLines.Count;
        _targetScale = s;
        _hullArc = new(x0 + 276.5f * s, y0 - 83.5f * s);
        _shieldArc = new(x0 + 409 * s, _hullArc.Y);
        _inRange = tp.InRange ? new(x0 + 200 * s, y0 - 90 * s) : null;
        _preview = global::Allegiance.ClientCore.MonoGame.Rendering.TargetModelPreview.PaneRect(x0, y0, s);
        if (bars)
        {
            // P1107 — two slim side-tinted bars flanking the native model preview on
            // the row the ring gauges used: "ARMOR ====" left, "==== SHIELD" right.
            // A horizontal bar can no longer be mistaken for an own-ship ring.
            float labelScale = secondary;
            var armorSize = _small.MeasureString("ARMOR") * labelScale;
            var shieldSize = _small.MeasureString("SHIELD") * labelScale;
            var hull = CompactHudLayout.TargetBar(shield: false, x0, y0, s, armorSize.X, armorSize.Y);
            var shields = CompactHudLayout.TargetBar(shield: true, x0, y0, s, shieldSize.X, shieldSize.Y);
            _hullBar = hull.Bar;
            _shieldBar = shields.Bar;
            // The native in-range icon sat at local (200,90), just above the hull
            // gauge's slot; with a bar there it would touch the bar's top edge, so the
            // compact pane puts the same diamond on the bar row, left of the ARMOR
            // label (nothing else occupies that native pane space).
            if (tp.InRange)
                _inRange = new Vector2(hull.LabelTopLeft.X - 10 * s, y0 - CompactHudLayout.BarCentreY * s);
            Line("ARMOR", hull.LabelTopLeft, Muted, labelScale);
            Line("SHIELD", shields.LabelTopLeft, Muted, labelScale);
        }
        else
        {
            Line("ARMOR", new(_hullArc.X - 16 * s - 8 * s - Size("ARMOR").X, _hullArc.Y - Size("ARMOR").Y / 2), Muted, _scale);
            Line("SHIELD", new(_shieldArc.X + 16 * s + 8 * s, _shieldArc.Y - Size("SHIELD").Y / 2), Muted, _scale);
        }
        if (tti != null) Line(tti, new(x0 + 242 * s - Size(tti).X * detail / _scale, y0 - 10 * s - Size(tti).Y * detail / _scale), Muted, detail);
        if (kb != null)
        {
            var (left, top) = TargetPaneLayout.ExperienceNumberTopLeft(x0, y0, s, Size(kb).X);
            Line(kb, new(left, top), CarryGold, _scale);
        }
        AddTargetGeometry();
        // Decide at the fully open position so the native slide remains continuous.
        // P1107 — the compact pane additionally refuses to tuck itself into the
        // gauge cluster's FOOTPRINT: at 1280x720 the pane's native anchors put its
        // name between the hull/shield rings and its bars between the thrust/fuel
        // rings, which reads exactly like more own-ship gauges (the confusion this
        // flag exists to remove). The crowded one-row layout then takes over.
        _slideY = 0;
        if (TargetIntersectsOwn((int)MathF.Round(slide)) || (compact && TargetInsideOwnFootprint((int)MathF.Round(slide))))
        {
            CompactTarget(v, TargetCaption(tp), kb, speed, range, tti, side, tp.InRange);
            foreach (var bounds in _ownBounds) Layout.Reserve(bounds);
            _slideY = slide;
        }
        else ReserveTargetPane(model, v, _scale);
        foreach (var bounds in _targetBounds)
        {
            var moved = bounds;
            moved.Offset(0, (int)MathF.Round(_slideY));
            Layout.ReserveInstrument(moved);
        }
    }

    private bool TargetIntersectsOwn(int slide)
    {
        foreach (var part in _targetBounds)
        {
            var open = part;
            open.Offset(0, -slide);
            foreach (var own in _ownBounds)
                if (own.Intersects(open)) return true;
        }
        return false;
    }

    /// <summary>P1107 — does any part of the target pane land inside the own-ship
    /// cluster's overall footprint (the union of every own ring/label/status box)?
    /// Only the compact HUD uses this; the flag-off path keeps its shipped rule
    /// (per-element intersection) so its pixels are unchanged.</summary>
    private bool TargetInsideOwnFootprint(int slide)
    {
        if (_ownBounds.Count == 0) return false;
        var footprint = _ownBounds[0];
        foreach (var own in _ownBounds) footprint = Rectangle.Union(footprint, own);
        foreach (var part in _targetBounds)
        {
            var open = part;
            open.Offset(0, -slide);
            if (footprint.Intersects(open)) return true;
        }
        return false;
    }

    private float _slideY;

    private void Line(string text, Vector2 position, Color color, float drawScale)
    {
        _targetLines.Add((text, position, color, drawScale));
        _targetBounds.Add(_smallSdf.InkBounds(text, position, drawScale));
    }

    private void AddTargetGeometry()
    {
        if (CompactHud)
        {
            _targetBounds.Add(new Rectangle(_hullBar.X - 1, _hullBar.Y - 1, _hullBar.Width + 2, _hullBar.Height + 2));
            _targetBounds.Add(new Rectangle(_shieldBar.X - 1, _shieldBar.Y - 1, _shieldBar.Width + 2, _shieldBar.Height + 2));
        }
        else
        {
            var radius = new Vector2(17 * _targetScale);
            _targetBounds.Add(Extent(_hullArc - radius, 2 * radius));
            _targetBounds.Add(Extent(_shieldArc - radius, 2 * radius));
        }
        if (PreviewWillDraw) _targetBounds.Add(_preview);
        if (_inRange is { } c) _targetBounds.Add(Extent(c - new Vector2(8 * _scale), new Vector2(16 * _scale)));
    }

    private void CompactTarget(Viewport v, string name, string? kb, string? speed, string? range,
        string? tti, Color side, bool inRange)
    {
        bool compact = CompactHud;
        float secondary = SecondaryScale;
        _targetLines.Clear();
        _targetBounds.Clear();
        // Three bottom-aligned rows retain the selected text size. The optional
        // model occupies its own column. Leave the bottom-right map its full width.
        float right = MinimapGeometry.CornerPanel(v.Width, v.Height).X - 6;
        float gap = 8 * _scale, line = _small.LineSpacing * _scale;
        float preview = PreviewWillDraw ? 3 * line : 0;
        float available = right - preview - 12 - gap;
        name = FitText(_small, name, available, _scale);
        string detail = string.Join("    ", new[] { speed, range, tti }.Where(t => t != null));
        detail = FitText(_small, detail, available, _scale);
        float Width(string text) => PaneTextSize(_small, text).X;
        // P1107 — a horizontal bar takes the ring's slot in this crowded layout too.
        // The slot's HEIGHT stays exactly today's (24 px at scale 1) so the three
        // bottom-aligned rows keep their shipped positions; only the ring becomes a
        // 48-wide bar, which is what makes it read as a bar rather than a gauge.
        float slot = 24 * _scale;
        float barSlot = compact ? 48 * _scale : slot;
        float labelScale = compact ? secondary : _scale;
        float labelHeight = _small.LineSpacing * labelScale;
        _targetScale = compact ? 1 : slot / 32;
        float gaugeWidth = Width("ARMOR") * labelScale / _scale + Width("SHIELD") * labelScale / _scale + 2 * barSlot + 4 * gap
            + (kb == null ? 0 : Width(kb)) + (inRange ? 2 * gap : 0);
        float width = Math.Max(gaugeWidth, Math.Max(Width(name), Width(detail)));
        float cx = Math.Min(v.Width / 2f, right - width / 2);
        float detailY = v.Height - 5 - line;
        float arcY = detailY - gap - slot / 2;
        float nameY = arcY - slot / 2 - gap - line;
        Line(name, new(cx - Width(name) / 2, nameY), side, _scale);
        if (detail.Length > 0) Line(detail, new(cx - Width(detail) / 2, detailY), Muted, _scale);
        _beforeArcs = _targetLines.Count;
        float x = cx - gaugeWidth / 2;
        _inRange = inRange ? new(x + gap, arcY) : null;
        if (inRange) x += 2 * gap;
        Line("ARMOR", new(x, arcY - labelHeight / 2), Muted, labelScale);
        x += Width("ARMOR") * labelScale / _scale + gap;
        _hullArc = new(x + barSlot / 2, arcY);
        PlaceSlotBar(x, arcY, barSlot, ref _hullBar);
        x += barSlot + gap;
        float bonusLeft = x;
        if (kb != null) x += Width(kb);
        x += gap;
        _shieldArc = new(x + barSlot / 2, arcY);
        PlaceSlotBar(x, arcY, barSlot, ref _shieldBar);
        Line("SHIELD", new(x + barSlot + gap, arcY - labelHeight / 2), Muted, labelScale);
        if (kb != null) Line(kb, new(bonusLeft, arcY - line / 2), CarryGold, _scale);
        _preview = new((int)(cx - width / 2 - preview - gap), (int)nameY, (int)preview, (int)preview);
        AddTargetGeometry();
    }

    /// <summary>P1107 — the slot a ring used to occupy in the crowded one-row target
    /// layout becomes a horizontal bar when the compact HUD is on.</summary>
    private void PlaceSlotBar(float left, float centreY, float slot, ref Rectangle bar)
    {
        if (!CompactHud) return;
        float height = CompactHudLayout.BarHeight * _scale;
        bar = new Rectangle((int)left, (int)(centreY - height / 2), (int)slot, Math.Max(1, (int)MathF.Round(height)));
    }

    /// <summary>P1107 — one slim target hull/shield bar: a recessed side-tinted track
    /// (so 0 % reads as an EMPTY bar exactly like native's empty gauge) plus the
    /// side-coloured fill. The fill colour is the pane's native target colour/alpha
    /// (<see cref="TargetPaneModel"/> <c>SideColor</c>, alpha 0.5).</summary>
    protected virtual void DrawCompactTargetBar(Rectangle bar, float fill, Color side)
    {
        if (bar.Width <= 0 || bar.Height <= 0) return;
        FillRect(bar.X, bar.Y, bar.Width, bar.Height, side * .22f);
        float inner = Math.Max(0f, (bar.Width - 2f) * Math.Clamp(fill, 0f, 1f));
        if (inner > 0f) FillRect(bar.X + 1, bar.Y + 1, inner, Math.Max(1, bar.Height - 2), side);
        OutlineRect(bar.X, bar.Y, bar.Width, bar.Height, side * .55f);
    }

    protected override void DrawTargetPane(HudModel model, Viewport viewport)
    {
        if (!model.TargetPaneVisible || model.Context.TargetPane is not { } tp) return;
        var offset = new Vector2(0, _slideY);
        var side = _targetLines[0].Color;
        void DrawLines(int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var (text, pos, color, scale) = _targetLines[i];
                DrawRawText(_small, text, pos + offset, color, scale);
            }
        }
        DrawLines(0, _beforeArcs);
        if (CompactHud)
        {
            // P1107 — slim horizontal bars, side-tinted; a null fraction (aleph /
            // out-of-sector) draws the empty track, exactly native's 0 %.
            var hull = _hullBar;
            hull.Offset(0, (int)MathF.Round(_slideY));
            var shields = _shieldBar;
            shields.Offset(0, (int)MathF.Round(_slideY));
            DrawCompactTargetBar(hull, CompactHudLayout.BarFill(tp.HullFraction), side);
            DrawCompactTargetBar(shields, CompactHudLayout.BarFill(tp.ShieldFraction), side);
        }
        else
        {
            DrawModernTargetGauge(_hullArc + offset, tp.HullFraction ?? 0, side, _targetScale);
            DrawModernTargetGauge(_shieldArc + offset, tp.ShieldFraction ?? 0, side, _targetScale);
        }
        if (TargetModelPreview is { } preview)
        {
            var dst = _preview;
            dst.Offset(0, (int)MathF.Round(_slideY));
            _sb.Draw(preview, dst, Color.White);
            Mark("Target model preview");
        }
        bool hasBonus = tp.IsShip && tp.KillBonusPercent.HasValue;
        int end = _targetLines.Count - (hasBonus ? 1 : 0);
        DrawLines(_beforeArcs, end);
        if (_inRange is { } c)
            foreach (var (x, y) in DiamondCorners)
                DrawLine(c + offset, c + offset + new Vector2(x * 7, y * 7) * _scale, _scale, side);
        if (hasBonus)
        {
            DrawLines(end, end + 1);
            Mark("Target kill bonus");
        }
        Mark("Target name", "Target type description", "Target side identity", "Target hull", "Target shield",
            "Target range or sector", "Target speed", "Target in weapon range", "Target time to intercept");
    }
}
