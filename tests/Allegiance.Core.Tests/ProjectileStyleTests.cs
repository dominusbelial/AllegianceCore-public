using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Visual;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P465b — the pure <see cref="ProjectileStyle.For"/> resolver maps a
/// projectile type id to the bolt's render style from the loaded static core.
/// Proven against the REAL PCore001 corpus (the same core the server boots and
/// the shop catalogue reads) plus synthetic entries, so real types exercise the
/// actual data path while the unknown/missing-core fallbacks stay covered.
/// </summary>
/// <remarks>
/// PCore001 carries no per-type colour (every projectile type measures
/// (0,0,0,0)), so its known types must resolve to the amber tracer — the
/// native-equivalent flat-bolt colour, since native ignores <c>color</c> for the
/// visible decal (see <see cref="ProjectileStyle"/> remarks). What PCore001
/// <em>does</em> carry — real widths and radii — is what these assertions pin.
/// </remarks>
public class ProjectileStyleTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc"));
        return StaticCore.Load(file);
    }

    private static IReadOnlyDictionary<short, DataProjectileTypeIGC> Synthetic()
    {
        // id 777: a made-up type with a genuine green colour + a real width/radius,
        // so the non-black-colour path is exercised (PCore001 has none).
        return new Dictionary<short, DataProjectileTypeIGC>
        {
            [777] = new(
                Color: [0.2f, 0.9f, 0.4f, 1f],
                Radius: 4f,
                Rotation: 0f,
                ModelName: "",
                TextureName: "tf8",
                Power: 0f,
                BlastPower: 0f,
                BlastRadius: 0f,
                Speed: 2000f,
                Lifespan: 1f,
                ProjectileTypeId: 777,
                DamageType: 0,
                AbsoluteF: false,
                Directional: false,
                Width: 1.5f,
                AmbientSound: -1),
        };
    }

    /// <summary>A real PCore001 type with a real width (the 'PE Mini-Gun 1'
    /// family, projectile type 2) preserves its width and radius, and resolves
    /// its (black) colour to the amber tracer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PCore001_real_width_and_radius_are_preserved()
    {
        Require.Artwork();
        var style = ProjectileStyle.For(2, Core().ProjectileTypes);
        Assert.Equal(0.1f, style.Width);
        Assert.Equal(1f, style.Radius);
        Assert.Equal(1.0f, style.R);          // black colour -> amber tracer
        Assert.Equal(0.84f, style.G);
        Assert.Equal(0.32f, style.B);
    }

    /// <summary>A real PCore001 type with zero width (the 'PE Gat Gun 1' family,
    /// projectile type 1) falls back to the default tracer width while keeping
    /// its real radius.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PCore001_zero_width_uses_default_width()
    {
        Require.Artwork();
        var style = ProjectileStyle.For(1, Core().ProjectileTypes);
        Assert.Equal(ProjectileStyle.DefaultWidth, style.Width);
        Assert.Equal(1f, style.Radius);
        Assert.Equal(1.0f, style.R);
        Assert.Equal(0.84f, style.G);
        Assert.Equal(0.32f, style.B);
    }

    /// <summary>A genuinely non-black colour in a (synthetic) core maps to the
    /// bolt tint — the mechanism the real core exercises once colour is present.</summary>
    [Fact]
    public void Real_nonblack_colour_maps_to_tint()
    {
        var style = ProjectileStyle.For(777, Synthetic());
        Assert.Equal(0.2f, style.R);
        Assert.Equal(0.9f, style.G);
        Assert.Equal(0.4f, style.B);
        Assert.Equal(1.5f, style.Width);
        Assert.Equal(4f, style.Radius);
    }

    /// <summary>An unknown type id falls back to the full amber tracer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Unknown_type_falls_back_to_amber()
    {
        Require.Artwork();
        var style = ProjectileStyle.For(9999, Core().ProjectileTypes);
        Assert.Equal(ProjectileStyle.AmberFallback, style);
    }

    /// <summary>A missing core (null table) falls back to the full amber tracer.</summary>
    [Fact]
    public void Missing_core_falls_back_to_amber()
    {
        Assert.Equal(ProjectileStyle.AmberFallback, ProjectileStyle.For(2, null));
    }
}
