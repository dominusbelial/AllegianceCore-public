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

    /// <summary>
    /// P397 — when set, this game's mission map is <b>generated procedurally</b>
    /// (<c>Allegiance.Core.MapGen.MissionMapGenerator</c>) instead of loaded from
    /// <see cref="Map"/> (which is then ignored). <c>null</c>/absent (the
    /// default) preserves today's exact file-load behaviour byte-for-byte — the
    /// generator is a pure addition, mirroring the opt-in <c>liveSettings</c>
    /// convention. See <see cref="MapGenConfig"/> for the tunables.
    /// </summary>
    public MapGenConfig? MapGen { get; init; }
}

/// <summary>
/// P397/P398/P399 — per-game procedural map-generation settings. All numeric fields
/// are nullable: an absent field falls back to the native default baked into
/// <see cref="Allegiance.Core.MapGen.MapGenParams"/> (which this config maps
/// onto). Supported <see cref="Type"/>s are the ring family (P398) and the
/// radial/spoke family (P399): <c>SingleRing</c>, <c>DoubleRing</c>,
/// <c>BigRing</c>, <c>HiLo</c>, <c>HiHigher</c>, <c>InsideOut</c>,
/// <c>LargeSplit</c>, <c>Star</c>, <c>PinWheel</c>, <c>DiamondRing</c>,
/// <c>SnowFlake</c>.
/// </summary>
public sealed class MapGenConfig
{
    /// <summary>The topology name, e.g. <c>"HiHigher"</c>. Matched
    /// case-insensitively against <see cref="Allegiance.Core.MapGen.MapGenType"/>.</summary>
    public string Type { get; init; } = "";

    /// <summary>0 ⇒ one team cluster per team; non-zero ⇒ two (<c>iMapSize</c>).</summary>
    public int? MapSize { get; init; }

    public int? PlayerSectorMineableAsteroids { get; init; }
    public int? PlayerSectorSpecialAsteroids { get; init; }
    public int? PlayerSectorAsteroids { get; init; }
    public int? PlayerSectorTreasures { get; init; }
    public int? NeutralSectorMineableAsteroids { get; init; }
    public int? NeutralSectorSpecialAsteroids { get; init; }
    public int? NeutralSectorAsteroids { get; init; }
    public int? NeutralSectorTreasures { get; init; }

    public double? He3Density { get; init; }
    public bool? FlagsGame { get; init; }
    public bool? ArtifactsGame { get; init; }
}

/// <summary>
/// Opt-in lobby heartbeat configuration (<see cref="ServerConfig.Lobby"/>). When
/// non-null the server announces itself to an <c>Allegiance.Lobby</c> directory at
/// <see cref="Url"/> on a fixed cadence; when null (the default) no heartbeat client
/// is ever constructed and the server makes no HTTP calls at all. Mirrors the
/// nullable-opt-in convention of <see cref="ServerConfig.CapturePath"/>.
/// </summary>
public sealed class LobbyConfig
{
    /// <summary>Base URL of the lobby directory (e.g. <c>http://localhost:5000</c>).</summary>
    public string Url { get; init; } = "";

    /// <summary>Human-readable name shown in the directory listing.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Publicly reachable address advertised to players (hostname/IP, no scheme).</summary>
    public string PublicAddress { get; init; } = "";

    /// <summary>Seconds between heartbeats. Registry default expiry is 90s, so the
    /// default 25s tolerates ~3 missed beats before a live server flaps off the list.</summary>
    public int HeartbeatIntervalSeconds { get; init; } = 25;
}

/// <summary>
/// P378 — the server's dedicated live/testing ("debug") settings section. A
/// clearly separated object in the config file (<c>server.json</c> /
/// <c>docker/compose.server.json</c>: <c>"liveSettings": { ... }</c>) that is
/// the single, obvious, growable home for runtime/testing-tunable knobs as
/// more are added later — distinct from the mission / network / artwork-path
/// config. The first real field is <see cref="DebugMode"/>. Crucially, the
/// mechanism is a simple reusable boolean named <c>DebugMode</c> (NOT a
/// single-purpose <c>LifepodDebugMode</c>), so a future debug convenience can
/// gate on the same flag without a second knob. An absent section equals all
/// defaults (debug off, no override) — byte-for-byte identical to pre-P378
/// behaviour.
/// </summary>
public sealed class LiveSettingsConfig
{
    /// <summary>
    /// P378 — the server-wide debug-mode flag (default <c>false</c>). When
    /// <c>true</c>, useful live-testing conveniences become available; the
    /// only current effect is that a drifting Lifepod's oxygen endurance
    /// defaults to <see cref="DebugDefaultLifepodEnduranceSeconds"/> (30 min)
    /// when no explicit <see cref="LifepodEnduranceSeconds"/> is set, so a
    /// tester has real time to exercise rescue/collision scenarios instead of
    /// racing the native 5-minute timer. Loudly logged at boot when active so
    /// it is never silently left on. A genuinely reusable flag for the next
    /// debug convenience, not a per-feature toggle.
    /// </summary>
    public bool DebugMode { get; set; }

