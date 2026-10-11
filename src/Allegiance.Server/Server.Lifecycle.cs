using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
// Disambiguate the LoadoutChange KIND enum (Sim) from the Allegiance.Core.Wire.LoadoutChange
// codec — both are in scope here; the enum is the natural meaning in the
// gameplay broadcast handler, the wire codec is reached as Allegiance.Core.Wire.LoadoutChange.
using LoadoutChange = Allegiance.Core.Sim.LoadoutChange;

namespace Allegiance.Server;

/// <summary>
/// Boot, tick loop, shutdown and connection-lifecycle members of <see cref="Server"/>
/// (P327 stage-1 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>
/// type, so private field/method access across the split files is unaffected.
/// </summary>
public sealed partial class Server
{
    // P324 (M4) — a side whose seated-peer set changed (a peer joined) must
    // receive an immediate per-side object sync rather than waiting for the
    // every-10 cadence tick. Populated by the seating path (TryJoinSide).
    private void MarkObjectSyncDirty(short sideId) => _objectSyncDirtySides.Add(sideId);

    /// <summary>A peer-owned ship: the <see cref="Ship"/>, the
    /// <see cref="Sector"/> it lives in, its last-broadcast position
    /// (the dirtiness watermark for the SINGLE_SHIP_UPDATE broadcast), and
    /// (P193) the peer's in-memory, connection-lifetime-only
    /// <see cref="PlayerScore"/>. A fresh <see cref="PlayerScore"/> is
    /// allocated when the <c>LiveShip</c> is created at logon and discarded
    /// with it on disconnect — the record's lifecycle <em>is</em> the
    /// PlayerScore's lifetime, so non-persistence is structural rather than an
    /// extra bookkeeping rule.</summary>
    /// <summary><c>LastBroadcastForward</c>/<c>LastBroadcastUp</c>: the
    /// orientation last echoed to this ship's own owning peer (and to same-side
    /// recipients via the teammate broadcast below), so a pure rotation — no
    /// position change at all, e.g. roll/pitch/yaw with no thrust — still
    /// crosses the dirty check and gets broadcast. Forward alone misses roll
    /// (rotation about the ship's own forward axis leaves forward unchanged),
    /// so both are tracked; together they fully determine the 3x3 orientation.</summary>
    internal sealed record LiveShip(Ship Ship, Sector Sector, Vector3 LastBroadcastPos, Vector3 LastBroadcastForward, Vector3 LastBroadcastUp, PlayerScore PlayerScore, PlayerWallet Wallet, float LastBroadcastShieldFraction, bool LastBroadcastMining, bool PositionEverBroadcast = false, double ConnectSimTime = 0);

    /// <summary>
    /// Load the config file, then <see cref="Boot(ServerConfig)"/>.
    /// </summary>
    public void Boot(string configPath, IReadOnlyList<(string Key, string Value)> cliOverrides) =>
        Boot(ServerConfigLoader.Load(configPath, cliOverrides));

    /// <summary>
    /// Boot in the strict order dictated by the reference engine (MISSION_INIT.md
    /// §4): static core first, then sides / sectors, then the tick. Stubbed step
    /// 5 logs that cores.txt/ranks.txt/maps.txt are intentionally not loaded.
    /// </summary>
    public void Boot(ServerConfig config)
    {
        _config = config;
        _period = _config.UpdatesPerSecond > 0 ? 1.0 / _config.UpdatesPerSecond : 0.05;
        _simNow = 0.0;
        ConsoleLog.Verbose = _config.ConsoleVerbose;   // P318: gate the high-frequency per-tick lines

        // P594 — the P591b/P591c warp re-entry guard is a NON-NATIVE QoL safety
        // net, OPT-IN and OFF by default. Only arm it (and banner LOUDLY) when
        // the operator explicitly opts in via the config flag / CLI switch; a
        // default config (flag absent or false) leaves it off, matching native.
        bool? warpReEntryGuard = _config.WarpReEntryGuard;
        Sector.WarpReEntryGuardEnabled = warpReEntryGuard ?? false;
        if (warpReEntryGuard == true)
            Console.WriteLine("[boot] *** NON-NATIVE WARP RE-ENTRY GUARD ARMED (warpReEntryGuard=true) — P591b/QoL safety net; " +
                              "NOT native behaviour (native relies on its GotoPlan exit push). Off by default ***");

        // P378 — resolve the live-settings lifepod endurance override and log
        // the debug-mode state LOUDLY at boot so it is never silently left on.
        // Coalesce a null section (absent `liveSettings` key — STJ leaves an
        // absent init property null) to an all-defaults section.
        var live = _config.LiveSettings ?? new LiveSettingsConfig();
        _lifepodEnduranceOverrideSeconds = ResolveLifepodEnduranceOverride(live);
        if (live.DebugMode)
            Console.WriteLine("[boot] *** DEBUG MODE ACTIVE (liveSettings.debugMode=true) — lifepod oxygen endurance " +
                              $"= {_lifepodEnduranceOverrideSeconds:0.##}s (30-min testing window; explicit liveSettings.lifepodEnduranceSeconds wins) ***");
        else if (_lifepodEnduranceOverrideSeconds is { } ovr)
            Console.WriteLine($"[boot] liveSettings.lifepodEnduranceSeconds = {ovr:0.##}s — lifepod oxygen endurance overridden (debug mode off)");

        // P473 — a test-harness economy grant is a port extension (native has no
        // money/tech cheat) that must never be silently on: banner it LOUDLY at
        // boot so it is visible in the very first lines of a live-test server.
        if (live.TestHarness is { Enabled: true } th)
            Console.WriteLine("[boot] *** TEST-HARNESS ECONOMY GRANT ACTIVE (liveSettings.testHarness.enabled=true) *** "
                              + $"prefix '{th.CharacterNamePrefix}' money={th.Money} ignoreTechGate={th.IgnoreTechGate} "
                              + "— applies ONLY to matching char names; real players NEVER affected ***");

        // P509 — the test-unlock grant is a port extension (native has no
        // "unlock all", see TestUnlockConfig) that must never be silently on:
        // banner it LOUDLY at boot. States the buyability-only scope explicitly
        // so nobody mistakes it for a stat-modifying cheat.
        if (live.TestUnlock is { Enabled: true } tu)
            Console.WriteLine("[boot] *** TEST UNLOCK ACTIVE (liveSettings.testUnlock.enabled=true) *** "
                              + $"allTech={tu.AllTech} money={tu.Money} — every side gets the required-tech union + every wallet gets money at seat; "
                              + "buyability ONLY (no development completed, flight/combat stats UNMODIFIED; P470 drone gate + P488 IsObsolete still apply) ***");

        // P554 — the privileged admin-cheat hook is a port extension (native has
        // no teleport/relocate cheat; DoCheatCode's tokens are FedSrv.CPP:2822-
        // 3022) that must never be silently on: banner it LOUDLY at boot. A
        // default config (no adminCheats section) prints nothing and no chat
        // line is ever interpreted as a cheat. P602 — because this server always
        // binds 0.0.0.0 (all interfaces, never loopback-only), enabling admin
        // cheats also appends the loud never-enable-on-a-public-server warning.
        var adminCheatWarning = Server.AdminCheatBootWarning(live.AdminCheats, boundToLoopbackOnly: false);
        if (adminCheatWarning is not null)
            Console.WriteLine(adminCheatWarning);

        // P600 — the per-tick profiler. OFF by default. A `profiler` config
        // section (or the ALLEGIANCE_TICK_PROFILER env var) turns it on; banner
        // it loudly so a measurement run is never mistaken for an uninstrumented
        // one. The env var is a convenience so a live run can profile without
        // editing a config file: "1"/"true" enables (log-only), "0"/"false"
        // forces off, and any other value is treated as the CSV output path.
        ApplyProfilerConfig(config.Profiler);
        if (TickProfiler.Instance is { } profiler)
            Console.WriteLine("[boot] *** TICK PROFILER ACTIVE (profiler.enabled=true) — per-subsystem + per-sector per-tick timing, "
                              + $"report every {profiler.ReportIntervalSeconds}s"
                              + (profiler.CsvPath is { Length: > 0 } ? $" → CSV {profiler.CsvPath}" : "")
                              + "; overhead negligible when off ***");

        // P603 — resolve the parallel per-peer broadcast build from config +
        // the ALLEGIANCE_BROADCAST_PARALLEL_BUILD env var (env wins), clamp the
        // degree of parallelism, and PRINT the active value so every server run
        // is labelled (and a measurement run is never mistaken for an
        // uninstrumented one). Default OFF = today's serial byte-for-byte path.
        ResolveBroadcastParallelBuild();

        // P605 — resolve the transport's send-draining mode (thread-per-peer
        // default, pool optional) from transport.mode config + the
        // ALLEGIANCE_TRANSPORT_MODE env var, print it at boot, and stamp it
        // into the profiler report header.
        ResolveTransportMode();

        ValidatePaths();
        var core = LoadCore();                       // checklist item 4
        _core = core;                                // P439: retain for game-over mission rebuild
        StubFileLists();                             // checklist item 5 (stub — cores/maps; ranks now real, see below)
        _rankTable = LoadRankTable();                // P130: real rank list from <artPath>/ranks.txt
        BindHostAndTransport();                      // checklist items 3 & 6
        OpenCaptureIfEnabled();                      // optional --capture (off by default)
        var missions = InstantiateGames(core);       // checklist item 8
        _missions = missions;
        // P382 — spawn each side's initial miner drones (docked at home) once
        // _missions is assigned: SpawnNpcShip's ship-id allocator walks
        // _missions (AllocateShipId → IsShipIdLive), which is still null while
        // InstantiateGames is building the batch. NOT gated on debugMode —
        // native spawns initial miners for every mission (fsmission.cpp:2196-
        // 2217). <see cref="SpawnInitialMinersAtBoot"/> is a test-facing opt-out
        // (default ON — real games unaffected): narrowly-scoped live fixtures
        // that spawn their own NPCs into a pristine arena turn it off so a
        // wandering initial miner doesn't perturb their precise assertions.
        // Debug mode's synthetic Omega/Plato garrisons already exist
        // (ApplyDebugTestTopology ran during InstantiateGames), so
        // SpawnInitialMiners' debug-aware home resolution finds them.
        if (SpawnInitialMinersAtBoot)
            foreach (var mission in missions)
                SpawnInitialMiners(mission);
        // P387 — in debug mode, auto-start every mission at boot so the sim
        // runs immediately with NO human player required. The P180/P188
        // ready-up hold leaves a fresh mission NotStarted, and Mission.Update
        // early-returns on NotStarted (Mission.cs:562-566) — the whole sim is
        // frozen. The mission only leaves NotStarted via TryAutoStart, which
        // is called solely from RecomputeSideReady (a player joining/readying,
        // Server.Lobby.cs:324) — so a boot with no player leaves P379/P381/
        // P382's docked-at-boot NPC miners auto-undocked (RunNpcAutoLaunch is
        // NOT stage-gated) but never ticking: no DecideMiningOrSelling, no
        // first warp, for however long the server sits playerless (observed
        // ~26 min, ending only when a peer connected and started the mission,
        // which unblocked BOTH sides' ships ~1 min later). Debug mode's whole
        // purpose is unattended autonomous test runs (P379 synthetic garrisons,
        // P382 spawned-at-boot miners, P381 auto-launch), so start the mission
        // at boot. TryAutoStart passes vacuously with no occupied sides
        // (IsAllOccupiedSidesReady finds no unready occupied side to veto), so
        // the 15 s briefing countdown begins immediately; the sim ticks during
        // Starting too, so the miners decide right away. Production (debugMode
        // off) behavior is untouched — it still awaits player ready-up.
        if (live.DebugMode)
        {
            Console.WriteLine("[boot] DEBUG MODE ACTIVE — auto-starting mission(s) at boot (no player required) so the autonomous NPC miners decide and warp immediately");
            foreach (var mission in missions)
                TryAutoStart(mission);
        }
        StartLobbyHeartbeat(core);                   // opt-in; no-op when config.Lobby is null
        PrintReady();
    }

