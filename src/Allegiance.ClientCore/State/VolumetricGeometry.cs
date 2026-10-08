namespace Allegiance.ClientCore.State;

using Allegiance.Core.Collision;

/// <summary>
/// P579 — pure geometry/fade helpers shared by the volumetric renderer and the
/// failing-first unit tests.
///
/// <b>Camera-relative placement.</b> The game's view matrix keeps the EYE at the
/// origin; every renderer subtracts <c>cameraEye</c> before the view transform
/// (<c>EnhancedExplosionRenderer</c> centre − cameraEye; native
/// <c>ExplosionGeo</c> likewise). The P573 spike's volumetric renderers did NOT
/// subtract it, so every explosion was offset by the whole camera position —
/// the "no explosions" bug Domingo saw live. This helper is the single place
/// the offset is applied so a unit test can pin it.
///
/// <b>Depth fade.</b> The volume melts onto the opaque scene by fading where the
/// scene's linear depth is closer than the volume's (<c>Volumetric.fx
/// DepthFade</c>). The P573 slope was <c>* (1/8)</c> — ~2000× too weak — and with
/// <c>--soft off</c> the unbound depth sampler read 0 → fade 0, erasing every
/// volume. The P579 slope is <c>SoftnessInv = 250</c> (the same
/// <c>SoftParticle.fx SoftnessNormalized</c> slope the Enhanced smoke uses), and
/// a 1×1 white "far" depth texture makes the no-depth case decode to scene=1 →
/// fade 1.
/// </summary>
public static class VolumetricGeometry
{
    /// <summary>The world-space volume centre, expressed camera-relative (subtract
    /// the camera eye, matching the view matrix's eye-at-origin convention).</summary>
    public static Vector3 CameraRelative(Vector3 world, Vector3 eye)
        => new(world.X - eye.X, world.Y - eye.Y, world.Z - eye.Z);

    /// <summary>Depth-fade softness inverse — the P579 slope (2000× the P573
    /// 1/8): a 1-unit normalized-depth gap saturates the fade to full.</summary>
    public const float SoftnessInv = 250f;

    /// <summary>Normalized linear depths: <paramref name="scene"/> = the opaque
    /// scene's encoded linear depth (0..1), <paramref name="volume"/> = the
    /// volume fragment's linear depth (0..1). Fade = 1 when the volume is nearer
    /// than the scene by more than <c>1/SoftnessInv</c>; 0 when the scene is
    /// deeper-hidden (fully occluded). A "far" scene (1) yields fade 1 — the
    /// no-depth (--soft off) guarantee.</summary>
    public static float DepthFade(float scene, float volume, float softnessInv = SoftnessInv)
    {
        var d = (scene - volume) * softnessInv;
        return d < 0f ? 0f : d > 1f ? 1f : d;
    }
}
