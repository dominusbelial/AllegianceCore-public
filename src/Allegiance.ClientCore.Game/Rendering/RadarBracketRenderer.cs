using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumericsVec2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P250 — draws the local current-sector tactical radar: a small icon for every
/// nearby ship / station / warp in the player's sector. On-screen objects get
/// their icon at the real projected pixel; off-screen objects get a marker
/// clamped to the viewport edge in their real direction (the edge-bracket
/// technique — native's <c>RadarImage::DrawBlip</c>, radarimage.cpp:286-427).
/// This is the port of native's on-screen 3D-viewport bracket system, NOT a
/// classic top-down radar disc.
///
/// Scope (per brief): flat single detail level — no text labels, no LOD modes,
/// no importance-tiered brightness (those are named follow-ups). Object type is
/// told apart by a simple shape + colour. Native has no distance fade
/// (radarimage.cpp:689-700 is commented out), so contacts draw at full opacity.
///
/// P436 (this stage's follow-up): the player's OWN currently-selected target
/// (<see cref="RadarContact.IsSelectedTarget"/>, set from <c>_autoTarget</c>
/// in Game1's radar gather loop) is given a distinct, bright pulsing highlight
/// (native's dedicated target detail tier <c>c_rlTarget</c>) — on-screen and,
/// crucially, when edge-clamped off-screen, where the base blip would otherwise
/// be indistinguishable from any other.
///
/// Like <see cref="HudRenderer"/> / <see cref="StarmapRenderer"/>, this begins
/// and ends its own <see cref="SpriteBatch"/> session so it can be invoked over
/// the 3D scene (or into an offscreen capture target) at any point in the draw
/// order.
/// </summary>
public sealed class RadarBracketRenderer
{
    /// <summary>The kind of radar contact (drives the icon shape + colour).</summary>
    public enum RadarContactType
    {
        Ship,
        Station,
        Warp,
        /// <summary>P495 — an asteroid contact. Native shows ordinary rocks in
        /// All/Default within the 1000 m LOD clip and named special rocks in
        /// every mode; the port's build-spot highlight reuses this kind with
        /// <see cref="RadarContact.IsInvestBuildSpot"/>.</summary>
        Asteroid,
        Missile,
        Probe,
        Mine,
        /// <summary>HUD-4 — a useful treasure part (radarimage.cpp:437-511).</summary>
        Treasure,
        /// <summary>HUD-4 — a treasure that resolves to the tech icon
        /// (radarimage.cpp:506-511).</summary>
        Tech,
    }


    /// <summary>One placed contact: type, resolved viewport pixel (on-screen
    /// exact, or edge-clamped), whether it is on-screen, and full opacity.
    /// <paramref name="SideColour"/> is the contact's REAL per-side colour
    /// (P372 — resolved from the side roster via
    /// <c>ClientWorld.TryGetSideColor</c>, native <c>pside-&gt;GetColor()</c>,
    /// radarimage.cpp:682-685); when null the renderer falls back to the
    /// type-based palette colour. Ships always supply their real side colour;
    /// stations/warps may leave it null and keep their type-based hue.
    /// <paramref name="IsSelectedTarget"/> (P436) flags the player's OWN
    /// currently-selected target (<c>_autoTarget</c>, the same field the P415
    /// U/Z filtered-target keys write), which the renderer draws with a
    /// distinct highlight — on-screen AND when edge-clamped off-screen.</summary>
    public readonly record struct RadarContact(
        RadarContactType Type,
        NumericsVec2 ScreenPoint,
        bool OnScreen,
        float Opacity,
        Color? SideColour = null,
        bool IsSelectedTarget = false,
        bool IsInvestBuildSpot = false,
        // HUD-5 overlay bracket flags (native radarimage.cpp:334-357).
        bool IsEnemy = false,
        bool IsSelf = false,
        bool AcceptedOrder = false,
        bool QueuedOrder = false,
        bool Threat = false,
        bool ThreatFlash = false,
        bool Subject = false,
        bool CarriedFlag = false,
        bool CarriedArtifact = false,
        bool Ripcord = false,
        bool Highlight = false,
        bool NearestEnemy = false,
        // HUD-4 — a treasure contact that resolves to the native tech icon
        // (radarimage.cpp:506-511).
        bool IsTech = false,
        // HUD-13 — the native bracket-sizing inputs for this contact
        // (radarimage.cpp:307-319): the projected screen radius and the placed
        // point's distance to the nearest viewport edge. Null keeps the
        // pre-HUD-13 fixed overlay radius (FallbackBracketRadius) for callers
        // that do not build a full radar snapshot.
        float? ScreenRadius = null,
        float? DistanceToEdge = null,
        // HUD-16 — the native contact icon surface for this contact
        // (native pmodel->GetIcon(), radarimage.cpp:591-593): the
        // '<iconName>bmp' stem resolved by RadarIconNameCache. Null keeps the
        // procedural shape (missing/blank art, or a kind with no native icon).
        string? IconStem = null);