    /// <summary>
    /// Construct the lobby heartbeat client when config.Lobby is set (fully opt-in),
    /// storing it for teardown in <see cref="Shutdown"/>. The payload's
    /// MissionName/CoreFile/Version are resolved once here:
    /// <list type="bullet">
    ///   <item><b>MissionName</b> — the FIRST configured game's <c>Name</c> (empty string
    ///        when <c>Games</c> is empty). The Lobby's RegisterRequest carries one singular
    ///        MissionName/CoreFile pair, but this port supports multiple concurrent missions
    ///        per process (<c>ServerConfig.Games</c> is a list) — so only the first is
    ///        reported, and the live PlayerCount aggregates across all missions via
    ///        <see cref="LivePlayerCount"/>. Documented simplification.</item>
    ///   <item><b>CoreFile</b> — the server-wide <c>StaticCore</c> (same for every mission).</item>
    ///   <item><b>Version</b> — the static core's version word.</item>
    /// </list>
    /// There is no MaxPlayers/player-cap concept in ServerConfig today, so the client
    /// sends 0 (the directory treats it as unknown/unbounded); no config field is
    /// invented for it here.
    /// </summary>
    private void StartLobbyHeartbeat(StaticCore core)
    {
        if (_config.Lobby is null)
            return;

        var firstGame = _config.Games.FirstOrDefault();
        _heartbeat = new LobbyHeartbeat(
            _config.Lobby,
            () => LivePlayerCount,
            firstGame?.Name ?? "",
            _config.StaticCore,
            core.Version.ToString(),
            _config.Port);
        Console.WriteLine($"[lobby] heartbeat enabled -> {_config.Lobby.Url}, every {_config.Lobby.HeartbeatIntervalSeconds}s");
    }

    /// <summary>
    /// P378 — resolve the configured lifepod oxygen-endurance override. The
    /// precedence rule (Domingo's explicit design):
    /// <list type="bullet">
    /// <item>an explicit <c>liveSettings.lifepodEnduranceSeconds</c> ALWAYS wins
    ///      (it is a general-purpose tunable, honoured regardless of debugMode);</item>
    /// <item>otherwise, <c>liveSettings.debugMode=true</c> implies a generous
    ///      testing default (<see cref="LiveSettingsConfig.DebugDefaultLifepodEnduranceSeconds"/>,
    ///      1800 s = 30 min);</item>
    /// <item>otherwise <c>null</c> — no override, so Ship falls back to the
    ///      core's native 300 s exactly as before P378 (byte-for-byte unchanged
    ///      for a normal server run with no config change).</item>
    /// </list>
    /// </summary>
    private static double? ResolveLifepodEnduranceOverride(LiveSettingsConfig? live)
    {
        // Defensive: STJ does not run the `= new()` initializer when the
        // `liveSettings` section is ABSENT (an absent init property stays null),
        // so treat null exactly like an all-defaults section — no override.
        if (live is null)
            return null;
        if (live.LifepodEnduranceSeconds is { } explicitValue)
            return explicitValue;
        if (live.DebugMode)
            return LiveSettingsConfig.DebugDefaultLifepodEnduranceSeconds;
        return null;
    }

    /// <summary>Synth garrison ids — deliberately in the 90000s (wrapping past
    /// short.MaxValue is fine: station ids are only ever compared against other
    /// station ids, and a negative short round-trips exactly through the wire),
    /// so they stay clear of the real corpus ids (4328/4329) and of
    /// <see cref="Mission.GenerateNewStationID"/>'s internal counter (starts at
    /// 9187 and only ever emits small positives), which is also internal to
    /// Allegiance.Core and not callable from Allegiance.Server anyway.</summary>
    private const short OmegaDebugStationId = unchecked((short)90001);
    private const short PlatoDebugStationId = unchecked((short)90002);

    /// <summary>P393 — the two synthetic P379 garrison station ids (side 0 = Omega,
    /// side 1 = Plato). Defined once here and shared by every debug-garrison search
    /// (<see cref="FindDebugGarrisonStation"/>), so the "is this the debug garrison"
    /// match rule is never written twice.</summary>
    private static readonly HashSet<short> _debugGarrisonStationIds =
        [OmegaDebugStationId, PlatoDebugStationId];

    /// <summary>P402 — did <paramref name="mission"/>'s map come from
    /// <c>g.MapGen</c> (procedurally generated) rather than a file? Backs every
    /// debug-station resolver's decision to use the generic path instead of the
    /// P379 synthetic-garrison preference (which only matters on the real
    /// m2xspiderw map, where leftover Ganymede/Organia creates ambiguity).</summary>
    private bool MissionCameFromMapGen(Mission mission) => _mapGenMissions.Contains(mission);

    /// <summary>P393 — the synthetic debug garrison <c>DataStationIGC</c> that a
    /// <see cref="ApplyDebugTestTopology"/> mission attaches for
    /// <paramref name="sideId"/>, if <paramref name="sector"/> hosts it. ONE shared
    /// matching rule backs every caller that wants a debug-garrison-first preference
    /// (P390's <see cref="ResolveSideHomeSector"/> for the join path, P393's
    /// <see cref="FindFriendlyStation"/> for the death/lifepod-respawn + rescue +
    /// briefing paths): a station whose runtime <see cref="DataStationIGC.SideId"/>
    /// equals the side AND whose id is one of <see cref="_debugGarrisonStationIds"/>.
    /// Matching by the station's own SideId — not a hardcoded side→id map — holds for
    /// any arena side count, exactly as P390 established. Returns <c>null</c> when no
    /// such garrison exists here (a normal game, or a sector that is not that side's
    /// arena home), so callers fall back to real-station resolution — which is
    /// byte-for-byte the pre-P393 behavior, since a production mission never attaches
    /// a synthetic garrison.
    /// <para>P402 — <paramref name="fromMapGen"/>: on a MapGen-sourced mission no
    /// synthetic garrison exists at all (the generator placed the real home
    /// stations), so the hardcoded-id preference is meaningless — return <c>null</c>
    /// and let the caller fall through to the generic real-station resolution, the
    /// same path a non-debug game uses (already correct there).</para>
    /// </summary>
    private static DataStationIGC? FindDebugGarrisonStation(Sector sector, short sideId, bool fromMapGen)
    {
        if (fromMapGen) return null;
        foreach (var st in sector.Stations)
            if (st.SideId == sideId && _debugGarrisonStationIds.Contains(st.StationId))
                return st;
        return null;
    }

