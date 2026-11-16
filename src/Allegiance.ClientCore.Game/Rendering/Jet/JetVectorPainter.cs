using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>P1035 — the submission surface the Jet painter emits to. Production
/// is <see cref="JetSpriteBatchSurface"/> (Game1's SpriteBatch, white pixel and the
/// two HUD fonts); tests supply a recording surface or the null surface, so the
/// geometry, smoke and allocation gates run without a graphics device.</summary>
public interface IJetVectorSurface
{
    /// <summary>Open/close the skin's own SpriteBatch session (main HUD pass only —
    /// overlay text is drawn inside the caller's session, like Classic).</summary>
    void Begin();
    void End();

    /// <summary>One straight stroke of <paramref name="width"/> px, centred on the
    /// segment.</summary>
    void Line(Vector2 a, Vector2 b, float width, Color color);

    /// <summary>A filled axis-aligned rectangle (plate or bar fill).</summary>
    void Fill(Vector2 min, Vector2 size, Color color);

    /// <summary>One text run. <paramref name="large"/> selects the HUD's large font,
    /// <paramref name="scale"/> the density scale.</summary>
    void Text(string text, Vector2 position, bool large, float scale, Color color);

    /// <summary>The text run's ink size at <paramref name="scale"/>.</summary>
    Vector2 Measure(string text, bool large, float scale);
}

/// <summary>P1038b — an optional audit channel for ICON glyphs: the drawing surface may
/// implement it to receive the box of every icon mark the painter composes (annunciator,
/// caution and inventory state glyphs), so the proof can measure that each icon is at
/// least as tall as the cap height of the text it belongs to. Production and test surfaces
/// do not implement it, so the check costs one type test per icon and draws nothing.</summary>
public interface IJetGlyphAudit
{
    /// <summary>Record one icon glyph: its drawn box and the minimum (cap-height) size the
    /// word it introduces needs it to reach.</summary>
    void Glyph(string id, RectangleF box, float minimumHeight);
}

/// <summary>P1035 — the production Jet surface: Game1's shared SpriteBatch, the
/// 1×1 white pixel and the two classic HUD fonts. Borrows every resource; it owns
/// nothing and therefore has nothing to dispose.</summary>
public sealed class JetSpriteBatchSurface : IJetVectorSurface
{
    private readonly SpriteBatch _batch;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;
    private readonly SpriteFont _large;

    public JetSpriteBatchSurface(SpriteBatch batch, Texture2D white, SpriteFont small, SpriteFont large)
    {
        _batch = batch;
        _white = white;
        _small = small;
        _large = large;
    }

    public void Begin() => _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
    public void End() => _batch.End();

    public void Line(Vector2 a, Vector2 b, float width, Color color)
    {
        Vector2 delta = b - a;
        float length = delta.Length();
        if (length < 0.001f) return;
        _batch.Draw(_white, a + delta * 0.5f, null, color, MathF.Atan2(delta.Y, delta.X),
            new Vector2(0.5f, 0.5f), new Vector2(length, width), SpriteEffects.None, 0f);
    }

    public void Fill(Vector2 min, Vector2 size, Color color)
        => _batch.Draw(_white, new Rectangle((int)MathF.Round(min.X), (int)MathF.Round(min.Y),
            (int)MathF.Round(size.X), (int)MathF.Round(size.Y)), color);

    public void Text(string text, Vector2 position, bool large, float scale, Color color)
        => _batch.DrawString(large ? _large : _small, text, position, color, 0f, Vector2.Zero,
            scale, SpriteEffects.None, 0f);

    public Vector2 Measure(string text, bool large, float scale)
        => (large ? _large : _small).MeasureString(text) * scale;
}

/// <summary>
/// P1035 — the Jet vector primitives every later Jet story draws with (P972 §4.1):
/// line/polyline with stroke width, circle/arc, corner brackets, a filled plate and
/// keyline-backed text. Every primitive paints a near-black OUTER UNDER-STROKE
/// first, then the requested ink, so a glyph stays legible over bright space
/// without any additive bloom. The standard stroke is 1.5 px at the 1080p
/// reference, multiplied by the existing density/Modern HUD scale and floored at
/// <see cref="MinimumLineStroke"/> (1.5 physical px). Since P1036 the under-stroke
/// on an ink LINE is capped at half the ink width (<see cref="LineKeylineAllowance"/>),
/// so the ink is always the dominant stroke — a 1 px hairline never reads as almost black.
/// <para>No per-frame allocation: the polyline point buffer is pre-sized, the
/// palette is copied once at construction and every loop is a plain <c>for</c>.</para>
/// </summary>
public sealed class JetVectorPainter
{
    /// <summary>The authored stroke width at 1080p / 1.0 desktop scale (P972 §4.1).</summary>
    public const float ReferenceStroke = 1.5f;

