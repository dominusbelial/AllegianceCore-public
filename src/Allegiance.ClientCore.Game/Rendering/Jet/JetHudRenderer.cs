using System.Globalization;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1035 — Jet story 1: the military vector skin's scaffold (P972 §4). It is a
/// concrete <see cref="IHudSkinRenderer"/> composed exactly like the Classic
/// adapter: the shared <see cref="HudSkinResources"/> passes keep radar, ship
/// tags, pod/death, command view, chat, cargo and flight parts working, while Jet
/// itself draws only its own symbology. Story 1 draws the broken gun cross, a
/// boxed numeric speed, a boxed throttle percentage and the small
/// <c>JET / GREEN - WIP</c> tag; later stories (P1036–P1043) grow
/// <see cref="Coverage"/> with the ladder, tapes, target, stores, caution and RWR.
/// <para><b>Shared composition choice (explicit):</b> every shared pass uses the
/// CLASSIC composition — native radar bracket renderer (<c>_resources.Radar</c>),
/// native side-colour ship tags (<c>ShipTags(model, modern: false)</c>), Classic
/// pod/death art, the Classic external command summary (<c>Command(model, true)</c>)
/// and the Classic chat/cargo/flight-parts console pass. Jet is a native-adjacent
/// monochrome skin, and those passes are the native-derived path; Modern's
/// composed variants stay Modern-only until a later Jet story skins them.</para>
/// <para><b>Never invents data:</b> speed and throttle come from the same
/// <see cref="HudFrame"/> the Classic/Modern gauges read, the gun cross follows the
/// existing cockpit-reticle gate, and unknown telemetry draws nothing.</para>
/// </summary>
public sealed class JetHudRenderer : IHudSkinRenderer
{
    /// <summary>The WIP tag. HUD fonts are ASCII (<see cref="HudFormat.Printable"/>),
    /// so the P972 em dash is drawn as a hyphen.</summary>
    public const string TagText = "JET / GREEN - WIP";

    /// <summary>The <see cref="HudElement"/> ids Jet itself draws in story 1.</summary>
    private static readonly string[] CoverageIds =
        ["Cockpit centre reticle", "Speed", "Thrust setting"];
    private static readonly HashSet<string> CoverageSet = new(CoverageIds, StringComparer.Ordinal);

    private const string SpeedCaption = "SPD / MPS";
    private const string ThrottleCaption = "THR";

    // P972 §4.1 fallbacks when a definition carries no palette block: the Jet
    // green variant's authored colours.
    private static readonly Color GreenPrimary = new(0x79, 0xFF, 0xC0);
    private static readonly Color GreenDim = new(0x3D, 0xA8, 0x7B);
    private static readonly Color GreenHot = new(0xC9, 0xFF, 0xE3);
    private static readonly Color GreenPlate = new(0x06, 0x11, 0x0C);
    private static readonly Color GreenKeyline = new(0x08, 0x0B, 0x09);

    private readonly IJetVectorSurface _surface;
    private readonly JetVectorPainter _painter;
    private readonly HudSkinResources _resources;
    private readonly HudSettings _settings;
    private readonly Action<RadarIconTextures?>? _radarIcons;
    private readonly float _displayScale;
    private readonly string _definitionName;

    private string _speedText = "0";
    private int _speedShown = int.MinValue;
    private string _throttleText = "0%";
    private int _throttleShown = int.MinValue;
    private int _viewportHeight = 1080;
    private bool _disposed;

    /// <summary>Test/alternate-surface form: any <see cref="IJetVectorSurface"/>.</summary>
    public JetHudRenderer(IJetVectorSurface surface, HudSettings settings, HudSkinResources resources,
        HudSkinDefinition definition, float displayScale = 1f, Action<RadarIconTextures?>? radarIcons = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(definition);
        _surface = surface;
        _settings = settings;
        _resources = resources;
        _definitionName = definition.Name;
        _radarIcons = radarIcons;
        _displayScale = displayScale > 0f ? displayScale : 1f;
        HudSkinPalette? palette = definition.Palette;
        _painter = new JetVectorPainter(surface,
            Parse(palette?.Primary, GreenPrimary), Parse(palette?.Dim, GreenDim), Parse(palette?.Hot, GreenHot),
            Parse(palette?.Plate, GreenPlate), Parse(palette?.Keyline, GreenKeyline));
    }

