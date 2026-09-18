using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the shared procedural asteroid rock mesh once per asteroid instance.
/// One <see cref="VertexBuffer"/>/<see cref="IndexBuffer"/> is built from
/// <see cref="RockMeshFactory"/>'s output in the constructor and re-used for
/// every rock — exactly the "no content pipeline asset needed, rocks are
/// generated, not loaded" model <see cref="Starfield"/>'s constructor documents
/// for stars. Each instance supplies its own world transform (position +
/// orientation + radius scale), and lighting is the built-in
/// <see cref="BasicEffect"/> default for cheap per-vertex-normal shading, the
/// same setup <see cref="ShipRenderer.Draw"/> uses for ships.
/// </summary>
/// <remarks>
/// Unlike <see cref="ShipRenderer"/> this does NOT need the Meshy/Blender
/// FBX-export special cases (<c>NoseAlignmentCorrection</c> / <c>CullNone</c>):
/// the procedural mesh is generated here with controlled winding, so normal
/// back-face culling (the MonoGame default) is correct and kept.
/// </remarks>
public sealed class AsteroidRenderer : IDisposable
{
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;
    private readonly Texture2D _texture;

    /// <summary>Flat gray-brown rock tint, modulated by the default light.
    /// Kept as a named constant for readability, though since P253 the actual
    /// gray-brown colour lives <em>baked into</em> the shared procedural diffuse
    /// (see <see cref="RockTextureFactory"/>), so the effect's diffuse is
    /// white (identity) to avoid double-darkening the texture.</summary>
    private static readonly Vector3 RockColor = new(0.52f, 0.46f, 0.40f);

    /// <param name="gd">The graphics device the buffers are created on. Like
    /// <see cref="Starfield"/>, no content-pipeline asset is needed — the rock
    /// mesh and its diffuse texture are generated, not loaded.</param>
    /// <param name="mesh">The shared mesh from <see cref="RockMeshFactory"/>.
    /// Null to build the default procedural rock.</param>
    public AsteroidRenderer(GraphicsDevice gd, RockMesh? mesh = null)
    {
        mesh ??= RockMeshFactory.Generate();

        var verts = new VertexPositionNormalTexture[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            verts[i] = new VertexPositionNormalTexture(
                new Vector3(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]),
                new Vector3(mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2]),
                // P253 — real per-vertex UVs (equirectangular, from RockMeshFactory)
                // instead of the old all-Vector2.Zero, so the rock's surface
                // actually maps onto the generated diffuse below.
                new Vector2(mesh.Uvs[i * 2], mesh.Uvs[i * 2 + 1]));
        }

        _vertices = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), mesh.VertexCount, BufferUsage.WriteOnly);
        _vertices.SetData(verts);

        _indices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, mesh.Indices.Length, BufferUsage.WriteOnly);
        _indices.SetData(mesh.Indices);

        _triangleCount = mesh.TriangleCount;

        // P253 — one shared procedural rock diffuse for every asteroid instance
        // (generated, not loaded — the same discipline as the mesh). Built here
        // once from RockTextureFactory's pure pixel buffer and re-used for all
        // rocks, so no per-asteroid allocation in the draw loop.
        var pixels = RockTextureFactory.GeneratePixels();
        _texture = new Texture2D(gd, RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSize);
        _texture.SetData(pixels);

        _effect = new BasicEffect(gd)
        {
            // DiffuseColor is WHITE (identity) now that the gray-brown tone is
            // baked into the texture: BasicEffect multiplies the lit color by
            // the sampled texel, so leaving RockColor here would darken the
            // already-RockColor-toned texture to ~half its intended brightness
            // (Texture × Diffuse). The mottled rock color comes from the
            // texture; lighting from EnableDefaultLighting below.
            DiffuseColor = Color.White.ToVector3(),
            AmbientLightColor = new Vector3(0.25f, 0.25f, 0.28f),
            TextureEnabled = true,
            Texture = _texture,
        };
    }

    /// <summary>Draw the shared rock mesh at the given world transform under
    /// the provided view/projection pair. The caller is responsible for baking
    /// the radius scale into <paramref name="world"/> (so bigger asteroids look
    /// bigger), as <see cref="Game1"/> does via
    /// <c>Matrix.CreateScale(ast.Radius)</c>.</summary>
    public void Draw(Matrix world, Matrix view, Matrix projection)
    {
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.EnableDefaultLighting();
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
        _texture.Dispose();
    }
}