    /// <summary>
    /// P379 — apply the debug-mode test topology to a freshly-assembled mission.
    /// Runs only when <c>liveSettings.debugMode == true</c> (gated at the boot
    /// call site). Three additive effects, all confined to debug mode and all
    /// reading/writing only real, hardcoded P379 map data (never a generic
    /// configurable N-sector scheme, per Domingo's design):
    /// <list type="number">
    /// <item><b>Inject one real garrison-type station per home base</b> — a
    ///      <c>DataStationIGC</c> of <c>StationTypeId = 15</c> (the exact type
    ///      the real Ganymede/Organia garrisons use) into Omega (side 0) and
    ///      Plato (side 1), each at a real position inside the sector, so the
    ///      full economy loop (dock, buy, bucket-funded research) is testable
    ///      at a genuine, side-owned station. Bucket funding/donation is
    ///      side-scoped (gated on the donor ship's SideId, zero proximity
    ///      check), so the economy works the moment the station exists.</item>
    /// <item><b>Seal the whole 3-sector arena, not just the home bases</b> —
    ///      remove the two non-Alderan warps from Omega (2154→Bug Nebula,
    ///      2156→Tellus) and from Plato (2179→Ganymede, 2190→Rigel), keeping
    ///      Omega→Alderan (2153) and Plato→Alderan (2184). Alderan's own four
    ///      OTHER real warps (2150→Cirrus, 2183→Juniper, 2187→Rigel, 2189→Gaia)
    ///      are removed too, leaving only its two warps back to Omega (2152) and
    ///      Plato (2185) — a live-playtest follow-up to the original pass, which
    ///      left Alderan's warp list untouched and let a ship leave the arena
    ///      entirely through one of those four. Full isolation (a real "mini 3
    ///      stage setup to test the full game cycle") was the actual intent.
    ///      Removal is one-directional (each sector's list is its own), so the
    ///      six kept pairs (Omega↔Alderan, Plato↔Alderan) are never affected by
    ///      trimming the far sectors' own separate warp-back objects.</item>
    /// <item><b>Wire the debug <see cref="Server.ShipSpawner"/> closure</b> — a
    ///      monotonic per-server spawn counter (never derived from peer counts,
    ///      safe across reconnects): side alternates <c>spawnCount % 2</c>
    ///      (side 0 → Omega, side 1 → Plato), hull alternates
    ///      <c>(spawnCount / 2) % 2</c> (each side's first peer → Scout, second
    ///      → Fighter, cycling again for a 5th+ connection instead of erroring —
    ///      handles "any number of clients", not just exactly 4). Ships spawn
    ///      just off their base's station so they can immediately fly in to
    ///      dock/buy, explicitly set <c>Ship.SideId</c> (without it a peer's own
    ///      EXPORT(26) never fires), and are placed via <c>Mission.PlaceShip</c>.</item>
    /// </list>
    /// Everything here goes through the existing <see cref="Server.ShipSpawner"/>
    /// seam, <see cref="Mission.PlaceStation"/> and the new
    /// <see cref="Sector.RemoveWarp"/> — no shared spawn/warp/station code is
    /// modified, so debug-mode-off behavior is untouched.
    /// </summary>
    private void ApplyDebugTestTopology(Mission mission, bool fromMapGen)
    {
        // P402 — a MapGen-sourced mission takes the generated-arena branch: the
        // generator already placed each side's real home station and produced the
        // topology, so there is nothing to inject or seal. We only wire the
        // hull-mix ShipSpawner, resolving each side's home sector generically
        // (the same way HomeSectorForSide does) since there is no leftover real
        // Ganymede/Organia to create the ambiguity the real-map branch exists to
        // disambiguate.
        if (fromMapGen)
        {
            WireGeneratedArenaShipSpawner();
            Console.WriteLine("[boot] DEBUG MODE ACTIVE — generated (MapGen) arena: no synthetic stations, no warp removal; hull-mix ShipSpawner wired via generic HomeSectorForSide resolution");
            return;
        }

        // P379 follow-up (landed together with P382): the topology below is
        // keyed to the real m2xspiderw sectors/warps (1052 Alderan, 1053 Omega,
        // 1060 Plato, and their fixed warp ids 2152-2190). debugMode is itself a
        // general-purpose tunable (P378 — it also raises lifepod endurance), and
        // turning it on must not break a mission on any OTHER map (e.g. the P378
        // endurance test boots debugMode=true on XXV_simple). If the map lacks
        // the expected m2xspiderw sectors (matched by id AND name) AND it did not
        // come from MapGen (already handled above), skip the topology gracefully —
        // debug mode remains active for its other effects.
        if (!(mission.Sectors.Any(s => s.Id == 1053 && s.Name == "Omega")
              && mission.Sectors.Any(s => s.Id == 1060 && s.Name == "Plato")
              && mission.Sectors.Any(s => s.Id == 1052 && s.Name == "Alderan")))
        {
            Console.WriteLine("[boot] DEBUG MODE ACTIVE but this map is neither m2xspiderw (Omega/Plato/Alderan) nor a generated (MapGen) map — skipping the P379 debug test topology; debug tunables (e.g. lifepod endurance) still apply");
            return;
        }

        var core = mission.Core;
        // Fixed real P379 sector ids (verified against m2xspiderw.igc + PCore001.igc).
        var omega = RequireSector(mission, 1053, "Omega");
        var plato = RequireSector(mission, 1060, "Plato");
        var alderan = RequireSector(mission, 1052, "Alderan");

        // 1) Inject one real garrison-type station per home base. Reuse the real
        //    garrison StationTypeId = 15 (inheriting SabmRestart/SabmLand/etc.)
        //    rather than hand-crafting a capability bitmask.
        if (!core.StationTypes.TryGetValue(15, out var garrison))
            throw new InvalidOperationException("P379 debug topology: core has no station type 15 (garrison)");
        mission.PlaceStation(new Station(MakeDebugStation(OmegaDebugStationId, 0, (short)1053, [800f, 990f, 0f], "Omega Debug Garrison"), garrison), omega);
        mission.PlaceStation(new Station(MakeDebugStation(PlatoDebugStationId, 1, (short)1060, [-1060f, -612f, 0f], "Plato Debug Garrison"), garrison), plato);

        // 2) Remove the two non-Alderan warps from each home base, leaving only
        //    the Alderan choke points (2153/2184). Also trim Alderan itself down
        //    to just its two warps back to Omega/Plato (2152/2185) — a follow-up
        //    to the original P379 pass: Domingo flagged live that Alderan's other
        //    four real warps (Cirrus/Juniper/Rigel/Gaia) were still open, letting
        //    a ship leave the 3-sector arena entirely. Full isolation was the
        //    actual intent ("mini 3 stage setup to test the full game cycle").
        RemoveWarps(omega, 2154, 2156);                    // Bug Nebula, Tellus
        RemoveWarps(plato, 2179, 2190);                    // Ganymede, Rigel
        RemoveWarps(alderan, 2150, 2183, 2187, 2189);      // Cirrus, Juniper, Rigel, Gaia

        // 3) Wire the debug ShipSpawner closure. A closure-captured counter that
        //    never derives from _peerShips.Count (mirrors tools/vizfight's
        //    InjectExplorerSpawn pattern).
        var spawned = 0;
        ShipSpawner = (core_, mission_, shipId) =>
        {
            int n = spawned++;
            short sideId = (short)(n % 2);            // side 0 → Omega, side 1 → Plato
            bool fighter = (n / 2) % 2 == 1;          // each side's 1st → Scout, 2nd → Fighter
            var home = sideId == 0 ? omega : plato;
            var hull = fighter ? ResolveDebugHull(core_, "Fighter") : ResolveDebugHull(core_, "Scout");

            var stationPos = home.Stations.First(st => st.SideId == sideId).Position;
            int withinBase = n / 2;                    // ordinal of this peer within its side
            var pos = new Vector3(
                stationPos[0] + 420f + (withinBase % 2) * 45f - 22.5f,
                stationPos[1],
                stationPos[2] + (withinBase / 2) * 45f);

            var ship = new Ship(shipId, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = sideId;                      // required or a real client skips EXPORT(26)
            mission_.PlaceShip(ship, home);
            Console.WriteLine($"[debugtopology] peer {n + 1} spawned: ship {shipId}, hull {hull.Name}, side {sideId}, sector {home.Id} '{home.Name}'");
            return (ship, home);
        };

        Console.WriteLine("[boot] DEBUG MODE ACTIVE — side 0 home Omega(1053)/side 1 home Plato(1060), single warp each to Alderan(1052), Alderan sealed to just those 2 warps, Scout+Fighter mix");
    }

    /// <summary>
    /// P402 — wire the hull-mix <see cref="Server.ShipSpawner"/> for a
    /// MapGen-sourced debug arena. Keeps the exact hull-mix alternation of the
    /// P379 real-map closure (<c>spawnCount % 2</c> for side, <c>(spawnCount/2) % 2</c>
    /// for hull) and the same slot-grid parking just off the side's home station,
    /// but resolves each side's home sector via the generic
    /// <see cref="HomeSectorForSide"/> instead of hardcoding Omega/Plato — every
    /// generated topology places exactly one real side-owned home station per
    /// side, so the generic answer is already correct (no leftover real
    /// Ganymede/Organia to disambiguate against). Deliberately NOT restricted to
    /// Brawl: any generated topology with a per-side home station + connectable
    /// layout works here (e.g. SingleRing — each side spawns in its own generated
    /// home sector and reaches the enemy through the generated warps).
    /// </summary>
    private void WireGeneratedArenaShipSpawner()
    {
        var spawned = 0;
        ShipSpawner = (core_, mission_, shipId) =>
        {
            int n = spawned++;
            short sideId = (short)(n % 2);            // side alternation, unchanged from P379
            bool fighter = (n / 2) % 2 == 1;          // each side's 1st → Scout, 2nd → Fighter
            var home = HomeSectorForSide(mission_, sideId)
                ?? throw new InvalidOperationException($"debug generated arena: side {sideId} has no resolvable home sector (no generated home station)");
            var hull = fighter ? ResolveDebugHull(core_, "Fighter") : ResolveDebugHull(core_, "Scout");

            DataStationIGC station = default;
            bool found = false;
            foreach (var st in home.Stations)
                if (st.SideId == sideId) { station = st; found = true; break; }
            if (!found)
                throw new InvalidOperationException($"debug generated arena: side {sideId} home sector '{home.Name}' has no station owned by that side");
            int withinBase = n / 2;                   // ordinal of this peer within its side
            var pos = new Vector3(
                station.Position[0] + 420f + (withinBase % 2) * 45f - 22.5f,
                station.Position[1],
                station.Position[2] + (withinBase / 2) * 45f);

            var ship = new Ship(shipId, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = sideId;                     // required or a real client skips EXPORT(26)
            mission_.PlaceShip(ship, home);
            Console.WriteLine($"[debugtopology] peer {n + 1} spawned: ship {shipId}, hull {hull.Name}, side {sideId}, sector {home.Id} '{home.Name}'");
            return (ship, home);
        };
    }

    private static Sector RequireSector(Mission mission, short id, string name) =>
        mission.Sectors.FirstOrDefault(s => s.Id == id)
        ?? throw new InvalidOperationException($"P379 debug topology: sector {id} '{name}' is not in this mission");

    /// <summary>Build the <see cref="DataStationIGC"/> static record for a P379
    /// synthetic home-base garrison. Up/Forward/Rotation are the standard
    /// identity orientation; BpHull/BpShield mirror the real garrison's 240/240.</summary>
    private static DataStationIGC MakeDebugStation(short stationId, short sideId, short clusterId, float[] position, string name) => new(
        Position: position,
        Up: [0f, 1f, 0f],
        Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f],
        ClusterId: clusterId,
        SideId: sideId,
        StationId: stationId,
        StationTypeId: 15,
        BpHull: 240,
        BpShield: 240,
        Name: name);

    private static void RemoveWarps(Sector sector, params short[] warpIds)
    {
        foreach (var id in warpIds)
        {
            var found = sector.Warps.FirstOrDefault(w => w.Warp.WarpId == id)
                ?? throw new InvalidOperationException($"P379 debug topology: sector '{sector.Name}' has no warp id {id} to remove");
            if (!sector.RemoveWarp(found))
                throw new InvalidOperationException($"P379 debug topology: failed to remove warp id {id} from sector '{sector.Name}'");
        }
    }

    private static DataHullTypeIGC ResolveDebugHull(StaticCore core, string nameContains)
    {
        foreach (var h in core.HullTypes.Values)
            if (h.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                return h;
        throw new InvalidOperationException($"P379 debug topology: core has no hull whose name contains '{nameContains}'");
    }

    /// <summary>
    /// P382 — the per-side initial-miner count at mission start. The native
    /// value comes from <c>MissionParams.nInitialMinersPerTeam</c>
    /// (<c>src/Igc/igc.h:1214</c>, default 1 at igc.h:1330), consumed at
    /// <c>fsmission.cpp:2205</c>. This port treats the whole <c>misparms</c>
    /// block as an opaque 252-byte blob (<c>Wire/MissionDef.cs:63</c>) and
    /// carries no real per-mission param data into it (the port's MISSIONDEF
    /// encode only populates the join-path fields; everything else is
    /// zero-filled), so reading the real byte offset would always yield 0 and
    /// spawn no miners. Rather than decode the full native
    /// <c>MissionParams</c> struct field-by-field (explicitly out of scope for
    /// P382), this documented default — 1, matching native's own default — is
    /// used instead. Tune here (or promote to a config field) to change the
    /// initial per-team miner count.
    /// </summary>
    private const int DefaultInitialMinersPerTeam = 1;

    /// <summary>
    /// P382 — test-facing opt-out for the boot-time initial-miner spawn. Default
    /// <c>true</c> (every real game spawns its stock miners, per the brief); a
    /// narrowly-scoped live fixture that spawns its own NPCs into a pristine
    /// arena (single-rock mining/contention arbitration, cache-cleanup with
    /// occupant-free home stations) sets this to <c>false</c> before
    /// <see cref="Boot(ServerConfig)"/> so a wandering initial miner can't
    /// perturb its precise assertions. This is a test seam, not a gameplay
    /// toggle — it is deliberately separate from <c>liveSettings.debugMode</c>
    /// and is never read by the per-mission spawn itself.
    /// </summary>
    public bool SpawnInitialMinersAtBoot { get; set; } = true;

    /// <summary>
    /// P382 — spawn each side's initial miner drones docked at its home station
    /// at mission start, the port of the "Create miners for each side in the
    /// sector with their home starbase" block (<c>fsmission.cpp:2196-2217</c>,
    /// which auto-buys <c>nInitialMinersPerTeam</c> copies of the side's
    /// <c>c_ptMiner</c> drone type via <c>CreateStockDrone</c>/<c>CreateDrone</c>
    /// with <c>c_aabmMineHe3</c> orders + unconditional <c>SetAutopilot(true)</c>,
    /// then docks each at the station: <c>SetStation(pstation)</c>). Runs once
    /// per real game (NOT debug-gated) at mission assemble.
    /// <para><b>Drone type from real core data.</b> Resolves the lowest-id
    /// <c>DataDroneTypeIGC</c> whose <c>PilotType == Ship.PilotTypeMiner</c>
    /// from <c>mission.Core.DroneTypes</c> (PCore001 has eleven race-specific
    /// miner drone types, ids 1/7/13/32/33/36/56/332/432/532/632; lowest id 1
    /// wins deterministically). The chosen id is passed straight through
    /// <see cref="SpawnNpcShip"/>'s existing <c>droneTypeId</c> convention
    /// (which sets the ship's real <c>PilotType</c>); note SpawnNpcShip itself
    /// assigns the lowest-HullId hull regardless (a pre-existing documented
    /// simplification — hull 1 'Adv Miner' here, itself a valid miner hull with
    /// the cargo the mining loop needs), so the spawned miner is a functional
    /// miner even though its hull isn't the drone type's exact per-race hull.
    /// A core with no miner drone type spawns none (native would too).</para>
    /// <para><b>Docked at home, then handed to P381.</b> Each spawned ship is
    /// latched docked (<c>StationId = home</c>) and stamped
    /// <c>LastDockTime = _simNow</c> exactly as P381's dock latches do, so the
    /// per-tick <c>RunNpcAutoLaunch</c> pass (P381) undocks it on its own after
    /// the ~10 s cooldown and the existing decision loop begins mining —
    /// precisely the "see them working at start" behaviour Domingo reported was
    /// missing. A docked-from-birth miner must carry a real <c>LastDockTime</c>;
    /// with the P381 default <c>NegativeInfinity</c> the cooldown would read as
    /// elapsed (instant undock) rather than the intended ~10 s pause.
    /// <see cref="ResolveSideHomeStation"/> picks the station; a side with no
    /// home station spawns none (native, iterating the side's station list,
    /// would too — no forced nonzero default).</para>
    /// </summary>
    private void SpawnInitialMiners(Mission mission)
    {
        // Resolve the real miner drone type (deterministic lowest-id pick).
        DataDroneTypeIGC? minerType = null;
        foreach (var dt in mission.Core.DroneTypes.Values.OrderBy(d => d.DroneTypeId))
            if (dt.PilotType == Ship.PilotTypeMiner) { minerType = dt; break; }
        if (minerType is null)
        {
            Console.WriteLine("[initial-miner] core has no Miner-pilot drone type — spawning no initial miners");
            return;
        }

        foreach (var side in mission.Sides)
        {
            var home = ResolveSideHomeStation(mission, side.Id);
            if (home is null)
            {
                Console.WriteLine($"[initial-miner] side {side.Id} has no home station — spawning no initial miners");
                continue;
            }
            DataStationIGC hs = home.Value;
            var sector = mission.Sectors.FirstOrDefault(s => s.Id == hs.ClusterId);
            if (sector is null)
            {
                Console.WriteLine($"[initial-miner] side {side.Id} home station {hs.StationId} has no resident sector — skipping");
                continue;
            }

            for (int i = 0; i < DefaultInitialMinersPerTeam; i++)
            {
                var pos = new Vector3(hs.Position[0], hs.Position[1], hs.Position[2]);
                var ship = SpawnNpcShip(mission, sector, side.Id, pos, minerType.Value.DroneTypeId);
                ship.StationId = hs.StationId;      // docked at home — native SetStation(pstation)
                ship.LastDockTime = _simNow;          // P381: docked-from-birth, so the auto-launch cooldown applies
                Console.WriteLine($"[initial-miner] side {side.Id} spawned initial miner #{i + 1}/{DefaultInitialMinersPerTeam} ship {ship.Id} (hull '{ship.HullType.Name}', droneType {minerType.Value.DroneTypeId}) docked at station {hs.StationId} '{hs.Name}' in '{sector.Name}'");
            }
        }
    }

    /// <summary>
    /// P382 — resolve a side's home station for the initial-miner spawn,
    /// mirroring native's "home starbase" notion (each side's stock miners dock
    /// at its own station). <b>Debug-aware:</b> when
    /// <c>liveSettings.debugMode</c> is active, P379 has relocated each side's
    /// home to the synthetic Omega/Plato garrisons
    /// (<see cref="OmegaDebugStationId"/> for side 0, <see cref="PlatoDebugStationId"/>
    /// for side 1), and the real Ganymede/Organia garrisons are not the usable
    /// debug home — so this resolves to those exact synthetic stations, never
    /// duplicating P379's logic. Normal mode reuses the established
    /// <see cref="HomeSectorForSide"/> + first-side-owned-station resolution
    /// (the P368/P379 shape) to find the side's real home starbase. Returns
    /// <c>null</c> when the side has no resolvable home station (spawn none —
    /// native iterates the side's own station list, so no station ⇒ no miners).
    /// P402: on a MapGen-sourced debug mission the generator already placed
    /// exactly one real home station per side (no Ganymede/Organia ambiguity),
    /// so the synthetic-garrison preference is skipped and the generic path below
    /// is used — identical to a non-debug game.
    /// </summary>
    private DataStationIGC? ResolveSideHomeStation(Mission mission, short sideId)
    {
        if (_config.LiveSettings?.DebugMode == true && !MissionCameFromMapGen(mission))
        {
            short synthId = sideId == 0 ? OmegaDebugStationId : PlatoDebugStationId;
            foreach (var sector in mission.Sectors)
                foreach (var st in sector.Stations)
                    if (st.StationId == synthId)
                        return st;
            // P379 created these just before this runs; absence is an invariant
            // violation worth logging loudly, then fall through to real stations.
            Console.WriteLine($"[initial-miner] DEBUG MODE: side {sideId} synthetic home station {synthId} not found (debug topology missing?) — falling back to real resolution");
        }

        var home = HomeSectorForSide(mission, sideId);
        if (home is null) return null;
        foreach (var st in home.Stations)
            if (st.SideId == sideId)
                return st;
        return null;
    }


    private void ValidatePaths()
    {
        if (string.IsNullOrWhiteSpace(_config.ArtPath) || !Directory.Exists(_config.ArtPath))
            throw new InvalidDataException(
                $"artPath '{_config.ArtPath}' does not exist. " +
                ExternalPaths.DescribeMissing(ExternalResourceKind.Artwork));
    }

    private StaticCore LoadCore()
    {
        var corePath = Path.Combine(_config.ArtPath, _config.StaticCore);
        if (!File.Exists(corePath))
            throw new InvalidDataException($"static core '{corePath}' not found under artPath");
        var file = IgcReader.ParseFile(corePath);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException($"'{corePath}' is not a static core (wrapper {file.Wrapper})");
        var core = StaticCore.Load(file);
        Console.WriteLine($"[boot] static core '{_config.StaticCore}' loaded: version={core.Version}, " +
                          $"{core.Civilizations.Count} civilizations, {core.HullTypes.Count} hulls, " +
                          $"{core.StationTypes.Count} station types");

        // The config indexes civilizations by id; surface the mapping so a bad
        // id in a game definition is obviously wrong in the logs.
        foreach (var civ in core.Civilizations.OrderBy(kv => kv.Key))
            Console.WriteLine($"[boot]   civ {civ.Key,4}  '{civ.Value.Name}'");
        return core;
    }

    /// <summary>BOOT_PATH.md step 5 — cores.txt/maps.txt are stubbed (dropped)
    /// and we say so rather than silently ignoring. ranks.txt is NOT stubbed
    /// anymore (P130 actually loads it — see <see cref="LoadRankTable"/>), so
    /// it is named out of this note; cores.txt/maps.txt remain deferred.</summary>
    private static void StubFileLists() =>
        Console.WriteLine("[boot] (stub) cores.txt / maps.txt NOT loaded — download list out of scope (rank list IS loaded from ranks.txt, see above)");

    /// <summary>
    /// P130 — load the real standalone rank table from
    /// <c>&lt;artPath&gt;/ranks.txt</c>, using the same configured-artwork-root
    /// path-resolution convention the static core, mission maps, and P116's
    /// <c>.cvh</c> corpus use (single configured <c>ArtPath</c>, files flat at
    /// that root — cf. <c>Server.cs:251,344,356</c>). Faithfully ports native's
    /// standalone <c>LoadRankInfo</c> <c>#else</c> branch
    /// (<c>FedSrv.CPP:125-148</c>): count header then <c>&lt;rank&gt;,&lt;name&gt;</c>
    /// lines, every entry <c>civ=-1</c>/<c>requiredRanking=0</c>. Missing file →
    /// empty table (native's <c>g.cRankInfo = 0</c>, FedSrv.CPP:145-146).
    /// </summary>
    private RankInfo LoadRankTable()
    {
        var path = Path.Combine(_config.ArtPath, "ranks.txt");
        var table = RankInfo.FromRanksFile(path);
        Console.WriteLine($"[boot] rank list loaded: {table.Ranks.Length} ranks from '{path}' (standalone: civ=-1, requiredRanking=0)");
        return table;
    }

    private void BindHostAndTransport()
    {
        _host = new ServerHost();
        _host.ShipKilled = BroadcastKillShip;   // wire the site's KillShipEvent to a broadcast
        _host.ShipWarped = OnShipWarped;        // wire the site's HitWarpEvent transit to per-peer routing
        _host.Payday = OnPayday;                // wire the site's PaydayEvent to the per-side wire payout
        _host.BucketCompletedHook = OnBucketCompleted;  // P119: wire the site's BucketCompleted to the completion recorder (detection only)
        _host.Mine = OnMine;                    // wire the site's MineAsteroidEvent to the host logger
        _host.ShipDocked = OnShipDocked;        // wire the site's DockWithStationEvent to the per-peer docked notify
        _host.ProjectileCreated = BroadcastProjectileInstance;   // P88: wire the site's ProjectileCreatedEvent to the wire broadcast
        _host.MissileCreated = BroadcastFireMissile;            // P90: wire the site's MissileCreatedEvent to the wire broadcast
        _host.MissileDestroyed = BroadcastMissileDestroyed;     // P90: wire the site's MissileDestroyedEvent to the wire broadcast
        _host.ProjectileHit = BroadcastProjectileHullHit;      // P364: wire the site's ProjectileHitEvent to the wire broadcast
        _host.Collision = BroadcastCollision;                  // P463c: wire the site's CollisionEvent to the wire broadcast
        _host.LoadoutChange = BroadcastLoadoutChange;          // P173: wire the site's LoadoutChangeEvent to the wire broadcast
        _host.AutoReload = BroadcastAutoReload;                // P361: wire the site's AutoReloadTriggered to the wire broadcast
        _host.MineCreated = BroadcastExportMine;                // P94: wire the site's MineCreatedEvent to the wire broadcast
        _host.MineDestroyed = BroadcastMineDestroyed;           // P94: wire the site's MineDestroyedEvent to the wire broadcast
        _host.MineHit = BroadcastMineHit;                       // P574: wire the site's MineHitEvent (per-hit pop at P2) to the wire broadcast
        _host.ProbeCreated = BroadcastExportProbe;             // P99: wire the site's ProbeCreatedEvent to the wire broadcast
        _host.ProbeDestroyed = BroadcastProbeDestroyed;        // P99: wire the site's ProbeDestroyedEvent to the wire broadcast
        _host.TreasureDestroyed = BroadcastTreasureDestroyed;  // P586: wire the site's TreasureDestroyedEvent (expiry) to the whole-mission DESTROY_TREASURE
        _host.WarpBomb = BroadcastWarpBomb;                   // P101: wire the site's WarpBombEvent to the WHOLE-mission wire broadcast
        _host.AsteroidKilled = BroadcastAsteroidDestroyed;     // P102: wire the site's KillAsteroidEvent to the WHOLE-mission wire broadcast
        _host.StationKilled = BroadcastStationDestroyed;       // P125: wire the site's KillStationEvent to VacateStation + WHOLE-mission STATION_DESTROYED(59)
        _host.StationDamaged = BroadcastStationDamaged;        // P451: wire the site's DamageStationEvent to the defending team's under-attack / critical alert
        _host.StationCaptured = BroadcastStationCapture;       // P126: wire the site's CaptureStationEvent to the WHOLE-mission STATION_CAPTURE(58) broadcast
        _host.VictoryByStationsComputedHook = OnVictoryByStationsComputed;  // P191: wire the site's post-capture/kill recompute to GAME_STATE(155) + P178 GameOver
        _host.SideLostRestartStationHook = OnSideLostRestartStation;   // P455: wire the site's restart-station-loss seam to the station-elimination win (DeactivateSide)
        _host.ProsperityArtifactComputedHook = OnProsperityArtifactComputed;  // P302: wire the prosperity/artifact recompute to GAME_STATE(155) + P178 GameOver
        _host.FlagsKillsComputedHook = OnFlagsKillsComputed;  // P303: wire the flags/kills recompute to GAME_STATE(155) + P178 GameOver
        _host.Notable = OnNotable;              // wire the site's ReportNotable to the public spectator event
        _host.RequestRipcordReceived = OnNpcRequestRipcord;  // P172: wire the site's IIgcSite.RequestRipcord (NPC) to the ripcord state machine
        var transport = new TcpTransport(_host, _transportMode, _transportPoolWorkers); // site set; assign the back-ref next
        _host.Transport = transport;
        transport.Host((ushort)_config.Port);         // reproduce: bind the logon transport
        Console.WriteLine($"[boot] transport listening on 0.0.0.0:{_host.BoundPort} (all interfaces)");
    }

    /// <summary>Open the capture file (truncating any prior capture) when
    /// <c>--capture</c> was given; otherwise leave <see cref="_captureStream"/>
    /// null. No other behaviour depends on capture, so when off there is nothing
    /// to undo and no timing impact.</summary>
    private void OpenCaptureIfEnabled()
    {
        var path = _config.CapturePath;
        if (string.IsNullOrEmpty(path)) return;
        _captureStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Console.WriteLine($"[boot] capturing packets to '{path}' (pipe through tools/fedmsgdump)");
    }

    /// <summary>Central send path: optional capture then the actual send. Every
    /// packet the server emits flows through here so capture sees all of TX.</summary>
    private void SendPeer(ConnectionId target, byte[] packet)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: true, packet));
        // P603 — internal test seam (InternalsVisibleTo): observe every outbound
        // packet per recipient, in order, so the byte-identity harness can collect
        // the exact per-peer payload stream both broadcast modes produce. Null in
        // production, so this is a no-op check on the default path.
        _testSendCapture?.Invoke(target, packet);
        _host.Send(target, packet);   // ServerHost.Send forwards as guaranteed: true
    }

    /// <summary>
    /// P606/P608 — central send path for the latest-wins <b>streaming</b> broadcasts
    /// (ship-state / heavy-light / per-side periodic syncs): same capture +
    /// test-seam as <see cref="SendPeer"/>, but routed as non-guaranteed and
    /// coalesced per <paramref name="coalesceKey"/> so a slow peer keeps at most
    /// the newest pending frame per key. Everything that must be ordered/never
    /// dropped (chat, kills, joins, exports, status, events) stays on
    /// <see cref="SendPeer"/>. P608: the OWNER's own-ship SingleShipUpdate passes
    /// <paramref name="guaranteed"/>=true so it is coalesce-keyed (bounded) yet
    /// never shed under capacity — on its own key, never superseded by teammate
    /// traffic (native: own ship SINGLE_SHIP_UPDATE sent FM_GUARANTEED,
    /// fsship.cpp:544).
    /// </summary>
    private void SendPeerCoalescing(ConnectionId target, byte[] packet, int coalesceKey, bool guaranteed = false)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: true, packet));
        _testSendCapture?.Invoke(target, packet);
        _testSendCoalescingCapture?.Invoke(target, packet, coalesceKey, guaranteed);
        if (guaranteed) _host.SendGuaranteedCoalescing(target, packet, coalesceKey);
        else _host.SendCoalescing(target, packet, coalesceKey);
    }

    /// <summary>Capture one received packet before it is processed. Called from
    /// the transport event drain (the sim thread) so it is naturally serialized
    /// with <see cref="SendPeer"/>'s lock-guarded writes.</summary>
    private void CaptureReceived(byte[] packet)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: false, packet));
    }

    /// <summary>
    /// P397 — map a per-game <see cref="MapGenConfig"/> onto the Core's
    /// <see cref="MapGenParams"/>, filling absent numeric fields with the
    /// params' native defaults and resolving the topology name against
    /// <see cref="MapGenType"/>. Throws on an unknown topology name so a typo'd
    /// config fails loudly at boot rather than silently falling back to a file.
    /// </summary>
    private static MapGenParams ToCoreParams(MapGenConfig cfg, int teams, int[] civIds)
    {
        if (!Enum.TryParse<MapGenType>(cfg.Type, ignoreCase: true, out var type))
            throw new InvalidDataException(
                $"map generation type '{cfg.Type}' is not supported "
                + "(expected one of: SingleRing, DoubleRing, BigRing, HiLo, HiHigher, "
                + "InsideOut, LargeSplit, Star, PinWheel, DiamondRing, SnowFlake, "
                + "Grid, EastWest, SplitBase, Brawl)");

        return new MapGenParams
        {
            Type = type,
            Teams = teams,
            CivIds = civIds,
            MapSize = cfg.MapSize ?? 0,
            PlayerSectorMineableAsteroids = (short)(cfg.PlayerSectorMineableAsteroids ?? 10),
            PlayerSectorSpecialAsteroids = (short)(cfg.PlayerSectorSpecialAsteroids ?? 2),
            PlayerSectorAsteroids = (short)(cfg.PlayerSectorAsteroids ?? 10),
            PlayerSectorTreasures = (short)(cfg.PlayerSectorTreasures ?? 0),
            NeutralSectorMineableAsteroids = (short)(cfg.NeutralSectorMineableAsteroids ?? 8),
            NeutralSectorSpecialAsteroids = (short)(cfg.NeutralSectorSpecialAsteroids ?? 2),
            NeutralSectorAsteroids = (short)(cfg.NeutralSectorAsteroids ?? 8),
            NeutralSectorTreasures = (short)(cfg.NeutralSectorTreasures ?? 0),
            He3Density = (float)(cfg.He3Density ?? 1.0),
            FlagsGame = cfg.FlagsGame ?? false,
            ArtifactsGame = cfg.ArtifactsGame ?? false,
        };
    }

    private List<Mission> InstantiateGames(StaticCore core)
    {
        var missions = new List<Mission>();

        // P116 — the production landing-bay resolver/cache, keyed by hull
        // ModelName and shared by every mission this server hosts. Its loader
        // locates each carrier's .cvh under the configured art path (the same
        // asset-root convention the static core and maps use; all corpus .cvh
        // files sit flat at that root), parses it once and caches the
        // reconstructed bay geometry, so the per-tick carrier-landing pass
        // (Sector.ResolveCarrierLandings) never reparses frames.
        var landingBays = new HullLandingBayCache(name =>
        {
            var p = Path.Combine(_config.ArtPath, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });
        _landingBays = landingBays;   // P439: retain for the game-over mission rebuild

        // P550 — the station landing-bay resolver/cache, keyed by station type
        // model name and shared by every mission this server hosts. Same loader
        // convention as the carrier cache above (<artPath>/<modelName>.cvh); the
        // dock pass (Sector.ResolveDockCollisions) runs the native station
        // InGarage geometric test against this real capgarage data.
        var stationLandingBays = new StationLandingBayCache(name =>
        {
            var p = Path.Combine(_config.ArtPath, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });
        _stationLandingBays = stationLandingBays;

        // P465c — the per-hull weapon-muzzle (hardpoint) offset cache, sharing
        // the landing-bay loader (the same <artPath>/<modelName>.cvh root), so
        // Weapon.FireWeapon spawns each round at its gun. Retained like
        // _landingBays so a game-over rebuild keeps the same cached offsets.
        var weaponMuzzles = new WeaponMuzzleCache(name =>
        {
            var p = Path.Combine(_config.ArtPath, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });
        _weaponMuzzles = weaponMuzzles;

        if (_config.Games.Count == 0)
        {
            Console.WriteLine("[boot] (config) no games — booting a bare transport, nothing to tick");
            return missions;
        }

        foreach (var g in _config.Games)
        {
            // P439 — the whole per-game assembly (map load-or-MapGen, config
            // validation, Mission.Assemble, _mapGenMissions tagging, P379 debug
            // topology, landing-bay attach, Stage = NotStarted) is factored into
            // AssembleGameMission so a finished mission can be rebuilt at
            // game-over from the SAME inputs, byte-for-byte identical to boot.
            var mission = AssembleGameMission(g, core, landingBays, weaponMuzzles, stationLandingBays);
            missions.Add(mission);
            Console.WriteLine($"[boot] game '{g.Name}' ready: map={g.Map}, {mission.Sectors.Count} sectors, " +
                              $"{mission.Sides.Count} sides [{string.Join(", ", mission.Sides.Select(s => s.Name ?? $"side{s.Id}"))}], " +
                              $"{mission.Warps.Count} warps, {mission.Sectors.Sum(s => s.Stations.Count)} stations, " +
                              $"{mission.Sectors.Sum(s => s.Asteroids.Count)} asteroids" +
                              (mission.DanglingWarps.Count > 0 ? $", {mission.DanglingWarps.Count} DANGLING warps" : ""));
        }
        return missions;
    }

    /// <summary>
    /// P439 — assemble ONE game's mission from its <see cref="GameConfig"/>, the
    /// loaded <paramref name="core"/> and the shared hull-landing-bay
    /// <paramref name="landingBays"/> resolver. Extracted verbatim from the
    /// <c>InstantiateGames</c> per-game loop body (which previously inlined it)
    /// so the same pristine assembly can be rerun at game-over to rebuild a
    /// finished mission — the port's collapsed equivalent of native's
    /// <c>ResetMission</c> + <c>GenerateMission</c> pair (which this port cannot
    /// reproduce in place because the world is generated once at
    /// <c>Mission.Assemble</c>, not re-generated on the next start). Covers:
    /// <list type="bullet">
    ///   <item>map load (file) or procedural MapGen (<c>g.MapGen</c>, P397);</item>
    ///   <item>config validation (Teams &gt;= 1, CivIds/SideNames lengths);</item>
    ///   <item><c>Mission.Assemble</c> (site-bound, core-resolved);</item>
    ///   <item>the <see cref="_mapGenMissions"/> tag (P402);</item>
    ///   <item>the P379 debug-mode test topology (<c>ApplyDebugTestTopology</c>);</item>
    ///   <item>the landing-bay resolver attach (P116) and <c>Stage = NotStarted</c> (P180).</item>
    /// </list>
    /// This is a pure refactor of the former loop body with byte-identical boot
    /// behaviour; boot tests must stay green.
    /// </summary>
    private Mission AssembleGameMission(GameConfig g, StaticCore core, HullLandingBayCache landingBays, WeaponMuzzleCache weaponMuzzles, StationLandingBayCache stationLandingBays)
    {
        // P397 — procedurally generate this game's map instead of loading a
        // file when g.MapGen is set. The generator path is a pure addition:
        // with MapGen null (every existing config), the byte-for-byte
        // file-load path below runs unchanged.
        MissionMap map;
        MapGenConfig? mg = g.MapGen;
        bool fromMapGen = mg is not null;
        if (fromMapGen)
        {
            var pmp = ToCoreParams(mg!, g.Teams, g.CivIds);
            map = MissionMapGenerator.Generate(pmp, core, 0.0);
            Console.WriteLine($"[boot] game '{g.Name}' generating map: topology={mg!.Type}, "
                + $"{map.Clusters.Count} sectors / {map.Warps.Count} warps / "
                + $"{map.Stations.Count} stations / {map.Asteroids.Count} asteroids");
        }
        else
        {
            var mapPath = Path.Combine(_config.ArtPath, g.Map);
            if (!File.Exists(mapPath)) throw new InvalidDataException($"map '{mapPath}' not found under artPath");
            var mapFile = IgcReader.ParseFile(mapPath);
            if (mapFile.Wrapper != IgcWrapper.DataSize)
                throw new InvalidDataException($"'{g.Map}' is not a map (wrapper {mapFile.Wrapper})");
            map = MissionMap.Load(mapFile);
        }

        if (g.Teams < 1)
            throw new InvalidDataException($"game '{g.Name}': teams must be >= 1, got {g.Teams}");
        if (g.CivIds.Length < g.Teams)
            throw new InvalidDataException($"game '{g.Name}': {g.Teams} teams but {g.CivIds.Length} civ ids");
        if (g.SideNames.Length < g.Teams)
            throw new InvalidDataException($"game '{g.Name}': {g.Teams} teams but {g.SideNames.Length} side names");

        var sides = new SideSpec[g.Teams];
        for (int t = 0; t < g.Teams; t++)
            sides[t] = new SideSpec((short)t, g.SideNames[t], (short)g.CivIds[t]);

        var mission = Mission.Assemble(core, map, sides, _host);  // site bound; core resolved
        // P402 — remember whether this mission's map came from MapGen so the
        // debug-topology machinery below (and the join/death/respawn paths that
        // share its station helpers) can tell a generated arena from the real
        // m2xspiderw map. Nothing else reads _mapGenMissions.
        if (fromMapGen) _mapGenMissions.Add(mission);
        // P379 — DEBUG-MODE TEST TOPOLOGY. Gate the entire mini-map (spawn
        // placement, home-base station injection, and warp isolation) on the
        // single reusable liveSettings.debugMode flag (P378). When OFF the
        // production server behavior is byte-for-byte unchanged — nothing in
        // this block runs and no shared spawn/warp/station path is touched.
        // P402 — a MapGen-sourced mission (fromMapGen) takes the generated-arena
        // branch instead.
        if (_config.LiveSettings?.DebugMode == true)
            ApplyDebugTestTopology(mission, fromMapGen);
        // P509 — TEST-UNLOCK TECH GRANT. Applied at assembly (which both boot
        // and the P439/P480 game-over rotation call), so every side's
        // DevelopmentTechs already carries the required-tech union before ANY
        // joiner seats — thus CREATE_BUCKETS at join carries the full mask and
        // the docked shop's P468 filter + the investment screen (P487) agree
        // with the server's buy-gate from the very first tick. No-op (byte-
        // identical) unless liveSettings.testUnlock.enabled.
        ApplyTestUnlock(mission);
        // P180 — a real team-lobby/pre-game phase: the mission is born
        // NotStarted (the stage's genuine default) and only transitions to
        // Started when every side with at least one connected player has
        // readied (see the ready-up recompute + auto-start in the PLAYER_READY
        // handler).
        mission.Stage = MissionStage.NotStarted;                // pre-game hold until every side is ready
        // P116 — attach the landing-bay resolver to every sector so the
        // per-tick carrier-landing pass has real .cvh geometry to test.
        foreach (var sec in mission.Sectors) sec.LandingBays = landingBays;
        // P550 — attach the station landing-bay resolver so the dock pass runs
        // the real capgarage geometric test (native CstationIGC::InGarage).
        foreach (var sec in mission.Sectors) sec.StationLandingBays = stationLandingBays;
        // P465c — attach the weapon-muzzle cache so Weapon.FireWeapon spawns
        // each round at its hardpoint frame (the server's real muzzle geometry).
        mission.WeaponMuzzles = weaponMuzzles;
        return mission;
    }

    private void PrintReady() =>
        Console.WriteLine($"[boot] server ready: {_missions.Count} mission(s), {_config.UpdatesPerSecond} upd/s " +
                          $"(period {_period * 1000:0.###} ms). Ctrl-C to stop.");

    /// <summary>
    /// P509 — the <c>testUnlock</c> tech grant for one assembled mission. No-op
    /// (byte-identical to P441/P444/P468/P488) unless
    /// <c>liveSettings.testUnlock.enabled</c>.
    /// <para>When enabled and <see cref="TestUnlockConfig.AllTech"/>, ORs the
    /// core's required-tech union (<see cref="StaticCore.ComputeRequiredTechUnion"/>)
    /// into every side's <see cref="Side.DevelopmentTechs"/> — the SAME bit OR
    /// native <c>ApplyDevelopmentTechs</c> performs on research (sideigc.h:194-210),
    /// so clients receive <c>SIDE_TECH_CHANGE(75)</c> through the real tech path.
    /// It does <b>not</b> mark any development completed: no <c>DEV_COMPLETED</c>,
    /// no global-attribute delta (<c>ApplyGlobalAttributeSet</c> is never invoked),
    /// so flight/combat stats stay unmodified — the buyability-only default the
    /// task specifies. P470's <c>GroupId &gt;= 0</c> drone-hull gate and P488's
    /// <c>IsObsolete</c> rule are untouched (they run downstream in the shop and
    /// the BUY_LOADOUT gate). Idempotent: re-ORing an already-granted mask
    /// changes nothing, so calling it again after a P480 migration (to announce
    /// the mask to now-connected peers) is safe and adds no spurious new-tech
    /// log lines. Runs on the sim/main thread; the side-group broadcast
    /// (<see cref="BroadcastSideTechChange"/>) fires for each side whenever it
    /// has connected peers in this mission.</summary>
    private void ApplyTestUnlock(Mission mission)
    {
        var tu = _config.LiveSettings?.TestUnlock;
        if (tu is not { Enabled: true }) return;

        var union = mission.Core.ComputeRequiredTechUnion();
        foreach (var side in mission.Sides)
        {
            var before = side.DevelopmentTechs;
            if (tu.AllTech)
                side.DevelopmentTechs = before.Or(union);
            bool changed = !union.IsSubsetOf(before);
            if (changed)
                Console.WriteLine($"[testunlock] side {side.Id} '{side.Name}': GRANTED all {union.PopCount} required-tech bits (dev mask {before.PopCount} -> {side.DevelopmentTechs.PopCount})");
            // Announce to the side's connected peers via SIDE_TECH_CHANGE (the
            // real tech path) whenever any are present — an already-connected
            // client (e.g. a P480-migrated player) must see the mask even
            // though it did not arrive via CREATE_BUCKETS at join.
            if (_peerShips.Any(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == side.Id))
                BroadcastSideTechChange(mission, side);
        }
    }

    /// <summary>
    /// One pass: pump transport events, advance the simulation clock by exactly
    /// one period and tick every mission, then sleep the real remainder to hold
    /// cadence. Measures the real pass cost with a Stopwatch and logs a tick
    /// that overran the period (never silently skips or accumulates — the sim
    /// clock is independent of real wall time).
    /// </summary>
    public void Tick()
    {
        var sw = Stopwatch.StartNew();
        SimPass();
        sw.Stop();
        SyncCadence(sw.Elapsed.TotalSeconds);
    }

    /// <summary>The simulation half of a tick, with no cadence sleep and no
    /// overrun accounting: pump + advance sim + tick every mission. Deterministic
    /// and fast, so tests can drive many ticks without wall-clock dependency.</summary>
    public void TickSim() => SimPass();

    /// <summary>P600 — resolve the per-tick profiler from config + the
    /// <c>ALLEGIANCE_TICK_PROFILER</c> env var (env wins), and configure the
    /// static <see cref="TickProfiler"/> singleton. The env var is a
    /// one-string switch so a live run can profile without touching a config
    /// file: "1"/"true" enables log-only, "0"/"false" forces off, any other
    /// value is treated as the CSV output path. Interval comes from config, or
    /// the <c>ALLEGIANCE_TICK_PROFILER_INTERVAL</c> env var when present.</summary>
    private static void ApplyProfilerConfig(ProfilerConfig? cfg)
    {
        bool enabled = cfg?.Enabled ?? false;
        int interval = cfg?.ReportIntervalSeconds ?? 5;
        string? csv = cfg?.CsvPath;

        string? env = Environment.GetEnvironmentVariable("ALLEGIANCE_TICK_PROFILER");
        if (!string.IsNullOrEmpty(env))
        {
            if (env is "0" or "false" or "off") { enabled = false; }
            else if (env is "1" or "true" or "on") { enabled = true; csv = null; }
            else { enabled = true; csv = env; }        // a path → enabled with CSV
        }
        string? envInt = Environment.GetEnvironmentVariable("ALLEGIANCE_TICK_PROFILER_INTERVAL");
        if (!string.IsNullOrEmpty(envInt) && int.TryParse(envInt, out var iv) && iv > 0)
            interval = iv;

        TickProfiler.Configure(enabled, interval, csv);
    }

    /// <summary>
    /// P603 — resolve the parallel per-peer broadcast build from config +
    /// the <c>ALLEGIANCE_BROADCAST_PARALLEL_BUILD</c> env var (env wins), clamp
    /// the degree of parallelism to <c>[1, ProcessorCount-1]</c>, print the
    /// active value at boot, and stamp it into the profiler's report header so
    /// every measurement is labelled. Default OFF = today's serial broadcast
    /// path, byte-for-byte. A missing/disabled config section keeps
    /// <see cref="Server._broadcastParallelBuild"/> false (no behaviour change).
    /// </summary>
    private void ResolveBroadcastParallelBuild()
    {
        var bc = _config.Broadcast;
        bool enabled = bc?.ParallelBuild ?? false;

        string? env = Environment.GetEnvironmentVariable("ALLEGIANCE_BROADCAST_PARALLEL_BUILD");
        if (!string.IsNullOrEmpty(env))
            enabled = env is not ("0" or "false" or "off" or "");

        _broadcastParallelBuild = enabled;

        int ncpu = Math.Max(2, Environment.ProcessorCount);
        int maxPar = bc?.MaxDegreeOfParallelism is { } m && m > 0 ? m : ncpu - 1;
        _broadcastMaxParallelism = Math.Clamp(maxPar, 1, Math.Max(1, ncpu - 1));

        Console.WriteLine(enabled
            ? $"[boot] broadcast.parallelBuild = true — per-peer broadcast building runs in parallel (max {_broadcastMaxParallelism} workers); "
              + "payloads are byte-identical to serial mode; see P603."
            : "[boot] broadcast.parallelBuild = false — serial broadcast building (today's byte-for-byte behaviour)");

        // Stamp the active value into the profiler report header so a parallel
        // measurement run can never be mistaken for a serial one.
        TickProfiler.SetLabel(enabled
            ? $"broadcast.parallelBuild=true (maxPar={_broadcastMaxParallelism})"
            : "broadcast.parallelBuild=false");
    }

    /// <summary>
    /// P605 — resolve the transport's send-draining mode from config +
    /// the <c>ALLEGIANCE_TRANSPORT_MODE</c> env var (env wins), plus the pool
    /// worker count from <c>transport.poolWorkers</c> /
    /// <c>ALLEGIANCE_TRANSPORT_POOL_WORKERS</c> (default about the core count).
    /// Prints the active mode at boot (so every server run is labelled) and
    /// stamps it into the profiler report header together with the broadcast
    /// flag. Default thread-per-peer = today's behaviour, unchanged. An
    /// unrecognized mode fails loudly (catches typos) rather than silently
    /// picking a path the operator didn't ask for.
    /// </summary>
    private void ResolveTransportMode()
    {
        var tc = _config.Transport;

        string modeStr = tc?.Mode is { Length: > 0 } m ? m : "thread-per-peer";
        string? env = Environment.GetEnvironmentVariable("ALLEGIANCE_TRANSPORT_MODE");
        if (!string.IsNullOrEmpty(env)) modeStr = env;

        _transportMode = ParseTransportMode(modeStr);

        int ncpu = Math.Max(1, Environment.ProcessorCount);
        int workers = tc?.PoolWorkers is { } w && w > 0 ? w : ncpu;
        string? envW = Environment.GetEnvironmentVariable("ALLEGIANCE_TRANSPORT_POOL_WORKERS");
        if (!string.IsNullOrEmpty(envW) && int.TryParse(envW, out var ew) && ew > 0)
            workers = ew;
        _transportPoolWorkers = workers;

        Console.WriteLine(_transportMode == TransportMode.Pool
            ? $"[boot] transport mode = pool ({_transportPoolWorkers} workers)"
            : "[boot] transport mode = thread-per-peer");

        // Compose a single report-header label covering both switchable modes
        // (P603 broadcast + P605 transport), so no measurement is ambiguous.
        string bcast = _broadcastParallelBuild
            ? $"broadcast.parallelBuild=true (maxPar={_broadcastMaxParallelism})"
            : "broadcast.parallelBuild=false";
        string trans = _transportMode == TransportMode.Pool
            ? $"transport=pool ({_transportPoolWorkers} workers)"
            : "transport=thread-per-peer";
        TickProfiler.SetLabel($"{bcast}; {trans}");
    }

    private static TransportMode ParseTransportMode(string modeStr)
        => modeStr.Trim().ToLowerInvariant() switch
        {
            "thread-per-peer" or "threadperpeer" or "thread" or "tpp" or "default" => TransportMode.ThreadPerPeer,
            "pool" or "workerpool" => TransportMode.Pool,
            _ => throw new InvalidDataException(
                $"unknown transport.mode '{modeStr}' (expected 'thread-per-peer' or 'pool'); "
                + "set it in server.json transport.mode or $ALLEGIANCE_TRANSPORT_MODE"),
        };

    /// <summary>P600 — close the profiler's CSV handle at shutdown so the file is
    /// flushed and released cleanly (best-effort).</summary>
    private void ShutdownProfiler() => TickProfiler.Shutdown();

    /// <summary>
    /// P314 — run one <see cref="SimPass"/> stage inside an exception barrier.
    /// The per-tick broadcast/drain/post-update section historically had no
    /// exception containment: any escape — a misbehaving <see cref="Notable"/>
    /// subscriber, an over-limit <c>ExportShip.Encode</c>, a corrupt part —
    /// propagated all the way to the server's top-level catch and terminated the
    /// process (a single bad tick took the whole server down). A bad tick must
    /// instead degrade gracefully: log the failing stage with enough context to
    /// diagnose it, skip that stage for this tick, and keep simulating.
    /// <para>Returns <c>false</c> when the stage threw, so a caller that needs
    /// to sequence dependent work can observe the failure; every stage is
    /// independently barriered so one failure can't cascade into siblings.</para>
    /// </summary>
    private bool Step(string stage, Action action)
    {
        try
        {
            // P600 — per-stage bucket timing. The "step:"+stage string is built
            // only when the profiler is on, so the disabled path stays a plain
            // call with no per-tick allocation.
            if (TickProfiler.Instance is not null)
            {
                using (TickProfiler.Enter("step:" + stage))
                    action();
            }
            else
            {
                action();
            }
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[simpass] stage '{stage}' threw {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.ToString());
            lock (_statsLock) _simPassStageFailures++;
            return false;
        }
    }

    /// <summary>
    /// P314 — the sole raiser of the public <see cref="Notable"/> spectator
    /// event, run through an exception barrier. A subscriber (e.g. the vizfight
    /// event feed) must never be able to stall or kill the server: one bad
    /// subscriber is logged and skipped, the rest of the tick continues.
    /// All <c>Notify(...)</c> call sites route through here.
    /// </summary>
    private void Notify(string text)
    {
        try
        {
            Notable?.Invoke(text);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[notable] spectator subscriber threw on '{text}': {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.ToString());
        }
    }

    /// <summary>Standard loop body: tick until cancelled.</summary>
    public void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Tick();
            if (ct.IsCancellationRequested) break; // observe cancellation promptly after sleeping
        }
    }

    /// <summary>Run the loop for a bounded duration; returns total ticks.</summary>
    public long RunFor(TimeSpan duration, CancellationToken external = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        cts.CancelAfter(duration);
        Loop(cts.Token);
        return LoopIterations;
    }

    private void HandleTransportEvent(ITransportEvent evt)
    {
        switch (evt)
        {
            case PeerJoinedEvt j:
                Console.WriteLine($"[conn] peer {j.Id} '{j.Name}' joined");
                lock (_statsLock) _connectionsJoined++;
                break;
            case PeerLeftEvt l:
                // A disconnected peer can no longer command its ship, so it is
                // released from the broadcast set. P128: its ship is then
                // retired from the sector (SDR_LOGGEDOFF + removed) so it no
                // longer keeps flying under Mission.Update — the same cleanup
                // the graceful LOGOFF path runs through this same ReleasePeer.
                ReleasePeer(l.Id);
                Console.WriteLine($"[conn] peer {l.Id} left");
                break;
            case SessionLostEvt:
                Console.WriteLine("[conn] session lost");
                break;
            case ReceivedEvt r:
                CaptureReceived(r.Data);
                ProcessDatagram(r.From, r.Data);
                break;
        }
    }

    /// <summary>The connection-cleanup path for a departed peer — reused by
    /// both an abrupt transport drop (<see cref="PeerLeftEvt"/>) and a
    /// graceful <c>FM_CS_LOGOFF</c> (P127). Releasing the peer means dropping
    /// it from the broadcast/command sets (<c>_peerShips</c>,
    /// <c>_peerNames</c>) <b>and</b> (P128) retiring the ship it left behind:
    /// broadcasting <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c> to its sector peers
    /// and removing it from the sector so it no longer flies under
    /// <c>Mission.Update</c> — the port's analogue of native's
    /// <c>CFSShip::~CFSShip</c> → <c>AnnounceExit(NULL, SDR_LOGGEDOFF)</c> +
    /// <c>m_pShip->Terminate()</c> (fsship.cpp:58-68). Callers that fired
    /// because of LOGOFF also hold the original frame for its guaranteed echo
    /// before releasing. Idempotent: a second call (LOGOFF, then the
    /// transport's own OnPeerLeft) is a no-op.</summary>
    private void ReleasePeer(ConnectionId id)
    {
        // P281 — a pending side-select peer (logged on, not yet spawned) is
        // released the same way: drop it from the pending hold.
        _pendingSideSelect.Remove(id);
        // P315 — drop this peer's unsupported-fmid log-gate state so the
        // rate-limit table stays bounded by the live peer set.
        _unsupportedLog.Remove(id.Value);
        // M1 — release every remaining connection-scoped cache keyed by this
        // peer. The original ReleasePeer only freed _pendingSideSelect /
        // _peerShips / _peerNames / _playerReady / _pendingJoinRequests; the
        // status/cluster/ping trackers below were never freed, so a
        // long-uptime server with connection churn accumulated dead entries
        // indefinitely. Placed before the idempotent early-return so a second
        // release is also a no-op here.
        _lastStatusSector.Remove(id);
        _lastStatusTick.Remove(id);
        _lastClusterInfoTick.Remove(id);
        _lastPingQuery.Remove(id);
        _peerRequests.Remove(id);   // P480 — forget the retained LogonRequest with the peer
        _peerShipUpdate.Remove(id); // P507 — release the per-peer ship-update cookie/counters with the peer
        if (!_peerShips.Remove(id, out var live))
        {
            // Idempotent second release (LOGOFF followed by the transport's own
            // OnPeerLeft) — the peer is already gone and its ship already retired.
            _peerNames.Remove(id);
            _playerReady.Remove(id);
            return;
        }

        // M1 — the departing connection also keys the teammate-scoped caches
        // (the subject-ship dimension of the composite (recipient, ship)
        // keys), and the departing ship id keys the per-subject loadout
        // snapshot cache. Free every entry where the departing peer is the
        // recipient OR the departing ship is the subject; otherwise these
        // grow monotonically with connection churn.
        short departedShipId = live.Ship.Id;
        _teammateExportSent.RemoveWhere(e => e.Recipient == id || e.ShipId == departedShipId);
        _teammatePlayerInfoSent.RemoveWhere(e => e.Recipient == id || e.ShipId == departedShipId);
        // P349 — the cross-side export latch gets the exact same treatment: free
        // every entry where the departing peer was the recipient OR the departing
        // ship was the subject, else it grows monotonically with connection churn.
        _crossSideExportSent.RemoveWhere(e => e.Recipient == id || e.ShipId == departedShipId);
        // P453 — the cross-side MINE export latch gets the same treatment: free
        // every entry where the departing peer was the recipient (the mine-id
        // subject dimension is not ship-linked — a mine's id is unrelated to any
        // ship id, so only the recipient dimension is released here).
        _crossSideMineExportSent.RemoveWhere(e => e.Recipient == id);
        // P366 — the same-side NPC latches get the same treatment as their
        // teammates: free every entry where the departing peer was the recipient
        // OR the departing ship was the subject, else they grow monotonically
        // with connection churn (an NPC dying mid-mission frees its subjects).
        _sameSideNpcExportSent.RemoveWhere(e => e.Recipient == id || e.ShipId == departedShipId);
        foreach (var k in _lastSameSideNpcStatusTick
                     .Where(kv => kv.Key.Recipient == id || kv.Key.ShipId == departedShipId)
                     .Select(kv => kv.Key).ToList())
            _lastSameSideNpcStatusTick.Remove(k);
        foreach (var k in _teammateLoadoutVersionSent
                     .Where(kv => kv.Key.Recipient == id || kv.Key.ShipId == departedShipId)
                     .Select(kv => kv.Key).ToList())
            _teammateLoadoutVersionSent.Remove(k);
        _teammateLoadoutCache.Remove(departedShipId);
        foreach (var k in _lastTeammateStatusTick
                     .Where(kv => kv.Key.Item1 == id || kv.Key.Item2 == departedShipId)
                     .Select(kv => kv.Key).ToList())
            _lastTeammateStatusTick.Remove(k);

        // P493 — the cross-side SHIP_STATUS latches get the same treatment: free
        // every entry where the departing peer is the recipient OR the departing
        // ship is the subject (the (recipient, ship) refresh tick), and every
        // (seeing-side, ship) visibility edge whose subject is the departing
        // ship — else they grow monotonically with connection churn.
        foreach (var k in _crossSideShipStatusTick
                     .Where(kv => kv.Key.Item1 == id || kv.Key.Item2 == departedShipId)
                     .Select(kv => kv.Key).ToList())
            _crossSideShipStatusTick.Remove(k);
        foreach (var k in _crossSideShipStatusSeen
                     .Where(kv => kv.Key.ShipId == departedShipId)
                     .Select(kv => kv.Key).ToList())
            _crossSideShipStatusSeen.Remove(k);

        RetireShipOnDisconnect(live);

        // P178 — win-by-elimination (native fsmission.cpp:5371-5427 →
        // CheckForVictoryByInactiveSides). A peer just left (its ship retired),
        // which is the one moment a side can go empty of players; re-evaluate
        // whether this mission has reached its endgame state. Only on this real
        // departure path — never on the idempotent early-return above. Runs LAST
        // in this block (after succession + QUIT_MISSION, below) so those
        // announcements reach the still-original-mission peers BEFORE the win
        // check — which, when it declares an end, triggers the P439 rotation and
        // P480 migration that re-seat the surviving peers into a FRESH mission
        // (a peer already migrated is no longer an audience member of the old
        // mission). This matches native, where RemovePlayerFromSide (succession)
        // and the QUIT_MISSION send happen during removal and the
        // win-by-inactive-sides check runs later in the sim loop.
        if (live.Ship.Mission is { } mission && live.Ship.SideId is { })
        {
            // P183 — the RemovePlayerFromSide port's leader-succession
            // (fsmission.cpp:848-1099): if the departing ship was its side's
            // leader, promote a successor (or clear the role), broadcasting
            // SET_TEAM_LEADER / SET_MISSION_OWNER as needed. Runs BEFORE the
            // victory check so the surviving peers hear it while they still
            // belong to the original mission.
            HandleLeaderSuccession(live.Ship);

            // P184 — a departing player's pending side-join request(s), if any,
            // must be cleared and each broadcast as DELPOSITIONREQ(DPR_Rejected)
            // (native RemovePlayerFromMission → RemoveJoinRequest(pfsPlayer, NULL),
            // fsmission.cpp:460-525 / 4938-4960). Usually at most one per ship in
            // this port.
            foreach (var pend in _pendingJoinRequests
                         .Where(kv => kv.Value.ShipId == live.Ship.Id).ToList())
            {
                _pendingJoinRequests.Remove(pend.Key);
                BroadcastDelPositionReq(mission, pend.Value.ShipId, pend.Value.SideId, DelPositionReqReason.Rejected);
            }

            // P183 — broadcast QUIT_MISSION(124) to the whole mission. This
            // disconnect path previously sent nothing telling remaining peers
            // "ship X quit the mission" — a real, live gap now closed.
            BroadcastQuitMission(mission, live.Ship.Id, QuitSideReason.LinkDead);

            // P178 — win-by-elimination (native fsmission.cpp:5371-5427 →
            // CheckForVictoryByInactiveSides). P548 gates it with native's
            // peer-departure deactivation condition (fsmission.cpp:1147-1149):
            // a side is deactivated — which is what leads to the victory check —
            // ONLY when the mission has STARTED (stage != NotStarted) AND the
            // departing player's side just lost its last player
            // (HasPlayers(psideOld, false)). In a NotStarted lobby a drop must
            // never deactivate/end the mission; in Starting/Started it
            // deactivates only a side left empty. LAST so the succession + QUIT
            // broadcasts above reach the original-mission peers before any
            // rotation + migration re-seats them.
            short departedSideId = live.Ship.SideId.Value;
            bool departingSideEmpty = CountPlayersOnSideExcluding(mission, departedSideId, live.Ship) == 0;
            if (mission.Stage != MissionStage.NotStarted && departingSideEmpty)
                DeactivateSideForPeerDeparture(mission, mission.Sides.FirstOrDefault(s => s.Id == departedSideId));
        }

        _peerNames.Remove(id);                     // P62: release the remembered name with the peer
        _playerReady.Remove(id);                   // P180: release the peer's lobby-ready flag with it
    }

    /// <summary>
    /// P128 — retire the zombie ship a departing peer leaves in the sector.
    /// The port's analogue of native's <c>CFSShip::~CFSShip</c>
    /// (fsship.cpp:58-68), which on destruction announces
    /// <c>SHIP_DELETE(NULL, SDR_LOGGEDOFF)</c> (<i>"client is gone, so waste
    /// their ship and everything about it"</i>, Messages.h:191) and then
    /// <c>m_pShip->Terminate()</c>. Three steps:
    /// <list type="number">
    /// <item><b>Detach any boarded children</b> — native Terminate "blows away
    /// the children" by setting each <c>SetParentShip(NULL)</c>
    /// (shipIGC.cpp:243-250), which ejects the child as an independent ship,
    /// not destroys it (shipIGC.cpp:3671-3700). This port's existing detach
    /// helper is <see cref="PerformLeave"/> — valid here because a boarded
    /// carrier is always docked (board gates on carrier <c>StationId != null</c>,
    /// Server.cs:2660-2662), so its docked gate passes; the same reuse the
    /// teleport bNewHull boot makes (Server.cs:3981-3982).</item>
    /// <item><b>Broadcast <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c></b> to the peers
    /// who can see the ship — the same sector-scoped audience the kill path
    /// uses for SDR_KILLED (P103; native instead addresses the whole mission
    /// group, fsship.cpp:213-219 — noted, matching this port's SHIP_DELETE
    /// convention).</item>
    /// <item><b>Remove the ship from its sector</b> via the same
    /// <c>_models.Remove</c> the death contract uses (Sector.cs:1082/1499), so
    /// it stops flying under Mission.Update — reusing the mechanism, not a
    /// second one.</item>
    /// </list>
    /// Runs on the server thread (called from <see cref="ReleasePeer"/>), same
    /// as the kill broadcasts. A ship with no sector (e.g. a peer that never
    /// got a validated logon) has nothing to retire.</summary>
    private void RetireShipOnDisconnect(LiveShip live)
    {
        var ship = live.Ship;
        if (ship.Sector is not { } sector)
        {
            Console.WriteLine($"[logoff] ship {ship.Id} has no sector — nothing to retire");
            return;
        }

        // 1. Native "blow away the children": detach every boarded child
        //    (shipIGC.cpp:243-250) via the port's existing detach helper.
        foreach (var kid in ship.ChildShips.ToArray())
            PerformLeave(kid, ship);

        // 2. Broadcast SHIP_DELETE(20, SDR_LOGGEDOFF) to the sector audience.
        //    The departing peer is already out of _peerShips, so it is not a
        //    target — consistent with the kill path's audience construction.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == ship.Sector)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length > 0)
        {
            var delete = ShipDelete.ForLoggedOffShip(ship.Id);
            byte[] packet = FedPacket.Pack(new[] { delete.Encode() });
            foreach (var t in targets)
                SendPeer(t, packet);
        }
        Console.WriteLine($"[logoff] ship {ship.Id} — SHIP_DELETE(20) SDR_LOGGEDOFF -> {targets.Length} peer(s)");

        // 3. Remove the ship from its sector (the same _models.Remove the
        //    death contract uses, Sector.cs:1082/1499) so Mission.Update stops
        //    flying it. Nulling Sector mirrors native Terminate's
        //    GetCluster() → NULL.
        sector.Remove(ship);
        ship.Sector = null;
    }

    /// <summary>Tear down in reverse of boot: stop the transport (joining its
    /// background threads), then drop the host. Idempotent.</summary>
    public void Shutdown()
    {
        // Heartbeat constructed last in Boot → torn down first, so its best-effort
        // deregister DELETE fires before the process tears down. Independent of the
        // transport shutdown (either order is fine) — kept first for reverse-of-boot clarity.
        _heartbeat?.Dispose();
        _heartbeat = null;
        _host?.Transport?.Shutdown();
        lock (_captureLock)
        {
            _captureStream?.Flush();
            _captureStream?.Dispose();
            _captureStream = null;
        }
        Console.WriteLine($"[shutdown] clean: {LoopIterations} ticks, {Overruns} overruns");
        // P600 — flush + release the profiler's CSV handle (best-effort).
        ShutdownProfiler();
    }

    /// <summary>Dispose = <see cref="Shutdown"/>, so <c>using</c> guarantees the
    /// transport is torn down (no orphan threads) even when an assertion fails.</summary>
    public void Dispose() => Shutdown();

    /// <summary>Boot, then tick until SIGINT/SIGTERM, then shut down cleanly.
    /// Returns 0.</summary>
    public int RunAsync(string configPath, IReadOnlyList<(string Key, string Value)> cliOverrides)
    {
        Boot(configPath, cliOverrides);
        using var cts = new CancellationTokenSource();

        // On Linux use PosixSignalRegistration to intercept SIGINT (Ctrl-C) and
        // SIGTERM and cancel the loop, letting the loop break and teardown run.
        // Skip Console.CancelKeyPress there — it hooks the same signal and
        // doubling up invites ambiguity; it is the fallback for other platforms.
        if (OperatingSystem.IsLinux())
        {
            // PosixSignalContext.Cancel defaults to FALSE: .NET runs the callback
            // then lets the signal's default disposition terminate the process
            // unless we suppress it. Without ctx.Cancel = true a SIGTERM/SIGINT
            // would kill the server mid-loop and teardown would never run.
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { cts.Cancel(); ctx.Cancel = true; });
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { cts.Cancel(); ctx.Cancel = true; });
            Loop(cts.Token);
        }
        else
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Loop(cts.Token);
        }

        Shutdown();
        return 0;
    }
}
