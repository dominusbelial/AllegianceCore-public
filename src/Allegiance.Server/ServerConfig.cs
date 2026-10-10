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

    /// <summary>
    /// P473 — the wire-level test-harness economy grant. Off by default
    /// (<c>null</c> = absent section; behaviour byte-identical to P441). When
    /// enabled it is a PORT EXTENSION (native has no money/tech cheat —
    /// <c>fCanCheat</c> only relaxes rank limits and win checks,
    /// FedSrv/fsship.h:337-339, fsmission.cpp:1877,3810): it applies ONLY to a
    /// connection whose character name starts with <see cref="TestHarnessConfig.CharacterNamePrefix"/>
    /// (so a real player can never receive it), grants that wallet at seat, and
    /// can skip the BUY_LOADOUT tech gate while still charging real prices
    /// through the real debit path. Loudly logged at boot and once per granted
    /// connection so it is never silently on.
    /// </summary>
    public TestHarnessConfig? TestHarness { get; set; }

    /// <summary>
    /// P509 — the live <c>testUnlock</c> option: grant every side all the tech
    /// bits any real core buyable requires (via the real tech path), plus give
    /// every seated player wallet a test <see cref="TestUnlockConfig.Money"/>,
    /// so a human can actually playtest every hull and weapon from a fresh
    /// side's garrison without researching/building techbases. Off by default
    /// (<c>null</c> = absent section; behaviour byte-identical to P441/P444/
    /// P468/P488 — no side gets extra tech, no wallet gets money). See
    /// <see cref="TestUnlockConfig"/>.
    /// </summary>
    public TestUnlockConfig? TestUnlock { get; set; }

    /// <summary>
    /// P554 — the privileged admin-cheat hook. Off by default (<c>null</c> =
    /// absent section; behaviour byte-identical to P441 — no chat line is ever
    /// interpreted as a cheat). When enabled it is a PORT EXTENSION (native has
    /// no teleport/relocate cheat at all — see the relocate token docs) that
    /// routes privileged players' chat lines through a <c>DoCheatCode</c>
    /// equivalent (<c>FedSrv.CPP:2814</c>, called from the chat handler
    /// <c>FedSrv.CPP:5023</c> under <c>_TEST</c>; a true return = "consumed, not
    /// sent as chat"). Privilege mirrors native <c>CanCheat</c>/<c>PrivilegedUser</c>
    /// (<c>FedSrv.CPP:664</c>) via <see cref="AdminCheatsConfig.AllowNames"/>.
    /// Loudly logged at boot (<c>Server.Lifecycle.cs</c>), like
    /// <see cref="TestUnlock"/>. Never on in a default config.
    /// </summary>
    public AdminCheatsConfig? AdminCheats { get; set; }
}

/// <summary>
/// P473 — the shape of the <c>liveSettings.testHarness</c> grant: an explicit,
/// off-by-default, prefix-scoped testing convenience so wire-level test harnesses
/// (allegiance-mcp's spawn/matrix bots, vizfight) can buy every real hull the way
/// a fully-developed real player could, instead of silently flying default ships
/// under P441's tech gating. Never applies to a non-prefixed (human) connection.
/// </summary>
public sealed class TestHarnessConfig
{
    /// <summary>Master switch. <c>false</c> (the default, and the meaning of an
    /// absent <c>testHarness</c> section) leaves behaviour byte-identical to
    /// P441 — no money grant, no tech-gate bypass, for anyone.</summary>
    public bool Enabled { get; set; }

    /// <summary>The character-name prefix that marks a connection as a test
    /// harness. Only a connection whose <c>LogonRequest.CharacterName</c>
    /// starts with this (ordinal, case-sensitive) is eligible for the grant —
    /// the guarantees that a human's random character name never matches and
    /// the grant never touches real players. Defaults to <c>"mcp-"</c>, the
    /// prefix the MCP and vizfight bot character names now use.</summary>
    public string CharacterNamePrefix { get; set; } = "mcp-";

    /// <summary>The wallet granted to a harness connection on seat, in credits.
    /// Set ABSOLUTELY at seat (overriding the P443 starting money), so a fresh
    /// side's garrison economy can never price the harness out of a hull.</summary>
    public int Money { get; set; }

    /// <summary>When <c>true</c>, a harness connection's <c>BUY_LOADOUT</c>
    /// skips ONLY the tech gate (the station <c>CanBuy</c> hull/part check). It
    /// still pays the real price through the real debit path — so pricing keeps
    /// being exercised — and still respects the funds gate, the drone-hull
    /// (<c>GroupId&lt;0</c>) gate, and the hardpoint-compatibility gate.</summary>
    public bool IgnoreTechGate { get; set; } = true;
}

