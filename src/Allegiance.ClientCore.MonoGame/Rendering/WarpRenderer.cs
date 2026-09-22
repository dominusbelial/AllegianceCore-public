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
/// factory's cool per-vertex gradient (deep-navy rim → mid-tone blue → pale-cyan
/// white-hot center — P258 reshaped the mesh into a symmetric hourglass whose
/// bright point is at the CENTER pinch, with dark wide rims at both ends; the
/// P242 bluish hue is kept) straight to the rasterizer, so the vortex
/// needs no lighting and no texture. The draw runs under an <b>additive</b>
/// blend so its own layers glow/accumulate and the background stays visible
/// through it (a genuinely translucent energy field).</para>
/// <para><b>Constant swirl:</b> Domingo's explicit direction is that the
/// vortex must <i>constantly</i> swirl, not sit static. <see cref="Draw"/>
/// accumulates real elapsed time internally and applies a continuous rotation
/// about the mesh's local forward axis (+Z) BEFORE the instance orientation,
/// so the whole hourglass spins around the warp's own forward axis forever. The
/// baked-in static helix twist (see <see cref="WarpMeshFactory"/>) plus this
/// animated spin reads unmistakably as a drain/vortex.</para>
/// <para><b>Both faces:</b> the hourglass is hollow — a viewer approaching
/// from either side must see the far wall of the funnel converge to the bright
/// center. Rather than emit doubled winding in the factory, the draw disables
/// back-face culling (this is a translucent-looking energy field, not solid
/// rock, so the <see cref="AsteroidRenderer"/>'s "keep normal culling"
/// argument does not apply).</para>
/// </remarks>
public sealed class WarpRenderer : IDisposable
{
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly VertexBuffer _coreVertices;
    private readonly IndexBuffer _coreIndices;
    private readonly int _coreTriangleCount;
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
    /// is aliased to <see cref="Sector.WarpVisualScale"/>, the single source
    /// of truth this constant shares with the real gameplay code. P237
    /// shipped it as a pure renderer constant; P238 promoted the number to
    /// <c>Sector.WarpVisualScale</c> and widened the real transit/warp-bomb
    /// trigger radii in <see cref="Sector"/> by the same factor, so the whole
    /// visible funnel was (at that point) functionally crossable/
    /// interactable. ⚠️ <b>P244 later re-decoupled the real trigger back to a
    /// tight raw-radius collision</b> (matching native), and <b>P247 shrunk
    /// this render-only value to 2.5x</b> (from P237/P238's original 9.5x) to
    /// close the gap that reopened — see <see cref="Sector.WarpVisualScale"/>'s
    /// own doc comment for the live-flight-tested reasoning and Domingo's
    /// explicit choice between the two real fix directions.
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
    /// the rendered ship, which is why it read as "very tiny" live. P237/P238
    /// originally set this to Domingo's then-accepted art-direction figure of ~10x
    /// a ship's size (multiplier 9.5, rim diameter 1900) — not derived from native
    /// source (its real visual warp size came from a separate authored 3D model
    /// this checkout lacks; <c>warpIGC.cpp:39</c> only sets the gameplay radius
    /// from wire data). <b>P247 corrected this to 2.5x</b> (rim diameter ≈ 500,
    /// ≈2.6× the ship) after live flight-testing showed the 9.5x scale made most
    /// of what looked crossable not actually be — see
    /// <see cref="Sector.WarpVisualScale"/>'s doc comment.</para>
    /// <para><b>Scope.</b> This <b>rendering</b> scale is the shared source of truth
    /// (<see cref="Sector.WarpVisualScale"/>). P238 had additionally widened the real
    /// transit/warp-bomb trigger thresholds in <see cref="Sector"/> by the same
    /// factor so rendering and real trigger couldn't drift apart; <b>P244
    /// re-decoupled them</b> (the real trigger is now a tight raw-radius collision,
    /// matching native, independent of this render-only constant). The wire
    /// <c>DataWarpIGC.Radius</c> value itself is never multiplied; only this
    /// renderer's world matrix applies the scale.</para>
    /// </remarks>
    public const float VisualScaleMultiplier = Sector.WarpVisualScale;

    /// <summary>
    /// P341 — the camera-facing glow-disc core's radius as a FRACTION of the
    /// funnel's rim radius (rim radius = Radius × <see cref="VisualScaleMultiplier"/>
    /// in world units). 1.0 sizes the core disc to exactly the funnel's rim, per
    /// the kimi-k3 consultation's "sized to roughly the rim radius ×
    /// WarpVisualScale" instruction — the glow fills the gate mouth so it reads
    /// as a bright portal opening, not a small dot inside it.
    /// </summary>
    public const float CoreRadiusFraction = 1.0f;

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

        // P341 — the camera-facing glow-disc core (WarpCoreMeshFactory: a flat
        // radial-gradient disc). Built once and reused for every warp, like the
        // funnel. The billboard orientation that keeps it facing the camera is
        // applied per-draw in Draw() — the mesh itself is a plain +Z-facing fan.
        var core = WarpCoreMeshFactory.Generate();
        _coreVertices = BuildVertexBuffer(gd, core);
        _coreIndices = BuildIndexBuffer(gd, core);
        _coreTriangleCount = core.TriangleCount;

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

