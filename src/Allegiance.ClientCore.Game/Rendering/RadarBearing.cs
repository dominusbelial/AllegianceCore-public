using System;
using Xna = Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1039 — the presentation-only own-relative BEARING of a contact (P972 §4.4, slice 3C).
/// The Jet RWR places a contact by bearing and range instead of reconstructing a direction
/// from a clamped screen point; this is the one derivation both the snapshot builder and the
/// unit tests use.
/// <para><b>Definition.</b> The bearing is the signed angle, about the SECTOR PLANE NORMAL,
/// from the own ship's forward projected onto that plane to the contact offset projected onto
/// the same plane: <c>0</c> = dead ahead, positive = to the ship's LEFT (the heading tape's
/// own sense — heading increases toward +Y, <see cref="Jet.JetFlightGeometry.Attitude"/>),
/// range −π … +π.</para>
/// <para><b>Roll invariance.</b> Only the forward axis and the plane normal enter the
/// derivation, so rolling the ship about its own forward leaves the bearing unchanged by
/// construction (the ship's own right/up axes are never read).</para>
/// <para><b>Null cases.</b> A zero contact offset, a pose whose forward has no usable planar
/// projection (a ship pointing along the plane normal: no horizontal reference exists) or a
/// zero plane normal return null — the renderer then falls back to the ordinary projected
/// scope instead of inventing a direction. A contact offset that itself lies along the plane
/// normal (directly overhead/below with no lateral component) also returns null: there is no
/// honest 2-D bearing for it.</para>
/// </summary>
public static class RadarBearing
{
    /// <summary>The planar length below which a direction has no usable bearing.</summary>
    public const float Epsilon = 1e-4f;

    /// <summary>
    /// The signed own-relative bearing in radians (null per the class remarks).
    /// </summary>
    /// <param name="ownForward">The own (source) ship's forward axis in world space.</param>
    /// <param name="planeNormal">The sector reference plane's normal (native world +Z).</param>
    /// <param name="delta">The contact position minus the own ship position.</param>
    public static float? Relative(Xna.Vector3 ownForward, Xna.Vector3 planeNormal, Xna.Vector3 delta)
    {
        float normalLength = planeNormal.Length();
        if (normalLength < Epsilon) return null;
        Xna.Vector3 normal = planeNormal / normalLength;

        float forwardLength = ownForward.Length();
        if (forwardLength < Epsilon) return null;
        Xna.Vector3 forward = ownForward / forwardLength;

        // Planar projections (the component along the normal is dropped).
        Xna.Vector3 planarForward = forward - normal * Xna.Vector3.Dot(forward, normal);
        if (planarForward.Length() < Epsilon) return null;      // vertical attitude
        planarForward.Normalize();

        Xna.Vector3 planarDelta = delta - normal * Xna.Vector3.Dot(delta, normal);
        float planarLength = planarDelta.Length();
        if (planarLength < Epsilon) return null;                // no horizontal direction
        planarDelta /= planarLength;

        float cos = Math.Clamp(Xna.Vector3.Dot(planarForward, planarDelta), -1f, 1f);
        float sin = Xna.Vector3.Dot(Xna.Vector3.Cross(planarForward, planarDelta), normal);
        return MathF.Atan2(sin, cos);
    }
}
