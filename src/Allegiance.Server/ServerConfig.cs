using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.Core;

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

    /// <summary>
    /// P598 — the port of native's <c>bAutoRestart</c> (igc.h:1154, default
    /// <c>false</c> at igc.h:1268): whether the mission restarts automatically
    /// after a game-over. When <c>true</c>, a game-over rotation arms a start
    /// timer of <see cref="RestartCountdownSeconds"/> on the freshly-assembled
    /// NotStarted mission; when it elapses the mission enters the normal start
    /// countdown and plays again — native's unattended restart for showcase /
    /// empty servers (fsmission.cpp:3539-3546). Off by default; when off (the
    /// default) the restart relies on the ready-up auto-start, exactly as
    /// before P598.
    /// </summary>
    public bool AutoRestart { get; init; }

    /// <summary>
    /// P598 — the port of native's <c>fRestartCountdown</c> (igc.h:1171,
    /// default <c>60.0f</c> at igc.h:1274): the seconds, when
    /// <see cref="AutoRestart"/> is on, between a game-over rotation and the
    /// rebuilt mission's auto-restart start countdown. Absent → 60.0.
    /// </summary>
    public double? RestartCountdownSeconds { get; init; }
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
    /// P612 — the admin-only sim time scale (<c>sim.timeScale</c>) for cinematic
    /// slow motion. <c>null</c> (the default, and the meaning of an absent field)
    /// = 1.0 (normal speed). Valid range [<see cref="MinTimeScale"/>,
    /// <see cref="MaxTimeScale"/>] = [0.1, 1.0]; a configured value outside it is
    /// clamped at boot (and loud), and the admin <c>timescale</c> cheat clamps to
    /// the same range. The server advances its sim clock by <c>period × scale</c>,
    /// so a battle of any size runs in clean slow motion while every connected
    /// client stays in sync (dead-reckoning extrapolates in scaled sim time via
    /// the <see cref="Allegiance.Core.Wire.TimescaleUpdate"/> message). Admin-only
    /// because it changes the pace of the whole world (LAN/demo-grade).
    /// </summary>
    public double? TimeScale { get; set; }

    /// <summary>The lowest allowed sim time scale (10× slow motion).</summary>
    public const double MinTimeScale = 0.1;
    /// <summary>The highest allowed sim time scale (normal speed; &gt;1 would speed
    /// the world up, out of scope — cinematic mode only slows).</summary>
    public const double MaxTimeScale = 1.0;

    /// <summary>Clamp a proposed scale into [<see cref="MinTimeScale"/>, <see cref="MaxTimeScale"/>].</summary>
    public static double ClampTimeScale(double v) =>
        Math.Clamp(v, MinTimeScale, MaxTimeScale);

    /// <summary>
    /// P618 — the load-driven time-dilation mode (<c>sim.timeDilation</c>):
    /// <c>"off"</c> (the default) = today's behaviour, only the P612 admin
    /// <c>timescale</c> cheat / <see cref="TimeScale"/> config moves the clock;
    /// <c>"auto"</c> = the <see cref="Allegiance.Core.Sim.TimeDilationController"/>
    /// measures the smoothed tick cost and lowers the sim time scale toward
    /// <c>nominal/cost</c> (clamped to [0.1, 1.0]) when the server can't hold real
    /// time, easing back to full speed when load drops. An admin <c>timescale</c>
    /// cheat always overrides auto (manual wins). Default <c>"auto"</c> since
    /// 2026-10-14 (Domingo, after the 221 200v200 A/B: clash39 with auto had no tick
    /// over 143 ms and no bot drops, vs 253 ms / 141 drops with off and 201 ms / 28 drops
    /// at 15 Hz; game speed averaged 0.92, minimum 0.73). Set <c>"off"</c> to disable.
    /// </summary>
    public string TimeDilation { get; set; } = "auto";

    /// <summary>P618 — EMA smoothing of the measured tick cost
    /// (<c>sim.timeDilation.alpha</c>). Lower = smoother, slower to react.</summary>
    public double? TimeDilationAlpha { get; set; }

    /// <summary>P618 — engage auto-slow when the smoothed cost exceeds
    /// <c>engageRatio × simPeriod</c> (<c>sim.timeDilation.engageRatio</c>).</summary>
    public double? TimeDilationEngageRatio { get; set; }

    /// <summary>P618 — release back to full speed when the smoothed cost falls
    /// below <c>releaseRatio × simPeriod</c> (<c>sim.timeDilation.releaseRatio</c>).
    /// Kept below <see cref="TimeDilationEngageRatio"/> for hysteresis.</summary>
    public double? TimeDilationReleaseRatio { get; set; }

    /// <summary>P618 — the minimum |target−scale| that justifies a change
    /// (<c>sim.timeDilation.deadband</c>); suppresses oscillation once converged.</summary>
    public double? TimeDilationDeadband { get; set; }

    /// <summary>P618 — max scale movement per wall second
    /// (<c>sim.timeDilation.maxChangePerSecond</c>); the rate limit that eases
    /// clients into slow motion without sudden speed jumps.</summary>
    public double? TimeDilationMaxChangePerSecond { get; set; }

    /// <summary>P618 — min wall seconds between scale adjustments
    /// (<c>sim.timeDilation.controlIntervalSeconds</c>); bounds the <c>TimescaleUpdate</c>
    /// broadcast rate alongside <see cref="TimeDilationMaxChangePerSecond"/>.</summary>
    public double? TimeDilationControlIntervalSeconds { get; set; }

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

    /// <summary>P602 — optional shared secret. When set (non-empty), an
    /// allow-listed connection is trusted ONLY if its cheat line presents this
    /// secret as the leading whitespace-delimited token
    /// (<c>"&lt;secret&gt; relocate …"</c>); a line without it is treated as
    /// ordinary chat and never as a cheat. When unset (the default), the
    /// LAN/demo behaviour is unchanged: name-only matching. The 221 test server
    /// and allegiance-mcp rely on the unset (name-only) default; leave it unset
    /// unless you need defence-in-depth against a spoofed name on a shared
    /// network.</summary>
    public string? Secret { get; set; }
}

