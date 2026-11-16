using System.Globalization;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1036 — Jet story 2/9: the core flight symbology (P972 §4.2, phase 3A).
/// Still a concrete <see cref="IHudSkinRenderer"/> composed exactly like the
/// Classic adapter (the shared <see cref="HudSkinResources"/> passes keep radar,
/// ship tags, pod/death, command view, chat, cargo and flight parts working),
/// but Jet now draws the round flight-path marker, the sector-plane pitch ladder,
/// the heading tape and the moving speed/throttle tapes, plus story 1's broken gun
/// cross and the <c>JET / GREEN - WIP</c> tag.
/// <para><b>Every value is shared-model data:</b> the flight-path marker is the
/// port's existing native velocity-indicator projection
/// (<c>WinTrek/indicator.cpp:128-150</c>, <c>Game1.Camera.ComputeVelocityMarker</c>),
/// attitude comes from <see cref="HudFlightReference"/> (built once in the model
/// adapter from the pose the client already tracks), speed and throttle are the
/// same <see cref="HudFrame"/> values Classic/Modern gauge, and nothing is invented
/// (no altitude, G, Mach or planetary horizon).</para>
/// <para><b>Gates:</b> the gun cross and the flight-path marker follow the native
/// reticle/indicator camera gates (<see cref="HudModel.ReticleVisible"/>,
/// <see cref="HudModel.VelocityMarkerVisible"/>); tapes, ladder and heading follow
/// the instrument group (<see cref="HudModel.InstrumentsVisible"/>), which is
/// exactly what Classic hides in the command view, docked and dead. A lifepod
/// keeps the tapes (pod speed) and shows the ladder/heading only as its data
/// allows.</para>
/// </summary>
public sealed class JetHudRenderer : IHudSkinRenderer
{
    /// <summary>The WIP tag. HUD fonts are ASCII (<see cref="HudFormat.Printable"/>),
    /// so the P972 em dash is drawn as a hyphen.</summary>
    public const string TagText = "JET / GREEN - WIP";

    /// <summary>The <see cref="HudElement"/> ids Jet itself draws from story 2 on:
    /// velocity indicator (flight-path marker), roll (the ladder's bank reference),
    /// speed, thrust setting and the centre reticle. The shared passes draw the
    /// rest and are not claimed here.</summary>
    private static readonly string[] CoverageIds =
        ["Velocity direction marker", "Roll tutorial cue", "Speed", "Thrust setting", "Cockpit centre reticle"];
    private static readonly HashSet<string> CoverageSet = new(CoverageIds, StringComparer.Ordinal);

    /// <summary>The drawn element ids, for the tests and the proof output.</summary>
    public static IReadOnlyList<string> ElementIds => CoverageIds;

    private const string SpeedCaption = "SPD / MPS";
    private const string ThrottleCaption = "THR";
    private const string HeadingCaption = "HDG ";
    private const string HeadingUnavailable = "HDG --";
    private const string IdleLabel = "IDLE";
    private const string ReverseLabel = "REV";

    /// <summary>Numeric ladder labels for −80 … +80 in 10° steps (ASCII only).</summary>
    private static readonly string[] RungLabels =
    [
        "-80", "-70", "-60", "-50", "-40", "-30", "-20", "-10", "00",
        "+10", "+20", "+30", "+40", "+50", "+60", "+70", "+80",
    ];

    /// <summary>Heading labels at the 20° marks, pre-formatted so a frame never
    /// formats a string for a tape tick.</summary>
    private static readonly string[] HeadingLabels =
    [
        "000", "020", "040", "060", "080", "100", "120", "140", "160",
        "180", "200", "220", "240", "260", "280", "300", "320", "340",
    ];

    /// <summary>Throttle labels at the 20 % marks (static: the scale is fixed).</summary>
    private static readonly string[] ThrottleLabels = ["000", "020", "040", "060", "080", "100"];

    /// <summary>Size of the speed-tick label ring: wide enough that the ticks of a
    /// ±180 mps tape (10 labels at 40 mps spacing) all stay cached.</summary>
    private const int SpeedTickCache = 16;

    /// <summary>The 800×480 layout floor: fewer ladder rungs, no IDLE/REV words
    /// (P972 §4.5).</summary>
    private const float CompactHeight = 700f;

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

    private JetAttitudeState _attitude;

