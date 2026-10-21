using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-16 — where native draws a radar contact's model icon relative to its
/// blip. <c>RadarImageImpl::DrawBlip</c> (<c>WinTrek/radarimage.cpp:320-427</c>)
/// first draws the overlay brackets around the object, then computes
/// <c>directionCenter</c> and <c>positionIcon</c> and draws the icon at
/// <c>positionObject + positionIcon</c> with <c>BlendModeAdd</c> in
/// <c>colorIcon</c> (<c>radarimage.cpp:417-420</c>).
///
/// <para>
/// <b>The rule, as written</b> (<c>radarimage.cpp:382-409</c>):
/// <code>
/// directionCenter.X = positionObject.X;
/// directionCenter.Y = roundRadar ? positionObject.Y
///                                : positionObject.Y - rectImage.Size().Y() / 5.0f;
/// fLengthSquared = X*X + Y*Y;
/// if (fLengthSquared &lt; 1.0f) directionCenter = (0, -1);
/// else directionCenter *= -1 / sqrt(fLengthSquared);      // unit vector TOWARD the centre
///
/// if (ucRadarState != c_ucRadarOnScreenLarge)
///     positionIcon = (0, 0);
/// else
///     positionIcon = directionCenter * min(distanceToEdge, radiusBracket);
/// </code>
/// So an <b>on-screen</b> blip (<c>c_ucRadarOnScreenLarge</c>) has its icon pulled
/// inward from the object by up to its bracket radius — native's comment: "the
/// radius bracket is big, and the icon should appear near the object instead of
/// off it … to avoid obscuring ships". Every other radar state
/// (<c>c_ucRadarOnScreenSmall</c>, <c>c_ucRadarOffScreen</c>) draws the icon
/// exactly at the blip.
/// </para>
/// <para>
/// The port models <see cref="RadarBracketLayout.RadarPlacement.OnScreen"/> (the
/// native large on-screen case) and <see cref="RadarBracketLayout.RadarPlacement.EdgeClamped"/>
/// (native off-screen). Native's <c>c_ucRadarOnScreenSmall</c> (an object under
/// <c>AnyViewMinRenderScreenSize</c>, only reachable in the command camera,
/// <c>WinTrek.cpp:7928-7938</c>) is not modelled by the port's radar gather;
/// treating every port on-screen contact as large matches the fact that the
/// port's projected radii are floored at <see cref="RadarScreenRadius.MinIconScreenRadius"/>.
/// </para>
/// <para>Pure (System/MonoGame value types only, no allocation) so the placement
/// rule is headless-unit-testable, like <see cref="RadarBracketLayout"/> and
/// <see cref="RadarScreenRadius"/>.</para>
/// </summary>
public static class RadarIconPlacement
{
    /// <summary>Native <c>rectImage.Size().Y() / 5.0f</c>
    /// (<c>radarimage.cpp:386-388</c>) — the <c>directionCenter</c> reference
    /// point sits this fraction of the image height below the image centre.</summary>
    public const float DirectionCentreHeightFraction = 0.2f;

    /// <summary>
    /// The icon offset from the contact's blip, in viewport pixels
    /// (<c>positionIcon</c>, <c>radarimage.cpp:382-409</c>).
    /// </summary>
    /// <param name="onScreenLarge">Native radar state is
    /// <c>c_ucRadarOnScreenLarge</c> (the port's
    /// <see cref="RadarBracketLayout.RadarPlacement.OnScreen"/>).</param>
    /// <param name="screenX"/><param name="screenY">The blip's viewport pixel
    /// (native <c>positionObject</c>, relative to the image centre; the method
    /// converts from viewport coordinates).</param>
    /// <param name="viewportWidth"/><param name="viewportHeight">The radar image
    /// rect size in pixels.</param>
    /// <param name="distanceToEdge">Native <c>pts-&gt;GetDistanceToEdge()</c> for
    /// this blip (<see cref="RadarScreenRadius.DistanceToEdge"/>).</param>
    /// <param name="radiusBracket">Native <c>radiusBracket</c>
    /// (<see cref="RadarBracketRenderer.BracketRadius"/>).</param>
    public static Vector2 Offset(
        bool onScreenLarge,
        float screenX, float screenY,
        float viewportWidth, float viewportHeight,
        float distanceToEdge, float radiusBracket, bool roundRadar = false)
    {
        // Every state other than c_ucRadarOnScreenLarge draws at the blip.
        if (!onScreenLarge) return Vector2.Zero;

        // rectImage is translated so its centre is the origin (radarimage.cpp:1131-
        // 1139), so positionObject is centre-relative. The reference point is
        // (0, height/5) in native's y-up image space (camera.cpp:174 maps eye Y
        // straight to image Y), i.e. ABOVE the centre; in round-radar mode it is
        // the centre itself (radarimage.cpp:363-365). Screen y points down, so the
        // y-up difference positionObject.Y - H/5 becomes y + H/5 here.
        float x = screenX - viewportWidth * 0.5f;
        float y = screenY - viewportHeight * 0.5f;
        float dy = roundRadar ? y : y + viewportHeight * DirectionCentreHeightFraction;

        float lengthSquared = x * x + dy * dy;
        float dirX, dirY;
        if (lengthSquared < 1f)
        {
            // Dead-centre degenerate: native pins (0,-1) in y-up space, i.e.
            // straight down on screen (radarimage.cpp:369-372).
            dirX = 0f;
            dirY = 1f;
        }
        else
        {
            // f = -1/sqrt(len²) → the unit vector pointing AWAY from the object,
            // back toward the reference point.
            float f = -1f / MathF.Sqrt(lengthSquared);
            dirX = x * f;
            dirY = dy * f;
        }

        // f = distanceToEdge < radiusBracket ? distanceToEdge : radiusBracket.
        float distance = distanceToEdge < radiusBracket ? distanceToEdge : radiusBracket;
        if (distance < 0f) distance = 0f;
        return new Vector2(dirX * distance, dirY * distance);
    }
}
