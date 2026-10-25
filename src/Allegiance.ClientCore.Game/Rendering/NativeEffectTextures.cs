using Allegiance.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P545 — the shared cache of the original game's native <em>effect</em>
/// textures, decoded once per stem and reused by every particle/effect
/// renderer that used to draw a procedural white soft blob
/// (<c>GenerateSoftSprite</c>). Real art replaces the white sprite, and the
/// native dim/additive tint (<see cref="NativeParticleCurves"/>) keeps dense
/// combat and missile launches from stacking into a solid white patch.
/// </summary>
/// <remarks>
/// <para><b>Which files / stems.</b> A stem is the artwork-file base name the
/// native engine references — <c>e_smoke</c>, <c>e_fire</c>, <c>e_tele</c>,
/// <c>fx09</c> (shield flare), <c>fx11</c>+<c>f103</c> (hull-hit
/// normal/tangent), <c>f101</c> (afterburner glow + strobe), <c>f02</c>
/// (aleph), <c>trail</c> (trail ribbon). The resolver is deliberately
/// stem-oriented so the renderers can request by the native art macro name.</para>
/// <para><b>Two physical encodings.</b> Most effect art is a binary RGB565
/// <c>.mdl</c> texture (decoded by <see cref="MdlTextureDecoder"/>, like the
/// explosions). Three — <c>e_smokebmp.mdl</c>, <c>e_firebmp.mdl</c>,
/// <c>e_telebmp.mdl</c> — are tiny <c>ImageFromFile(&quot;&lt;stem&gt;.png&quot;,…)</c>
/// model scripts that reference a real PNG (the original artwork ships the
/// smoke/fire/tele images as PNG). The loader detects the script header and
/// loads the referenced PNG via <see cref="Texture2D.FromStream"/>. Either
/// way the result is cached per stem.</para>
/// <para><b>Blend.</b> Every one of these native effect decals is drawn with
/// the additive default blend (<c>Context::DrawDecal</c>, default
/// <c>BlendModeAdd</c>, <c>engine/context.h:929</c>): SRCALPHA/ONE
/// (<c>engine/RasterD3D9.cpp:295-302</c>). The real textures are opaque
/// (RGB565 has no alpha; the PNGs carry alpha 255), so under additive
/// blending each sprite's black surround contributes 0 and only the painted
/// pixels add — native's exact look. The dim <em>tint colour</em>
/// (<see cref="NativeParticleCurves.SmokeColor"/> et al.) is what keeps the
/// stack grey, not white.</para>
/// <para><b>Fallback.</b> If a stem's file is missing or undecodable, the
/// renderer falls back to a single shared procedural soft sprite
/// (<see cref="SoftFallback"/>), logged once — never an invisible effect and
/// never a per-frame re-log. This is the only remaining use of the white
/// blob (task scope item 6).</para>
/// </remarks>
public sealed class NativeEffectTextures : IDisposable
{
    // ── Stem constants (the native art names; each renderer resolves one) ──
    /// <summary>Smoke puff artwork (particlegeo.cpp SmokeBehavior) — a PNG script.</summary>
    public const string SmokeStem = "e_smoke";
    /// <summary>Fire puff artwork (FireBehavior) — a PNG script.</summary>
    public const string FireStem = "e_fire";
    /// <summary>Teleport/warp spark artwork (SparkBehavior) — a PNG script.</summary>
    public const string TeleStem = "e_tele";
    /// <summary>Shield-flare animated lens (AWF_SHIELD_FLARE_TEXTURE).</summary>
    public const string ShieldFlareStem = "fx09";
    /// <summary>Hull-hit normal decal (AWF_EFFECT_NORMAL).</summary>
    public const string HullHitNormalStem = "fx11";
    /// <summary>Hull-hit tangent decal (AWF_EFFECT_TANGENT).</summary>
    public const string HullHitTangentStem = "f103";
    /// <summary>Afterburner glow + strobe decal (AWF_EFFECT_AFTERBURNER_GLOW /
    /// AWF_EFFECT_STROBE).</summary>
    public const string GlowStrobeStem = "f101";
    /// <summary>Aleph star / warp aperture (AWF_EFFECT_ALEPH_STAR).</summary>
    public const string AlephStem = "f02";

