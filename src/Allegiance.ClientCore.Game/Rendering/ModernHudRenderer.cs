using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Flight;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Second skin of HUD-1. Common readouts and visibility come from the
/// classic renderer; this skin replaces artwork, reticles and typography and
/// composes the remaining model groups into the same measured layout.</summary>
public sealed class ModernHudRenderer : HudRenderer, IDisposable
{
    private readonly HudSettings _settings;
    private readonly HudSdfFont _smallSdf, _bigSdf;
    private readonly RadarBracketRenderer _radar;
    private readonly Dictionary<GaugeKind, float> _animated = new();
    private long _previous;
    private float _dt, _scale = 1;
    private readonly HashSet<string> _drawn = new(StringComparer.Ordinal);
    // HUD-12b — per-source-string chat clip cache: the old per-character loop
    // allocated a new rowText + "..." string for every removed glyph, every line,
    // every frame.
    private readonly ChatLineTruncationCache _chatTruncations = new();
    public IReadOnlySet<string> DrawnElements => _drawn;
    private static readonly Color Accent = new(83, 225, 219);
    private static readonly Color Muted = new(162, 189, 201);
    private static readonly Color Alert = new(255, 121, 74);
    private static readonly Color CloakViolet = new(158, 140, 250);
    private static readonly Color CarryGold = new(255, 213, 94);
    // HUD-18 — the four in-range diamond diagonals, a static array so the draw
    // path no longer allocates a new int[] every frame.
    private static readonly (int X, int Y)[] DiamondCorners = [(-1, -1), (-1, 1), (1, -1), (1, 1)];

    public ModernHudRenderer(SpriteBatch batch, Texture2D white, SpriteFont small, SpriteFont big, HudSettings settings,
        SpriteFont? outlineSource = null, StatusTextures? nativeArt = null, HudNativeTextures? nativeHudTextures = null,
        RadarIconTextures? radarIcons = null)
        : base(batch, white, outlineSource is null ? small : Metrics(outlineSource, .25f), outlineSource is null ? big : Metrics(outlineSource, 30f / 56),
            nativeHudTextures: nativeHudTextures)
    {
        _settings = settings;
        _smallSdf = new(white.GraphicsDevice, _small, outlineSource is null ? 1 : .25f);
        _bigSdf = new(white.GraphicsDevice, _big, outlineSource is null ? 1 : 30f / 56);
        _radar = new(batch, white, nativeArt);
        _radar.SetRadarIcons(radarIcons);
    }

    /// <summary>HUD-16 — attach the native contact-icon surfaces once the core
    /// (and therefore the icon name tables) has loaded.</summary>
    public void SetRadarIcons(RadarIconTextures? icons) => _radar.SetRadarIcons(icons);
    private static SpriteFont Metrics(SpriteFont source, float scale)
    {
        var glyphs = source.GetGlyphs().Values.OrderBy(g => g.Character).ToArray();
        return new SpriteFont(source.Texture, glyphs.Select(g => g.BoundsInTexture).ToList(),
            glyphs.Select(g => new Rectangle((int)MathF.Round(g.Cropping.X * scale), (int)MathF.Round(g.Cropping.Y * scale), (int)MathF.Round(g.Cropping.Width * scale), (int)MathF.Round(g.Cropping.Height * scale))).ToList(),
            glyphs.Select(g => g.Character).ToList(), (int)MathF.Round(source.LineSpacing * scale), source.Spacing * scale,
            glyphs.Select(g => new Vector3(g.LeftSideBearing, g.Width, g.RightSideBearing) * scale).ToList(), source.DefaultCharacter);
    }
    protected override bool DeferWarpFlash => true;
    protected override bool UsesModernGauges => true;
    protected override float TextScale => _scale;
    /// <summary>P828 — Modern runs the radar label declutter by default
    /// (<see cref="HudSettings.RadarDeclutterOn"/>: on for Modern, off for
    /// Classic), resolved live from the shared settings instance so the options
    /// menu row takes effect without recreating the renderer.</summary>
    protected override RadarLabelDeclutterOptions RadarDeclutterPlan => _settings.RadarLabelPlan(TextScale);
    // FONT-1 — Modern deliberately keeps its own SDF small/big fonts for every
    // native identity (mapping lgBoldVerdana to _big, everything else to _small)
    // at its own TextScale, so its rendering is unchanged by the classic skin's
    // native-font switch.
    protected override (SpriteFont Font, float Scale) HudFont(NativeFont id, float paneScale)
        => (id == NativeFont.LgBoldVerdana ? _big : _small, TextScale);
    protected override void RenderText(SpriteFont font, string text, Vector2 position, Color color, float scale)
        => (font == _big ? _bigSdf : _smallSdf).Draw(_sb, text, position, color, scale);
    protected override void PrepareLayout(HudModel model, Viewport viewport)
    {
        float scale = Math.Clamp(viewport.Height / 1080f * _settings.Size, .5f, 4);
        if (scale != _scale) { _smallSdf.ResetRasterCache(); _bigSdf.ResetRasterCache(); }
        _scale = scale;
        var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(viewport.Width, viewport.Height);
        Layout.Reserve(new((int)mx, (int)my, (int)mw, (int)mh));
        if (model.FlightVisible && (model.Context.InventoryVisible || model.Context.InventorySlide > 0f)
            && model.Context.InventoryPane is { Rows.Count: > 0 } pane)
            Layout.Reserve(InventoryPaneLayout.ReserveRect(pane.Rows.Count, viewport.Width, viewport.Height));
        float spread = Math.Clamp(viewport.Height / 680f, 1.15f, 2.1f);
        foreach (var gauge in model.Gauges.Where(g => g.Visible))
        {
            float x = viewport.Width / 2f + gauge.NativeOffset.X * spread;
            float y = viewport.Height / 2f + gauge.NativeOffset.Y * spread;
            Layout.Reserve(new((int)(x - 56 * _scale), (int)(y - 40 * _scale), (int)(112 * _scale), (int)(112 * _scale)));
        }
        ReserveTargetPane(model, viewport);
        ReserveCommandSummary(model, viewport);
        ReserveConsoleAnchors(model, viewport);
    }
    public override void Draw(HudModel model, Viewport viewport)
    {
        _drawn.Clear();
        long now = Stopwatch.GetTimestamp();
        _dt = _previous == 0 ? 1 : (float)Math.Clamp(Stopwatch.GetElapsedTime(_previous, now).TotalSeconds, 0, .1);
        _previous = now;
        // Contacts were already filtered using the native LOD keys upstream.
        // Keep their exact projections, silhouettes, selected/build-spot markers.
        _radar.Draw(model, viewport, _settings.ColorBlindSafe);
        base.Draw(model, viewport);
        if (!model.Visible) { _animated.Clear(); return; }
        if (model.FlightVisible)
            Mark("Radar viewport and round border", "Radar LOD modes", "Radar ship contacts", "Radar station contacts", "Radar warp contacts", "Radar asteroid contacts", "Radar missile contacts", "Radar probe and mine contacts", "Radar treasure and tech contacts");
        BeginBatch();
        try { DrawModelOverlays(model, viewport);
            if (model.InstrumentsVisible && model.Telemetry is { WarpFlash: > 0 } warp) FillRect(0, 0, viewport.Width, viewport.Height, Color.Black * warp.WarpFlash);
        }
        finally { _sb.End(); }
    }
    protected override void ElementDrawn(params string[] ids) => Mark(ids);
    private void Mark(params string[] ids) { foreach (var id in ids) _drawn.Add(id); }

