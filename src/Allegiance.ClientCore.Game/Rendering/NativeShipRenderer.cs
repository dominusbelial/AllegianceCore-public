using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P566 — renders a hull from its ORIGINAL native <c>.mdl</c> mesh + native
/// texture, parsed at runtime from the artwork source (the same runtime-asset
/// convention as <see cref="ProjectileModelRenderer"/> / <see cref="MissileRenderer"/>).
/// <see cref="NativeShipResolver"/> holds the pure rules (winding correction, native
/// scale, texture-stem resolution); this class owns the GPU geometry/textures and the
/// draw pass.
///
/// <para><b>Winding / culling.</b> The native index stream is DirectX
/// clockwise-front; <see cref="NativeShipResolver.CorrectWinding"/> reverses each
/// triangle so it is MonoGame counter-clockwise-front, which lets this pass use
/// REAL front-face culling (<see cref="RasterizerState.CullCounterClockwise"/>)
/// instead of the Meshy path's <c>CullNone</c>. Culling back faces is what fixes
/// the reported per-ship transparent/missing parts and flat-shading self-occlusion
/// artefacts (a double-drawn shell overdraws/blends incorrectly).</para>
///
/// <para><b>Scale / orientation.</b> Each draw folds in the native uniform scale
/// <c>m_scale = (0.5·Length)/meshRadius</c> (<see cref="NativeShipResolver.NativeScaleForLength"/>)
/// and native's <c>RY(π)</c> mesh flip (<c>m_bFlip</c>, thinggeo.cpp:1501-1503)
/// before the caller's world matrix, exactly mirroring native's
/// <c>Orient · Scale · RY(π)</c> chain (thinggeo.cpp:1484-1503, shipIGC.cpp:1419).</para>
///
/// <para><b>Lighting.</b> P530 sector lighting is applied with the native
/// bidirectional ambient (0.25) — but P800 lifts the result to the shared
/// <see cref="ShipRenderer.ShipAmbientFloor"/>: the same floor the Meshy path
/// (<see cref="ShipRenderer.Draw"/>) and the HD/PBR path
/// (<see cref="HdMaterialRenderer"/>) already apply. Measured (P800): the
/// darkest native hull texture (the stealth bomber's <c>sbbmp</c>) averages
/// RGB (13,7,4) — at pure 0.25 ambient its hull renders at ~1–3/255 luminance,
/// effectively invisible in flight, while the SAME hull under the HD path or
/// as a Meshy model was readable. Native itself ships a second, brighter
/// ambient level (0.5, clusterIGC.h <c>GetAmbientLevel</c>, applied by
/// WinTrek.cpp:4935 when bidirectional lighting is off), so a 0.5-class
/// ambient floor stays inside native's own lighting envelope; a
/// componentwise <c>Max</c> leaves every normally-painted hull unchanged.</para>
/// </summary>
public sealed class NativeShipRenderer : IDisposable
{
    /// <summary>Native's <c>RY(π)</c> mesh flip (thinggeo.cpp:1501-1503;
    /// m_bFlip = options==0, shipIGC.cpp:1412). A proper rotation — no handedness
    /// change — it swings the .mdl's +Z nose to −Z, this port's forward.</summary>
    private static readonly Matrix NoseFlip = Matrix.CreateRotationY(MathHelper.Pi);

    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly Dictionary<string, NativeShipGeometry> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MdlFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<NativeShipGeometry>> _lodLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> _lodDesc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingModelLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingTextureLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly BasicEffect _effect;
    private readonly HdMaterialRenderer _hd;
    private readonly bool _ownsHd;
    public bool HdTexturesEnabled { get; set; } = true;
    public bool HdPbrEnabled { get; set; } = true;
    public void ResetHdResidency() => _hd.ResetResidency();
    public long HdResidentBytes => _hd.ResidentBytes;
    private readonly RasterizerState _cullCcw;