    /// <summary>
    /// The physical floor for an INK line. P972 §4.1's "1 physical pixel at
    /// 800×480" is a minimum; P1036 raises the line floor to 1.5 physical pixels
    /// because a 1 px quad centred on a fractional row splits its coverage across
    /// two rows at ~50 % and the stroke then reads as dark green inside the
    /// keyline (the 800×480 bright review finding). At 1.5 px one row is always
    /// fully covered, so the ink stays bright and dominant.
    /// </summary>
    public const float MinimumLineStroke = 1.5f;

    /// <summary>The near-black under-stroke allowance on each side of a glyph.</summary>
    public const float ReferenceKeyline = 1.0f;

    /// <summary>Text keyline offsets: the eight surrounding directions, drawn first.</summary>
    private static readonly Vector2[] TextKeylineOffsets =
    [
        new(-1f, 0f), new(1f, 0f), new(0f, -1f), new(0f, 1f),
        new(-1f, -1f), new(1f, -1f), new(-1f, 1f), new(1f, 1f),
    ];

    private const int MaxPathPoints = 72;
    private readonly Vector2[] _path = new Vector2[MaxPathPoints];
    private readonly IJetVectorSurface _surface;

    public JetVectorPainter(IJetVectorSurface surface, Color primary, Color dim, Color hot, Color plate, Color keyline)
    {
        _surface = surface;
        Primary = primary;
        Dim = dim;
        Hot = hot;
        Plate = plate;
        Keyline = keyline;
    }

    public IJetVectorSurface Surface => _surface;

    /// <summary>The density/Modern scale for this frame (see <see cref="ModernHudScale"/>).</summary>
    public float Scale { get; set; } = 1f;

    public Color Primary { get; }
    public Color Dim { get; }
    public Color Hot { get; }
    /// <summary>Near-black backing behind a text plate.</summary>
    public Color Plate { get; }
    /// <summary>Near-black outer under-stroke colour.</summary>
    public Color Keyline { get; }

    /// <summary>The standard stroke: 1.5 px × scale, never below 1.5 physical pixels.</summary>
    public float Stroke => MathF.Max(MinimumLineStroke, ReferenceStroke * Scale);

    /// <summary>The keyline allowance on one side, never below one physical pixel.</summary>
    public float KeylineWidth => MathF.Max(1f, ReferenceKeyline * Scale);

    /// <summary>
    /// The under-stroke allowance on one side of an INK LINE of
    /// <paramref name="width"/> px: at most one physical pixel and never more than
    /// half the ink width. P1036 review follow-up: at 800×480 the 1 px green ink
    /// used to sit inside a 3 px near-black band, so the gun cross read almost
    /// black. Keeping the dark allowance at ≤ ink/2 makes the ink the dominant
    /// stroke at every density while the outline still separates it from a bright
    /// backdrop. Text keeps the full <see cref="KeylineWidth"/> (glyph strokes are
    /// far denser than a hairline).
    /// </summary>
    public float LineKeylineAllowance(float width)
        => MathF.Min(KeylineWidth, MathF.Max(0.5f, MathF.Max(width, Hairline) * 0.5f));

    /// <summary>A hairline that still resolves to a whole physical pixel.</summary>
    public float Hairline => MathF.Max(1f, Scale);

    /// <summary>One line with its keyline under-stroke.</summary>
    public void Line(Vector2 a, Vector2 b, float width, Color color)
    {
        width = MathF.Max(width, Hairline);
        _surface.Line(a, b, width + LineKeylineAllowance(width) * 2f, Keyline);
        _surface.Line(a, b, width, color);
    }

    /// <summary>
    /// A polyline (optionally closed). The whole keyline path is emitted first and
    /// only then the ink pass, so a bright segment can never punch a hole in the
    /// shared dark outline at a joint.
    /// </summary>
    public void Polyline(ReadOnlySpan<Vector2> points, float width, Color color, bool closed = false)
    {
        if (points.Length < 2) return;
        width = MathF.Max(width, Hairline);
        float keyline = width + LineKeylineAllowance(width) * 2f;
        int last = closed ? points.Length : points.Length - 1;
        for (int i = 0; i < last; i++)
            _surface.Line(points[i], points[(i + 1) % points.Length], keyline, Keyline);
        for (int i = 0; i < last; i++)
            _surface.Line(points[i], points[(i + 1) % points.Length], width, color);
    }

    /// <summary>A full circle as a closed polyline.</summary>
    public void Circle(Vector2 center, float radius, float width, Color color)
        => Arc(center, radius, 0f, MathHelper.TwoPi, width, color);

