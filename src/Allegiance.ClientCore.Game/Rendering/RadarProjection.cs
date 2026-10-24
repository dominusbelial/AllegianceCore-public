using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P250 — world-space → screen-space projection for the local tactical radar.
/// Turns a nearby object's true world position into the inputs
/// <see cref="RadarBracketLayout.Resolve"/> needs (real NDC when in front, plus
/// the camera-space right/up components) and returns the final viewport pixel +
/// placement. This is the same camera-relative projection the target-pick /
/// lock-bracket code already uses (<c>ComputeLockBracket</c>,
/// <c>CollectTargetCandidates</c>: subtract <c>cameraEye</c>, transform through
/// <c>view*proj</c>); factored here so the game and the live-verify harness
/// share the EXACT production path.
///
/// <para><b>P804 — native eye-space clamp.</b> The off-screen direction is the
/// object's camera-space right/up components exactly as native reads them
/// (<c>m_pcamera-&gt;TransformLocalToEye(positionModel)</c>,
/// <c>WinTrek.cpp:7882</c>) and clamps them to the image rect / round-radar
/// circle (<c>WinTrek.cpp:7883-7921</c>). The pre-P804 port projected that
/// direction through the projection matrix first (a tan-space NDC
/// approximation) and normalised by the dominant axis, which distorted the edge
/// position at the frame corners.</para>
/// </summary>
public static class RadarProjection
{
    /// <summary>Project one world-space contact position into a radar site.</summary>
    /// <param name="roundRadar">Native <c>m_bRoundRadar</c>: on-screen only
    /// inside the inscribed circle, off-screen pinned to that circle
    /// (<c>WinTrek.cpp:7849-7906</c>).</param>
    public static RadarBracketLayout.RadarSite Project(
        Vector3 worldPos, Vector3 cameraEye,
        Matrix view, Matrix proj,
        float width, float height, float inset, bool roundRadar = false)
    {
        var rel = worldPos - cameraEye;
        var viewProj = view * proj;

        // Real projection (meaningful only when the object is in front, w>0).
        var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
        bool inFront = clip.W > 1e-6f;
        float ndcX = inFront ? clip.X / clip.W : 0f;
        float ndcY = inFront ? clip.Y / clip.W : 0f;

        // Camera-space x = right, y = up — the raw components native's
        // eye-space clamp scales to the rect/circle edge. Valid even behind
        // the eye, so the lateral signs (left/right, up/down) are correct.
        var cam = Vector3.Transform(rel, view);
        float eyeX = cam.X;
        float eyeY = cam.Y;

        return RadarBracketLayout.Resolve(
            inFront, ndcX, ndcY, eyeX, eyeY, width, height, inset, roundRadar);
    }
}
