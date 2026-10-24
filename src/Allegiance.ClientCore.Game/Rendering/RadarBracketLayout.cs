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
    /// Native <c>c_flBorderSide</c> (<c>WinTrek/radarimage.h:20</c>): the inset
    /// the radar image rect is shrunk by before the eye-space clamp, and the
    /// constant in the native round-radar radius
    /// <c>(YMax − YMin) · 0.48 = (height − 2·10) · 0.48</c>
    /// (<c>WinTrek.cpp:7726</c>, <c>radarimage.cpp:167-169</c>).
    /// </summary>
    public const float NativeBorderSide = 10f;

    /// <summary>Native's round-radar radius: <c>((YMax − YMin) · 0.48f)</c> on
    /// the border-expanded image rect (<c>WinTrek.cpp:7726</c>,
    /// <c>radarimage.cpp:173</c>). With <see cref="NativeBorderSide"/> this is
    /// <c>0.48 · (height − 20)</c>.</summary>
    public static float RoundRadarRadius(float height, float inset = NativeBorderSide)
        => MathF.Max(0f, (height - 2f * inset) * 0.48f);

    /// <summary>
    /// Decide an object's radar placement + viewport pixel, faithfully to
    /// native's eye-space edge clamp (<c>WinTrek.cpp:7882-7960</c>).
    /// </summary>
    /// <param name="inFront">True when the object projects in front of the
    /// camera (<c>clip.w &gt; 0</c>), so <paramref name="ndcX"/>/<paramref name="ndcY"/>
    /// are meaningful.</param>
    /// <param name="ndcX"/><param name="ndcY">The object's normalized device
    /// coordinates (-1..1) from projecting through <c>view*proj</c>. Only valid
    /// when <paramref name="inFront"/> is true.</param>
    /// <param name="eyeX"/><param name="eyeY">The object's CAMERA-SPACE
    /// right/up components (<c>m_pcamera-&gt;TransformLocalToEye(positionModel)</c>,
    /// <c>WinTrek.cpp:7882</c>) — the values native clamps directly, with y UP.
    /// Valid even behind the eye.</param>
    /// <param name="width"/><param name="height">Viewport size in pixels.</param>
    /// <param name="inset">Inset from the viewport edge (px) at which off-screen
    /// markers are clamped — native <c>c_flBorderSide</c> = 10.</param>
    /// <param name="roundRadar">Native <c>m_bRoundRadar</c>
    /// (<c>WinTrek.cpp:7849-7875,7883-7906</c>, <c>radarimage.cpp:1176</c>): the
    /// blip is on screen only inside the inscribed circle and an off-screen blip
    /// is pinned to that circle, not the rectangle.</param>
    public static RadarSite Resolve(
        bool inFront, float ndcX, float ndcY,
        float eyeX, float eyeY,
        float width, float height, float inset, bool roundRadar = false)
    {
        float halfW = MathF.Max(width * 0.5f - inset, 1f);
        float halfH = MathF.Max(height * 0.5f - inset, 1f);
        float roundRadius = RoundRadarRadius(height, inset);

        // WinTrek.cpp:7836-7875 — native projects first, then decides on-screen
        // state. The square radar uses the image rect; the round radar compares
        // the CENTRED image point against the inscribed circle.
        bool inRect = inFront && MathF.Abs(ndcX) <= 1f && MathF.Abs(ndcY) <= 1f;
        bool onScreen = inRect;
        if (inRect && roundRadar)
        {
            // rectImage was offset to the centre, so the native image point is
            // (x·0.5·width, y·0.5·height) in y-up pixels.
            float cx = ndcX * 0.5f * width;
            float cy = ndcY * 0.5f * height;
            onScreen = cx * cx + cy * cy < roundRadius * roundRadius;
        }
        if (onScreen)
        {
            float px = (ndcX * 0.5f + 0.5f) * width;
            float py = (0.5f - ndcY * 0.5f) * height;
            return new RadarSite(RadarPlacement.OnScreen, new Vector2(px, py));
        }

        // Off-screen. WinTrek.cpp:7883-7906 — ROUND radar: scale the eye-space
        // (x,y) so it lands on the circle. (Native scales by fScreenSize / |xy|;
        // a zero lateral direction pins straight down, the same degenerate
        // fallback native's directionCentre uses.)
        if (roundRadar)
        {
            float len = MathF.Sqrt(eyeX * eyeX + eyeY * eyeY);
            float dx, dy;
            if (len < 1e-6f) { dx = 0f; dy = -roundRadius; }
            else { float f = roundRadius / len; dx = eyeX * f; dy = eyeY * f; }
            return new RadarSite(RadarPlacement.EdgeClamped,
                new Vector2(width * 0.5f + dx, height * 0.5f - dy));
        }

        // WinTrek.cpp:7908-7921 — SQUARE radar: clamp the eye-space (x,y) to the
        // rect by the SMALLER ray parameter, so the marker lands exactly on the
        // edge the ray crosses (native's tX/tY/tMin). Not an NDC normalisation:
        // the lateral components are used raw.
        float x = eyeX, y = eyeY;
        if (x == 0f)
            y = y <= 0f ? -halfH : halfH;                 // :7910-7912
        else if (y == 0f)
            x = x <= 0f ? -halfW : halfW;                 // :7913-7915
        else
        {
            float tX = (x > 0f ? halfW : -halfW) / x;     // :7917-7918
            float tY = (y > 0f ? halfH : -halfH) / y;
            float tMin = MathF.Min(tX, tY);               // :7919-7921
            x *= tMin;
            y *= tMin;
        }
        return new RadarSite(RadarPlacement.EdgeClamped,
            new Vector2(width * 0.5f + x, height * 0.5f - y));   // image y-up → screen y-down
    }
}