    protected override void DrawGauges(HudModel model, Viewport viewport)
    {
        float spread = Math.Clamp(viewport.Height / 680f, 1.15f, 2.1f);
        foreach (var gauge in model.Gauges.Where(g => g.Visible))
        {
            var center = new Vector2(viewport.Width / 2f + gauge.NativeOffset.X * spread, viewport.Height / 2f + gauge.NativeOffset.Y * spread);
            float radius = 32 * _scale;
            float current = _animated.GetValueOrDefault(gauge.Kind, gauge.Fraction);
            // Display the exact value as a number; only geometry eases. Critical
            // colour and the native flash timer never wait for interpolation.
            current += (gauge.Fraction - current) * (1 - MathF.Exp(-_dt * 16));
            _animated[gauge.Kind] = current;
            Color color = gauge.DamageFlash ? Alert : new(gauge.Color.R, gauge.Color.G, gauge.Color.B);
            if (_settings.ColorBlindSafe && gauge.Kind != GaugeKind.Thrust && gauge.Fraction <= .5f) color = new(255, 179, 71);
            Arc(center, radius, -MathF.PI * 1.22f, MathF.PI * 1.44f, Muted * .22f, 2 * _scale);
            Arc(center, radius, -MathF.PI * 1.22f, MathF.PI * 1.44f * current, color * .13f, 6 * _scale);
            Arc(center, radius, -MathF.PI * 1.22f, MathF.PI * 1.44f * current, color, 2 * _scale);
            string percent = HudStringCache.Percent(gauge.Fraction);
            var size = _small.MeasureString(percent);
            DrawMeasuredText(_small, percent, center - size / 2, color, true);
            size = _small.MeasureString(gauge.Label);
            DrawMeasuredText(_small, gauge.Label, new(center.X - size.X / 2, center.Y + radius + 8 * _scale), Muted, true);
            if (gauge.Kind != GaugeKind.Thrust && gauge.Fraction <= (gauge.Kind == GaugeKind.Energy ? .05f : .15f))
            {
                // Shape plus colour: danger remains legible without red/green.
                float pulse = .55f + .45f * MathF.Sin((float)Stopwatch.GetTimestamp() / Stopwatch.Frequency * MathF.Tau * 2);
                DrawMeasuredText(_small, "!", new(center.X + radius, center.Y - radius), color * pulse, true);
            }
            Mark(gauge.Kind switch { GaugeKind.Hull => "Hull integrity", GaugeKind.Shield => "Shield charge", GaugeKind.Energy => "Energy reserve", GaugeKind.Fuel => "Fuel reserve", GaugeKind.Ammo => "Ammunition reserve", _ => "Thrust setting" });
        }
    }

    /// <summary>HUD-6 — HUD-2 style own-ship status: short measured text labels
    /// at the same native centre-HUD anchors the classic skin's icons use.</summary>
    protected override void DrawOwnStatus(HudModel model, Viewport viewport)
    {
        var status = model.Status;
        if (!status.Visible) return;

        DrawStatusText("Detection eye", status.DetectionEyeVisible, "EYE", Muted, viewport);
        DrawStatusText("Cloak active", status.CloakActive, "CLOAK", CloakViolet, viewport);
        DrawStatusText("Autopilot status", status.AutopilotEngaged, "AUTOPILOT", Accent, viewport);
        DrawStatusText("Vector lock status", status.VectorLockEngaged, "VECTOR LOCK", Accent, viewport);
        DrawStatusText("Carried flag", status.CarriedFlag, "FLAG", CarryGold, viewport);
        DrawStatusText("Carried artifact", status.CarriedArtifact, "ARTIFACT", CarryGold, viewport);

        if (status.KillBonusPercent is { } kb)
            DrawStatusText("Kill bonus", true, HudStringCache.KillBonusBadge(kb), CarryGold, viewport);
        if (status.SignaturePercent is { } sig)
            DrawStatusText("Signature percentage", true, HudStringCache.Signature(sig), Accent, viewport);
        // HUD-12b — unknown strength (before the first 220) falls back to the
        // legacy CLOAK DRAINED label instead of dropping the row.
        if (status.CloakActive
            && HudStringCache.CloakStatusText(status.CloakDrained, status.CloakStrengthPercent) is { } cloakText)
            DrawStatusText("Cloak strength and drained state", true, cloakText,
                status.CloakDrained ? Alert : CloakViolet, viewport);
    }

