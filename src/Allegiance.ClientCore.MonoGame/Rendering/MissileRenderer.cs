using Allegiance.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws every live missile with the <em>real parsed</em> missile model mesh
/// (P233's <see cref="MdlFile"/>), loaded once and built into a single shared
/// MonoGame <see cref="VertexBuffer"/>/<see cref="IndexBuffer"/> in the ctor —
/// the "one shared buffer if all missiles on screen share a model" convention
/// from the <see cref="AsteroidRenderer"/>/<see cref="WarpRenderer"/> pattern,
/// but with the geometry coming from a real <c>.mdl</c> file rather than
/// procedural generation. Each instance's <see cref="Draw"/> world transform is
/// supplied by the caller: rotated so the missile's nose sits along its
/// per-instance heading and translated to its position minus the camera eye.
/// </summary>
/// <remarks>
/// <para><b>Solid, lit — no texture.</b> P233 did not decode textures and the
/// task scopes texture-mapping out, so the mesh renders as a solid-coloured,
/// lit surface (BasicEffect <c>EnableDefaultLighting</c> + a per-renderer
/// <see cref="DiffuseColor"/>), using the mesh's real per-vertex normals. The
/// .mdl texcoords are carried but unused (set to 0).</para>
/// <para><b>Winding/culling — the P234 empirical finding.</b> The mesh's indices
/// are consumed in raw file order (no flip, mirroring <see cref="MdlFile"/>'s
/// own "no correction applied" contract). The DirectX-authored winding renders
/// <b>inside-out</b> under MonoGame's default back-face culling
/// (<c>CullCounterClockwiseFace</c>): an offscreen render of <c>mis05.mdl</c>
/// showed small interior holes in the concave regions (20 px), which vanished
/// (0 px) once the near faces were kept. The MonoGame-side fix is to cull the
/// <em>other</em> faces — <see cref="CullMode.CullClockwiseFace"/> — keeping the
/// raw file indices intact. This is MonoGame's own mechanism (a per-renderer
/// rasterizer state), not a mesh/CPU flip. See docs/MISSILE_RENDERING_P234.md
/// for the measured before/after.</para>
/// <para><b>Shared buffer:</b> all meshes from the parsed file are concatenated
/// into one vertex/index buffer (offsets carried across), so a single
/// <see cref="DrawIndexedPrimitives"/> per missile instance suffices. If a file
/// has multiple chunks (turret/antenna), they all compose one model.</para>
/// </remarks>
public sealed class MissileRenderer : IDisposable
{
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _rasterizer;

    /// <summary>
    /// Construct from a parsed <see cref="MdlFile"/>'s meshes.
    /// </summary>
    /// <param name="gd">The graphics device the buffers are created on.</param>
    /// <param name="meshes">The parsed mesh list (all of <c>MdlFile.Meshes</c>,
    /// concatenated into one buffer).</param>
    /// <param name="diffuseColor">The solid surface tint (RGB in 0..1) used for
    /// every instance (this port's single-model / single-colour scope).</param>
    public MissileRenderer(GraphicsDevice gd, IReadOnlyList<MdlMesh> meshes, Vector3 diffuseColor)
    {
        int vertexTotal = 0, indexTotal = 0;
        foreach (var m in meshes)
        {
            vertexTotal += m.VertexCount;
            indexTotal += m.Indices.Length;
        }
        if (vertexTotal == 0 || indexTotal == 0)
            throw new ArgumentException("missile model has no geometry");

        var verts = new VertexPositionNormalTexture[vertexTotal];
        var indices = new uint[indexTotal];
        int vCursor = 0, iCursor = 0;
        foreach (var m in meshes)
        {
            int vertBase = vCursor;
            for (int v = 0; v < m.VertexCount; v++)
            {
                verts[vCursor++] = new VertexPositionNormalTexture(
                    new Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]),
                    new Vector3(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2]),
                    new Vector2(m.TexCoords[2 * v], m.TexCoords[2 * v + 1]));
            }
            for (int i = 0; i < m.Indices.Length; i++)
                indices[iCursor++] = (uint)(m.Indices[i] + vertBase);
        }

        _vertices = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
        _vertices.SetData(verts);
        _indices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
        _indices.SetData(indices);
        _triangleCount = indices.Length / 3;

        _effect = new BasicEffect(gd) { DiffuseColor = diffuseColor };
        _effect.EnableDefaultLighting();    // method, not a property — sets default directional lighting
        // P234 winding fix: the DirectX-authored mesh is CW-front; MonoGame's
        // default CullCounterClockwiseFace culls its near faces (inside-out).
        // Cull CullClockwiseFace instead (empirically 0 interior holes — see class doc).
        _rasterizer = new RasterizerState { CullMode = CullMode.CullClockwiseFace };
    }

    /// <summary>Draw the missile at the given world transform (nose-along-heading
    /// rotation + camera-relative translation), lit by the shared effect.</summary>
    public void Draw(Matrix world, Matrix view, Matrix projection)
    {
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.RasterizerState = _rasterizer;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
        _rasterizer.Dispose();
    }
}
