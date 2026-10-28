using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.Core;

namespace Allegiance.Server;

/// <summary>Showcase mode and economy pacing. At incomeMultiplier=1 the periodic economy is native.
/// Income still enters the team treasury and is distributed to real player wallets (fsmission.cpp:2294-2324).</summary>
public sealed class ShowcaseConfig
{
    /// <summary>Opt-in orchestration; setters preserve absent source-generated JSON defaults.</summary>
    public string TechMode { get; set; } = "unlocked";
    public double IncomeMultiplier { get; set; } = 1;
    /// <summary>Showcase research accelerator, not native gameplay. Ignored in unlocked mode.</summary>
    public double ResearchTimeScale { get; set; } = 1;
    /// <summary>Hard simulation-time bound after a research showcase finale fires. Port orchestration.</summary>
    public double FinaleTimeoutSeconds { get; set; } = 300;
    public bool LoopResearchPlans { get; set; }
    /// <summary>Opt-in preset orchestration; native has creator-selected goals, no rotation.</summary>
    public GoalRotationConfig? GoalRotation { get; set; }
    public void Validate()
    {
        if (TechMode is not ("unlocked" or "research"))
            throw new InvalidDataException("showcase.techMode must be unlocked|research");
        if (!double.IsFinite(IncomeMultiplier) || IncomeMultiplier < 1 || IncomeMultiplier > 10000)
            throw new InvalidDataException("showcase.incomeMultiplier must be finite and between 1 and 10000");
        if (!double.IsFinite(FinaleTimeoutSeconds) || FinaleTimeoutSeconds < 1 || FinaleTimeoutSeconds > 3600)
            throw new InvalidDataException("showcase.finaleTimeoutSeconds must be finite and between 1 and 3600");
        if (!double.IsFinite(ResearchTimeScale) || ResearchTimeScale < 1 || ResearchTimeScale > 64)
            throw new InvalidDataException("showcase.researchTimeScale must be finite and between 1 and 64");
    }
}

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
    /// <summary>Native MissionParams goal fields. Absent/empty = existing
    /// dedicated-server policy (no event goal; optional legacy countdown).</summary>
    public Allegiance.Core.Model.MissionGoals Goals { get; set; } = new();

    public Allegiance.Core.Model.MissionGoals ResolveGoals()
    {
        if (Goals is null) throw new InvalidDataException($"game '{Name}': goals must be an object");
        return ValidateGoals(Goals with { });
    }

    internal Allegiance.Core.Model.MissionGoals ValidateGoals(Allegiance.Core.Model.MissionGoals goals)
    {
        if (GameLengthSeconds is { } duration)
        {
            if (!double.IsFinite(duration) || duration < 0 || duration > float.MaxValue)
                throw new InvalidDataException($"game '{Name}': gameLengthSeconds must be finite and nonnegative");
            if (goals.GameLengthSeconds != 0 && goals.GameLengthSeconds != (float)duration)
                throw new InvalidDataException($"game '{Name}': gameLengthSeconds conflicts with goals.gameLengthSeconds");
            goals.GameLengthSeconds = (float)duration;
        }
        goals.Validate($"game '{Name}'.goals");
        if (goals.IsProsperityGame && !AllowDevelopments)
            throw new InvalidDataException($"game '{Name}': prosperity requires allowDevelopments=true (Igc/igc.h:1484-1489)");
        if (goals.IsTerritoryGame && MapGen is { } map &&
            (string.Equals(map.Type, "Brawl", StringComparison.OrdinalIgnoreCase) ||
             (Teams == 2 && (string.Equals(map.Type, "SingleRing", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(map.Type, "DoubleRing", StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidDataException($"game '{Name}': territory requires a larger generated map (Igc/igc.h:1494-1503)");
        return goals;
    }

    /// <summary>Native bAllowDevelopments, FedSrv/fsmission.cpp:2520.</summary>
    public bool AllowDevelopments { get; set; } = true;

    /// <summary>
    /// P797 — native <c>MissionParams::bShowMap</c> (<c>Igc/igc.h:1123</c>,
    /// "Show all warps at the start of the game"): when true every aleph is
    /// revealed to every side at mission creation (<c>Igc/mapmakerigc.cpp:999-1018</c>
    /// <c>RevealMap</c>, repeated at start by <c>Igc/missionigc.cpp:4056-4071</c>).
    /// <b>Default <c>false</c></b> is native's default (<c>igc.h:1244</c>) — a side
    /// must scout its alephs. Set it for a showcase/test world that wants the
    /// pre-P797 "everyone knows the map" behaviour.
    /// </summary>
    public bool ShowMap { get; set; }

    /// <summary>
    /// P797 — native <c>MissionParams::bShowHomeSector</c> (<c>Igc/igc.h:1148</c>,
    /// "Show everything in a player's home sector at start of game"): when true,
    /// every model in a cluster where a side owns a station is revealed to that
    /// side (<c>Igc/mapmakerigc.cpp:969-997</c>). <b>Default <c>false</c></b> is
    /// native's default (<c>igc.h:1313</c>).
    /// </summary>
    public bool ShowHomeSector { get; set; }

    /// <summary>
    /// P767 / SIM-0100 — enables the NPC wingman "getting ammo" resupply
    /// latch (mission option <c>Mission.NpcWingmanAmmoResupply</c>). Native's
    /// trigger lives only in the offline client (<c>BaseClient::Reload</c>'s
    /// <c>!m_fm.IsConnected()</c> branch, clintlib.cpp:3697-3717); FedSrv
    /// inherits <c>IgcSite::Reload</c>'s <c>false</c> default (Igc/igc.h:4923),
    /// so native MULTIPLAYER wingmen never resupply-travel. Default
    /// <c>false</c> = that native multiplayer behaviour; single-player/skirmish
    /// (<c>SkirmishOptions.CreateConfig</c>) sets it <c>true</c>, mirroring the
    /// offline client where the wingman latches, flies to the nearest
    /// <c>c_sabmReload</c> station (or a landable carrier) and chats "Out of
    /// ammo, I'm getting resupplies."</summary>
    public bool NpcWingmanAmmoResupply { get; set; }
    public bool AllowShipyardPath { get; set; } = true;
    public bool AllowExpansionPath { get; set; } = true;
    public bool AllowTacticalPath { get; set; } = true;
    public bool AllowSupremacyPath { get; set; } = true;
    public int InitialMinersPerTeam { get; set; } = 1;
    /// <summary>Plan directory override (core/civ*-*.json); research mode defaults to bundled showcase-plans.</summary>
    public string? ResearchPlanDirectory { get; set; }
    /// <summary>Selections by side id: auto, generated, or a plan name.</summary>
    public Dictionary<short, string> ResearchPlans { get; set; } = new();
    /// <summary>Native bAllowAlliedRip, FedSrv/FedSrv.CPP:13387.</summary>
    public bool AllowAlliedRip { get; set; }

    /// <summary>
    /// P856 — native <c>MissionParams::bAllowFriendlyFire</c>
    /// (<c>Igc/igc.h:1149</c>, "Allow friends to damage friends"). The native
    /// default is <b>false</b>: <c>MissionParams::Reset</c> zeroes the struct
    /// (<c>Igc/igc.h:1246</c>) and never assigns the flag, so a fresh game has
    /// friendly fire off. Off means the same-side and allied damage gates in
    /// <c>CshipIGC::ReceiveDamage</c> (<c>Igc/shipIGC.cpp:1222-1228</c>),
    /// <c>CstationIGC::ReceiveDamage</c> (<c>Igc/stationIGC.cpp:126-133</c>),
    /// <c>CmissileIGC::ReceiveDamage</c> (<c>Igc/missileigc.h:86-94</c>) and
    /// <c>CprobeIGC::ReceiveDamage</c> (<c>Igc/probeigc.h:74-89</c>) suppress
    /// non-repair friendly/allied damage. Reaches the mission as
    /// <see cref="Allegiance.Core.Model.Mission.AllowFriendlyFire"/>.
    /// </summary>
    public bool AllowFriendlyFire { get; set; }

    /// <summary>
    /// P856 — native <c>MissionParams::bAllowAlliedViz</c>
    /// (<c>Igc/igc.h:1127</c>, "ALLY"). Native's <c>MissionParams::Reset</c>
    /// sets it explicitly false (<c>Igc/igc.h:1243</c>). When on, allied sides
    /// see each other's models and share scans (<c>FedSrv/FedSrv.CPP:10893-10895</c>,
    /// <c>:10989-11000</c>), the start map reveals allied home clusters
    /// (<c>Igc/mapmakerigc.cpp:473-474</c> → <c>RevealAlliedClusters</c>,
    /// <c>:1020-1058</c>) and an armed probe's first shot reveals it to allied
    /// sides without scanners (<c>Igc/probeigc.cpp:448-463</c>). Reaches the
    /// mission as <see cref="Allegiance.Core.Model.Mission.AllowAlliedViz"/>.
    /// <para>Native's team screen also flips the <em>client's local copy</em> on
    /// whenever an alliance exists (<c>WinTrek/teamscreen.cpp:2335-2337</c>) and
    /// pushes it to the server through <c>MISSIONPARAMS</c>; this port has no
    /// MISSIONPARAMS update path (P722), so the configured value is the
    /// authoritative one — stated, not silently reinterpreted.</para>
    /// </summary>
    public bool AllowAlliedViz { get; set; }

    /// <summary>Human name, used in logs only.</summary>
    public string Name { get; set; } = "";

    /// <summary>File name of the map, relative to <see cref="ServerConfig.ArtPath"/>.</summary>
    public string Map { get; set; } = "";

    /// <summary>Number of sides. Sides are built for ids 0..Teams-1.</summary>
    public int Teams { get; set; }

    /// <summary>Native MissionParams::nMaxPlayersPerTeam (Igc/igc.h:1334).
    /// Explicit larger capacities remain available for extended fleet scenarios.</summary>
    public int MaxPlayersPerTeam { get; set; } = 100;

    /// <summary>Native lobby default (FedSrv/fslobby.cpp:33): 0x7ffe selects
    /// rank balance; 0x7fff disables balance; other values limit player-count
    /// imbalance after NotStarted (FedSrv/fsmission.cpp:4612-4671).</summary>
    public int MaxTeamImbalance { get; set; } = 0x7ffe;

    /// <summary>P852 — native <c>MissionParams::nMinPlayersPerTeam</c>
    /// (Igc/igc.h:1211). A side with fewer connected players than this is
    /// forced not-ready by <c>CheckForSideAllReady</c>
    /// (FedSrv/fsmission.cpp:5501-5503). Default 1 is native's default
    /// (Igc/igc.h:1333, KGJV #114), preserving the port's previous
    /// "at least one connected player" floor.</summary>
    public int MinPlayersPerTeam { get; set; } = 1;

    /// <summary>P852 — native <c>MissionParams::bAllowDefections</c>
    /// (Igc/igc.h:1134). When false, a player seated on a real side may not
    /// switch sides once the mission is STAGE_STARTED —
    /// <c>CheckPositionRequest</c> refuses with DPR_NoDefections
    /// (FedSrv/fsmission.cpp:4583-4590). Default <c>true</c> is native's
    /// default (Igc/igc.h:1255).</summary>
    public bool AllowDefections { get; set; } = true;

    /// <summary>Per-team <c>rgCivID[]</c> — indexes <see cref="ServerConfig.StaticCore"/>
    /// civilizations. JSON null or -1 means random at each assembly; length must be &gt;= Teams.</summary>
    [JsonConverter(typeof(CivIdsJsonConverter))]
    public int[] CivIds { get; set; } = [];

    /// <summary>First match civilization seed; later match seeds follow a reproducible stream.
    /// Omit to draw and log a seed. Each logged match seed replays as the first civSeed.</summary>
    public int? CivSeed { get; set; }

    /// <summary>Per-team display names; length must be &gt;= Teams.</summary>
    public string[] SideNames { get; set; } = [];

    /// <summary>
    /// P397 — when set, this game's mission map is <b>generated procedurally</b>
    /// (<c>Allegiance.Core.MapGen.MissionMapGenerator</c>) instead of loaded from
    /// <see cref="Map"/> (which is then ignored). <c>null</c>/absent (the
    /// default) preserves today's exact file-load behaviour byte-for-byte — the
    /// generator is a pure addition, mirroring the opt-in <c>liveSettings</c>
    /// convention. See <see cref="MapGenConfig"/> for the tunables.
    /// </summary>
    public MapGenConfig? MapGen { get; set; }

    /// <summary>Mission treasure rates and loaded-core set IDs, with native defaults.</summary>
    public Allegiance.Core.Model.TreasureParameters Treasure { get; set; } = new();

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
    public bool AutoRestart { get; set; }

    /// <summary>
    /// P762 / SIM-0142 — native <c>MissionParams::bAutoStart</c>
    /// (<c>Igc/igc.h:1140</c>, default <c>false</c> at <c>igc.h:1256</c>):
    /// when the mission is still NotStarted and <c>FAllReady</c> holds, begin
    /// the start countdown automatically (FedSrv.CPP:8223-8226). The port's
    /// ready-up auto-start predicates on this; the default is <c>true</c>
    /// because that is the established dedicated-server behaviour this port
    /// shipped (P180/P188), and setting it false restores native's
    /// "only an admin starts" default without touching P660 AutoNextMatch.
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// P762 / SIM-0142 — native <c>MissionParams::fStartCountdown</c>
    /// (<c>Igc/igc.h:1146</c>, default <c>15.0f</c> at <c>igc.h:1262</c>):
    /// the countdown length <c>bAutoStart</c> schedules once <c>FAllReady</c>
    /// holds (FedSrv.CPP:8225 <c>StartCountdown(fStartCountdown)</c>). Absent →
    /// the mission briefing constant <c>c_fMissionBriefingCountdown = 15</c>,
    /// which is the same value.
    /// </summary>
    public double? StartCountdownSeconds { get; set; }

    /// <summary>
    /// P762 / SIM-0142 — native <c>MissionParams::bLockTeamSettings</c>
    /// (<c>Igc/igc.h:1138</c>, default <c>false</c> at <c>igc.h:1254</c>):
    /// while set, a side leader may not boot a teammate off the mission
    /// (FedSrv/FedSrv.CPP:6802; a moderator boot is unaffected). Absent → false,
    /// the previous port behaviour.
    /// </summary>
    public bool LockTeamSettings { get; set; }

    /// <summary>
    /// P820c — native <c>MissionParams::bSquadGame</c> (<c>Igc/igc.h:1142</c>,
    /// default <b>false</b> at <c>igc.h:1258</c>): whether this game is a squad
    /// game. A squad game gives each side a squad (derived from the side
    /// leader's preferred squad when the leader first joins an empty side),
    /// boots seated non-members when the squad changes (<c>SetSideSquad</c>,
    /// fsmission.cpp:1612-1641) and relays the squad to clients via
    /// <c>SET_TEAM_INFO(15)</c>. Non-squad games (the default) keep every side
    /// squad-less with no squad-related boots — byte-for-byte the pre-P820b
    /// behaviour. Mirrored onto <c>Mission.SquadGame</c> at assemble.
    /// </summary>
    public bool SquadGame { get; set; }

    /// <summary>
    /// P854 — private-game per-side invitation lists, the port's synchronous-config
    /// substitute for native <c>MissionParams::nInvitationListID</c> +
    /// the per-row (iTeam, szSubject) SQL invitation lists fed to
    /// <c>CFSMission::AddInvitation</c> (<c>FedSrv/fsmission.cpp:217-222</c>;
    /// <c>FedSrv/FedSrv.CPP:1307-1318,785-801</c>). Key = side/team index; value = the
    /// list of invited subjects for that side. A non-empty side list makes the game
    /// require an invitation (mirrors <c>nInvitationListID != 0</c> →
    /// <c>RequiresInvitation()</c>, fsmission.h:407-411). Each subject is a player
    /// CALLSIGN on a non-squad game and a SQUAD name on a squad game
    /// (<see cref="SquadGame"/>), matched case-insensitively exactly as native
    /// <c>CFSSide::IsInvited</c> (fsside.cpp:34-64). Empty/absent = a fully public
    /// game (the pre-P854 behaviour). Loaded onto <c>Mission</c> at assemble.
    /// </summary>
    public Dictionary<short, List<string>> SideInvitations { get; set; } = new();

    /// <summary>Permit countdown completion without connected players.
    /// The dedicated server defaults to arena policy; FedSrv.CPP:8237-8243.</summary>
    public bool AllowEmptyTeams { get; set; } = true;

    /// <summary>Minimum connected players on each side when empty teams are disabled.</summary>
    public int MinimumPlayersPerTeam { get; set; } = 1;

    /// <summary>Optional match duration in simulation seconds (fsmission.cpp:2339-2342).</summary>
    public double? GameLengthSeconds { get; set; }

    /// <summary>
    /// P799 — the native per-player life budget, <c>MissionParams::iLives</c>
    /// (<c>igc.h:1185</c>: "Player must die more than this number of times to
    /// exit"; a ship is a ghost while <c>m_nDeaths &gt; iLives</c>,
    /// <c>Igc/shipIGC.h:2465-2469</c>). Default
    /// <see cref="Allegiance.Core.Wire.MissionDef.UnlimitedLives"/> = 0x7fff —
    /// the native <c>MissionParams::Reset</c> default (<c>igc.h:1291</c>) and the
    /// value production missions have always sent (<c>c_cUnlimitedLives</c>,
    /// <c>igc.h:1007</c>), so absent from the config the behaviour is
    /// byte-for-byte the pre-P799 unlimited game. A finite value N grants each
    /// player N+1 launches' worth of deaths: the (N+1)-th death makes them a
    /// ghost (no buy/launch/board/promote, FedSrv.CPP:6156,6356,6388,6405,7050)
    /// and a side whose every connected player is a ghost is eliminated
    /// (FedSrv.CPP:12070 → fsmission.cpp DeactivateSide).
    /// </summary>
    public short Lives { get; set; } = Allegiance.Core.Wire.MissionDef.UnlimitedLives;

    /// <summary>
    /// P598 — the port of native's <c>fRestartCountdown</c> (igc.h:1171,
    /// default <c>60.0f</c> at igc.h:1274): the seconds, when
    /// <see cref="AutoRestart"/> is on, between a game-over rotation and the
    /// rebuilt mission's auto-restart start countdown. Absent → 60.0.
    /// </summary>
    public double? RestartCountdownSeconds { get; set; }
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
    public string Type { get; set; } = "";

    /// <summary>P811 — the fixed RNG seed for this game's generated map. When
    /// set, the map generator draws through a deterministic stream so the same
    /// seed rebuilds the exact same generated map (CLI <c>--map-seed N</c>
    /// overlays this). Absent → the historic shared-RNG (non-reproducible)
    /// behaviour.</summary>
    public int? Seed { get; set; }

    /// <summary>0 ⇒ one team cluster per team; non-zero ⇒ two (<c>iMapSize</c>).</summary>
    public int? MapSize { get; set; }

    public int? PlayerSectorMineableAsteroids { get; set; }
    public int? PlayerSectorSpecialAsteroids { get; set; }
    public int? PlayerSectorAsteroids { get; set; }
    public int? PlayerSectorTreasures { get; set; }
    public int? NeutralSectorMineableAsteroids { get; set; }
    public int? NeutralSectorSpecialAsteroids { get; set; }
    public int? NeutralSectorAsteroids { get; set; }
    public int? NeutralSectorTreasures { get; set; }

    public double? He3Density { get; set; }
    public bool? FlagsGame { get; set; }
    public bool? ArtifactsGame { get; set; }
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
    /// <summary>Opt-in private credential file; empty keeps an unverified directory listing.</summary>
    public string EnrollmentFile { get; set; } = "";

    /// <summary>Base URL of the lobby directory (e.g. <c>http://localhost:5000</c>).</summary>
    public string Url { get; set; } = "";

    /// <summary>Human-readable name shown in the directory listing.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Publicly reachable address advertised to players (hostname/IP, no scheme).</summary>
    public string PublicAddress { get; set; } = "";

    /// <summary>Seconds between heartbeats. Registry default expiry is 90s, so the
    /// default 25s tolerates ~3 missed beats before a live server flaps off the list.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 25;

    /// <summary>P761b — opt in to publishing this server's connected pilot names to
    /// the lobby directory, so a player's FIND_PLAYER query can locate the game
    /// they are in (native WinTrek/gamescreen.cpp:1348-1352 → FM_C_FIND_PLAYER).
    /// <b>Default <c>false</c></b>: nothing about the roster leaves the server
    /// unless its owner turns this on. Even when on, the names answer the
    /// per-name query only — the public <c>/api/servers</c> listing never carries
    /// them.</summary>
    public bool PublishPlayerNames { get; set; }

    /// <summary>P810 — the minimum client version this server requires. Empty
    /// (the default) = no requirement; otherwise the value rides the heartbeat
    /// as <c>minClientVersion</c> and the client's server list hides this
    /// server for players on older builds (see the design doc §3).</summary>
    public string? MinimumClientVersion { get; set; }
}

/// <summary>P660 dedicated-server rotation policy. Uses native bAutoRestart's
/// SET_START_TIME while NotStarted (fsmission.cpp:3539-3546), then StartCountdown.</summary>
public sealed class AutoNextMatchConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Simulation seconds, like the native restart and briefing timers.</summary>
    public double IntermissionSeconds { get; set; } = 30;
}

/// <summary>The dedicated live/testing settings section. An absent section
/// uses defaults: debug off, automatic next match on, automatic dilation on.</summary>
public sealed class LiveSettingsConfig
{
    /// <summary>P660: keep connected players seated and start the next match after
    /// an intermission. Enabled by default for this dedicated server. Disable to
    /// retain the ready-up flow. Legacy GameConfig.AutoRestart remains independent.</summary>
    public AutoNextMatchConfig AutoNextMatch { get; set; } = new();
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
        double.IsFinite(v) ? Math.Clamp(v, MinTimeScale, MaxTimeScale) : MaxTimeScale;

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
    /// authenticated connection whose name starts with <see cref="TestHarnessConfig.CharacterNamePrefix"/>
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
    /// <summary>P655: bearer presented in LOGONREQ.CDKey. Without a configured
    /// secret, grants require an actual loopback socket. Never trust names alone.</summary>
    public string? Secret { get; set; }

    /// <summary>Master switch. <c>false</c> (the default, and the meaning of an
    /// absent <c>testHarness</c> section) leaves behaviour byte-identical to
    /// P441 — no money grant, no tech-gate bypass, for anyone.</summary>
    public bool Enabled { get; set; }

    /// <summary>The character-name prefix that marks a connection as a test
    /// harness. A matching prefix is an eligibility filter; the configured
    /// secret or a verified loopback socket is also required. Defaults to mcp-.</summary>
    public string CharacterNamePrefix { get; set; } = "mcp-";

    /// <summary>The wallet granted to a harness connection on seat, in credits.
    /// Set ABSOLUTELY at seat (overriding the P443 starting money), so a fresh
    /// side's garrison economy can never price the harness out of a hull.</summary>
    public int Money { get; set; }

    private bool _ignoreTechGate = true;
    [JsonIgnore] internal bool IgnoreTechGateExplicit { get; private set; }

    /// <summary>When <c>true</c>, a harness connection's <c>BUY_LOADOUT</c>
    /// skips ONLY the tech gate (the station <c>CanBuy</c> hull/part check). It
    /// still pays the real price through the real debit path — so pricing keeps
    /// being exercised — and still respects the funds gate, the drone-hull
    /// (<c>GroupId&lt;0</c>) gate, and the hardpoint-compatibility gate.</summary>
    public bool IgnoreTechGate
    {
        get => _ignoreTechGate;
        set { _ignoreTechGate = value; IgnoreTechGateExplicit = true; }
    }
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

    private bool _allTech = true;
    [JsonIgnore] internal bool AllTechExplicit { get; private set; }

    /// <summary>When <c>true</c> (the default), every side is granted the full
    /// required-tech union of the core (all buyable tech bits). When <c>false</c>
    /// and <c>Enabled</c> is true, only the seat-money grant applies (no tech).</summary>
    public bool AllTech
    {
        get => _allTech;
        set { _allTech = value; AllTechExplicit = true; }
    }

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

    /// <summary>P655: bearer sent in LOGONREQ.CDKey, or as the first token of
    /// an admin command. Required for remote cheats and spectator access. With
    /// no secret, only server-verified loopback sockets are eligible.</summary>
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
/// <c>broadcast</c> section). Parallel building is OFF by default. P659's
/// independent <c>minUpdateHz</c> pose floor defaults to 2 Hz (0 restores the
/// pre-floor ranked budget). When parallel building is on, <c>BroadcastShipStates</c> splits into a serial
/// snapshot phase + a parallel per-peer build phase + a serial enqueue phase,
/// and <c>BroadcastShipsUpdate</c> parallelises across sectors (within-sector
/// peer order — and the per-ship Deviation staleness accumulator — stay
/// serial). See docs/reports/P603_PARALLEL_BROADCAST_BUILD.md.
/// </summary>
public sealed class BroadcastConfig
{
    /// <summary>Capture owned wire data on the sim thread and encode/publish it
    /// off-thread. Default ON since the 2026-10-27 rig A/B (b42 vs control b39:
    /// clash-peak tick 44 ms vs 71–80 ms, TiDi min 0.87 vs 0.48, same egress).
    /// ALLEGIANCE_BROADCAST_SNAPSHOT=0 turns it off.</summary>
    public bool ImmutableSnapshot { get; set; } = true;

    /// <summary>Share ship record encodings within a snapshot. Default ON (with
    /// ImmutableSnapshot, same rig A/B); requires ImmutableSnapshot.
    /// ALLEGIANCE_BROADCAST_ENCODE_ONCE=0 turns it off.</summary>
    public bool EncodeOnce { get; set; } = true;

    /// <summary>P677 — per-viewer interest rates. Opt-in; also enables the
    /// immutable snapshot and encode-once pipeline. ALLEGIANCE_BROADCAST_TIERS overrides.</summary>
    public bool InterestTiers { get; set; }
    public double Tier1Hz { get; set; } = 5;
    public double Tier2Hz { get; set; } = 2;
    /// <summary>World units. Minimum engagement radius; mounted gun reach can extend it.</summary>
    public float EngagementRange { get; set; } = 1500;
    /// <summary>Enemies outside this radius are T2; allies remain T1.</summary>
    public float MidDistanceRange { get; set; } = 6000;

    /// <summary>P813a — distance-tiered promotion (world units). A far (T1/T2)
    /// ship whose pose has displaced this far since its last served pose is
    /// "big-changed" and promoted to a full-rate send for that tick, so a
    /// manoeuvring distant ship is caught up within one tick of drifting past
    /// the threshold and the client's dead reckoner never extrapolates further
    /// than this distance. 0 (the value used before P813a) disables promotion —
    /// far ships ride only their tier cadence. Used only when
    /// <c>InterestTiers</c> is on; the flag-off stream is untouched.</summary>
    public float PromotionEpsilon { get; set; } = 200;

    /// <summary>P659 — minimum remote visible-ship pose frequency per viewer.
    /// Default 2 Hz; 0 preserves the existing ranked budget. Rounded up to a
    /// whole broadcast tick, capped by UpdatesPerSecond. LIGHT records carry
    /// no pose and do not satisfy this floor. Measured in wall time, not sim time.</summary>
    public double MinUpdateHz { get; set; } = 2;

    /// <summary>
    /// Parallel-build switch. <c>false</c> (the default) uses the serial builders.
    /// Serial and parallel modes apply the same independent pose floor.
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

    /// <summary>P654 (H-15) — hard ceiling on live client connections
    /// (greetings + established). Absent → 1024. Overridable by
    /// <c>ALLEGIANCE_TRANSPORT_MAX_CONNECTIONS</c>.</summary>
    public int? MaxConnections { get; set; }

    /// <summary>P654 (H-15) — ceiling on client connections from ONE source IP
    /// address. Absent → 64 (a sane public-server default). The 200v200 rig's
    /// ~390 bots all originate from one Mac bot host, so the README's
    /// recommended settings raise this (see README "Recommended settings").
    /// Overridable by <c>ALLEGIANCE_TRANSPORT_MAX_PER_IP</c>.</summary>
    public int? MaxConnectionsPerIp { get; set; }

    /// <summary>P654 (H-02/H-15) — ceiling on connections still in the greeting
    /// (pending-handshake) phase. Absent → 256. Overridable by
    /// <c>ALLEGIANCE_TRANSPORT_MAX_PENDING_HANDSHAKE</c>.</summary>
    public int? MaxPendingHandshake { get; set; }

    /// <summary>P654 (H-02/H-21) — the server's inbound DATA-phase frame cap in
    /// bytes (largest legitimate client message is ~1.1 KB, so 64 KiB is
    /// generous). A pre-auth peer can no longer claim a huge body up front.
    /// Absent → 65536. Overridable by
    /// <c>ALLEGIANCE_TRANSPORT_MAX_FRAME_BYTES</c>.</summary>
    public int? MaxFrameBytes { get; set; }

    /// <summary>
    /// P813d — EXPERIMENTAL, default OFF. When true, ordered-guaranteed
    /// one-shot messages (chat, kills, deaths, docks, ship deletes, game over)
    /// are routed into a dedicated per-peer <b>park lane</b> in the transport
    /// that drains strictly before ordinary ordered and stream traffic and is
    /// never shed or coalesced (see <c>TcpTransport.ReliableOneShot</c>). When
    /// false (the default), one-shots ride the ordinary ordered lane exactly as
    /// today — behaviour byte-for-byte unchanged. Decided by the 200v200 rig
    /// baseline; see docs/reports/P813D_RELIABLE_TIER_AUDIT.md. Absent → false.
    /// Overridable at boot by <c>ALLEGIANCE_TRANSPORT_RELIABLE_ONESHOT</c>
    /// ("1"/"true" enables).
    /// </summary>
    public bool? ReliableOneShot { get; set; }

    /// <summary>P654 (H-02) — read deadline (seconds) for a server-inbound frame
    /// once it has STARTED (first header byte seen): a header-then-stall or
    /// dribbling-body peer is dropped rather than parking a buffer + thread
    /// (slowloris protection). Absent → 30. Overridable by
    /// <c>ALLEGIANCE_TRANSPORT_DATA_FRAME_TIMEOUT_SECONDS</c>. Distinct from
    /// <see cref="DataLinkIdleTimeoutSeconds"/>, which bounds the silence
    /// BETWEEN frames (P671).</summary>
    public int? DataFrameReadTimeoutSeconds { get; set; }

    /// <summary>P671 — the IDLE timeout (seconds) for a server-inbound data
    /// session: a connected peer that sends NO bytes for this long is dropped
    /// as link-dead (native's 30 s g_sDeadTime, clintlib.cpp:2047). A healthy
    /// client never hits it because the shared client session keep-alive sends
    /// a CS_PING every 6 s (clintlib.cpp:2050-2087). Absent → 30. Overridable
    /// by <c>ALLEGIANCE_TRANSPORT_IDLE_TIMEOUT_SECONDS</c>. Before P671 the
    /// frame deadline doubled as this idle grace; the two are now separate.</summary>
    public int? DataLinkIdleTimeoutSeconds { get; set; }

    // ---- P654 (H-24) per-peer message/chat budget (native's chat budget) ----

    /// <summary>
    /// P762 / SIM-0145 — select the chat-budget constant profile:
    /// <c>"dedicated"</c> (the default; P654's era-adjusted 1000 / 20 / +5) or
    /// <c>"native"</c> (<c>c_chatBudgetMax = 100</c>, <c>c_chatBudgetCost = 20</c>,
    /// +1 per tick — <c>fsship.h:24-25</c>, <c>FedSrv.CPP:8287</c>). Explicit
    /// <see cref="ChatBudgetMax"/>/<see cref="ChatBudgetCost"/>/
    /// <see cref="ChatBudgetRefillPerTick"/> values override the selected
    /// profile. Overridable by <c>ALLEGIANCE_TRANSPORT_CHAT_BUDGET_PROFILE</c>.
    /// </summary>
    public string? ChatBudgetProfile { get; set; }

    /// <summary>P654 (H-24) — the per-peer chat/message token-bucket MAXIMUM.
    /// Port of native <c>c_chatBudgetMax = 100</c> (fsship.h:24), sized up from
    /// the modem era so today's busy commanders are not starved (era-constraint
    /// sizing per the project rule — native's constants are cited in the code).
    /// Absent → 1000. Overridable by <c>ALLEGIANCE_TRANSPORT_CHAT_BUDGET_MAX</c>.</summary>
    public int? ChatBudgetMax { get; set; }

    /// <summary>P654 (H-24) — the per-peer token cost of one chat / state-
    /// changing message. Port of native <c>c_chatBudgetCost = 20</c>
    /// (fsship.h:25), cited in code. Absent → 20. Overridable by
    /// <c>ALLEGIANCE_TRANSPORT_CHAT_BUDGET_COST</c>.</summary>
    public int? ChatBudgetCost { get; set; }

    /// <summary>P654 (H-24) — token refill per simulation tick. Native refills
    /// +1 per tick (FedSrv.CPP:8287, IncrementChatBudget), capped at the max;
    /// sized to +5 so today's sustained traffic (~100 msg/s) flows while a
    /// flood is still sharply bounded. Absent → 5. Overridable by
    /// <c>ALLEGIANCE_TRANSPORT_CHAT_BUDGET_REFILL</c>.</summary>
    public int? ChatBudgetRefillPerTick { get; set; }
}

/// <summary>Opt-in receive pipeline and attribution settings.</summary>
public sealed class ReceiveConfig
{
    /// <summary>Decode and validate wire data on transport receive workers. Default off.
    /// ALLEGIANCE_RECEIVE_DECOUPLE overrides this value at boot.</summary>
    public bool Decouple { get; set; }
    /// <summary>Emit per-tick fmid/peer counts, bytes and a 100-tick drain histogram.</summary>
    public bool Measure { get; set; }
}

/// <summary>
/// Top-level server configuration. Shape is decided (see task): a JSON file at
/// <c>--config</c> (default <c>server.json</c>), with scalar fields overridable
/// on the command line (<c>--port</c>, <c>--artpath</c>, <c>--staticcore</c>,
/// <c>--updatespersec</c>). Games are file-defined — there is no CLI form for
/// a nested array except the ordered civ list of one game (<c>--civs</c>).
/// </summary>
public sealed class ServerConfig
{
    public ServerIdentityConfig? Identity { get; set; }

    /// <summary>
    /// P762 / SIM-0142-adjacent moderation row — the port's stand-in for the
    /// native Steam moderator/developer group (<c>FedSrv.CPP:6751-6753</c>):
    /// each configured entry is a <b>bearer secret</b> whose holder may remove
    /// any player with a reason and message.
    /// <para><b>Authentication (P762b).</b> The port has no ASGS/Steam account
    /// service and does not validate <c>SteamAuthTicket</c>; the logon
    /// character name, CD key and Steam ID on the wire are all self-asserted.
    /// This list is therefore matched only against the secret presented in
    /// <c>LOGONREQ.CDKey</c> (fixed-time comparison), exactly like the P655
    /// admin-cheat/test-harness bearer secrets. Character names and Steam IDs
    /// are <b>never</b> matched — a spoofed name must never grant moderator
    /// rights. Use long random secrets, one per moderator, and distribute them
    /// out of band; without an authenticating front end a shared secret is the
    /// only available credential. Empty by default: no moderator privileges are
    /// granted unless an operator configures this list.</para>
    /// </summary>
    public string[] ModeratorAccounts { get; set; } = [];

    /// <summary>Optional local benchmark policy; normal games retain an unseeded RNG.</summary>
    public int? BenchmarkSeed { get; set; }
    public ShowcaseConfig Showcase { get; set; } = new();

    /// <summary>Resolve mode dependencies before binding sockets or changing global runtime state.
    /// Unlocked preserves existing settings, including P703's independently selected plans.</summary>
    public void ResolveShowcaseMode()
    {
        Showcase.Validate();
        if (Showcase.GoalRotation is { } rotation)
            foreach (var game in Games) rotation.Validate(game);
        if (Showcase.TechMode != "research") return;
        var live = LiveSettings ?? new LiveSettingsConfig();
        void Conflict(string key) => throw new InvalidDataException($"showcase.techMode=research conflicts with {key}");
        if (live.TestUnlock is { Enabled: true }) Conflict("liveSettings.testUnlock.enabled=true (wallet/tech grant)");
        if (live.TestUnlock is { AllTechExplicit: true, AllTech: true }) Conflict("liveSettings.testUnlock.allTech=true");
        if (live.TestHarness is { Enabled: true }) Conflict("liveSettings.testHarness.enabled=true (purchase grant)");
        if (live.TestHarness is { IgnoreTechGateExplicit: true, IgnoreTechGate: true }) Conflict("liveSettings.testHarness.ignoreTechGate=true");
        foreach (var game in Games)
            if (!game.AllowDevelopments) Conflict($"games[{game.Name}].allowDevelopments=false");
        LiveSettings = live;
        live.TestUnlock ??= new();
        live.TestUnlock.AllTech = false;
        live.TestHarness ??= new();
        live.TestHarness.IgnoreTechGate = false;
    }
    /// <summary>P629 simulation rules, including the stress-test crowding override.</summary>
    public SimConfig Sim { get; set; } = new();
    public ReceiveConfig? Receive { get; set; }
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

    /// <summary>Optional numeric listener address; null keeps dedicated hosting on all interfaces.</summary>
    public string? BindAddress { get; set; }

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
    public LobbyConfig? Lobby { get; set; }

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
    /// P763 — how long (simulation seconds) a logged-on peer may sit at the
    /// side-select screen before the server auto-assigns it a side (NA =
    /// auto-pick). This is a <b>port adaptation</b>, not native behaviour:
    /// native's team screen sends <c>C POSITIONREQ</c> when the player picks a
    /// side (<c>WinTrek/teamscreen.cpp:2964-2981</c>, <c>Inc/Messages.h:320</c>)
    /// and the server only seats that request (<c>FedSrv.CPP:6658-6691</c> →
    /// <c>fsmission.cpp:4729-4774</c>) — there is no timer that seats a silent
    /// client. <c>null</c> or any value &lt;= 0 (the default) means OFF: only
    /// peers with a recorded <c>POSITIONREQ</c> choice are drained, matching
    /// native. Set it to 30 to restore the pre-P763 port behaviour (e.g. a rig
    /// with AFK bots); the auto-seat is then opt-in only.
    /// </summary>
    public double? SideSelectAutoSeatSeconds { get; set; }

    /// <summary>
    /// P378 — the dedicated live/testing settings section (see
    /// <see cref="LiveSettingsConfig"/>). Absent section uses defaults:
    /// debug off, no lifepod override, automatic next match and dilation on.
    /// </summary>
    public LiveSettingsConfig LiveSettings { get; set; } = new();

    /// <summary>The game worlds to instantiate at boot.</summary>
    public List<GameConfig> Games { get; set; } = [];

    /// <summary>
    /// P600 — the per-tick profiler settings. Null/absent (the default) = the
    /// profiler is OFF, with no hot-path overhead. See <see cref="ProfilerConfig"/>.
    /// </summary>
    public ProfilerConfig? Profiler { get; set; }

    /// <summary>
    /// Per-peer broadcast settings. Null/absent uses serial building, the port
    /// ship-update model and P659's 2 Hz pose floor. See <see cref="BroadcastConfig"/>.
    /// </summary>
    public BroadcastConfig? Broadcast { get; set; }

    /// <summary>
    /// P605 — the server transport's send-draining mode. Null/absent (the
    /// default) = thread-per-peer, today's behaviour (unchanged). See
    /// <see cref="TransportConfig"/>.
    /// </summary>
    public TransportConfig? Transport { get; set; }

    /// <summary>
    /// P731 — the UDP discovery responder. Null/absent (the default) = enabled
    /// on the game port + 1 with the default rate limit, i.e. today's installs
    /// gain LAN/"Find Server" discoverability with no config change. See
    /// <see cref="DiscoveryConfig"/> for the fields and the exact resolution
    /// rules.
    /// </summary>
    public DiscoveryConfig? Discovery { get; set; }

    /// <summary>
    /// P813c (EXPERIMENTAL) — reconnect grace. An absent section (the default)
    /// coalesces to <c>Enabled = false</c>: a drop retires the ship immediately,
    /// exactly as before. See <see cref="ReconnectConfig"/>.
    /// </summary>
    public ReconnectConfig Reconnect { get; set; } = new();

    /// P810 — the opt-in server self-update (the modernised native
    /// <c>AllSrvUpdate</c> role, <c>AutoUpdate/AutoUpdate.cpp:325-356</c>).
    /// Null/absent (the default) = fully opt-out: no feed is ever contacted and
    /// the updater never runs. When enabled the server stages a newer release
    /// ONLY at game over — the native-like quiet point, after the finished
    /// mission is rotated and every GAME_OVER packet is on the wire — and the
    /// staged build is applied by <see cref="Allegiance.Update.UpdateApplier"/>
    /// at the next process start. Docker deployments should leave this off and
    /// keep pulling images (docker/README.md).
    /// </summary>
    public UpdatesConfig? Updates { get; set; }
}

/// <summary>
/// P813c (EXPERIMENTAL) — reconnect grace. Native ends a player's session the
/// moment their connection dies: the dropped connection's <c>CFSPlayer</c> is
/// destroyed (<c>FedSrv/FedSrv.CPP:4549-4566</c> <c>OnDestroyConnection</c>),
/// which runs <c>RemovePlayerFromMission(QSR_LinkDead)</c>
/// (<c>FedSrv/fsship.cpp:995</c>) and deletes the ship
/// (<c>FedSrv/fsship.cpp:58-68</c>). That is right for the LAN/Zone hosting
/// native was built for, but a short WAN drop (or an NAT/relay hiccup, P809)
/// throws the player out of a battle.
/// <para>When <see cref="Enabled"/> is true the server instead <b>parks</b> an
/// unrequested drop's ship for <see cref="GraceSeconds"/> simulation seconds
/// and lets the same pilot resume it with a session-scoped resume token (the
/// native <c>LOGONREQ.dwCookie</c> field, <c>Inc/Messages.h:43</c>). The window
/// expiring runs exactly the native removal above, so nothing is lost by
/// leaving it off — and <b>off is the default</b>: with the flag off every
/// disconnect path is byte-for-byte the pre-P813c behaviour. A voluntary
/// <c>LOGOFF</c> is never held (native deletes the player immediately there
/// too, <c>FedSrv/FedSrv.CPP:5801</c>). See
/// <c>docs/reports/P813C_RECONNECT_GRACE.md</c>.</para>
/// </summary>
public sealed class ReconnectConfig
{
    /// <summary>Whether an unrequested drop parks the ship (default
    /// <c>false</c> = native immediate removal, today's behaviour). CLI
    /// <c>--reconnect=true|false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long a parked ship is held, in simulation seconds
    /// (default 5). The expiry runs the native drop path. CLI
    /// <c>--reconnectGrace=N</c>.</summary>
    public double GraceSeconds { get; set; } = 5;

    public void Validate()
    {
        if (!double.IsFinite(GraceSeconds) || GraceSeconds < 0.25 || GraceSeconds > 300)
            throw new InvalidDataException("reconnect.graceSeconds must be finite and between 0.25 and 300");
    }
}

/// <summary>P810 — the server self-update settings (<see cref="ServerConfig.Updates"/>).
/// All three fields are required together when enabled (validated at boot).</summary>
public sealed class UpdatesConfig
{
    /// <summary>Master switch. <b>Default <c>false</c></b> — the self-update is
    /// strictly opt-in, and Docker users should keep it off (image pulls own the
    /// binary there).</summary>
    public bool Enabled { get; set; }

    /// <summary>The signed release feed root (its <c>latest.json</c> is read first).</summary>
    public string FeedUrl { get; set; } = "";

    /// <summary>SubjectPublicKeyInfo PEM of the ECDSA P-256 feed signing key.
    /// Supply via a mounted file / environment expansion — never inline a
    /// private key (the publish guard scans for secrets).</summary>
    public string PublicKeyPem { get; set; } = "";

    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(FeedUrl))
            throw new InvalidDataException("updates.enabled requires updates.feedUrl");
        if (string.IsNullOrWhiteSpace(PublicKeyPem))
            throw new InvalidDataException("updates.enabled requires updates.publicKeyPem");
    }
}

/// <summary>
/// P731 — the UDP discovery responder's settings. Absent section (or a null
/// property) falls back to: <see cref="Enabled"/> true, port = game port + 1,
/// and the default per-second request rate limit. Follows the P686 guarded-
/// JSON-defaults convention: an absent field keeps its declared default and is
/// validated by <see cref="Validate"/> exactly like the other sections.
/// </summary>
public sealed class DiscoveryConfig
{
    /// <summary>Whether the responder binds at all. False silences discovery
    /// entirely (no socket, no replies) — the documented opt-out for server
    /// hosts that must not advertise. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The UDP port to bind. Null (the default) = the game port + 1
    /// (e.g. 6074 for a 6073 game), resolved by the responder at boot so it
    /// always tracks the configured game port. An explicit value pins it.</summary>
    public int? Port { get; set; }

    /// <summary>Max request datagrams answered per wall-clock second. Protects
    /// the responder from being a reflection amplifier for a spoofed broadcast
    /// storm; replies are tiny and stateless. Default 20.</summary>
    public int RateLimitPerSecond { get; set; } = 20;

    /// <summary>IPv4 multicast group the responder joins so multicast "Find
    /// Server" queries reach it. Empty/null = do not join multicast (broadcast
    /// + unicast only). Default <c>239.255.7.31</c>.</summary>
    public string MulticastGroup { get; set; } = "239.255.7.31";

    public void Validate()
    {
        if (Port is { } p && (p < 1 || p > 65535))
            throw new InvalidDataException("discovery.port must be between 1 and 65535");
        if (RateLimitPerSecond < 0 || RateLimitPerSecond > 100000)
            throw new InvalidDataException("discovery.rateLimitPerSecond must be between 0 and 100000");
    }
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

        ApplyGoalEnvironment(config, Environment.GetEnvironmentVariable("ALLEGIANCE_GAME_GOALS"));
        ApplyMissionOptionEnvironment(config,
            Environment.GetEnvironmentVariable("ALLEGIANCE_GAME_ALLOW_FRIENDLY_FIRE"),
            Environment.GetEnvironmentVariable("ALLEGIANCE_GAME_ALLOW_ALLIED_VIZ"));
        ApplyCli(config, cli);
        foreach (var game in config.Games) _ = game.ResolveGoals();
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

    /// <summary>Environment value supplied explicitly so precedence can be tested
    /// without changing process-global environment during parallel tests.</summary>
    public static void ApplyGoalEnvironment(ServerConfig config, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) ApplyCli(config, [("goals", value)]);
    }

    /// <summary>P856 — the two native boolean mission options' environment
    /// overrides (<c>ALLEGIANCE_GAME_ALLOW_FRIENDLY_FIRE</c>,
    /// <c>ALLEGIANCE_GAME_ALLOW_ALLIED_VIZ</c>). Explicit parameters for the same
    /// reason as <see cref="ApplyGoalEnvironment"/>: a test can drive precedence
    /// without touching process-global state.</summary>
    public static void ApplyMissionOptionEnvironment(ServerConfig config, string? friendlyFire, string? alliedViz)
    {
        var cli = new List<(string Key, string Value)>(2);
        if (!string.IsNullOrWhiteSpace(friendlyFire)) cli.Add(("friendly-fire", friendlyFire));
        if (!string.IsNullOrWhiteSpace(alliedViz)) cli.Add(("allied-viz", alliedViz));
        if (cli.Count > 0) ApplyCli(config, cli);
    }

    /// <summary>P856 — the switch grammar the boolean mission options accept: an
    /// explicit <c>true</c>/<c>false</c> (like the other boolean CLI keys), or the
    /// container-friendly <c>1</c>/<c>0</c>.</summary>
    private static bool TryParseOptionSwitch(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on": result = true; return true;
            case "0": case "false": case "no": case "off": result = false; return true;
            default: result = false; return false;
        }
    }

    /// <summary>Return value: number of CLI keys actually recognized.</summary>
    public static int ApplyCli(ServerConfig config, IReadOnlyList<(string Key, string Value)> cli)
    {
        int recognized = 0;
        foreach (var (key, value) in cli)
        {
            switch (key.ToLowerInvariant())
            {
                case "identity-issuer":
                case "identityissuer":
                    if (config.Identity is null) throw new InvalidDataException("--identity-issuer requires an identity config block");
                    config.Identity.Issuer = value; recognized++; break;
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
                case "goal":
                    var preset = Allegiance.Core.Model.MissionGoals.Preset(value);
                    if (config.Games.Count == 0) throw new InvalidDataException("--goal requires a configured game");
                    foreach (var game in config.Games) { game.Goals = preset with { }; game.GameLengthSeconds = null; }
                    recognized++; break;
                case "goals":
                    Allegiance.Core.Model.MissionGoals parsedGoals;
                    try
                    {
                        parsedGoals = JsonSerializer.Deserialize(value, ServerJsonContext.Default.MissionGoals)
                            ?? throw new InvalidDataException("--goals must be a JSON object");
                        parsedGoals.Validate("--goals");
                    }
                    catch (JsonException ex) { throw new InvalidDataException($"--goals: {ex.Message}", ex); }
                    if (config.Games.Count == 0) throw new InvalidDataException("--goals requires a configured game");
                    foreach (var game in config.Games) { game.Goals = parsedGoals with { }; game.GameLengthSeconds = null; }
                    recognized++; break;
                case "friendly-fire":
                case "friendlyfire":
                case "allowfriendlyfire":
                    // P856 — native bAllowFriendlyFire (igc.h:1149, default off).
                    if (!TryParseOptionSwitch(value, out var friendlyFire))
                        throw new InvalidDataException($"--friendly-fire must be 0/1 or true/false, got '{value}'");
                    if (config.Games.Count == 0) throw new InvalidDataException("--friendly-fire requires a configured game");
                    foreach (var game in config.Games) game.AllowFriendlyFire = friendlyFire;
                    recognized++; break;
                case "allied-viz":
                case "alliedviz":
                case "allowalliedviz":
                    // P856 — native bAllowAlliedViz (igc.h:1127, default off).
                    if (!TryParseOptionSwitch(value, out var alliedViz))
                        throw new InvalidDataException($"--allied-viz must be 0/1 or true/false, got '{value}'");
                    if (config.Games.Count == 0) throw new InvalidDataException("--allied-viz requires a configured game");
                    foreach (var game in config.Games) game.AllowAlliedViz = alliedViz;
                    recognized++; break;
                case "civs":
                    var ids = value.Split(',');
                    if (config.Games.Count != 1 || ids.Length != config.Games[0].Teams
                        || ids.Any(id => !int.TryParse(id, out var n) || n < -1 || n > short.MaxValue))
                        throw new InvalidDataException("--civs requires one comma-separated civilization ID per team (-1 = random) and one configured game");
                    config.Games[0].CivIds = ids.Select(int.Parse).ToArray();
                    recognized++; break;
                case "civseed":
                case "civ-seed":
                    if (!int.TryParse(value, out var civSeed))
                        throw new InvalidDataException("--civ-seed requires an integer");
                    foreach (var game in config.Games) game.CivSeed = civSeed;
                    recognized++; break;
                case "mapseed":
                case "map-seed":
                    // P811 — pin the generated map's RNG seed. Applies to every
                    // game configured with a procedural mapGen; a config that
                    // only loads file maps cannot be made reproducible this way.
                    if (!int.TryParse(value, out var mapSeed))
                        throw new InvalidDataException($"--map-seed must be an integer, got '{value}'");
                    if (config.Games.All(g => g.MapGen is null))
                        throw new InvalidDataException("--map-seed requires a game configured with a generated map (a \"mapGen\" block with a topology type), not a file map");
                    foreach (var game in config.Games)
                        if (game.MapGen is { } mg) mg.Seed = mapSeed;
                    recognized++; break;
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
                case "reconnect":
                    // P813c — the experimental reconnect grace switch (default off).
                    config.Reconnect ??= new ReconnectConfig();
                    if (bool.TryParse(value, out var reconnect))
                        config.Reconnect.Enabled = reconnect;
                    else
                        config.Reconnect.Enabled = true;   // bare --reconnect
                    recognized++; break;
                case "reconnectgrace":
                case "reconnectgraceseconds":
                    // P813c — the hold window in simulation seconds; implies enabled.
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var grace))
                        throw new InvalidDataException($"--reconnectGrace must be a number of seconds, got '{value}'");
                    config.Reconnect ??= new ReconnectConfig();
                    config.Reconnect.Enabled = true;
                    config.Reconnect.GraceSeconds = grace;
                    recognized++; break;
            }
        }
        return recognized;
    }
}
