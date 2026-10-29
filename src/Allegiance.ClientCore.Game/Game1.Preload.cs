using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P871 / SIM-0223 — the core-type preload slice of <see cref="Game1"/> (partial
/// class). Native <c>WinTrekClient::Preload(pszModelName, pszTextureName)</c>
/// (<c>WinTrek/trekigc.cpp:4912-4937</c>) is called by the core for every model
/// type it creates (<c>Igc/hullTypeIGC.cpp:49-51</c>,
/// <c>Igc/modelIGC.cpp:118</c>) so the modeler has the model namespace and the
/// surface resident before first draw. The port has no C++ object graph, so the
/// same requests are issued once over the loaded catalogue tables.
/// </summary>
public sealed partial class Game1
{
    private Rendering.AssetPreloader? _preloader;

    /// <summary>Issue one native preload request per core type that names a
    /// model or texture: hulls (<c>hullTypeIGC.cpp:49-51</c>), stations, probes,
    /// missiles and mines (their <c>CmodelIGC::LoadCVH</c> path).</summary>
    private void PreloadCoreTypes()
    {
        if (_shopCatalog is not { } catalog)
        {
            Console.WriteLine("[monogame] preload: no static core — nothing to preload");
            return;
        }
        _preloader = new Rendering.AssetPreloader(_assets, GraphicsDevice);
        foreach (var hull in catalog.Hulls) _preloader.Preload(hull.ModelName, hull.TextureName);
        if (catalog.StationTypes is { } stations)
            foreach (var type in stations.Values) _preloader.Preload(type.ModelName, type.TextureName);
        if (catalog.ProbeTypes is { } probes)
            foreach (var type in probes.Values) _preloader.Preload(type.ModelName, type.TextureName);
        if (catalog.MissileTypes is { } missiles)
            foreach (var type in missiles.Values) _preloader.Preload(type.ModelName, type.TextureName);
        if (catalog.MineTypes is { } mines)
            foreach (var type in mines.Values) _preloader.Preload(type.ModelName, type.TextureName);
        Console.WriteLine($"[monogame] preload: {_preloader.Requests.Count} requests — "
            + $"{_preloader.ModelsLoaded} models, {_preloader.TexturesLoaded} textures warmed, "
            + $"{_preloader.Missing} missing (native Preload, trekigc.cpp:4912-4937)");
    }
}
