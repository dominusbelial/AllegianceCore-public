using System;
using System.Collections.Generic;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-16 — the native radar <b>contact icon</b> surface name for every contact
/// kind. Native loads each model's icon once at model creation from its IGC
/// <c>iconName</c> + <c>"bmp"</c>:
/// <code>
/// IObject* WinTrekClient::LoadRadarIcon(const char* szName)
/// {
///     if (szName &amp;&amp; (szName[0] != '\0'))
///         psurface = GetModeler()->LoadSurface(ZString(szName) + "bmp", true);
///     else
///         psurface = NULL;
///     return psurface;
/// }
/// </code>
/// (<c>WinTrek/trekigc.cpp:2731-2744</c>), called from the generic model loader
/// (<c>Igc/modelIGC.cpp:118</c>, through <c>LoadCVH</c>/<c>LoadWarp</c>/
/// <c>LoadMine</c>/<c>LoadDecal</c>: <c>modelIGC.cpp:127-136,276-279</c>) and
/// from the hull type (<c>Igc/hullTypeIGC.cpp:51</c>, used by every ship through
/// <c>Igc/shipIGC.cpp:4732</c>). The per-kind icon names are:
/// <list type="bullet">
/// <item><b>ship</b> — its hull type's <c>iconName</c> (<c>DataHullTypeIGC.IconName</c>,
/// <c>hullTypeIGC.cpp:51</c>).</item>
/// <item><b>station</b> — the station type's <c>iconName</c>
/// (<c>stationIGC.cpp:240-244</c> → <c>Load(…, GetIconName(), …)</c>).</item>
/// <item><b>asteroid</b> — <c>AsteroidDef.iconName</c>
/// (<c>asteroidigc.cpp:35</c>; the table rows are <c>asteroidTypes[]</c> at
/// <c>asteroidigc.cpp:126-137</c>).</item>
/// <item><b>warp</b> — <c>warpDef.iconName</c> (<c>warpIGC.cpp:34</c> →
/// <c>LoadWarp</c>, <c>modelIGC.cpp:134-139</c>).</item>
/// <item><b>probe</b> — <c>dataProbeType-&gt;iconName</c>
/// (<c>probeigc.cpp:114</c>).</item>
/// <item><b>mine</b> — <c>dataMineType-&gt;iconName</c>
/// (<c>mineigc.cpp:103</c>).</item>
/// <item><b>missile</b> — <c>dataMissileType-&gt;iconName</c>
/// (<c>missileigc.cpp:50,60</c>).</item>
/// <item><b>treasure</b> — its buyable's <c>GetIconName()</c>, or the fixed
/// cash/powerup/flag/artifact icon (<c>treasureIGC.cpp:118-155</c>), the same
/// mapping <see cref="TreasureVisualResolver"/> already ports for the 3-D
/// model. A useful <b>tech</b> treasure overrides it with the shared
/// <c>icontechbmp</c> surface (<c>radarimage.cpp:224,719,890</c>).</item>
/// </list>
/// <para>
/// The client has no static core object graph, so the type-id → icon-name
/// lookups are built ONCE from the parsed core tables (<see cref="ShopCatalog"/>)
/// and the per-instance names the wire already carries (<c>DataWarpIGC.IconName</c>,
/// <c>DataAsteroidIGC.IconName</c>) are memoised on first sight. Every per-frame
/// lookup is a dictionary read: no string is built on the frame path after
/// warm-up, which <c>Hud16RadarIconTests.Per_frame_resolution_allocates_nothing</c>
/// pins with <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// </para>
/// <para>Pure data — no graphics device, so the whole mapping is unit-testable
/// headless.</para>
/// </summary>
public sealed class RadarIconNameCache
{
    /// <summary>The native shared tech-treasure icon (<c>radarimage.cpp:224</c>,
    /// drawn at <c>:719</c> and <c>:890</c>). Native loads the literal stem
    /// <c>"icontechbmp"</c>, so the cache returns it verbatim instead of
    /// appending another <c>bmp</c>.</summary>
    public const string TechStem = "icontechbmp";

    private readonly ShopCatalog? _catalog;
    private readonly Dictionary<short, string?> _ships = new();
    private readonly Dictionary<short, string?> _stations = new();
    private readonly Dictionary<short, string?> _missiles = new();
    private readonly Dictionary<short, string?> _probes = new();
    private readonly Dictionary<short, string?> _mines = new();
    // Per-instance icon names the wire carries (warp/asteroid) and the treasure
    // visual resolution, memoised so the frame path never builds a string twice.
    private readonly Dictionary<string, string?> _iconNames = new(StringComparer.Ordinal);
    private readonly Dictionary<(TreasureCode Code, short Id), string?> _treasures = new();

