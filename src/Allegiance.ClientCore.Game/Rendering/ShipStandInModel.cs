using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P607 — the pure geometry of the procedural ship stand-in (positions +
/// triangle-list indices, no MonoGame types), so the mesh is unit-testable
/// headless exactly like <see cref="TreasureMeshFactory"/>/<see cref="WarpMeshFactory"/>.
/// </summary>
public sealed record ShipStandInGeometry(float[] Positions, int[] Indices)
{
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// P607 — a guaranteed-non-null procedural ship stand-in model, used as the
/// final fallback in the ship colour pass when a ship has NO native art (its
/// hull failed native <c>.mdl</c> resolution) AND no Meshy pack is installed
/// (so <c>_fallbackModel</c> / <c>_shipModels</c> are all null). It is built
/// entirely from raw vertex/index geometry + one <see cref="BasicEffect"/> at
/// load time, independent of the artwork corpus and the Meshy pack, so it can
/// never be null — the render path that draws ships never crashes on a null
/// model (the P607 live crash: <c>NullReferenceException</c> in
/// <c>ShipRenderer.Draw</c> at <c>Game1.Rendering.cs:611</c> when both
/// <c>shipModel</c> and <c>_fallbackModel</c> were null).
/// </summary>
/// <remarks>
/// Shape: a simple faceted hull — an octahedron elongated along the local
/// forward axis, the "hull-class default" spirit without any per-hull art. It
/// is drawn through the ordinary <see cref="ShipRenderer.Draw"/> path (which
/// normalises size via <c>ShipRenderScale</c>), so the ship stays visible and
/// targetable exactly like every other ship. This is deliberately a plain
/// placeholder, not a faithful reproduction of any faction hull (the task's
/// explicit "simple procedural shape" option).
/// </remarks>
public static class ShipStandInModel
{
    /// <summary>Half-length of the stand-in along the forward (Z) axis. Chosen
    /// to read as a modest fighter silhouette at scale 1 (unknown hulls render
    /// at scale 1 because <c>HullLength</c> is 0 → <c>ShipRenderScale</c> 1).</summary>
    public const float HalfLength = 15f;

    /// <summary>Half-width/height of the equatorial square (radius about the
    /// forward axis), in mesh units.</summary>
    public const float HalfWidth = 5.5f;

    /// <summary>
    /// Pure geometry: one elongated octahedron (top apex, flat equatorial
    /// square, bottom tip). Deterministic and MonoGame-free.
    /// </summary>
    public static ShipStandInGeometry CreateGeometry()
    {
        float L = HalfLength, W = HalfWidth;
        float[] pos =
        {
            0f, 0f, L,     // 0 top apex (nose)
            -W, -W, 0f,    // 1 equator
            W, -W, 0f,     // 2
            W, W, 0f,      // 3
            -W, W, 0f,     // 4
            0f, 0f, -L,    // 5 bottom apex (tail)
        };
        int[] idx =
        {
            0, 1, 2,  0, 2, 3,  0, 3, 4,  0, 4, 1,   // upper cone (4 faces)
            5, 2, 1,  5, 3, 2,  5, 4, 3,  5, 1, 4,   // lower cone (4 faces)
        };
        return new ShipStandInGeometry(pos, idx);
    }

    /// <summary>
    /// Build the procedural stand-in <see cref="Model"/>. Per-vertex normals =
    /// direction from centre (so <c>BasicEffect</c> lighting shades the
    /// facets), one <c>ModelMeshPart</c> + one root bone. Never returns null.
    /// </summary>
    public static Model Build(GraphicsDevice gd)
    {
        var geo = CreateGeometry();
        int n = geo.VertexCount;

        var verts = new VertexPositionNormalTexture[n];
        for (int i = 0; i < n; i++)
        {
            var p = new Vector3(geo.Positions[3 * i], geo.Positions[3 * i + 1], geo.Positions[3 * i + 2]);
            verts[i] = new VertexPositionNormalTexture(p, Vector3.Normalize(p), Vector2.Zero);
        }
        var indices = new uint[geo.Indices.Length];
        for (int i = 0; i < geo.Indices.Length; i++) indices[i] = (uint)geo.Indices[i];

        var vbuf = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
        vbuf.SetData(verts);
        var ibuf = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
        ibuf.SetData(indices);

        var effect = new BasicEffect(gd);
#pragma warning disable CS0618 // ModelMeshPart's parameterless ctor is deprecated but is the only way to build a Model at runtime (as the content loader itself does).
        var part = new ModelMeshPart
        {
            VertexBuffer = vbuf,
            IndexBuffer = ibuf,
            StartIndex = 0,
            PrimitiveCount = indices.Length / 3,
        };
#pragma warning restore CS0618

        var mesh = new ModelMesh(gd, new List<ModelMeshPart> { part })
        {
            // Rough bounding sphere so the renderer's scale helper has a radius.
            BoundingSphere = new BoundingSphere(Vector3.Zero, HalfLength),
        };
        // ModelMeshPart.Effect requires a wired parent (set only once the part
        // is attached to its mesh, else the setter NREs), so assign it here.
        part.Effect = effect;

        var bone = new ModelBone { Index = 0, Transform = Matrix.Identity };
        bone.AddMesh(mesh);

        var model = new Model(gd, new List<ModelBone> { bone }, new List<ModelMesh> { mesh });
        // Single bone with no parent → the Model ctor already made it the root;
        // ensure it explicitly so the invariant holds even if the ctor changes.
        model.Root = bone;
        return model;
    }
}
