namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-13 — the two per-contact inputs native's radar bracket sizing reads from
/// the ThingSite each frame: <c>GetScreenRadius()</c> and
/// <c>GetDistanceToEdge()</c> (<c>WinTrek/radarimage.cpp:307-319</c>).
///
/// <para><b>Screen radius.</b> Native projects the object's world radius at its
/// camera distance (<c>WinTrek/WinTrek.cpp:7928-7941</c>):
/// <c>sinAlpha = radius / distance; fScreenRadius = angleToPixels * asin(sinAlpha)</c>
/// with <c>angleToPixels = rectImage.Width / FOV</c>
/// (<c>WinTrek.cpp:7712</c>) — i.e. an angular radius in pixels — then floors it
/// at <c>CmdViewMaxIconScreenSize</c> = 7 px (<c>WinTrek/cmdview.h:56</c>) and
/// <c>DrawBlip</c> caps it at <c>sqrt2 * 80</c>
/// (<c>radarimage.cpp:307-312</c>). The port's px-per-world factor
/// <c>viewportHeight/2 * |proj.M22| / distance</c> is the same angular scale
/// (for a standard perspective matrix <c>height/(2·tan(fovy/2))</c> equals
/// <c>width/fovx</c> at small angles), and is exactly the projection the
/// selected-target / missile-lock bracket already uses (<c>Game1.ProjectedRadius</c>,
/// <c>src/Allegiance.ClientCore.Game/Game1.Camera.cs:215-224</c>, which now
/// delegates here). Reusing it keeps every bracket on the same projected object
/// radius, per the HUD-13 brief.
///
/// <para><b>Distance to edge.</b> Native measures the placed blip's distance to
/// the nearest edge of the radar image rect (<c>WinTrek.cpp:7944-7956</c>); for
/// an off-screen (edge-pinned) blip it is forced to 0 and the icon size to
/// <c>CmdViewMaxIconScreenSize</c> (<c>WinTrek.cpp:7876-7878</c>). The port
/// gives an edge-clamped contact the 7 px icon radius but keeps the real
/// distance from its clamped point to the viewport edge instead of 0. The
/// result is the same: 1.05 * 7 is under <c>c_flBorderSide</c> (10), so
/// <see cref="RadarBracketRenderer.BracketRadius"/> never clips it
/// (<c>radarimage.cpp:314-319</c>).</para>
///
/// Deliberately pure (no MonoGame, no graphics device) so the rule is
/// headless-unit-testable, matching <see cref="RadarBracketLayout"/>.
/// </summary>
public static class RadarScreenRadius
{
    /// <summary>Native <c>maxRadius = sqrt2 * 80.0f</c> — the cap
    /// <c>DrawBlip</c> applies to <c>pts-&gt;GetScreenRadius()</c> before scaling
    /// (<c>WinTrek/radarimage.cpp:307-312</c>).</summary>
    public const float MaxObjectScreenRadius = 1.41421356f * 80f;

    /// <summary>Native <c>CmdViewMaxIconScreenSize</c> = 7.0f
    /// (<c>WinTrek/cmdview.h:56</c>) — the floor <c>GetScreenRadius()</c> always
    /// reports (<c>WinTrek/WinTrek.cpp:7931-7941</c>) and the size every
    /// off-screen blip is given (<c>WinTrek.cpp:7876-7878</c>).</summary>
    public const float MinIconScreenRadius = 7f;

    /// <summary>One contact's native bracket-sizing inputs.</summary>
    public readonly record struct ContactBracket(float ScreenRadius, float DistanceToEdge);

    /// <summary>
    /// Project a world-space radius to screen pixels at the object's camera
    /// distance, then apply native's floor (<see cref="MinIconScreenRadius"/>)
    /// and cap (<see cref="MaxObjectScreenRadius"/>). Pure.
    /// </summary>
    /// <param name="worldRadius">The object's world radius (native
    /// <c>ImodelIGC::GetRadius()</c>).</param>
    /// <param name="distanceToEye">Distance from the camera eye to the object
    /// centre, world units.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    /// <param name="projectionM22">The projection matrix's <c>M22</c> (vertical
    /// scale, <c>1/tan(fovy/2)</c>).</param>
    public static float Project(float worldRadius, float distanceToEye, float viewportHeight, float projectionM22)
    {
        if (worldRadius <= 0f || distanceToEye <= 0.001f || MathF.Abs(projectionM22) < 1e-6f)
            return MinIconScreenRadius;
        float pxPerWorld = viewportHeight * 0.5f * MathF.Abs(projectionM22) / distanceToEye;
        float radiusPx = worldRadius * pxPerWorld;
        if (radiusPx > MaxObjectScreenRadius) return MaxObjectScreenRadius;
        return radiusPx < MinIconScreenRadius ? MinIconScreenRadius : radiusPx;
    }

    /// <summary>
    /// A placed blip's distance to the nearest viewport edge, px — native's
    /// <c>distanceToEdge</c> (<c>WinTrek/WinTrek.cpp:7944-7956</c>). Never
    /// negative. Pure.
    /// </summary>
    public static float DistanceToEdge(float screenX, float screenY, float viewportWidth, float viewportHeight)
    {
        float d = MathF.Min(screenX, screenY);
        d = MathF.Min(d, viewportWidth - screenX);
        d = MathF.Min(d, viewportHeight - screenY);
        return d > 0f ? d : 0f;
    }

    /// <summary>
    /// Fill one contact's native sizing inputs from its radar placement: the
    /// projected radius for an on-screen blip, or native's fixed icon radius for
    /// an edge-pinned one (<c>WinTrek.cpp:7876-7878</c>), plus the distance from
    /// the placed point to the nearest viewport edge. Pure and allocation-free.
    /// </summary>
    public static ContactBracket ForContact(
        bool onScreen, float worldRadius, float distanceToEye,
        float viewportWidth, float viewportHeight, float projectionM22,
        float screenX, float screenY)
    {
        float radius = onScreen
            ? Project(worldRadius, distanceToEye, viewportHeight, projectionM22)
            : MinIconScreenRadius;
        return new ContactBracket(
            radius, DistanceToEdge(screenX, screenY, viewportWidth, viewportHeight));
    }
}