    /// <summary>Production form: Game1's shared SpriteBatch, white pixel and fonts.</summary>
    public JetHudRenderer(SpriteBatch batch, Texture2D white, SpriteFont small, SpriteFont large,
        HudSettings settings, HudSkinResources resources, HudSkinDefinition definition,
        float displayScale = 1f, Action<RadarIconTextures?>? radarIcons = null)
        : this(new JetSpriteBatchSurface(batch, white, small, large), settings, resources, definition, displayScale, radarIcons)
    { }

    public string Id => HudSkinIds.Jet;

    /// <summary>The elements Jet itself draws; the shared passes below draw the
    /// rest (they are not claimed here).</summary>
    public IReadOnlySet<string> Coverage => CoverageSet;

    /// <summary>The resolved skin name, for the options row/proof output.</summary>
    public string DefinitionName => _definitionName;

    /// <summary>The painter, exposed for the vector-primitive tests.</summary>
    public JetVectorPainter Painter => _painter;

    public void Draw(HudModel model, in HudDrawFrame frame)
    {
        // Classic-composition shared passes (see the class remarks).
        _resources.Radar(model);
        _resources.ShipTags(model, false);

        float scale = ModernHudScale.Core(_displayScale, _settings.Size, frame.Viewport.Height);
        _painter.Scale = scale;
        _viewportHeight = frame.Viewport.Height > 0 ? frame.Viewport.Height : _viewportHeight;

        if (model.Visible)
        {
            float w = frame.Viewport.Width;
            float h = frame.Viewport.Height;
            _surface.Begin();
            try
            {
                if (model.ReticleVisible)
                    DrawGunCross(new(w * 0.5f, h * 0.5f), scale);
                // Native hides the centre instrument group in the command view
                // (HudModel.InstrumentsVisible = flight && !docked/dead/command), so
                // the readouts follow that same gate rather than widening it.
                if (model.InstrumentsVisible && model.Telemetry is { } own)
                {
                    DrawSpeed(own.Speed, w, h, scale);
                    DrawThrottle(own.Throttle, w, h, scale);
                }
                DrawTag(w, h, scale);
            }
            finally
            {
                _surface.End();
            }
        }

        _resources.PodDeath(model);
    }

    /// <summary>The classic external command-summary line (Classic composition).</summary>
    public void DrawCommand(HudModel model) => _resources.Command(model, true);

    /// <summary>The Classic console composition (chat, cargo, flight parts). Modern
    /// composes those in its own HUD pass; Jet defers to the Classic passes.</summary>
    public void DrawConsole(HudModel model)
    {
        _resources.Chat(model);
        _resources.Cargo(model);
        _resources.FlightParts(model);
    }

    /// <summary>The shared external radar renderer is Game1's, not ours; forward the
    /// one-time icon attachment exactly like the Classic adapter does.</summary>
    public void SetRadarIcons(RadarIconTextures? icons) => _radarIcons?.Invoke(icons);

    /// <summary>Overlay text inside the caller's active SpriteBatch session
    /// (Game1 begins it), with Jet's keyline under-stroke.</summary>
    public Rectangle DrawOverlayText(string text, Vector2 position, Color color)
    {
        string printable = HudFormat.Printable(text);
        float scale = ModernHudScale.Core(_displayScale, _settings.Size, _viewportHeight);
        _painter.Scale = scale;
        Vector2 size = _painter.Measure(printable, large: false, scale);
        _painter.Text(printable, position, large: false, scale, color);
        return new Rectangle((int)position.X, (int)position.Y,
            (int)MathF.Ceiling(size.X), (int)MathF.Ceiling(size.Y));
    }

    // ── Story-1 symbology ────────────────────────────────────────────────
    /// <summary>A small BROKEN cross (P972 §4.2): four arms with a centre gap, no
    /// lead point and no centre dot, so it can never be mistaken for the
    /// flight-path marker or the ballistic lead. Drawn only under the native
    /// internal/chase/missile camera gate the shared model applies
    /// (WinTrek/indicator.cpp:762-797) — the cockpit reticle itself is native
    /// indicator.cpp:149-150.</summary>
    private void DrawGunCross(Vector2 center, float scale)
    {
        float gap = 6f * scale;
        float arm = 17f * scale;
        float stroke = _painter.Stroke;
        Color ink = _painter.Primary;
        _painter.Line(new(center.X - gap - arm, center.Y), new(center.X - gap, center.Y), stroke, ink);
        _painter.Line(new(center.X + gap, center.Y), new(center.X + gap + arm, center.Y), stroke, ink);
        _painter.Line(new(center.X, center.Y - gap - arm), new(center.X, center.Y - gap), stroke, ink);
        _painter.Line(new(center.X, center.Y + gap), new(center.X, center.Y + gap + arm), stroke, ink);
    }

