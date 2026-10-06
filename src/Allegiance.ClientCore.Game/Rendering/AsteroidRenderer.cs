using Allegiance.Core.Rendering;
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
        // P529 — mipmapped so minified rocks stop aliasing (colour shimmer /
        // jump as the camera or rock moves — a P529 cause). The shared
        // procedural diffuse gets the same full mip chain as the real surfaces.
        _texture = TextureMipmapBuilder.CreateMipmapped(gd, pixels, RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSize);

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

    /// <summary>
    /// Draw the shared rock mesh at the given world transform under the
    /// provided view/projection pair. The caller is responsible for baking
    /// the radius scale into <paramref name="world"/> (so bigger asteroids look
    /// bigger), as <see cref="Game1"/> does via
    /// <c>Matrix.CreateScale(ast.Radius)</c>. Uses the renderer's default
    /// procedural diffuse (the original shared gray-brown rock).
    /// </summary>
    public void Draw(Matrix world, Matrix view, Matrix projection)
        => Draw(world, view, projection, _texture, null);

    /// <summary>
    /// Draw with an explicit diffuse — P286's per-type colour variation: the
    /// caller passes the texture generated for an asteroid's resource type
    /// (<see cref="RockTextureFactory.GeneratePixels"/>) so different asteroids
    /// in a sector read differently, while the (imported or procedural) mesh
    /// geometry stays whatever this renderer was built with.
    /// </summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, Texture2D texture)
        => Draw(world, view, projection, texture, null);

    /// <summary>
    /// Draw the shared rock mesh under P529's native per-sector bidirectional
    /// lighting (<paramref name="lighting"/>, from
    /// <see cref="SectorLighting.FromCluster"/>), or — when null — the legacy
    /// stock <c>EnableDefaultLighting</c> rig (kept for the old-vs-new contact
    /// sheets in the P529 proof and for back-compat). Sets its own explicit
    /// render state so the pass never inherits a SpriteBatch HUD leftover.
    /// </summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, Texture2D texture, SectorLighting? lighting)
    {
        _effect.Texture = texture;
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;

        var gd = _effect.GraphicsDevice;
        // P529 — explicit render state for the asteroid pass. The previous draw
        // is routinely a SpriteBatch HUD pass that leaves LinearClamp /
        // AlphaBlend / DepthStencilState.None behind, so pin what this pass
        // needs: opaque blending, real depth, wrap + mip-filtered sampling (the
        // rock UVs wrap, and the P529 mipmaps need filtered minification), and
        // the back-face culling the CCW mesh expects. Nothing is restored — the
        // next passes set their own (the P339/P525 discipline).
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        gd.SamplerStates[0] = SamplerState.LinearWrap;

        if (lighting is { } lp)
            ApplySectorLighting(lp);
        else
            _effect.EnableDefaultLighting();   // legacy stock rig (proof / back-compat)

        _effect.CurrentTechnique.Passes[0].Apply();

        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
    }

    /// <summary>
    /// P547 — depth-only pass: draw the shared rock mesh with the linear-depth
    /// effect so the ENHANCED smoke can fade at rock intersections. Same
    /// world transform and state discipline as <see cref="Draw(Matrix,Matrix,Matrix,Texture2D,SectorLighting?)"/>;
    /// only the effect (and its WorldView/WorldViewProj/Near/InvRange params,
    /// set from code) differs. The caller owns <paramref name="depthEffect"/>.
    /// </summary>
    public void DrawDepth(Matrix world, Matrix view, Matrix projection, Effect depthEffect, float near, float far)
    {
        depthEffect.Parameters["WorldView"].SetValue(world * view);
        depthEffect.Parameters["WorldViewProj"].SetValue(world * view * projection);
        depthEffect.Parameters["Near"].SetValue(near);
        depthEffect.Parameters["InvRange"].SetValue(LinearDepth.InvRange(near, far));
        depthEffect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
    }

    /// <summary>
    /// P529 + P530 — apply native per-sector bidirectional lighting as the two
    /// directional lights <c>BasicEffect</c> sums. Delegates to the shared
    /// <see cref="SectorLightingEffect"/> so every 3D scene renderer programs
    /// its effect through the one helper (key + blue back-light + 0.25 ambient,
    /// specular pinned zero on every light, stock third light off).
    /// </summary>
    private void ApplySectorLighting(SectorLighting lp)
        => SectorLightingEffect.Apply(_effect, lp);

    /// <summary>
    /// P286 — which of the imported mesh variants an asteroid uses, chosen from
    /// its stable <c>AsteroidId</c> so different asteroids in a sector visibly
    /// vary in silhouette (drgrock/tacrock/bgrnd05), not just colour. Guarantees
    /// a healthy spread regardless of the wire <c>ModelName</c> (the corpus
    /// ships many asteroids sharing one bgrndNN name).
    /// </summary>
    /// <param name="asteroidId">The asteroid's <c>DataAsteroidIGC.AsteroidId</c>.</param>
    /// <returns>A mesh index 0..2 selecting drgrock/tacrock/bgrnd05.</returns>
    public static int MeshVariantFor(int asteroidId)
    {
        int m = asteroidId % 3;
        return m < 0 ? m + 3 : m;   // AsteroidId is a short, never negative, but be safe
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
        _texture.Dispose();
    }
}