    private void DrawStatusText(string id, bool shown, string text, Color color, Viewport viewport)
    {
        if (!shown) return;
        // HUD-6b — the native TranslateImage anchor is the image CORNER
        // (engine/image.cpp:834-845); draw from that point instead of centring
        // the text on it.
        var pos = OwnStatusLayout.ScreenPos(id, viewport.Width, viewport.Height);
        DrawMeasuredText(_small, text, pos, color, instrument: true);
        Mark(id);
    }

    protected override void DrawCrosshair(Vector2 center, bool firing, bool turret)
    {
        var color = firing ? Color.White : Accent;
        Arc(center, 1.5f * _scale, 0, MathF.Tau, color, 2 * _scale);
        for (int i = 0; i < 4; i++)
        {
            float a = MathF.PI * .5f * i;
            var direction = new Vector2(MathF.Cos(a), MathF.Sin(a));
            DrawLine(center + direction * 9 * _scale, center + direction * 16 * _scale, _scale, color * .7f);
        }
        if (firing) DrawMeasuredText(_small, "FIRING", center + new Vector2(24, 18) * _scale, Alert);
    }
    public override void DrawLeadIndicator(LeadIndicatorView lead)
    {
        var color = lead.InRange ? Color.Lerp(Alert, Accent, lead.Green) : Muted;
        Arc(lead.LeadCenter, 10 * _scale, 0, MathF.Tau, color, 1.5f * _scale);
        // P804 — the target-now tether only when the target projects in view.
        if (lead.TargetVisible)
            DrawLine(lead.LeadCenter, lead.TargetCenter, _scale, color * .28f);
    }
    public override void DrawAimPredictor(LeadIndicatorView v, CameraMode camera, Viewport viewport)
    {
        Vector2 center = camera == CameraMode.Cockpit
            ? new Vector2(viewport.Width / 2f, viewport.Height / 2f)
            : v.LeadCenter;
        var color = v.InRange ? Color.Lerp(Alert, Accent, v.Green) : Muted;
        Arc(center, 7 * _scale, 0, MathF.Tau, color, 1.4f * _scale);
        DrawLine(center + new Vector2(0, -11) * _scale, center + new Vector2(0, -5) * _scale, _scale, color * .8f);
        DrawLine(center + new Vector2(0, 5) * _scale, center + new Vector2(0, 11) * _scale, _scale, color * .8f);
        DrawLine(center + new Vector2(-11, 0) * _scale, center + new Vector2(-5, 0) * _scale, _scale, color * .8f);
        DrawLine(center + new Vector2(5, 0) * _scale, center + new Vector2(11, 0) * _scale, _scale, color * .8f);
    }
    protected override void DrawVelocityMarker(Vector2 position)
    {
        DrawLine(position + new Vector2(0, -8) * _scale, position + new Vector2(0, 8) * _scale, _scale, Accent);
        DrawLine(position + new Vector2(-8, 0) * _scale, position + new Vector2(8, 0) * _scale, _scale, Accent);
        DrawLine(position + new Vector2(-4, -4) * _scale, position + new Vector2(4, 4) * _scale, _scale, Accent * .5f);
    }
    protected override void DrawJoystickIndicator(Vector2 center, float yaw, float pitch)
    {
        // HUD-19c — same native y-up placement/pointing as the classic skin
        // (JoystickLayout: screen centre.X − 100·yaw, centre.Y + 100·pitch;
        // apex from the native atan(pitch/yaw) angle, WinTrek/indicator.cpp:159-167).
        // HUD-19 left Modern on −100·pitch, mirroring it vertically vs classic.
        // Only the glyph look (stroked chevron, brightness ramp) is Modern's own.
        float magnitude = JoystickLayout.Magnitude(yaw, pitch);
        var position = JoystickLayout.Position(center, yaw, pitch);
        var color = Color.White * (.35f + .65f * magnitude);
        float length = 12f * _scale * (0.5f + 0.5f * magnitude);
        var direction = JoystickLayout.ApexDirection(yaw, pitch);
        var tip = position + direction * length;
        var back = position - direction * length;
        var side = new Vector2(-direction.Y, direction.X) * (5f * _scale);
        DrawLine(back + side, tip, 2f * _scale, color);
        DrawLine(back - side, tip, 2f * _scale, color);
    }
    protected override void DrawChaseAim(Vector2 position)
    {
        Arc(position, 9 * _scale, 0, MathF.Tau, Muted, 1.5f * _scale);
        DrawLine(position + new Vector2(0, -4) * _scale, position + new Vector2(0, 4) * _scale, _scale, Muted);
        DrawLine(position + new Vector2(-4, 0) * _scale, position + new Vector2(4, 0) * _scale, _scale, Muted);
    }
    protected override void DrawOrderArrows(HudOrderArrows arrows, Viewport viewport)
    {
        var center = new Vector2(viewport.Width / 2f, viewport.Height / 2f);
        if (arrows.SameTarget && arrows.AcceptedDirection is { } sameDirection)
        {
            DrawOrderArrow(center, sameDirection, new Color(255, 255, 0));
            return;
        }
        if (arrows.AcceptedDirection is { } acceptedDirection)
            DrawOrderArrow(center, acceptedDirection, new Color(0, 255, 0));
        if (arrows.CurrentDirection is { } currentDirection)
            DrawOrderArrow(center, currentDirection, new Color(255, 0, 0));
    }
    private void DrawOrderArrow(Vector2 center, HudPoint direction, Color color)
    {
        var d = new Vector2(direction.X, direction.Y);
        if (d.LengthSquared() < 1e-6f) return;
        d.Normalize();
        var anchor = center + d * 108f;
        var tip = anchor + d * 13f * _scale;
        var side = new Vector2(-d.Y, d.X) * (7f * _scale);
        var back = anchor - d * 4f * _scale;
        DrawLine(back + side, tip, 2.4f * _scale, color);
        DrawLine(back - side, tip, 2.4f * _scale, color);
    }
    protected override void DrawLockBracket(in LockBracketView targetLock)
    {
        float toEdge = MathF.Min(MathF.Min(targetLock.Center.X, targetLock.Center.Y),
            MathF.Min(_viewportWidth - targetLock.Center.X, _viewportHeight - targetLock.Center.Y));
        float radius = LockBracketStyle.HalfSizeForFraction(targetLock.State, targetLock.Fraction, targetLock.ScreenRadius, toEdge);
        var color = targetLock.State == Allegiance.ClientCore.State.MissileLockFeedback.LockState.Partial ? new Color(255, 190, 84) : Alert;
        for (int i = 0; i < 4; i++) Arc(targetLock.Center, radius, i * MathF.PI / 2 + .12f, .85f, color, 2 * _scale);
    }
    protected override void DrawTargetPane(HudModel model, Viewport viewport)
    {
        if (!model.TargetPaneVisible || model.Context.TargetPane is not { Present: true } tp) return;
        float w = viewport.Width, h = viewport.Height;
        float s = TargetPaneLayout.Scale(w, h);
        float x0 = TargetPaneLayout.OriginX(w, h);
        // P805 (row 471) — the native target-pane slide, same rule as classic.
        float y0 = TargetPaneLayout.OriginY(w, h) + TargetPaneLayout.SlideOffset(model.Context.TargetPaneSlide, s);
        var side = tp.SideColor is { } sc ? new Color(sc.R, sc.G, sc.B, sc.A) : new Color(224, 231, 235);
        if (_settings.ColorBlindSafe) side = HudPalette.Side(side);

        // HUD-21b — the 3-D model preview is painted AFTER the arcs below so the
        // modern skin's geometric gauges never cover the model (a modern-skin
        // choice; the classic skin follows native's paint order, where the
        // gauges are on top — see HudRenderer.DrawTargetPaneDecor).

        // Name + type (same native anchor as classic, dialog.mdl:4043-4050).
        string nameLine = (tp.Name ?? "unknown") + (tp.TypeDescription is { } td ? ": " + td : "");
        var nameSize = PaneTextSize(_small, nameLine);
        DrawRawText(_small, nameLine,
            new(x0 + TargetPaneLayout.LocalCenterX * s - nameSize.X / 2f,
                y0 - TargetPaneLayout.LocalNameBottom * s - nameSize.Y), side);

        if (tp.Distance is { } distance)
        {
            string range = string.Concat("RANGE: ", HudFormat.FormatNativeNumber(distance));
            DrawRawText(_small, range, new(x0 + 400f * s, y0 - 33f * s - PaneTextSize(_small, range).Y), Muted);
        }
        else if (tp.SectorName is { } sectorName)
        {
            // HUD-18 — native TargetRangeString's LOCATION: fallback
            // (dialog.mdl:4053-4060), same native anchor as RANGE.
            string location = HudStringCache.Location(sectorName);
            DrawRawText(_small, location, new(x0 + 400f * s, y0 - 33f * s - PaneTextSize(_small, location).Y), Muted);
        }
        if (tp.Speed is { } speed)
        {
            string text = string.Concat("SPEED: ", HudFormat.FormatNativeNumber(speed));
            var size = PaneTextSize(_small, text);
            DrawRawText(_small, text, new(x0 + 242f * s - size.X, y0 - 33f * s - size.Y), Muted);
        }

        // Geometric hull/shield arcs at the native gauge anchors. The arc
        // radius follows the pane scale s (not the Modern UI scale) so the
        // gauge geometry stays glued to the native pane extents.
        float hullX = x0 + (235f + 83f / 2f) * s;
        float shieldX = x0 + (367f + 84f / 2f) * s;
        float gaugeY = y0 - (33f + 101f / 2f) * s;
        float arcRadius = 16f * s;
        DrawModernTargetGauge(new(hullX, gaugeY), tp.HullFraction ?? 0f, side, s);
        DrawModernTargetGauge(new(shieldX, gaugeY), tp.ShieldFraction ?? 0f, side, s);

        // HUD-21/21b — the target's 3-D model preview (after the arcs; empty
        // fallback when the input is null).
        DrawTargetModelPreview(x0, y0, s);

        // ARMOR/SHIELD flank their arcs (native puts them beside the gauges,
        // dialog.mdl:4220-4222) so the labels never sit on the arc stroke.
        var armorSize = PaneTextSize(_small, "ARMOR");
        var shieldSize = PaneTextSize(_small, "SHIELD");
        DrawRawText(_small, "ARMOR", new(hullX - arcRadius - 8f * s - armorSize.X, gaugeY - armorSize.Y / 2f), Muted);
        DrawRawText(_small, "SHIELD", new(shieldX + arcRadius + 8f * s, gaugeY - shieldSize.Y / 2f), Muted);

        // HUD-18 — the native GlassHUD-only TTI line (dialog.mdl:1280-1355).
        // Under the SPEED line, right-aligned to the same x; the string is the
        // cached TargetTti.Text (this method must not allocate per frame).
        if (tp.TimeToIntercept is { } tti)
        {
            string text = HudStringCache.Tti(tti);
            var size = PaneTextSize(_small, text);
            DrawRawText(_small, text, new(x0 + 242f * s - size.X, y0 - 10f * s - size.Y), Muted);
        }

        // Native turret-range flag (dialog.mdl:4073-4081), modern diamond.
        if (tp.InRange)
        {
            var c = new Vector2(x0 + 200f * s, y0 - 90f * s);
            foreach (var (x, y) in DiamondCorners)
                DrawLine(c, c + new Vector2(x * 7, y * 7) * _scale, _scale, side);
        }

        // HUD-20 — the same native TargetExperienceImage anchor (pane-local
        // 280,110 = the text top, engine/image.cpp:336-388), as a Modern text readout; the native switch hides it for
        // non-ships (dialog.mdl:4208-4214,4232).
        if (tp.IsShip && tp.KillBonusPercent is { } killBonus)
        {
            string number = HudStringCache.KillBonusBadge(killBonus);
            var (numberLeft, numberTop) = TargetPaneLayout.ExperienceNumberTopLeft(x0, y0, s,
                PaneTextSize(_small, number).X);   // StringImage origin = text top
            DrawRawText(_small, number, new(numberLeft, numberTop), CarryGold);
            Mark("Target kill bonus");
        }

        ElementDrawn("Target name", "Target type description", "Target side identity",
            "Target hull", "Target shield", "Target range or sector", "Target speed",
            "Target in weapon range", "Target time to intercept");
    }

