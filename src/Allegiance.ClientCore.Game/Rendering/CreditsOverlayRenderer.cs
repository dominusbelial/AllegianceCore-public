using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P577 — the in-game <b>Credits / Licences</b> screen (F12 toggle), the
/// mandatory visible-attribution surface for the CC BY 4.0 ESA/Hubble/Webb and
/// public-domain NASA imagery that powers the HD sky tier (scope item 4).
/// Mirrors <see cref="HelpOverlayRenderer"/>'s panel conventions: own
/// <see cref="SpriteBatch"/> session, translucent panel + teal border, pure
/// ASCII, drawn top-most so it is fully legible, and toggleable independently
/// of every other overlay.
/// <para>
/// The credit text mirrors <c>docs/reports/P577_assets/sky-hd.json</c> and
/// <c>licenses/</c>. It is scrollable (Wheel / PageUp / PageDown) when the
/// table overflows the panel height.
/// </para>
/// </summary>
public sealed class CreditsOverlayRenderer
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _small;

    private static readonly Color PanelBg = new(0.015f, 0.026f, 0.052f, 0.95f);
    private static readonly Color PanelBorder = new(0.30f, 0.50f, 0.88f);
    private static readonly Color HeaderColor = new(0.62f, 0.82f, 1.00f);
    private static readonly Color BodyColor = new(0.86f, 0.92f, 0.97f);
    private static readonly Color AccentColor = new(1.00f, 0.85f, 0.45f);
    private static readonly Color NoteColor = new(0.58f, 0.68f, 0.80f);

    private const float LineHeight = 20f;
    private float _scroll = 0f;

    public CreditsOverlayRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        SpriteFont small)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        _small = small;
    }
    private static readonly string[] Body =
    [
        "CREDITS / LICENCES  (F12 TOGGLE)",
        "",
        "The HD sky tier (P577) is built from real telescope public data.",
        "Derived assets are credited per source; CC BY requires this visible",
        "attribution and no implied endorsement. Full mapping and texts:",
        "docs/reports/P577_assets/sky-hd.json and licenses/.",
        "",
        "--- STARS ---",
        "NASA / Goddard Space Flight Center Scientific Visualization Studio,",
        "  Deep Star Maps 2020 (dataset 4851)  -  Public Domain (NASA)",
        "  https://svs.gsfc.nasa.gov/4851/  (Hipparcos-2, Tycho-2, Gaia DR2)",
        "",
        "--- NEBULAE (ESA/Hubble, CC BY 4.0; https://esahubble.org/copyright/) ---",
        "env1 Whirlpool Galaxy M51  -  ESA/Hubble & NASA",
        "env2 Bubble Nebula NGC 7635 -  NASA, ESA/Hubble and the Hubble Heritage Team",
        "env3 Supernova 1987A        -  NASA, ESA, R. Kirshner et al.",
        "env4 Pillars of Creation    -  NASA, ESA/Hubble and the Hubble Heritage Team",
        "env5 star-forming region    -  ESA/Hubble & NASA",
        "env6 Supernova remnant N103B-  NASA, ESA, Y.-H. Chu",
        "env7 Tapestry of Starbirth  -  NASA, ESA/Hubble and the Hubble Heritage Team",
        "env8 Mystic Mountain        -  NASA, ESA, M. Livio, Hubble 20th Anniv Team",
        "env9 Horsehead Nebula (IR)  -  NASA, ESA, and the Hubble Heritage Team",
        "env10 Westerlund 2          -  NASA, ESA, the Hubble Heritage Team",
        "",
        "--- PLANETS (NASA, Public Domain) ---",
        "Earth Apollo 17 'Blue Marble'    -  NASA / Apollo 17 crew",
        "Jupiter (PIA01594)               -  NASA, ESA, A. Simon, M. Wong",
        "Neptune (PIA00046)               -  NASA/JPL",
        "Mars (PIA00003)                  -  NASA/USGS",
        "Saturn (PIA21047)                -  NASA/JPL-Caltech/SSI",
        "Uranus (PIA18182)                -  NASA/JPL",
        "Venus (PIA10124)                 -  NASA/JPL",
        "Moon (FullMoon2010)              -  NASA/GSFC (G. H. Revera)",
        "Titan (PIA12320)                 -  NASA/JPL-Caltech/SSI",
        "",
        "Credits are maintained in docs/reports/P577_assets/licenses/ and",
        "sky-hd.json. ESA/Hubble/Webb images: CC BY 4.0. NASA images: public",
        "domain (US Government works).",
    ];

    /// <summary>Feed keyboard input so the text can be scrolled when long.</summary>
    public void Scroll(float delta)
        => _scroll = Math.Max(0f, _scroll + delta);

    public void Draw(Viewport viewport)
    {
        float w = viewport.Width;
        float h = viewport.Height;
        float panelW = w * 0.965f;
        float panelH = h * 0.90f;
        float panelX = (w - panelW) / 2f;
        float panelY = (h - panelH) / 2f;
        const float pad = 18f;

        // total body height at font line spacing
        float lineH = _small.LineSpacing + 3f;
        float top = panelY + pad + 8f;
        float bodyTop = top;
        float avail = panelH - 2f * pad - 40f;
        float totalBody = Body.Length * lineH;
        float maxScroll = Math.Max(0f, totalBody - avail);
        if (_scroll > maxScroll) _scroll = maxScroll;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            FillRect(panelX, panelY, panelW, panelH, PanelBg);
            OutlineRect(panelX, panelY, panelW, panelH, PanelBorder);
            FillRect(panelX, panelY, panelW, 2f, PanelBorder);

            // Header
            string header = "CREDITS / LICENCES  (F12 TOGGLE)";
            var hs = _small.MeasureString(header);
            _sb.DrawString(_small, header,
                new Vector2((w - hs.X) / 2f, panelY + 8f), HeaderColor);

            // Body (scrolled)
            float y = bodyTop - _scroll;
            for (int i = 0; i < Body.Length; i++)
            {
                string line = Body[i];
                if (y + lineH > panelY && y < panelY + panelH - 6f)
                {
                    var c = line.StartsWith("---") || line.StartsWith("CREDITS")
                        ? AccentColor
                        : line.StartsWith("--") ? HeaderColor : BodyColor;
                    if (y + lineH > panelY + 8f)
                        _sb.DrawString(_small, line, new Vector2(panelX + pad, y), c);
                }
                y += lineH;
            }

            // Footer note
            string note = maxScroll > 0f
                ? "Wheel / PageUp / PageDown to scroll. Esc or F12 to close."
                : "Esc or F12 to close.";
            var nsz = _small.MeasureString(note);
            _sb.DrawString(_small, note,
                new Vector2((w - nsz.X) / 2f, panelY + panelH - 24f), NoteColor);
        }
        finally
        {
            _sb.End();
        }
    }

    private void FillRect(float x, float y, float w, float h, Color color) =>
        _sb.Draw(_white, new Rectangle((int)System.MathF.Round(x),
            (int)System.MathF.Round(y), (int)System.MathF.Round(w),
            (int)System.MathF.Round(h)), color);

    private void OutlineRect(float x, float y, float w, float h, Color color)
    {
        FillRect(x, y, w, 1f, color);
        FillRect(x, y + h - 1f, w, 1f, color);
        FillRect(x, y, 1f, h, color);
        FillRect(x + w - 1f, y, 1f, h, color);
    }
}