/// <summary>
/// P600 — the per-tick profiler configuration (top-level <c>profiler</c> section).
/// A committed, OFF-by-default diagnostic: measuring where the server's single
/// simulation thread spends its tick is a pre-condition for per-sector parallel
/// simulation (P600). Absent section (or <c>enabled:false</c>) = profiler off,
/// with zero hot-path overhead (a null static check). Can also be forced via the
/// <c>ALLEGIANCE_TICK_PROFILER</c> env var (see Server.Lifecycle boot), so a live
/// run can profile without editing a config file.
/// </summary>
public sealed class ProfilerConfig
{
    /// <summary>Master switch. false (the default, and the meaning of an absent
    /// <c>profiler</c> section) leaves the server's tick un-instrumented.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to roll the reporting window over and emit an aggregate
    /// (mean/p95/max ms per tick per bucket). Default 5 s (100 ticks at 20 ups).</summary>
    public int ReportIntervalSeconds { get; set; } = 5;

    /// <summary>When set, append one CSV row per bucket per report window to this
    /// file (columns: window_start_unix, window_seconds, bucket, count, mean_ms,
    /// p95_ms, max_ms, total_ms). Null = log-only.</summary>
    public string? CsvPath { get; set; }

    /// <summary>
    /// P619 — master switch for slow-tick capture. When true (and the profiler
    /// is enabled), any single tick whose wall time meets
    /// <see cref="SlowTickMs"/> emits one <c>[slowtick]</c> line giving the top
    /// sub-step timings inside it (including the parallel broadcast build's per-
    /// worker max), plus whether a GC occurred during the tick (collection-count
    /// deltas and <c>GetTotalPauseDuration</c> delta). This is the attribution
    /// tool for the once-per-window multi-second tick that survives both ship-
    /// update modes and server GC (P619). Off by default; the <c>[slowtick]</c>
    /// machinery costs nothing when off.
    /// </summary>
    public bool SlowTickCapture { get; set; }

    /// <summary>P619 — the wall-time threshold (ms) that trips slow-tick capture.
    /// Default 250 ms. Ignored unless <see cref="SlowTickCapture"/> is true.</summary>
    public double SlowTickMs { get; set; } = 250;
}

/// <summary>
/// P603 — the parallel per-peer broadcast-build configuration (top-level
/// <c>broadcast</c> section). OFF by default (an absent section, or
/// <c>parallelBuild:false</c>, preserves today's serial, byte-for-byte
/// behaviour). When on, <c>BroadcastShipStates</c> splits into a serial
/// snapshot phase + a parallel per-peer build phase + a serial enqueue phase,
/// and <c>BroadcastShipsUpdate</c> parallelises across sectors (within-sector
/// peer order — and the per-ship Deviation staleness accumulator — stay
/// serial). See docs/reports/P603_PARALLEL_BROADCAST_BUILD.md.
/// </summary>
public sealed class BroadcastConfig
{
    /// <summary>
    /// Master switch. <c>false</c> (the default, and the meaning of an absent
    /// <c>broadcast</c> section) leaves both broadcast builders running exactly
    /// as they did before P603 — the same serial code path, byte-for-byte.
    /// <c>true</c> enables the parallel per-peer build path (gated per build).
    /// </summary>
    public bool ParallelBuild { get; set; }