    // Text caches: a steady frame formats nothing.
    private string _speedText = "0";
    private int _speedShown = int.MinValue;
    private string _throttleText = "0%";
    private int _throttleShown = int.MinValue;
    private string _headingText = HeadingUnavailable;
    private int _headingShown = int.MinValue;
    private string _pitchText = "00";
    private int _pitchShown = int.MinValue;
    private readonly int[] _speedTickKeys = new int[SpeedTickCache];
    private readonly string[] _speedTickText = new string[SpeedTickCache];

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

    /// <summary>The attitude resolved for the most recent frame (tests/proofs).</summary>
    public JetAttitude Attitude => _attitude.Current;

    /// <summary>Clear the held heading/roll (proof fixtures start every scene from no
    /// held value; a live client never calls this — the hold is the point).</summary>
    public void ResetFlightState() => _attitude = default;

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
                if (model.InstrumentsVisible)
                {
                    // Attitude is resolved once per frame; a null reference
                    // (docked/dead/command/no own ship) clears it without
                    // inventing values.
                    _attitude.Update(model.Context.FlightReference);
                    DrawHeadingTape(w, h, scale);
                    DrawPitchLadder(w, h, scale);
                }

                if (model.ReticleVisible)
                    DrawGunCross(new(w * 0.5f, h * 0.5f), scale);

                // Native velocity indicator: cockpit only, rounded speed non-zero,
                // projected inside the view (indicator.cpp:128-150).
                if (model.VelocityMarkerVisible && model.Context.VelocityMarker is { } marker)
                    DrawFlightPathMarker(new(marker.X, marker.Y), w, h, scale);

