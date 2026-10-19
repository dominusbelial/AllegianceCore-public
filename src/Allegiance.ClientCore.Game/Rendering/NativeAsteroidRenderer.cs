using System;
using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P567 — renders an asteroid from its ORIGINAL native <c>.mdl</c> rock mesh +
/// native texture, parsed at runtime from the artwork source and cached per model
/// (the P566 native-mesh pipeline reused for asteroids).
/// <see cref="NativeAsteroidResolver"/> holds the pure rules (raw native winding,
/// native scale <c>radius/meshRadius</c>, the <c>RY(π)</c> flip, texture-stem
/// resolution); this class owns the GPU geometry/textures and the draw pass.
///
/// <para><b>Winding / culling.</b> The native index stream is shipped RAW (P570),
/// letting <see cref="RasterizerState.CullCounterClockwiseFace"/> keep the outside
/// under the game's perspective projection — the convention verified solid in the
/// real window for ships and re-verified for every rock mesh (their raw geometric
/// normals are ~100% inward yet render solid). Culling back faces is what fixes the
/// inside-out look: the old imported path's outward-wound variants drew their
/// interiors.</para>
///
/// <para><b>Scale / orientation.</b> Each draw folds in the native uniform scale
/// <c>m_scale = radius/meshRadius</c> (<see cref="NativeAsteroidResolver.ScaleForRadius"/>)
/// and native's <c>RY(π)</c> mesh flip (<c>m_bFlip</c>, thinggeo.cpp:1501-1503) before
/// the caller's world matrix, mirroring native's <c>Orient · Scale · RY(π)</c> chain
/// (thinggeo.cpp:1484-1503, asteroidigc.cpp:38). The per-instance spin (P252) is
/// folded into the caller's orientation, exactly as native post-rotates its
/// orientation each tick.</para>
///
/// <para><b>Lighting.</b> P529/P530 native per-sector bidirectional lighting, exactly
/// like the native ship path — the same <see cref="SectorLightingEffect"/> the
/// imported/procedural <see cref="AsteroidRenderer"/> uses, so rocks share the sector's
/// light.</para>
/// </remarks>
public sealed class NativeAsteroidRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly Dictionary<string, NativeShipRenderer.NativeShipGeometry> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MdlFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingModelLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingTextureLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HdMaterialRenderer _hd;
    private readonly bool _ownsHd;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullCcw;

    public NativeAsteroidRenderer(GraphicsDevice gd, Assets.IAssetSource assets, HdMaterialRenderer? hd = null)
    {
        _gd = gd;
        _hd = hd ?? new HdMaterialRenderer(gd, NativeMaterialParts.NativeReader(assets));
        _ownsHd = hd is null;
        _assets = assets;
        _effect = new BasicEffect(gd);
        _cullCcw = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
    }

    /// <summary>The compiled native rock geometry for a model stem (parsed + built
    /// once, cached), or null when the file is missing/undecodable (logged once →
    /// the caller falls back to the imported/procedural path). Built through P566's
    /// pipeline (<see cref="NativeShipRenderer.NativeShipGeometry.Build"/>), which
    /// ships the RAW native winding per P570.</summary>
    public NativeShipRenderer.NativeShipGeometry? GeometryFor(string modelStem)
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
            var geo = NativeShipRenderer.NativeShipGeometry.Build(_gd, file.Meshes);
            _models[modelStem] = geo;
            return geo;
        }
        catch (Exception ex)
        {
            LogMissingModel(modelStem, ex.Message);
            return null;
        }
    }

    /// <summary>The decoded native rock texture for a texture stem, cached, or null
    /// if unavailable (logged once → caller renders lit-solid, never invisible).</summary>
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

    /// <summary>The merged-bounding-sphere radius of a rock's native mesh (native
    /// <c>m_radiusMesh</c>), or 0 when unknown → the caller leaves scale at native
    /// default (1).</summary>
    public float MeshRadius(string modelStem)
        => GeometryFor(modelStem) is { } g ? g.MeshRadius : 0f;

    /// <summary>The native texture STEM for an asteroid's model, resolved from the
    /// model's own imported texture namespace(s) — see
    /// <see cref="NativeAsteroidResolver.ResolveTextureStem"/>. <paramref name="overrideTextureName"/>
    /// is the wire <c>textureName</c> (empty in the corpus). Returns null when the
    /// model is undecodable or has no resolvable texture unit → caller renders lit-solid.</summary>
    public string? TextureStemFor(string modelStem, string? overrideTextureName)
    {
        if (GeometryFor(modelStem) is null) return null;   // undecodable → no texture either
        if (!_parsed.TryGetValue(modelStem, out var mdl)) return null;
        return NativeAsteroidResolver.ResolveTextureStem(mdl, overrideTextureName, modelStem, stem => _assets.Exists(stem + ".mdl"));
    }

    private void LogMissingModel(string stem, string why)
    {
        if (_missingModelLogged.Add(stem))
            Console.WriteLine($"[monogame] native asteroid model '{stem}.mdl' unavailable ({why}) — falling back to imported/procedural rock");
    }

    private void LogMissingTexture(string stem, string why)
    {
        if (_missingTextureLogged.Add(stem))
            Console.WriteLine($"[monogame] native asteroid texture '{stem}.mdl' unavailable ({why}) — rendering lit-solid");
    }

    /// <summary>
    /// Draw a native asteroid rock. <paramref name="world"/> is the caller's
    /// camera-relative world matrix (orientation · spin · translation, the same one
    /// the imported path passes <see cref="AsteroidRenderer.Draw"/> but WITHOUT the
    /// <c>CreateScale(rRadius)</c> — the native uniform
    /// <paramref name="nativeScale"/> = radius/meshRadius replaces it). The native
    /// <c>RY(π)</c> flip and scale are folded in before it, replicating native's
    /// <c>Orient · Scale · RY(π)</c> chain so the mesh is scaled in local space and
    /// then placed (translation never scaled).
    /// </summary>
    public void Draw(NativeShipRenderer.NativeShipGeometry geo, Texture2D? texture, Matrix world,
        Matrix view, Matrix projection, float nativeScale, SectorLighting? lighting, string? modelStem = null, string? textureStem = null)
    {
        var gd = _gd;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        gd.SamplerStates[0] = SamplerState.LinearWrap;

        var local = NativeAsteroidResolver.NoseFlip * Matrix.CreateScale(nativeScale) * world;

        _effect.World = local;
        _effect.View = view;
        _effect.Projection = projection;
        if (lighting is { } lp)
            SectorLightingEffect.Apply(_effect, lp);
        else
        {
            _effect.EnableDefaultLighting();
            _effect.AmbientLightColor = new Vector3(0.25f, 0.25f, 0.28f);
        }
        _effect.Texture = texture;
        _effect.TextureEnabled = texture is not null;

        geo.Materials.Draw(gd, _hd, modelStem, textureStem,
            texture, TextureFor, _effect, geo.Vertices, geo.Indices, local, view, projection,
            1, lighting);
    }

    /// <summary>P567 — depth-only pass for soft-particle intersections, using the same
    /// native flip+scale+world transform as <see cref="Draw"/> and the same raw-winding
    /// <c>CullCounterClockwiseFace</c>. The caller owns <paramref name="depthEffect"/>.</summary>
    public void DrawDepth(NativeShipRenderer.NativeShipGeometry geo, Matrix world, Matrix view, Matrix projection,
        float nativeScale, Effect depthEffect, float near, float far)
    {
        var gd = _gd;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        var m = NativeAsteroidResolver.NoseFlip * Matrix.CreateScale(nativeScale) * world;
        depthEffect.Parameters["WorldView"].SetValue(m * view);
        depthEffect.Parameters["WorldViewProj"].SetValue(m * view * projection);
        depthEffect.Parameters["Near"].SetValue(near);
        depthEffect.Parameters["InvRange"].SetValue(LinearDepth.InvRange(near, far));
        depthEffect.CurrentTechnique.Passes[0].Apply();
        gd.SetVertexBuffer(geo.Vertices);
        gd.Indices = geo.Indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geo.TriangleCount);
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
