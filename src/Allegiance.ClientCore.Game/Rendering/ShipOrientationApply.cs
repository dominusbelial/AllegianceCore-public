using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.Core.Content;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P520 — applies the per-asset orientation override table
/// (<see cref="ModelOrientationOverrides"/>) to a freshly-loaded ship model by
/// folding the yaw/pitch/roll into each mesh's parent-bone transform, once.
///
/// <para>This is the exact transform slot the verified live play-patch used
/// (multiply each unique <c>mesh.ParentBone.Transform</c> by the extra
/// rotation) — it lands in the same slot as the baked axis-swap, BEFORE the
/// renderer's global <c>NoseAlignmentCorrection</c>, so the two compose
/// correctly and every subsequent roll/pitch/yaw command spins around axes that
/// match the visible geometry. Because it mutates the shared <c>Model</c>'s
/// bones at load, every consumer that renders the same loaded model —
/// gameplay, roster-capture reference sheets, thumbnails, turnarounds — sees
/// the corrected orientation automatically.</para>
/// </summary>
public static class ShipOrientationApply
{
    /// <summary>Fold the asset's orientation override (if any) into
    /// <paramref name="model"/>'s parent bones. Idempotent-safe per load: each
    /// model is loaded once and corrected once.</summary>
    public static void Apply(Model model, string asset)
    {
        if (model is null) return;
        var o = ModelOrientationOverrides.Get(asset);
        if (o == ModelOrientationOverrides.Identity) return;

        var rot = Matrix.CreateRotationY(MathHelper.ToRadians(o.YawDeg))
                * Matrix.CreateRotationX(MathHelper.ToRadians(o.PitchDeg))
                * Matrix.CreateRotationZ(MathHelper.ToRadians(o.RollDeg));
        var seen = new HashSet<ModelBone>();
        foreach (var mesh in model.Meshes)
            if (seen.Add(mesh.ParentBone))
                mesh.ParentBone.Transform = mesh.ParentBone.Transform * rot;
    }
}
