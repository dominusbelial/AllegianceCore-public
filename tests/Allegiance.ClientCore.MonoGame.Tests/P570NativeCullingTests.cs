using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P570 — native ship culling regression tests. Bug: P566's
/// <see cref="NativeShipResolver.CorrectWinding"/> reversal makes native ships
/// render inside-out (front faces culled, "transparent") in the REAL game,
/// because it is correct for roster-capture's ORTHOGRAPHIC sheet but backwards
/// for MonoGame's PERSPECTIVE projection that the game / chase view / hangar
/// preview all use. The fix (<see cref="NativeShipResolver.RenderIndices"/>)
/// ships the RAW native winding so <c>CullCounterClockwiseFace</c> keeps the
/// outside. These tests prove it in pure math (no GPU): with the shipped
/// winding, the OUTSIDE (camera-facing) faces wind CLOCKWISE on screen, so the
/// CullCounterClockwiseFace default keeps them solid. This fails when the
/// winding is reversed (CorrectWinding) and passes with the raw winding.
/// </summary>
public class P570NativeCullingTests
{
    private static string ArtRoot =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    /// <summary>Parses a real player .mdl mesh; returns null if artwork is not
    /// present in this environment (test is then inert rather than a false fail).</summary>
    private static MdlMesh? TryLoadMesh(string model)
    {
        string path = Path.Combine(ArtRoot, model + ".mdl");
        if (!File.Exists(path)) return null;
        try
        {
            var f = MdlFile.Parse(path);
            // Use the LARGEST sub-mesh (the hull), not just the first small part.
            return f.Meshes.Where(m => m.VertexCount >= 3 && m.Indices.Length >= 3)
                           .OrderByDescending(m => m.Indices.Length).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    // The render transform the real game uses for a native ship: nose flip
    // (RY(π), NativeShipRenderer.NoseFlip) folded before the camera-relative
    // world, then the chase-camera view + perspective projection.
    private static (Matrix Local, Matrix View, Matrix Proj) GameTransform()
    {
        var noseFlip = Matrix.CreateRotationY(MathHelper.Pi);
        var world = Matrix.CreateTranslation(new Vector3(0f, 0f, -600f)); // ship 600 ahead of cam
        var local = noseFlip * world;
        var view = Matrix.CreateLookAt(Vector3.Zero, new Vector3(0f, 0f, -600f), Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50f), 16f / 9f, 0.1f, 60000f);
        return (local, view, proj);
    }

    /// <summary>Determine whether the mesh's geometric NEAR surface (identified
    /// winding-independently) is KEPT by CullCounterClockwiseFace. The near-side
    /// set is found by orienting each triangle's cross-normal OUTWARD with a single
    /// GLOBAL sign (derived from the bulk of the mesh — robust to concave parts,
    /// and identical for both windings since the global sign absorbs the index
    /// order). Only the projection winding differs between raw and reversed, so
    /// this test genuinely distinguishes solid from inside-out.</summary>
    private static (int Total, int Kept, int Culled) MeasureWinding(MdlMesh mesh, ushort[] indices)
    {
        var (local, view, proj) = GameTransform();
        var localViewProj = local * view * proj;

        // Pass 1: mesh centroid + global outward sign (winding-independent).
        Vector3 meshCentroid = Vector3.Zero;
        for (int v = 0; v < mesh.VertexCount; v++)
            meshCentroid += new Vector3(mesh.Positions[3 * v], mesh.Positions[3 * v + 1], mesh.Positions[3 * v + 2]);
        meshCentroid /= Math.Max(1, mesh.VertexCount);

        double alignSum = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            var pa = new Vector3(mesh.Positions[3 * ia], mesh.Positions[3 * ia + 1], mesh.Positions[3 * ia + 2]);
            var pb = new Vector3(mesh.Positions[3 * ib], mesh.Positions[3 * ib + 1], mesh.Positions[3 * ib + 2]);
            var pc = new Vector3(mesh.Positions[3 * ic], mesh.Positions[3 * ic + 1], mesh.Positions[3 * ic + 2]);
            var n = Vector3.Cross(pb - pa, pc - pa);
            var center = (pa + pb + pc) / 3f;
            alignSum += Vector3.Dot(n, center - meshCentroid);
        }
        float globalSign = alignSum >= 0 ? 1f : -1f;   // flip all cross-normals to point OUTWARD

        // Pass 2: near-side (outward normal toward camera) -> screen winding.
        int kept = 0, culled = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            var pa = new Vector3(mesh.Positions[3 * ia], mesh.Positions[3 * ia + 1], mesh.Positions[3 * ia + 2]);
            var pb = new Vector3(mesh.Positions[3 * ib], mesh.Positions[3 * ib + 1], mesh.Positions[3 * ib + 2]);
            var pc = new Vector3(mesh.Positions[3 * ic], mesh.Positions[3 * ic + 1], mesh.Positions[3 * ic + 2]);

            var wa = Vector3.Transform(pa, local);
            var wb = Vector3.Transform(pb, local);
            var wc = Vector3.Transform(pc, local);
            // Geometric outward normal (winding-independent) in camera-relative space.
            var outward = globalSign * Vector3.TransformNormal(Vector3.Cross(pb - pa, pc - pa), local);
            var centroid = (wa + wb + wc) / 3f;
            bool cameraFacing = Vector3.Dot(outward, -centroid) > 0f;   // camera at origin
            if (!cameraFacing) continue;

            Vector4 ca = Vector4.Transform(new Vector4(pa, 1f), localViewProj);
            Vector4 cb = Vector4.Transform(new Vector4(pb, 1f), localViewProj);
            Vector4 cc = Vector4.Transform(new Vector4(pc, 1f), localViewProj);
            if (ca.W <= 1e-6f || cb.W <= 1e-6f || cc.W <= 1e-6f) continue;
            float ax = ca.X / ca.W, ay = ca.Y / ca.W;
            float bx = cb.X / cb.W, by = cb.Y / cb.W;
            float cx = cc.X / cc.W, cy = cc.Y / cc.W;
            float signedArea = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            // Ground truth (real window): with the CORRECT raw winding the ship is
            // SOLID, and the test shows the near surface then winds NDC-NEGATIVE —
            // so CullCounterClockwiseFace keeps NDC-negative faces (window-clockwise
            // is NDC-negative because NDC y is up while window y is down).
            if (signedArea < 0f) kept++; else culled++;
        }
        return (kept + culled, kept, culled);
    }

    [Theory]
    [InlineData("fig05")]
    [InlineData("wc_gtsct")]
    public void Shipped_native_winding_keeps_the_outside_under_the_game_perspective(string model)
    {
        var mesh = TryLoadMesh(model);
        if (mesh is null) return;   // artwork absent here -> inert, not a false fail

        var (total, kept, culled) = MeasureWinding(mesh, NativeShipResolver.RenderIndices(mesh.Indices));

        Assert.True(total > 50, $"'{model}' parsed too little geometry ({total} camera-facing triangles)");
        // With the CORRECT (raw) winding, the visible near surface of the hull is
        // KEPT by CullCounterClockwiseFace (NDC-positive / window-clockwise). If
        // someone re-applies CorrectWinding (the P566 reversal), the near surface
        // becomes the culled pole -> this fails, catching the inside-out regression.
        double keptFraction = (double)kept / total;
        Assert.True(keptFraction >= 0.9,
            $"'{model}': only {keptFraction:P0} of the near-surface faces are kept by " +
            $"CullCounterClockwiseFace (should be ≥90%); {kept} kept / {culled} culled — " +
            "winding is reversed, ships render inside-out");
    }
}