    /// <summary>Build the type-id → surface-stem tables once. A null
    /// <paramref name="catalog"/> (no artwork core) leaves every kind without an
    /// icon — the renderer then keeps its procedural fallback, exactly like
    /// native's NULL <c>LoadRadarIcon</c> result.</summary>
    /// <param name="catalog">The parsed static core the client already loads for
    /// the shop/hangar (<see cref="ShopCatalog.TryLoad"/>).</param>
    /// <param name="hulls">Hull id → hull type, the client's existing
    /// <c>_hullById</c> table. Falls back to <see cref="ShopCatalog.Hulls"/>.</param>
    public RadarIconNameCache(ShopCatalog? catalog,
        IReadOnlyDictionary<short, DataHullTypeIGC>? hulls = null)
    {
        _catalog = catalog;
        if (hulls is { } hullTable)
        {
            foreach (var hull in hullTable.Values)
                _ships[hull.HullId] = SurfaceStem(hull.IconName);
        }
        else if (catalog?.Hulls is { } hullList)
        {
            foreach (var hull in hullList)
                _ships[hull.HullId] = SurfaceStem(hull.IconName);
        }
        if (catalog?.StationTypes is { } stations)
            foreach (var (id, type) in stations)
                _stations[id] = SurfaceStem(type.IconName);
        if (catalog?.MissileTypes is { } missiles)
            foreach (var (id, type) in missiles)
                _missiles[id] = SurfaceStem(type.IconName);
        if (catalog?.ProbeTypes is { } probes)
            foreach (var (id, type) in probes)
                _probes[id] = SurfaceStem(type.IconName);
        if (catalog?.MineTypes is { } mines)
            foreach (var (id, type) in mines)
                _mines[id] = SurfaceStem(type.IconName);
    }

    /// <summary>Native <c>LoadRadarIcon</c> (<c>trekigc.cpp:2731-2744</c>): a
    /// non-empty <c>iconName</c> becomes <c>iconName + "bmp"</c>; an empty/null
    /// name means "no icon" (native returns NULL and
    /// <c>radarimage.cpp:591-593</c> then skips the whole contact).</summary>
    public static string? SurfaceStem(string? iconName)
        => string.IsNullOrEmpty(iconName) ? null : string.Concat(iconName, "bmp");

    /// <summary>Ship icon: the hull type's icon (<c>hullTypeIGC.cpp:51</c>),
    /// keyed by the wire's hull id.</summary>
    public string? Ship(short hullId) => _ships.GetValueOrDefault(hullId);

    /// <summary>Station icon: the station type's icon
    /// (<c>stationIGC.cpp:240-244</c>).</summary>
    public string? Station(short stationTypeId) => _stations.GetValueOrDefault(stationTypeId);

    /// <summary>Missile icon: the missile type's icon
    /// (<c>missileigc.cpp:50,60</c>), keyed by the wire's expendable type id.</summary>
    public string? Missile(short missileTypeId) => _missiles.GetValueOrDefault(missileTypeId);

    /// <summary>Probe icon: the probe type's icon (<c>probeigc.cpp:114</c>).</summary>
    public string? Probe(short probeTypeId) => _probes.GetValueOrDefault(probeTypeId);

    /// <summary>Mine icon: the mine type's icon (<c>mineigc.cpp:103</c>).</summary>
    public string? Mine(short mineTypeId) => _mines.GetValueOrDefault(mineTypeId);

    /// <summary>Asteroid icon: <c>AsteroidDef.iconName</c> from the wire's
    /// <c>DataAsteroidIGC.IconName</c> (<c>asteroidigc.cpp:35,126-137</c>).</summary>
    public string? Asteroid(string? iconName) => InstanceStem(iconName);

    /// <summary>Warp icon: <c>warpDef.iconName</c> from the wire's
    /// <c>DataWarpIGC.IconName</c> (<c>warpIGC.cpp:34</c>).</summary>
    public string? Warp(string? iconName) => InstanceStem(iconName);

    /// <summary>Treasure icon. A useful tech treasure uses the shared
    /// <c>icontechbmp</c> surface exactly where native swaps it in
    /// (<c>radarimage.cpp:719,890</c>); otherwise the treasure's own model icon
    /// (its buyable's icon name or the fixed cash/powerup/flag/artifact icon,
    /// <c>treasureIGC.cpp:118-155</c>).</summary>
    public string? Treasure(TreasureCode code, short payloadId, bool isTech)
    {
        if (isTech) return TechStem;
        if (_treasures.TryGetValue((code, payloadId), out var cached)) return cached;
        var stem = SurfaceStem(TreasureVisualResolver.Resolve(code, payloadId, _catalog).IconName);
        _treasures[(code, payloadId)] = stem;
        return stem;
    }

    private string? InstanceStem(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName)) return null;
        if (_iconNames.TryGetValue(iconName, out var cached)) return cached;
        var stem = SurfaceStem(iconName);
        _iconNames[iconName] = stem;
        return stem;
    }

    /// <summary>Every surface stem this cache can resolve (plus the shared tech
    /// and fixed treasure icons), for the one-time texture preload. Startup-only
    /// convenience; the per-frame path never enumerates this.</summary>
    public IEnumerable<string> KnownStems()
    {
        foreach (var map in new[] { _ships, _stations, _missiles, _probes, _mines })
            foreach (var stem in map.Values)
                if (stem is not null) yield return stem;
        yield return TechStem;
        // The fixed treasure icons (treasureIGC.cpp:130-155): cash/powerup/flag/
        // artifact; part/development icons are preloaded by RadarIconTextures
        // from the core's own tables.
        yield return "cashiconbmp";
        yield return "iconpubmp";
        yield return "flagbmp";
        yield return "articonbmp";
    }
}
