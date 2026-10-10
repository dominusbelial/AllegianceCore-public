using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P421 — pins the per-resource-type rock-texture mapping to the REAL original
/// assets in the artwork tree (via the same headless <see cref="MdlTextureDecoder"/>
/// used by <see cref="ExplosionAssetFramesTests"/>, no GPU). The mapping comes
/// from the native authoritative <c>asteroidTypes[]</c> table
/// (<c>Igc/asteroidigc.cpp</c>): each resource type's native rock model
/// (<c>bgrndNN</c>) carries the matching <c>bgrndNNbmp.mdl</c> surface.
/// These are REAL-ASSET tests: they decode the shipped <c>bgrndNNbmp.mdl</c>
/// files and assert each type resolves to its correct real surface — catching a
/// mismatch, a wrong stem, or a silent fall-back-to-procedural when a real asset
/// exists. Gold has no native rock row and must NOT silently claim a real asset.
/// </summary>
public class RockTextureSourceTests
{
    private static string ArtworkDir() =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    // Expected real surface stem per resource type (native asteroidTypes[] model →
    // its bgrndNNbmp.mdl). Gold (0x04) has NO native rock row → null (procedural).
    public static TheoryData<RockTextureFactory.AsteroidResourceType, string?> Mapping => new()
    {
        { RockTextureFactory.AsteroidResourceType.Plain, "bgrnd05bmp" },
        { RockTextureFactory.AsteroidResourceType.Buildable, "bgrnd05bmp" },
        { RockTextureFactory.AsteroidResourceType.Helium, "bgrnd55bmp" },
        { RockTextureFactory.AsteroidResourceType.Uranium, "bgrnd51bmp" },
        { RockTextureFactory.AsteroidResourceType.Silicon, "bgrnd52bmp" },
        { RockTextureFactory.AsteroidResourceType.Carbon, "bgrnd53bmp" },
        { RockTextureFactory.AsteroidResourceType.Gold, null },
    };

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Mapping))]
    public void EveryResourceType_ResolvesToItsCorrectRealAsset(
        RockTextureFactory.AsteroidResourceType type, string? expectedStem)
    {
        Require.Artwork();
        // The resolver must yield EXACTLY the expected stem — no mismatch, no
        // accidental empty string meaning "fall back" when a real asset exists.
        Assert.Equal(expectedStem, RockTextureSource.MdlStemFor(type));
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Mapping))]
    public void RealStem_WhenPresent_DecodesToARealSurfaceTexture(
        RockTextureFactory.AsteroidResourceType type, string? stem)
    {
        Require.Artwork();
        if (stem is null)
        {
            // Gold has no native rock — the fall-back path is the contract.
            Assert.Null(RockTextureSource.PathFor(type));
            return;
        }

        string path = Path.Combine(ArtworkDir(), stem + ".mdl");
        Assert.True(File.Exists(path), $"real rock surface missing: {path}");

        var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));

        // A real SURFACE texture, not a 16×16 radar/map icon: must be a genuine
        // rock-sized bitmap. All bgrndNNbmp are 2048×2048.
        Assert.True(tex.Width >= 256 && tex.Height >= 256,
            $"'{stem}' is only {tex.Width}x{tex.Height} — likely an icon, not a surface");
        Assert.Equal(tex.Width, tex.Height);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Gold_ReportsNoPath_SoCallerUsesProceduralFallback()
    {
        Require.Artwork();
        // Gold is the one type with no native rock row. Its path must be null so
        // EnsureAsteroidTexture provably falls back to RockTextureFactory.
        Assert.Null(RockTextureSource.MdlStemFor(RockTextureFactory.AsteroidResourceType.Gold));
        Assert.Null(RockTextureSource.PathFor(RockTextureFactory.AsteroidResourceType.Gold));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EachRealSurface_IsDistinctFromTheOthers()
    {
        Require.Artwork();
        // The real surface FILES must be genuinely distinguishable per resource
        // (so a helium rock reads differently from a carbon rock in a field) —
        // not all resolving to the same bitmap. Two types MAY legitimately share
        // a stem (native maps buildable→bgrnd05bmp exactly like plain), so we
        // assert the set of distinct real stems is what native dictates and that
        // every distinct stem maps to a unique file.
        var expectedStems = new[]
        {
            "bgrnd05bmp",   // plain + buildable
            "bgrnd55bmp",   // helium
            "bgrnd51bmp",   // uranium
            "bgrnd52bmp",   // silicon
            "bgrnd53bmp",   // carbon
        };

        var actualStems = System.Enum.GetValues<RockTextureFactory.AsteroidResourceType>()
            .Select(RockTextureSource.MdlStemFor)
            .Where(s => s is not null)
            .Cast<string>()
            .Distinct()
            .OrderBy(s => s)
            .ToArray();

        Assert.Equal(expectedStems.OrderBy(s => s).ToArray(), actualStems);

        // And each distinct file is a genuinely different asset (distinct SHA-256).
        var seen = new HashSet<string>();
        foreach (string stem in actualStems)
        {
            string path = Path.Combine(ArtworkDir(), stem + ".mdl");
            Assert.True(File.Exists(path), $"real rock surface missing: {path}");
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            Assert.True(seen.Add(hash), $"two distinct stems decoded to the same asset: {stem}");
        }
    }
}