    private void DrawModernTargetGauge(Vector2 center, float fraction, Color color, float scale)
    {
        float radius = 16f * scale;
        float frac = Math.Clamp(fraction, 0f, 1f);
        Arc(center, radius, -MathF.PI * .72f, MathF.PI * 1.44f, Muted * .22f, 2f * scale);
        if (frac > 0f)
            Arc(center, radius, -MathF.PI * .72f, MathF.PI * 1.44f * frac, color, 2f * scale);
    }

    protected override void DrawTargetSelectionBracket(in TargetSelectionView target)
    {
        // HUD-4 — native projected-radius bracket (radarimage.cpp:296-319).
        float half = target.ScreenRadius is { } radius and > 0f ? MathF.Max(radius * 1.05f, 8f) : TargetSelectionStyle.HalfSize;
        float toEdge = MathF.Min(MathF.Min(target.Center.X, target.Center.Y), MathF.Min(_viewportWidth - target.Center.X, _viewportHeight - target.Center.Y));
        if (half > toEdge + 10f) half = MathF.Max(toEdge + 10f, 8f);
        Color color = target.SideColor is { } side ? new(side.R, side.G, side.B, side.A) : TargetSelectionStyle.ColorFor(target.Engaged, target.IsEnemy);
        if (_settings.ColorBlindSafe) color = target.SideColor is { } nativeSide
            ? HudPalette.Side(new(nativeSide.R, nativeSide.G, nativeSide.B)) : HudPalette.Relationship(target.IsEnemy);
        color = Color.Lerp(color, Color.White, .28f);
        foreach (int x in new[] { -1, 1 }) foreach (int y in new[] { -1, 1 })
        {
            var corner = target.Center + new Vector2(x, y) * half;
            DrawLine(corner, corner - new Vector2(x * 10, 0), _scale * (target.Engaged ? 2 : 1), color);
            DrawLine(corner, corner - new Vector2(0, y * 10), _scale * (target.Engaged ? 2 : 1), color);
        }
        float top = target.Center.Y + half + 8;
        foreach (var text in new[] { target.CharacterName,
            target.Distance is { } d ? $"{d:0} m" + (target.ClosingSpeed is { } closing ? $"  {closing:+0;-0;0} mps" : "") : null,
            target.HullFraction is { } h ? $"HULL {h:P0}" : null,
            target.ShieldFraction is { } s ? $"SHLD {s:P0}" : null,
            target.OreFraction is { } o ? $"ORE {o:P0}" : null })
        {
            if (text is null) continue;
            var box = DrawMeasuredText(_small, text, new(target.Center.X - _small.MeasureString(text).X / 2, top), color);
            top = box.Bottom + 4;
        }
    }
    /// <summary>HUD-7 — the native flight equipment/inventory pane in the
    /// HUD-2 style: a geometric panel at the same SideBottomRight anchor, with
    /// measured SDF text and thin progress bars honouring the HUD size and the
    /// colour-blind-safe palette.</summary>
    protected override void DrawInventoryPane(HudInventoryPane pane, HudModel model, Viewport viewport)
    {
        // HUD-10 — native inventory pane slide (dialog.mdl:5387, 190 px, .5 s).
        float slideOffset = (1f - Math.Clamp(model.Context.InventorySlide, 0f, 1f)) * 190f;
        float w = viewport.Width, h = viewport.Height;
        int rowCount = pane.Rows.Count;
        float s = InventoryPaneLayout.Scale(h);
        float left = InventoryPaneLayout.LeftX(w, h);
        float top = InventoryPaneLayout.TopY(rowCount, w, h) + slideOffset;
        float lineW = InventoryPaneLayout.LineWidth * s;
        float capH = InventoryPaneLayout.CapHeight * s;
        float rowH = InventoryPaneLayout.RowHeight * s;
        float totalH = capH * 2f + rowH * rowCount;

        var panel = new Color(0.02f, 0.055f, 0.06f, 0.82f);
        FillRect(left - 6 * _scale, top - 6 * _scale, lineW + 12 * _scale, totalH + 12 * _scale, panel);
        FillRect(left - 6 * _scale, top - 6 * _scale, 2 * _scale, totalH + 12 * _scale, Accent * 0.6f);
        OutlineRect(left - 6 * _scale, top - 6 * _scale, lineW + 12 * _scale, totalH + 12 * _scale, Muted * 0.35f);

        if (pane.ShipName.Length > 0)
        {
            var nameSize = _small.MeasureString(pane.ShipName) * _scale;
            RenderText(_small, pane.ShipName,
                new(left + MathF.Max(0f, (lineW - nameSize.X) / 2f), top + capH * 0.5f - nameSize.Y * 0.5f), Color.White, _scale);
        }

        float rowTop = top + capH;
        foreach (var row in pane.Rows)
        {
            DrawModernInventoryRow(row, left, rowTop, s);
            rowTop += rowH;
        }
        Mark("Flight equipment inventory pane", "Equipment part identity and key",
            "Equipment selected and active state", "Equipment quantity and empty warning",
            "Equipment mounting progress", "Launcher arming progress", "Equipment weapon range",
            "Equipment shot damage", "Equipment burst rate", "Equipment shield strength",
            "Equipment shield recharge seconds", "Equipment afterburner top speed",
            "Equipment afterburner endurance", "Equipment cloak endurance",
            "Inventory turret gunner identity", "Inventory cargo slots", "Inventory own ship name",
            "Equipment empty slot affordances");
    }

