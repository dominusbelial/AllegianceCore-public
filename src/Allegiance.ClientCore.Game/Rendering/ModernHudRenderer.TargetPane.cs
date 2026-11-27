using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

public partial class ModernHudRenderer
{
    private readonly List<Rectangle> _ownBounds = new(32);
    private readonly List<Rectangle> _targetBounds = new(12);
    private readonly List<(string Text, Vector2 Position, Color Color)> _targetLines = new(8);
    private int _beforeArcs;
    private Vector2 _hullArc, _shieldArc;
    private Vector2? _inRange;
    private float _targetScale;
    private Rectangle _preview;

    // Round outwards to include antialiased SDF edges at fractional positions.
    private static Rectangle Extent(Vector2 p, Vector2 size, int pad = 1)
    {
        int x = (int)MathF.Floor(p.X) - pad, y = (int)MathF.Floor(p.Y) - pad;
        return new(x, y, (int)MathF.Ceiling(p.X + size.X) + pad - x, (int)MathF.Ceiling(p.Y + size.Y) + pad - y);
    }

    private void ReserveOwnText(string text, Vector2 position)
    {
        var size = _small.MeasureString(text);
        position.X += size.X * (1 - _scale) / 2; // DrawMeasuredText's centre correction
        var bounds = Extent(position, size * _scale);
        bounds.Inflate(3, 1); // the backing plate
        _ownBounds.Add(Rectangle.Union(bounds, _smallSdf.InkBounds(text, position, _scale)));
    }

