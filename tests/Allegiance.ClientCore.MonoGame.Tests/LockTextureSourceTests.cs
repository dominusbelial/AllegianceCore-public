using System.IO;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Xunit;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P428 — pins the missile-lock indicator to the REAL original-game lock
/// sprites in the artwork tree (via the same headless <see cref="MdlTextureDecoder"/>
/// used by <see cref="RockTextureSourceTests"/> / <see cref="GaugeTextures"/>,
/// no GPU). The mapping comes from native <c>WinTrek/artwork.h:80-81</c> +
/// <c>radarimage.cpp:217-218,330-332</c>: partial lock →
/// <c>plockbmp.mdl</c>, full lock → <c>lockedbmp.mdl</c>.
/// These are REAL-ASSET tests: they decode the shipped files and assert the
/// full-lock sprite genuinely ADDS the green flower-burst over the partial
/// (partial is the orange brackets with no centre; full is the same brackets
/// plus a green 4-petal centre — Domingo's remembered "flower", confirmed by
/// decoding both real .mdl files).
/// </summary>
public class LockTextureSourceTests
{
    private static string ArtworkDir() =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(MissileLockFeedback.LockState.None, null)]
    [InlineData(MissileLockFeedback.LockState.Partial, "plockbmp")]
    [InlineData(MissileLockFeedback.LockState.Full, "lockedbmp")]
    public void LockState_ResolvesToItsCorrectRealAsset(
        MissileLockFeedback.LockState state, string? expectedStem)
    {
        Require.Artwork();
        Assert.Equal(expectedStem, LockTextureSource.MdlStemFor(state));
        if (expectedStem is null)
            Assert.Null(LockTextureSource.PathFor(state));
        else
            Assert.Equal(Path.Combine(ArtworkDir(), expectedStem + ".mdl"), LockTextureSource.PathFor(state));
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(MissileLockFeedback.LockState.Partial)]
    [InlineData(MissileLockFeedback.LockState.Full)]
    public void RealSprite_IsPresentAndDecodes_AsASquare64x64(MissileLockFeedback.LockState state)
    {
        Require.Artwork();
        string path = LockTextureSource.PathFor(state)!;
        Assert.True(File.Exists(path), $"real lock sprite missing: {path}");

        var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));

        // Native's lock blips are 64×64 RGB565 (measured, not guessed).
        Assert.Equal(64, tex.Width);
        Assert.Equal(64, tex.Height);

        // Genuinely non-empty content (a real sprite, not a blank box).
        int nonBlack = CountPixels(tex, static (r, g, b) => (r + g + b) > 20);
        Assert.True(nonBlack > 100, $"lock sprite '{state}' is (nearly) blank: {nonBlack}/4096 pixels");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FullLockSprite_AddsTheGreenFlowerBurst_OverThePartial()
    {
        Require.Artwork();
        var partial = MdlTextureDecoder.Decode(
            File.ReadAllBytes(LockTextureSource.PathFor(MissileLockFeedback.LockState.Partial)!));
        var full = MdlTextureDecoder.Decode(
            File.ReadAllBytes(LockTextureSource.PathFor(MissileLockFeedback.LockState.Full)!));

        // The partial sprite is the orange corner brackets with NO green centre;
        // the full sprite adds a green 4-petal flower-burst in the centre
        // (native's lockedbmp — Domingo's remembered "flower"). This is the
        // core P428 discriminating assertion: full genuinely carries green
        // content that partial does not.
        int partialGreen = CountPixels(partial, static (r, g, b) => g > r && g > b && g > 60);
        int fullGreen = CountPixels(full, static (r, g, b) => g > r && g > b && g > 60);

        Assert.Equal(0, partialGreen);          // no green flower in the partial sprite
        Assert.True(fullGreen > 50, $"full lock should add a green flower, got only {fullGreen} green px");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TheTwoLockSprites_AreDistinctFiles()
    {
        Require.Artwork();
        // The partial→full transition must swap between two GENUINELY distinct
        // assets (that is the whole native mechanic), not two paths to one file.
        string pHash = Sha(File.ReadAllBytes(LockTextureSource.PathFor(MissileLockFeedback.LockState.Partial)!));
        string fHash = Sha(File.ReadAllBytes(LockTextureSource.PathFor(MissileLockFeedback.LockState.Full)!));
        Assert.NotEqual(pHash, fHash);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ToPremultipliedRgba_KeysTheBlackSurround_LeavingOpaqueContent()
    {
        Require.Artwork();
        // The lock sprites sit over the 3-D scene, so the black surround must be
        // keyed to alpha 0 (the GaugeTextures convention) while the orange/green
        // sprite content stays opaque — otherwise the draw smears a black box.
        var tex = MdlTextureDecoder.Decode(
            File.ReadAllBytes(LockTextureSource.PathFor(MissileLockFeedback.LockState.Full)!));
        byte[] premul = MdlTextureDecoder.ToPremultipliedRgba(tex);

        int transparent = 0, opaque = 0;
        for (int i = 0; i < premul.Length; i += 4)
        {
            if (premul[i + 3] == 0) transparent++;
            else opaque++;
        }
        // Black surround keyed out (most of the 64×64 canvas) but real content survives.
        Assert.True(transparent > 0);
        Assert.True(opaque > 100);
    }

    private static int CountPixels(MdlTexture tex, Func<int, int, int, bool> pred)
    {
        var rgb = tex.Rgba;
        int n = 0;
        for (int i = 0; i < rgb.Length; i += 4)
        {
            if (pred(rgb[i], rgb[i + 1], rgb[i + 2])) n++;
        }
        return n;
    }

    private static string Sha(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
}