    private readonly SpriteBatch _sb;
    // Round-radar mode for the native icon offset (radarimage.cpp:363-365); set per HUD frame.
    private bool _roundRadarIcons;
    private readonly Texture2D _white;
    // HUD-12 — the native radar bracket surfaces (radarimage.cpp:219-230,343-357;
    // WinTrek/artwork.h:73-76). Null when the artwork is absent; the procedural
    // frames below then remain the fallback.
    private Texture2D? _artFlag, _artArtifact, _artSubject, _artRipcord, _artThreat;
    // HUD-13 — the nearest-enemy and selected-target bracket surfaces
    // (artwork.h:68,70; drawn at radarimage.cpp:334,352). Null → procedural
    // diamond / P436 highlight fallback.
    private Texture2D? _artEnemy, _artTargeted;
    // HUD-16 — the native per-contact icon surfaces (pmodel->GetIcon(),
    // radarimage.cpp:591-593,417-420). Null → every contact keeps its
    // procedural shape.
    private RadarIconTextures? _icons;

    // Icon palette — the teal HUD accent family for ships (the familiar "other
    // craft" colour), warm gold for stations (infrastructure), violet for warps
    // (the swirl portals). Flat single detail level, no tiering. Public so the
    // type-based fallback mapping (ContactColour) is unit-testable.
    public static readonly Color ShipColour = new(0.28f, 0.85f, 0.85f);
    public static readonly Color StationColour = new(1.00f, 0.74f, 0.26f);
    public static readonly Color WarpColour = new(0.86f, 0.44f, 0.97f);
    // HUD-4 — native per-kind fallback hues for the radar contact vocabulary.
    // Side-bearing contacts (missile/probe/mine) normally carry their real side
    // colour; these only apply while no roster colour is known yet.
    public static readonly Color MissileColour = new(0.96f, 0.36f, 0.30f);
    public static readonly Color ProbeColour = new(0.42f, 0.80f, 0.96f);
    public static readonly Color MineColour = new(0.98f, 0.72f, 0.26f);
    public static readonly Color TreasureColour = new(0.95f, 0.93f, 0.80f);
    public static readonly Color TechColour = new(0.55f, 0.95f, 0.62f);
    // P436 — the player's OWN currently-selected target's highlight. Bright
    // white so no per-side blip colour (arbitrary rosters) or the teal/gold/
    // violet palette above can be mistaken for it; combined with the pulsing
    // ring + diamond shape it is unmistakable from every ordinary contact.
    // Public so the chosen hue is unit-testable (TargetSelectionStyle-style).
    public static readonly Color TargetHighlightColour = new(1.00f, 1.00f, 1.00f);

    // P495 — the muted grey-blue of a rock silhouette (the base icon for the
    // radar's build-spot asteroid contacts; only drawn while a station-type
    // investment is hovered).
    public static readonly Color RockColour = new(0.55f, 0.585f, 0.605f);

    // P495 — the build-spot highlight for the matching asteroids of a hovered
    // station-type investment (native SetInvestAsteroid). Amber-orange,
    // distinct from the white selected-target highlight and the gold station
    // blip, and paired with a pulsing double-ring + dot (no target diamond) so
    // it cannot alias the target "lock". Public so the hue is unit-testable.
    public static readonly Color BuildSpotHighlightColour = new(1.00f, 0.665f, 0.075f);

    // HUD-5 — overlay bracket palette. Native uses per-command artwork surfaces
    // (radarimage.cpp:328-357); the port keeps procedural frames in the same
    // positions, using distinct hues so the overlays never alias the base icons.
    // Accepted/queued orders have no native bracket (radarimage.cpp:352 draws
    // only target/enemy/me/subject/flag/artifact/rip/targeted/threat) — they are
    // conveyed by the label plus the native brightness step instead.
    public static readonly Color EnemyBracketColour = new(1.00f, 0.20f, 0.15f);
    public static readonly Color SubjectBracketColour = new(0.95f, 0.98f, 1.00f);
    public static readonly Color FlagMarkerColour = new(1.00f, 0.82f, 0.20f);
    public static readonly Color ArtifactMarkerColour = new(0.86f, 0.44f, 0.97f);
    public static readonly Color RipcordMarkerColour = new(0.30f, 0.90f, 0.98f);
    public static readonly Color ThreatMarkerColour = new(1.00f, 0.10f, 0.05f);
    public static readonly Color HighlightMarkerColour = new(1.00f, 1.00f, 1.00f);

    public RadarBracketRenderer(SpriteBatch spriteBatch, Texture2D whitePixel,
        StatusTextures? nativeArt = null)
    {
        _sb = spriteBatch;
        _white = whitePixel;
        SetNativeArt(nativeArt);
    }

    /// <summary>HUD-12 — attach the decoded native bracket bitmaps. Safe to call
    /// before/after construction; a null argument leaves every overlay on its
    /// procedural fallback.</summary>
    public void SetNativeArt(StatusTextures? art)
    {
        _artFlag = art?.RadarFlag;
        _artArtifact = art?.RadarArtifact;
        _artSubject = art?.RadarSubject;
        _artRipcord = art?.RadarRipcord;
        _artThreat = art?.RadarThreat;
        _artEnemy = art?.RadarEnemy;
        _artTargeted = art?.RadarTargeted;
    }

    /// <summary>HUD-16 — attach the decoded native contact-icon surfaces
    /// (<see cref="RadarIconTextures"/>). Safe to call after construction; null
    /// leaves every contact on the procedural fallback.</summary>
    public void SetRadarIcons(RadarIconTextures? icons) => _icons = icons;

    /// <summary>HUD-16 — the colour native tints a contact's icon with
    /// (<c>radarimage.cpp:678-685</c>): <c>colorIcon</c> is the side colour, red
    /// while the damage flash is lit, and the neutral constant (white,
    /// <c>radarimage.cpp:1471</c>) for an object without a side. Pure.</summary>
    public static Color NativeIconColour(Color? sideColour, bool threatFlash)
    {
        if (sideColour is { } side) return threatFlash ? Color.Red : side;
        return Color.White;
    }

