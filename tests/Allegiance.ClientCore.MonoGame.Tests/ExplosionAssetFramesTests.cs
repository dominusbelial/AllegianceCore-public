using System.IO;
using System.Linq;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P263 — verifies the real original-game explosion artwork against the declared
/// preset data. Each fireball film in <see cref="ExplosionPresets"/> names a real
/// <c>expNNbmp.mdl</c> strip; native slices it into <c>width/height</c> square
/// frames (<c>AnimatedImage</c>, <c>engine/image.cpp:588-625</c>), and the
/// declared <see cref="ExplosionSpriteFilm.FrameCount"/> must equal what the real
/// asset yields (via the headless <see cref="MdlTextureDecoder"/> — no GPU).
/// These are REAL-ASSET tests: they read the artwork tree (not fixtures), so
/// they pin the preset table to the actual shipped sprites and would catch a
/// wrong declared frame count immediately.
/// </summary>
public class ExplosionAssetFramesTests
{
    private static string ArtworkDir() =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly (string Stem, int Expected)[] Strips =
    {
        ("exp20", 20), ("exp22", 30), ("exp23", 38), ("exp24", 16), ("exp25", 32),
    };

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("exp20", 20)]
    [InlineData("exp22", 30)]
    [InlineData("exp23", 38)]
    [InlineData("exp24", 16)]
    [InlineData("exp25", 32)]
    public void RealStrip_DecodesToTheDeclaredFrameCount(string stem, int expectedFrames)
    {
        Require.Artwork();
        string path = Path.Combine(ArtworkDir(), stem + "bmp.mdl");
        Assert.True(File.Exists(path), $"artwork strip missing: {path}");
        var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));
        int frames = tex.Width / tex.Height;                 // AnimatedImage slicing
        Assert.Equal(expectedFrames, frames);
        // Every declared preset that uses this stem agrees with the real asset.
        Assert.All(AllFilmsFor(stem), f => Assert.Equal(expectedFrames, f.FrameCount));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShockWaveStrip_DecodesAsASquareRingTexture()
    {
        Require.Artwork();
        string path = Path.Combine(ArtworkDir(), "fx18bmp.mdl");
        Assert.True(File.Exists(path), $"artwork shockwave missing: {path}");
        var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));
        Assert.Equal(tex.Height, tex.Width);                 // square ring texture
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EveryPresetReferencesARealArtworkStrip()
    {
        Require.Artwork();
        for (int t = Allegiance.Core.Sim.ExplosionType.Mine;
             t <= Allegiance.Core.Sim.ExplosionType.LargeStation; t++)
        {
            var preset = ExplosionPresets.For(t);
            Assert.NotEmpty(preset.Films);
            foreach (var film in preset.Films)
            {
                string path = Path.Combine(ArtworkDir(), film.Stem + "bmp.mdl");
                Assert.True(File.Exists(path),
                    $"explosion type {t} references missing artwork '{film.Stem}bmp.mdl'");
            }
        }
    }

    private static ExplosionSpriteFilm[] AllFilmsFor(string stem) =>
        Enumerable.Range(Allegiance.Core.Sim.ExplosionType.Mine,
                         Allegiance.Core.Sim.ExplosionType.LargeStation -
                         Allegiance.Core.Sim.ExplosionType.Mine + 1)
            .SelectMany(t => ExplosionPresets.For(t).Films)
            .Where(f => f.Stem == stem)
            .ToArray();
}