    private void DrawModernInventoryRow(HudInventoryRow row, float left, float rowTop, float s)
    {
        float rowH = InventoryPaneLayout.RowHeight * s;
        float textY = rowTop + 1.5f * _scale;
        var white = Color.White;
        var grey = new Color(162, 189, 201);
        var yellow = new Color(255, 235, 120);

        // HUD-20 — invobserver.mdl:1-12: white "Observers: N" at the native key
        // column Point(23,5), no icon, no key.
        if (row.Kind == HudInventoryRowKind.Observers)
        {
            RenderText(_small, row.Name, new(left + InventoryPaneLayout.KeyX * s, textY), white, _scale);
            return;
        }

        Color keyColor = row.Active ? yellow : row.IsEmpty ? grey : white;
        if (row.Key.Length > 0)
            RenderText(_small, row.Key, new(left + InventoryPaneLayout.KeyX * s, textY), keyColor, _scale);

        Color nameColor = row.Selected ? white : row.IsEmpty ? grey : new Color(224, 236, 238);
        if (row.Name.Length > 0)
        {
            // HUD-11 — no right-hand columns on a cargo/empty row: let the name
            // use the full line so "<cargo mount>" is not clipped.
            bool hasRightColumn = row.OutOfAmmo || row.StatsText is not null || row.ReadyState is 1 or 2;
            float nameMax = Math.Max(0f, ((hasRightColumn ? InventoryPaneLayout.StatsX : InventoryPaneLayout.LineWidth)
                - InventoryPaneLayout.NameX - (hasRightColumn ? 2f : 3f)) * s);
            RenderText(_small, FitText(_small, row.Name, nameMax, _scale),
                new(left + InventoryPaneLayout.NameX * s, textY), nameColor, _scale);
        }

        if (row.OutOfAmmo)
            RenderText(_small, "A", new(left + InventoryPaneLayout.AmmoX * s, textY), Alert, _scale);

        if (row.ReadyState is 1 or 2)
        {
            float bx = left + InventoryPaneLayout.StatsX * s;
            float by = rowTop + 2f * _scale;
            float bw = 64f * s;
            float bh = 14f * s;
            FillRect(bx, by, bw, bh, new Color(0f, 0f, 0f, 0.7f));
            // P786 — ReadyState 2 fills by the live GetArmedFraction
            // (consoledata.cpp:1189-1202), ReadyState 1 by the mount fraction.
            float frac = HudFormat.Clamp01(row.ReadyState == 2 ? (row.ArmedFraction ?? row.MountedFraction) : row.MountedFraction);
            if (frac > 0f)
                FillRect(bx + 1f, by + 1f, (bw - 2f) * frac, bh - 2f,
                    row.ReadyState == 2 ? new Color(140, 220, 150) : new Color(200, 205, 210));
            OutlineRect(bx, by, bw, bh, Muted * 0.4f);
        }
        else if (row.StatsText is { } stats)
        {
            // HUD-11 — scale the native StatsBar reading into its 81-native-px
            // slot instead of clipping it (invsweapon.mdl Point(130,5)).
            float statsMax = Math.Max(0f, (InventoryPaneLayout.LineWidth - InventoryPaneLayout.StatsX - 3f) * s);
            float fit = InventoryPaneLayout.FitFontScale(stats, statsMax, _small, _scale);
            RenderText(_small, stats, new(left + InventoryPaneLayout.StatsX * s, textY), white, _scale * fit);
        }
    }

