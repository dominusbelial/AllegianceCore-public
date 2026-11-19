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

    /// <summary>P1055 — a CONTENT text run: text the shared content renderer paints (or the
    /// frame that renderer paints from resolves). <paramref name="Owner"/> is the content
    /// panel the run belongs to (a run inside its own panel is not a collision).</summary>
    public readonly record struct ContentTextRun(string Element, string Owner, string Text, RectangleF Box, bool Large);
    /// <summary>P1055 — a content panel/box: a painted region the content renderer owns
    /// (the map panel, the sector-info pane, the leaderboard panel).</summary>
    public readonly record struct ContentBox(string Element, RectangleF Box);

    /// <summary>P1055 — every content text run this scene paints, in draw order.</summary>
    public readonly List<ContentTextRun> ContentTextRuns = new();
    /// <summary>P1055 — every content panel/box this scene paints.</summary>
    public readonly List<ContentBox> ContentBoxes = new();

    /// <summary>P1055 — record one content text run at its resolved anchor, measured with
    /// the real HUD font (the box the SpriteBatch inks).</summary>
    public void ContentText(string element, string owner, string text, Vector2 position,
        float scale, bool large = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var size = Measure(text, large, scale);
        ContentTextRuns.Add(new(element, owner, text,
            new RectangleF(position.X, position.Y, size.X, size.Y), large));
    }

    /// <summary>P1055 — record one content panel/box (a painted region, not chrome).</summary>
    public void ContentPanel(string element, RectangleF box) => ContentBoxes.Add(new(element, box));

    /// <summary>P1042b — a content text run the shared renderer drew at the same
    /// control id the chrome uses.</summary>
    public void Content(string family, string id, RectangleF box, string text)
        => ContentTextRuns.Add(new(id, family, text, box, false));

    /// <summary>P1042c — record a MEASURED box that no chrome primitive drew (the pre-fix
    /// career title geometry, used to prove the title-vs-tab rule flags before the fix).</summary>
    public void RecordSynthetic(string element, string text, RectangleF box)
        => Texts.Add(new(element, text, box));

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
        int TitleVsTab, int Failures, string[] Samples);

    /// <summary>P1055 — the content-vs-content audit counts for one (scene, size, skin).</summary>
    public sealed record ContentResult(int Runs, int Boxes, int TextText, int TextBox,
        int OffScreen, int Failures, string[] Samples);

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
    /// <summary>
    /// P1042c — item 0b's rule: a screen TITLE box that overlaps, or comes within
    /// <paramref name="gap"/> px of, a tab-row box. <paramref name="titleId"/> is the
    /// element the chrome recorded the title under; a tab row is any control whose element
    /// belongs to <paramref name="tabFamily"/> and whose id names a tab. Counts each violation
    /// pairing (title, tab) — a title that crowds two tabs counts twice.
    /// </summary>
    public static int TitleVsTab(ScreenRecorder recorder, string titleId, string tabFamily, float gap,
        List<string>? samples = null)
    {
        int count = 0;
        for (int i = 0; i < recorder.Texts.Count; i++)
        {
            var title = recorder.Texts[i];
            if (title.Element != titleId) continue;
            for (int j = 0; j < recorder.Controls.Count; j++)
            {
                var tab = recorder.Controls[j];
                if (tab.Family != tabFamily) continue;
                float clearance = tab.Box.Y - (title.Box.Y + title.Box.Height);
                if (clearance < gap)
                {
                    count++;
                    samples?.Add($"title-vs-tab: '{title.Text}' bottom {title.Box.Y + title.Box.Height:0.#} " +
                        $"tab '{tab.Id}' top {tab.Box.Y:0.#} gap {clearance:0.#} < {gap}");
                }
            }
        }
        return count;
    }

    public static Result Evaluate(ScreenRecorder recorder, int width, int height, List<HitRect> hitRects,
        int titleVsTab = 0)
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
            // The INK box: a bracket arm on the neighbouring row's edge may legally touch the
            // run's line box (leading), never its ink.
            var box = InkBox(text.Box);
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
        // P1042c — a recorded run's box is the UNION of the ink and its eight 1 px keyline
        // offsets. The keyline is a dark backing that may legally touch a neighbouring run
        // (and the plate edge); only INK overlap counts, so each box is shrunk by 1 px.
        for (int i = 0; i < recorder.Texts.Count; i++)
        for (int j = i + 1; j < recorder.Texts.Count; j++)
        {
            var a = recorder.Texts[i];
            var b = recorder.Texts[j];
            if (!Intersects(InkBox(a.Box), InkBox(b.Box))) continue;
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
                if (!Intersects(InkBox(text.Box), InkBox(content.Box))) continue;
                cross++;
                contentOverlap++;
                if (samples.Count < 42)
                    samples.Add($"overlap-content: {text.Element} chrome '{text.Text}' over content '{content.Text}'");
            }
        }

        int failures = hitVsDraw + strokeVsText + outsidePlate + offScreen + cross + same + titleVsTab;
        return new(recorder.Controls.Count, hitVsDraw, strokeVsText, outsidePlate, offScreen, cross, same,
            contentOverlap, titleVsTab, failures, samples.ToArray());
    }

    /// <summary>
    /// P1055 — the CONTENT-vs-CONTENT audit. The P1042b/P1042c classes compare the Jet chrome
    /// with the content text it sits over; two pieces of the game's own text overlapping each
    /// other were never checked (Claude's review of the P1042c captures found the Benchmark
    /// description over its own rows and the sector-info pane over the alert line). This class
    /// measures content runs the content renderer paints:
    /// <list type="bullet">
    /// <item><c>content-overlap</c> — two content text runs whose INK boxes intersect by more
    /// than <paramref name="tolerance"/> px on BOTH axes. Reported with the shared width/height
    /// in px (the overlap area).</item>
    /// <item><c>content-panel-overlap</c> — a content text run crossing a content panel/box it
    /// does not belong to (a run inside its OWN panel — a pane line in the pane box — is not a
    /// collision; a run that crosses another panel's edge, or that a later panel paints over,
    /// is).</item>
    /// <item><c>content-off-screen</c> — a content text box past the viewport.</item>
    /// </list>
    /// The tolerance is 1 px of penetration on each axis, measured on the ink box (the run's
    /// line pitch is its leading, not ink), so two runs that abut exactly on a 20 px line pitch
    /// are not counted while a real collision is. <paramref name="findings"/> receives one line
    /// per finding for the report's table.
    /// </summary>
    public static ContentResult EvaluateContent(ScreenRecorder recorder, int width, int height,
        List<string> findings, float tolerance = 1f, string? screen = null)
    {
        var samples = new List<string>();
        int textText = 0, textBox = 0, offScreen = 0;
        string tag = screen is null ? "" : screen + ": ";

        for (int i = 0; i < recorder.ContentTextRuns.Count; i++)
        for (int j = i + 1; j < recorder.ContentTextRuns.Count; j++)
        {
            var a = recorder.ContentTextRuns[i];
            var b = recorder.ContentTextRuns[j];
            var (w, h) = Overlap(InkBox(a.Box), InkBox(b.Box));
            if (w <= tolerance || h <= tolerance) continue;
            textText++;
            string line = $"{tag}content-overlap {a.Element} '{Clip(a.Text)}' / {b.Element} '{Clip(b.Text)}' " +
                $"overlap {w:0.#}x{h:0.#} px ({w * h:0} px^2)";
            findings.Add(line);
            if (samples.Count < 8) samples.Add(line);
        }

        foreach (var run in recorder.ContentTextRuns)
        foreach (var box in recorder.ContentBoxes)
        {
            if (box.Element == run.Owner) continue;             // the run's own panel
            // Nested panels are legal: the sector-info pane sits INSIDE the starmap panel, so
            // the pane's own lines are "inside the map panel" by construction. A panel that
            // fully contains the run's OWNER panel is an ancestor, not a collision.
            var owner = recorder.ContentBoxes.Find(b => b.Element == run.Owner);
            if (owner.Element is not null && Contains(box.Box, owner.Box)) continue;
            var (w, h) = Overlap(InkBox(run.Box), box.Box);
            if (w <= tolerance || h <= tolerance) continue;
            textBox++;
            string line = $"{tag}content-panel-overlap {run.Element} '{Clip(run.Text)}' over panel " +
                $"{box.Element} by {w:0.#}x{h:0.#} px";
            findings.Add(line);
            if (samples.Count < 16) samples.Add(line);
        }

        foreach (var run in recorder.ContentTextRuns)
        {
            if (run.Box.X < 0f || run.Box.Y < 0f || run.Box.X + run.Box.Width > width + 0.01f
                || run.Box.Y + run.Box.Height > height + 0.01f)
            {
                offScreen++;
                string line = $"{tag}content-off-screen {run.Element} '{Clip(run.Text)}' at {Fmt(run.Box)} " +
                    $"in {width}x{height}";
                findings.Add(line);
                if (samples.Count < 24) samples.Add(line);
            }
        }

        int failures = textText + textBox + offScreen;
        return new(recorder.ContentTextRuns.Count, recorder.ContentBoxes.Count, textText, textBox,
            offScreen, failures, samples.ToArray());
    }

    /// <summary>P1055 — the overlap rectangle of two boxes (0 when they do not intersect).</summary>
    private static (float W, float H) Overlap(RectangleF a, RectangleF b)
    {
        float w = MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X);
        float h = MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y);
        return (w, h);
    }

    /// <summary>A run's text shortened for a findings line.</summary>
    private static string Clip(string text)
    {
        string flat = text.Replace("\r", "").Replace("\n", " / ");
        return flat.Length <= 42 ? flat : flat[..39] + "...";
    }

    private static bool Same(RectangleF a, RectangleF b)
        => MathF.Abs(a.X - b.X) <= 0.01f && MathF.Abs(a.Y - b.Y) <= 0.01f
        && MathF.Abs(a.Width - b.Width) <= 0.01f && MathF.Abs(a.Height - b.Height) <= 0.01f;

    /// <summary>P1042c — the INK box of a recorded run: its box carries the 1 px keyline
    /// under-stroke on every side and SpriteFont's line pitch (leading) in its height. Ink
    /// overlap therefore compares 1 px narrower and 4 px shorter per side; two runs on a
    /// 20 px line pitch with a 24 px line box do not overlap, a real collision still does.</summary>
    private static RectangleF InkBox(RectangleF box)
        => new(box.X + 1f, box.Y + 4f, MathF.Max(0f, box.Width - 2f), MathF.Max(0f, box.Height - 8f));

    private static RectangleF Shrink(RectangleF box, float by)
        => new(box.X + by, box.Y + by, MathF.Max(0f, box.Width - 2f * by), MathF.Max(0f, box.Height - 2f * by));

    private static bool Contains(RectangleF box, Vector2 p)
        => p.X >= box.X && p.X <= box.X + box.Width && p.Y >= box.Y && p.Y <= box.Y + box.Height;

    private static bool Contains(RectangleF outer, RectangleF inner)
        => inner.X >= outer.X - 0.01f && inner.Y >= outer.Y - 0.01f
        && inner.X + inner.Width <= outer.X + outer.Width + 0.01f
        && inner.Y + inner.Height <= outer.Y + outer.Height + 0.01f;

    /// <summary>Do two ink boxes share more than a touching edge? A 0.5 px inset on each
    /// side keeps a run and its neighbour that abut exactly (the briefing's 20 px line pitch
    /// against a 20 px line box) from counting as an overlap — only shared ink does.</summary>
    private static bool Intersects(RectangleF a, RectangleF b)
    {
        const float Eps = 0.5f;
        return a.X + Eps < b.X + b.Width - Eps && b.X + Eps < a.X + a.Width - Eps
            && a.Y + Eps < b.Y + b.Height - Eps && b.Y + Eps < a.Y + a.Height - Eps;
    }

    private static string Fmt(RectangleF r) => $"{r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#}";
}
