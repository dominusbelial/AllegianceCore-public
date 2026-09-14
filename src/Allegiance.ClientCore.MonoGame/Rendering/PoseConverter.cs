using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xna = Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Conversions between the sim's pure-types (<see cref="Vector3"/> /
/// <see cref="Orientation"/>) and MonoGame's rendering types
/// (<c>Microsoft.Xna.Framework.Vector3</c> / <c>Matrix</c>). Kept here so the
/// ship renderer and chase camera stay straightforward MonoGame code while the
/// <c>Flight/*</c> logic stays free of MonoGame types.
/// </summary>
public static class PoseConverter
{
    public static Xna.Vector3 ToXna(in Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Build the world rotation from a sim <see cref="Orientation"/>. The sim
    /// stores rows = {right, up, backward} as the local axes' world directions;
    /// MonoGame's row-vector convention (<c>v * M</c>) maps each local basis
    /// axis to its world direction via the matching <em>row</em>, so the XNA
    /// matrix rows are exactly right / up / backward.
    /// </summary>
    public static Xna.Matrix ToXnaRotation(Orientation o)
    {
        var r = o.GetRight();
        var u = o.GetUp();
        var b = o.GetBackward();
        return new Xna.Matrix(
            r.X, r.Y, r.Z, 0f,
            u.X, u.Y, u.Z, 0f,
            b.X, b.Y, b.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary>Full world transform (rotate then translate) for a sim pose,
    /// using the pose's TRUE absolute world position. Do not use this for
    /// drawing — see <see cref="ToXnaWorldRelative"/> and
    /// <see cref="ChaseCamera.Matrices"/>'s remarks for why large absolute
    /// world coordinates broke rendering on this GPU (P207). Kept for
    /// anything that genuinely needs the true transform (none of this POC's
    /// drawing code should).</summary>
    public static Xna.Matrix ToXnaWorld(in Vector3 pos, Orientation o) =>
        ToXnaRotation(o) * Xna.Matrix.CreateTranslation(ToXna(pos));

    /// <summary>Camera-relative world transform: rotation unchanged, but the
    /// translation is the object's position MINUS the camera's true
    /// world-space eye (both already-converted XNA vectors) — this is what
    /// every draw call must use, per <see cref="ChaseCamera.Matrices"/>'s
    /// remarks. Keeps values reaching the vertex shader small regardless of
    /// where the action is in the sim's world.</summary>
    public static Xna.Matrix ToXnaWorldRelative(Xna.Vector3 xnaPosition, Orientation o, Xna.Vector3 cameraEye) =>
        ToXnaRotation(o) * Xna.Matrix.CreateTranslation(xnaPosition - cameraEye);
}
