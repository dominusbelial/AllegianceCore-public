using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Jet;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace P1042Proof;

/// <summary>
/// P1042 — the Jet screen chrome's recording surface. It implements the production
/// primitive interface (so the renderer draws into it instead of the device) plus the
/// element/rect audit channels, and measures with the REAL HUD fonts, so every box in
/// the audit is the box the production SpriteBatch would ink.
///
/// <para>Keyline runs are collapsed: the painter draws the eight near-black offsets of a
/// glyph and then the ink; all nine share the same text and a position within 2 px, so
/// they are one visual run (otherwise same-element overlap would count by construction).</para>
/// </summary>
public sealed class ScreenRecorder : IJetVectorSurface, IJetElementAudit, IScreenRectAudit
{
    public readonly record struct TextRun(string Element, string Text, RectangleF Box);
    public readonly record struct ControlRun(string Family, string Id, RectangleF Box);

    private readonly SpriteFont _small;
    private readonly SpriteFont _large;
    private string _element = "";
    private readonly HashSet<string> _drawn = new(StringComparer.Ordinal);

    public readonly List<TextRun> Texts = new();
    public readonly List<RectangleF> Fills = new();
    public readonly List<(Vector2 A, Vector2 B, float Width)> Lines = new();
    public readonly List<ControlRun> Controls = new();
    public readonly List<ControlRun> Plates = new();
    /// <summary>P1042b — text the shared CONTENT renderer painted inside a control the
    /// chrome also draws (port-verb labels, native hover string).</summary>
    public readonly List<TextRun> ContentRuns = new();
    public readonly List<string> DrawnElements = new();

    public ScreenRecorder(SpriteFont small, SpriteFont large)
    {
        _small = small;
        _large = large;
    }

    public void Begin() { }
    public void End() { }

    public void Line(Vector2 a, Vector2 b, float width, Color color) => Lines.Add((a, b, width));

    public void Fill(Vector2 min, Vector2 size, Color color)
        => Fills.Add(new RectangleF(min.X, min.Y, size.X, size.Y));

    public void Text(string text, Vector2 position, bool large, float scale, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 size = Measure(text, large, scale);
        var box = new RectangleF(position.X, position.Y, size.X, size.Y);
        // Collapse the keyline offsets into the run they surround.
        for (int i = 0; i < Texts.Count; i++)
        {
            var run = Texts[i];
            if (run.Element == _element && run.Text == text
                && MathF.Abs(run.Box.X - box.X) <= 2f && MathF.Abs(run.Box.Y - box.Y) <= 2f)
            {
                Texts[i] = run with { Box = Union(run.Box, box) };
                return;
            }
        }
        Texts.Add(new(_element, text, box));
    }

    public Vector2 Measure(string text, bool large, float scale)
        => (large ? _large : _small).MeasureString(text) * scale;

    public void Element(string id) => _element = id;

    public void Drawn(string id)
    {
        if (_drawn.Add(id)) DrawnElements.Add(id);
    }

    public void Control(string family, string id, RectangleF drawn)
        => Controls.Add(new(family, id, drawn));

    public void Plate(string family, string id, RectangleF box)
        => Plates.Add(new(family, id, box));

    /// <summary>P1042b — a content text run the shared renderer drew at the same
    /// control id the chrome uses.</summary>
    public void Content(string family, string id, RectangleF box, string text)
        => ContentRuns.Add(new(id, text, box));

    private static RectangleF Union(RectangleF a, RectangleF b)
    {
        float x = MathF.Min(a.X, b.X), y = MathF.Min(a.Y, b.Y);
        return new(x, y, MathF.Max(a.X + a.Width, b.X + b.Width) - x,
            MathF.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }
}

/// <summary>P1042 — the layout audit of a recorded Jet screen frame.</summary>
public static class ScreenAudit
{
    public readonly record struct HitRect(string Family, string Id, RectangleF Box);

    public sealed class Prepared
    {
        public readonly List<HitRect> HitRects = new();
    }

    public sealed record Result(int Controls, int HitVsDraw, int StrokeVsText, int TextOutsidePlate,
        int TextOffScreen, int TextOverlapCross, int TextOverlapSame, int ContentOverlap,
        int Failures, string[] Samples);

    public static Prepared Intro(ScreenRecorder recorder, IntroMainFrame frame)
    {
        var prepared = new Prepared();
        for (int i = 0; i < frame.Labels.Count; i++)
        {
            var row = frame.Layout.Rows[i];
            prepared.HitRects.Add(new(ScreenActions.Intro, ScreenActions.IntroRow(i),
                new RectangleF(row.X, row.Y, row.Width, row.Height)));
        }
        return prepared;
    }

    public static Prepared Hangar(ScreenRecorder recorder, HangarScreenFrame frame)
    {
        var prepared = new Prepared();
        foreach (var action in HangarControls.Actions(frame.Developments))
        {
            var rect = HangarScreen.Rect(frame.Width, frame.Height, action);
            prepared.HitRects.Add(new(ScreenActions.Hangar, ScreenActions.HangarElement(action),
                new RectangleF(rect.X, rect.Y, rect.Width, rect.Height)));
        }
        return prepared;
    }