    /// <summary>The <c>.mdl</c> relative path the resolver attempts for a stem
    /// (the port's artwork convention: <c>&lt;stem&gt;bmp.mdl</c>).</summary>
    public static string AssetFile(string stem) => stem + "bmp.mdl";

    /// <summary>True when the first bytes read like an <c>ImageFromFile</c>
    /// model script (<c>use &quot;model&quot;;</c>), versus a binary
    /// <c>.mdl</c> texture (magic <c>0x0DEBADF00</c>).</summary>
    public static bool IsPngScript(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 8) return false;
        // "use " is the ASCII header of every model script we care about.
        return bytes[0] == (byte)'u' && bytes[1] == (byte)'s' && bytes[2] == (byte)'e';
    }

    /// <summary>Extract the PNG filename referenced by an <c>ImageFromFile</c>
    /// script body, or null if none.</summary>
    public static string? ExtractPngReference(string script)
    {
        var m = PngReference.Match(script);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Number of square frames a strip of the given size slices into
    /// (cell = height; native AnimatedImage — width/height frames).</summary>
    public static int SliceFrameCount(int width, int height) =>
        height <= 0 ? 0 : Math.Max(width / height, 1);

    /// <summary>The PNG-script stem detector's authoritative header marker.</summary>
    private static readonly Regex PngReference = new(@"ImageFromFile\(\s*""([A-Za-z0-9_\-]+\.png)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly GraphicsDevice _gd;
    private readonly IAssetSource _assets;
    private readonly Dictionary<string, Texture2D[]?> _films = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingLogged = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? _softFallback;

    private NativeEffectTextures(GraphicsDevice gd, IAssetSource assets)
    {
        _gd = gd;
        _assets = assets;
    }

    /// <summary>Create a cache over <paramref name="assets"/> (typically
    /// <see cref="AssetSources.Artwork"/>). Nothing is eagerly loaded —
    /// textures decode lazily on first <see cref="TextureFor"/> request.</summary>
    public static NativeEffectTextures Load(GraphicsDevice gd, IAssetSource assets) =>
        new(gd, assets);

    /// <summary>The shared procedural soft-sprite fallback, generated once on
    /// first use. White, alpha 1 at centre → 0 at rim (the exact sprite the
    /// renderers used to always draw) — used only when a native stem is
    /// missing.</summary>
    public Texture2D SoftFallback
    {
        get
        {
            if (_softFallback is null) _softFallback = GenerateSoftSprite(_gd, 64);
            return _softFallback;
        }
    }

    /// <summary>
    /// The decoded native texture for <paramref name="stem"/> (cached per
    /// stem), or <c>null</c> if the artwork is missing/undecodable (logged
    /// once — the caller falls back to <see cref="SoftFallback"/>). Resolves
    /// both the binary <c>.mdl</c> textures and the PNG-script
    /// <c>&lt;stem&gt;bmp.mdl</c>s (see class remarks). Returns the first frame
    /// for an animated strip (see <see cref="FramesFor"/>).
    /// </summary>
    public Texture2D? TextureFor(string stem)
    {
        var frames = FramesFor(stem);
        return frames is { Length: > 0 } ? frames[0] : null;
    }

    /// <summary>
    /// The decoded frames for an animated strip <paramref name="stem"/> (e.g.
    /// <c>fx09</c> → its 9 square lens frames), or <c>null</c> if missing/
    /// undecodable. Non-strip textures (smoke/fire/tele PNGs) yield a single
    /// frame. Sliced the same way <see cref="ExplosionAssets"/> slices the
    /// explosion strips (cell = strip height).
    /// </summary>
    public Texture2D[]? FramesFor(string stem)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        if (_films.TryGetValue(stem, out var arr)) return arr;

        Texture2D[]? loaded = null;
        try
        {
            loaded = LoadFrames(stem);
        }
        catch (Exception ex)
        {
            LogMissing(stem, ex.Message);
            return null;
        }

        _films[stem] = loaded;
        return loaded;
    }

    /// <summary>Kind of decoded payload a stem's artwork resolves to.</summary>
    public enum NativeTextureKind { None, Png, Binary }

    /// <summary>Pure result of resolving a stem's raw bytes (GPU-free, so
    /// unit-testable headless): <see cref="Png"/> script → referenced PNG name,
    /// binary RGB565 <see cref="Binary"/> → decoded <see cref="MdlTexture"/>
    /// (and <see cref="Frames"/> square frames), else <see cref="None"/>.</summary>
    public readonly record struct NativeTextureResolution(
        NativeTextureKind Kind, string? PngName, MdlTexture? Texture, int Frames)
    {
        public static readonly NativeTextureResolution Fail = new(NativeTextureKind.None, null, null, 0);
    }

    /// <summary>
    /// Pure decode of a stem's <c>.mdl</c> bytes — no graphics device, so tests
    /// can assert how each native art name resolves (PNG-script vs binary RGB565
    /// strip and its frame count) headlessly.
    /// </summary>
    public static NativeTextureResolution Resolve(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return NativeTextureResolution.Fail;
        if (IsPngScript(bytes))
        {
            string? png = ExtractPngReference(System.Text.Encoding.ASCII.GetString(bytes));
            return png is null
                ? NativeTextureResolution.Fail
                : new NativeTextureResolution(NativeTextureKind.Png, png, null, 1);
        }
        try
        {
            var tex = MdlTextureDecoder.Decode(bytes);
            return new NativeTextureResolution(NativeTextureKind.Binary, null, tex, SliceFrameCount(tex.Width, tex.Height));
        }
        catch
        {
            return NativeTextureResolution.Fail;
        }
    }

    private Texture2D[]? LoadFrames(string stem)
    {
        string rel = AssetFile(stem);
        if (!_assets.Exists(rel))
        {
            LogMissing(stem, "not present");
            return null;
        }
        var bytes = AssetSources.ReadAllBytes(_assets, rel);
        var res = Resolve(bytes);
        switch (res.Kind)
        {
            case NativeTextureKind.Png:
                if (!_assets.Exists(res.PngName!))
                {
                    LogMissing(stem, $"script has no resolvable {res.PngName}");
                    return null;
                }
                using (var stream = _assets.OpenRead(res.PngName!))
                    return new[] { Texture2D.FromStream(_gd, stream) };
            case NativeTextureKind.Binary:
                return SliceStrip(res.Texture!);
            default:
                LogMissing(stem, "undecodable");
                return null;
        }
    }

    /// <summary>Split a decoded binary <c>.mdl</c> strip into its square frames
    /// (cell = the strip height, width/height frames — native AnimatedImage
    /// slicing, as <see cref="ExplosionAssets"/>).</summary>
    private Texture2D[] SliceStrip(MdlTexture tex)
    {
        int cells = SliceFrameCount(tex.Width, tex.Height);
        var frames = new Texture2D[cells];
        for (int c = 0; c < cells; c++)
            frames[c] = UploadOpaque(tex, c * tex.Height, 0, tex.Height, tex.Height);
        return frames;
    }

    /// <summary>Upload a rectangular region of a decoded RGB565 texture as
    /// opaque RGBA (alpha 255) — additive rendering makes the black surround
    /// vanish, exactly like <see cref="ExplosionAssets"/> /
    /// <see cref="ProjectileSpriteRenderer"/>.</summary>
    private Texture2D UploadOpaque(MdlTexture tex, int ox, int oy, int w, int h)
    {
        var surface = new Texture2D(_gd, w, h);
        var colors = new Color[w * h];
        int src = tex.Width;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = ((oy + y) * src + (ox + x)) * 4;
                colors[y * w + x] = new Color(
                    (int)tex.Rgba[si], (int)tex.Rgba[si + 1], (int)tex.Rgba[si + 2], 255);
            }
        surface.SetData(colors);
        return surface;
    }

    private void LogMissing(string stem, string why)
    {
        if (_missingLogged.Add(stem))
            Console.WriteLine($"[monogame] native effect texture '{stem}bmp.mdl' unavailable ({why}) — soft-sprite fallback");
    }

    /// <summary>The exact white soft radial sprite the particle renderers used
    /// to generate (the fallback only, when native art is missing).</summary>
    private static Texture2D GenerateSoftSprite(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float inv = 2f / size;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * inv - 1f;
                float ny = (y + 0.5f) * inv - 1f;
                float r = MathF.Sqrt(nx * nx + ny * ny);
                float a = MathHelper.Clamp(1f - r, 0f, 1f);
                a *= a;
                data[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetData(data);
        return tex;
    }

    public void Dispose()
    {
        foreach (var arr in _films.Values)
            if (arr is not null)
                foreach (var t in arr) t?.Dispose();
        _softFallback?.Dispose();
    }
}
