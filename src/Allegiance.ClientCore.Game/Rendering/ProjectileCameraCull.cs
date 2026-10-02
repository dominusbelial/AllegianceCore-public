namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P239 — decides whether to <em>skip drawing</em> a projectile bolt because the
/// active camera eye sits at (or within a hair of) the bolt's own solid geometry.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Weapon fire has no muzzle offset (an accepted
/// scope cut — <c>Weapon.cs:230</c> spawns at the ship's centre, mirroring
/// native's no-hull-mesh fallback, <c>weaponIGC.cpp:203</c>), so every projectile
/// originates at the ship's centre. In cockpit view the camera eye sits 14 units
/// forward of that same centre (<c>CockpitCamera._forwardOffset = 14f</c>, P211),
/// and each projectile renders as a synthetic solid box <c>32–480</c> units long
/// centred on the spawn (<c>Game1</c>'s <c>MinBoltLength</c>/<c>MaxBoltLength</c>/
/// <c>BoltLengthPerUnitSeconds</c>) with culling disabled
/// (<c>ProjectileRenderer</c>'s <c>CullMode.None</c>). For the first moments after
/// a shot spawns, that box's enormous longitudinal extent therefore <b>contains
/// the cockpit eye</b>, and the camera ends up briefly <em>inside</em> a solid
/// tinted box — a persistent full-screen colour flood while holding fire. This is
/// the rendering-only analogue of a first-person shooter culling geometry that
/// intersects the camera.</para>
/// <para><b>The check.</b> Model the bolt as an elongated box centred at its
/// spawn, long axis along the (unit) travel direction <c>dir</c>, half-length
/// <c>len/2</c>, square cross-section of side <c>width</c>. Transform the eye into
/// the bolt's local frame: <c>u = (eye−pos)·dir</c> is the longitudinal coordinate
/// and the perpendicular distance to the long axis is
/// <c>√(|eye−pos|² − u²)</c>. Cull when the eye is inside the box expanded by a
/// small <paramref name="clearance"/> on every face. The expansion covers the
/// near-clip-slicing variant (a bolt whose geometry grazes just past the eye can
/// otherwise have the 1-unit near plane slice through it), so both flavours of the
/// bug are handled by one test. It is intentionally applied to BOTH camera modes:
/// the <em>active</em> eye is passed in, so chase mode (whose eye is far behind
/// the ship) only ever culls in the rare case a bolt is coincident with that eye.
/// </para>
/// </summary>
public static class ProjectileCameraCull
{
    /// <summary>
    /// Return <c>true</c> when the camera eye is inside (or within
    /// <paramref name="clearance"/> of) the bolt's solid box and should be culled.
    /// </summary>
    /// <param name="eyeX/eyeY/eyeZ">Active camera eye world position.</param>
    /// <param name="posX/posY/posZ">Bolt centre (the projectile's spawn/
    /// extrapolated position).</param>
    /// <param name="dirX/dirY/dirZ">Unit travel direction (the bolt's long axis;
    /// the caller guarantees it is normalized — see <c>Game1</c>'s gather loop).</param>
    /// <param name="length">The bolt's full length (long-axis extent).</param>
    /// <param name="width">The bolt's cross-section side (the thin dimension).</param>
    /// <param name="clearance">Extra margin beyond the box faces, comfortably past
    /// the near clip plane so a bolt grazing the eye is caught too.</param>
    public static bool ShouldCull(
        float eyeX, float eyeY, float eyeZ,
        float posX, float posY, float posZ,
        float dirX, float dirY, float dirZ,
        float length, float width, float clearance)
    {
        // Eye relative to the bolt centre.
        float rx = eyeX - posX, ry = eyeY - posY, rz = eyeZ - posZ;
        // Longitudinal coordinate along the long axis (dir assumed unit).
        float u = rx * dirX + ry * dirY + rz * dirZ;
        // Squared perpendicular distance from the eye to the long axis.
        float perpSq = rx * rx + ry * ry + rz * rz - u * u;

        float halfLong = length * 0.5f + clearance;
        float halfLat = width * 0.5f + clearance;
        return Math.Abs(u) <= halfLong && perpSq <= halfLat * halfLat;
    }
}