    public NativeShipRenderer(GraphicsDevice gd, Assets.IAssetSource assets, HdPack? hdPack = null, ShaderAssetCache? shaders = null, HdMaterialRenderer? hd = null, Func<double>? seconds = null)
    {
        _gd = gd;
        _assets = assets;
        _effect = new BasicEffect(gd);
        _hd = hd ?? new HdMaterialRenderer(gd, NativeMaterialParts.NativeReader(assets), hdPack, shaders: shaders, seconds: seconds);
        _ownsHd = hd is null;
        _cullCcw = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
    }

    /// <summary>P862 — how many hull meshes are resident (diagnostics for the
    /// sector asset warm-up: a warp into a sector full of hulls never seen
    /// before grows this).</summary>
    public int LoadedModelCount => _models.Count;

    /// <summary>P862 — how many native hull textures are resident.</summary>
    public int LoadedTextureCount => _textures.Count;

    /// <summary>P862 — resolve a hull model's mesh, native texture and every
    /// native LODGeo level into the caches WITHOUT drawing, so the first frame
    /// that actually shows that hull does no decode/upload work. Calls exactly
    /// the loaders <see cref="Draw"/> and <see cref="LodLevel"/> call, so a warmed
    /// model is a plain cache hit afterwards; nothing here is evicted (the
    /// caches live for the match).</summary>
    /// <param name="modelStem">The hull's <c>DataHullTypeIGC.ModelName</c>.</param>
    /// <param name="hullTextureName">The hull's texture-name override (usually
    /// empty — the model's own texture namespace is used).</param>
    /// <returns>1 when at least one new resource was created, 0 when everything
    /// was already resident, -1 when the model is missing/undecodable.</returns>
    public int Warm(string modelStem, string? hullTextureName)
    {
        if (string.IsNullOrEmpty(modelStem)) return -1;
        int models = _models.Count, textures = _textures.Count, levels = _lodLevels.Count;
        if (MeshFor(modelStem) is null) return -1;
        if (TextureStemFor(modelStem, hullTextureName) is { } texStem) TextureFor(texStem);
        int count = LodLevelCount(modelStem);
        for (int i = 0; i < count; i++) _ = LodLevel(modelStem, i);
        return _models.Count > models || _textures.Count > textures || _lodLevels.Count > levels ? 1 : 0;
    }

