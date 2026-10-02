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
/// told apart by a simple shape + colour. Distance-based alpha fade is applied
/// per contact (see <see cref="RadarBracketLayout.Opacity"/>).
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
        /// <summary>P495 — an asteroid contact, added to the radar ONLY while a
        /// station-type investment is hovered, so the matching build-spot rocks
        /// light up (native <c>SetInvestAsteroid</c>, loadout.cpp:1396-1409).
        /// Otherwise asteroids have no radar blip (P436).</summary>
        Asteroid,
    }

    /// <summary>One placed contact: type, resolved viewport pixel (on-screen
    /// exact, or edge-clamped), whether it is on-screen, and fade opacity.
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
        bool IsInvestBuildSpot = false);

    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;

    // Icon palette — the teal HUD accent family for ships (the familiar "other
    // craft" colour), warm gold for stations (infrastructure), violet for warps
    // (the swirl portals). Flat single detail level, no tiering. Public so the
    // type-based fallback mapping (ContactColour) is unit-testable.
    public static readonly Color ShipColour = new(0.28f, 0.85f, 0.85f);
    public static readonly Color StationColour = new(1.00f, 0.74f, 0.26f);
    public static readonly Color WarpColour = new(0.86f, 0.44f, 0.97f);
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

    public RadarBracketRenderer(SpriteBatch spriteBatch, Texture2D whitePixel)
    {
        _sb = spriteBatch;
        _white = whitePixel;
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
                DrawContact(c, highlightPulse);
        }
        finally
        {
            _sb.End();
        }
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
            _ => WarpColour,
        };
    }

    private void DrawContact(RadarContact c, float highlightPulse)
    {
        var pt = new Vector2(c.ScreenPoint.X, c.ScreenPoint.Y);
        float a = Math.Clamp(c.Opacity, 0f, 1f);
        var colour = ContactColour(c.Type, c.SideColour);
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
        }
        // P436 — the player's own selected target: draw the bright highlight
        // AFTER the base icon so it dominates, whether the contact is on-screen
        // or edge-clamped off-screen (the edge case is the exact reported gap).
        if (c.IsSelectedTarget)
            DrawTargetHighlight(pt, a, highlightPulse);
        // P495 — a build-spot asteroid of the hovered station-type investment:
        // amber pulsing marker (native SetInvestAsteroid highlight), drawn on
        // top so the matched rocks read clearly against the scene.
        if (c.IsInvestBuildSpot)
            DrawBuildSpotHighlight(pt, a, highlightPulse);
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
        var top = new Vector2(c.X, c.Y - halfDiag);
        var right = new Vector2(c.X + halfDiag, c.Y);
        var bottom = new Vector2(c.X, c.Y + halfDiag);
        var left = new Vector2(c.X - halfDiag, c.Y);
        DrawLine(top, right, 2.2f, colour);
        DrawLine(right, bottom, 2.2f, colour);
        DrawLine(bottom, left, 2.2f, colour);
        DrawLine(left, top, 2.2f, colour);
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
