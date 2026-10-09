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
/// P586 — renders a treasure with its ORIGINAL native visual, resolved per code
/// by <see cref="TreasureVisualResolver"/> (part's own model, <c>acs14</c> for
/// cash/power-up, <c>artifact</c> for the artifact, the <c>biosflag</c> decal for
/// a flag), parsed at runtime from the artwork source via the same
/// <c>MdlFile.Parse</c> / <c>NativeShipGeometry.Build</c> / <c>MdlTextureDecoder</c>
/// path <see cref="NativeShipRenderer"/> established (P566/P567). A treasure is
/// a single model drawn at its NATIVE model scale (native
/// <c>SetRadius(5.0f)</c> is only the collision radius, <c>treasureIGC.cpp:193</c>;
/// the visual scale is the model's own) with native's random orientation +
/// rotation (<c>treasureIGC.cpp:180-190</c>), made DETERMINISTIC per
/// <c>treasureId</c> (the globally-unique ObjectId) so all clients agree on the
/// tumble — a deliberate divergence from native's per-machine <c>rand()</c>,
/// mandated by the task ("deterministic per treasure id so all clients agree
/// visually").
/// </summary>
/// <remarks>
/// <para><b>Model path.</b> A <c>Part</c>/<c>Development</c>/<c>Cash</c>/<c>Powerup</c>/
/// <c>Artifact</c> treasure draws the resolved model mesh + its texture, sector-lit
/// with the PURE native ambient (0.25, P564), culling back faces with the corrected
/// winding (<see cref="NativeShipResolver.RenderIndices"/>). A <c>Flag</c> treasure
/// draws the <c>biosflag</c> texture as a camera-facing decal billboard (native
/// <c>LoadDecal(…, white, bDirectional=false, width=1.0, …)</c>,
/// <c>treasureIGC.cpp:157-171</c>), like the chaff/probe decal precedent.</para>
/// <para><b>Fallback.</b> When a model/asset is missing or undecodable (logged once),
/// <see cref="DrawModel"/> returns false and the caller draws the pre-P586 procedural
/// gold-gem placeholder (<see cref="TreasureRenderer"/>), so a gap in the artwork
/// tree never blanks a treasure.</para>
/// </remarks>
public sealed class NativeTreasureRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private readonly Assets.IAssetSource _assets;
    private readonly Dictionary<string, string> _diskStems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NativeShipRenderer.NativeShipGeometry> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MdlFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingModelLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingTextureLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<short, SpinState> _spins = new();
    private readonly BasicEffect _effect;
    private readonly BasicEffect _decalEffect;
    private readonly RasterizerState _cullCcw;

    public NativeTreasureRenderer(GraphicsDevice gd, Assets.IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
        // P586b — a case-insensitive index of every .mdl basename in the artwork
        // root, so a treasure model/texture stem resolves to its EXACT on-disk
        // name. Native runs on Windows (case-insensitive), so a namespace like
        // "wep07bbmp" must still find the capitalised "Wep07bbmp.mdl" unit;
        // Linux's case-sensitive File.Exists would otherwise miss it.
        foreach (var rel in assets.Enumerate("."))
            if (rel.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = Path.GetFileNameWithoutExtension(rel);
                if (!string.IsNullOrEmpty(baseName))
                    _diskStems[baseName] = baseName;
            }
        _effect = new BasicEffect(gd);
        _decalEffect = new BasicEffect(gd) { VertexColorEnabled = false, TextureEnabled = true };
        _cullCcw = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
    }

    /// <summary>The exact on-disk basename (without .mdl) for a stem, resolved
    /// case-insensitively (native Windows semantics), or null when no such file
    /// exists. Dictionary-value == key for canonical case; for a mismatched
    /// file (e.g. stem "wep07bbmp" → file "Wep07bbmp.mdl") the value is the
    /// on-disk spelling, which is what must be opened.</summary>
    private string? ExactStem(string stem) =>
        _diskStems.TryGetValue(stem, out var exact) ? exact : null;

    /// <summary>The compiled native geometry for a model stem (parsed + built once,
    /// cached), or null when missing/undecodable (logged once → caller falls back
    /// to the gem placeholder).</summary>
    public NativeShipRenderer.NativeShipGeometry? MeshFor(string modelStem)
    {
        if (string.IsNullOrEmpty(modelStem)) return null;
        if (_models.TryGetValue(modelStem, out var g)) return g;
        var exact = ExactStem(modelStem);
        try
        {
            if (exact is null)
            {
                LogMissingModel(modelStem, "not present");
                return null;
            }
            var file = MdlFile.Parse(AssetSources.ReadAllBytes(_assets, exact + ".mdl"));
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

    /// <summary>The decoded native texture for a texture stem, cached, or null if
    /// unavailable (logged once → caller renders lit-solid, never invisible).</summary>
    public Texture2D? TextureFor(string textureStem)
    {
        if (string.IsNullOrEmpty(textureStem)) return null;
        if (_textures.TryGetValue(textureStem, out var t)) return t;
        var exact = ExactStem(textureStem);
        try
        {
            if (exact is null)
            {
                LogMissingTexture(textureStem, "not present");
                return null;
            }
            var mdlTex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(_assets, exact + ".mdl"));
            var surf = new Texture2D(_gd, mdlTex.Width, mdlTex.Height);
            surf.SetData(MdlTextureDecoder.ToPremultipliedRgba(mdlTex));
            _textures[textureStem] = surf;
            return surf;
        }
        catch (Exception ex)
        {
            LogMissingTexture(textureStem, ex.Message);
            return null;
        }
    }

    /// <summary>The resolved texture stem for a model's own texture namespace
    /// (<see cref="NativeShipResolver.ResolveTextureStem"/>), or null when the
    /// model is undecodable / has no resolvable texture unit. The probe is
    /// case-insensitive via <see cref="ExactStem"/> so a weapon unit whose
    /// on-disk casing differs (e.g. "Wep07bbmp.mdl") is still found.</summary>
    public string? TextureStemFor(string modelStem)
    {
        if (MeshFor(modelStem) is null) return null;
        if (!_parsed.TryGetValue(modelStem, out var mdl)) return null;
        return NativeShipResolver.ResolveTextureStem(mdl, null, modelStem, s => ExactStem(s) is not null);
    }

    /// <summary>
    /// Deterministic per-treasure spin state — the port of native's random
    /// orientation + rotation (<c>treasureIGC.cpp:180-190</c>: random axis,
    /// <c>angle = random(-6,6)</c> rad/s), seeded from the globally-unique
    /// <c>treasureId</c> so every client tumbles the same pickup identically.
    /// Seeded with a fixed xor of the id (a 31-bit multiplicative hash), not
    /// <c>System.Random</c> seeded by id (whose low bits are weak).
    /// </summary>
    private SpinState Spin(short treasureId)
    {
        if (_spins.TryGetValue(treasureId, out var s)) return s;
        uint seed = (uint)treasureId;
        seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;   // xxhash-ish avalanche
        float Next01() { seed = seed * 747796405u + 2891336453u; return (seed >> 8) / (float)0xFFFFFF; }
        // Unit random axis (native RandomDirection) and rate in [-6, 6].
        float a = Next01() * MathHelper.TwoPi;
        float z = Next01() * 2f - 1f;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        var axis = new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
        var baseAngle = (Next01() * 2f - 1f) * MathHelper.Pi;
        var rate = (Next01() * 2f - 1f) * 6f;                        // random(-6, 6)
        s = new SpinState(axis, baseAngle, rate);
        _spins[treasureId] = s;
        return s;
    }

    /// <summary>The deterministic spin transform for a treasure at elapsed seconds
    /// since that treasure spawned: <c>Rotate(axis, baseAngle + rate·elapsed)</c>
    /// (native SetOrientation + SetRotation, treasureIGC.cpp:180-190).</summary>
    public Matrix SpinTransform(short treasureId, double elapsed)
    {
        var s = Spin(treasureId);
        return Matrix.CreateFromAxisAngle(s.Axis, s.BaseAngle + s.Rate * (float)elapsed);
    }

    /// <summary>
    /// Draw a resolved MODEL treasure (part / development / cash / powerup /
    /// artifact). <paramref name="world"/> is the caller's camera-relative world
    /// (translation to <c>position − cameraEye</c>); the deterministic spin and
    /// native scale are folded in before it. Returns true when geometry drew;
    /// false when the model is unavailable (caller falls back to the gem).
    /// </summary>
    public bool DrawModel(TreasureVisual visual, short treasureId, Matrix world,
        Matrix view, Matrix projection, double elapsed, SectorLighting? lighting)
    {
        if (visual.ModelStem is null) return false;
        var geo = MeshFor(visual.ModelStem);
        if (geo is null) return false;

        // Texture: honour an explicit stem (cash acs14bmp / artifact artifactbmp)
        // else resolve from the model's own texture namespace.
        string? texStem = visual.TextureStem;
        if (texStem is null) texStem = TextureStemFor(visual.ModelStem);
        var tex = texStem is not null ? TextureFor(texStem) : null;

        var gd = _gd;
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = _cullCcw;
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        // Native scale = the model's own authored size (1.0 in .mdl units); the
        // deterministic tumble is applied as a rotation around the model's centre.
        var local = SpinTransform(treasureId, elapsed) * world;

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
        _effect.Texture = tex;
        _effect.TextureEnabled = tex is not null;
        _effect.Alpha = 1f;

        _effect.CurrentTechnique.Passes[0].Apply();
        gd.SetVertexBuffer(geo.Vertices);
        gd.Indices = geo.Indices;
        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, geo.TriangleCount);

        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        return true;
    }

    /// <summary>
    /// Draw a FLAG treasure as its camera-facing <c>biosflag</c> decal billboard
    /// (native <c>LoadDecal</c>, <c>treasureIGC.cpp:157-171</c>), sized to the
    /// native pickup radius 5.0, tinted white (native's white color argument).
    /// Returns true when the decal texture drew; false when unavailable.
    /// </summary>
    public bool DrawDecal(TreasureVisual visual, short treasureId, Vector3 worldPos,
        Matrix view, Matrix projection, SectorLighting? lighting)
    {
        if (visual.TextureStem is null) return false;
        var tex = TextureFor(visual.TextureStem);
        if (tex is null) return false;

        var gd = _gd;
        gd.BlendState = BlendState.AlphaBlend;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.RasterizerState = RasterizerState.CullNone;
        gd.SamplerStates[0] = SamplerState.LinearClamp;

        // Camera-facing billboard: undo view rotation. Size = native pickup radius
        // 5.0 (treasureIGC.cpp:193) — the same legibility scale as the gem.
        Matrix viewRot = view;
        viewRot.Translation = Vector3.Zero;
        Matrix inverseViewRot = Matrix.Transpose(viewRot);   // view rotation is orthonormal
        var half = 5.0f;
        var corners = new[]
        {
            new Vector3(-half, -half, 0f), new Vector3(-half, half, 0f),
            new Vector3(half, half, 0f), new Vector3(half, -half, 0f),
        };
        var verts = new VertexPositionTexture[corners.Length];
        var idx = new short[] { 0, 1, 2, 0, 2, 3 };
        for (int i = 0; i < corners.Length; i++)
            verts[i] = new VertexPositionTexture(
                Vector3.Transform(corners[i], inverseViewRot) + worldPos,
                new Vector2(i is 1 or 2 ? 1f : 0f, i is 0 or 3 ? 1f : 0f));
        _decalEffect.Texture = tex;
        _decalEffect.World = Matrix.Identity;
        _decalEffect.View = view;
        _decalEffect.Projection = projection;
        _decalEffect.CurrentTechnique.Passes[0].Apply();
        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length, idx, 0, idx.Length / 3);

        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        return true;
    }

    private void LogMissingModel(string stem, string why)
    {
        if (_missingModelLogged.Add(stem))
            Console.WriteLine($"[monogame] native treasure model '{stem}.mdl' unavailable ({why}) — falling back to gem");
    }

    private void LogMissingTexture(string stem, string why)
    {
        if (_missingTextureLogged.Add(stem))
            Console.WriteLine($"[monogame] native treasure texture '{stem}.mdl' unavailable ({why}) — rendering lit-solid");
    }

    private readonly record struct SpinState(Vector3 Axis, float BaseAngle, float Rate);

    public void Dispose()
    {
        foreach (var g in _models.Values) g.Dispose();
        foreach (var t in _textures.Values) t.Dispose();
        _effect.Dispose();
        _decalEffect.Dispose();
        _cullCcw.Dispose();
    }
}