    /// <summary>HUD-13 — native <c>DrawBlip</c>'s bracket radius
    /// (<c>WinTrek/radarimage.cpp:307-319</c>): the contact's projected screen
    /// radius capped at <c>sqrt2*80</c>, scaled by 1.05, then clipped so the
    /// bracket stays on screen:
    /// <c>if (radiusBracket &gt; distanceToEdge + c_flBorderSide) radiusBracket =
    /// distanceToEdge + c_flBorderSide</c>. Pure and GPU-free so the sizing rule
    /// is unit-testable without a graphics device.</summary>
    /// <param name="screenRadius">Native <c>pts-&gt;GetScreenRadius()</c> — the
    /// contact's projected radius in px (see <see cref="RadarScreenRadius"/>).</param>
    /// <param name="distanceToEdge">Native <c>pts-&gt;GetDistanceToEdge()</c> — the
    /// placed blip's distance to the nearest viewport edge, px.</param>
    public static float BracketRadius(float screenRadius, float distanceToEdge)
    {
        float radiusObject = screenRadius > RadarScreenRadius.MaxObjectScreenRadius
            ? RadarScreenRadius.MaxObjectScreenRadius : screenRadius;
        float radiusBracket = 1.05f * radiusObject;
        // c_flBorderSide (radarimage.h:20) — the gutter native adds to the edge
        // distance so the bracket never extends past the screen.
        float clip = (distanceToEdge > 0f ? distanceToEdge : 0f) + LockBracketStyle.BorderSide;
        return radiusBracket > clip ? clip : radiusBracket;
    }

    /// <summary>HUD-13 — the bracket radius for one contact: the native formula
    /// when the snapshot carried the projection, else the pre-HUD-13 fixed
    /// overlay radius (kept only for callers that build a bare contact). Pure.</summary>
    public static float BracketRadiusFor(in RadarContact c) => c.ScreenRadius is { } radius
        ? BracketRadius(radius, c.DistanceToEdge ?? float.PositiveInfinity)
        : FallbackBracketRadius;

    /// <summary>The pre-HUD-13 fixed overlay bracket radius, used only when a
    /// contact arrives without its projected screen radius.</summary>
    public const float FallbackBracketRadius = 15f;

    /// <summary>HUD-12 — the native <c>DrawExpandedBlip</c> quadrant geometry
    /// (radarimage.cpp:255-277): <c>delta = radius/√2</c>, floored at
    /// <c>m = (xsize/2)/2 = xsize/4</c> (the native code only uses the
    /// horizontal midpoint), each quarter of the bitmap drawn from the contact
    /// centre with the offset below. Pure and GPU-free so the rule is testable
    /// without a graphics device.</summary>
    public static float ExpandedBlipDelta(int textureWidth, int textureHeight, float bracketRadius)
    {
        float delta = bracketRadius / MathF.Sqrt(2f);
        float minimum = textureWidth / 4f;
        return delta < minimum ? minimum : delta;
    }

    /// <summary>HUD-12 — source rectangle of native DrawExpandedBlip quadrant
    /// <paramref name="quadrant"/> (0=top-left, 1=top-right, 2=bottom-right,
    /// 3=bottom-left) for a <paramref name="width"/>×<paramref name="height"/>
    /// surface. Pure.</summary>
    public static Rectangle ExpandedBlipQuadrant(int quadrant, int width, int height)
    {
        int halfW = width / 2, halfH = height / 2;
        return quadrant switch
        {
            0 => new Rectangle(0, 0, halfW, halfH),
            1 => new Rectangle(halfW, 0, width - halfW, halfH),
            2 => new Rectangle(halfW, halfH, width - halfW, height - halfH),
            3 => new Rectangle(0, halfH, halfW, height - halfH),
            _ => throw new ArgumentOutOfRangeException(nameof(quadrant)),
        };
    }

