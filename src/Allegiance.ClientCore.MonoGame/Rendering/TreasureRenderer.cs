using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P293 — draws every live treasure as the shared procedural gold-gem
/// placeholder (see <see cref="TreasureMeshFactory"/>). One
/// <see cref="VertexBuffer"/>/<see cref="IndexBuffer"/> is built from the
/// factory's output in the constructor and re-used for every treasure — the
/// "generated, not loaded" model (no content-pipeline asset), exactly like
/// <see cref="WarpRenderer"/> / <see cref="AsteroidRenderer"/>.
/// </summary>
/// <remarks>
/// <para><b>Look:</b> a self-lit solid golden gem, not shaded geometry. A
/// <see cref="BasicEffect"/> with <c>VertexColorEnabled = true</c> passes the
/// factory's per-vertex gold gradient straight to the rasterizer (no lighting,
/// no texture). Unlike the translucent warp/mine/chaff glows, a treasure is a
/// physical pickup object, so it draws opaque and writes depth (solid objects
/// occlude correctly against the ships/asteroids that drew before it).</para>
/// <para><b>Slow spin:</b> native imparts a random orientation <em>and</em> a
/// random rotation to a non-flag treasure
/// (<c>treasureIGC.cpp:180-190</c> <c>r.angle(random(-6,6))</c>), so the gem
/// should tumble rather than sit frozen. <see cref="Draw"/> accumulates real
/// elapsed time and applies a gentle continuous rotation about local Y (the
/// gem's long axis) before the caller's world transform, matching the
/// <see cref="WarpRenderer"/> continuous-spin precedent.</para>
/// <para><b>Scale:</b> the caller bakes the real per-instance scale into
/// <paramref name="world"/> via <c>Matrix.CreateScale(<see cref="DefaultRadius"/>)</c>,
/// where <see cref="DefaultRadius"/> is the native treasure pickup radius 5.0
/// (<c>treasureIGC.cpp:193</c>) — the only size the decoded data/native
/// carries (the gem's unit mesh is scaled to that so it is legible among the
/// established ~10–20-unit small-object display scale of mines/chaff).</para>
/// </remarks>
public sealed class TreasureRenderer : IDisposable
{
    /// <summary>
    /// World-space radius the unit gem is drawn at — the native treasure
    /// collision/pickup radius <c>SetRadius(5.0f)</c>
    /// (<c>treasureIGC.cpp:193</c>, <c>//NYI -- needs to be gotten from the
    /// DB</c>). The only size a treasure carries; the gem's unit mesh is
    /// uniformly scaled by this.
    /// </summary>
    public const float DefaultRadius = 5.0f;

    /// <summary>Gentle tumble rate (rad/s) about the gem's long (local Y) axis.</summary>
    public const float SpinSpeed = 0.7f;

    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;
    private double _elapsed;

    /// <param name="gd">The graphics device the buffers are created on (no
    /// content-pipeline asset — the gem is generated, not loaded).</param>
    /// <param name="mesh">The shared mesh from <see cref="TreasureMeshFactory"/>.
    /// Null to build the default gold gem.</param>
    public TreasureRenderer(GraphicsDevice gd, TreasureMesh? mesh = null)
    {
        mesh ??= TreasureMeshFactory.Generate();

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
    /// Draw the shared gem mesh at the given world transform under the provided
    /// view/projection pair, spinning it gently about local Y by the accumulated
    /// real time. The caller bakes the real per-instance radius scale into
    /// <paramref name="world"/> (via <c>Matrix.CreateScale(TreasureRenderer.DefaultRadius)</c>).
    /// </summary>
    /// <param name="deltaSeconds">Wall-clock seconds since the previous frame;
    /// accumulated internally so the spin is continuous, never restarted by a
    /// per-frame one-shot.</param>
    public void Draw(Matrix world, Matrix view, Matrix projection, double deltaSeconds)
    {
        _elapsed += Math.Max(0.0, deltaSeconds);
        float angle = (float)(_elapsed * SpinSpeed);

        _effect.World = Matrix.CreateRotationY(angle) * world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;

        // Opaque solid + DepthRead with culling off: the gem writes depth so
        // nearby ships correctly occlude it, and culling is off so winding
        // never hides a facet of the convex self-lit gem. Restore all three
        // device states afterwards so we don't leak into later passes.
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.RasterizerState = _cullNone;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);

        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
        _cullNone.Dispose();
    }
}
