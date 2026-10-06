using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P547 — headless tests for the custom-shader pipeline: the runtime loader
/// (null + log-once for missing/corrupt .mgfxo), the staleness test (an edited
/// .fx without a rebuild fails), the soft-fade factor, the linear-depth
/// encode/decode round-trip, the settings defaults (bloom Off, soft particles
/// On in Enhanced / absent in Native), and the "every effect parameter set from
/// code" guard (the shader's declared uniforms match exactly what the renderers
/// set — GL ignores shader parameter defaults).
/// </summary>
public class P547ShaderTests
{
    // ── 1) settings defaults ──────────────────────────────────────────────
    [Fact]
    public void Bloom_defaults_to_off_and_soft_particles_default_on_in_enhanced()
    {
        var o = new Game1.LaunchOptions();
        Assert.False(o.Bloom, "bloom must default OFF");
        Assert.True(o.EnhancedVfx, "Enhanced is the P546 default");
        Assert.True(o.SoftParticles, "soft particles default ON in Enhanced");
        // Combined effective enable: on in Enhanced, absent in Native.
        Assert.True(Game1.LaunchOptions.EffectiveSoftParticles(enhancedVfx: true, softParticles: true));
        Assert.False(Game1.LaunchOptions.EffectiveSoftParticles(enhancedVfx: false, softParticles: true),
            "soft particles are ABSENT in Native");
    }

    // ── 2) loader: missing / corrupt -> null, logged once ─────────────────
    [Fact]
    public void Loader_returns_null_and_logs_once_for_missing_mgfxo()
    {
        var cache = new ShaderAssetCache(_ => null);
        // Missing file: the stream opener throws.
        Func<Stream> missing = () => throw new FileNotFoundException("gone");
        Assert.Null(cache.LoadFrom("Content/Shaders/Nope.ogl.mgfxo", missing));
        Assert.Equal(1, cache.LoggedFailureCount);
        // Second attempt: still null, still logged ONCE.
        Assert.Null(cache.LoadFrom("Content/Shaders/Nope.ogl.mgfxo", missing));
        Assert.Equal(1, cache.LoggedFailureCount);
        Assert.True(cache.WasAttempted("Content/Shaders/Nope.ogl.mgfxo"));
    }

    [Fact]
    public void Loader_returns_null_and_logs_once_for_corrupt_mgfxo()
    {
        // Compile delegate throws on the (corrupt) bytes -> null, logged once.
        var cache = new ShaderAssetCache(bytes => throw new InvalidOperationException("bad shader bytes"));
        Func<Stream> corrupt = () => new MemoryStream(new byte[] { 1, 2, 3, 4 });
        Assert.Null(cache.LoadFrom("Content/Shaders/Bad.ogl.mgfxo", corrupt));
        Assert.Equal(1, cache.LoggedFailureCount);
        Assert.Null(cache.LoadFrom("Content/Shaders/Bad.ogl.mgfxo", corrupt));
        Assert.Equal(1, cache.LoggedFailureCount);
    }

    [Fact]
    public void Loader_caches_results_compiles_once()
    {
        // A path whose compile succeeds (returns an effect object, here null is
        // fine as a stand-in) is attempted exactly ONCE across repeated Loads.
        var compiled = new List<byte[]>();
        var cache = new ShaderAssetCache(bytes => { compiled.Add(bytes); return null; });
        var r1 = cache.LoadFrom("Content/Shaders/Ok.ogl.mgfxo", () => new MemoryStream(new byte[] { 9 }));
        var r2 = cache.LoadFrom("Content/Shaders/Ok.ogl.mgfxo", () => new MemoryStream(new byte[] { 9 }));
        Assert.Single(compiled);          // compiled exactly once (result cached)
        Assert.Equal(0, cache.LoggedFailureCount);
        Assert.True(cache.WasAttempted("Content/Shaders/Ok.ogl.mgfxo"));
        Assert.Same(r1, r2);              // same (null) cached result returned
        cache.Dispose();
    }

    // ── 3) soft-fade factor ───────────────────────────────────────────────
    [Theory]
    [InlineData(0.040f, 0.040f, 0.004f, 0.0f)]   // diff 0 -> 0 (melted at the surface)
    [InlineData(0.042f, 0.038f, 0.004f, 1.0f)]   // diff == softness -> 1
    [InlineData(0.050f, 0.036f, 0.004f, 1.0f)]   // beyond -> saturated 1
    [InlineData(0.034f, 0.046f, 0.004f, 0.0f)]   // scene nearer (negative diff) -> 0
    [InlineData(0.041f, 0.039f, 0.004f, 0.5f)]   // half the softness -> 0.5
    public void SoftFade_factor_ramps_with_depth_difference(float scene, float particle, float softness, float expected)
    {
        Assert.Equal(expected, LinearDepth.SoftFade(scene, particle, softness), 5);
    }