        // P341 — Layer 1: the camera-facing glow-disc core, drawn on top of the
        // funnel (see DrawCore for the billboard technique). The funnel's hollow
        // hourglass has no angle-independent silhouette; this round disc is the
        // warp's "bright spot in the center" and carries the coherent read from
        // every approach angle.
        DrawCore(world, view, projection);

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

    /// <summary>
    /// P341 — draw ONLY the camera-facing glow-disc core at the given world
    /// transform. The disc is billboarded so it always faces the camera —
    /// identical apparent size/brightness from every approach angle, the whole
    /// point of the redesign. Public so a caller can draw the core in isolation
    /// (e.g. an isolated live-proof that the disc stays round from any angle);
    /// <see cref="Draw"/> uses it as the last pass after the funnel.
    /// </summary>
    /// <remarks>
    /// <para><b>Billboard technique.</b> <paramref name="world"/> is the funnel's
    /// world transform — <c>wRot * CreateScale(s) * CreateTranslation(relPos)</c>:
    /// per-instance orientation, uniform radius scale, and camera-relative
    /// translation (P207 floating-origin rule — the camera sits at the origin of
    /// the camera-relative world). The core deliberately IGNORES the funnel's
    /// swirl/orientation (wRot): it re-orients itself to face the camera. The
    /// unit direction from the warp toward the camera is <c>-relPos</c>; the
    /// camera's world-space UP is recovered from the view matrix's rotation block
    /// (column 2 = the camera's up in world space, under XNA's row-major
    /// row-vector convention) — the standard billboard-from-view-basis technique,
    /// which respects any camera roll.</para>
    /// <para>Self-contained: sets and restores the additive / depth-read /
    /// cull-none state it needs, so it is safe to call standalone or from
    /// <see cref="Draw"/>.</para>
    /// </remarks>
    public void DrawCore(Matrix world, Matrix view, Matrix projection)
    {
        float coreScale = CoreRadiusFraction
                          * MathF.Sqrt(world.M11 * world.M11 + world.M21 * world.M21 + world.M31 * world.M31);
        var relPos = new Vector3(world.M41, world.M42, world.M43);

        // Unit direction from the warp toward the camera (camera = origin of the
        // camera-relative world), so the disc normal points at the viewer.
        Vector3 toCam = -relPos;
        if (toCam.LengthSquared() < 1e-12f)
            toCam = Vector3.Backward;                       // camera exactly on the warp — arbitrary facing
        toCam = Vector3.Normalize(toCam);

        // The camera's world-space UP, recovered from the view matrix (column 2
        // of the rotation block = the camera's up in world space).
        var camUp = new Vector3(view.M12, view.M22, view.M32);

        // Orthonormal right-handed basis with forward = toCam: right = up × toCam,
        // up = toCam × right (so right × up == toCam). Guard the degenerate case
        // (camera looking straight down the disc axis ⇒ camUp ∥ toCam) with a
        // fixed fallback so the cross product never collapses to zero.
        Vector3 right = Vector3.Cross(camUp, toCam);
        if (right.LengthSquared() < 1e-8f)
            right = Vector3.Cross(Vector3.UnitY, toCam);
        right = Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(toCam, right);
        up = Vector3.Normalize(up);

        var billboard = new Matrix(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            toCam.X, toCam.Y, toCam.Z, 0f,
            0f, 0f, 0f, 1f);

        _effect.World = billboard * Matrix.CreateScale(coreScale) * Matrix.CreateTranslation(relPos);
        _effect.View = view;
        _effect.Projection = projection;
        _effect.CurrentTechnique.Passes[0].Apply();

        var gd = _effect.GraphicsDevice;
        gd.BlendState = _additive;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.RasterizerState = _cullNone;
        gd.SetVertexBuffer(_coreVertices);
        gd.Indices = _coreIndices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _coreTriangleCount);

        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _coreVertices.Dispose();
        _coreIndices.Dispose();
        _effect.Dispose();
        _cullNone.Dispose();
        _additive.Dispose();
    }

    /// <summary>Build a <see cref="VertexPositionColor"/> buffer from a pure-logic
    /// <see cref="WarpMesh"/> (shared by the funnel and the P341 glow-disc core).</summary>
    private static VertexBuffer BuildVertexBuffer(GraphicsDevice gd, WarpMesh mesh)
    {
        var verts = new VertexPositionColor[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            verts[i] = new VertexPositionColor(
                new Vector3(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]),
                new Color(mesh.Colors[i * 4], mesh.Colors[i * 4 + 1], mesh.Colors[i * 4 + 2], mesh.Colors[i * 4 + 3]));
        }

        var vb = new VertexBuffer(gd, typeof(VertexPositionColor), mesh.VertexCount, BufferUsage.WriteOnly);
        vb.SetData(verts);
        return vb;
    }

    /// <summary>Build an <see cref="IndexBuffer"/> from a pure-logic
    /// <see cref="WarpMesh"/> (shared by the funnel and the P341 glow-disc core).</summary>
    private static IndexBuffer BuildIndexBuffer(GraphicsDevice gd, WarpMesh mesh)
    {
        var ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, mesh.Indices.Length, BufferUsage.WriteOnly);
        ib.SetData(mesh.Indices);
        return ib;
    }
}
