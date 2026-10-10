using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P593b — draws every live missile with its OWN real parsed native
/// <c>.mdl</c> mesh AND its native texture, closing the lit-solid "white
/// shard" defect that left most missile types as plain grey-white faceted
/// solids in combat.
///
/// <para><b>Why this exists.</b> The pre-P593b <see cref="MissileRenderer"/>
/// (P233) never decoded textures: it rendered a SINGLE shared <c>mis05</c>
/// mesh lit-solid with a fixed grey diffuse — so of the 56 missile types in
/// PCore001, the 45 whose <c>ModelName</c> is not one of the three P287-imported
/// content assets (<c>mis05</c>/<c>mis400</c>/<c>artifact</c>) all rendered as
/// light-grey faceted solids: the "white shards" seen in combat. Native loads
/// every model missile with its own real mesh + texture:
/// <c>CmissileIGC::Initialize</c> calls <c>Load(0, modelName, textureName,
/// iconName, …)</c> (<c>src/Igc/missileigc.cpp:49-51</c>). This renderer ports
/// that — each type's real <c>.mdl</c>, textured with its resolved native
/// texture — the P593 way.</para>
///
/// <para><b>Texture.</b> The texture stem is resolved exactly as native's
/// <c>Load(0, modelName, textureName, …)</c>: an explicit <c>textureName</c>
/// override when meaningful, else the model's OWN imported texture namespace
/// (<c>m_pgeoMesh-&gt;FindTexture()</c>, <c>thinggeo.cpp:1038-1043</c> /
/// <c>xfile.cpp:577</c>) via <see cref="NativeShipResolver.ResolveTextureStem"/>
/// — the P593/P586b rule, never a <c>&lt;modelName&gt;bmp</c> guess.</para>
///
/// <para><b>Orientation.</b> Almost every missile <c>.mdl</c> is authored
/// nose-forward along model +Z (measured across all 20 distinct missile models:
/// longest axis = Z), matching the existing missile path's convention (the
/// caller builds the world transform with <c>mo.Set(-Forward)</c>, mapping model
/// +Z → the missile's real forward). The lone exception measured is
/// <c>mis404</c> (Kinetic Torpedo), whose nose is along +Y; a per-model
/// nose-alignment prerotation brings its nose onto +Z so it also flies
/// nose-first. <see cref="NoseAlignFor"/> computes the longest axis per model.</para>
///
/// <para><b>Winding/culling.</b> Cull-mode none (double-sided), the
/// <see cref="ProjectileModelRenderer"/> convention — the various missile
/// <c>.mdl</c>s wind inconsistently (P234 measured <c>mis05</c> needing CW cull;
/// <c>mis404</c> differs), and a small missile model rendering both faces costs
/// nothing and guarantees no interior hole or inside-out surface for any
/// winding.</para>
/// </summary>
public sealed class NativeMissileRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly Dictionary<string, string> _diskStems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectileModelRenderer.Geometry> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MdlFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Matrix> _noseAlign = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;

    public NativeMissileRenderer(GraphicsDevice gd, Assets.IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
        _effect = new BasicEffect(gd) { TextureEnabled = true };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
        // P593/P586b — case-insensitive index of every .mdl basename so a
        // model/texture stem resolves to its exact on-disk unit (native runs
        // case-insensitive; Linux File.Exists is not).
        foreach (var rel in assets.Enumerate("."))
            if (rel.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                _diskStems[Path.GetFileNameWithoutExtension(rel)] = Path.GetFileNameWithoutExtension(rel);
    }

    /// <summary>The exact on-disk basename (without .mdl) for a stem, resolved
    /// case-insensitively, or null when no such file exists.</summary>
    private string? ExactStem(string stem) =>
        _diskStems.TryGetValue(stem, out var exact) ? exact : null;

    /// <summary>
    /// P593b — the native texture-stem resolution for a missile model, factored
    /// as a pure testable static (the exact logic <see cref="TextureStemFor"/>
    /// uses). Honors an explicit missile-type <c>textureName</c> override
    /// (native <c>Load(0, modelName, textureName, …)</c>,
    /// <c>missileigc.cpp:50</c>) then falls back to the model's OWN imported
    /// texture namespace (<c>thinggeo.cpp:1038-1043</c>/<c>xfile.cpp:577</c>) —
    /// the P593 rule, never a <c>&lt;modelName&gt;bmp</c> guess.
    /// <paramref name="fileExists"/> is the case-insensitive disk-stem probe.
    /// </summary>
    public static string? ResolveTextureStemFor(MdlFile mdl, string? overrideTextureName, string modelName, Func<string, bool> fileExists)
        => NativeShipResolver.ResolveTextureStem(mdl, overrideTextureName, modelName, fileExists);

    /// <summary>The compiled mesh for a model stem (parsed + built once, cached,
    /// reusing <see cref="ProjectileModelRenderer.Geometry.Build"/>), or null if
    /// missing/undecodable (logged once). Also computes + caches the per-model
    /// nose-alignment so the missile flies nose-first.</summary>
    public ProjectileModelRenderer.Geometry? MeshFor(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;
        if (_models.TryGetValue(modelName, out var g)) return g;
        if (!TryParse(modelName, out var file)) return null;
        try
        {
            _noseAlign[modelName] = NoseAlignFor(file);
            var geo = ProjectileModelRenderer.Geometry.Build(_gd, file.Meshes);
            _models[modelName] = geo;
            return geo;
        }
        catch (Exception ex)
        {
            LogMissing(modelName, ex.Message);
            return null;
        }
    }

    /// <summary>Parse + cache the <c>.mdl</c> for a model stem (case-insensitive
    /// on-disk lookup), so mesh + texture resolution share one parse.</summary>
    private bool TryParse(string modelName, out MdlFile file)
    {
        if (_parsed.TryGetValue(modelName, out file!)) return true;
        var exact = ExactStem(modelName);
        if (exact is null)
        {
            file = null!;
            LogMissing(modelName, "not present");
            return false;
        }
        try
        {
            file = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, exact + ".mdl"));
            _parsed[modelName] = file;
            return true;
        }
        catch (Exception ex)
        {
            file = null!;
            LogMissing(modelName, ex.Message);
            return false;
        }
    }

    /// <summary>The resolved texture stem for a missile model — honoring the
    /// type's real <c>textureName</c> override (native
    /// <c>Load(0, modelName, textureName, …)</c>) else the model's OWN imported
    /// namespace — or null when none resolves (logged once).</summary>
    public string? TextureStemFor(string modelName, string? overrideTextureName)
    {
        if (!TryParse(modelName, out var mdl)) return null;
        var stem = ResolveTextureStemFor(mdl, overrideTextureName, modelName, s => ExactStem(s) is not null);
        if (stem is null)
            LogMissing(modelName + " (texture)", "no import namespace resolves a texture unit");
        return stem;
    }

    /// <summary>The decoded diffuse texture for a missile model — resolved via
    /// <see cref="TextureStemFor"/> and decoded from the exact on-disk unit — or
    /// null when unavailable (logged once → caller keeps the solid fallback,
    /// never an invisible missile).</summary>
    public Texture2D? TextureFor(string modelName, string? overrideTextureName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;
        if (_textures.TryGetValue(modelName, out var t)) return t;
        var stem = TextureStemFor(modelName, overrideTextureName);
        if (stem is null) return null;
        var exact = ExactStem(stem);
        if (exact is null) { LogMissing(stem, "not present"); return null; }
        try
        {
            var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, exact + ".mdl"));
            var surf = new Texture2D(_gd, mdlTex.Width, mdlTex.Height);
            surf.SetData(MdlTextureDecoder.ToPremultipliedRgba(mdlTex));
            _textures[modelName] = surf;
            return surf;
        }
        catch (Exception ex)
        {
            LogMissing(exact, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// P593b — the per-model nose-alignment: a prerotation that brings the
    /// model's longest (nose) axis onto +Z, so a Y-forward missile like
    /// <c>mis404</c> still flies nose-first under the caller's +Z→forward world
    /// transform. Identity for the dominant Z-forward models.
    /// </summary>
    private static Matrix NoseAlignFor(MdlFile file)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var m in file.Meshes)
            for (int v = 0; v < m.VertexCount; v++)
            {
                float x = m.Positions[3 * v], y = m.Positions[3 * v + 1], z = m.Positions[3 * v + 2];
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            }
        float ex = maxX - minX, ey = maxY - minY, ez = maxZ - minZ;
        if (ey >= ex && ey >= ez) return Matrix.CreateRotationX(MathHelper.PiOver2);   // +Y → +Z (mis404)
        if (ex >= ey && ex >= ez) return Matrix.CreateRotationY(-MathHelper.PiOver2);  // +X → +Z (defensive)
        return Matrix.Identity;                                                          // +Z forward (dominant)
    }

    /// <summary>
    /// P593b — startup self-check: resolve mesh + texture for every distinct
    /// missile model in the core and report any that would render lit-solid
    /// (mesh or texture unresolved). Returns the number of lit-solid fallbacks
    /// (0 on the real 221 core). Mirrors P593's
    /// <see cref="ProjectileModelRenderer.AuditDistinctModelTextures"/>.
    /// </summary>
    public int AuditDistinctModelTextures(IEnumerable<string> modelNames, Action<string>? log = null)
    {
        int litSolid = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modelNames)
        {
            if (string.IsNullOrWhiteSpace(m) || !seen.Add(m.Trim())) continue;
            var stem = m.Trim();
            var geo = MeshFor(stem);
            var tex = TextureStemFor(stem, null);
            if (geo is null || tex is null)
            {
                litSolid++;
                log?.Invoke($"[monogame] P593b missile model '{stem}' mesh={(geo is null ? "MISSING" : "ok")} texture={(tex is null ? "LIT-SOLID" : "ok")}");
            }
        }
        return litSolid;
    }

    private void LogMissing(string key, string why)
    {
        if (_missingLogged.Add(key))
            Console.WriteLine($"[monogame] missile model '{key}.mdl' unavailable ({why}) — lit-solid fallback");
    }

    /// <summary>
    /// Draw a missile at the caller's world transform (nose-aligned +
    /// camera-relative translation), textured with the model's real native
    /// texture when resolved, else lit-solid. Sector-lit via
    /// <see cref="SectorLightingEffect"/> when <paramref name="lighting"/> is
    /// supplied (P530), matching the missile path's native bidirectional light.
    /// Restores render state after (the P529 discipline).
    /// </summary>
    public void Draw(Matrix world, Matrix view, Matrix projection, string modelName,
        Texture2D? texture, SectorLighting? lighting = null)
    {
        if (!_models.TryGetValue(modelName, out var geo))
        {
            geo = MeshFor(modelName);
            if (geo is null) return;
        }
        var nose = _noseAlign.TryGetValue(modelName, out var na) ? na : Matrix.Identity;

        var gd = _gd;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullNone;
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        _effect.World = nose * world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.Texture = texture;
        _effect.TextureEnabled = texture is not null;
        if (lighting is { } lp)
            SectorLightingEffect.Apply(_effect, lp);
        else if (texture is not null)
        {
            _effect.EnableDefaultLighting();
            _effect.AmbientLightColor = new Vector3(0.32f, 0.34f, 0.36f);
        }
        else
        {
            _effect.EnableDefaultLighting();
            _effect.AmbientLightColor = new Vector3(0.4f, 0.42f, 0.45f);
        }
        _effect.CurrentTechnique.Passes[0].Apply();

        gd.SetVertexBuffer(geo.Vertices);
        gd.Indices = geo.Indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geo.TriangleCount);

        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
    }

    public void Dispose()
    {
        foreach (var g in _models.Values) g.Dispose();
        foreach (var t in _textures.Values) t.Dispose();
        _effect.Dispose();
        _cullNone.Dispose();
    }
}