    /// <summary>
    /// Evaluate every audit class. Hit-vs-draw counts a drawn control whose rectangle is
    /// not exactly the hit rectangle for that action (and a drawn control with no hit
    /// action at all). Text classes use the real measured ink boxes.
    /// </summary>
    public static Result Evaluate(ScreenRecorder recorder, int width, int height, List<HitRect> hitRects)
    {
        var samples = new List<string>();
        int hitVsDraw = 0;
        foreach (var control in recorder.Controls)
        {
            HitRect? match = null;
            foreach (var hit in hitRects)
                if (hit.Family == control.Family && hit.Id == control.Id) { match = hit; break; }
            if (match is not { } rect)
            {
                hitVsDraw++;
                if (samples.Count < 6) samples.Add($"hit-vs-draw: no hit rect for {control.Family}/{control.Id}");
                continue;
            }
            if (!Same(rect.Box, control.Box))
            {
                hitVsDraw++;
                if (samples.Count < 6)
                    samples.Add($"hit-vs-draw {control.Id}: drawn {Fmt(control.Box)} hit {Fmt(rect.Box)}");
            }
        }

        int strokeVsText = 0;
        foreach (var (a, b, _) in recorder.Lines)
        foreach (var text in recorder.Texts)
        {
            var box = Shrink(text.Box, 0.5f);
            if (Contains(box, a) || Contains(box, b) || Contains(box, new Vector2((a.X + b.X) / 2f, (a.Y + b.Y) / 2f)))
            {
                strokeVsText++;
                if (samples.Count < 12) samples.Add($"stroke-vs-text: {text.Element} '{text.Text}' crossed at {Fmt(text.Box)}");
            }
        }

        int outsidePlate = 0;
        foreach (var text in recorder.Texts)
        {
            bool hasPlate = false, inside = false;
            foreach (var plate in recorder.Plates)
            {
                if (plate.Id != text.Element) continue;
                hasPlate = true;
                if (Contains(plate.Box, text.Box)) { inside = true; break; }
            }
            if (hasPlate && !inside)
            {
                outsidePlate++;
                if (samples.Count < 18) samples.Add($"text-outside-plate: {text.Element} '{text.Text}' at {Fmt(text.Box)}");
            }
        }

        int offScreen = 0;
        foreach (var text in recorder.Texts)
        {
            if (text.Box.X < 0f || text.Box.Y < 0f || text.Box.X + text.Box.Width > width + 0.01f
                || text.Box.Y + text.Box.Height > height + 0.01f)
            {
                offScreen++;
                if (samples.Count < 24) samples.Add($"text-off-screen: {text.Element} '{text.Text}' at {Fmt(text.Box)}");
            }
        }

        int cross = 0, same = 0, contentOverlap = 0;
        for (int i = 0; i < recorder.Texts.Count; i++)
        for (int j = i + 1; j < recorder.Texts.Count; j++)
        {
            var a = recorder.Texts[i];
            var b = recorder.Texts[j];
            if (!Intersects(a.Box, b.Box)) continue;
            if (a.Element == b.Element)
            {
                if (a.Text == b.Text) continue;   // the same run measured twice
                same++;
                if (samples.Count < 30) samples.Add($"overlap-same: {a.Element} '{a.Text}' / '{b.Text}'");
            }
            else
            {
                cross++;
                if (samples.Count < 36) samples.Add($"overlap-cross: {a.Element} '{a.Text}' / {b.Element} '{b.Text}'");
            }
        }

        // P1042b — chrome text crossing CONTENT text of the SAME control: the doubled
        // Buy/Def class. Counted as a cross overlap (the two labels are different runs of
        // the same control); ContentOverlap is the subset this rule found.
        for (int i = 0; i < recorder.Texts.Count; i++)
        {
            var text = recorder.Texts[i];
            for (int j = 0; j < recorder.ContentRuns.Count; j++)
            {
                var content = recorder.ContentRuns[j];
                if (content.Element != text.Element) continue;
                if (!Intersects(text.Box, content.Box)) continue;
                cross++;
                contentOverlap++;
                if (samples.Count < 42)
                    samples.Add($"overlap-content: {text.Element} chrome '{text.Text}' over content '{content.Text}'");
            }
        }

        int failures = hitVsDraw + strokeVsText + outsidePlate + offScreen + cross + same;
        return new(recorder.Controls.Count, hitVsDraw, strokeVsText, outsidePlate, offScreen, cross, same,
            contentOverlap, failures, samples.ToArray());
    }

    private static bool Same(RectangleF a, RectangleF b)
        => MathF.Abs(a.X - b.X) <= 0.01f && MathF.Abs(a.Y - b.Y) <= 0.01f
        && MathF.Abs(a.Width - b.Width) <= 0.01f && MathF.Abs(a.Height - b.Height) <= 0.01f;

    private static RectangleF Shrink(RectangleF box, float by)
        => new(box.X + by, box.Y + by, MathF.Max(0f, box.Width - 2f * by), MathF.Max(0f, box.Height - 2f * by));

    private static bool Contains(RectangleF box, Vector2 p)
        => p.X >= box.X && p.X <= box.X + box.Width && p.Y >= box.Y && p.Y <= box.Y + box.Height;

    private static bool Contains(RectangleF outer, RectangleF inner)
        => inner.X >= outer.X - 0.01f && inner.Y >= outer.Y - 0.01f
        && inner.X + inner.Width <= outer.X + outer.Width + 0.01f
        && inner.Y + inner.Height <= outer.Y + outer.Height + 0.01f;

    private static bool Intersects(RectangleF a, RectangleF b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static string Fmt(RectangleF r) => $"{r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#}";
}
