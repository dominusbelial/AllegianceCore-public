using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P1073 — request the native artwork a sector needs, through the same verified
/// on-demand battle-pack streamer P1065 uses for hulls. Generated maps have no
/// P1054 recording, so this is the only path that can deliver their planet
/// posters, warp funnels, station types and asteroid surfaces.
///
/// <para>Native draws every one of these from the local Artwork folder
/// (<c>WinTrek/WinTrek.cpp:3622-3640</c> for the sky, <c>asteroidigc.cpp:33-38</c>
/// for rocks, <c>igc.h:2649</c> for station types), so the browser only changes the
/// delivery container. Keys mirror <see cref="GeneratedMapArt"/> and the pack keys
/// written by <c>tools/p1065-battle-packs.py</c> / <c>tools/p1066-scene-packs.py</c>.</para>
/// </summary>
public sealed partial class Game1
{
    /// <summary>True when a battle pack with this key exists in the published manifest.</summary>
    public Func<string, bool>? BattlePackExists { get; set; }

    /// <summary>Sector → content revision whose closures were already requested.</summary>
    private readonly Dictionary<short, long> _sectorArtRequests = new();

    private static string? PosterDigits(string poster)
    {
        if (string.IsNullOrEmpty(poster)) return null;
        var digits = new string(poster.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>Sky-art pack keys for one cluster's native fields.</summary>
    internal static string[] SkyPackKeys(DataClusterIGC cluster)
    {
        var keys = new List<string>(3);
        if (PosterDigits(cluster.PosterName) is { } digits) keys.Add("mapgen-globe-" + digits);
        if (!string.IsNullOrEmpty(cluster.PlanetName)) keys.Add("mapgen-planet-" + cluster.PlanetName.ToLowerInvariant());
        return keys.ToArray();
    }

    /// <summary>Warp-funnel pack keys for a sector's alephs (plus the native fallback).</summary>
    internal static string[] WarpPackKeys(ClientWorld world, short sector)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { "mapgen-warp-alephbmp" };
        foreach (var warp in world.Warps.Values)
            if (warp.ClusterId == sector && !string.IsNullOrEmpty(warp.TextureName))
                keys.Add("mapgen-warp-" + warp.TextureName.ToLowerInvariant() + "bmp");
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Request every documented generated-map closure the sector actually uses.</summary>
    public void RequestSectorArt(short sector, short? prefetch = null)
    {
        if (_flightSession?.World is not { } world) return;
        RequestSectorArt(world, sector);
        if (prefetch is { } next && next != sector) RequestSectorArt(world, next);
    }

    private void RequestSectorArt(ClientWorld world, short sector)
    {
        if (RequestBattlePack is not { } request) return;
        long revision = (_assets as Allegiance.ClientCore.Browser.Assets.StagedAssetSource)?.ContentRevision ?? 0;
        if (_sectorArtRequests.TryGetValue(sector, out long previous) && previous == revision) return;
        _sectorArtRequests[sector] = revision;
        if (world.Clusters.TryGetValue(sector, out var cluster))
            foreach (string key in SkyPackKeys(cluster)) request(key);
        foreach (string key in WarpPackKeys(world, sector)) request(key);
        foreach (var station in world.Stations.Values)
            if (station.ClusterId == sector) request("station-" + station.StationTypeId);
        foreach (var rock in RocksInSector(world, sector))
            if (!string.IsNullOrEmpty(rock.ModelName))
            {
                // Generated maps use the native fixed table (mapgen-rock-*); an
                // authored static map may reuse any model (static-rock-*). Both keys
                // are requested; the streamer ignores one this generation lacks.
                request("mapgen-rock-" + rock.ModelName.ToLowerInvariant());
                request("static-rock-" + rock.ModelName.ToLowerInvariant());
            }
    }

    /// <summary>Whether every sky closure that exists in the manifest is resident.
    /// Unknown keys are skipped (an older generation without them must not black out
    /// the sky); known-but-pending keys hold the sky until the verified bytes arrive.</summary>
    public bool SkyArtReady(short sector)
    {
        if (_flightSession?.World is not { } world || !world.Clusters.TryGetValue(sector, out var cluster)) return true;
        if (BattlePackReady is null) return true;
        foreach (string key in SkyPackKeys(cluster))
            if (BattlePackExists?.Invoke(key) != false && !BattlePackReady(key)) return false;
        return true;
    }

    /// <summary>Harness/test hook: every station and asteroid in the current sector,
    /// with the exact model/texture stems the renderer resolves and whether their
    /// bytes are resident. "Resident" is the same <see cref="Assets.IAssetSource"/>
    /// answer the renderer uses, so a true row means the native art can draw now.</summary>
    public object? SectorArtMetrics
    {
        get
        {
            if (_flightSession?.World is not { } world || world.MyShipId is not { } id
                || !world.Ships.TryGetValue(id, out var own)) return null;
            short sector = own.SectorId;
            var stations = new List<object>();
            foreach (var station in world.Stations.Values)
            {
                if (station.ClusterId != sector) continue;
                var type = _shopCatalog?.StationTypes?.GetValueOrDefault(station.StationTypeId);
                var stationType = type.GetValueOrDefault();
                string? model = type is null ? null : stationType.ModelName;
                string? texture = type is null ? null : _flightStations?.TextureStemFor(stationType.ModelName, stationType.TextureName);
                stations.Add(new
                {
                    id = station.StationId, typeId = station.StationTypeId, model,
                    modelResident = model is not null && _assets.Exists(model + ".mdl"),
                    texture, textureResident = texture is not null && _assets.Exists(texture + ".mdl"),
                    visible = model is not null && _assets.Exists(model + ".mdl"),
                });
            }
            var rocks = new List<object>();
            foreach (var rock in RocksInSector(world, sector))
            {
                string? texture = _flightRocks?.TextureStemFor(rock.ModelName, rock.TextureName);
                rocks.Add(new
                {
                    id = rock.AsteroidId, model = rock.ModelName,
                    modelResident = !string.IsNullOrEmpty(rock.ModelName) && _assets.Exists(rock.ModelName + ".mdl"),
                    texture, textureResident = texture is not null && _assets.Exists(texture + ".mdl"),
                    textured = texture is not null && _assets.Exists(texture + ".mdl"),
                });
            }
            var sky = world.Clusters.TryGetValue(sector, out var cluster)
                ? new { sector, poster = cluster.PosterName, planet = cluster.PlanetName, ready = SkyArtReady(sector),
                    planetResident = string.IsNullOrEmpty(cluster.PlanetName) || _assets.Exists(cluster.PlanetName + ".mdl") }
                : null;
            return new { sector, stations, rocks, sky,
                warps = WarpPackKeys(world, sector).Select(key => new { key, ready = BattlePackReady?.Invoke(key) == true }).ToArray() };
        }
    }
}
