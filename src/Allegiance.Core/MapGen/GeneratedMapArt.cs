using Allegiance.Core.MapGen;

namespace Allegiance.Core;

/// <summary>
/// P1073 — the complete artwork namespace a procedurally generated map can reference,
/// in one enumerable place. The browser's pack tools and tests use it so a generated
/// map can never ask for a surface that no verified pack carries.
///
/// <para><b>Sources.</b> Planet posters come from <c>CMapData::smszPlanetName</c>
/// (<c>src/Igc/mapmakerigc.cpp:43-70</c>, transcribed by <see cref="MapGenContext.PlanetNames"/>);
/// environment domes are <c>globe1..globe8</c> (<c>MissionMapGenerator.cs:385,410</c>),
/// whose painted surface is <c>environmentNbmp</c> (<c>NativeSkyRenderer</c>,
/// <c>globeN.mdl</c> + <c>environmentNbmp.mdl</c>, native <c>clusterIGC.cpp:41-42</c>);
/// aleph funnel textures are <c>plnt19</c>/<c>plnt42</c> and the <c>aleph</c> fallback
/// (<c>MissionMapGenerator.cs:47-48</c>, <c>WarpRenderer.LoadTextureFile</c>); asteroid
/// meshes/textures come from the native fixed table
/// (<c>src/Igc/asteroidigc.cpp:126-137</c>, <see cref="NativeAsteroidTypes"/>).</para>
/// </summary>
public static class GeneratedMapArt
{
    /// <summary>Funnel surface of a neutral-sector aleph (native texture name, no bmp suffix).</summary>
    public const string NeutralAlephTexture = "plnt19";
    /// <summary>Funnel surface of a player-side aleph.</summary>
    public const string PlayerAlephTexture = "plnt42";
    /// <summary>The legacy <c>aleph</c> fallback the warp renderer also probes.</summary>
    public const string FallbackAlephTexture = "aleph";

    /// <summary>The authored planet-poster pool (native <c>smszPlanetName</c>), distinct names.</summary>
    public static IReadOnlyList<string> PlanetPosterStems =>
        MapGenContext.PlanetNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Environment dome mesh stems the generator emits (<c>globe1..globe8</c>).</summary>
    public static IReadOnlyList<string> EnvironmentDomeStems =>
        Enumerable.Range(1, 8).Select(n => "globe" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Environment dome surface stems (<c>environmentNbmp</c>) matching the domes.</summary>
    public static IReadOnlyList<string> EnvironmentTextureStems =>
        Enumerable.Range(1, 8).Select(n => "environment" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "bmp").ToArray();

    /// <summary>Warp funnel stems: the two generator textures plus the renderer's fallback.</summary>
    public static IReadOnlyList<string> WarpTextureStems => [NeutralAlephTexture, PlayerAlephTexture, FallbackAlephTexture];

    /// <summary>Every distinct asteroid model stem a generated map can place.</summary>
    public static IReadOnlyList<string> AsteroidModelStems =>
        Enumerable.Range(0, NativeAsteroidTypes.Count)
            .Select(i => NativeAsteroidTypes.GetTypeDefaults(i).ModelName)
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// The native surface stem an asteroid model resolves to when its wire
    /// <c>textureName</c> is empty: the model's first imported namespace, which for the
    /// shipped rocks is <c>&lt;model&gt;bmp</c> (asteroidigc.cpp:126-137;
    /// <c>NativeAsteroidResolver.ResolveTextureStem</c>).
    /// </summary>
    public static IReadOnlyList<string> AsteroidTextureStems =>
        AsteroidModelStems.Select(m => m + "bmp").ToArray();

    /// <summary>Model stems (no extension) a generated map can request from the pack store.</summary>
    public static IEnumerable<string> AllModelStems =>
        PlanetPosterStems.Concat(EnvironmentDomeStems).Concat(WarpTextureStems)
            .Concat(AsteroidModelStems).Concat(AsteroidTextureStems)
            .Concat(EnvironmentTextureStems).Distinct(StringComparer.OrdinalIgnoreCase);
}