    // ── 4) linear-depth encode/decode round-trip ─────────────────────────
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.00012f)]
    [InlineData(0.017f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(0.999f)]
    public void LinearDepth_encode_decode_round_trips(float value)
    {
        var c = LinearDepth.Encode(value);
        float back = LinearDepth.Decode(c);
        Assert.Equal(value, back, 5);
    }

    [Fact]
    public void LinearDepth_normalize_and_inv_range()
    {
        Assert.Equal(0f, LinearDepth.Normalize(1f, 1f, 6000f), 6);
        Assert.Equal(0.5f, LinearDepth.Normalize(1f + 0.5f * (6000f - 1f), 1f, 6000f), 4);
        Assert.Equal(1f, LinearDepth.Normalize(6000f, 1f, 6000f), 6);
        Assert.Equal(1f / 5999f, LinearDepth.InvRange(1f, 6000f), 8);
    }

    // ── 5) staleness: every committed .mgfxo matches its .fx source hash ──
    [Fact]
    public void Mgfxo_staleness_each_committed_shader_matches_its_source_hash()
    {
        string shaderDir = RepoGameShadersDir();
        string outDir = RepoContentShadersDir();
        (string Stem, string Fx, string Mgfxo, string Sha)[] expected =
        {
            ("Bloom", Path.Combine(shaderDir, "Bloom.fx"),
                Path.Combine(outDir, "Bloom.ogl.mgfxo"), Path.Combine(outDir, "Bloom.fx.sha256")),
            ("LinearDepth", Path.Combine(shaderDir, "LinearDepth.fx"),
                Path.Combine(outDir, "LinearDepth.ogl.mgfxo"), Path.Combine(outDir, "LinearDepth.fx.sha256")),
            ("SoftParticle", Path.Combine(shaderDir, "SoftParticle.fx"),
                Path.Combine(outDir, "SoftParticle.ogl.mgfxo"), Path.Combine(outDir, "SoftParticle.fx.sha256")),
        };
        foreach (var (stem, fx, mgfxo, sha) in expected)
        {
            Assert.True(File.Exists(fx), $"shader source missing: {fx}");
            Assert.True(File.Exists(mgfxo), $"compiled shader missing: {mgfxo} — run scripts/build-shaders.sh");
            Assert.True(new FileInfo(mgfxo).Length > 0, $"compiled shader empty: {mgfxo}");
            Assert.True(File.Exists(sha), $"staleness record missing: {sha} — run scripts/build-shaders.sh");

            string current = Sha256Hex(File.ReadAllBytes(fx));
            string recorded = File.ReadAllText(sha).Trim().Split(' ', '\t')[0];
            // An edited .fx without a rebuild makes current != recorded -> FAIL.
            Assert.Equal(recorded, current);
        }
    }

    // ── 6) every effect parameter set from code (GL ignores defaults) ────
    [Fact]
    public void Every_effect_parameter_is_set_from_code()
    {
        // The renderers expose exactly the parameters they set every draw; the
        // shader must declare exactly those (no extra param the code forgets,
        // no param the code sets that doesn't exist).
        AssertParametersEqual("SoftParticle", "SoftParticle.fx", SoftParticleRenderer.RequiredParameters);
        AssertParametersEqual("Bloom", "Bloom.fx", BloomPostProcessor.RequiredParameters);
        AssertParametersEqual("LinearDepth", "LinearDepth.fx", LinearDepth.DepthPassParameters);
    }

    private static void AssertParametersEqual(string label, string fxFileName, IEnumerable<string> required)
    {
        string fx = File.ReadAllText(Path.Combine(RepoGameShadersDir(), fxFileName));
        var declared = DeclaredTopLevelUniforms(fx);
        var req = required.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var dec = declared.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(req, dec);
    }

    /// <summary>Top-level uniform declarations in a .fx. Robust brace-depth
    /// walk (counting '{'/'}' per line, so <c>};</c> sampler closings work):
    /// only lines at brace depth 0 (outside structs/sampler blocks/function
    /// bodies) that are a scalar/matrix/texture <c>TYPE NAME</c> declaration
    /// (optionally with a default) count — struct/vertex fields (contain ':')
    /// and function signatures (end in '(') are excluded, and leading
    /// whitespace is tolerated.</summary>
    private static List<string> DeclaredTopLevelUniforms(string fx)
    {
        var names = new List<string>();
        int depth = 0;
        foreach (var raw in fx.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("//") || line.StartsWith('#')) continue;
            // Strip a trailing // comment before parsing (comments may contain
            // ':' or parentheses that would confuse the declaration parse).
            if (line.Contains("//"))
                line = line.Substring(0, line.IndexOf("//", StringComparison.Ordinal)).Trim();
            if (line.Length == 0) continue;
            if (line.Contains(':')) continue;          // struct/vertex semantic field
            var m = Regex.Match(line,
                @"^(float|float2|float3|float4|float4x4|int|texture)\s+([A-Za-z_][A-Za-z0-9_]*)\b");
            if (depth == 0 && m.Success)
            {
                string after = line.Substring(m.Length).TrimStart();
                // Must be a declaration (`;` or `= default;`), not a function sig `(`.
                if (after.StartsWith(";") || after.StartsWith("="))
                    names.Add(m.Groups[2].Value);
            }
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
            if (depth < 0) depth = 0;
        }
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    // ── helpers ──────────────────────────────────────────────────────────
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("cannot locate AllegianceCore repo root");
        return dir;
    }

    private static string RepoGameShadersDir()
        => Path.Combine(RepoRoot(), "src", "Allegiance.ClientCore.Game", "Shaders");

    private static string RepoContentShadersDir()
        => Path.Combine(RepoRoot(), "src", "Allegiance.ClientCore.MonoGame", "Content", "Shaders");

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
