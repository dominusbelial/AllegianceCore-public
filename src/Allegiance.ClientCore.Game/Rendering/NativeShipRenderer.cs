using System;
using System.Collections.Generic;
using System.IO;
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
/// <para><b>Lighting.</b> P530 sector lighting is applied with the PURE native
/// ambient (0.25) — deliberately WITHOUT the <see cref="ShipRenderer.ShipAmbientFloor"/>
/// lift, which P530 added specifically for the dark textured MESHY hulls. Native
/// ships carry real native art whose brightness the original game tuned to be
/// readable under 0.25 ambient; whether that lift is warranted for native art is
/// reported in docs/reports/P566.</para>
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
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingModelLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingTextureLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullCcw;

    public NativeShipRenderer(GraphicsDevice gd, Assets.IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
        _effect = new BasicEffect(gd);
        _cullCcw = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
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
            var geo = NativeShipGeometry.Build(_gd, file.Meshes);
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
        try
        {
            if (!_assets.Exists(textureStem + ".mdl"))
            {
                LogMissingTexture(textureStem, "not present");
                return null;
            }
            var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, textureStem + ".mdl"));
            var surf = new Texture2D(_gd, mdlTex.Width, mdlTex.Height);
            var cols = new Color[mdlTex.Width * mdlTex.Height];
            int n = 0;
            for (int y = 0; y < mdlTex.Height; y++)
                for (int x = 0; x < mdlTex.Width; x++)
                {
                    int si = (y * mdlTex.Width + x) * 4;
                    cols[n++] = new Color((int)mdlTex.Rgba[si], (int)mdlTex.Rgba[si + 1], (int)mdlTex.Rgba[si + 2], 255);
                }
            surf.SetData(cols);
            _textures[textureStem] = surf;
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
        Matrix view, Matrix projection, float opacity, float nativeScale, SectorLighting? lighting)
    {
        bool translucent = opacity < 0.999f;
        var gd = _gd;
        gd.BlendState = translucent ? BlendState.AlphaBlend : BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        var local = NoseFlip * Matrix.CreateScale(nativeScale) * world;

        _effect.World = local;
        _effect.View = view;
        _effect.Projection = projection;
        if (lighting is { } lp)
        {
            // P566 — PURE native sector lighting (ambient 0.25). No ShipAmbientFloor
            // lift: native art is tuned for native ambient; see report.
            SectorLightingEffect.Apply(_effect, lp);
        }
        else
        {
            _effect.EnableDefaultLighting();
            _effect.AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f);
        }
        _effect.Texture = texture;
        _effect.TextureEnabled = texture is not null;
        _effect.Alpha = translucent ? opacity : 1f;

        _effect.CurrentTechnique.Passes[0].Apply();
        gd.SetVertexBuffer(geo.Vertices);
        gd.Indices = geo.Indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geo.TriangleCount);

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
        public required VertexBuffer Vertices { get; init; }
        public required IndexBuffer Indices { get; init; }
        public required int TriangleCount { get; init; }

        /// <summary>Merged-bounding-sphere radius of the raw native mesh — native
        /// <c>m_radiusMesh</c>, the denominator of <c>m_scale</c>.</summary>
        public required float MeshRadius { get; init; }

        public static NativeShipGeometry Build(GraphicsDevice gd, IReadOnlyList<MdlMesh> meshes)
        {
            int vertexTotal = 0, indexTotal = 0;
            foreach (var m in meshes) { vertexTotal += m.VertexCount; indexTotal += m.Indices.Length; }
            if (vertexTotal == 0 || indexTotal == 0)
                throw new InvalidDataException("native ship model has no geometry");

            var verts = new VertexPositionNormalTexture[vertexTotal];
            var indices = new uint[indexTotal];
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
            var vbuf = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
            vbuf.SetData(verts);
            var ibuf = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
            ibuf.SetData(indices);
            return new NativeShipGeometry
            {
                Vertices = vbuf,
                Indices = ibuf,
                TriangleCount = indices.Length / 3,
                MeshRadius = NativeShipResolver.MergedRadius(meshes),
            };
        }

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var g in _models.Values) g.Dispose();
        foreach (var t in _textures.Values) t.Dispose();
        _effect.Dispose();
        _cullCcw.Dispose();
    }
}