    /// <summary>Draw every contact's icon (or edge-bracket) for this frame.
    /// <paramref name="highlightPulse"/> is a 0..1 frame-independent phase
    /// (computed by the caller from wall time) driving the selected-target
    /// highlight's pulse; pass 0 for a steady (non-pulsing) marker.</summary>
    public void Draw(IReadOnlyList<RadarContact> contacts, Viewport viewport, float highlightPulse = 0f)
    {
        if (contacts.Count == 0)
            return;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            foreach (var c in contacts)
                DrawContact(c, viewport, highlightPulse);
        }
        finally
        {
            _sb.End();
        }
        // HUD-16 — native draws the model icon AFTER the brackets, additively
        // (radarimage.cpp:417-420); a second additive pass keeps that blend mode
        // and layering without disturbing the shared alpha batch above.
        DrawNativeIcons(contacts, viewport);
    }

    /// <summary>Classic radar skin for the shared HUD snapshot.</summary>
    public void Draw(HudModel model, Viewport viewport, bool colorBlindSafe = false)
    {
        if (!model.FlightVisible) return;
        _roundRadarIcons = model.Context.RoundRadar;
        if (model.Context.RoundRadar)
            DrawRoundBorder(viewport);
        Draw(model.Context.Contacts, viewport, model.Context.RadarPulse, colorBlindSafe);
    }

    /// <summary>Draw every contact straight from the model's contact list,
    /// mapping each struct on the stack — no per-frame <c>RadarContact[]</c>
    /// copy (HUD-5b: the former LINQ <c>Select…ToArray</c> allocated once per
    /// frame per contact at 107v107).</summary>
    public void Draw(IReadOnlyList<HudContact> contacts, Viewport viewport, float highlightPulse, bool colorBlindSafe)
    {
        if (contacts.Count == 0)
            return;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            for (int i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                Color? side = c.SideColor is { } color
                    ? (colorBlindSafe
                        ? HudPalette.Side(new Color(color.R, color.G, color.B, color.A))
                        : new Color(color.R, color.G, color.B, color.A))
                    : null;
                DrawContact(new RadarContact((RadarContactType)c.Kind,
                    new(c.Position.X, c.Position.Y), c.OnScreen, c.Opacity, side,
                    c.Selected, c.BuildSpot,
                    c.IsEnemy, c.IsSelf, c.AcceptedOrder, c.QueuedOrder, c.Threat, c.ThreatFlash,
                    c.Subject, c.CarriedFlag, c.CarriedArtifact, c.Ripcord, c.Highlight, c.NearestEnemy,
                    c.IsTech, c.ScreenRadius, c.DistanceToEdge, c.IconStem), viewport, highlightPulse);
            }
        }
        finally
        {
            _sb.End();
        }
        // HUD-16 — the additive native-icon pass (radarimage.cpp:417-420).
        // Reuse the mapped contact array view: DrawNativeIcons maps the same
        // struct on the stack, so there is no per-frame copy.
        DrawNativeIcons(contacts, viewport, colorBlindSafe);
    }

    /// <summary>HUD-4 — native optional round radar border (radarimage.cpp:1130-1177):
    /// a 60-segment circle centred on the viewport, radius 0.48×(height−20),
    /// colour (0.5,0.5,0.5,0.25). Drawn first, beneath the contact icons.</summary>
    public void DrawRoundBorder(Viewport viewport)
    {
        float radius = (viewport.Height - 20f) * 0.48f;
        if (radius <= 0f) return;
        var center = new Vector2(viewport.Width * 0.5f, viewport.Height * 0.5f);
        var color = new Color(0.5f, 0.5f, 0.5f, 0.25f);
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            const int segments = 60;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathHelper.TwoPi / segments;
                float a1 = (i + 1) * MathHelper.TwoPi / segments;
                DrawLine(center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius,
                         center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius, 1f, color);
            }
        }
        finally { _sb.End(); }
    }

    /// <summary>Resolve the colour a contact should be drawn with. A contact
    /// that supplies a real <paramref name="sideColour"/> (P372 — every ship
    /// carries its real side colour; native colours every side-bearing blip by
    /// <c>pside-&gt;GetColor()</c>, radarimage.cpp:682-685) uses it; otherwise
    /// the type-based palette colour applies. Pure and GPU-free so the mapping
    /// is unit-testable without a graphics device.</summary>
    public static Color ContactColour(RadarContactType type, Color? sideColour)
    {
        if (sideColour is { } sc)
            return sc;
        return type switch
        {
            RadarContactType.Ship => ShipColour,
            RadarContactType.Station => StationColour,
            RadarContactType.Asteroid => RockColour,
            RadarContactType.Missile => MissileColour,
            RadarContactType.Probe => ProbeColour,
            RadarContactType.Mine => MineColour,
            RadarContactType.Treasure => TreasureColour,
            RadarContactType.Tech => TechColour,
            _ => WarpColour,
        };
    }

    private void DrawContact(RadarContact c, Viewport viewport, float highlightPulse)
    {
        var pt = new Vector2(c.ScreenPoint.X, c.ScreenPoint.Y);
        float a = Math.Clamp(c.Opacity, 0f, 1f);
        // Native threat flash overrides a side colour with red while the damage
        // bucket's flash phase is lit (radarimage.cpp:682-684); neutral objects
        // keep their neutral white.
        var colour = c.ThreatFlash && c.SideColour is not null
            ? Color.Red : ContactColour(c.Type, c.SideColour);
        // HUD-16 — when this contact's native icon surface decoded, the
        // additive icon pass draws it and the procedural shape below is the
        // fallback only (native has no shape at all, radarimage.cpp:417-420).
        bool hasIcon = _icons?.Peek(c.IconStem) is not null;
        if (!hasIcon)
            switch (c.Type)
            {
                case RadarContactType.Ship:
                    // Small filled square, coloured by the contact's REAL side
                    // (P372 — native colours every side-bearing blip by
                    // pside->GetColor(), radarimage.cpp:682-685), falling back to
                    // the type-based teal only when no roster colour is known yet.
                    DrawFilledSquare(pt, 8f, colour, a);
                    break;
                case RadarContactType.Station:
                    // Larger dot + outer ring (a station is bigger/heavier than a ship).
                    DrawRing(pt, 7f, 1.6f, colour, a);
                    DrawFilledDot(pt, 3.2f, colour, a);
                    break;
                case RadarContactType.Warp:
                    // Two concentric rings suggesting the swirling vortex mouth.
                    DrawRing(pt, 7f, 1.5f, colour, a);
                    DrawRing(pt, 4f, 1.2f, colour, a);
                    break;
                case RadarContactType.Asteroid:
                    // P495 — a build-spot rock silhouette: a small stroked hexagon
                    // in muted stone grey. These contacts exist ONLY while a
                    // station-type investment is hovered and only for the matching
                    // rocks, so the highlight below is the meaning-carrying part.
                    DrawHexagon(pt, 5f, colour, a);
                    break;
                case RadarContactType.Missile:
                    // HUD-4 — a small rotated-square outline (a fast, armed dart).
                    DrawDiamond(pt, 6f, colour, a);
                    break;
                case RadarContactType.Probe:
                    // HUD-4 — a hollow sensor ring + centre pip.
                    DrawRing(pt, 5f, 1.4f, colour, a);
                    DrawFilledDot(pt, 1.6f, colour, a);
                    break;
                case RadarContactType.Mine:
                    // HUD-4 — a filled dot with four short cross ticks (a mine).
                    DrawFilledDot(pt, 2.6f, colour, a);
                    DrawMineCross(pt, 6f, colour, a);
                    break;
                case RadarContactType.Treasure:
                    // HUD-4 — a hollow square crate outline.
                    DrawHollowSquare(pt, 6f, colour, a);
                    break;
                case RadarContactType.Tech:
                    // HUD-4 — a triangle outline (research/tech).
                    DrawTriangle(pt, 7f, colour, a);
                    break;
            }
        // HUD-4 — native c_maskMe own-ship marker (radarimage.cpp:733-735): a
        // distinct hollow square around the own ship's square icon.
        // (HUD-16: when the ship icon art decodes the base shape above is
        // replaced by the icon; the own-ship marker is a separate native
        // bracket surface and stays procedural as before, see HUD-13.)
        if (c.IsSelf)
            DrawHollowSquare(pt, 11f, colour, a);
        // HUD-5 — native overlay brackets (radarimage.cpp:334-357), drawn over
        // the base icon: nearest enemy, command subjects, carried flag/artifact,
        // ripcord, threat last-fired and the flag-treasure highlight. Accepted
        // and queued orders have no native bracket — they change the label and
        // the bracket brightness only (radarimage.cpp:1094-1096), so they are
        // deliberately absent here.
        // HUD-13 — the selected target's native targetedbmp bracket and the
        // P436 procedural fallback both live here (with the art present the
        // overlay draws the native surface AFTER the base icon, so it still
        // dominates on-screen and when edge-clamped).
        DrawOverlayBrackets(c, pt, a, colour, highlightPulse);
        // P495 — a build-spot asteroid of the hovered station-type investment:
        // amber pulsing marker (native SetInvestAsteroid highlight), drawn on
        // top so the matched rocks read clearly against the scene.
        if (c.IsInvestBuildSpot)
            DrawBuildSpotHighlight(pt, a, highlightPulse);
    }

    /// <summary>HUD-16 — draw every contact's native model icon in the additive
    /// pass native uses (<c>radarimage.cpp:417-420</c>): 1:1 pixel size, centred
    /// on <c>positionObject + positionIcon</c> (<see cref="RadarIconPlacement"/>),
    /// tinted with <c>colorIcon</c> (<see cref="NativeIconColour"/>). Contacts
    /// whose art is absent are skipped here and keep their procedural shape from
    /// the alpha pass.</summary>
    private void DrawNativeIcons(IReadOnlyList<RadarContact> contacts, Viewport viewport)
    {
        var icons = _icons;
        if (icons is null) return;
        // Resolve every surface BEFORE the batch begins: the first sight of a
        // per-instance icon (a warp's map icon) decodes it here, once.
        bool any = false;
        for (int i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            if (icons.EnsureLoaded(c.IconStem) is not null) any = true;
        }
        if (!any) return;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Additive);
        try
        {
            for (int i = 0; i < contacts.Count; i++)
                DrawNativeIcon(contacts[i], viewport, icons);
        }
        finally
        {
            _sb.End();
        }
    }

    /// <summary>HUD-16 — the HudContact view of <see cref="DrawNativeIcons"/>,
    /// mapping the colour-blind-safe side colour on the stack (no copy).</summary>
    private void DrawNativeIcons(IReadOnlyList<HudContact> contacts, Viewport viewport, bool colorBlindSafe)
    {
        var icons = _icons;
        if (icons is null) return;
        bool any = false;
        for (int i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            if (icons.EnsureLoaded(c.IconStem) is not null) any = true;
        }
        if (!any) return;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Additive);
        try
        {
            for (int i = 0; i < contacts.Count; i++)
            {
                var c = contacts[i];
                Color? side = c.SideColor is { } color
                    ? (colorBlindSafe
                        ? HudPalette.Side(new Color(color.R, color.G, color.B, color.A))
                        : new Color(color.R, color.G, color.B, color.A))
                    : null;
                DrawNativeIcon(new RadarContact((RadarContactType)c.Kind,
                    new(c.Position.X, c.Position.Y), c.OnScreen, c.Opacity, side,
                    c.Selected, c.BuildSpot,
                    c.IsEnemy, c.IsSelf, c.AcceptedOrder, c.QueuedOrder, c.Threat, c.ThreatFlash,
                    c.Subject, c.CarriedFlag, c.CarriedArtifact, c.Ripcord, c.Highlight, c.NearestEnemy,
                    c.IsTech, c.ScreenRadius, c.DistanceToEdge, c.IconStem), viewport, icons);
            }
        }
        finally
        {
            _sb.End();
        }
    }

    private void DrawNativeIcon(in RadarContact c, Viewport viewport, RadarIconTextures icons)
    {
        if (icons.Peek(c.IconStem) is not { } texture) return;
        // Native draws the icon at positionObject + positionIcon
        // (radarimage.cpp:382-420). Only the on-screen-large state offsets it.
        float bracketRadius = BracketRadiusFor(c);
        float distanceToEdge = c.DistanceToEdge
            ?? RadarScreenRadius.DistanceToEdge(c.ScreenPoint.X, c.ScreenPoint.Y,
                viewport.Width, viewport.Height);
        var offset = RadarIconPlacement.Offset(c.OnScreen, c.ScreenPoint.X, c.ScreenPoint.Y,
            viewport.Width, viewport.Height, distanceToEdge, bracketRadius, _roundRadarIcons);
        var position = new Vector2(c.ScreenPoint.X, c.ScreenPoint.Y) + offset;
        // Scale RGB only: the Additive blend already multiplies by source alpha,
        // so scaling alpha too would dim the icon by opacity squared.
        var tint = NativeIconColour(c.SideColour, c.ThreatFlash);
        float opacity = Math.Clamp(c.Opacity, 0f, 1f);
        tint = new Color((byte)(tint.R * opacity), (byte)(tint.G * opacity), (byte)(tint.B * opacity), tint.A);
        _sb.Draw(texture, position, null, tint, 0f,
            new Vector2(texture.Width * 0.5f, texture.Height * 0.5f), 1f, SpriteEffects.None, 0f);
    }

    /// <summary>HUD-5b — whether native draws any overlay bracket at all for
    /// this contact. The bracket surfaces are exactly
    /// enemy/me/subject/flag/artifact/rip/targeted/last-fired
    /// (radarimage.cpp:352-357); an accepted or queued order is NOT one of them
    /// (native conveys it through the command icon plus the label/bracket
    /// brightness, radarimage.cpp:1094-1096), so the former green/amber corner
    /// frames are gone. P805 (rows 428/437): c_maskHighlight (the flag-treasure
    /// highlight, radarimage.cpp:637-640) has NO surface either — native loads
    /// no highlight bitmap and DrawBlip never draws one — so it is no longer a
    /// bracket either; it only keeps the FULL brightness (radarimage.cpp:1094).
    /// Pure and GPU-free so the rule is unit-testable.</summary>
    public static bool HasOverlayBracket(in RadarContact c)
        => c.NearestEnemy || c.Subject || c.CarriedFlag || c.CarriedArtifact
           || c.Ripcord || c.Threat;

    private void DrawOverlayBrackets(RadarContact c, Vector2 pt, float a, Color colour, float highlightPulse)
    {
        // HUD-13 — every bracket is drawn at this contact's native radius
        // (radarimage.cpp:307-319) instead of the old fixed 15 px.
        float bracketRadius = BracketRadiusFor(c);
        // Native draws every bracket in colorOther, the brightness-dimmed side
        // colour (radarimage.cpp:1094-1096): ×1.0 for the important masks,
        // ×0.75 for a queued order, ×0.5 otherwise. c_maskRip is the exception:
        // it is drawn in colorIcon, the undimmed side colour (radarimage.cpp:350).
        float brightness = RadarLabelModel.BracketBrightness(
            importantMask: c.IsSelectedTarget || c.AcceptedOrder || c.Threat || c.ThreatFlash
                || c.Highlight || c.CarriedFlag || c.CarriedArtifact,
            queued: c.QueuedOrder);
        float ba = Math.Max(a, .9f) * brightness;
        float iconAlpha = Math.Max(a, .9f);
        // HUD-13 — the selected target's native c_maskTarget surface
        // (targetedbmp, radarimage.cpp:352; artwork.h:70). Without the art the
        // P436 procedural highlight remains the fallback.
        if (c.IsSelectedTarget)
        {
            if (_artTargeted is { } targetArt)
                DrawExpandedBlip(targetArt, pt, colour, ba, bracketRadius);
            else
                DrawTargetHighlight(pt, a, highlightPulse);
        }
        if (!HasOverlayBracket(c)) return;
        // HUD-12 — native surfaces first (DrawExpandedBlip, radarimage.cpp:343-357);
        // procedural frames only while the artwork is unavailable.
        if (c.NearestEnemy)
        {
            // HUD-13 — native c_maskEnemy → m_psurfaceEnemy (enemybmp,
            // radarimage.cpp:334; artwork.h:68). The procedural diamond is the
            // fallback when the art is absent, scaled to the same radius.
            if (_artEnemy is { } enemyArt)
                DrawExpandedBlip(enemyArt, pt, colour, ba, bracketRadius);
            else
                DrawDiamond(pt, bracketRadius, EnemyBracketColour, ba);
        }
        if (c.Subject)
        {
            if (_artSubject is { } art) DrawExpandedBlip(art, pt, colour, ba, bracketRadius);
            else DrawOpenCorners(pt, bracketRadius, 1.6f, SubjectBracketColour, ba);
        }
        if (c.CarriedFlag)
        {
            if (_artFlag is { } art) DrawExpandedBlip(art, pt, colour, ba, bracketRadius);
            else DrawPennant(pt, bracketRadius, FlagMarkerColour, ba);
        }
        if (c.CarriedArtifact)
        {
            if (_artArtifact is { } art) DrawExpandedBlip(art, pt, colour, ba, bracketRadius);
            else DrawPennant(pt, bracketRadius, ArtifactMarkerColour, ba);
        }
        if (c.Ripcord)
        {
            if (_artRipcord is { } art) DrawExpandedBlip(art, pt, colour, iconAlpha, bracketRadius);
            else DrawRing(pt, bracketRadius, 2f, RipcordMarkerColour, ba);
        }
        if (c.Threat)
        {
            if (_artThreat is { } art) DrawExpandedBlip(art, pt, colour, ba, bracketRadius);
            else DrawThreatTicks(pt, ThreatMarkerColour, ba, bracketRadius);
        }
        // P805 (rows 428/437) — the Highlight mask draws NOTHING: native
        // c_maskHighlight has no surface (radarimage.cpp loads no highlight
        // bitmap; DrawBlip's bracket pass handles only enemy/me/subject/flag/
        // artifact/rip/targeted/last-fired). Its native effect is the FULL
        // brightness already applied via BracketBrightness above and the label
        // gate (radarimage.cpp:707). The former procedural ring was a port
        // invention; the P495 build-spot pulse remains the separate asteroid
        // feature it always was.
    }

    /// <summary>HUD-12 — draw a native radar bracket surface exactly the way
    /// <c>DrawExpandedBlip</c> does (<c>radarimage.cpp:255-277</c>): the bitmap's
    /// four quadrants are exploded outward around the contact by
    /// <see cref="ExpandedBlipDelta"/>, tinted with the native colour. HUD-13 —
    /// <paramref name="bracketRadius"/> is the contact's native
    /// <c>radiusBracket</c> (<see cref="BracketRadius"/>), derived from
    /// <c>pts-&gt;GetScreenRadius()</c> and clipped by
    /// <c>pts-&gt;GetDistanceToEdge()</c> (radarimage.cpp:307-319).</summary>
    private void DrawExpandedBlip(Texture2D art, Vector2 centre, Color colour, float alpha, float bracketRadius)
    {
        float delta = ExpandedBlipDelta(art.Width, art.Height, bracketRadius);
        var tint = colour * Math.Clamp(alpha, 0f, 1f);
        for (int q = 0; q < 4; q++)
        {
            var src = ExpandedBlipQuadrant(q, art.Width, art.Height);
            var offset = q switch
            {
                0 => new Vector2(-delta, -delta),
                1 => new Vector2(delta, -delta),
                2 => new Vector2(delta, delta),
                _ => new Vector2(-delta, delta),
            };
            _sb.Draw(art, centre + offset, src, tint);
        }
    }

    private void DrawOpenCorners(Vector2 c, float half, float thickness, Color colour, float a)
    {
        var col = WithAlpha(colour, a);
        float arm = half * 0.45f;
        foreach (int sx in new[] { -1, 1 })
            foreach (int sy in new[] { -1, 1 })
            {
                var corner = c + new Vector2(sx * half, sy * half);
                DrawLine(corner, corner - new Vector2(sx * arm, 0), thickness, col);
                DrawLine(corner, corner - new Vector2(0, sy * arm), thickness, col);
            }
    }

    private void DrawPennant(Vector2 c, float radius, Color colour, float a)
    {
        var top = c + new Vector2(0f, -radius);
        var right = c + new Vector2(radius * .8f, -radius * .2f);
        var left = c + new Vector2(-radius * .8f, -radius * .2f);
        DrawLine(top, right, 2.2f, colour);
        DrawLine(right, left, 2.2f, colour);
        DrawLine(left, top, 2.2f, colour);
    }

    private void DrawThreatTicks(Vector2 c, Color colour, float a, float radius)
    {
        var col = WithAlpha(colour, a);
        float inner = radius * 0.7f;
        for (int i = 0; i < 4; i++)
        {
            float angle = i * MathHelper.TwoPi / 4 + MathHelper.PiOver4;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            DrawLine(c + dir * inner, c + dir * radius, 2.4f, col);
        }
    }


    /// <summary>
    /// P495 — the distinct marker for a build-spot asteroid of the hovered
    /// station-type investment (native <c>SetInvestAsteroid</c>, loadout.cpp:1396-1409):
    /// an amber pulsing double-ring plus a centred dot — the "build here"
    /// cue, deliberately shaped differently from the white target "lock"
    /// (rings + diamond) so the two can never be confused. Alpha floored so a
    /// distant/edge-clamped rock stays visible, exactly like the P436 target
    /// highlight.
    /// </summary>
    private void DrawBuildSpotHighlight(Vector2 c, float baseOpacity, float pulse01)
    {
        float ha = Math.Max(baseOpacity, 0.9f);
        float p = Math.Clamp(pulse01, 0f, 1f);
        DrawRing(c, 15f + 4f * p, 2.6f, BuildSpotHighlightColour, ha);
        DrawRing(c, 9f, 1.6f, BuildSpotHighlightColour, ha);
        DrawFilledDot(c, 2.6f, BuildSpotHighlightColour, ha);
    }

    /// <summary>Draw a six-sided "rock" outline around <paramref name="c"/> —
    /// the radar icon for an asteroid build-spot contact.</summary>
    private void DrawHexagon(Vector2 c, float radius, Color colour, float a)
    {
        const int sides = 6;
        var col = WithAlpha(colour, a);
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * MathHelper.TwoPi / sides;
            float a1 = (i + 1) * MathHelper.TwoPi / sides;
            DrawLine(c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius,
                     c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius, 2.2f, col);
        }
    }

    /// <summary>
    /// P436 — the distinct marker for the player's OWN selected target
    /// (native's dedicated target detail tier, <c>c_rlTarget</c>): a large
    /// bright-white pulsing ring plus a diamond "lock" frame around the base
    /// icon. Its alpha is FLOORED well above the distance fade so a distant or
    /// edge-clamped target stays unmistakable — a far / off-screen target is
    /// precisely when the pilot needs the cue most (the reported problem).
    /// </summary>
    private void DrawTargetHighlight(Vector2 c, float baseOpacity, float pulse01)
    {
        // The highlight must read even when the distance fade has dimmed the
        // base contact (a far / edge-clamped target is exactly the case where
        // you NEED it) — floor the alpha so it never vanishes.
        float ha = Math.Max(baseOpacity, 0.9f);
        float p = Math.Clamp(pulse01, 0f, 1f);
        DrawRing(c, 16f + 4f * p, 2.6f, TargetHighlightColour, ha);
        DrawRing(c, 9.5f, 1.4f, TargetHighlightColour, ha);
        DrawDiamond(c, 13f, TargetHighlightColour, ha);
    }

    /// <summary>Draw a rotated-square "lock" frame (diamond) around
    /// <paramref name="c"/> — a crisp target-reticle shape no contact type's
    /// round/square icon mimics.</summary>
    private void DrawDiamond(Vector2 c, float halfDiag, Color colour, float a)
    {
        var col = WithAlpha(colour, a);
        var top = new Vector2(c.X, c.Y - halfDiag);
        var right = new Vector2(c.X + halfDiag, c.Y);
        var bottom = new Vector2(c.X, c.Y + halfDiag);
        var left = new Vector2(c.X - halfDiag, c.Y);
        DrawLine(top, right, 2.2f, col);
        DrawLine(right, bottom, 2.2f, col);
        DrawLine(bottom, left, 2.2f, col);
        DrawLine(left, top, 2.2f, col);
    }

    /// <summary>HUD-4 — a hollow axis-aligned square outline (treasure crate /
    /// own-ship marker).</summary>
    private void DrawHollowSquare(Vector2 c, float half, Color colour, float a)
    {
        var col = WithAlpha(colour, a);
        DrawLine(c + new Vector2(-half, -half), c + new Vector2(half, -half), 2f, col);
        DrawLine(c + new Vector2(half, -half), c + new Vector2(half, half), 2f, col);
        DrawLine(c + new Vector2(half, half), c + new Vector2(-half, half), 2f, col);
        DrawLine(c + new Vector2(-half, half), c + new Vector2(-half, -half), 2f, col);
    }

    /// <summary>HUD-4 — a triangle outline (tech icon).</summary>
    private void DrawTriangle(Vector2 c, float radius, Color colour, float a)
    {
        var col = WithAlpha(colour, a);
        var p0 = c + new Vector2(0f, -radius);
        var p1 = c + new Vector2(radius, radius * 0.75f);
        var p2 = c + new Vector2(-radius, radius * 0.75f);
        DrawLine(p0, p1, 2f, col);
        DrawLine(p1, p2, 2f, col);
        DrawLine(p2, p0, 2f, col);
    }

    /// <summary>HUD-4 — four short cross ticks around a mine dot.</summary>
    private void DrawMineCross(Vector2 c, float radius, Color colour, float a)
    {
        var col = WithAlpha(colour, a);
        DrawLine(c + new Vector2(-radius, 0f), c + new Vector2(-radius * .35f, 0f), 2f, col);
        DrawLine(c + new Vector2(radius * .35f, 0f), c + new Vector2(radius, 0f), 2f, col);
        DrawLine(c + new Vector2(0f, -radius), c + new Vector2(0f, -radius * .35f), 2f, col);
        DrawLine(c + new Vector2(0f, radius * .35f), c + new Vector2(0f, radius), 2f, col);
    }

    private Color WithAlpha(Color c, float a) =>
        new(
            (byte)(c.R * a),
            (byte)(c.G * a),
            (byte)(c.B * a),
            (byte)(255f * a));

    private void DrawFilledSquare(Vector2 c, float size, Color colour, float a)
    {
        var col = WithAlpha(colour, a);
        _sb.Draw(_white, new Rectangle((int)(c.X - size / 2f), (int)(c.Y - size / 2f),
            (int)size, (int)size), col);
    }

    private void DrawFilledDot(Vector2 c, float radius, Color colour, float a)
    {
        const int segs = 20;
        var col = WithAlpha(colour, a);
        for (int i = 0; i < segs; i++)
        {
            float a0 = i * MathHelper.TwoPi / segs;
            float a1 = (i + 1) * MathHelper.TwoPi / segs;
            DrawTri(c, c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius,
                c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius, col);
        }
    }

    private void DrawRing(Vector2 c, float radius, float thickness, Color colour, float a)
    {
        const int segments = 32;
        var col = WithAlpha(colour, a);
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathHelper.TwoPi / segments;
            float a1 = (i + 1) * MathHelper.TwoPi / segments;
            var p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
            var p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            DrawLine(p0, p1, thickness, col);
        }
    }

    private void DrawTri(Vector2 a, Vector2 b, Vector2 cc, Color colour)
    {
        // Two rotated stretched rects to approximate a filled triangle fan cell
        // (a crude but adequate screen-space disc for the station dot).
        DrawLine(a, b, 2.2f, colour);
        DrawLine(b, cc, 2.2f, colour);
        DrawLine(cc, a, 2.2f, colour);
    }

    private void DrawLine(Vector2 a, Vector2 b, float thickness, Color colour)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 0.001f)
            return;
        float angle = MathF.Atan2(d.Y, d.X);
        _sb.Draw(_white, a + d * 0.5f, null, colour, angle,
            new Vector2(0.5f, 0.5f), new Vector2(len, thickness), SpriteEffects.None, 0f);
    }
}
