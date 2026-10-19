using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P544 — draws the 3D-model projectile rounds (EW Lsr Can 1/2, EW Mini-LC 1/2,
/// PE MassDriver) using their <em>real</em> native <c>.mdl</c> meshes parsed at
/// runtime from <c>ALLEGIANCE_ARTWORK_PATH</c> — the same runtime-asset
/// convention as <see cref="ProbeRenderer"/> / <see cref="MissileRenderer"/>
/// (geometry comes from a real <c>.mdl</c> parsed at runtime, not a
/// content-pipeline asset). Native's projectile-model branch
/// (<c>projectileIGC.cpp:50-56</c>) <c>Load</c>s the mesh named by
/// <c>modelName</c> and <c>SetRadius(radius)</c> — the mesh renders at its
/// authored size and <c>radius</c> describes its half-length (measured: the
/// <c>dn_ptlascan1/2</c> beam meshes span ~250 units, matching their radius-125
/// half-length), so no uniform rescale is applied.
/// </summary>
/// <remarks>
/// <para><b>Orientation / spin.</b> The beam meshes' long axis is model +Z
/// (measured centroid z = 125 for the ~250-unit beams), so the mesh's +Z is
/// aligned to the projectile's travel direction and the native
/// <c>Rotation(forward, rotation)</c> (<c>:76-80</c>) rolls the model about that
/// travel axis at <c>rotation</c> rad/s. Each model's texture is
/// <c>&lt;modelName&gt;bmp.mdl</c> (the mesh's own material bitmap, decoded like
/// any sprite).</para>
/// <para><b>Culling / winding.</b> Cull-mode none — the small projectile models
/// render both faces so no interior hole ever appears regardless of the
/// DirectX-authored winding (the laser beams are thin, so double-sided is
/// harmless and safest).</para>
/// </remarks>
public sealed class ProjectileModelRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly Dictionary<string, Geometry> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MdlFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);
    // P593 — a case-insensitive index of every .mdl basename in the artwork
    // root (stem -> exact on-disk stem), the P586b proven pattern: a projectile
    // model's imported texture namespace may be capitalised on disk (e.g.
    // "Wep07bbmp.mdl") and a case-sensitive File.Exists on Linux would miss it →
    // lit-solid white. The index makes the texture probe case-insensitive.
    private readonly Dictionary<string, string> _diskStems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HdMaterialRenderer _hd;
    private readonly bool _ownsHd;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _cullNone;

    public ProjectileModelRenderer(GraphicsDevice gd, Assets.IAssetSource assets, HdMaterialRenderer? hd = null)
    {
        _gd = gd;
        _hd = hd ?? new HdMaterialRenderer(gd, NativeMaterialParts.NativeReader(assets));
        _ownsHd = hd is null;
        _assets = assets;
        _effect = new BasicEffect(gd) { TextureEnabled = true };
        _cullNone = new RasterizerState { CullMode = CullMode.None };
        foreach (var rel in assets.Enumerate("."))
            if (rel.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                _diskStems[Path.GetFileNameWithoutExtension(rel)] = Path.GetFileNameWithoutExtension(rel);
    }

    /// <summary>The compiled mesh for a <c>.mdl</c> model stem (parsed + built
    /// once, cached), or null if the file is missing/undecodable (logged once).</summary>
    public Geometry? MeshFor(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;
        if (_models.TryGetValue(modelName, out var g)) return g;
        if (!TryParse(modelName, out var file)) return null;
        try
        {
            var geo = Geometry.Build(_gd, file.Meshes);
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
    /// on-disk lookup via the P593 disk-stem index), so both the mesh builder and
    /// the texture resolver share one parse and the model's real imported texture
    /// namespaces are available.</summary>
    private bool TryParse(string modelName, out MdlFile file)
    {
        if (_parsed.TryGetValue(modelName, out file!)) return true;
        if (!_diskStems.TryGetValue(modelName, out var exact))
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

    /// <summary>
    /// P593 — the native texture-stem resolution for a 3D-model projectile round,
    /// factored as a pure, testable static (the exact logic
    /// <see cref="TextureFor"/> uses). Resolves the model's OWN imported texture
    /// namespace via <see cref="NativeShipResolver.ResolveTextureStem"/>
    /// (<c>thinggeo.cpp:1038-1043</c> / <c>xfile.cpp:577</c>), never a hardcoded
    /// <c>&lt;modelName&gt;bmp</c> guess — so a round whose texture unit is named
    /// differently (shared namespace, doubled-'b', capitalised on disk) still
    /// resolves its real bitmap instead of falling back to lit-solid white.
    /// <paramref name="fileExists"/> is the case-insensitive disk-stem probe
    /// (<see cref="_diskStems"/> in the renderer).
    /// </summary>
    public static string? ResolveTextureStemFor(MdlFile mdl, string modelName, Func<string, bool> fileExists)
        => NativeShipResolver.ResolveTextureStem(mdl, null, modelName, fileExists);

    /// <summary>The decoded diffuse texture for a model stem — resolved from the
    /// model's OWN imported texture namespace (native
    /// <c>m_pgeoMesh-&gt;FindTexture()</c>, <c>thinggeo.cpp:1038-1043</c> /
    /// <c>xfile.cpp:577</c>), NOT a hardcoded <c>&lt;modelName&gt;bmp</c> guess —
    /// cached, or null if unavailable (logged once). P593: this closes the
    /// "white popcorn" defect class — a 3D-model round whose texture unit is
    /// named differently from <c>&lt;modelName&gt;bmp</c> (shared namespace,
    /// doubled-'b', or capitalised on disk) previously fell back to lit-solid
    /// white; the native namespace probe (plus the P586b case-insensitive disk
    /// index) resolves its real bitmap instead, so a round never renders
    /// lit-solid.</summary>
    public Texture2D? TextureFor(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return null;
        if (_textures.TryGetValue(modelName, out var t)) return t;
        if (!TryParse(modelName, out var mdl)) return null;
        string? stem = ResolveTextureStemFor(mdl, modelName, s => _diskStems.ContainsKey(s));
        if (stem is null)
        {
            LogMissing(modelName + " (texture)", "no import namespace resolves a texture unit");
            return null;
        }
        if (!_diskStems.TryGetValue(stem, out var exact))
        {
            LogMissing(stem, "not present");
            return null;
        }
        try
        {
            var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, exact + ".mdl"));
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
    /// P593 — startup self-check: resolve mesh + texture for every distinct
    /// 3D-model projectile and report any that would fall back to lit-solid
    /// (texture unresolved). Returns the number of lit-solid fallbacks found
    /// (0 on the real 221 core). Mirrors P586b's <c>TreasureVisualResolver.SelfCheck</c>.
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
            var tex = TextureFor(stem);
            if (geo is null || tex is null)
            {
                litSolid++;
                log?.Invoke($"[monogame] P593 projectile model '{stem}' mesh={(geo is null ? "MISSING" : "ok")} texture={(tex is null ? "LIT-SOLID" : "ok")}");
            }
        }
        return litSolid;
    }

    private void LogMissing(string key, string why)
    {
        if (_missingLogged.Add(key))
            Console.WriteLine($"[monogame] projectile model '{key}.mdl' unavailable ({why}) — box fallback");
    }

    /// <summary>
    /// Draw the model oriented along <paramref name="dir"/>, rolled about travel
    /// by <paramref name="spin"/> radians, at <paramref name="centre"/>
    /// (eye-relative). Textured with the model's real bitmap when available, else
    /// lit-solid. World = align(+Z→dir) · rollZ(spin) · translate (MonoGame
    /// row-vector: align, then roll about the now-aligned +Z = travel, then move).
    /// </summary>
    public void Draw(Geometry geo, Texture2D? texture, XnaVec centre, XnaVec dir, float spin, Matrix view, Matrix projection, float scale = 1f, string? modelName = null, Allegiance.Core.Rendering.SectorLighting? lighting = null)
    {
        var travel = XnaVec.Normalize(dir);
        var world = Matrix.CreateScale(scale) * AlignZ(travel) * Matrix.CreateRotationZ(spin) * Matrix.CreateTranslation(centre);

        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.Texture = texture;
        _effect.TextureEnabled = texture is not null;
        if (texture is null)
        {
            _effect.EnableDefaultLighting();
            _effect.AmbientLightColor = new XnaVec(0.4f, 0.42f, 0.45f);
        }
        var gd = _gd;
        gd.RasterizerState = _cullNone;
        geo.Materials.Draw(gd, _hd, modelName, modelName is not null && _parsed.TryGetValue(modelName, out var mdl) ? ResolveTextureStemFor(mdl, modelName, _diskStems.ContainsKey) : null,
            texture, TextureForStem, _effect, geo.Vertices, geo.Indices, world, view, projection,
            1, lighting);
    }

    private Texture2D? TextureForStem(string stem)
    {
        if (_textures.TryGetValue(stem, out var texture)) return texture;
        if (!_diskStems.TryGetValue(stem, out var exact)) return null;
        try
        {
            var decoded = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, exact + ".mdl"));
            texture = new Texture2D(_gd, decoded.Width, decoded.Height);
            texture.SetData(MdlTextureDecoder.ToPremultipliedRgba(decoded));
            _textures[stem] = texture;
            return texture;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return null; }
    }

    private static Matrix AlignZ(XnaVec dir)
    {
        var z = XnaVec.UnitZ;
        float d = XnaVec.Dot(z, dir);
        if (Math.Abs(d) > 0.9999f)
        {
            // Already (anti-)aligned: +Z needs a 180° flip about Y for -Z.
            return d > 0f ? Matrix.Identity : Matrix.CreateRotationY(MathHelper.Pi);
        }
        var axis = XnaVec.Normalize(XnaVec.Cross(z, dir));
        float angle = MathF.Acos(Math.Clamp(d, -1f, 1f));
        return Matrix.CreateFromAxisAngle(axis, angle);
    }

    /// <summary>A compiled projectile model: one vertex/index buffer across all
    /// meshes (offsets carried across) + per-vertex normals/texcoords.</summary>
    public sealed class Geometry : IDisposable
    {
        public NativeMaterialParts Materials { get; private init; } = null!;
        public required VertexBuffer Vertices { get; init; }
        public required IndexBuffer Indices { get; init; }
        public required int TriangleCount { get; init; }

        public static Geometry Build(GraphicsDevice gd, IReadOnlyList<MdlMesh> meshes)
        {
            int vertexTotal = 0, indexTotal = 0;
            foreach (var m in meshes) { vertexTotal += m.VertexCount; indexTotal += m.Indices.Length; }
            if (vertexTotal == 0 || indexTotal == 0)
                throw new InvalidDataException("projectile model has no geometry");

            var verts = new VertexPositionNormalTexture[vertexTotal];
            var indices = new uint[indexTotal];
            int vc = 0, ic = 0;
            foreach (var m in meshes)
            {
                int vb = vc;
                for (int v = 0; v < m.VertexCount; v++)
                    verts[vc++] = new VertexPositionNormalTexture(
                        new XnaVec(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]),
                        new XnaVec(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2]),
                        new Vector2(m.TexCoords[2 * v], m.TexCoords[2 * v + 1]));
                for (int i = 0; i < m.Indices.Length; i++)
                    indices[ic++] = (uint)(m.Indices[i] + vb);
            }
            var vbuf = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
            vbuf.SetData(verts);
            var ibuf = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
            ibuf.SetData(indices);
            return new Geometry { Materials = new(meshes, verts, indices), Vertices = vbuf, Indices = ibuf, TriangleCount = indices.Length / 3 };
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
        _cullNone.Dispose();
    }
}
