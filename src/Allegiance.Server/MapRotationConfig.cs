using Allegiance.Core;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>Opt-in showcase preset policy. Native creators choose a single map;
/// this operator policy selects it before the existing assembly path.</summary>
public sealed class MapRotationConfig
{
    public string Mode { get; set; } = "cycle";
    public int? Seed { get; set; }
    public MapRotationEntry[] Entries { get; set; } = [];

    internal void Validate(ServerConfig config)
    {
        if (Mode is not ("cycle" or "random"))
            throw new InvalidDataException("showcase.mapRotation.mode must be cycle|random");
        if (Entries is null || Entries.Length < 2)
            throw new InvalidDataException("showcase.mapRotation.entries requires at least two entries");
        if (config.LiveSettings?.DebugMode == true)
            throw new InvalidDataException("showcase.mapRotation requires liveSettings.debugMode=false (fixed debug topology)");
        foreach (var entry in Entries)
        {
            if (entry is null || (entry.Map is null) == (entry.MapGen is null))
                throw new InvalidDataException("showcase.mapRotation entry requires exactly one of map or mapGen");
            if (entry.Map is { } name && (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)
                || name.Contains('\\') || name.Any(char.IsWhiteSpace)))
                throw new InvalidDataException("showcase.mapRotation.map must be a file name without whitespace or directories");
            if (entry.MapGen is not { } mg) continue;
            if (!Enum.TryParse<MapGenType>(mg.Type, true, out var type) || !Enum.IsDefined(type)
                || !Enum.GetNames<MapGenType>().Contains(mg.Type, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"showcase.mapRotation: unsupported mapGen.type '{mg.Type}'");
            if (config.Games.Any(g => g.HomeSwap != "off"))
                throw new InvalidDataException("showcase.mapRotation generated entries require games[].homeSwap=off; generated homes follow their civilization's own rules");
            if (mg.MapSize is < 0 or > 1 || mg.He3Density is { } density && (!double.IsFinite(density) || density < 0 || density > 100))
                throw new InvalidDataException("showcase.mapRotation mapGen: mapSize must be 0|1; he3Density must be finite in [0,100]");
            int?[] counts = [mg.PlayerSectorMineableAsteroids, mg.PlayerSectorSpecialAsteroids, mg.PlayerSectorAsteroids,
                mg.PlayerSectorTreasures, mg.NeutralSectorMineableAsteroids, mg.NeutralSectorSpecialAsteroids,
                mg.NeutralSectorAsteroids, mg.NeutralSectorTreasures];
            if (counts.Any(c => c is < 0 or > 1000))
                throw new InvalidDataException("showcase.mapRotation mapGen resource counts must be in [0,1000]");
        }
    }

    internal int Select(int match, Random stream, int previous)
    {
        if (Mode == "cycle") return match % Entries.Length;
        int index = stream.Next(Entries.Length - (previous >= 0 ? 1 : 0));
        return previous >= 0 && index >= previous ? index + 1 : index;
    }
}

public sealed class MapRotationEntry
{
    public string? Map { get; set; }
    public MapGenConfig? MapGen { get; set; }
}

public sealed partial class Server
{
    private readonly Dictionary<MapRotationEntry, MissionMap> _rotationFileMaps = new();
    private readonly Dictionary<GameConfig, (Random Stream, int Seed, int Match, int Index)> _mapRotations = new();

    // Preflight every entry before opening the listener, including entries not
    // selected at boot. Keep parsed file maps so validation and assembly agree.
    private void ValidateMapRotation(StaticCore core)
    {
        if (_config.Showcase.MapRotation is not { } rotation) return;
        foreach (var game in _config.Games)
        foreach (var entry in rotation.Entries)
        {
            try
            {
                MissionMap map;
                if (entry.Map is { } name)
                {
                    if (!_rotationFileMaps.TryGetValue(entry, out map!))
                        _rotationFileMaps[entry] = map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(_config.ArtPath, name)));
                }
                else
                {
                    var civs = MatchCivilizations.Resolve(game, core, 0);
                    map = MissionMapGenerator.Generate(ToCoreParams(entry.MapGen!, game.Teams, civs, seedOverride: 0,
                        research: ResearchSettingsFor(game)), core, 0);
                }
                if (map.Clusters.Count == 0 || map.Clusters.Count >= ClusterInfo.MaxClusters || map.Warps.Count >= ClusterInfo.MaxWarps)
                    throw new InvalidDataException("map exceeds the CLUSTERINFO sector/warp limits or has no sectors");
                if (game.Teams is < 2 or > 6 || Enumerable.Range(0, game.Teams).Any(side => !map.Stations.Any(s => s.SideId == side))
                    || map.Stations.Any(s => s.SideId >= game.Teams))
                    throw new InvalidDataException($"{game.Teams} teams do not fit the map's station owners");
                var goals = _config.Showcase.GoalRotation?.Entries.Select(e => e.Goals) ?? [game.ResolveGoals(validateMap: false)];
                foreach (var goal in goals) GameConfig.ValidateMapGoals(goal, game.Teams, entry.MapGen);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException($"showcase.mapRotation game '{game.Name}' entry '{entry.Map ?? "gen:" + entry.MapGen?.Type}': {e.Message}", e);
            }
        }
    }

    private (MapRotationEntry Entry, int Seed, string Receipt) NextMap(GameConfig game)
    {
        var rotation = _config.Showcase.MapRotation!;
        if (!_mapRotations.TryGetValue(game, out var state))
        {
            int seed = rotation.Seed ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue);
            state = (new Random(seed), seed, 0, -1);
        }
        int index = rotation.Select(state.Match, state.Stream, state.Index);
        // Consume one seed on every match, including file and pinned entries, so
        // layout RNG consumption cannot perturb the entry selection stream.
        int drawnSeed = state.Stream.Next();
        int mapSeed = rotation.Entries[index].MapGen?.Seed ?? drawnSeed;
        var entry = rotation.Entries[index];
        _mapRotations[game] = (state.Stream, state.Seed, state.Match + 1, index);
        return (entry, mapSeed, $" map={entry.Map ?? "gen:" + Enum.Parse<MapGenType>(entry.MapGen!.Type, true)} mapSeed={mapSeed}"
            + $" rotation={rotation.Mode}#{index} rotationMatch={state.Match + 1} rotationSeed={state.Seed}");
    }
}