/// <summary>
/// P509 — the shape of the <c>liveSettings.testUnlock</c> option: an explicit,
/// off-by-default testing convenience so a HUMAN (and bots) can buy every real
/// hull and weapon a developed side could, straight from a fresh side's
/// garrison — the natural companion to P473's <c>testHarness</c>, which skips
/// the tech gate on the SERVER for <c>mcp-</c>-prefixed connections only and so
/// never lifts the client shop's P468 tech filter for a real player.
/// <para>Native has no "unlock all": its mission params only <em>restrict</em>
/// tech (<c>bAllowDevelopments</c> sideigc.cpp:117 / fsmission.cpp:1948, and
/// the <c>bAllowSupremacyPath</c>/<c>bAllowTacticalPath</c>/<c>bAllowExpansionPath</c>
/// toggles asteroidigc.cpp:183-195). This is therefore a <b>port extension for
/// testing</b> — the same spirit as P473 — that is explicit and loudly logged,
/// never implicit.</para>
/// <para>When enabled the grant (1) ORs the <b>required-tech union</b> of the
/// core into every side's <see cref="Allegiance.Core.Model.Side.DevelopmentTechs"/>
/// through the same tech path research uses (<c>ApplyDevelopmentTechs</c>,
/// sideigc.h:194-210) so clients get <c>SIDE_TECH_CHANGE(75)</c> and the shop
/// (P468) + investment screen (P487) agree with the server's buy-gate (P441/
/// P444/P283), and (2) sets every seated player's wallet to <see cref="Money"/>
/// at seat (after P443 starting money) so purchases are affordable — purchases
/// still go through real pricing and the real debit path.</para>
/// <para><b>Deliberate scope:</b> only <em>buyability</em> is unlocked. The
/// union is derived by OR-ing every hull, part, launcher, station-type, drone
/// and development <c>TtbmRequired</c> — NOT by marking developments
/// completed — so developments with <b>global-attribute effects</b> are
/// deliberately <b>not</b> marked complete and flight/combat stats stay
/// unmodified (the default the task specifies). P470's <c>GroupId &gt;= 0</c>
/// drone-hull gate and P488's <c>IsObsolete</c> rule still apply unchanged.</para>
/// </summary>
public sealed class TestUnlockConfig
{
    /// <summary>Master switch. <c>false</c> (the default, and the meaning of an
    /// absent <c>testUnlock</c> section) leaves behaviour byte-identical to
    /// P441/P444/P468/P488 — no tech granted, no wallet money, for anyone.</summary>
    public bool Enabled { get; set; }

    /// <summary>When <c>true</c> (the default), every side is granted the full
    /// required-tech union of the core (all buyable tech bits). When <c>false</c>
    /// and <c>Enabled</c> is true, only the seat-money grant applies (no tech).</summary>
    public bool AllTech { get; set; } = true;

    /// <summary>The wallet granted to every seated player when enabled, in
    /// credits. Set ABSOLUTELY at seat (overriding/refreshing the P443 starting
    /// money), so a fresh side's garrison economy can never price a tester out
    /// of a hull. Purchases still pay the real price through the real debit path.</summary>
    public int Money { get; set; }
}

/// <summary>
/// P554 — the shape of the <c>liveSettings.adminCheats</c> option: an explicit,
/// off-by-default, allow-listed port of native's privileged chat-cheat hook
/// (<c>DoCheatCode</c>, <c>FedSrv.CPP:2814</c>, called from the chat handler
/// <c>FedSrv.CPP:5023</c>). Native decides privilege at connection time —
/// <c>CanCheat()</c> (true only in <c>_TEST</c> builds or for the
/// <c>szBotAuthenticationGuid</c> CDKey, <c>FedSrv.CPP:914-921</c>) or
/// <c>UTL::PrivilegedUser(name)</c> (a case-insensitive allow-list plus
/// <c>@HQ</c>/<c>@Dev</c>/<c>@Alleg</c>/<c>@Zone</c> name suffixes,
/// <c>_Utility/Utility.cpp:46-82</c>), both checked against the join gate at
/// <c>FedSrv.CPP:664</c>. This port's privilege = <see cref="Enabled"/> AND
/// the sender's character name in <see cref="AllowNames"/> (case-insensitive,
/// mirroring native's list comparison). Disabled unless configured, so a
/// default server never treats any chat line as a cheat.
/// </summary>
public sealed class AdminCheatsConfig
{
    /// <summary>Master switch. <c>false</c> (the default, and the meaning of an
    /// absent <c>adminCheats</c> section) leaves behaviour byte-identical to
    /// P441 — no chat line is ever interpreted as a cheat, for anyone (even a
    /// name in <see cref="AllowNames"/>).</summary>
    public bool Enabled { get; set; }

    /// <summary>The character names allowed to use admin cheats (the port of
    /// native <c>UTL::PrivilegedUser</c>'s allow-list, <c>Utility.cpp:60-67</c>;
    /// compared case-insensitively, exactly as native upper-cases both sides).
    /// A privileged player may issue the <c>relocate</c> cheat on their OWN
    /// ship only (see the token docs) — never on an arbitrary ship.</summary>
    public List<string> AllowNames { get; set; } = [];
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
    /// P594 — the P591b/P591c warp re-entry guard (<c>Sector.WarpReEntryGuardEnabled</c>)
    /// is a documented NON-NATIVE quality-of-life safety net and is <b>OFF by
    /// default</b> (a native ship never needs it — its GotoPlan flies through a
    /// warp at cruise speed so the +15 exit push clears the arrival aleph inside
    /// the ~2 s m_warpState re-arm). Set <c>true</c> (server.json
    /// <c>warpReEntryGuard</c> or CLI <c>--warpReEntryGuard=true</c>) to opt in;
    /// the guard's block counter <c>Ship.NumWarpReEntryBlocks</c> stays live so a
    /// routing regression that reintroduces re-entry still surfaces. Null (the
    /// default) means off.
    /// </summary>
    public bool? WarpReEntryGuard { get; set; }

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
                case "warpreentryguard":
                    if (bool.TryParse(value, out var guard))
                        config.WarpReEntryGuard = guard;
                    else
                        config.WarpReEntryGuard = true;
                    recognized++; break;
            }
        }
        return recognized;
    }
}
