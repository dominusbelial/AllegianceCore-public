using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P545 — headless tests that each renderer's native art name resolves to the
/// real file in the artwork tree and decodes to the expected payload, via the
/// GPU-free <see cref="NativeEffectTextures.Resolve"/>. Reads the actual
/// <c>.mdl</c> files from <see cref="AssetSources.ResolveRoot"/> (file IO only —
/// no graphics device), so these assert the real native-texture mapping the
/// renderers use (e_smokebmp / e_firebmp scripts, fx09 strip, fx11/f103 binaries,
/// f101, and the missing-art → <see cref="NativeEffectTextures.NativeTextureKind.None"/>
/// fallback path).
/// </summary>
public class NativeEffectTextureResolutionTests
{
    private static readonly string ArtRoot = AssetSources.ResolveRoot();

    private static byte[] ReadStem(string stem)
    {
        string path = Path.Combine(ArtRoot, NativeEffectTextures.AssetFile(stem));
        Assert.True(File.Exists(path), $"artwork file '{path}' should exist");
        return File.ReadAllBytes(path);
    }

    /// <summary>Every renderer's native art name → the file it resolves to
    /// (scope item: "each renderer resolves its native texture name").</summary>
    [Theory]
    [InlineData(NativeEffectTextures.SmokeStem, "e_smokebmp.mdl")]
    [InlineData(NativeEffectTextures.FireStem, "e_firebmp.mdl")]
    [InlineData(NativeEffectTextures.TeleStem, "e_telebmp.mdl")]
    [InlineData(NativeEffectTextures.ShieldFlareStem, "fx09bmp.mdl")]
    [InlineData(NativeEffectTextures.HullHitNormalStem, "fx11bmp.mdl")]
    [InlineData(NativeEffectTextures.HullHitTangentStem, "f103bmp.mdl")]
    [InlineData(NativeEffectTextures.GlowStrobeStem, "f101bmp.mdl")]
    public void Renderer_stem_resolves_to_the_native_art_file(string stem, string expectedFile)
    {
        Assert.Equal(expectedFile, NativeEffectTextures.AssetFile(stem));
        Assert.True(File.Exists(Path.Combine(ArtRoot, expectedFile)),
            $"expected artwork '{expectedFile}' present for stem '{stem}'");
    }

    [Fact]
    public void Smoke_script_resolves_to_e_smoke_png()
    {
        var res = NativeEffectTextures.Resolve(ReadStem(NativeEffectTextures.SmokeStem));
        Assert.Equal(NativeEffectTextures.NativeTextureKind.Png, res.Kind);
        Assert.Equal("e_smoke.png", res.PngName);
        Assert.True(File.Exists(Path.Combine(ArtRoot, res.PngName!)));
    }

    [Fact]
    public void Fire_and_tele_scripts_resolve_to_their_pngs()
    {
        var fire = NativeEffectTextures.Resolve(ReadStem(NativeEffectTextures.FireStem));
        Assert.Equal(NativeEffectTextures.NativeTextureKind.Png, fire.Kind);
        Assert.Equal("e_fire.png", fire.PngName);

        var tele = NativeEffectTextures.Resolve(ReadStem(NativeEffectTextures.TeleStem));
        Assert.Equal(NativeEffectTextures.NativeTextureKind.Png, tele.Kind);
        Assert.Equal("e_tele.png", tele.PngName);
    }

    [Fact]
    public void Shield_flare_fx09_is_a_nine_frame_binary_strip()
    {
        var res = NativeEffectTextures.Resolve(ReadStem(NativeEffectTextures.ShieldFlareStem));
        Assert.Equal(NativeEffectTextures.NativeTextureKind.Binary, res.Kind);
        Assert.Equal(576, res.Texture!.Width);
        Assert.Equal(64, res.Texture.Height);
        Assert.Equal(9, res.Frames);   // AnimatedImage slices width/height frames
    }

    [Fact]
    public void Hull_hit_fx11_and_f103_resolve_as_binary_textures()
    {
        foreach (string stem in new[]
        {
            NativeEffectTextures.HullHitNormalStem,   // fx11 (AWF_EFFECT_NORMAL)
            NativeEffectTextures.HullHitTangentStem,  // f103 (AWF_EFFECT_TANGENT)
            NativeEffectTextures.GlowStrobeStem,      // f101 (glow + strobe)
        })
        {
            var res = NativeEffectTextures.Resolve(ReadStem(stem));
            Assert.Equal(NativeEffectTextures.NativeTextureKind.Binary, res.Kind);
            Assert.NotNull(res.Texture);
            Assert.True(res.Texture!.Width >= 1 && res.Texture.Height >= 1);
        }
    }

    [Fact]
    public void Missing_art_resolves_to_none_and_never_a_texture()
    {
        // A stem whose file is absent → the resolver yields None (the renderers
        // then fall back to the logged soft sprite — never an invisible effect).
        var res = NativeEffectTextures.Resolve(new byte[] { 0, 1, 2, 3 });
        Assert.Equal(NativeEffectTextures.NativeTextureKind.None, res.Kind);
        Assert.Null(res.Texture);

        var empty = NativeEffectTextures.Resolve(System.Array.Empty<byte>());
        Assert.Equal(NativeEffectTextures.NativeTextureKind.None, empty.Kind);
    }

    [Fact]
    public void Non_mdl_bytes_resolve_to_none_not_a_crash()
    {
        // A binary payload that isn't a valid RGB565 .mdl (bad magic) degrades
        // gracefully to None — the fallback path, no throw.
        var junk = new byte[256];
        var res = NativeEffectTextures.Resolve(junk);
        Assert.Equal(NativeEffectTextures.NativeTextureKind.None, res.Kind);
    }
}
