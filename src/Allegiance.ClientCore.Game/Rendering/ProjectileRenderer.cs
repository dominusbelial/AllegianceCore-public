using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws each live projectile as a shared procedural "bolt" — a thin elongated
/// box, generated once (no content-pipeline asset — the
/// <see cref="AsteroidRenderer"/>/<see cref="Starfield"/> "generated, not
/// loaded" model). Each instance's <see cref="Draw"/> world transform is
/// supplied by the caller: rotated so the box's long axis aligns with the
/// projectile's travel direction and scaled to the desired streak length/width,
/// translated to the extrapolated position minus the camera eye.
/// </summary>
/// <remarks>
/// <para><b>Why a box, not a flat quad.</b> A flat decal quad read edge-on
/// (when the travel direction lines up with the view) degenerates to an
/// invisible line; a thin box always has projected area, so it reads as a
/// bolt/streak from every angle. Native projectiles are actually textured
/// decals (<c>&lt;textureName&gt;bmp</c>, tinted white — see P465b), but the
/// port's per-type bolt textures are not imported (few exist in the Artwork
/// pack and native animates them over ~2 s), so a solid-colour procedural bolt
/// sized from the real per-instance velocity is what renders; see the P465b
/// report for why the flat tinted bolt stays.</para>
/// <para><b>Directional style only.</b> Native's <c>Directional</c> flag
/// distinguishes stretched-along-motion decals from flat circles; the wire
/// (<see cref="Wire.ProjectileInstance"/>) carries no such flag, so every
/// projectile renders as a directional streak — the only decal style that needs
/// no extra data.</para>
/// <para><b>Colour:</b> per-instance tint via <see cref="BasicEffect.DiffuseColor"/>
/// with vertex colours baked white. <see cref="Game1"/> supplies the per-type
/// tint/width resolved from the loaded static core by <c>ProjectileStyle.For</c>
/// (P465b) — real width/colour where the core carries them, else the amber
/// fallback (PCore001 carries no colour, so its bolts render amber).</para>
/// <para><b>Culling:</b> disabled (<see cref="CullMode.None"/>, like
/// <see cref="WarpRenderer"/>) so the bolt stays visible regardless of the
/// generated winding.</para>
/// </remarks>
public sealed class ProjectileRenderer : IDisposable
{
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;

    /// <param name="gd">The graphics device the buffers are created on (no
    /// content-pipeline asset — the bolt is generated, not loaded).</param>
    public ProjectileRenderer(GraphicsDevice gd)
    {
        // Unit box spanning [-0.5, +0.5]^3 centred at the origin; the long axis
        // is +Z (the direction the caller aligns to the projectile's travel).
        // Because the box is symmetric about the origin, the exact handedness of
        // the orientation is irrelevant — ±travel both land on the long axis.
        // Vertices are white so per-instance DiffuseColor provides the tint.
        var verts = new[]
        {
            new VertexPositionColor(new Vector3(-0.5f, -0.5f, -0.5f), Color.White),
            new VertexPositionColor(new Vector3( 0.5f, -0.5f, -0.5f), Color.White),
            new VertexPositionColor(new Vector3( 0.5f,  0.5f, -0.5f), Color.White),
            new VertexPositionColor(new Vector3(-0.5f,  0.5f, -0.5f), Color.White),
            new VertexPositionColor(new Vector3(-0.5f, -0.5f,  0.5f), Color.White),
            new VertexPositionColor(new Vector3( 0.5f, -0.5f,  0.5f), Color.White),
            new VertexPositionColor(new Vector3( 0.5f,  0.5f,  0.5f), Color.White),
            new VertexPositionColor(new Vector3(-0.5f,  0.5f,  0.5f), Color.White),
        };
        var indices = new uint[]
        {
            0, 3, 2,  0, 2, 1,    // z = -0.5
            5, 6, 7,  5, 7, 4,    // z = +0.5
            4, 7, 3,  4, 3, 0,    // x = -0.5
            1, 2, 6,  1, 6, 5,    // x = +0.5
            4, 5, 1,  4, 1, 0,    // y = -0.5
            3, 7, 6,  3, 6, 2,    // y = +0.5
        };

        _vertices = new VertexBuffer(gd, typeof(VertexPositionColor), verts.Length, BufferUsage.WriteOnly);
        _vertices.SetData(verts);

        _indices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
        _indices.SetData(indices);

        _triangleCount = indices.Length / 3;
        _effect = new BasicEffect(gd) { VertexColorEnabled = true };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
    }

    /// <summary>Draw the bolt at the given world transform (rotation to align the
    /// long axis with travel + scale to the streak length/width + camera-relative
    /// translation), tinted <paramref name="tint"/> (RGB in 0..1).</summary>
    public void Draw(Matrix world, Vector3 tint, Matrix view, Matrix projection)
    {
        _effect.World = world;
        _effect.DiffuseColor = tint;
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
