using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P871 / SIM-0223 — the <c>Preload</c> rules and the content-loader wiring:
/// null-model textures, the <c>bmp</c> suffix, the alphabetic-name gate, the
/// colour-key restoration and the mipmap-generation scope (native
/// <c>WinTrek/trekigc.cpp:4912-4937</c>, caller <c>Igc/hullTypeIGC.cpp:49-51</c>).
/// </summary>
public class P871PreloadTests
{
    [Fact]
    public void A_texture_name_gets_the_native_bmp_suffix_and_a_numeric_one_is_not_a_surface()
    {
        Assert.Equal("dn_hifigbmp", PreloadRules.FromType("dn_hifig", "dn_hifig").TextureStem);
        // Igc/hullTypeIGC.cpp:49-51 — iswalpha(textureName[0]) ? textureName : NULL.
        Assert.Null(PreloadRules.FromType("hull", "12").TextureStem);
        Assert.True(PreloadRules.IsSurfaceName("shiny"));
        Assert.False(PreloadRules.IsSurfaceName("1"));
        Assert.False(PreloadRules.IsSurfaceName(""));
        Assert.False(PreloadRules.IsSurfaceName(null));
    }

    [Fact]
    public void A_null_model_name_still_preloads_the_texture()
    {
        var request = PreloadRules.FromType(null, "engine");
        Assert.Null(request.ModelStem);
        Assert.Equal("enginebmp", request.TextureStem);
        Assert.False(request.IsEmpty);
        // Both null is nothing to do.
        Assert.True(PreloadRules.FromType(null, null).IsEmpty);
        Assert.True(PreloadRules.FromType("  ", " ").IsEmpty);
    }

    [Fact]
    public void The_preload_scope_turns_colour_key_off_and_mipmaps_on_and_resets_mipmaps()
    {
        Assert.True(PreloadRules.ColorKeyHint);      // modeler default (modeler.cpp:726)
        Assert.False(PreloadRules.MipMapGeneration);
        using (PreloadRules.Begin())
        {
            Assert.False(PreloadRules.ColorKeyHint);
            Assert.True(PreloadRules.MipMapGeneration);
            // A nested scope re-enters with mipmaps ON and its close RESETS them to
            // false (native: trekigc.cpp:4936 hard-sets false, it never restores).
            using (PreloadRules.Begin())
            {
                Assert.False(PreloadRules.ColorKeyHint);
                Assert.True(PreloadRules.MipMapGeneration);
            }
            Assert.False(PreloadRules.ColorKeyHint);
            Assert.False(PreloadRules.MipMapGeneration);
        }
        Assert.True(PreloadRules.ColorKeyHint);
        Assert.False(PreloadRules.MipMapGeneration);
    }

    [Fact]
    public void The_production_preloader_reads_the_model_and_the_bmp_texture_inside_the_scope()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p871-preload-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A minimal BmpMdl surface: the decoder accepts the native "bmp" record
            // header used by the real artwork tree.
            File.WriteAllBytes(Path.Combine(dir, "enginebmp.mdl"), BmpMdl(2, 2));
            File.WriteAllBytes(Path.Combine(dir, "fighter.cvh"), [1, 2, 3, 4]);
            var assets = new DirectoryAssetSource(dir);
            using var preloader = new AssetPreloader(assets);

            // Null model, texture only: the texture is warmed, the model is not.
            preloader.Preload(null, "engine");
            Assert.Equal(1, preloader.TexturesLoaded);
            Assert.Equal(0, preloader.ModelsLoaded);
            // The surface was decoded (headless: decoded but not uploaded).
            Assert.Equal(0, preloader.TexturesUploaded);
            Assert.Null(preloader.Texture("enginebmp"));

            // Model + a numeric texture: only the model is warmed.
            preloader.Preload("fighter", "7");
            Assert.Equal(1, preloader.ModelsLoaded);
            Assert.Equal(4, preloader.ModelBytes("fighter")!.Length);
            Assert.Single(preloader.Requests, r => r.ModelStem == "fighter");

            // A missing surface is counted, not thrown.
            preloader.Preload("nosuchmodel", "nosuchtexture");
            Assert.True(preloader.Missing >= 2);
            // The scope is closed again after the call (colour key + mipmaps restored).
            Assert.True(PreloadRules.ColorKeyHint);
            Assert.False(PreloadRules.MipMapGeneration);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A minimal valid native texture MDL (the same synthetic fixture
    /// <see cref="MdlTextureDecoderTests"/> uses), so the preloader's decode step
    /// runs for real.</summary>
    private static byte[] BmpMdl(int width, int height)
    {
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        var b = new List<byte>();
        W32(b, 0x0DEBADF00u);  // magic
        W32(b, 0x00010000u);   // version
        W32(b, 0);             // countNamespaces
        W32(b, 0);             // countImports
        W32(b, 0);             // countExports
        W32(b, 0);             // countDefinitions
        W32(b, 0);             // definition index
        W32(b, 9);             // ObjectImport token
        W32(b, 0);             // import index
        W32(b, 7);             // ObjectBinary token
        W32(b, 2);             // sizeX
        W32(b, 2);             // sizeY
        W32(b, 4);             // pitch
        W32(b, 16);            // bitcount (RGB565)
        W32(b, 0x0000F800u);   // redMask
        W32(b, 0x000007E0u);   // greenMask
        W32(b, 0x0000001Fu);   // blueMask
        W32(b, 0);             // alphaMask
        b.AddRange(new byte[] { 0, 0, 0, 0 }); // colorkey byte + pad
        W16(b, 0xF800); W16(b, 0x07E0); W16(b, 0x001F); W16(b, 0xFFFF);
        return b.ToArray();
    }

    private static void W32(List<byte> b, uint v)
    {
        b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 24));
    }

    private static void W16(List<byte> b, ushort v)
    {
        b.Add((byte)v); b.Add((byte)(v >> 8));
    }
}
