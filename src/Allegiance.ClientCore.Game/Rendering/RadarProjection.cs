using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P250 — world-space → screen-space projection for the local tactical radar.
/// Turns a nearby object's true world position into the inputs
/// <see cref="RadarBracketLayout.Resolve"/> needs (real NDC when in front, plus
/// the camera-space direction) and returns the final viewport pixel +
/// placement. This is the same camera-relative projection the target-pick /
/// lock-bracket code already uses (<c>ComputeLockBracket</c>,
/// <c>CollectTargetCandidates</c>: subtract <c>cameraEye</c>, transform through
/// <c>view*proj</c>); factored here so the game and the live-verify harness
/// share the EXACT production path.
///
/// Faithful to native: the off-screen direction uses the camera-space
/// right/up components of the object (native's <c>TransformLocalToEye</c>,
/// WinTrek.cpp:7882) projected at a forced in-front depth, so the marker's
/// lateral signs stay correct even when the object is behind the eye — the
/// same information native clamps to the image rect in WinTrek.cpp:7893-7923.
/// </summary>
public static class RadarProjection
{
    /// <summary>Project one world-space contact position into a radar site.</summary>
    public static RadarBracketLayout.RadarSite Project(
        Vector3 worldPos, Vector3 cameraEye,
        Matrix view, Matrix proj,
        float width, float height, float inset)
    {
        var rel = worldPos - cameraEye;
        var viewProj = view * proj;

        // Real projection (meaningful only when the object is in front, w>0).
        var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
        bool inFront = clip.W > 1e-6f;
        float ndcX = inFront ? clip.X / clip.W : 0f;
        float ndcY = inFront ? clip.Y / clip.W : 0f;

        // Camera-space direction: x = right, y = up. Valid even behind the eye.
        var cam = Vector3.Transform(rel, view);
        var dirClip = Vector4.Transform(new Vector4(cam.X, cam.Y, -1f, 1f), proj);
        float dirX = MathF.Abs(dirClip.W) > 1e-6f ? dirClip.X / dirClip.W : 0f;
        float dirY = MathF.Abs(dirClip.W) > 1e-6f ? dirClip.Y / dirClip.W : 0f;

        return RadarBracketLayout.Resolve(inFront, ndcX, ndcY, dirX, dirY, width, height, inset);
    }
}
