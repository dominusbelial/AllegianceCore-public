using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P333 — normalizes each ship model's raw geometric size to the real,
/// server-authoritative hull size, so ships read at consistent/proportional
/// real-world scale instead of whatever arbitrary per-model size the P287
/// Meshy exports happened to come out at.
///
/// <para><b>The problem.</b> The MonoGame content pipeline unit-normalizes each
/// imported model (its merged bounding-sphere local radius ≈ 1.0), and
/// <see cref="ShipRenderer"/> draws every ship through
/// <c>mesh.ParentBone.Transform</c> — which bakes a large (~100×) uniform scale
/// that restores real-world units. With 72 separately Meshy-generated models
/// (P287/P288) this leaves <b>every</b> ship rendering at roughly the SAME
/// physical size (~95–160 unit radius, per <c>docs/P305_roster/roster_variety.txt</c>),
/// regardless of whether it is a scout or a carrier. A fighter (real
/// <c>HullType.Length</c> ~50) and a carrier (real Length ~1500+) therefore
/// both read at ~100 units — fighters look grossly oversized, capitals puny,
/// exactly Domingo's live "they look terrible, some are oversized".</para>
///
/// <para><b>The real dimension used.</b> The authoritative real-world size of a
/// hull is <c>DataHullTypeIGC.Length</c>, and the gameplay size derived from it
/// is a SPHERE of radius <c>0.5 × Length</c> — the collision radius every
/// server physics/docking read uses (<c>Server.Docking.ShipRadius</c>,
/// <c>Sector.cs</c> proximity/collision). We chose the merged-bounding-sphere
/// radius as the model's own measured extent (the same measurement
/// <c>tools/roster-capture</c> uses, and the natural partner to a sphere-based
/// collision), and scale so that the model's <em>true rendered</em> sphere
/// radius equals that gameplay collision radius <c>0.5 × Length</c>. This is
/// the one choice that makes "how big the ship looks" agree with "how big it
/// actually is for gameplay" for the first time, rather than diverging further.
/// (A longest-axis/length measure was rejected: there is no gameplay bounding
/// box to align to — gameplay size is a sphere — so a box measure could not
/// produce the visual ≡ gameplay agreement this does.)</para>
///
/// <para><b>Scope.</b> Pure visual scale correction applied in the render path
/// only; the underlying sim/collision radius is server-authoritative and is
/// never touched here.</para>
/// </summary>
public static class ShipRenderScale
{
    /// <summary>
    /// TRUE rendered merged-bounding-sphere radius of a model, accounting for
    /// the parent-bone 100× real-world restore <see cref="ShipRenderer"/> draws
    /// through — the same measurement the P331 <c>roster-capture</c> tool uses
    /// (<c>RenderRadius</c>). Because the bone bake is a uniform scale, each
    /// mesh's local sphere radius scales linearly with its bone scale; each
    /// transformed sphere is then merged into one that bounds the whole model.
    /// </summary>
    /// <param name="m">A compiled content <c>Model</c>; null → 0.</param>
    public static float RenderRadius(Model? m)
    {
        if (m is null) return 0f;
        if (_renderRadiusCache.TryGetValue(m, out var cached)) return cached;

        BoundingSphere? acc = null;
        foreach (var mesh in m.Meshes)
        {
            var T = mesh.ParentBone.Transform;
            float s = new Vector3(T.M11, T.M12, T.M13).Length();
            var local = mesh.BoundingSphere;
            var c = Vector3.Transform(local.Center, T);
            var sph = new BoundingSphere(c, local.Radius * s);
            acc = acc is null ? sph : BoundingSphere.CreateMerged(acc.Value, sph);
        }
        float radius = acc is null ? 0f : acc.Value.Radius;
        _renderRadiusCache[m] = radius;
        return radius;
    }

    /// <summary>
    /// The uniform scale that maps <paramref name="model"/>'s TRUE rendered size
    /// to the real hull size <paramref name="hullLength"/> (<c>HullType.Length</c>):
    /// scale so the rendered merged-sphere radius equals the hull's gameplay
    /// collision radius <c>0.5 × hullLength</c> (<see cref="MapLengthAndRadius"/>).
    /// Returns 1 (no scaling) when the model or length is unusable, so a missing
    /// catalogue/hull never corrupts the transform.
    /// </summary>
    public static float ScaleForLength(float hullLength, Model? model)
    {
        if (model is null || hullLength <= 0f) return 1f;
        float rr = RenderRadius(model);
        return MapLengthAndRadius(hullLength, rr);
    }

    /// <summary>
    /// Pure mapping: the scale factor that makes a sphere of radius
    /// <paramref name="renderRadius"/> equal the hull's collision radius
    /// <c>0.5 × hullLength</c>. Guarded: non-positive radius or length → 1.
    /// </summary>
    public static float MapLengthAndRadius(float hullLength, float renderRadius)
    {
        if (hullLength <= 0f || renderRadius <= 0f) return 1f;
        return (0.5f * hullLength) / renderRadius;
    }

    /// <summary>Render-radius cache keyed by <c>Model</c> instance — models are
    /// singletons cached by Game1's <c>_shipModels</c> (one instance per asset)
    /// plus the fallback singleton, so the reference is a stable, cheap key.</summary>
    private static readonly Dictionary<Model, float> _renderRadiusCache = new();
}
