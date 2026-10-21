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
/// P564 — renders a station from its ORIGINAL native <c>.mdl</c> mesh + native
/// per-group textures, parsed at runtime from the artwork source (the same runtime-asset
/// convention as <see cref="NativeShipRenderer"/> / <see cref="NativeAsteroidRenderer"/>).
/// Replaces the procedural hub+ring <see cref="StationRenderer"/>/<see cref="StationMeshFactory"/>
/// placeholder with each station type's real native model
/// (<c>DataStationTypeIGC.ModelName</c>, stationIGC.cpp:240) at native
/// scale/orientation (<see cref="NativeStationResolver"/>), lit by the sector
/// light, with NO side tint (native does not tint station meshes).
/// </summary>
/// <remarks>
/// <para><b>Winding / culling.</b> The native index stream is DirectX
/// clockwise-front; <see cref="NativeStationResolver.RenderIndices"/> preserves
/// it so <c>CullCounterClockwiseFace</c> keeps the outside under
/// MonoGame's perspective projection — real front-face culling, solid, no
/// self-occlusion holes.</para>
///
/// <para><b>Scale / orientation.</b> Each draw folds in the native uniform scale
/// <c>m_scale = Radius/meshRadius</c> (<see cref="NativeStationResolver.ScaleForRadius"/>)
/// and native's <c>RY(π)</c> mesh flip (stationIGC.cpp:240, thinggeo.cpp:1501-1503)
/// before the caller's world matrix, exactly mirroring native's
/// <c>Orient · Scale · RY(π)</c> chain (thinggeo.cpp:1484-1503, stationIGC.cpp:249).</para>
///
/// <para><b>Fallback.</b> When the model file is missing/undecodable
/// (<see cref="MeshFor"/> returns null) the caller falls back to the procedural
/// placeholder <see cref="StationRenderer"/> (logged once), per the task's
/// "keep the placeholder as a logged fallback for missing art".</para>
/// </remarks>
public sealed class NativeStationRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly Dictionary<string, NativeStationGeometry> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MdlFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingModelLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingTextureLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HdMaterialRenderer _hd;
    private readonly bool _ownsHd;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullCcw;

    public NativeStationRenderer(GraphicsDevice gd, Assets.IAssetSource assets, HdMaterialRenderer? hd = null)
    {
        _gd = gd;
        _hd = hd ?? new HdMaterialRenderer(gd, NativeMaterialParts.NativeReader(assets));
        _ownsHd = hd is null;
        _assets = assets;
        _effect = new BasicEffect(gd);
        _cullCcw = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
    }

    /// <summary>The compiled native geometry for a model stem (parsed + built once,
    /// cached), or null when the file is missing/undecodable (logged once → caller
    /// falls back to the procedural placeholder).</summary>
    public NativeStationGeometry? MeshFor(string modelStem)
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
            var geo = NativeStationGeometry.Build(_gd, file.Meshes, file.LodLevels);
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
        if (_missingTextureLogged.Contains(textureStem)) return null;
        try
        {
            if (!_assets.Exists(textureStem + ".mdl"))
            {
                LogMissingTexture(textureStem, "not present");
                return null;
            }
            var surf = NativeStationTexture.Load(_gd, _assets, textureStem);
            _textures[textureStem] = surf;
            return surf;
        }
        catch (Exception ex)
        {
            LogMissingTexture(textureStem, ex.Message);
            return null;
        }
    }

    /// <summary>The native origin-centred vertex radius of a model (native
    /// <c>m_radiusMesh</c>), or 0 when unknown → caller leaves scale at native default.</summary>
    public float MeshRadius(string modelStem)
        => MeshFor(modelStem) is { } g ? g.MeshRadius : 0f;

    /// <summary>The native texture STEM for a station model, resolved from the
    /// model's own imported texture namespace(s) / the station's TextureName
    /// override — see <see cref="NativeStationResolver.ResolveTextureStem"/>.
    /// Returns null when the model is undecodable or has no resolvable texture unit →
    /// caller renders lit-solid.</summary>
    public string? TextureStemFor(string modelStem, string? stationTextureName)
    {
        if (MeshFor(modelStem) is null) return null;   // undecodable → no texture either
        if (!_parsed.TryGetValue(modelStem, out var mdl)) return null;
        return NativeStationResolver.ResolveTextureStem(mdl, stationTextureName, modelStem, stem => _assets.Exists(stem + ".mdl"));
    }

    /// <summary>Native lights from the model's lights export, never synthetic anchors.</summary>
    public IReadOnlyList<MdlLight> LightsFor(string modelStem)
        => MeshFor(modelStem) is not null && _parsed.TryGetValue(modelStem, out var mdl)
            ? mdl.Lights : Array.Empty<MdlLight>();

    private void LogMissingModel(string stem, string why)
    {
        if (_missingModelLogged.Add(stem))
            Console.WriteLine($"[monogame] native station model '{stem}.mdl' unavailable ({why}) — falling back to placeholder");
    }

    private void LogMissingTexture(string stem, string why)
    {
        if (_missingTextureLogged.Add(stem))
            Console.WriteLine($"[monogame] native station texture '{stem}.mdl' unavailable ({why}) — rendering lit-solid");
    }

    /// <summary>
    /// Draw a native station mesh. <paramref name="world"/> is the caller's
    /// camera-relative world matrix (orientation · translation). The native flip
    /// and uniform <paramref name="nativeScale"/> are folded in before it.
    /// No side tint — the station's real texture provides its look (native does
    /// not tint station meshes).
    /// </summary>
    public void Draw(NativeStationGeometry geo, Texture2D? texture, Matrix world,
        Matrix view, Matrix projection, float nativeScale, SectorLighting? lighting, string? modelStem = null, string? textureStem = null)
    {
        var gd = _gd;
        // ThingGeo brackets the mesh with BlendModeSourceAlpha
        // (effect/thinggeo.cpp:1642-1645). Binary station skins are opaque;
        // PNG imports retain their authored alpha.
        gd.BlendState = BlendState.NonPremultiplied;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        gd.SamplerStates[0] = SamplerState.LinearWrap;

        var local = NativeStationResolver.NoseFlip * Matrix.CreateScale(nativeScale) * world;

        _effect.World = local;
        _effect.View = view;
        _effect.Projection = projection;
        if (lighting is { } lp)
            SectorLightingEffect.Apply(_effect, lp);
        else
        {
            _effect.EnableDefaultLighting();
            _effect.AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f);
        }
        _effect.DiffuseColor = Color.White.ToVector3();
        geo.Materials.Draw(gd, _hd, modelStem, textureStem,
            texture, TextureFor, _effect, geo.Vertices, geo.Indices, local, view, projection,
            1, lighting);
    }

    /// <summary>P564 — depth-only pass for soft-particle intersections, using the
    /// same native flip+scale+world transform as <see cref="Draw"/> and the same
    /// CullCounterClockwise winding. The caller owns <paramref name="depthEffect"/>.</summary>
    public void DrawDepth(NativeStationGeometry geo, Matrix world, Matrix view, Matrix projection,
        float nativeScale, Effect depthEffect, float near, float far)
    {
        var gd = _gd;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        var m = NativeStationResolver.NoseFlip * Matrix.CreateScale(nativeScale) * world;
        depthEffect.Parameters["WorldView"].SetValue(m * view);
        depthEffect.Parameters["WorldViewProj"].SetValue(m * view * projection);
        depthEffect.Parameters["Near"].SetValue(near);
        depthEffect.Parameters["InvRange"].SetValue(LinearDepth.InvRange(near, far));
        depthEffect.CurrentTechnique.Passes[0].Apply();
        gd.SetVertexBuffer(geo.Vertices);
        gd.Indices = geo.Indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geo.TriangleCount);
    }

    /// <summary>A compiled native station model: one vertex/index buffer with a draw range for each native group across all
    /// meshes (vertex offsets carried into the index stream), with per-vertex
    /// position/normal/texcoord and authored native indices
    /// (<see cref="NativeStationResolver.RenderIndices"/>).</summary>
    public sealed class NativeStationGeometry : IDisposable
    {
        public NativeMaterialParts Materials { get; private init; } = null!;
        public required VertexBuffer Vertices { get; init; }
        public required IndexBuffer Indices { get; init; }
        public required int TriangleCount { get; init; }
        public required IReadOnlyList<NativeStationPart> Parts { get; init; }

        /// <summary>Maximum vertex distance from the origin of the raw native mesh — native
        /// <c>m_radiusMesh</c>, the denominator of <c>m_scale</c>.</summary>
        public required float MeshRadius { get; init; }

        /// <summary>HUD-21b — native <c>m_radiusMesh</c> (max vertex distance,
        /// computed once at load). For stations this equals <see cref="MeshRadius"/>
        /// because <see cref="NativeStationResolver.MeshRadius"/> already uses the
        /// native max-vertex rule; it is exposed under the same name the ship and
        /// asteroid geometries use so the preview rule is uniform.</summary>
        public required float MaxVertexRadius { get; init; }

        public static NativeStationGeometry Build(GraphicsDevice gd, IReadOnlyList<MdlMesh> meshes,
            IReadOnlyList<MdlLodLevel>? lods = null)
        {
            int vertexTotal = 0, indexTotal = 0;
            foreach (var m in meshes) { vertexTotal += m.VertexCount; indexTotal += m.Indices.Length; }
            if (vertexTotal == 0 || indexTotal == 0)
                throw new InvalidDataException("native station model has no geometry");

            var verts = new VertexPositionNormalTexture[vertexTotal];
            var indices = new uint[indexTotal];
            int vc = 0, ic = 0;
            var parts = new List<NativeStationPart>();
            foreach (var m in meshes)
            {
                int vb = vc;
                for (int v = 0; v < m.VertexCount; v++)
                    verts[vc++] = new VertexPositionNormalTexture(
                        new Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]),
                        new Vector3(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2]),
                        new Vector2(m.TexCoords[2 * v], m.TexCoords[2 * v + 1]));
                // Preserve authored interior and exterior winding; a centroid
                // heuristic cannot distinguish a bay wall from an outer hull.
                var renderIdx = NativeStationResolver.RenderIndices(m.Indices);
                parts.Add(new NativeStationPart(m.TextureStem, ic, m.TriangleCount));
                for (int i = 0; i < renderIdx.Length; i++)
                    indices[ic++] = (uint)(renderIdx[i] + vb);
            }
            var vbuf = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
            vbuf.SetData(verts);
            var ibuf = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
            ibuf.SetData(indices);
            return new NativeStationGeometry
            {
                Materials = new(meshes, verts, indices),
                Vertices = vbuf,
                Indices = ibuf,
                TriangleCount = indices.Length / 3,
                Parts = parts,
                MeshRadius = NativeStationResolver.MeshRadius(meshes, lods),
                MaxVertexRadius = NativeStationResolver.MeshRadius(meshes, lods),
            };
        }

        public void Dispose()
        {
            Materials.Dispose();
            Vertices.Dispose();
            Indices.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var g in _models.Values) g.Dispose();
        foreach (var t in _textures.Values) t.Dispose();
        if (_ownsHd) _hd.Dispose();
        _effect.Dispose();
        _cullCcw.Dispose();
    }
}

/// <summary>One native TextureGeo draw range in the combined buffers.</summary>
public readonly record struct NativeStationPart(string? TextureStem, int StartIndex, int TriangleCount);