    /// <summary>
    /// P378 — override the lifepod oxygen endurance, in seconds, used when a
    /// ship's hull becomes a drifting Lifepod. <c>null</c> (the default, and
    /// the meaning of an absent field) preserves today's exact behaviour —
    /// the real native 300 s (5 min) baked into the core's
    /// <c>FloatConstants[c_fcidLifepodEndurance = 31]</c>. When set (e.g.
    /// 1800 for a 30-min debug session, or 600 for a "normal but more
    /// forgiving" live-play session) that value wins REGARDLESS of
    /// <see cref="DebugMode"/> — an explicit value is always honoured over
    /// the debug default. This is a real general-purpose tunable, not a
    /// debug-mode-only value.
    /// </summary>
    public double? LifepodEnduranceSeconds { get; set; }

    /// <summary>The generous endurance applied when <see cref="DebugMode"/> is
    /// on and no explicit <see cref="LifepodEnduranceSeconds"/> is given:
    /// 1800 s = 30 minutes, the requested testing window.</summary>
    public const double DebugDefaultLifepodEnduranceSeconds = 1800.0;
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
    public string ArtPath { get; set; } =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    /// <summary>Static core file name, relative to <see cref="ArtPath"/>.</summary>
    public string StaticCore { get; set; } = "PCore001.igc";

    /// <summary>TCP port the logon transport listens on.</summary>
    public int Port { get; set; } = 6073;

    /// <summary>Tick cadence (Hz). Fixed period = 1000 / updatesPerSecond ms.</summary>
    public int UpdatesPerSecond { get; set; } = 20;

    /// <summary>
    /// When set (non-empty), write every received and sent packet to this file
    /// in <c>CaptureStream</c> frame format for offline inspection with
    /// <c>tools/fedmsgdump</c>. Empty/null (the default) = capture off, with no
    /// behavioural or timing impact on the server.
    /// </summary>
    public string? CapturePath { get; set; }

    /// <summary>
    /// P318 — when true, emit the high-frequency per-tick/per-message diagnostic
    /// lines (<c>[ships]</c>/<c>[ctrl]</c>/<c>[shipstatus]</c>/<c>[teamshipstatus]</c>,
    /// the per-tick missile broadcasts, and the NPC stand-by/mining lines) that are
    /// otherwise silent. Default false — the minimal production state that removes the
    /// review finding H6's console-write volume and the stalled-stdout freeze vector.
    /// Flip to true (server.json <c>consoleVerbose</c> or <c>--verbose</c>) for
    /// debugging without recompiling.
    /// </summary>
    public bool ConsoleVerbose { get; set; }

    /// <summary>When set, enable the lobby heartbeat (announce to an
    /// <c>Allegiance.Lobby</c> directory on a fixed cadence). Null (the default) =
    /// fully opt-out: no heartbeat client is constructed and no HTTP call is ever made.</summary>
    public LobbyConfig? Lobby { get; init; }

    /// <summary>
    /// P378 — the dedicated live/testing settings section (see
    /// <see cref="LiveSettingsConfig"/>). Absent section = defaults (debug off,
    /// no lifepod override), so a normal server run with no config change is
    /// byte-for-byte identical to pre-P378 behaviour.
    /// </summary>
    public LiveSettingsConfig LiveSettings { get; init; } = new();

    /// <summary>The game worlds to instantiate at boot.</summary>
    public List<GameConfig> Games { get; init; } = [];
}

/// <summary>Static helpers for reading <see cref="ServerConfig"/> from JSON.</summary>
public static class ServerConfigLoader
{
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
            config = JsonSerializer.Deserialize(json, ServerJsonContext.Default.ServerConfig)
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
                    config.ArtPath = value; recognized++; break;
                case "staticcore":
                    config.StaticCore = value; recognized++; break;
                case "port":
                    if (!int.TryParse(value, out var port))
                        throw new InvalidDataException($"--port must be an integer, got '{value}'");
                    config.Port = port; recognized++; break;
                case "updatespersec":
                case "ups":
                case "rate":
                    if (!int.TryParse(value, out var up))
                        throw new InvalidDataException($"--updatespersec must be an integer, got '{value}'");
                    config.UpdatesPerSecond = up; recognized++; break;
                case "capture":
                case "capturefile":
                    if (string.IsNullOrWhiteSpace(value))
                        throw new InvalidDataException("--capture needs a non-empty file path");
                    config.CapturePath = value; recognized++; break;
                case "verbose":
                case "consoleverbose":
                    if (bool.TryParse(value, out var verbose))
                        config.ConsoleVerbose = verbose;
                    else
                        config.ConsoleVerbose = true;   // bare --verbose / --verbose=true
                    recognized++; break;
            }
        }
        return recognized;
    }
}
