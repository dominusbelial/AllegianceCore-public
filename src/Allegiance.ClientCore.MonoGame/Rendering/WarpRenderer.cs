using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Draws the shared procedural warp funnel mesh once per warp instance. One
/// <see cref="VertexBuffer"/>/<see cref="IndexBuffer"/> is built from
/// <see cref="WarpMeshFactory"/>'s output in the constructor and re-used for
/// every warp — the "generated, not loaded" model (no content-pipeline asset),
/// exactly like <see cref="AsteroidRenderer"/>/<see cref="Starfield"/>.
/// </summary>
/// <remarks>
/// <para><b>Look:</b> a self-lit translucent energy vortex, not shaded geometry.
/// A <see cref="BasicEffect"/> with <c>VertexColorEnabled = true</c> passes the
/// factory's cool per-vertex gradient (deep-navy rim → mid-tone blue →
/// pale-cyan white-hot tip — P242; Domingo's correction that the aleph reads
/// bluish, not the tutorial's brown) straight to the rasterizer, so the vortex
/// needs no lighting and no texture. The draw runs under an <b>additive</b>
/// blend so its own layers glow/accumulate and the background stays visible
/// through it (a genuinely translucent energy field).</para>
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
    private readonly BlendState _additive;
    private double _elapsed;

    /// <summary>Angular swirl speed in radians/second about the forward axis.</summary>
    public const float SwirlSpeed = 0.9f;

    /// <summary>
    /// P237/P238 — the scale multiplier for the rendered warp funnel, applied
    /// by <see cref="Game1"/> when it composes each warp's world matrix (the real
    /// per-instance <c>DataWarpIGC.Radius</c> is multiplied by this before the
    /// uniform <c>CreateScale</c>). Its value is <b>not a second literal</b> — it
    /// is aliased to <see cref="Sector.WarpVisualScale"/> (9.5f), the single
    /// source of truth shared with the real gameplay triggers. P237 shipped it
    /// as a pure renderer constant; P238 promoted the number to
    /// <c>Sector.WarpVisualScale</c> and widened the real transit/warp-bomb
    /// trigger radii in <see cref="Sector"/> by the same factor, so the whole
    /// visible funnel is now functionally crossable/interactable and the two
    /// (visual + real trigger) cannot drift apart again.
    /// </summary>
    /// <remarks>
    /// <para><b>Calibration.</b> The shared funnel mesh has a <em>unit</em> rim
    /// radius (<see cref="WarpMeshFactory.DefaultRimRadius"/> = 1), so the rendered
    /// rim <b>radius</b> = <c>Radius × multiplier</c> and the rim <b>diameter</b> =
    /// <c>2 × Radius × multiplier</c>. P227 shipped the warp at the raw wire
    /// <c>Radius</c> (=100 for every warp in <c>m2xspiderw.igc</c>), i.e. a
    /// 200-unit-wide gate. Measuring the ACTUAL rendered model this client draws
    /// for every ship (the single <c>bios_scout_upscaled.fbx</c>, the P207 scope
    /// cut — there is no per-hull-type visual variety) gives a world-unit bounding
    /// box of <c>~190 × 102 × 179</c>, longest dimension ≈ 190 units. So at
    /// Radius=100 the warp's rim was only ~200 units wide — basically the length of
    /// the rendered ship, which is why it read as "very tiny" live. Domingo's
    /// accepted art-direction figure is that the warp should read at ~10x the size
    /// of a ship: rim diameter = 10 × 190 = 1900 units. The real radius is 100, so
    /// the multiplier is <c>1900 / (2 × 100) = 9.5</c> (exactly 9.996× the measured
    /// 190.065-unit ship, i.e. "10x" to all practical purposes). This is Domingo's
    /// own number, not derived from source (native's real visual warp size came from
    /// a separate authored 3D model this checkout lacks; <c>warpIGC.cpp:39</c> only
    /// sets the gameplay radius from wire data).</para>
    /// <para><b>Scope.</b> This <b>rendering</b> scale is the shared source of truth
    /// (<see cref="Sector.WarpVisualScale"/>), and P238 additionally widens the real
    /// transit/warp-bomb trigger thresholds in <see cref="Sector"/> by the same
    /// factor — so rendering and real trigger cannot drift apart. The wire
    /// <c>DataWarpIGC.Radius</c> value itself is never multiplied; only the consuming
    /// thresholds and the renderer's world matrix apply the scale.</para>
    /// </remarks>
    public const float VisualScaleMultiplier = Sector.WarpVisualScale;

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

        // Additive glow (P242): output += srcColor * srcAlpha, so the funnel's
        // self-lit bluish layers ACCUMULATE where they overlap — the drained
        // core of the vortex naturally burns hotter without one ring occluding
        // the one behind it — and whatever is BEHIND the warp (the starfield /
        // asteroids / other ships) stays visible, brightened rather than
        // painted over. This is the canonical real-time treatment for glowing
        // portal/vortex energy, and it pairs with the bluish palette to read
        // as translucent light. Same blend equation as MissileTrailRenderer's
        // soft-exhaust sprites.
        _additive = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            AlphaSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaDestinationBlend = Blend.One,
            ColorBlendFunction = BlendFunction.Add,
            AlphaBlendFunction = BlendFunction.Add,
        };
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

        // P242: render as a translucent, self-lit energy field. Additive blend
        // (so the funnel's own overlapping layers glow/accumulate and the
        // background shows through), DepthRead (test depth against the opaque
        // geometry that drew before us — asteroids/ships wrote real depth — so
        // solid objects in front correctly occlude the warp, but DON'T write
        // depth, so the warp's own spinning/swirled layers never self-occlude
        // or z-fight each other), and the P227 CullNone (hollow funnel shows
        // its inner wall).
        gd.BlendState = _additive;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.RasterizerState = _cullNone;
        gd.SetVertexBuffer(_vertices);
        gd.Indices = _indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);

        // Restore the caller's expectations — don't leak additive/depth-read/
        // cull-none into the renderers that run after this in Game1.Draw (the
        // opaque projectile/missile passes and the HUD sprite batch all assume
        // the device defaults). MissileTrailRenderer sets the precedent of
        // restoring BlendState after its own additive draw; we restore all
        // three states we touched so the frame is exactly as we found it.
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
        _additive.Dispose();
    }
}