    /// <summary>The compiled native geometry for a model stem (parsed + built once,
    /// cached), or null when the file is missing/undecodable (logged once → caller
    /// falls back to Meshy then placeholder).</summary>
    public NativeShipGeometry? MeshFor(string modelStem)
    {
        if (string.IsNullOrEmpty(modelStem)) return null;
        if (_models.TryGetValue(modelStem, out var g)) return g;
        try
        {
            if (!_assets.Exists(modelStem + ".mdl"))
            {
                LogMissingModel(modelStem, "not present");
                return null;
            }
            var file = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, modelStem + ".mdl"));
            _parsed[modelStem] = file;
            var geo = NativeShipGeometry.Build(_gd, file.Meshes, file.LodLevels);
            _models[modelStem] = geo;
            return geo;
        }
        catch (Exception ex)
        {
            LogMissingModel(modelStem, ex.Message);
            return null;
        }
    }

    /// <summary>The decoded native texture for a texture stem, cached, or null if
    /// unavailable (logged once → caller renders lit-solid, never invisible).</summary>
    public Texture2D? TextureFor(string textureStem)
    {
        if (string.IsNullOrEmpty(textureStem)) return null;
        if (_textures.TryGetValue(textureStem, out var t)) return t;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_assets.Exists(textureStem + ".mdl"))
            {
                LogMissingTexture(textureStem, "not present");
                return null;
            }
            var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, textureStem + ".mdl"));
            var surf = new Texture2D(_gd, mdlTex.Width, mdlTex.Height);
            // P862 — upload the decoder's RGBA bytes straight (SurfaceFormat.Color IS
            // RGBA8, and the decoder already writes alpha 255 for every texel). The
            // old Color[] staging cost a 16.7 MB array plus a 4.2M-iteration loop per
            // 2048² texture — ~33 MB of garbage per texture, and a cold sector loads
            // several in one frame (the P862 transit hitch).
            surf.SetData(mdlTex.Rgba);
            _textures[textureStem] = surf;
            AssetLoadLog.Note("ship texture", textureStem, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                $"{mdlTex.Width}x{mdlTex.Height}");
            return surf;
        }
        catch (Exception ex)
        {
            LogMissingTexture(textureStem, ex.Message);
            return null;
        }
    }

    /// <summary>The merged-bounding-sphere radius of a model's native mesh (native
    /// <c>m_radiusMesh</c>), or 0 when unknown → caller leaves scale at native default.</summary>
    public float MeshRadius(string modelStem)
        => MeshFor(modelStem) is { } g ? g.MeshRadius : 0f;

    /// <summary>P628 — the number of native <c>LODGeo</c> detail levels a model
    /// carries (0 when the .mdl has no LODGeo or is undecodable). Each is a
    /// lower-triangle-count detail variant chosen by projected screen size —
    /// see <see cref="ShipLod.NativeLodLevelFor"/>.</summary>
    public int LodLevelCount(string modelStem)
    {
        if (MeshFor(modelStem) is null) return 0;
        return _parsed.TryGetValue(modelStem, out var mdl) ? mdl.LodLevels.Count : 0;
    }

    /// <summary>P628 — the baked LODGeo pixel-radius thresholds for a model in
    /// the order <see cref="ShipLod.NativeLodLevelFor"/> expects: <b>descending</b>
    /// (finest level's threshold first), i.e. the same order native's
    /// <c>LODGeoImpl::AddGeo</c> sorts its <c>m_radii</c> (geometry.cpp:1667-1713)
    /// before <c>Render</c> walks them. Empty when the model has none.</summary>
    public IReadOnlyList<float> LodPixelRadii(string modelStem)
    {
        var o = DescendingOrder(modelStem);
        if (o.Length == 0) return Array.Empty<float>();
        var f = _parsed[modelStem];
        var r = new float[o.Length];
        for (int i = 0; i < o.Length; i++) r[i] = f.LodLevels[o[i]].PixelRadius;
        return r;
    }

    /// <summary>P628 — the compiled geometry for the LODGeo level at index
    /// <paramref name="index"/> in descending-pixel-radius order (0 = the first
    /// reduced-detail level, i.e. used first as the ship shrinks). Returns null
    /// when the model has no such level or is undecodable. Built+cached on first
    /// access so models without LOD never allocate level buffers.</summary>
    public NativeShipGeometry? LodLevel(string modelStem, int index)
    {
        var o = DescendingOrder(modelStem);
        if (index < 0 || index >= o.Length) return null;
        if (MeshFor(modelStem) is null) return null;
        var levels = BuildLevels(modelStem);
        return levels[o[index]];
    }

    /// <summary>P628 — file indexes of a model's LODGeo levels in descending
    /// pixel-radius order (native's m_radii sort order).</summary>
    private int[] DescendingOrder(string modelStem)
    {
        if (_lodDesc.TryGetValue(modelStem, out var o)) return o;
        if (!_parsed.TryGetValue(modelStem, out var mdl)) return Array.Empty<int>();
        var idx = Enumerable.Range(0, mdl.LodLevels.Count).ToArray();
        Array.Sort(idx, (a, b) => mdl.LodLevels[b].PixelRadius.CompareTo(mdl.LodLevels[a].PixelRadius));
        _lodDesc[modelStem] = idx;
        return idx;
    }

    /// <summary>P628 — the compiled per-file-order level geometries, built once
    /// (a broken level becomes null → caller falls back to the base mesh).</summary>
    private List<NativeShipGeometry> BuildLevels(string modelStem)
    {
        if (_lodLevels.TryGetValue(modelStem, out var list)) return list;
        var mdl = _parsed[modelStem];
        list = new List<NativeShipGeometry>(mdl.LodLevels.Count);
        foreach (var lev in mdl.LodLevels)
        {
            try { list.Add(NativeShipGeometry.Build(_gd, lev.Meshes)); }
            catch { list.Add(null!); }
        }
        _lodLevels[modelStem] = list;
        return list;
    }

    /// <summary>
    /// P569 — the per-axis half-extents (the shield-ellipse proxy) of a model's
    /// native mesh, or null when the model is undecodable. Fed to the
    /// shield-flare cap math as the struck ship's <c>ellipse</c> — the client's
    /// faithful stand-in for native's hull hit-test
    /// <c>GetEllipseEquation()</c> (<c>shieldIGC.cpp:171</c>); see
    /// <see cref="NativeShipResolver.MergedExtents"/>.
    /// </summary>
    public Vector3? ShieldEllipse(string modelStem)
    {
        if (MeshFor(modelStem) is null) return null;
        if (!_parsed.TryGetValue(modelStem, out var mdl)) return null;
        return NativeShipResolver.MergedExtents(mdl.Meshes);
    }

    /// <summary>P566 — the native hardpoint <c>FrameData</c> points of a model
    /// (engine nozzles / weapon mounts / attach points), parsed from its <c>.mdl</c>
    /// (<see cref="MdlFile.Frames"/>), or null when the model is undecodable. Exposed
    /// so engine-trail / nozzle-glow emitters can anchor at the REAL nozzle points the
    /// artist authored instead of the client's guessed aft-centre.</summary>
    public IReadOnlyList<MdlFrame>? FramesFor(string modelStem)
    {
        if (MeshFor(modelStem) is null) return null;
        return _parsed.TryGetValue(modelStem, out var mdl) ? mdl.Frames : null;
    }

    /// <summary>
    /// The native texture STEM for a hull's model, resolved from the model's own
    /// imported texture namespace(s) — see <see cref="NativeShipResolver.ResolveTextureStem"/>.
    /// Returns null when the model is undecodable or has no resolvable texture unit →
    /// caller renders lit-solid. <paramref name="hullTextureName"/> is the hull's
    /// <c>TextureName</c> override (may be null).
    /// </summary>
    public string? TextureStemFor(string modelStem, string? hullTextureName)
    {
        if (MeshFor(modelStem) is null) return null;   // undecodable → no texture either
        if (!_parsed.TryGetValue(modelStem, out var mdl)) return null;
        // fileExists operates on the bare texture STEM (e.g. "fig05bmp"); the
        // asset source wants the full relative path "fig05bmp.mdl".
        return NativeShipResolver.ResolveTextureStem(mdl, hullTextureName, modelStem, stem => _assets.Exists(stem + ".mdl"));
    }

    /// <summary>P800 — the sector lighting with its ambient component lifted to
    /// <see cref="ShipRenderer.ShipAmbientFloor"/> (componentwise Max, so only
    /// sub-floor ambients change). The native ship path now shares the floor the
    /// Meshy and HD paths already apply; see the class doc for the native cite.</summary>
    internal static SectorLighting WithShipAmbientFloor(SectorLighting l)
    {
        var a = l.Ambient;
        var f = ShipRenderer.ShipAmbientFloor;
        return l with
        {
            Ambient = new Allegiance.Core.Collision.Vector3(
                MathF.Max(a.X, f.X), MathF.Max(a.Y, f.Y), MathF.Max(a.Z, f.Z)),
        };
    }

    private void LogMissingModel(string stem, string why)
    {
        if (_missingModelLogged.Add(stem))
            Console.WriteLine($"[monogame] native ship model '{stem}.mdl' unavailable ({why}) — falling back to Meshy/placeholder");
    }

    private void LogMissingTexture(string stem, string why)
    {
        if (_missingTextureLogged.Add(stem))
            Console.WriteLine($"[monogame] native ship texture '{stem}.mdl' unavailable ({why}) — rendering lit-solid");
    }

    /// <summary>
    /// Draw a native ship mesh. <paramref name="world"/> is the caller's camera-
    /// relative world matrix (PoseConverter orientation · translation, the same one
    /// the Meshy path passes <see cref="ShipRenderer.Draw"/>). The native flip and
    /// uniform <paramref name="nativeScale"/> are folded in before it. <paramref name="opacity"/>
    /// in (0,1] alpha-blends the whole hull (a cloaked native ship renders
    /// translucent, back faces still culled so it never self-occludes into holes).
    /// </summary>
    public void Draw(NativeShipGeometry geo, Texture2D? texture, Matrix world,
        Matrix view, Matrix projection, float opacity, float nativeScale, SectorLighting? lighting,
        string? modelStem = null, string? textureStem = null, bool? hdOverride = null)
    {
        bool translucent = opacity < 0.999f;
        var gd = _gd;
        gd.BlendState = translucent ? BlendState.AlphaBlend : BlendState.Opaque;
        // P564 — a translucent (cloaked) hull must NOT write depth: with depth-write
        // ON, the hull's own far-side faces lose the test against the already-written
        // near side, so the ship shows holes ("some parts of the mesh are kind of
        // transparent or invisible") instead of a uniform fade. Matching native's
        // translucent-object handling (fade/alpha with depth-write off), a cloaked
        // native ship fades as a whole. Opaque ships keep depth-write on (depth
        // occlusion still correct).
        gd.DepthStencilState = translucent
            ? DepthStencilState.DepthRead
            : DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        var local = NoseFlip * Matrix.CreateScale(nativeScale) * world;

        _effect.World = local;
        _effect.View = view;
        _effect.Projection = projection;
        if (lighting is { } lp)
        {
            // P800 — ambient floor lift, shared with the Meshy path
            // (ShipRenderer.ShipAmbientFloor) and the HD/PBR path
            // (HdMaterialRenderer.cs:130). Componentwise Max: normally-painted
            // hulls (albedo ≫ floor) are untouched; the darkest native art
            // (stealth bomber sbbmp, measured avg RGB (13,7,4)) stops
            // disappearing under the 0.25 bidirectional ambient.
            SectorLightingEffect.Apply(_effect, WithShipAmbientFloor(lp));
        }
        else
        {
            _effect.EnableDefaultLighting();
            _effect.AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f);
        }
        _effect.Texture = texture;
        _effect.TextureEnabled = texture is not null;
        _effect.Alpha = translucent ? opacity : 1f;

        // HD-5 — the pack participation mode. Normally the ship follows the shared
        // HdMaterialRenderer.Mode (the single global setting every renderer obeys).
        // The inspector HD-compare override forces HD on (respecting the lighting
        // choice) or forces native off, regardless of the texture gate.
        var drawMode = hdOverride switch
        {
            false => HdDrawMode.Off,
            true => HdPbrEnabled ? HdDrawMode.Pbr : HdDrawMode.Classic,
            _ => _hd.Mode,
        };
        geo.Materials.Draw(gd, _hd, modelStem, textureStem,
            texture, TextureFor, _effect, geo.Vertices, geo.Indices, local, view, projection,
            opacity, lighting, drawMode);

        if (translucent)
            gd.BlendState = BlendState.Opaque;
    }

    /// <summary>P566 — depth-only pass for soft-particle intersections, using the
    /// same native flip+scale+world transform as <see cref="Draw"/> and the same
    /// CullCounterClockwise winding fix. The caller owns <paramref name="depthEffect"/>.</summary>
    public void DrawDepth(NativeShipGeometry geo, Matrix world, Matrix view, Matrix projection,
        float nativeScale, Effect depthEffect, float near, float far)
    {
        var gd = _gd;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        var m = NoseFlip * Matrix.CreateScale(nativeScale) * world;
        depthEffect.Parameters["WorldView"].SetValue(m * view);
        depthEffect.Parameters["WorldViewProj"].SetValue(m * view * projection);
        depthEffect.Parameters["Near"].SetValue(near);
        depthEffect.Parameters["InvRange"].SetValue(LinearDepth.InvRange(near, far));
        depthEffect.CurrentTechnique.Passes[0].Apply();
        gd.SetVertexBuffer(geo.Vertices);
        gd.Indices = geo.Indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geo.TriangleCount);
    }

    /// <summary>A compiled native ship model: one vertex/index buffer across all
    /// meshes (vertex offsets carried into the index stream), with per-vertex
    /// position/normal/texcoord and the WINDING CORRECTED indices
    /// (<see cref="NativeShipResolver.CorrectWinding"/>).</summary>
    public sealed class NativeShipGeometry : IDisposable
    {
        public NativeMaterialParts Materials { get; private init; } = null!;
        public required VertexBuffer Vertices { get; init; }
        public required IndexBuffer Indices { get; init; }
        public required int TriangleCount { get; init; }
        private VertexBuffer? _pbrVertices;
        private IReadOnlyList<MdlMesh> _source = [];
        public VertexBuffer PbrVertices(GraphicsDevice gd)
        {
            if (_pbrVertices is not null) return _pbrVertices;
            Merge(_source, out var verts, out var indices);
            var pbr = PbrVertex.Build(verts, indices);
            _pbrVertices = new VertexBuffer(gd, typeof(PbrVertex), pbr.Length, BufferUsage.WriteOnly);
            _pbrVertices.SetData(pbr);
            return _pbrVertices;
        }

        /// <summary>Merged-bounding-sphere radius of the raw native mesh — the
        /// scene-side hull proxy (larger than native's <c>m_radiusMesh</c>).</summary>
        public required float MeshRadius { get; init; }

        /// <summary>HUD-21b — native <c>m_radiusMesh</c>: the maximum vertex
        /// distance from the model origin, computed ONCE here at load
        /// (<see cref="NativeShipResolver.MaxVertexRadius"/>,
        /// <c>engine/geometry.cpp:408-418</c>, <c>effect/thinggeo.cpp:1053</c>).
        /// The target-pane preview normalises by this so the farthest vertex
        /// lands on radius 1 (<c>thinggeo.cpp:373-381</c>).</summary>
        public required float MaxVertexRadius { get; init; }

        /// <param name="lods">The file's LOD levels (optional): native
        /// <c>m_radiusMesh</c> is the max over the whole LODGeo, so they feed
        /// <see cref="MaxVertexRadius"/> only (the buffers stay the base mesh).</param>
        public static NativeShipGeometry Build(GraphicsDevice gd, IReadOnlyList<MdlMesh> meshes,
            IReadOnlyList<MdlLodLevel>? lods = null)
        {
            Merge(meshes, out var verts, out var indices);
            var vbuf = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
            vbuf.SetData(verts);
            var ibuf = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
            ibuf.SetData(indices);
            return new NativeShipGeometry
            {
                _source = meshes,
                Materials = new(meshes, verts, indices),
                Vertices = vbuf,
                Indices = ibuf,
                TriangleCount = indices.Length / 3,
                MeshRadius = NativeShipResolver.MergedRadius(meshes),
                MaxVertexRadius = NativeShipResolver.MaxVertexRadius(meshes, lods),
            };
        }

        private static void Merge(IReadOnlyList<MdlMesh> meshes,
            out VertexPositionNormalTexture[] verts, out uint[] indices)
        {
            int vertexTotal = 0, indexTotal = 0;
            foreach (var m in meshes) { vertexTotal += m.VertexCount; indexTotal += m.Indices.Length; }
            if (vertexTotal == 0 || indexTotal == 0)
                throw new InvalidDataException("native ship model has no geometry");

            verts = new VertexPositionNormalTexture[vertexTotal];
            indices = new uint[indexTotal];
            int vc = 0, ic = 0;
            foreach (var m in meshes)
            {
                int vb = vc;
                for (int v = 0; v < m.VertexCount; v++)
                    verts[vc++] = new VertexPositionNormalTexture(
                        new Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]),
                        new Vector3(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2]),
                        new Vector2(m.TexCoords[2 * v], m.TexCoords[2 * v + 1]));
                // P570 — ship the RAW native winding so CullCounterClockwiseFace
                // keeps the outside under MonoGame's PERSPECTIVE projection (the
                // real game + hangar preview). See NativeShipResolver.RenderIndices
                // for why P566's reversal was inside-out in the real game.
                var renderIdx = NativeShipResolver.RenderIndices(m.Indices);
                for (int i = 0; i < renderIdx.Length; i++)
                    indices[ic++] = (uint)(renderIdx[i] + vb);
            }
        }

        public void Dispose()
        {
            _pbrVertices?.Dispose();
            Materials.Dispose();
            Vertices.Dispose();
            Indices.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var g in _models.Values) g.Dispose();
        foreach (var list in _lodLevels.Values)
            foreach (var g in list) g?.Dispose();
        foreach (var t in _textures.Values) t.Dispose();
        if (_ownsHd) _hd.Dispose();
        _effect.Dispose();
        _cullCcw.Dispose();
    }
}