                // Native hides the centre instrument group in the command view
                // (HudModel.InstrumentsVisible = flight && !docked/dead/command), so
                // the tapes follow that same gate rather than widening it.
                if (model.InstrumentsVisible && model.Telemetry is { } own)
                {
                    DrawSpeedTape(own.Speed, w, h, scale);
                    DrawThrottleTape(own.Throttle, w, h, scale);
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

    // ── Flight symbology (P1036) ─────────────────────────────────────────
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

    /// <summary>
    /// The round flight-path marker (P972 §4.2): a circle with two side wings and a
    /// vertical tail, drawn at the port's existing native velocity projection
    /// (see <see cref="JetFlightGeometry.FlightPathMarkerVisible"/>). The circle and
    /// the wings keep it distinct from the broken gun cross and from the ballistic
    /// lead marker; it is hidden exactly when the native velocity indicator is
    /// hidden.
    /// </summary>
    private void DrawFlightPathMarker(Vector2 position, float w, float h, float scale)
    {
        if (position.X < 0f || position.Y < 0f || position.X > w || position.Y > h) return;
        float radius = 7f * scale;
        float wing = 12f * scale;
        float tail = 9f * scale;
        float stroke = _painter.Stroke;
        Color ink = _painter.Primary;
        _painter.Circle(position, radius, stroke, ink);
        _painter.Line(new(position.X - radius - wing, position.Y), new(position.X - radius, position.Y), stroke, ink);
        _painter.Line(new(position.X + radius, position.Y), new(position.X + radius + wing, position.Y), stroke, ink);
        _painter.Line(new(position.X, position.Y - radius - tail), new(position.X, position.Y - radius), stroke, ink);
    }

    /// <summary>
    /// The sector-plane pitch ladder (P972 §4.2): 10° labelled rungs, with the
    /// optional 5° refinement as short unlabelled ticks at ≥1080p (it stays
    /// uncluttered there; labels remain on the 10° marks), centred on the
    /// boresight, rolled by the ship's bank, dashed below the reference plane,
    /// numeric labels that stay horizontal, and off-scale chevrons when the ladder
    /// runs past ±80°. At 800×480 the span shrinks to ±20° and the 5° ticks are
    /// dropped (P972 §4.5).
    /// </summary>
    private void DrawPitchLadder(float w, float h, float scale)
    {
        if (!_attitude.Current.Available) return;
        float pitch = _attitude.Current.PitchDegrees;
        float roll = _attitude.Current.RollDegrees;
        bool compact = h < CompactHeight;
        bool fineRungs = h >= 1000f;                 // 5° ticks at 1080p and above
        float span = compact ? JetFlightGeometry.CompactLadderHalfSpanDegrees
            : JetFlightGeometry.LadderHalfSpanDegrees;
        float pxPerDegree = JetFlightGeometry.LadderPixelsPerDegree * scale;
        float halfWidth = (compact ? 28f : 46f) * scale;
        float angle = -roll / JetFlightGeometry.RadiansToDegrees;
        float cosA = MathF.Cos(angle), sinA = MathF.Sin(angle);
        Vector2 center = new(w * 0.5f, h * 0.5f);
        float stroke = _painter.Stroke;

        int limit = (int)JetFlightGeometry.LadderLimitDegrees;
        int step = fineRungs ? (int)JetFlightGeometry.LadderFineStepDegrees
            : (int)JetFlightGeometry.LadderStepDegrees;
        for (int rungPitch = -limit; rungPitch <= limit; rungPitch += step)
        {
            if (rungPitch - pitch < -span || rungPitch - pitch > span) continue;
            // The rung nearest the current pitch lands on the boresight and would
            // cover the gun cross and the flight-path marker; the pitch numeral
            // below the ladder carries that value instead.
            if (MathF.Abs(rungPitch - pitch) * pxPerDegree < 24f * scale) continue;
            bool major = rungPitch % (int)JetFlightGeometry.LadderStepDegrees == 0;
            Vector2 at = center + JetFlightGeometry.LadderOffset(pitch, rungPitch, roll, pxPerDegree);
            Vector2 half = new(cosA * halfWidth, sinA * halfWidth);
            if (!major)
            {
                // 5° refinement tick: short, dim, never labelled.
                Vector2 shortHalf = new(cosA * halfWidth * 0.45f, sinA * halfWidth * 0.45f);
                _painter.Line(at - shortHalf, at + shortHalf, _painter.Hairline, _painter.Dim);
                continue;
            }
            bool belowPlane = rungPitch < 0;
            Color ink = belowPlane ? _painter.Dim : _painter.Primary;
            if (belowPlane)
                DashedLine(at - half, at + half, stroke, ink);   // dashed below the sector plane
            else
                _painter.Line(at - half, at + half, stroke, ink);
            // Labels stay horizontal even when the ladder rolls.
            string label = RungLabels[(rungPitch + limit) / (int)JetFlightGeometry.LadderStepDegrees];
            Vector2 size = _painter.Measure(label, large: false, scale);
            _painter.Text(label, new(at.X - halfWidth - size.X - 4f * scale, at.Y - size.Y * 0.5f),
                large: false, scale, ink);
            _painter.Text(label, new(at.X + halfWidth + 4f * scale, at.Y - size.Y * 0.5f),
                large: false, scale, ink);
        }

        // Off-scale chevrons: the ladder has run past its last usable rung.
        if (JetFlightGeometry.LadderOffScaleTop(pitch, span))
            Chevron(center + JetFlightGeometry.LadderOffset(pitch, pitch + span, roll, pxPerDegree),
                angle, up: true, scale);
        if (JetFlightGeometry.LadderOffScaleBottom(pitch, span))
            Chevron(center + JetFlightGeometry.LadderOffset(pitch, pitch - span, roll, pxPerDegree),
                angle, up: false, scale);

        // Current pitch numeral below the ladder (presentation only).
        int roundedPitch = (int)MathF.Round(pitch);
        if (roundedPitch != _pitchShown)
        {
            _pitchShown = roundedPitch;
            _pitchText = FormatSigned(roundedPitch);
        }
        Vector2 pitchSize = _painter.Measure(_pitchText, large: false, scale);
        _painter.Text(_pitchText, new(center.X - pitchSize.X * 0.5f,
                center.Y + span * pxPerDegree + 20f * scale), large: false, scale, _painter.Dim);
    }

    /// <summary>One off-scale chevron: two strokes forming a "V" along the ladder's
    /// rolled axis, pointing away from the drawn rungs.</summary>
    private void Chevron(Vector2 at, float angle, bool up, float scale)
    {
        float arm = 8f * scale;
        float spread = 7f * scale;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        float tipY = up ? -arm : arm;
        float sideY = up ? arm * 0.25f : -arm * 0.25f;
        // (x·cos − y·sin, x·sin + y·cos)
        Vector2 tip = new(at.X - tipY * sin, at.Y + tipY * cos);
        Vector2 left = new(at.X - spread * cos - sideY * sin, at.Y - spread * sin + sideY * cos);
        Vector2 right = new(at.X + spread * cos - sideY * sin, at.Y + spread * sin + sideY * cos);
        _painter.Line(left, tip, _painter.Stroke, _painter.Primary);
        _painter.Line(tip, right, _painter.Stroke, _painter.Primary);
    }

    /// <summary>
    /// The heading tape across the top (P972 §4.2): 10° ticks, 20° numeric marks, a
    /// caret at the current heading and a boxed numeric readout. Inside the vertical
    /// singularity the state holder keeps the last valid heading but marks it
    /// unavailable, so the readout becomes <c>HDG --</c> instead of jumping.
    /// </summary>
    private void DrawHeadingTape(float w, float h, float scale)
    {
        float cx = w * 0.5f;
        float y = 22f * scale;
        float pxPerDegree = JetFlightGeometry.HeadingTapeScale(w);
        bool haveHeading = _attitude.Current.Available && _attitude.Current.HeadingValid;
        float halfSpanPx = MathF.Max(0f, MathF.Min(cx - 70f * scale, 340f * scale));
        Color caret = haveHeading ? _painter.Primary : _painter.Dim;

        if (haveHeading)
        {
            float heading = _attitude.Current.HeadingDegrees;
            float halfSpanDegrees = halfSpanPx / pxPerDegree;

            // Horizontal tape rail.
            _painter.Line(new(cx - halfSpanPx, y), new(cx + halfSpanPx, y), _painter.Hairline, _painter.Dim);

            int first = (int)MathF.Ceiling((heading - halfSpanDegrees) / JetFlightGeometry.HeadingTickStep)
                * (int)JetFlightGeometry.HeadingTickStep;
            int last = (int)MathF.Floor((heading + halfSpanDegrees) / JetFlightGeometry.HeadingTickStep)
                * (int)JetFlightGeometry.HeadingTickStep;
            for (int degrees = first; degrees <= last; degrees += (int)JetFlightGeometry.HeadingTickStep)
            {
                float x = cx + (degrees - heading) * pxPerDegree;
                if (x < 24f * scale || x > w - 24f * scale) continue;
                int normalized = ((degrees % 360) + 360) % 360;
                bool major = normalized % (int)JetFlightGeometry.HeadingLabelStep == 0;
                _painter.Line(new(x, y), new(x, y + (major ? 6f : 3f) * scale), _painter.Hairline,
                    major ? _painter.Primary : _painter.Dim);
                if (major)
                {
                    string text = HeadingLabels[normalized / (int)JetFlightGeometry.HeadingLabelStep];
                    Vector2 size = _painter.Measure(text, large: false, scale);
                    _painter.Text(text, new(x - size.X * 0.5f, y - size.Y - 2f * scale),
                        large: false, scale, _painter.Dim);
                }
            }

            int rounded = (int)MathF.Round(heading) % 360;
            if (rounded < 0) rounded += 360;
            if (rounded != _headingShown)
            {
                _headingShown = rounded;
                _headingText = HeadingCaption + rounded.ToString("000", CultureInfo.InvariantCulture);
            }
        }
        else
        {
            _headingShown = int.MinValue;
            _headingText = HeadingUnavailable;
        }

        // Caret below the rail pointing up at the current heading (it must not
        // overlap the numeric marks, which sit above the rail).
        _painter.Line(new(cx - 5f * scale, y + 9f * scale), new(cx, y + 1f * scale), _painter.Stroke, caret);
        _painter.Line(new(cx, y + 1f * scale), new(cx + 5f * scale, y + 9f * scale), _painter.Stroke, caret);

        // Boxed numeric below the caret.
        DrawReadout(_headingText, cx, y + 22f * scale, scale, ReadoutAlign.Center);
    }

    /// <summary>
    /// The left SPEED tape (P972 §4.2): a fixed moving tape of 20 mps ticks with
    /// 40 mps numeric marks and a boxed numeric readout at the boresight row. The
    /// value is the shared model's speed (a lifepod reads the same field).
    /// </summary>
    private void DrawSpeedTape(float speed, float w, float h, float scale)
    {
        float tapeX = 76f * scale;
        float cy = h * 0.5f;
        float halfSpan = h * 0.30f;
        float perMps = JetFlightGeometry.SpeedTapeScale(h);
        float stroke = _painter.Hairline;

        _painter.Line(new(tapeX, cy - halfSpan), new(tapeX, cy + halfSpan), stroke, _painter.Dim);
        DrawCaption(SpeedCaption, new(tapeX - 8f * scale, cy - halfSpan), scale);

        float visible = halfSpan / perMps;
        int from = (int)MathF.Max(0f, MathF.Floor((speed - visible) / JetFlightGeometry.SpeedTickStep))
            * (int)JetFlightGeometry.SpeedTickStep;
        int to = (int)(speed + visible);
        int labelSlot = 0;
        for (int value = from; value <= to; value += (int)JetFlightGeometry.SpeedTickStep)
        {
            float y = cy - (value - speed) * perMps;
            bool major = value % (int)JetFlightGeometry.SpeedLabelStep == 0;
            _painter.Line(new(tapeX, y), new(tapeX + (major ? 7f : 4f) * scale, y), stroke,
                major ? _painter.Primary : _painter.Dim);
            if (major)
            {
                string text = SpeedTickText(value, labelSlot++ % SpeedTickCache);
                Vector2 size = _painter.Measure(text, large: false, scale);
                _painter.Text(text, new(tapeX - 6f * scale - size.X, y - size.Y * 0.5f),
                    large: false, scale, _painter.Dim);
            }
        }

        // Boxed numeric at the boresight row, right of the tape.
        int rounded = (int)MathF.Round(speed);
        if (rounded != _speedShown) { _speedShown = rounded; _speedText = rounded.ToString(CultureInfo.InvariantCulture); }
        DrawReadout(_speedText, tapeX + 10f * scale, cy, scale, ReadoutAlign.LeftEdge);
    }

    /// <summary>
    /// The right THROTTLE tape (P972 §4.2): the same −1…+1 throttle the shared model
    /// maps to <c>(throttle+1)/2</c> (HudModel.cs:421, native
    /// <c>jsValues[c_axisThrottle]*0.5+0.5</c> WinTrek/WinTrek.cpp:8466), so idle is
    /// 50 %, full forward 100 % and the turret's forced −1 reads 0 %. The idle
    /// detent is a doubled tick at 50 % and the reverse band below it is a dashed dim
    /// rail with a REV label, so the numeric percent cannot conceal the detent.
    /// Afterburner and movement-button overrides stay in the shared model — the tape
    /// just reads the resulting value.
    /// </summary>
    private void DrawThrottleTape(float throttle, float w, float h, float scale)
    {
        float tapeX = w - 76f * scale;
        float cy = h * 0.5f;
        float halfSpan = h * 0.30f;
        float perPercent = JetFlightGeometry.ThrottleTapeScale(h);
        float stroke = _painter.Hairline;
        bool compact = h < CompactHeight;

        int percent = JetFlightGeometry.ThrottlePercent(throttle);

        _painter.Line(new(tapeX, cy - halfSpan), new(tapeX, cy + halfSpan), stroke, _painter.Dim);
        DrawCaption(ThrottleCaption, new(tapeX + 8f * scale, cy - halfSpan), scale, rightAligned: true);

        // Reverse band (0…50 %) as a dashed dim rail below the idle detent.
        float reverseTop = cy - (50f - percent) * perPercent;
        float reverseBottom = cy - (0f - percent) * perPercent;
        DashedLine(new(tapeX - 5f * scale, reverseTop), new(tapeX - 5f * scale, reverseBottom), stroke, _painter.Dim);
        if (!compact)
        {
            // REV sits in the same left margin as IDLE, so the right column stays
            // numeric labels only.
            Vector2 reverseSize = _painter.Measure(ReverseLabel, large: false, scale);
            _painter.Text(ReverseLabel,
                new(tapeX - 12f * scale - reverseSize.X,
                    cy - (25f - percent) * perPercent - reverseSize.Y * 0.5f),
                large: false, scale, _painter.Dim);
        }

        float visible = halfSpan / perPercent;
        int from = (int)MathF.Max(0f, MathF.Floor((percent - visible) / JetFlightGeometry.ThrottleTickStep))
            * (int)JetFlightGeometry.ThrottleTickStep;
        int to = (int)MathF.Min(100f, percent + visible);
        for (int value = from; value <= to; value += (int)JetFlightGeometry.ThrottleTickStep)
        {
            float y = cy - (value - percent) * perPercent;
            bool major = value % (int)JetFlightGeometry.ThrottleLabelStep == 0;
            _painter.Line(new(tapeX - (major ? 7f : 4f) * scale, y), new(tapeX, y), stroke,
                major ? _painter.Primary : _painter.Dim);
            if (major)
            {
                string text = ThrottleLabels[value / (int)JetFlightGeometry.ThrottleLabelStep];
                Vector2 size = _painter.Measure(text, large: false, scale);
                _painter.Text(text, new(tapeX + 10f * scale, y - size.Y * 0.5f), large: false, scale, _painter.Dim);
            }
        }

        // Idle detent: a doubled tick across the tape at 50 %.
        float idleY = cy - (50f - percent) * perPercent;
        _painter.Line(new(tapeX - 9f * scale, idleY - 1f * scale), new(tapeX + 9f * scale, idleY - 1f * scale),
            _painter.Stroke, _painter.Primary);
        _painter.Line(new(tapeX - 9f * scale, idleY + 1f * scale), new(tapeX + 9f * scale, idleY + 1f * scale),
            _painter.Stroke, _painter.Primary);
        if (!compact)
        {
            Vector2 idleSize = _painter.Measure(IdleLabel, large: false, scale);
            _painter.Text(IdleLabel, new(tapeX - 12f * scale - idleSize.X, idleY - idleSize.Y * 0.5f),
                large: false, scale, _painter.Dim);
        }

        if (percent != _throttleShown)
        {
            _throttleShown = percent;
            _throttleText = percent.ToString(CultureInfo.InvariantCulture) + "%";
        }
        DrawReadout(_throttleText, tapeX - 10f * scale, cy, scale, ReadoutAlign.RightEdge);
    }

    // ── shared readout helpers ──────────────────────────────────────────
    /// <summary>How a readout box is anchored to its tape.</summary>
    private enum ReadoutAlign { Center, LeftEdge, RightEdge }

    /// <summary>A boxed numeric on a plate with a keyline tick facing the tape.</summary>
    private void DrawReadout(string text, float x, float y, float scale, ReadoutAlign align)
    {
        Vector2 ink = _painter.Measure(text, large: true, scale);
        float pad = 7f * scale;
        float tick = 4f * scale;
        float boxWidth = ink.X + pad * 2f + tick;
        float boxHeight = ink.Y + pad * 1.2f;
        float left = align switch
        {
            ReadoutAlign.Center => x - boxWidth * 0.5f,
            ReadoutAlign.LeftEdge => x,
            _ => x - boxWidth,
        };
        var box = new RectangleF(left, y - boxHeight * 0.5f, boxWidth, boxHeight);
        _painter.FillPlate(box, 0.82f);
        float tickX = align == ReadoutAlign.RightEdge
            ? box.X + boxWidth - tick * 0.5f
            : box.X + tick * 0.5f;
        _painter.Line(new(tickX, box.Y), new(tickX, box.Y + box.Height), _painter.Stroke, _painter.Dim);
        _painter.Text(text, new(box.X + pad, box.Y + (box.Height - ink.Y) * 0.5f),
            large: true, scale, _painter.Primary);
    }

    private void DrawCaption(string text, Vector2 anchor, float scale, bool rightAligned = false)
    {
        Vector2 ink = _painter.Measure(text, large: false, scale);
        Vector2 position = rightAligned ? new(anchor.X - ink.X, anchor.Y - ink.Y) : new(anchor.X, anchor.Y - ink.Y);
        _painter.Text(text, position, large: false, scale, _painter.Dim);
    }

    /// <summary>A dashed line of alternating segments (no allocation).</summary>
    private void DashedLine(Vector2 a, Vector2 b, float width, Color color)
    {
        Vector2 delta = b - a;
        const int segments = 5;
        for (int i = 0; i < segments; i += 2)
        {
            _painter.Line(a + delta * (i / (float)segments), a + delta * ((i + 1) / (float)segments), width, color);
        }
    }

    /// <summary>The WIP announcement, bottom-centre.</summary>
    private void DrawTag(float w, float h, float scale)
    {
        Vector2 ink = _painter.Measure(TagText, large: false, scale);
        _painter.Text(TagText, new(w * 0.5f, h - 12f * scale - ink.Y * 0.5f), large: false, scale, _painter.Dim, centered: true);
    }

    /// <summary>Cached speed-tape tick label: each visible label owns a stable ring
    /// slot, so a steady tape formats nothing after the first frame.</summary>
    private string SpeedTickText(int value, int slot)
    {
        if (_speedTickText[slot] is { } cached && _speedTickKeys[slot] == value) return cached;
        _speedTickKeys[slot] = value;
        _speedTickText[slot] = value.ToString(CultureInfo.InvariantCulture);
        return _speedTickText[slot];
    }

    private static string FormatSigned(int degrees)
        => degrees > 0 ? "+" + degrees.ToString("00", CultureInfo.InvariantCulture)
            : degrees < 0 ? degrees.ToString("00", CultureInfo.InvariantCulture)
            : "00";

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