    /// <summary>An arc of <paramref name="sweep"/> radians starting at <paramref name="start"/>.</summary>
    public void Arc(Vector2 center, float radius, float start, float sweep, float width, Color color)
    {
        if (radius <= 0f) return;
        int segments = ArcSegments(radius);
        if (segments > MaxPathPoints - 1) segments = MaxPathPoints - 1;
        for (int i = 0; i <= segments; i++)
        {
            float angle = start + sweep * (i / (float)segments);
            _path[i] = new Vector2(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius);
        }
        Polyline(_path.AsSpan(0, segments + 1), width, color);
    }

    /// <summary>Four corner strokes (a targeting/readout frame), no full rectangle.</summary>
    public void CornerBrackets(RectangleF box, float armLength, float width, Color color)
    {
        if (box.Width <= 0f || box.Height <= 0f) return;
        float arm = MathF.Min(armLength, MathF.Min(box.Width, box.Height) * 0.5f);
        width = MathF.Max(width, Hairline);
        float keyline = width + LineKeylineAllowance(width) * 2f;
        float x0 = box.X, y0 = box.Y, x1 = box.X + box.Width, y1 = box.Y + box.Height;
        for (int pass = 0; pass < 2; pass++)
        {
            bool ink = pass == 1;
            float w = ink ? width : keyline;
            Color c = ink ? color : Keyline;
            // Each corner: one horizontal arm and one vertical arm.
            _surface.Line(new(x0, y0), new(x0 + arm, y0), w, c);
            _surface.Line(new(x0, y0), new(x0, y0 + arm), w, c);
            _surface.Line(new(x1, y0), new(x1 - arm, y0), w, c);
            _surface.Line(new(x1, y0), new(x1, y0 + arm), w, c);
            _surface.Line(new(x0, y1), new(x0 + arm, y1), w, c);
            _surface.Line(new(x0, y1), new(x0, y1 - arm), w, c);
            _surface.Line(new(x1, y1), new(x1 - arm, y1), w, c);
            _surface.Line(new(x1, y1), new(x1, y1 - arm), w, c);
        }
    }

    /// <summary>A filled plate (near-black green) with a keyline outline — the
    /// backing a numeric readout sits on. <paramref name="alpha"/> keeps it from
    /// hiding the world behind it.</summary>
    public void FillPlate(RectangleF box, float alpha = 0.85f)
    {
        if (box.Width <= 0f || box.Height <= 0f) return;
        _surface.Fill(new(box.X, box.Y), new(box.Width, box.Height), Plate * alpha);
        float w = KeylineWidth;
        _surface.Fill(new(box.X, box.Y), new(box.Width, w), Keyline);
        _surface.Fill(new(box.X, box.Y + box.Height - w), new(box.Width, w), Keyline);
        _surface.Fill(new(box.X, box.Y), new(w, box.Height), Keyline);
        _surface.Fill(new(box.X + box.Width - w, box.Y), new(w, box.Height), Keyline);
    }

    /// <summary>
    /// Keyline-backed text: eight near-black offsets around the anchor, then the
    /// ink. The anchor is the text's top-left unless <paramref name="centered"/>.
    /// </summary>
    public void Text(string text, Vector2 anchor, bool large, float scale, Color color, bool centered = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 size = _surface.Measure(text, large, scale);
        Vector2 position = centered ? new(anchor.X - size.X * 0.5f, anchor.Y - size.Y * 0.5f) : anchor;
        float offset = KeylineWidth;
        for (int i = 0; i < TextKeylineOffsets.Length; i++)
        {
            Vector2 delta = TextKeylineOffsets[i] * offset;
            _surface.Text(text, position + delta, large, scale, Keyline);
        }
        _surface.Text(text, position, large, scale, color);
    }

    /// <summary>The ink size a <see cref="Text"/> run would occupy.</summary>
    public Vector2 Measure(string text, bool large, float scale) => _surface.Measure(text, large, scale);

    /// <summary>P1038b — report one icon glyph to the optional <see cref="IJetGlyphAudit"/>
    /// channel (used by the proof's minimum-glyph-size check). No-op on the production and
    /// test surfaces, which do not implement it.</summary>
    public void Glyph(string id, RectangleF box, float minimumHeight)
    {
        if (_surface is IJetGlyphAudit audit) audit.Glyph(id, box, minimumHeight);
    }

    /// <summary>Enough segments that a circle reads as a circle at this radius,
    /// bounded by the pre-sized point buffer.</summary>
    private static int ArcSegments(float radius) => Math.Clamp((int)(radius * 1.2f), 10, MaxPathPoints - 2);
}

/// <summary>A float rectangle in screen pixels (the renderers' own geometry type;
/// avoids depending on the integer measure for sub-pixel vector layout).</summary>
public readonly record struct RectangleF(float X, float Y, float Width, float Height);
