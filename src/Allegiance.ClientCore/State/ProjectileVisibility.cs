namespace Allegiance.ClientCore.State;

/// <summary>
/// P542 — the single source of truth for projectile VISIBILITY decisions that
/// both the MonoGame renderer and the headless diagnostics/draw path share.
/// Before P542 these lived as private <c>Game1</c> constants plus the
/// <c>ProjectileCameraCull</c> helper in the Game project, which the headless
/// <see cref="ClientWorld"/> path could not see — so a live proof had no way to
/// count how many received projectiles were actually DRAWN. Centralising the
/// radius test and the near-camera cull here (pure math, no MonoGame dependency)
/// lets the renderer's <c>[proj-diag]</c> line and the test both measure "drawn"
/// with exactly the same rules.
/// </summary>
public static class ProjectileVisibility
{
    /// <summary>Projectile visibility radius around the local ship — the "sector
    /// filter" analog for a wire (<c>PROJECTILE_INSTANCE</c>) that carries no
    /// cluster id (the same reasoning and value as the former
    /// <c>Game1.ProjectileVisibilityRadiusSqr</c>, P231).</summary>
    public const float VisibilityRadius = 5900f;
    public const float VisibilityRadiusSqr = VisibilityRadius * VisibilityRadius;

    /// <summary>Near-camera cull gap (former <c>Game1.ProjectileNearCamClearance</c>,
    /// P239/P243): a bolt whose solid box is inside (or within this distance of)
    /// the active camera eye is skipped so own-forward shots spawning at ship
    /// centre don't momentarily engulf the cockpit eye.</summary>
    public const float NearCamClearance = 15f;

    // Bolt length tuning (formerly private Game1 constants, P239/P243): length ∝
    // speed so faster shots read as longer streaks, clamped to a sensible band.
    // Centralised here so the draw decision (and hence the headless
    // [proj-diag] "drawn" count) uses exactly the renderer's sizing.
    public const float BoltLengthPerUnitSeconds = 0.02f;
    public const float MinBoltLength = 4f;
    public const float MaxBoltLength = 16f;

    /// <summary>
    /// True when the camera eye lies inside (or within <paramref name="clearance"/>
    /// of) the bolt's elongated solid box — the former
    /// <c>ProjectileCameraCull.ShouldCull</c> body, made dependency-free so the
    /// headless path can apply the identical rule. See that type's doc comment
    /// for the geometry derivation.
    /// </summary>
    public static bool CameraCulled(
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
