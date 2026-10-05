using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Rendering;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws every live deployed probe with the <em>real parsed</em> probe model
/// mesh (P233's <see cref="MdlFile"/>), loaded once and built into a single
/// shared MonoGame <see cref="VertexBuffer"/>/<see cref="IndexBuffer"/> in the
/// ctor — mirroring <see cref="MissileRenderer"/>'s exact structure (both are
/// small native-<c>.mdl</c>-sourced objects with the same real-asset story:
/// the geometry comes from a real <c>.mdl</c> file parsed at runtime, not a
/// MonoGame content-pipeline asset; see <see cref="ProbeModelSource"/>). Each
/// instance's <see cref="Draw"/> world transform is supplied by the caller:
/// translated to the probe's real position minus the camera eye (a probe is
/// static, so there is no per-instance rotation — the mesh's authored upright
/// is correct, unlike a flying missile whose nose must swing onto its heading).
/// </summary>
/// <remarks>
/// <para><b>Solid, lit — no texture.</b> P233 did not decode probe textures and
/// this task scopes texture-mapping out, so the mesh renders as a solid,
/// lit surface (BasicEffect <c>EnableDefaultLighting</c> + a per-instance
/// <see cref="DiffuseColor"/> side tint), using the mesh's real per-vertex
/// normals. The .mdl texcoords are carried but unused (set to 0).</para>
/// <para><b>Winding/culling — the P290 empirical finding.</b> Unlike the missile
/// mesh (<c>mis05.mdl</c>, which P234 measured to need
/// <see cref="CullMode.CullClockwiseFace"/> to avoid interior holes), the real
/// probe mesh <c>utl02.mdl</c> is wound so that MonoGame's <b>default</b>
/// back-face culling (<see cref="CullMode.CullCounterClockwiseFace"/>) keeps
/// the near faces and renders the fuller silhouette — measured offscreen:
/// CCW cull 21,819 B vs CW cull 21,671 B vs no cull 21,820 B on the same
/// frame (larger PNG = more lit pixels), i.e. the probe's front faces are
/// counter-clockwise and <c>CullClockwiseFace</c> would wrongly cull them.
/// The renderer therefore uses the default cull (no special flip), and the raw
/// file indices are kept intact. Different probe <c>.mdl</c>s may wind
/// differently, but the shared single-model scope renders this real
/// representative mesh correctly.</para>
/// <para><b>Shared buffer:</b> all meshes from the parsed file are concatenated
/// into one vertex/index buffer (offsets carried across), so a single
/// <see cref="DrawIndexedPrimitives"/> per probe instance suffices. If a file
/// has multiple chunks (e.g. utl02's antenna), they all compose one model.</para>
/// <para><b>Side tint:</b> unlike the missile renderer (one fixed colour) a
/// probe's owning side is on the wire (<see cref="ClientCore.State.ProbeEntity.SideId"/>),
/// so each instance is tinted friendly/enemy via the caller — the same
/// side-colour convention StationRenderer uses.</para>
/// </remarks>
public sealed class ProbeRenderer : IDisposable
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
    public ProbeRenderer(GraphicsDevice gd, IReadOnlyList<MdlMesh> meshes)
    {
        int vertexTotal = 0, indexTotal = 0;
        foreach (var m in meshes)
        {
            vertexTotal += m.VertexCount;
            indexTotal += m.Indices.Length;
        }
        if (vertexTotal == 0 || indexTotal == 0)
            throw new ArgumentException("probe model has no geometry");

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

        _effect = new BasicEffect(gd)
        {
            // Ambient tuned so the dark side of the surface (turned away from
            // the key light) still reads against the starfield, like the
            // station renderer.
            AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f),
            TextureEnabled = false,
        };

        // P290 winding finding: the probe mesh (utl02.mdl) is wound for
        // MonoGame's DEFAULT back-face culling (CullCounterClockwiseFace) —
        // measured offscreen that culls nothing real vs CW cull shrinking the
        // silhouette (see the class doc). So no special flip is applied here,
        // unlike the missile renderer (whose mis05.mdl needed CW culling).
        _rasterizer = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
    }

    /// <summary>Draw the probe at the given world transform (translated to the
    /// probe's real position minus the camera eye), tinted <paramref name="tint"/>
    /// (friendly/enemy side colour). P530 — <paramref name="lighting"/> is the
    /// current sector's native bidirectional lighting; when supplied the shared
    /// <see cref="SectorLightingEffect"/> replaces the stock rig (key + blue
    /// back-light + 0.25 ambient, specular zero, third light off). When null the
    /// legacy stock rig is kept (back-compat / non-scene callers).</summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, Color tint, SectorLighting? lighting = null)
    {
        _effect.DiffuseColor = tint.ToVector3();
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        if (lighting is { } lp)
            SectorLightingEffect.Apply(_effect, lp);
        else
            _effect.EnableDefaultLighting();    // legacy stock rig (back-compat)
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        // P530 — explicit render state for the probe pass (the P529 discipline):
        // pin blend/depth/rasterizer/sampler so it never inherits a SpriteBatch
        // HUD leftover. The CCW-cull rasterizer is this model's P290 winding.
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _rasterizer;
        gd.SamplerStates[0] = SamplerState.LinearClamp;
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