    /// <summary>
    /// P603 — cap on the parallel build phase's degree of parallelism
    /// (<c>Parallel.ForEach</c> <c>MaxDegreeOfParallelism</c>). When set, it is
    /// clamped to <c>[1, ProcessorCount-1]</c>; when null, the default
    /// <c>ProcessorCount - 1</c> is used (leaving one core free for the
    /// transport / OS, per the task's bounded-degree guidance). Diagnostic only —
    /// it never changes which bytes are produced, only how fast.
    /// </summary>
    public int? MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// P604 — which per-peer ship-state update model the server uses:
    /// <c>"port"</c> (the default; today's per-tick O(N²) teammate
    /// <c>SINGLE_SHIP_UPDATE(78)</c> position <c>posBatch</c> stream is sent to
    /// every same-side peer every tick, plus the P608 owner own-ship stream) or
    /// <c>"native"</c> (the port's per-tick teammate <c>posBatch</c> stream is
    /// suppressed and ALL remote-ship positions travel only through the native
    /// deviation-ranked HEAVY/LIGHT_SHIPS_UPDATE(176/175) budget — see
    /// docs/reports/P604_NATIVE_SHIP_UPDATE_BUDGET.md). Overridable at boot by
    /// <c>ALLEGIANCE_SHIP_UPDATE_MODEL</c> (env wins). An unrecognized value
    /// fails loudly at boot. The default is <c>"port"</c>; it flips to
    /// <c>"native"</c> in a follow-up once the A/B (and a 221 run) shows it is
    /// faster, smooth on the client, and the bots navigate.
    /// </summary>
    public string? ShipUpdateModel { get; set; }
}

/// <summary>
/// P605 — the server transport's send-draining mode (top-level
/// <c>transport</c> section). Absent section (the default) = thread-per-peer,
/// today's behaviour — each peer's send queue is drained by its own
/// dedicated writer thread. <c>mode: "pool"</c> selects the fixed send-worker
/// pool (a bounded set of workers draining hash-partitioned per-peer queues,
/// per-peer order preserved, slow peers skipped not blocked). See
/// docs/reports/P605_SEND_WORKER_POOL_TRANSPORT.md.
/// </summary>
public sealed class TransportConfig
{
    /// <summary>
    /// <c>"thread-per-peer"</c> (default) or <c>"pool"</c>. Case-insensitive;
    /// overridable at boot by <c>ALLEGIANCE_TRANSPORT_MODE</c> (env wins).
    /// An unrecognized value fails loudly at boot.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// Pool mode only: how many send workers to spin up. When unset (the
    /// default), about the core count (<c>ProcessorCount</c>). Overridable at
    /// boot by <c>ALLEGIANCE_TRANSPORT_POOL_WORKERS</c>. Ignored in
    /// thread-per-peer mode.
    /// </summary>
    public int? PoolWorkers { get; set; }

    /// <summary>
    /// P619c — the liveness grace (seconds) granted to a connected HUMAN client
    /// (non-harness character name) before it is disconnected for making no
    /// write progress; the MCP bots keep the strict 30 s limit. Default 60
    /// (twice native's 30 s link-dead tolerance). Overridable at boot by
    /// <c>ALLEGIANCE_TRANSPORT_HUMAN_GRACE_SECONDS</c>.
    /// </summary>
    public int? HumanGraceSeconds { get; set; }
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
    /// <summary>P629 simulation rules, including the stress-test crowding override.</summary>
    public SimConfig Sim { get; set; } = new();
    /// <summary>Root of the artwork tree (contains the core and map files).
    /// Resolved by <see cref="ExternalPaths"/>: <c>$ALLEGIANCE_ARTWORK_PATH</c>,
    /// else the gitignored <c>local.env</c>, else empty (validation in
    /// <see cref="Server.ValidatePaths"/> then fails with an actionable
    /// message). No fallback to an owner's machine path.</summary>
    public string ArtPath { get; set; } =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

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

    /// <summary>
    /// P600 — the per-tick profiler settings. Null/absent (the default) = the
    /// profiler is OFF, with no hot-path overhead. See <see cref="ProfilerConfig"/>.
    /// </summary>
    public ProfilerConfig? Profiler { get; init; }

    /// <summary>
    /// P603 — the parallel per-peer broadcast-build settings. Null/absent (the
    /// default) = OFF: both broadcast builders keep today's serial, byte-for-byte
    /// behaviour. See <see cref="BroadcastConfig"/>.
    /// </summary>
    public BroadcastConfig? Broadcast { get; init; }

    /// <summary>
    /// P605 — the server transport's send-draining mode. Null/absent (the
    /// default) = thread-per-peer, today's behaviour (unchanged). See
    /// <see cref="TransportConfig"/>.
    /// </summary>
    public TransportConfig? Transport { get; init; }
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
            if (e is FileNotFoundException or DirectoryNotFoundException)
                throw new InvalidDataException(
                    $"failed to read config '{path}': {e.Message}. " +
                    $"Copy '{ExampleFor(path)}' to '{path}' and edit the placeholders " +
                    "(see the header comment in Program.cs). Artwork path can also come from " +
                    "$ALLEGIANCE_ARTWORK_PATH or the repo-root local.env.");
            throw new InvalidDataException($"failed to read config '{path}': {e.Message}");
        }

        ApplyCli(config, cli);
        return config;
    }

    /// <summary>Best-effort example file to suggest for a missing config.
    /// For the default <c>server.json</c> the committed
    /// <c>server.example.json</c> is the exact match.</summary>
    private static string ExampleFor(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        var example = name.Equals("server.json", StringComparison.OrdinalIgnoreCase)
            ? "server.example.json"
            : name.Insert(name.LastIndexOf('.') is var i && i > 0 ? i : name.Length, ".example");
        return string.IsNullOrEmpty(dir) ? example : Path.Combine(dir, example);
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
