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
    }

    /// <summary>One placed contact: type, resolved viewport pixel (on-screen
    /// exact, or edge-clamped), whether it is on-screen, and fade opacity.
    /// <paramref name="SideColour"/> is the contact's REAL per-side colour
    /// (P372 — resolved from the side roster via
    /// <c>ClientWorld.TryGetSideColor</c>, native <c>pside-&gt;GetColor()</c>,
    /// radarimage.cpp:682-685); when null the renderer falls back to the
    /// type-based palette colour. Ships always supply their real side colour;
    /// stations/warps may leave it null and keep their type-based hue.</summary>
    public readonly record struct RadarContact(
        RadarContactType Type,
        NumericsVec2 ScreenPoint,
        bool OnScreen,
        float Opacity,
        Color? SideColour = null);

    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;

    // Icon palette — the teal HUD accent family for ships (the familiar "other
    // craft" colour), warm gold for stations (infrastructure), violet for warps
    // (the swirl portals). Flat single detail level, no tiering. Public so the
    // type-based fallback mapping (ContactColour) is unit-testable.
    public static readonly Color ShipColour = new(0.28f, 0.85f, 0.85f);
    public static readonly Color StationColour = new(1.00f, 0.74f, 0.26f);
    public static readonly Color WarpColour = new(0.86f, 0.44f, 0.97f);

    public RadarBracketRenderer(SpriteBatch spriteBatch, Texture2D whitePixel)
    {
        _sb = spriteBatch;
        _white = whitePixel;
    }

    /// <summary>Draw every contact's icon (or edge-bracket) for this frame.</summary>
    public void Draw(IReadOnlyList<RadarContact> contacts, Viewport viewport)
    {
        if (contacts.Count == 0)
            return;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            foreach (var c in contacts)
                DrawContact(c);
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
            _ => WarpColour,
        };
    }

    private void DrawContact(RadarContact c)
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
        }
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