    private void PrepareTarget(HudModel model, Viewport v)
    {
        if (!model.TargetPaneVisible || model.Context.TargetPane is not { } tp) return;
        _ownBounds.Clear();
        if (model.CentreHudVisible)
        {
            foreach (var g in model.Gauges)
            {
                if (!g.Visible) continue;
                var c = GaugeCenter(g, v);
                _ownBounds.Add(Extent(c - new Vector2(35 * _scale), new Vector2(70 * _scale)));
                string percent = HudStringCache.Percent(g.Fraction);
                ReserveOwnText(percent, c - _small.MeasureString(percent) / 2);
                ReserveOwnText(g.Label, new(c.X - _small.MeasureString(g.Label).X / 2, c.Y + 40 * _scale));
                if (g.Kind != GaugeKind.Thrust && g.Fraction <= (g.Kind == GaugeKind.Energy ? .05f : .15f))
                    ReserveOwnText("!", c + new Vector2(32, -32) * _scale);
            }
            VisitStatus(model, v, reserve: true);
        }

        float s = TargetPaneLayout.Scale(v.Width, v.Height, _scale);
        float x0 = TargetPaneLayout.OriginX(v.Width, v.Height, _scale);
        float slide = TargetPaneLayout.SlideOffset(model.Context.TargetPaneSlide, s);
        float y0 = TargetPaneLayout.OriginY(v.Width, v.Height, _scale) + slide;
        var side = new Color(tp.SideColor.R, tp.SideColor.G, tp.SideColor.B, tp.SideColor.A);
        if (_settings.ColorBlindSafe) side = HudPalette.Side(side);
        string name = TargetCaption(tp);
        string? range = tp.Distance is { } d ? "RANGE: " + HudFormat.FormatNativeNumber(d)
            : tp.SectorName is { } sector ? HudStringCache.Location(sector) : null;
        string? speed = tp.Speed is { } sp ? "SPEED: " + HudFormat.FormatNativeNumber(sp) : null;
        string? tti = tp.TimeToIntercept is { } t ? HudStringCache.Tti(t) : null;
        string? kb = tp.IsShip && tp.KillBonusPercent is { } n ? HudStringCache.KillBonusBadge(n) : null;

        _targetBounds.Clear();
        _targetLines.Clear();
        Vector2 Size(string text) => PaneTextSize(_small, text);
        // Native pane anchors (Artwork/dialog.mdl:4043-4081,4192-4242).
        Line(name, new(x0 + 320 * s - Size(name).X / 2, y0 - 125 * s - Size(name).Y), side);
        if (range != null) Line(range, new(x0 + 400 * s, y0 - 33 * s - Size(range).Y), Muted);
        if (speed != null) Line(speed, new(x0 + 242 * s - Size(speed).X, y0 - 33 * s - Size(speed).Y), Muted);
        _beforeArcs = _targetLines.Count;
        _targetScale = s;
        _hullArc = new(x0 + 276.5f * s, y0 - 83.5f * s);
        _shieldArc = new(x0 + 409 * s, _hullArc.Y);
        _inRange = tp.InRange ? new(x0 + 200 * s, y0 - 90 * s) : null;
        _preview = global::Allegiance.ClientCore.MonoGame.Rendering.TargetModelPreview.PaneRect(x0, y0, s);
        Line("ARMOR", new(_hullArc.X - 16 * s - 8 * s - Size("ARMOR").X, _hullArc.Y - Size("ARMOR").Y / 2), Muted);
        Line("SHIELD", new(_shieldArc.X + 16 * s + 8 * s, _shieldArc.Y - Size("SHIELD").Y / 2), Muted);
        if (tti != null) Line(tti, new(x0 + 242 * s - Size(tti).X, y0 - 10 * s - Size(tti).Y), Muted);
        if (kb != null)
        {
            var (left, top) = TargetPaneLayout.ExperienceNumberTopLeft(x0, y0, s, Size(kb).X);
            Line(kb, new(left, top), CarryGold);
        }
        AddTargetGeometry();
        // Decide at the fully open position so the native slide remains continuous.
        _slideY = 0;
        if (TargetIntersectsOwn((int)MathF.Round(slide)))
        {
            CompactTarget(v, name, kb, speed, range, tti, side, tp.InRange);
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

    private float _slideY;

    private void Line(string text, Vector2 position, Color color)
    {
        _targetLines.Add((text, position, color));
        _targetBounds.Add(_smallSdf.InkBounds(text, position, _scale));
    }

    private void AddTargetGeometry()
    {
        var radius = new Vector2(17 * _targetScale);
        _targetBounds.Add(Extent(_hullArc - radius, 2 * radius));
        _targetBounds.Add(Extent(_shieldArc - radius, 2 * radius));
        if (PreviewWillDraw) _targetBounds.Add(_preview);
        if (_inRange is { } c) _targetBounds.Add(Extent(c - new Vector2(8 * _scale), new Vector2(16 * _scale)));
    }

    private void CompactTarget(Viewport v, string name, string? kb, string? speed, string? range,
        string? tti, Color side, bool inRange)
    {
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
        float diameter = 24 * _scale;
        _targetScale = diameter / 32;
        float gaugeWidth = Width("ARMOR") + Width("SHIELD") + 2 * diameter + 4 * gap
            + (kb == null ? 0 : Width(kb)) + (inRange ? 2 * gap : 0);
        float width = Math.Max(gaugeWidth, Math.Max(Width(name), Width(detail)));
        float cx = Math.Min(v.Width / 2f, right - width / 2);
        float detailY = v.Height - 5 - line;
        float arcY = detailY - gap - diameter / 2;
        float nameY = arcY - diameter / 2 - gap - line;
        Line(name, new(cx - Width(name) / 2, nameY), side);
        if (detail.Length > 0) Line(detail, new(cx - Width(detail) / 2, detailY), Muted);
        _beforeArcs = _targetLines.Count;
        float x = cx - gaugeWidth / 2;
        _inRange = inRange ? new(x + gap, arcY) : null;
        if (inRange) x += 2 * gap;
        Line("ARMOR", new(x, arcY - line / 2), Muted);
        x += Width("ARMOR") + gap;
        _hullArc = new(x + diameter / 2, arcY);
        x += diameter + gap;
        float bonusLeft = x;
        if (kb != null) x += Width(kb);
        x += gap;
        _shieldArc = new(x + diameter / 2, arcY);
        Line("SHIELD", new(x + diameter + gap, arcY - line / 2), Muted);
        if (kb != null) Line(kb, new(bonusLeft, arcY - line / 2), CarryGold);
        _preview = new((int)(cx - width / 2 - preview - gap), (int)nameY, (int)preview, (int)preview);
        AddTargetGeometry();
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
                var (text, pos, color) = _targetLines[i];
                DrawRawText(_small, text, pos + offset, color);
            }
        }
        DrawLines(0, _beforeArcs);
        DrawModernTargetGauge(_hullArc + offset, tp.HullFraction ?? 0, side, _targetScale);
        DrawModernTargetGauge(_shieldArc + offset, tp.ShieldFraction ?? 0, side, _targetScale);
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