    /// <summary>Boxed numeric speed, left of centre (P972 §4.2 "boxed numeric").</summary>
    private void DrawSpeed(float speed, float w, float h, float scale)
    {
        int rounded = (int)MathF.Round(speed);
        if (rounded != _speedShown) { _speedShown = rounded; _speedText = rounded.ToString(CultureInfo.InvariantCulture); }
        Vector2 ink = _painter.Measure(_speedText, large: true, scale);
        float pad = 7f * scale;
        float tick = 4f * scale;
        float boxWidth = ink.X + pad * 2f + tick;
        float boxHeight = ink.Y + pad * 1.2f;
        float right = w * 0.5f - 150f * scale;
        var box = new RectangleF(right - boxWidth, h * 0.5f + 96f * scale, boxWidth, boxHeight);
        _painter.FillPlate(box, 0.82f);
        _painter.Line(new(box.X + boxWidth - tick * 0.5f, box.Y), new(box.X + boxWidth - tick * 0.5f, box.Y + box.Height),
            _painter.Stroke, _painter.Dim);
        _painter.Text(_speedText, new(box.X + pad, box.Y + (box.Height - ink.Y) * 0.5f), large: true, scale, _painter.Primary);
        DrawCaption(SpeedCaption, new(box.X, box.Y - 4f * scale), scale);
    }

    /// <summary>Boxed throttle percentage, right of centre. The percentage is the
    /// same thrust-gauge fraction the shared model computes for both existing
    /// skins (HudModel.cs:414: <c>(throttle+1)/2</c>, native
    /// <c>jsValues[c_axisThrottle]*0.5+0.5</c> WinTrek/WinTrek.cpp:8466), so idle is
    /// 50%, full forward 100% and the turret's forced −1 reads 0%.</summary>
    private void DrawThrottle(float throttle, float w, float h, float scale)
    {
        int percent = (int)MathF.Round((Math.Clamp(throttle, -1f, 1f) + 1f) * 0.5f * 100f);
        if (percent != _throttleShown)
        {
            _throttleShown = percent;
            _throttleText = percent.ToString(CultureInfo.InvariantCulture) + "%";
        }
        Vector2 ink = _painter.Measure(_throttleText, large: true, scale);
        float pad = 7f * scale;
        float tick = 4f * scale;
        float boxWidth = ink.X + pad * 2f + tick;
        float boxHeight = ink.Y + pad * 1.2f;
        float left = w * 0.5f + 150f * scale;
        var box = new RectangleF(left, h * 0.5f + 96f * scale, boxWidth, boxHeight);
        _painter.FillPlate(box, 0.82f);
        _painter.Line(new(box.X + tick * 0.5f, box.Y), new(box.X + tick * 0.5f, box.Y + box.Height),
            _painter.Stroke, _painter.Dim);
        _painter.Text(_throttleText, new(box.X + boxWidth - pad - ink.X, box.Y + (box.Height - ink.Y) * 0.5f),
            large: true, scale, _painter.Primary);
        DrawCaption(ThrottleCaption, new(box.X + boxWidth, box.Y - 4f * scale), scale, rightAligned: true);
    }

    private void DrawCaption(string text, Vector2 anchor, float scale, bool rightAligned = false)
    {
        Vector2 ink = _painter.Measure(text, large: false, scale);
        Vector2 position = rightAligned ? new(anchor.X - ink.X, anchor.Y - ink.Y) : new(anchor.X, anchor.Y - ink.Y);
        _painter.Text(text, position, large: false, scale, _painter.Dim);
    }

    /// <summary>The WIP announcement, bottom-centre.</summary>
    private void DrawTag(float w, float h, float scale)
    {
        Vector2 ink = _painter.Measure(TagText, large: false, scale);
        _painter.Text(TagText, new(w * 0.5f, h - 12f * scale - ink.Y * 0.5f), large: false, scale, _painter.Dim, centered: true);
    }

    private static Color Parse(string? hex, Color fallback)
        => HudSkinColor.TryParseHex(hex, out var color) ? new Color(color.R, color.G, color.B) : fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The surface borrows Game1's SpriteBatch/fonts; nothing here owns a
        // disposable resource.
    }
}
