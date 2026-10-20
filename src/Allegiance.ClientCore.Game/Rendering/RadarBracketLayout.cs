using System.Numerics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P250 — the on-screen placement math for the local current-sector tactical
/// radar. Given a nearby object's projected position this decides whether it
/// lands <b>on-screen</b> (draw the icon at its real projected pixel) or
/// <b>off-screen</b> (clamp the icon to the viewport edge in the object's real
/// direction).
///
/// This is the port of the core mechanic of native's <c>RadarImage</c>
/// (<c>WinTrek/radarimage.cpp</c>, 1482+72 lines) — an on-screen 3D-viewport
/// bracket / edge-indicator system, <i>not</i> a classic top-down radar disc.
/// Native's decisive part is <c>DrawBlip</c> (radarimage.cpp:286-427): every
/// nearby object in the player's sector gets an icon at its real projected
/// position when on screen, or pinned to the viewport edge in the object's
/// real direction when off screen (the <c>radiusBracket</c>/<c>distanceToEdge</c>
/// machinery, radarimage.cpp:314-319, and the eye-space edge clamp in
/// WinTrek.cpp:7893-7923).
///
/// Deliberately PURE (System.Numerics only, no MonoGame on the surface — the
/// established pure-logic discipline of this client, e.g. P240's
/// <see cref="SmoothedOrientation"/> and P249's <see cref="StarmapLayout"/>),
/// so the on-screen/clamp contract is headless-unit-testable. The
/// caller (<see cref="RadarProjection"/> / <see cref="Game1"/>) supplies the
/// already-projected inputs (the real NDC when the object is in front of the
/// camera, plus the object's camera-space direction, which stays valid even
/// behind the eye) and receives the final viewport pixel + placement kind.
/// </summary>
public static class RadarBracketLayout
{
    /// <summary>On-screen icon vs edge-clamped locator.</summary>
    public enum RadarPlacement
    {
        /// <summary>Object projects inside the viewport — draw at the exact pixel.</summary>
        OnScreen,
        /// <summary>Object projects outside the viewport (or behind the camera) —
        /// clamp to the viewport edge in its real direction.</summary>
        EdgeClamped,
    }

    /// <summary>A placed radar marker: which mode and where in viewport pixels.</summary>
    public readonly record struct RadarSite(RadarPlacement Placement, Vector2 Point);

    /// <summary>
    /// HUD-4 — the fixed opacity every radar contact is drawn at. Native's
    /// distance fade block is commented out (radarimage.cpp:689-700), so
    /// contacts are full opacity at every range; the port pins that with this
    /// constant so the parity row stays complete.
    /// </summary>
    public const float NativeContactOpacity = 1f;

    /// <summary>
    /// Decide an object's radar placement + viewport pixel.
    /// </summary>
    /// <param name="inFront">True when the object projects in front of the
    /// camera (<c>clip.w &gt; 0</c>), so <paramref name="ndcX"/>/<paramref name="ndcY"/>
    /// are meaningful.</param>
    /// <param name="ndcX"/><param name="ndcY">The object's normalized device
    /// coordinates (-1..1) from projecting through <c>view*proj</c>. Only valid
    /// when <paramref name="inFront"/> is true.</param>
    /// <param name="dirX"/><param name="dirY">The object's NDC <em>direction</em> from
    /// the screen centre, computed in camera space (right/up components at a
    /// forced in-front depth) so the lateral signs stay correct even when the
    /// object is behind the eye — the same eye-space direction native clamps in
    /// WinTrek.cpp:7882-7923.</param>
    /// <param name="width"/><param name="height">Viewport size in pixels.</param>
    /// <param name="inset">Inset from the viewport edge (px) at which off-screen
    /// brackets are clamped, so a centred icon stays fully on screen. Equivalent
    /// to native's edge "gutter" (radarimage.cpp:318-319 clips by the border).</param>
    public static RadarSite Resolve(
        bool inFront, float ndcX, float ndcY,
        float dirX, float dirY,
        float width, float height, float inset)
    {
        // On-screen: the object's real projected NDC lands inside the viewport —
        // place the icon at its exact projected pixel (native's
        // c_ucRadarOnScreenLarge path, WinTrek.cpp:7868).
        if (inFront && MathF.Abs(ndcX) <= 1f && MathF.Abs(ndcY) <= 1f)
        {
            float px = (ndcX * 0.5f + 0.5f) * width;
            float py = (0.5f - ndcY * 0.5f) * height;
            return new RadarSite(RadarPlacement.OnScreen, new Vector2(px, py));
        }

        // Off-screen (out of the frustum OR behind the camera): clamp to the
        // viewport edge in the object's real screen direction. The direction is
        // normalised so the dominant axis lands on the inset rect boundary —
        // the classic square edge-bracket (native clamps the eye-space x/y to
        // the image rect the same way, WinTrek.cpp:7893-7923).
        float sx = dirX, sy = dirY;
        float s = MathF.Max(MathF.Abs(sx), MathF.Abs(sy));
        if (s < 1e-6f) { sx = 0f; sy = -1f; s = 1f; }   // dead-centre degenerate: pin bottom
        float nx = sx / s;   // now in [-1,1]
        float ny = sy / s;
        float spanW = MathF.Max(width - 2f * inset, 1f);
        float spanH = MathF.Max(height - 2f * inset, 1f);
        float ex = (nx * 0.5f + 0.5f) * spanW + inset;
        float ey = (0.5f - ny * 0.5f) * spanH + inset;
        return new RadarSite(RadarPlacement.EdgeClamped, new Vector2(ex, ey));
    }
}