    private void DrawModelOverlays(HudModel model, Viewport viewport)
    {
        float w = viewport.Width, h = viewport.Height, line = (_small.LineSpacing + 5) * _scale;
        float leftWidth = Math.Max(120, w / 2 - 126.5f * Math.Clamp(h / 680, 1.15f, 2.1f) - 56 * _scale - 40);
        if (model.Context.Chat is { } chat)
        {
            // BuildChatLines already returns the ChatPaneModel's visible window
            // (offset applied once). Re-applying StartIndex here collapsed every
            // scrolled window back to the newest page (P757b).
            // P805 (row 470) — native ShowChatHistoryHUD (WinTrek.cpp:5074-5078)
            // hides the history lines; the compose line still draws.
            bool historyVisible = model.ChatHistoryVisible;
            int take = Math.Min(ChatRenderer.MaxVisible, chat.Lines.Count) * (historyVisible ? 1 : 0);
            // HUD-10/HUD-11 — the native ChatListPane geometry: 355 px wide,
            // configured NumChatLines at the native 14 px line height, CommPane
            // at SideTop(-160,-126) (dialog.mdl:180,5394; chatpane.cpp:374-380).
            // The port glyphs are scaled into the native item slot so history
            // rows never overlap each other or the afterburner/weapons block.
            int paneLines = ChatPaneLayout.ConfiguredLines(chat.PaneLines);
            int paneLeft = ChatPaneLayout.Left(viewport.Width);
            int paneTop = ChatPaneLayout.Top(paneLines);
            float row = ChatPaneLayout.NativeFontHeight;
            float maxWidth = ChatPaneLayout.Width;
            float textScale = ChatPaneLayout.TextScale(_small.LineSpacing);
            float markerY = paneTop - _small.LineSpacing * textScale;
            if (chat.Offset > 0 && historyVisible)
                _smallSdf.Draw(_sb, "(OLDER - PgDn to newest)", new(paneLeft, markerY), Muted, textScale);
            if (chat.FilterLabel is { } fl && take > 0)
            {
                string label = $"[{fl}]";
                float labelX = Math.Max(paneLeft, paneLeft + maxWidth - _small.MeasureString(label).X * textScale);
                _smallSdf.Draw(_sb, label, new(labelX, markerY), Muted, textScale);
            }
            for (int i = 0; i < take; i++)
            {
                Color colour = chat.LineColors is { } colors && i < colors.Count && colors[i] is { } lc
                    ? new Color(lc.R, lc.G, lc.B, lc.A) : Color.White;
                string rowText = chat.Lines[i];
                // HUD-12b — the clip is cached per source string/width instead of
                // allocating rowText + "..." once per removed character per frame.
                float chatWidth = maxWidth - 6f;
                if (_small.MeasureString(rowText).X * textScale > chatWidth)
                    rowText = _chatTruncations.Fit(rowText, chatWidth, _small, textScale);
                _smallSdf.Draw(_sb, rowText, new(paneLeft + 3, paneTop + 3 + i * row), colour, textScale);
            }
            if (take > 0) Mark("Chat history");
            if (chat.Composing)
            {
                var variant = ComposePaneLayout.Variant(chat.ComposeState == NativeComposeState.NotComposing
                    ? NativeComposeState.Chat : chat.ComposeState);
                float composeTop = ComposePaneLayout.Top(viewport.Height, variant.PanelHeight, 1f);
                Color colour = chat.SideColor is { } sc ? new Color(sc.R, sc.G, sc.B, sc.A) : Color.White;
                DrawMeasuredText(_small, chat.Recipient.Trim() + " " + chat.Input + "|",
                    new(ComposePaneLayout.Left(viewport.Width, 1f) + variant.TextLeft * _scale,
                        composeTop + variant.TextTop * _scale), colour, centerAtScale: false, maxWidth: maxWidth);
                Mark("Chat composition and recipient");
            }
        }
        if (model.FlightVisible)
        {
            string radar = $"RADAR {RadarLod.Label(model.Context.RadarMode)}";
            DrawMeasuredText(_small, radar, new(20, h - 54 * _scale), Muted, centerAtScale: false, maxWidth: leftWidth);
            if (model.Context.CargoLine is { } cargo) { DrawMeasuredText(_small, cargo, new(20, h - 132 * _scale), Muted, centerAtScale: false, maxWidth: leftWidth); Mark("Inventory cargo slots"); }
            if (model.Context.FlightPartsLine is { } parts) { DrawMeasuredText(_small, parts, new(20, h - 158 * _scale), Accent, centerAtScale: false, maxWidth: leftWidth); }
        }
        // HUD-11 — below the native chat pane (its bottom is always 126 px from
        // the top, dialog.mdl:5394), so the summary never overprints it.
        float commandY = ChatPaneLayout.BottomFromTop + 6;
        foreach (var command in model.Context.CommandLines)
        {
            DrawMeasuredText(_small, command, new((w - _small.MeasureString(command).X) / 2, commandY), Accent);
            commandY += line; Mark("Command view ship summary");
        }
        if (model.PodVisible)
        {
            float y = Math.Min(h * .82f, h - 190 * _scale);
            string[] lines = { "YOU ARE IN AN EJECT POD", model.Context.PodReturnInstruction ?? "SELECT FRIENDLY BASE / AUTOPILOT TO BASE",
                $"OXYGEN {TimeSpan.FromSeconds(model.Telemetry!.Value.OxygenSecondsRemaining):mm\\:ss}" };
            foreach (var text in lines)
            {
                DrawMeasuredText(_small, text, new((w - _small.MeasureString(text).X) / 2, y), Accent);
                y += line;
            }
            Mark("Eject pod identity panel", "Lifepod oxygen endurance");
        }
        if (model.DeathVisible) { DrawMeasuredText(_big, "YOU WERE KILLED", new(w * .4f, h * .095f), Alert); Mark("Death banner"); }
        if (model.IndicatorsVisible && model.Context.HitConfirmation)
        {
            var c = new Vector2(w / 2, h / 2);
            foreach (int x in new[] { -1, 1 }) foreach (int y in new[] { -1, 1 })
                DrawLine(c + new Vector2(x * 19, y * 19) * _scale, c + new Vector2(x * 25, y * 25) * _scale, 2 * _scale, Color.White);
        }
    }
    private void Arc(Vector2 center, float radius, float start, float sweep, Color color, float thickness)
    {
        int segments = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) * radius / 4));
        for (int i = 0; i < segments; i++)
        {
            float a = start + sweep * i / segments, b = start + sweep * (i + 1) / segments;
            DrawLine(center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius, center + new Vector2(MathF.Cos(b), MathF.Sin(b)) * radius, thickness, color);
        }
    }

    /// <summary>HUD-9 — the same native console-text anchors as Classic, in the
    /// HUD-2 style (SDF text, HUD size scale, colour-blind palette for side hues).</summary>
    protected override void DrawConsoleText(HudModel model, Viewport viewport)
    {
        if (!model.Visible || model.Context.Console is not { } console) return;
        float w = viewport.Width, h = viewport.Height;
        var side = console.SideColor is { } native
            ? (_settings.ColorBlindSafe ? HudPalette.Side(new(native.R, native.G, native.B)) : new Color(native.R, native.G, native.B))
            : Color.White;   // P805 — native GetSideColor fallback is white (consoledata.cpp:99-107)
        var cortText = new Color(0.586f, 0.578f, 0.680f);   // native cortText/softText (dialog.mdl:24, :3517)
        var yellow = new Color(255, 224, 96);
        var red = Alert;
        float lineHeight = TextLineHeight;
        int chatLines = HudConsoleFormat.BannerChatLines(model.Context.Chat);

        if (model.FlightVisible && console.SectorName is { } sector)
        {
            float cx = w - 100 * _scale;
            float labelTop = HudConsoleFormat.SectorLabelTop * _scale;
            var psz = _small.MeasureString("Current location:");
            var nsz = _small.MeasureString(sector);
            DrawMeasuredText(_small, "Current location:", new(cx - psz.X / 2, labelTop), yellow, fixedAnchor: true);
            DrawMeasuredText(_small, sector, new(cx - nsz.X / 2, HudConsoleFormat.NameBelowLabel(labelTop, lineHeight)), cortText, fixedAnchor: true);
            Mark("Sector location");
        }
        else if (HudConsoleFormat.DockedLookingAt(model.Visible, model.FlightVisible,
            model.Context.Docked, model.Context.CommandView, console.ViewedSectorName) is { } looking)
        {
            // HUD-20 — docked command view: "Currently viewing:" + the viewed
            // sector name in native Color(1,0,0) (dialog.mdl:1429-1471,5421-5422).
            float cx = w - 100 * _scale;
            float labelTop = HudConsoleFormat.SectorLabelTop * _scale;
            var psz = _small.MeasureString(looking.Label);
            var nsz = _small.MeasureString(looking.Name);
            DrawMeasuredText(_small, looking.Label, new(cx - psz.X / 2, labelTop), yellow, fixedAnchor: true);
            DrawMeasuredText(_small, looking.Name, new(cx - nsz.X / 2, HudConsoleFormat.NameBelowLabel(labelTop, lineHeight)),
                new Color(looking.Color.R, looking.Color.G, looking.Color.B, looking.Color.A), fixedAnchor: true);
            Mark("Sector location");
        }
        if (model.FlightVisible && console.BoardState == 2 && console.CarrierName is { } carrier)
        {
            float cx = w - 100 * _scale;
            // Native anchors "Turret of:" at -75 (dialog.mdl:5425); Modern's larger
            // font makes the sector NAME above run into it, so it also clears that
            // name (visual-only; the classic HUD keeps the native anchor).
            float labelTop = MathF.Max(HudConsoleFormat.TurretLabelTop * _scale,
                HudConsoleFormat.NameBelowLabel(HudConsoleFormat.SectorLabelTop * _scale, lineHeight) + lineHeight + 4 * _scale);
            var hsz = _small.MeasureString("Turret of:");
            var nsz = _small.MeasureString(carrier);
            DrawMeasuredText(_small, "Turret of:", new(cx - hsz.X / 2, labelTop), yellow, fixedAnchor: true);
            DrawMeasuredText(_small, carrier, new(cx - nsz.X / 2, HudConsoleFormat.NameBelowLabel(labelTop, lineHeight)), red, fixedAnchor: true);
            Mark("Turret carrier identity");
        }
        if (console.AcceptedOrder is not null || console.QueuedOrder is not null)
        {
            float labelTop = HudConsoleFormat.OrderLabelTop * _scale;
            DrawMeasuredText(_small, "Current orders:", new(HudConsoleFormat.OrderLabelX * _scale, labelTop), yellow, fixedAnchor: true, centerAtScale: false);
            if (console.QueuedOrder is { } q)
            {
                // HUD-15 — the same native comqueuedbmp/comacceptbmp glyphs fit the
                // modern skin (16x16 white masks tinted per skin), drawn at the
                // native offsets before their lines.
                DrawOrderLineGlyph(accepted: false, _scale, red);
                DrawMeasuredText(_small, q, new(HudConsoleFormat.OrderTextX * _scale, HudConsoleFormat.OrderQueuedTop(labelTop, _scale)), red, fixedAnchor: true, centerAtScale: false);
                Mark("Queued order text");
            }
            if (console.AcceptedOrder is { } a)
            {
                DrawOrderLineGlyph(accepted: true, _scale, yellow);
                DrawMeasuredText(_small, a, new(HudConsoleFormat.OrderTextX * _scale, HudConsoleFormat.OrderAcceptedTop(labelTop, _scale)), cortText, fixedAnchor: true, centerAtScale: false);
                Mark("Accepted order text");
            }
        }
        if (console.VoteText is { } vote)
        {
            var vsz = _small.MeasureString(vote);
            DrawMeasuredText(_small, vote, new(w / 2f - vsz.X / 2, HudConsoleFormat.VoteTop(chatLines) * _scale), Color.White, fixedAnchor: true);
            Mark("Vote banner");
        }
        int criticalCount = console.CriticalTips.Count;
        for (int slot = 0; slot < criticalCount; slot++)
        {
            var tip = console.CriticalTips[criticalCount - 1 - slot];
            var tsz = _small.MeasureString(tip.Text);
            DrawMeasuredText(_small, tip.Text, new(w / 2f - tsz.X / 2, HudConsoleFormat.CriticalTipTop(chatLines, slot) * _scale), red, fixedAnchor: true);
        }
        if (criticalCount > 0) Mark("Critical tip text lines");
        int ordinary = 0;
        foreach (var tip in console.OrdinaryTips)
        {
            var tsz = _small.MeasureString(tip);
            float fromBottom = (14f + ordinary * 11f) * _scale;
            DrawMeasuredText(_small, tip, new(w / 2f - tsz.X / 2, h - fromBottom - tsz.Y), side);
            ordinary++;
        }
        if (ordinary > 0) Mark("Tip text lines");
        if (model.FlightVisible && model.Context.Ripcord is { } ripcord)
        {
            // FONT-1c — native RipcordCountdownImage is JustifyCenter over a
            // 150-wide box at SideCenter(-75,15) (dialog.mdl:5145-5151,5429),
            // so the glyphs centre on the viewport centre line and use softText.
            var rsz = _small.MeasureString(ripcord) * _scale;
            DrawMeasuredText(_small, ripcord,
                new(HudConsoleFormat.RipcordTextLeft(w, rsz.X, _scale), h / 2f - HudConsoleFormat.RipcordTop * _scale),
                cortText, fixedAnchor: true, centerAtScale: false);
            Mark("Ripcord countdown");
        }
    }
    public void Dispose() { _smallSdf.Dispose(); _bigSdf.Dispose(); }
}

public static class HudPalette
{
    // Blue/orange plus neutral white; marker shapes carry relationship as well.
    public static Color Relationship(bool? enemy) => enemy switch { true => new(255, 179, 71), false => new(86, 180, 233), _ => new(224, 231, 235) };
    public static Color Side(Color color)
    {
        // Preserve side identity by nearest native side hue rather than treating
        // every contact as friendly/enemy (alliances remain native semantics).
        Color[] native = { new(0, 120, 255), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0), new(255, 0, 255), new(0, 255, 255) };
        Color[] safe = { new(86, 180, 233), new(230, 159, 0), new(0, 158, 115), new(240, 228, 66), new(204, 121, 167), new(0, 114, 178) };
        int nearest = Enumerable.Range(0, native.Length).MinBy(i => Vector3.DistanceSquared(color.ToVector3(), native[i].ToVector3()));
        return safe[nearest];
    }
}
