using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Server;

/// <summary>
/// One playable game world (a mission) drawn from the configuration. In the
/// reference engine the per-team civilizations and names come from
/// <c>MissionParams::rgCivID[]</c> plus a host-supplied name array
/// (<c>UpdateSides</c>, <c>missionigc.cpp:3734</c>) — neither is derivable from
/// the core or the map, which is precisely why they live here (see
/// BOOT_PATH.md / MISSION_INIT.md §2).
/// </summary>
public sealed class GameConfig
{
    /// <summary>Human name, used in logs only.</summary>
    public string Name { get; init; } = "";

    /// <summary>File name of the map, relative to <see cref="ServerConfig.ArtPath"/>.</summary>
    public string Map { get; init; } = "";

    /// <summary>Number of sides. Sides are built for ids 0..Teams-1.</summary>
    public int Teams { get; init; }

    /// <summary>Per-team <c>rgCivID[]</c> — indexes <see cref="ServerConfig.StaticCore"/>
    /// civilizations. Length must be &gt;= Teams.</summary>
    public int[] CivIds { get; init; } = [];

    /// <summary>Per-team display names; length must be &gt;= Teams.</summary>
    public string[] SideNames { get; init; } = [];
}

/// <summary>
/// Top-level server configuration. Shape is decided (see task): a JSON file at
/// <c>--config</c> (default <c>server.json</c>), with scalar fields overridable
/// on the command line (<c>--port</c>, <c>--artpath</c>, <c>--staticcore</c>,
/// <c>--updatespersec</c>). Games are file-defined — there is no CLI form for
/// a nested array.
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Root of the artwork tree (contains the core and map files).</summary>
    public string ArtPath { get; init; } = "/home/domingo/Development/Artwork";

    /// <summary>Static core file name, relative to <see cref="ArtPath"/>.</summary>
    public string StaticCore { get; init; } = "PCore001.igc";

    /// <summary>TCP port the logon transport listens on.</summary>
    public int Port { get; init; } = 6073;

    /// <summary>Tick cadence (Hz). Fixed period = 1000 / updatesPerSecond ms.</summary>
    public int UpdatesPerSecond { get; init; } = 20;

    /// <summary>The game worlds to instantiate at boot.</summary>
    public List<GameConfig> Games { get; init; } = [];
}

/// <summary>Static helpers for reading <see cref="ServerConfig"/> from JSON.</summary>
public static class ServerConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load the config file (throwing a helpful error naming the problem file),
    /// then overlay scalar CLI overrides. CLI keys are matched case-insensitively
    /// against the config property names (aliases: artpath, staticcore, port,
    /// updatespersec). Unknown keys are ignored so a stale option cannot brick a
    /// boot — but see <see cref="ServerConfigLoader.ApplyCli"/> which returns the
    /// recognized count for logging.
    /// </summary>
    public static ServerConfig Load(string path, IReadOnlyList<(string Key, string Value)> cli)
    {
        ServerConfig config;
        try
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions)
                     ?? throw new InvalidDataException("config parsed to null");
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException($"failed to read config '{path}': {e.Message}");
        }

        ApplyCli(config, cli);
        return config;
    }

    /// <summary>Return value: number of CLI keys actually recognized.</summary>
    public static int ApplyCli(ServerConfig config, IReadOnlyList<(string Key, string Value)> cli)
    {
        int recognized = 0;
        foreach (var (key, value) in cli)
        {
            switch (key.ToLowerInvariant())
            {
                case "artpath":
                case "art":
                    config.GetType().GetProperty(nameof(ServerConfig.ArtPath))!.SetValue(config, value);
                    recognized++; break;
                case "staticcore":
                    config.GetType().GetProperty(nameof(ServerConfig.StaticCore))!.SetValue(config, value);
                    recognized++; break;
                case "port":
                    if (!int.TryParse(value, out var port))
                        throw new InvalidDataException($"--port must be an integer, got '{value}'");
                    config.GetType().GetProperty(nameof(ServerConfig.Port))!.SetValue(config, port);
                    recognized++; break;
                case "updatespersec":
                case "ups":
                case "rate":
                    if (!int.TryParse(value, out var up))
                        throw new InvalidDataException($"--updatespersec must be an integer, got '{value}'");
                    config.GetType().GetProperty(nameof(ServerConfig.UpdatesPerSecond))!.SetValue(config, up);
                    recognized++; break;
            }
        }
        return recognized;
    }
}
