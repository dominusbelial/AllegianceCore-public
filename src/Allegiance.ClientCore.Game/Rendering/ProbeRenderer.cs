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
/// <para><b>Materials.</b> Each type's native mesh retains its texture scopes.
/// Textured parts use white tint and optionally the shared atlas PBR service;
/// missing textures retain the lit-solid, side-tinted fallback.</para>
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
/// <para><b>Shared buffers.</b> Meshes are concatenated into one vertex/index
/// buffer per probe type, with a draw range for each mesh's texture scope.</para>
/// </remarks>
public sealed class ProbeRenderer : IDisposable
{
    private readonly string _modelStem;
    private readonly string? _textureStem;
    public float MeshRadius { get; }
    private readonly HdMaterialRenderer? _hd;
    private readonly NativeMaterialParts _materials;
    private readonly Assets.IAssetSource? _assets;
    private readonly Dictionary<string, Texture2D> _partTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly int _triangleCount;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _rasterizer;
    private readonly Texture2D? _texture;

    /// <summary>
    /// Construct from a parsed <see cref="MdlFile"/>'s meshes.
    /// </summary>
    /// <param name="gd">The graphics device the buffers are created on.</param>
    /// <param name="meshes">The parsed mesh list (all of <c>MdlFile.Meshes</c>,
    /// concatenated into one buffer).</param>
    /// <param name="texture">P615 — the probe's decoded native texture
    /// (<c>utl02bmp</c> for the canonical <c>utl02</c> demo, resolved via
    /// <see cref="ProbeModelSource.ResolveTextureStemFor"/>). Null keeps the
    /// legacy lit-solid side-tinted fallback (never an invisible probe).</param>
    public ProbeRenderer(GraphicsDevice gd, IReadOnlyList<MdlMesh> meshes, Texture2D? texture = null, HdMaterialRenderer? hd = null, Assets.IAssetSource? assets = null, string modelStem = ProbeModelSource.DemoModelStem, string? textureStem = ProbeModelSource.DemoTextureStem)
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

        _hd = hd; _assets = assets; _modelStem = modelStem; _textureStem = textureStem;
        MeshRadius = NativeShipResolver.MergedRadius(meshes);
        _materials = new(meshes, verts, indices);
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
            // P615 — when a native probe texture resolves it is bound; the
            // lit-solid fallback (null texture) keeps texturing off.
            TextureEnabled = texture is not null,
        };

        _texture = texture;

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
        // P615 — native paints the probe from its own texture (probeigc.cpp:
        // 112-113; there is NO per-side team tint on the probe mesh in native,
        // team is shown via icons — the same no-tint rule the native station
        // path follows). So when the real texture is bound, DiffuseColor is
        // white so the texture shows its true colours; the friendly/enemy
        // side tint is used only by the lit-solid fallback.
        if (_texture is not null)
            _effect.DiffuseColor = Color.White.ToVector3();
        else
            _effect.DiffuseColor = tint.ToVector3();
        _effect.Texture = _texture;
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
        if (_hd is not null)
            _materials.Draw(gd, _hd, _modelStem, _textureStem,
                _texture, TextureFor, _effect, _vertices, _indices, world, view, projection, 1, lighting);
        else
        {
            gd.SetVertexBuffer(_vertices); gd.Indices = _indices;
            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _triangleCount);
        }
    }

    private Texture2D? TextureFor(string stem)
    {
        if (_partTextures.TryGetValue(stem, out var texture)) return texture;
        if (stem.Equals(_textureStem, StringComparison.OrdinalIgnoreCase)) return _texture;
        if (_assets is null) return null;
        try
        {
            var decoded = MdlTextureDecoder.Decode(Assets.AssetSources.ReadAllBytes(_assets, stem + ".mdl"));
            texture = new Texture2D(_effect.GraphicsDevice, decoded.Width, decoded.Height);
            texture.SetData(MdlTextureDecoder.ToPremultipliedRgba(decoded));
            _partTextures.Add(stem, texture); return texture;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return null; }
    }

    public void Dispose()
    {
        _materials.Dispose();
        foreach (var t in _partTextures.Values) t.Dispose();
        _vertices.Dispose();
        _indices.Dispose();
        _effect.Dispose();
        _rasterizer.Dispose();
        _texture?.Dispose();
    }

    /// <summary>
    /// P615 — true when the shared probe mesh's native texture unit resolves
    /// in the artwork (the demo model <c>utl02</c>'s own imported namespace
    /// <c>utl02bmp</c>). The renderer binds it, so a live probe is textured.
    /// Pre-P615 the probe drew LIT-SOLID by design (<c>TextureEnabled=false</c>)
    /// even though the texture resolved — the audit blind spot this closes —
    /// exactly the <see cref="HullHitSparkRenderer.BitsTextured"/> contract
    /// P609 added for the hull-hit bit.
    /// </summary>
    public static bool AppliesTexture(Func<string, bool> fileExists) =>
        fileExists(ProbeModelSource.DemoTextureStem);
}
