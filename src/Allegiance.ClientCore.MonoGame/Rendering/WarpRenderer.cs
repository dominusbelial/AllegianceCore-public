using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the shared procedural warp funnel mesh once per warp instance. One
/// <see cref="VertexBuffer"/>/<see cref="IndexBuffer"/> is built from
/// <see cref="WarpMeshFactory"/>'s output in the constructor and re-used for
/// every warp — the "generated, not loaded" model (no content-pipeline asset),
/// exactly like <see cref="AsteroidRenderer"/>/<see cref="Starfield"/>.
/// </summary>
/// <remarks>
/// <para><b>Look:</b> a self-lit energy vortex, not shaded geometry. A
/// <see cref="BasicEffect"/> with <c>VertexColorEnabled = true</c> passes the
/// factory's warm per-vertex gradient (dark brown rim → amber → white-hot tip)
/// straight to the rasterizer, so the "swirling brown vortex with a bright
/// center" (<c>training/mission4.cpp:494</c>) needs no lighting and no
/// texture.</para>
/// <para><b>Constant swirl:</b> Domingo's explicit direction is that the
/// vortex must <i>constantly</i> swirl, not sit static. <see cref="Draw"/>
/// accumulates real elapsed time internally and applies a continuous rotation
/// about the mesh's local forward axis (+Z) BEFORE the instance orientation,
/// so the whole funnel spins around the warp's own forward axis forever. The
/// baked-in static helix twist (see <see cref="WarpMeshFactory"/>) plus this
/// animated spin reads unmistakably as a drain/vortex.</para>
/// <para><b>Both faces:</b> the funnel is hollow — a viewer on the rim side
/// must see its inner wall and a viewer on the tip side its outer wall. Rather
/// than emit doubled winding in the factory, the draw disables back-face
/// culling (this is a translucent-looking energy field, not solid rock, so
/// the <see cref="AsteroidRenderer"/>'s "keep normal culling" argument does
/// not apply).</para>
/// </remarks>
public sealed class WarpRenderer : IDisposable
{
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;
    private double _elapsed;

    /// <summary>Angular swirl speed in radians/second about the forward axis.</summary>
    public const float SwirlSpeed = 0.9f;

    /// <param name="gd">The graphics device the buffers are created on (no
    /// content-pipeline asset — the warp is generated, not loaded).</param>
    /// <param name="mesh">The shared mesh from <see cref="WarpMeshFactory"/>.
    /// Null to build the default procedural warp.</param>
    public WarpRenderer(GraphicsDevice gd, WarpMesh? mesh = null)
    {
        mesh ??= WarpMeshFactory.Generate();

        var verts = new VertexPositionColor[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            verts[i] = new VertexPositionColor(
                new Vector3(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]),
                new Color(mesh.Colors[i * 4], mesh.Colors[i * 4 + 1], mesh.Colors[i * 4 + 2], mesh.Colors[i * 4 + 3]));
        }

        _vertices = new VertexBuffer(gd, typeof(VertexPositionColor), mesh.VertexCount, BufferUsage.WriteOnly);
        _vertices.SetData(verts);

        _indices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, mesh.Indices.Length, BufferUsage.WriteOnly);
        _indices.SetData(mesh.Indices);

        _triangleCount = mesh.TriangleCount;
        _effect = new BasicEffect(gd) { VertexColorEnabled = true };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
    }

    /// <summary>
    /// Draw the shared warp mesh at the given world transform under the
    /// provided view/projection pair, spinning it continuously about the warp's
    /// forward axis by the accumulated real time. The caller bakes the real
    /// per-instance radius scale into <paramref name="world"/> (via
    /// <c>Matrix.CreateScale(warp.Radius)</c>) exactly as
    /// <see cref="Game1"/> does for asteroids — so a warp's rim diameter equals
    /// its real <c>DataWarpIGC.Radius</c> in world units (Domingo: warps are
    /// "very large, many ships could cross at the same time").
    /// </summary>
    /// <param name="deltaSeconds">Wall-clock seconds since the previous frame;
    /// accumulated internally so the swirl is continuous, never restarted by a
    /// per-frame one-shot.</param>
    public void Draw(Matrix world, Matrix view, Matrix projection, double deltaSeconds)
    {
        _elapsed += Math.Max(0.0, deltaSeconds);
        float angle = (float)(_elapsed * SwirlSpeed);

        // Swirl about local +Z (the mesh's forward axis) is applied first, then
        // the instance orientation rotates that spun frame into the world.
        _effect.World = Matrix.CreateRotationZ(angle) * world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.RasterizerState = _cullNone;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
        _cullNone.Dispose();
    }
}
