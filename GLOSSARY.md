# GLOSSARY.md — AllegianceCore domain index

A per-domain, kept-current index of the terms, concepts and **key files** behind
the AllegianceCore port. Use it (and keep it current) instead of re-discovering
the tree on every task.

Maintenance rule (see `AGENTS.md` / lane rules): **update this glossary when you
add a system or rename/move a key file.** When a task introduces a new domain
or a new headline concept, add a one-line entry under the matching section (or
a new section) with its key files. The CI glossary-link-check script
(`tools/check-glossary-links.py`, wired into `ci.yml`) fails the build if any
relative path referenced here does not exist, so keep every cited path real.

Native reference (read-only, outside this repo): `~/Development/Allegiance/src/`.
Terms marked *native* cite the C++ they port. HUD conventions: native 2-D images
are **y-up**; a StringImage hangs **below** its point; GroupImage paints its mdl
list in **reverse**.

---

## Top-level layout

- **`AllegianceCore.slnx`** — the .NET solution (all of `src/` plus the
  tested `tools/`).
- **`src/Allegiance.Core`** — the engine: simulation, wire protocol, data
  formats, static core, map generation. Referenced by everything.
- **`src/Allegiance.Server`** — the dedicated game server (world sim, players,
  bots, broadcast).
- **`src/Allegiance.Lobby`** — the server-directory web app.
- **`src/Allegiance.ClientCore`** — headless client world-state (session,
  flight prediction, replay).
- **`src/Allegiance.ClientCore.Game`** — the MonoGame presentation layer
  (rendering, HUD, input, screens).
- **`src/Allegiance.Bot`** — the minimal BotClass client used by the MCP driver
  and multi-player bots.
- **`src/Allegiance.SinglePlayer`** — offline skirmish/training host embedded
  in the client.
- **`tools/`** — one-off proof harnesses and dev tools, mostly outside the
  solution (built by `tools/build-tools.sh`).
- **`scripts/`** — operational shell/Python scripts (dev-stack, live-proof,
  run-tests).
- **`docs/parity/`** — the CSV parity/sim-rule matrices that track native
  equivalence row by row.
- **`docs/reports/`** — one report per task (P####), the primary history of
  why things are the way they are.

---

## 1. Simulation (`src/Allegiance.Core/Sim`, `Model`)

- **World / mission** — the assembled playable game state. Key files:
  `src/Allegiance.Core/Model/Mission.cs` (`Mission.Assemble`),
  `src/Allegiance.Core/Model/Sector.cs`, `src/Allegiance.Core/Model/Side.cs`,
  `src/Allegiance.Core/Sim/Ship.cs`. *Native:* `igc.h`, `missionigc.cpp`.
- **Mission goals (P832a)** — native numeric MissionParams goal fields and
  predicates in `src/Allegiance.Core/Model/MissionGoals.cs`; per-game
  `games[].goals` resolves in `src/Allegiance.Server/ServerConfig.cs` and flows
  through assembly, `src/Allegiance.Core/Wire/MissionDef.cs`, lobby metadata and
  shared briefing text. `src/Allegiance.Core/Model/Mission.GoalObjects.cs`
  creates native initial flag pedestals/flags and neutral artifacts. Absent goals
  preserve the port's countdown/finale policy; `NativeDefaults()` explicitly
  selects native Reset's 100% conquest. Report: `docs/reports/P832A_GAME_GOALS.md`.
- **Mission registries and core ownership (P869)** — live insertion-ordered,
  identity-preserving object registries with O(1) membership/deletion, lazy
  model-owned site wrappers, and the separate team-lobby side in
  `src/Allegiance.Core/Model/Mission.Registry.cs`; shutdown/end-of-tick migration retirement
  in `src/Allegiance.Core/Model/Mission.Lifetime.cs`; shared named binary-core
  leases in `src/Allegiance.Core/Model/StaticCoreCache.cs`, also owned by
  `src/Allegiance.ClientCore/State/ClientWorld.MissionEntry.cs` and released on
  reset/session disposal. Client file revisions include mtime in the cache key.
  Tests editing shared constants use
  `tests/Allegiance.Core.Tests/CoreConstantOverride.cs` to clone them. Native:
  `Igc/missionigc.cpp:3033-3053,3506-3732`, `Igc/igc.h:5130-5197`.
  Waypoint command ownership lazily materializes `src/Allegiance.Core/Sim/Buoy.cs`
  when the registry is read; allocation/lifetime proofs: `tests/Allegiance.Core.Tests/P869bReviewTests.cs`.
  Evidence: `tests/Allegiance.Core.Tests/P869OwnershipTests.cs` and
  `docs/parity/p869-ownership.csv`; report `docs/reports/P869_REPORT.md`.
- **Static-core text fallback (P908)** — binary-first server/client core loading
  falls back to `<core>/Constants.csv` and native text version 1337 through
  `src/Allegiance.Core/Model/StaticCoreCache.cs`. The shared AOT-safe codecs in
  `src/Allegiance.Core/StaticCoreText/StaticCoreRoundtrip.cs` also serve
  `tools/core2txt/`; runtime validates complete tables before publishing a core.
  `src/Allegiance.ClientCore.Game/Assets/TextCoreAssets.cs` gives the game client's
  asset-source resolver the same fallback.
  `src/Allegiance.ClientCore/State/ClientMissionCoreCache.cs` tracks text revisions
  independently; `src/Allegiance.ClientCore/State/ClientWorld.MissionEntry.cs`
  refuses mismatched text/binary versions at join. Native:
  `Igc/missionigc.cpp:2390-2431,2637-2675`, `clintlib/clintlib.cpp:4212-4221`.
  Proof: `tests/Allegiance.Core.Tests/P908TextCoreTests.cs`,
  `tools/p908-text-core-proof/`; report: `docs/reports/P908_REPORT.md`.
- **File-map generation top-up (P864c / SIM-0748)** —
  `src/Allegiance.Core/Model/Mission.FileMap.cs` replaces unavailable station
  types from original local/effect plus side development techs, then fills only
  empty asteroid/treasure lists. Shared asteroid placement/He3 budgeting lives
  in `src/Allegiance.Core/MapGen/MissionMapGenerator.cs`; file-map resource
  defaults come from native Reset. Native: `Igc/missionigc.cpp:3977-4055`.
  Report: `docs/reports/P864c_REPORT.md`.
- **N-side showcase fleets (P927, opt-in bot policy)** — `spawn_scenario.sides`
  validates 2–6 actual mission sides in `tools/allegiance-mcp/ScenarioSideSpec.cs`;
  `tools/allegiance-mcp/MultiSideEnemy.cs` ranks reachable living observed enemies
  for waves, finale and aleph duties. Scout intel keeps all living threats;
  targetless fogged finales can arm a deadline. Worker plans/results and recovery
  retain every side; legacy two-side shapes stay pinned. Native server victory
  and deadline loops were already N-side. Operator reference: `docs/GUIDE.md`;
  report: `docs/reports/P927_REPORT.md`.
- **Per-match showcase map rotation (P926, preset policy)** — default-off
  `showcase.mapRotation` chooses file or generated entries before every configured
  world's assembly. `src/Allegiance.Server/MapRotationConfig.cs` preflights all
  entries and owns independent seeded streams; file-map asteroid top-ups also use
  the receipt's seed. `tools/allegiance-mcp/ShowcaseBattleSector.cs` derives a
  shared neutral rendezvous from public topology and re-resolves it each match;
  `tools/allegiance-mcp/BotManager.Exploration.cs` resets adapted fleets' scout intel on client mission generations.
  `tools/p819-side-bias-proof/matchups.py` groups results by map. Operator reference:
  `docs/GUIDE.md`; report: `docs/reports/P926_REPORT.md`. No new native rule.
- **Showcase match cap (P930, preset policy)** — `showcase.matchCapSeconds`
  defaults to 3600 simulation seconds (`0` off), applied after goal selection in
  `src/Allegiance.Server/Server.Lifecycle.cs` on boot and every fresh rebuild.
  A shorter authored countdown wins; research finales clamp to its end in
  `src/Allegiance.Server/Server.Showcase.cs`. Native `dtGameLength`
  (`Igc/igc.h:1162,1526-1528`) drives the ordinary clock and timeout ranking
  (`FedSrv/fsmission.cpp:2337-2504`), with the P914 station-kill preset option.
  Cap receipts come from `src/Allegiance.Server/Server.Periodic.cs`; tests:
  `tests/Allegiance.ClientCore.Tests/P930MatchCapTests.cs`,
  `tests/Allegiance.ClientCore.Tests/P930MatchCapLiveTests.cs`.
  Report: `MATCH_CAP_P930.md`.
- **Per-match goal rotation (P832g, preset policy)** — opt-in
  `showcase.goalRotation` (`cycle`/seeded `random`, no consecutive repeats),
  `src/Allegiance.Server/GoalRotationConfig.cs`; selects creator parameters before
  each fresh world/map in `src/Allegiance.Server/Server.Lifecycle.cs`, logs exact
  values on `[matchup]`, and uses ordinary MISSIONDEF/briefing for bots/clients.
  `SkirmishOptions.GoalRotation` forwards the same preset; absent preserves fixed
  goals. Example `tools/p832g-goal-rotation.json`; skill coverage joins goals by
  match in `scripts/showcase-skills.py`. Report: `docs/reports/P832G_GOAL_ROTATION.md`.
- **Friendly fire / allied visualization options (P856)** — the two native
  create-game `MissionParams` booleans, default off: `bAllowFriendlyFire`
  (`Igc/igc.h:1149`) and `bAllowAlliedViz` (`Igc/igc.h:1127`). Per-game
  `games[].allowFriendlyFire` / `games[].allowAlliedViz` resolve in
  `src/Allegiance.Server/ServerConfig.cs` (env
  `ALLEGIANCE_GAME_ALLOW_FRIENDLY_FIRE` / `ALLEGIANCE_GAME_ALLOW_ALLIED_VIZ`,
  CLI `--friendly-fire` / `--allied-viz`) and land on
  `Mission.AllowFriendlyFire` / `Mission.AllowAlliedViz` at assembly. Consumers:
  `src/Allegiance.Core/Sim/BlastDamage.cs` (ship/station/missile/probe damage),
  `src/Allegiance.Core/Sim/Probe.cs` (native armed-probe immunity + firing
  reveal), `src/Allegiance.Core/Model/Sector.cs` (allied visibility/scans) and
  `src/Allegiance.Core/Model/Mission.cs` (`RevealAlliedClusters`,
  `AreSidesFriendly` over live `Side.AllyGroup`). Advertised in
  `src/Allegiance.Core/Wire/MissionDef.cs` bits 28/6 and in the lobby directory
  game settings (client GAME DETAILS panel). Report:
  `docs/reports/P856_GAME_OPTIONS_FF_ALLIEDVIZ.md`.
- **Static core** — the parsed `PCore001.igc` asset: civilizations, hulls,
  parts, stations, asteroids, float constants. Key:
  `src/Allegiance.Core/Model/StaticCore.cs`,
  `src/Allegiance.Core/IgcReader.cs`, `src/Allegiance.Core/IgcFile.cs`.
- **Encrypted native core (P868)** — `src/Allegiance.Core/IgcReader.cs`
  decodes the cumulative DWORD XOR before walking body records. File loading
  uses explicit format selection (`ServerConfig.StaticCoreEncrypted` /
  JSON `staticCoreEncrypted`, default false); decrypted files can retain the
  native `zone_core.igc` name. Version/size prefixes stay plain. Native:
  `FedSrv/FedSrv.CPP:31-56,9304,9763`; tests:
  `tests/Allegiance.Core.Tests/P868EncryptedCoreTests.cs`.
- **Ship** — player/bot vessel: flight, weapons, shields, cargo. Key:
  `src/Allegiance.Core/Sim/Ship.cs`. *Native:* `shipigc.cpp`, `fsship.cpp`.
- **Wingman defend acquisition (P952 audit)** — a guard needs a damaging
  primary weapon before acquiring threats near its protectee (native
  `Igc/shipIGC.cpp:2680-2695`); the return-to-defend rule is at `:2373-2406`.
  `src/Allegiance.Core/Sim/Ship.cs` implements it; armed/unarmed coverage lives
  in `tests/Allegiance.Core.Tests/P872NativeBehaviourTests.cs` and the armed
  movement proof in `tests/Allegiance.Core.Tests/NpcWingmanDefendLiveTests.cs`.
  P872 bisect and fixture correction: `docs/reports/NPC_DEFEND_BISECT_P952.md`.
- **Drone stock loadout (P956)** — every spawned drone carries its hull's
  preferred parts: `Ship.MountPreferredParts` (`src/Allegiance.Core/Sim/Ship.Equipment.cs`)
  is native `CreateDrone`'s fill (`Igc/common.cpp:2462-2480`), called by
  `src/Allegiance.Server/Server.cs` `SpawnNpcShip` for every production spawn
  (constructors, completed drone types/purchased drones, boot/rotation miners).
  Parts reach clients through the same-side NPC `EXPORT` (P366), the cross-side
  identity `EXPORT` (P349) and the spawn-time `LOADOUT_CHANGE` to same-side
  in-sector peers (`src/Allegiance.Server/Server.Combat.cs`,
  `FedSrv/fsship.cpp:511-542`). Proof:
  `tests/Allegiance.Core.Tests/DroneSpawnLoadoutTests.cs`;
  report `docs/reports/DRONE_LOADOUT_P956.md`.
- **Flight model / physics** — acceleration, drag, turn rates, afterburner.
  Key: `src/Allegiance.Core/Sim/` (`Acceleration`, `Physics`, `TurnRate`…).
  *Native:* `shipIGC.cpp`, `rotvel.cpp`.
- **Native NPC GotoPlan (P817/P875, production default)** — pure waypoint masks, reverse-drag
  stopping distance, warp route search, directional dodge/diversion, angular
  stopping drift, boost handling and builder/layer completion: `src/Allegiance.Core/Sim/NativeGotoPlan.cs`,
  `src/Allegiance.Core/Sim/Ship.NativeGotoPlan.cs`. Per-mission flag
  `sim.nativeGotoPlan` / `ALLEGIANCE_SIM_NATIVE_GOTOPLAN` defaults on in
  `src/Allegiance.Server/SimConfig.cs`; the environment overrides configuration.
  `src/Allegiance.Server/Server.Lifecycle.cs` binds it to each assembled mission.
  `docker-compose.yml` still explicitly defaults the environment override to `0`;
  see the deployment caveat in `docs/reports/P903_REPORT.md`.
  Native: `Igc/common.cpp:1157-2333`, `Igc/shipIGC.cpp:2795-2833`.
  Local A/B and independent C++ formula trace: `tools/p817-gotoplan-proof/`;
  report `docs/reports/P817_NATIVE_GOTOPLAN.md`.
- **Experimental native tick order (P906)** — independent, default-off server JSON
  `sim.nativeShipPhases` and `sim.nativeCollisionQueue` bind to each mission in
  `src/Allegiance.Server/Server.Lifecycle.cs`. Ship-wide preplot/plot/execute
  helpers live in `src/Allegiance.Core/Sim/Ship.TickOrder.cs`; the common swept
  contact timeline, bounce recalculation and final movement live in
  `src/Allegiance.Core/Model/Sector.TickOrder.cs`; zero-duration boundary tests
  use `src/Allegiance.Core/Collision/NarrowPhase.cs`. Native:
  `Igc/clusterIGC.cpp:170-345,592-706`, `_Utility/HitTest.cpp:187-202`. Existing `sim.nativeBroadcastOrder`
  remains separate and default off (`FedSrv/FedSrv.CPP:4237-4254`). All three
  rows remain partial pending the rig decision; limitations and the local
  200-client proof (`tools/p906-tick-order-proof/`) are recorded in
  `docs/reports/P906_REPORT.md`.
- **Discovery-aware server routing (P923)** — default-off server JSON
  `liveSettings.seenWarpRouting` binds `Mission.SeenWarpRoutingEnabled`.
  `src/Allegiance.Core/Model/Sector.cs` gates every legacy BFS edge through the
  ship's native personal/side visibility. `src/Allegiance.Core/Sim/Ship.WarpNavigation.cs`
  refreshes legacy and native NPC route caches on per-side warp discovery
  revisions or changed local scanner eligibility. Revisions include partner
  reveal and retain predictable visibility. Native GotoPlan already gates
  visibility with this flag off; only its cache refresh changes when enabled.
  Native: `Igc/common.cpp:920,987`, `Igc/shipIGC.h:341-389`,
  `FedSrv/FedSrv.CPP:10783-10799,11124-11126`. Tests:
  `tests/Allegiance.Core.Tests/P923SeenWarpRoutingTests.cs`,
  `tests/Allegiance.Core.Tests/P923SeenWarpRoutingLiveTests.cs`; measurements:
  `tools/p923-routing-proof/Program.cs`; report: `docs/reports/P923_REPORT.md`.
- **Mission replay identity (P903)** — first generation and rotation bookkeeping
  in `src/Allegiance.Server/Server.Lifecycle.cs` and
  `src/Allegiance.Server/Server.Lobby.cs` feeds `Mission.ReplayCount` and cached
  `GetContextName` in `src/Allegiance.Core/Model/Mission.cs`; the native context
  travels as `LobbyGameResult.GameId` on career reports. Native:
  `Igc/missionigc.cpp:3846-3857,4211-4238`, `FedSrv/fsmission.cpp:2766`.
  Production regression: `tests/Allegiance.Core.Tests/P903ProductionReachabilityTests.cs`.
- **Live generic model lookup / economic tick order (P904)** —
  `src/Allegiance.Core/Model/Mission.Lookup.cs` resolves the eight native
  command-target kinds from live registries; the server's chat/drone order
  resolver uses it so static records cannot override registry deletions.
  `src/Allegiance.Core/Model/Mission.cs` samples payday income after movement;
  `src/Allegiance.Server/Server.Broadcast.cs` resolves timeout before the
  Started-only ballot tail. Native: `Igc/missionigc.cpp:3386-3416`,
  `FedSrv/FedSrv.CPP:8245-8278`, `FedSrv/fsmission.cpp:2508-2592`.
  Certification (including independent old/new static-core consumer retirement):
  `tests/Allegiance.Core.Tests/P904CertificationTests.cs`,
  `docs/parity/p904-certification.csv`, `docs/reports/P904_REPORT.md`.
- **Cross-side visibility sweep (P131/P797/P835)** — the every-10th-pass
  scanner×target sweep behind `SeenBySide` (`Sector.ResolveCrossSideVisibility`,
  `Sim/VisibilityMath.cs`, `Sim/SideVisibilityState.cs`). P835 adds the sweep's
  sub-step timers (`vis.enumerate`/`vis.scan`/`vis.write`), a conservative
  exact-result occlusion pre-filter in `LineOfSightExist`, and the native
  last-spotter short-circuit (`FedSrv.CPP:10896-10903`, sticky spotter identity
  `FedSrv.CPP:11026`) — unconditional since P858 (the rig A/B rejected the
  `sim.visibilitySpotterCache` on/off flag; the identity rule is native
  behaviour and is wire-observable in OBJECT_SPOTTED). Clash-peak cost
  analysis + bench: `tools/P835Bench/`, report
  `docs/reports/P835_CLASH_PEAK_COST.md`; P858 flag removal:
  `docs/reports/P858_DROP_REJECTED_FLAGS.md`.
- **Spotting side effects (P851)** — what native books on the false→true edge of
  a side's own scanner: the first-sight score credits (`AddTargetSpot` for a
  high-value hull with its 50 s `RecentlySpotted` latch, `SpotWarp` /
  `SpotSpecialAsteroid`), the station `HandleNewEnemyCluster` miner re-command,
  and the rule that docked/boarded ships are neither scanners nor targets.
  Raised from `Sector.ResolveCrossSideVisibility`'s `FirstSightEffects` through
  `IIgcSite.SpotterCreditEvent` (credited in `Server.Combat.cs OnSpotterCredit`),
  with `Ship.MarkPreviouslySpotted`/`RecentlySpotted`. Report:
  `docs/reports/P851_CERTIFY.md`.
- **Mounted part** — an equipped part instance (weapon/shield/etc.) with its
  mount and state. `src/Allegiance.Core/Sim/MountedPart.cs`. *Native:*
  `mountc.cpp`.
- **Projectile / weapon** — guns and their rounds. `src/Allegiance.Core/Sim/Weapon.cs`,
  `Projectile.cs`, `BlastDamage.cs`. *Native:* `weaponigc.cpp`, `projectileigc.cpp`.
- **Lead solve** — the shared ballistic lead/intercept solve
  (`solveForLead` → `solveForImpact`), used by the AI *and* the client HUD.
  `src/Allegiance.Core/Sim/LeadSolver.cs` (pure), `ShipDodgeLaw.cs`
  (`SolveForImpact`). *Native:* `Igc/common.cpp:59-149`,
  `Igc/shipIGC.cpp:2464,2617`. Report P804.
- **Missile** — homing ordnance. `src/Allegiance.Core/Sim/Missile.cs`,
  `MissileIntercept.cs`. *Native:* `missileigc.cpp`.
- **Mine / probe / chaff** — deployable equipment.
  `src/Allegiance.Core/Sim/Mine.cs`, `Probe.cs`, `Chaff.cs`. *Native:*
  `mineigc.cpp`, `probeigc.cpp`, `chaffigc.cpp`. Native mine model radius is
  `DataMineTypeIGC.MineRadius` (derived tail field); the shadowed inherited
  `Radius` can contain uninitialized data and must not drive artifact clearance
  (`src/Allegiance.Core/Model/Mission.Treasure.cs`, P832d).
- **Asteroid** — ore-bearing rocks, mined for He3. `src/Allegiance.Core/Sim/Asteroid.cs`,
  `src/Allegiance.Core/Data/NativeAsteroidTypes.cs` (type table).
  *Native:* `asteroidigc.cpp`.
- **Station / construction** — bases and their build queue.
  `src/Allegiance.Core/Sim/Station.cs`, `ConstructionPhase.cs`,
  `BuildingEffect.cs`. *Native:* `stationigc.cpp`, `construction.cpp`.
- **Docking / landing bays** — the native InGarage geometric tests.
  `src/Allegiance.Core/Sim/DockApproach.cs`,
  `src/Allegiance.Core/Collision/HullLandingBayCache.cs`. *Native:* `dock.cpp`.
- **Treasure / loot** — dropped items and pod rescue.
  `src/Allegiance.Core/Sim/Treasure.cs`,
  `src/Allegiance.Core/TreasureMountPicker.cs`. *Native:* `treasureigc.cpp`.
- **Economy** — money, payday, donation, refunds.
  `src/Allegiance.Core/Sim/` (`Money`, `Payday`…), server side
  `src/Allegiance.Server/Server.Economy.cs`.
- **Time / ticking** — fixed-rate simulation loop and time dilation.
  `src/Allegiance.Server/Server.Periodic.cs`,
  `src/Allegiance.Core/Sim/TimeDilationController.cs`. *Native:* the
  20/60 updates-per-second heartbeat.
- **Tick profiler** — the feature-flagged per-window profiler for the server
  tick: `[tickprof]` per-bucket mean/p95/max lines, P619 `[slowtick]` capture,
  P838 `[gcprof]` per-window GC pause/collection/allocation line with per-step
  allocation attribution. `src/Allegiance.Core/Profiling/TickProfiler.cs`;
  env `ALLEGIANCE_TICK_PROFILER`, `ALLEGIANCE_TICK_PROFILER_INTERVAL`,
  `ALLEGIANCE_SLOW_TICK_MS`. Report: `docs/reports/P838_GC_PROFILE.md`.
- **Broadcast byte / update-age telemetry (P983)** — the default-off `profiler.broadcastBudget`
  diagnostic: per-peer + server-wide rolling 5 × 1 s byte/frame counters by category (heavy
  ship update, light/ranked pose, reliable/keyed, chat/lobby, other) and the newest pose age
  (sim ms) per peer per visible ship, reported on the P600 profiler window/CSV.
  `src/Allegiance.Server/BroadcastTelemetry.cs`, `src/Allegiance.Server/Server.BroadcastTelemetry.cs`;
  the categories are tagged at the send sites in `src/Allegiance.Server/Server.BroadcastSnapshot.cs`,
  `src/Allegiance.Server/Server.RankedPose.cs`, `src/Allegiance.Server/Server.Broadcast.cs`,
  `src/Allegiance.Server/Server.InterestTiers.cs`, `src/Allegiance.Server/Server.Callbacks.cs`;
  env `ALLEGIANCE_BROADCAST_TELEMETRY`. Report: `docs/reports/BROADCAST_TELEMETRY_P983.md`.
- **Broadcast lane breakdown + stale-age reasons (P990)** — the same flag, opened up: `[bb-lane]` /
  `[bb-other]` (ordered one-shot lane per wire *fmid*, read off each frame's own headers at the sink) /
  `[bb-keyed]` (coalescing lane per coalesce-key family) / `[bb-age-why]` (why each pair older than 3 s or
  never posed is stale: docked, dead, lifepod, parented, otherSector, viewerNotSwept, stationary,
  starved, subjectUnknown, newPair, unserved), plus the `PoseStampTable` dense per-peer stamp table that
  replaced P983's `Dictionary<short,double>`.
  `src/Allegiance.Server/BroadcastTelemetry.cs` (`PoseStampTable`, lane rings, `RecordSendFrame`),
  `src/Allegiance.Server/Server.BroadcastTelemetry.cs` (`StaleReason`, `ClassifyStale`, `AddLaneReport`,
  `AddAgeReport`), `src/Allegiance.Server/Server.UpdateCadence.cs` (`TrackedShipIds`).
  Report: `docs/reports/BROADCAST_SLICE_D_P990.md`.
- **SHIP_STATUS change gate (P991)** — default-off `broadcast.shipStatusChangeGate` that makes every
  `FM_S_SHIP_STATUS(90)` send edge-triggered on the wire content, native's per-frame rule
  (`FedSrv/FedSrv.CPP:3665-3719`: per side, per ship, `if (*pss != *pssOld)`), replacing the port's three
  periodic refreshes (own ship 40 ticks, same-side teammates 100 ticks per recipient phase, cross-side seen
  enemies 100 ticks per pair phase) and adding native's join-time per-ship dump
  (`FedSrv/fsmission.cpp:4303-4323`). State is one content value per (side, subject) — native's
  `m_rgShipStatus` shape — dropped at departure, park, migration, rotation and id reuse.
  `src/Allegiance.Server/Server.ShipStatusGate.cs` (`ShipStatusContent`, `StatusGateCrossSide`,
  `SendStatusDumpToJoiner`), the decision sites in `src/Allegiance.Server/Server.Broadcast.cs`
  (`SweepShipDetection`, `BroadcastShipStates*`, `SweepCrossSideShipStatuses`) and the pooled encoders in
  `src/Allegiance.Server/Server.BroadcastBuffers.cs`; env `ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE`.
  Flag off is byte-identical to the pre-P991 wire (pinned).
  Report: `docs/reports/SHIP_STATUS_CHANGE_GATE_P991.md`.
- **Compact gunfire batches (P994, default off)** — `broadcast.gunfireBatch` /
  `ALLEGIANCE_BROADCAST_GUNFIRE_BATCH` replaces selected 12/213 deliveries with
  ordered `GUNFIRE_BATCH(221)` records. Codec: `src/Allegiance.Core/Wire/GunfireBatch.cs`;
  reusable recipient builders and shared encoding: `src/Allegiance.Server/Server.GunfireBatch.cs`.
  Source pose dependencies and audiences remain in `src/Allegiance.Server/Server.FireAudience.cs`.
  Both client paths expand to legacy message objects; exact IDs, spawn position
  and lifespan preserve own-echo and bot attribution. Native array/compact-value
  inspiration: `Inc/Messages.h:890-900`, `Igc/igc.h:2198-2255`.
  Report: `docs/reports/GUNFIRE_BATCH_P994.md`.
- **Gunfire traffic study (P992)** — why the port relays every shot and hit and what a change would save:
  `PROJECTILE_INSTANCE(12)` is one 58-byte frame per shot per peer in the shooter's sector (native has no such
  server→client message: its clients fire from the `stateM` fire bits their ship updates carry), `PROJECTILE_HULL_HIT(213)`
  is one 48-byte frame per landed round to the same audience, and the fire relay also drags a
  `SINGLE_SHIP_UPDATE(78)` 0.5 s pose refresh plus a first-sight `EXPORT(26)`/`SHIP_STATUS(90)` — 112 B per shot per
  recipient, 33 % of egress on a rig-shaped fixture. Send sites `src/Allegiance.Server/Server.Combat.cs`
  (`BroadcastProjectileInstance`, `BroadcastProjectileHullHit`), audience `src/Allegiance.Server/Server.FireAudience.cs`
  (`BroadcastShipEmission`, `EnsureEmissionPose`), consumers `src/Allegiance.ClientCore/State/ClientWorld.cs` +
  `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` / `Rendering/HullHitSparkParticles.cs` (the 5 900 u
  `ProjectileVisibility` radius) and `tools/allegiance-mcp/BotManager.cs` (own-hit credit from 213). Offline
  calculator + loopback fixture: `tests/Allegiance.Core.Tests/P992GunfireTrafficTests.cs`.
  Report: `docs/reports/GUNFIRE_TRAFFIC_P992.md`.
- **Pool scheduling / UDP handoff metrics (P907)** — profiler-gated, process-wide
  `[thread-pool]` counters and a reusable once-per-second queue-delay probe in
  `src/Allegiance.Core/Profiling/ThreadPoolTelemetry.cs`; host UDP callback-entry
  to dedicated-worker delays in `src/Allegiance.Core/Transport/UdpTransport.ReceiveDelay.cs`.
  `src/Allegiance.Server/Server.SchedulingMetrics.cs` emits both on profiler windows,
  including embedded hosts via the existing client log tee. Kernel arrival time
  is unavailable; the probe is a scheduling proxy. `profiler.schedulingMetrics`
  disables these additions for cost A/B only. Proof: `tools/p907-scheduling-proof/`;
  independent sampled GCAllocationTick totals (proof only) in
  `tools/p895-transport-proof/AllocationEventMeter.cs` cross-check decreasing
  runtime cumulative allocation counters without changing GC settings;
  meaning and rig plan: `docs/reports/P907_REPORT.md`, `docs/UDP_TRANSPORT.md`.
- **Sector collision and model scratch (P928)** — each sector owns a leased
  `src/Allegiance.Core/Collision/CollisionDetector.cs` with resettable collider
  slots (`src/Allegiance.Core/Collision/Collider.cs`) and reusable geometry/result
  buffers. `src/Allegiance.Core/Model/Sector.Scratch.cs` captures fresh ordered
  model membership for each pass; disposal clears references and nested callbacks
  use independent storage. Weapon updates retain a stable per-ship turret snapshot.
  No simulation rule or flag changes. Pre-refactor 500-tick byte-identity fixture:
  `tests/Allegiance.Core.Tests/Fixtures/P928Golden.csv`; replay and allocation
  reporter: `tools/p928-allocation-proof/`; report `docs/reports/P928_REPORT.md`.
- **Random / determinism (P958)** — `Mission.Random` in
  `src/Allegiance.Core/Model/Mission.cs` defaults to the ambient generator;
  `Server.SimulationRandom` in `src/Allegiance.Server/Server.cs` optionally shares
  a seeded stream across assembly, ejection, warp exits, separation, launches,
  drops, chaff, complaints, weapons, probes and treasures. Existing map-seed
  precedence stays in `src/Allegiance.Core/MapGen/MapRandom.cs`; an explicitly
  seeded server also seeds otherwise-unseeded map resource generation.
  `tests/Allegiance.Core.Tests/P958SimulationRandomTests.cs` compares complete
  200-pod telemetry; `tools/p958-pod-load.py` sweeps seeds and records traces.
  Native shared `rand`/`srand`: `zlib/zmath.h:114-129`,
  `FedSrv/FedSrv.CPP:10245`. Report: `docs/reports/POD_LOAD_P958.md`.

## 2. Wire / FEDMSG (`src/Allegiance.Core/Wire`)

- **FEDMSG** — the fixed-function message-format id numbers that tag every
  packet. `src/Allegiance.Core/Wire/FedMsgCrc.cs`,
 `src/Allegiance.Core/Wire/NotSupportedFedMsgs.cs`. *Native:*
 `~/Development/Allegiance/src/ObjEdit/FedMsgs.h`. Audit:
 `docs/parity/fedmsg-wiring.csv`,
 `docs/reports/P627_FEDMSG_WIRING_AUDIT.md`.
- **Codec (message)** — one file per wire message, e.g. `AsteroidsUpdate.cs`,
  `ShipUpdate.cs`, `ChatMessage.cs`, `MissionDef.cs`, `ClusterInfo.cs`,
  `MineWire.cs`, `ProjectileBroadcast.cs`. Under `src/Allegiance.Core/Wire/`.
  *Native:* each ports a `Msg*.cpp` handler.
- **CaptureStream** — the binary session capture/encrypt framing.
  `src/Allegiance.Core/Wire/CaptureStream.cs`.
- **Message CRC** — crc32 of the plaintext message, the modern integrity
  mechanism. `src/Allegiance.Core/Wire/FedMsgCrc.cs`.
- **The wire-data HUD source** — the native values the HUD needs, read the
  native way (P786). `src/Allegiance.ClientCore.Game/Rendering/HudWireValues.cs`,
  report `docs/reports/P786_HUD_WIRE_DATA.md`.

## 3. Server (`src/Allegiance.Server`)

- **Boot / config** — arg parsing and config load/override (`--config`,
  `--key value`, `--map-seed`). `src/Allegiance.Server/Program.cs`,
  `src/Allegiance.Server/ServerConfig.cs` (`ServerConfigLoader.ApplyCli`).
- **Lifecycle / mission assembly** — `Server.Lifecycle.cs` builds missions from
  `GameConfig` (map load **or** `mapGen`), `Server.Periodic.cs` ticks the world.
- **Lobby heartbeat** — announces the server to the lobby directory.
  `src/Allegiance.Server/LobbyHeartbeat.cs`. Publication is driven by native's
  dirty flag: `Server.MarkLobbyDirty`/`LobbyDirtyVersion` bumps on every native
  `SetLobbyIsDirty` call site and the heartbeat republishes on the first tick
  after the 10 s coalesce (`FedSrv/fsmission.cpp:5606-5628`).
- **Lobby mission info (P866)** — the full `pfmLobbyMissionInfo` payload
  (player/noat counts, per-team caps, slot-availability scan, armed countdown
  offset, squad game, eject pods, invulnerable stations, playing squads) on the
  register payload: `src/Allegiance.Server/Server.MissionParams.cs`
  (`SnapshotLobbyGameParams`, `LobbyMissionLive`), `LobbyHeartbeat.cs`
  (`LobbyGameParams`), `src/Allegiance.Lobby/ServerListing.cs`
  (`GameParamsDto`/`GameParamsRequest`). Native:
  `FedSrv/fsmission.cpp:3983-4096`.
- **Mission cookies / chosen-game joins (P883)** — server-lifetime unique
  `Mission.Cookie` addresses each configured or provisioned game; resets retain
  the cookie. `src/Allegiance.Server/Server.MissionCookies.cs` resolves native
  LOGONREQ `dwCookie` (NA → first), and pending peers retain their selected
  mission. Registry `missions[]` and `/api/games` ready acknowledgements carry
  the cookie through both client join flows. Reconnect uses the optional tagged
  `LogonRequest.ResumeToken` extension; side choice remains POSITIONREQ.
  `src/Allegiance.Server/Server.Broadcast.cs` and
  `src/Allegiance.Server/CrossSideSweepCache.cs` scope audiences and topology
  caches by mission so local side/cluster ids cannot cross game boundaries.
  Native: `FedSrv/FedSrv.CPP:489-515`, `FedSrv/fsmission.h:229-235`.
  Proof: `tests/Allegiance.Core.Tests/P883MissionCookieTests.cs`,
  `tools/p883-join-proof/`; report: `docs/reports/P883_REPORT.md`.
- **Native server counters (P866)** — `cTimeouts`/`cPacketsOut`/`cBytesOut` and
  their per-second twins as named, thread-safe counters fed where the port
  hands guaranteed frames to the per-peer send queue and where an unrequested
  connection loss retires a player; reported on a `[server-counters]` window
  line. `src/Allegiance.Core/Transport/ServerCounters.cs`,
  `src/Allegiance.Server/Server.Egress.cs`. Native:
  `FedSrv/counters.h:10-21`, `FedSrv/FedSrv.CPP:4558-4562,4613-4652`.
- **Event audiences** — every KILL/MINE broadcast's recipient set is named after
  the native group it ports: `Server.RealSideAudience` (native
  `GetGroupRealSides()`, `FedSrv/fsmission.h:361`, seated players only —
  `fsship.cpp:1073-1078`) for whole-mission KILL notices, and the per-side eye
  gate (`Asteroid.GetCurrentEye`) for `ASTEROID_MINED`. `Server.FireAudience.cs`,
  `Server.Combat.cs`, `Server.Research.cs`. Native: `FedSrv/FedSrv.CPP:11429-11457,
  12454-12483,12512-12540`.
- **Discovery responder** — the P731 UDP "Find Server" responder.
  `src/Allegiance.Server/Discovery/`.
- **Broadcast** — sending ship/asteroid/station updates to peers, coalesced and
  interest-tiered. `Server.Broadcast.cs`, `BroadcastSnapshot.cs`,
  `Server.InterestTiers.cs`.
- **Boarded source pose (P891)** — launch copies the parent's guaranteed
  `SET_CLUSTER` to its passengers (`src/Allegiance.Server/Server.Docking.cs`);
  `src/Allegiance.ClientCore/State/ClientWorld.cs` keeps child identity/cookie
  while leaving the hangar. Airborne children receive a separate parent slot
  in the existing HEAVY packet outside the remote quota, shared by
  `src/Allegiance.Server/Server.RankedPose.cs`,
  `src/Allegiance.Server/Server.Broadcast.cs` and
  `src/Allegiance.Server/Server.InterestTiers.cs`. This cadence is an explicit
  renderer adaptation: native ranks the parent and locally simulates it.
  Native phases/classification: `docs/parity/p891-boarded-pose.csv`;
  report: `docs/reports/P891_REPORT.md`.
- **Turret relay (one-shot "last update kind")** — a boarded gunner's accepted
  `ACTIVE_TURRET_UPDATE(99)` is stored (`Server._lastTurretUpdates`, native
  `m_atu`) and relayed to every other cluster player by exactly ONE
  ship-update pass (`Server._pendingTurretRelays`, native `m_ucLastUpdate` +
  `ResetLastUpdate`), then consumed; active/inactive selection uses that kind,
  not the weapons state. `Server.ShipCommands.cs`
  (`RecordAcceptedActiveTurretUpdate`), `Server.Broadcast.cs`
  (`CollectActiveTurretRelay`), `Server.NativeBroadcastOrder.cs`
  (`CollectInactiveTurretRelay`, `ConsumePendingTurretRelays`). Native:
  `FedSrv/FedSrv.CPP:3774-3796,4049-4071`, `FedSrv/fsship.h:290-293`. Report:
  `docs/reports/P822_TURRET_RELAY_ONESHOT.md`.
- **Native broadcast order (P788c, EXPERIMENTAL, default off)** —
  `sim.nativeBroadcastOrder` / `ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER` moves
  started-mission ship/status projection before `Mission.Update`, retaining
  the per-viewer pose floor and detached encode caches; the ship phase publishes
  before simulation and complementary same-tick streams merge in
  `src/Allegiance.Server/BroadcastSnapshot.cs`:
  `src/Allegiance.Server/Server.NativeBroadcastOrder.cs`. Native:
  `FedSrv/FedSrv.CPP:4237-4254`. Report:
  `docs/reports/P788C_NATIVE_TICK_ORDER_2.md`.
- **Warp re-entry handshake** — players leave `NoUpdate` and then `Waiting`
  only on accepted ship updates (`FedSrv/fsship.cpp:1173-1190`), handled by
  `src/Allegiance.Server/Server.Dispatch.cs`. Controls-only scripted pilots
  echo their latest pose with a fresh PING clock through the same validation:
  `src/Allegiance.Core/Wire/ScriptedWarpHandshake.cs`, used by Bot and
  `src/Allegiance.ClientCore/Session/ConnectionSession.cs`.
- **Native bot ownership / standalone IDs / map catalog (P869)** —
  `src/Allegiance.Server/Server.NativeOwnership.cs` admits owner transfer only
  for a validated `BotAuthenticationSecret` role (admin/harness are separate);
  `src/Allegiance.Server/StandaloneCharacterIds.cs` retains process-lifetime,
  case-sensitive standalone IDs separately from verified account subjects.
  `src/Allegiance.Server/Server.StaticMaps.cs` loads native `maps.txt` and
  sends `src/Allegiance.Core/Wire/StaticMapInfo.cs` after validated logon;
  `src/Allegiance.ClientCore/State/ClientWorld.StaticMaps.cs` replaces and
  selects the catalog by exact file/team pair. P966: a player-CLAIMED map
  (`Server.Provisioning.cs`) is only ever a catalog KEY — an exact
  file+team match (native `VerifyUserMap`, `FedSrv.CPP:234-245`) whose
  canonical path stays under `artPath`; `..`/rooted/non-catalog claims are
  refused without path echoes. Native:
  `FedSrv/FedSrv.CPP:74-82,211-244,990-998,7690-7715`.
- **Inbound native-message certification (P867)** — the 49 active cases of
  `FedSrvSiteBase::OnAppMessage`, with sender/stage/ownership/trap guards,
  effects, handler cites and equal/classified results:
  `docs/parity/p867-onappmessage.csv`; inventory/citation check
  `tools/p867-dispatch-audit.py`. Dispatch stays in
  `src/Allegiance.Server/Server.Dispatch.cs`; native chat lobby controls and
  the balance-mode team flush use `src/Allegiance.Server/Server.DispatchParity.cs`.
  Report: `docs/reports/P867_REPORT.md`.
- **Logon re-send bound / cached side roster (P966)** — a peer still pending
  side selection gets the idempotent re-LOGONREQ re-ack (same ship id, no new
  allocation; P646 keeps one logon per connection) but bounded: duplicates
  inside `PendingSideSelect.ReackRetrySeconds` are dropped with rate-limited
  diagnostics, and the SIDE_ROSTER bytes come from a per-mission cache
  invalidated by every roster-affecting event (the lobby-dirty choke point,
  seat registration, side-civ changes). Native has no pending-side-select
  retry flow — it asserts an existing player never resends LOGONREQ
  (`FedSrv.CPP:4830-4832`). `src/Allegiance.Server/Server.Dispatch.cs`,
  `src/Allegiance.Server/Server.Lobby.cs` (`SideRosterPacket`),
  `src/Allegiance.Server/Server.cs` (`PendingSideSelect`).
- **Ship commands** — client intent handlers (flight, firing, docking, buying).
  `Server.ShipCommands.cs`, `Server.Docking.cs`, `Server.Combat.cs`.
- **Egress / send path** — the P605/P645 send-worker transport hardening.
  `Server.Egress.cs`, `Server.Dispatch.cs`.
- **TCP small-write pressure (P898)** — fixed 8 KiB send buffers can exhaust
  kernel packet memory at WAN RTTs even with zero loss. Default-off
  `transport.tcpBatchWrites` gathers up to 32 pending frames (16 KiB gathering
  budget) per nonblocking pool send, retaining framing, partial progress and
  existing liveness deadlines: `src/Allegiance.Core/Transport/TcpTransport.cs`.
  Counters: `src/Allegiance.Core/Transport/SendDiagnostics.cs`; regression tests:
  `tests/Allegiance.Core.Tests/Transport/P898TcpWriteTests.cs`; private-container
  kernel-netem sweep: `tools/p895-transport-proof/run-tcp-delay-matrix.py`.
  Report: `docs/reports/P898_REPORT.md`.
- **Connection lifecycle / shutdown ownership (P967)** — one owner per connection
  phase. A greeting socket and its thread are adopted by the accept path and are
  closed and joined by `Shutdown` before the sender pool goes away; a greeting that
  completes after the shutdown generation bump fails closed (no `OnPeerJoined`, no
  workers). Departed-peer bookkeeping is bounded: the `IsPeerClosed` tombstone keeps
  a recent window of *receive-drained* departures (`MaxEndedRetention`) instead of
  one entry per historical connection, and the exactly-once `OnPeerLeft` flag lives
  on the peer object. Connection-scoped server state (the chat budget) is retired by
  `ReleasePeerWithReason`, and the bucket-completion diagnostic is a bounded ring
  retired with its mission. `src/Allegiance.Core/Transport/TcpTransport.cs`,
  `src/Allegiance.Server/Server.Lifecycle.cs`; proof `tools/p967-shutdown-proof/`,
  tests `tests/Allegiance.Core.Tests/Transport/P967ShutdownOwnershipTests.cs`;
  report `docs/reports/TRANSPORT_SHUTDOWN_LEAKS_P967.md`.
- **Worker-pool sizing / host profile (P889)** — one resolver turns logical
  cores × expected players × `hostProfile` (`dedicated` | `shared`) into the
  parallel-build and transport-pool sizes, replacing the independent
  "cores−1 / cores" rules; explicit `broadcast.*`/`transport.*` config and env
  still win. `src/Allegiance.Server/PoolSizing.cs` (resolver, per-machine
  calibration profile), `Server.Lifecycle.cs` (`ResolvePoolSizing`), a
  `hostProfile`/`expectedPlayers` pair in `src/Allegiance.Server/ServerConfig.cs`.
  Skirmish/training declare `shared` in
  `src/Allegiance.SinglePlayer/SkirmishOptions.cs` and
  `src/Allegiance.SinglePlayer/Training/TrainingHost.cs`; the bot driver is
  sized by `SkirmishBudget.ForOptions`. The bounded calibration is
  `src/Allegiance.SinglePlayer/Benchmark/SizingCalibrationCommand.cs` +
  `src/Allegiance.ClientCore.Game/Game1.SizingCalibration.cs`, and the AIX2
  client frame log (`[frameprof]`) is emitted from
  `src/Allegiance.ClientCore.Game/Diagnostics/FrameProfileWindow.cs`. Docs:
  `docs/PERFORMANCE.md` ("Running on your own hardware"), report:
  `docs/reports/P889_REPORT.md`.
- **Automatic game-client transport (P897)** — pure Auto/TCP/UDP policy in
  `src/Allegiance.ClientCore.Game/Discovery/ClientTransportPolicy.cs`, route-selected
  adapter classification in `src/Allegiance.Core/Transport/NetworkLink.cs`, optional
  UDP port/host link on `src/Allegiance.Core/Discovery/DiscoveryProtocol.cs` replies.
  Shared Settings persist the preference in `src/Allegiance.ClientCore.Game/Rendering/HudSettings.cs`; join
  selects once and `ConnectionSession` freezes the choice for reconnect. Bots/MCP
  retain their TCP default. Proof: `tools/p897-transport-proof/`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/P897ClientTransportTests.cs`;
  report `docs/reports/P897_REPORT.md`, operator guide `docs/UDP_TRANSPORT.md`.
- **Optional UDP transport (P895, default off)** — `transport.protocol=udp`
  adds UDP alongside TCP pool: `src/Allegiance.Core/Transport/UdpTransport.cs`
  (cookie handshake, ordered/independent latest-wins channels, fragmentation,
  paced congestion control), `src/Allegiance.Core/Transport/DualTransport.cs`
  (distinct mixed-peer ids), `src/Allegiance.Core/Transport/IServerTransport.cs`
  (host lifecycle/accounting), `src/Allegiance.Core/Transport/TransportFactory.cs`
  (client preference + TCP fallback). Lobby `udpPort` reaches the shared client
  session. Native delivery intent: `_Utility/Messages.cpp:558-563`;
  configuration: `docs/UDP_TRANSPORT.md`; proofs:
  `tests/Allegiance.Core.Tests/Transport/P895UdpTransportTests.cs`,
  `tools/p895-transport-proof/`; report: `docs/reports/P895_REPORT.md`.
  P895b ADU2 batching/compact records, selective delayed/piggyback ACKs and
  blocking client receives with deadline timers (server readiness set/bounded
  deadline heap) live in
  `src/Allegiance.Core/Transport/UdpTransport.Records.cs`,
  `src/Allegiance.Core/Transport/UdpTransport.Scheduling.cs`; counters in
  `src/Allegiance.Core/Transport/UdpTransport.Statistics.cs` attribute overhead.
  Proofs: `tests/Allegiance.Core.Tests/Transport/P895bUdpOverheadTests.cs`;
  before/after and gate: `docs/reports/P895B_REPORT.md`.
- **Game-flow ordering proof (P888)** — stage/result/roster/rotation frames use
  the ordered send lane; the accelerated P660 test observes its short intermission
  at production tick speed. Both writer modes and the optional reliable park lane
  are covered by `tests/Allegiance.Core.Tests/Transport/TcpTransportPriorityLaneTests.cs`
  and `tests/Allegiance.ClientCore.Tests/P660NextMatchLiveTests.cs`.
  Native: `FedSrv/fsmission.cpp:1750-1762,3435-3438`;
  report: `docs/reports/P888_REPORT.md`.
- **Clash projection scheduling (P863)** — `src/Allegiance.Server/CrossSideSweepCache.cs`
  retains completed per-object/side/audience work for identity, discovery and
  status sweeps, invalidated by audience changes and authoritative latch removal;
  `src/Allegiance.Server/ShipUpdateRanking.cs` selects only the stable served
  prefix while preserving per-peer deviation. Sector model/blast membership is
  indexed without changing insertion order in `src/Allegiance.Core/Model/Sector.cs`;
  `sim.prepare` / `sim.models` complete the sector timing breakdown. Proof and
  400-peer projection measurements: `tests/Allegiance.Core.Tests/P863ClashBudgetTests.cs`;
  report `docs/reports/P863_REPORT.md`.
- **Surge projection allocation (P863b)** — native-mode ranked poses use the
  existing single-delivery packet leases in `src/Allegiance.Server/Server.RankedPose.cs`;
  selection/deviation remains sequential within each sector. Encoded events
  share a wrapper within a snapshot, and missile fan-outs share detached source
  poses/statuses in `src/Allegiance.Server/Server.FireAudience.cs`. The 400-peer
  death/eject/missile/relaunch transcript and per-step measurements live in
  `tests/Allegiance.Core.Tests/P863bSurgeBudgetTests.cs`; report
  `docs/reports/P863b_REPORT.md`.
- **Pending publisher stream index (P968, experiment)** — default-off
  `broadcast.pendingStreamIndex` / `ALLEGIANCE_BROADCAST_PENDING_STREAM_INDEX`
  indexes the coalesced pending snapshot by `(ConnectionId, Key)` in
  `src/Allegiance.Server/BroadcastSnapshot.cs`, skipping key-zero freshness checks
  while preserving per-delivery decisions and reliable order. Configuration:
  `src/Allegiance.Server/ServerConfig.cs`,
  `src/Allegiance.Server/Server.BroadcastSnapshot.cs`. Proofs:
  `tests/Allegiance.Core.Tests/P968PublisherStreamIndexTests.cs`; report:
  `docs/reports/PUBLISHER_STREAM_INDEX_P968.md`. Modern scheduling optimization;
  no new native rule.
- **Broadcast reuse and failure containment (P899)** — cross-thread snapshot
  and single-delivery packet reuse uses explicitly locked stacks in
  `src/Allegiance.Server/BroadcastPool.cs`, consumed by
  `src/Allegiance.Server/BroadcastSnapshot.cs` and
  `src/Allegiance.Server/PooledPosePacket.cs`. Cleanup reports failures without
  skipping remaining packet releases or stranding publication waiters. Proofs:
  `tests/Allegiance.Core.Tests/P899PublisherTests.cs`,
  `tests/Allegiance.Core.Tests/P899LoopbackMeasurements.cs`,
  `tools/p899-runtime-proof/Program.cs`; report `docs/reports/P899_REPORT.md`.
- **Per-world cache retention across rotation (P884)** — every per-ship /
  per-peer latch on the server is scoped to ONE world: native resets the same
  `CfsMission` and nukes every cluster (`Igc/missionigc.cpp:3804-3828`, called at
  `FedSrv/fsmission.cpp:3527`), so a retired world must become unreachable and no
  cache may accumulate one entry per (peer, ship) per match. `ReleasePeer`
  (`src/Allegiance.Server/Server.Lifecycle.cs`) frees the connection dimension,
  `RunMigrationLatchSweep` (`src/Allegiance.Server/Server.Lobby.cs`) frees the
  retired-ship dimension, and `RotateMissionAfterGameOver` clears the per-world
  leftovers (`_teammatePositionSent`, `_teammateLoadoutCache`,
  `_crossSideShipStatusSeen`, `_shipDetected`). Proof (retired `Mission`/`Ship`
  collected by a forced GC + no per-world cache grows with match count):
  `tests/Allegiance.ClientCore.Tests/P884RotationRetentionTests.cs`; report
  `docs/reports/P884_REPORT.md`.
- **Per-world retirement under the rig's settings (P884b)** — the P884 caches are
  now retired **per mission** (a server can host several worlds, P883b) by
  `RetirePerWorldShipState` (`src/Allegiance.Server/Server.WorldRetirement.cs`),
  which drops every ship-keyed entry that is not a live ship of ANOTHER mission:
  the P332/P317/P493 latches, HUD-17's `_shipDetected`, native per-ship `m_atu`
  (`_lastTurretUpdates`, `FedSrv/fsship.h:541`) and the allied-dock pending sets.
  The same file's `TrimRetiredLatchCapacity` releases the **peak backing arrays**
  of every `(recipient, subject)` latch after the migration sweep, so a match's
  widest moment is not retained by the next match (the rig's per-match floor
  step at 394 peers is the `_crossSideDiscoverySent` entry array: 10 MB → 44 MB
  over three matches). Ballot lookups are read-only (`_ballots` is created only
  when a ballot is), so an inbound VOTE from a peer still seated in the retiring
  world cannot keep that world's `Mission` reachable. Proof:
  `tests/Allegiance.ClientCore.Tests/P884bWorldRetirementTests.cs`
  (rig settings, `shipUpdateModel=native` + a two-world scoping case); report
  `docs/reports/P884B_REPORT.md`.
- **Game-over world boundary (P860)** — a rotation rebuilds the same static map,
  so both worlds' stations carry the SAME `StationId`s and a received
  `EXPORT(26)` is attributable to a world only by its position in the byte
  stream. `GAME_OVER(62)` is that boundary (native queues it before the reset:
  `FedSrv/fsmission.cpp:3437` then `:3490`); the harness's world-export
  accumulators clear inside the receive loop at that message
  (`Bot.ResetWorldExportsOnGameOver`, `src/Allegiance.Bot/Bot.cs`) instead of on
  a test thread. Per-side export gate: `src/Allegiance.Server/Server.Lobby.cs`
  (join burst) / `Server.Research.cs` (`ExportDiscoveredStations`), native
  `FedSrv/fsmission.cpp:4215-4222` and `FedSrv.CPP:10750-10778`. Report:
  `docs/reports/P860_REPORT.md`.
- **Verified duplicate logons (P864c / SIM-0496)** — the same verified lobby
  AccountId may replace its old mission seat through `RemovePlayerByName` in
  `src/Allegiance.Server/Server.Identity.cs`, called after `BindIdentity` by
  `src/Allegiance.Server/Server.Dispatch.cs`. Different/unverified identities
  and pending names refuse duplicates. Server config `alwaysRefuseDuplicateLogons`
  / env `ALLEGIANCE_ALWAYS_REFUSE_DUPLICATE_LOGONS` opts into always refusing
  (default off). Native: `FedSrv/fsmission.cpp:1248-1273`, `FedSrv/FedSrv.CPP:629`.
  Report: `docs/reports/P864c_REPORT.md`.
- **Session time conversion / admin starting tech (P935)** —
  `src/Allegiance.ClientCore/Flight/OwnShipServerClock.cs` implements the IGC
  add/subtract offset seam in simulation seconds, shared by native client ship
  and turret exporters; `src/Allegiance.Core/Sim/IgcTime.cs` encodes native
  millisecond rounding (`zlib/ztime.h:144-159`). Object imports live in
  `src/Allegiance.ClientCore/State/ClientWorld.cs`. Construction/chaff use original
  creation times; server pose tick indices stay unchanged. Native:
  `clintlib/clintlib.h:860-869`, `Igc/buildingeffectigc.h:50,142`.
  `src/Allegiance.Core/Model/TechBitOverrides.cs` holds lazy per-side altered/value
  masks; `src/Allegiance.Core/Model/Mission.TechOverrides.cs` applies them before
  initial techs/buckets at countdown. Production: console `override-tech` and
  `games[].techBitOverrides` in `src/Allegiance.Server/ServerConfig.cs`.
  Native: `FedSrv/fsmission.cpp:228-236,2718-2733`,
  `FedSrv/AdminGame.cpp:312-328`, `Igc/missionigc.cpp:3885-3898`.
  Report: `docs/reports/P935_REPORT.md`.
- **Local admin console (P913)** — existing stdin enqueues bounded commands for
  the tick thread in `src/Allegiance.Server/Server.Console.cs`: live player
  roster, moderator-path boot, all-games system chat/private whisper, status,
  200-entry chat history and independent ADMIN-chat alerts. No listener or new
  credentials. Native: `AllSrvUI/PagePlayers.cpp:77-120,354-418,776-829`,
  `AllSrvUI/PageChat.cpp:91-183,485-528`, `FedSrv/FedSrv.CPP:3129-3130`.
  Operator/Docker usage: `docs/GUIDE.md`; proof:
  `tests/Allegiance.ClientCore.Tests/P913ServerConsoleTests.cs`; report:
  `docs/reports/P913_REPORT.md`.
- **Console sink / verbosity gate (P318, P619c, P964)** — all routed
  diagnostic lines, and after `Install` every plain `Console.WriteLine`, go
  through one bounded queue drained by one background thread
  (`src/Allegiance.Core/ConsoleLog.cs`); a full queue drops instead of blocking
  the sim thread. The gate (`ConsoleLog.Verbose`, from
  `serverConfig.consoleVerbose`) silences the high-frequency per-tick lines but
  still counts the attempts. Test seams: `Install`/`Uninstall`,
  `PointSinkForTest`, `Flush` (waits until the drain has **written** everything
  enqueued, not merely dequeued it), `ResetForTest` (installs a fresh queue
  *generation*, so a left-over drain cannot count against it) and
  `OpenProbeForTest` (per-thread attribution of `Write` attempts/emissions — use
  it instead of asserting deltas of the process-global counters). Report:
  `docs/reports/VERBOSE_GATE_FLAKE_P964.md`.
- **Cheats / debug** — `Server.AdminCheats.cs`, `Server.Privileges.cs`;
  debug mode `docs/reports/P378_DEBUG_MODE.md`.
- **Showcase / scenario hosting** — `Server.Showcase.cs`,
  `Server.SideIntegrity.cs`.
- **Preset station-kill deadlines (P914)** — default-off
  `showcase.stationKillsDecideDeadline` adds hostile station destructions after
  native goal criteria and before ship kills. Research/rotating showcase and
  offline `GameConfig.SkirmishPreset` missions opt into preset scope at assembly;
  player-created and LAN worlds retain native ranking. Mission-owned counts and
  uncredited destruction accounting live in
  `src/Allegiance.Core/Model/Mission.StationKills.cs`, populated once per lethal
  station instance in `src/Allegiance.Core/Sim/Station.cs`. Presets always publish
  the tally through existing chat and game-over fields, shared by both HUD styles.
  Proof: `tests/Allegiance.Core.Tests/P914StationDeadlineTests.cs`,
  `tests/Allegiance.ClientCore.Tests/P914PresetPresentationTests.cs`,
  `tools/p914-ui-proof/`; report: `docs/reports/P914_REPORT.md`.
- **Seed pinning** — `ServerConfig.BenchmarkSeed` (sim RNG) vs the P811
  `MapGenConfig.Seed` / `--map-seed` (reproducible generated maps).
- **Reconnect Grace** (EXPERIMENTAL, `reconnect.enabled`, default **off**) —
  an unrequested drop parks the player's ship for `reconnect.graceSeconds`
  (default 5 s) instead of deleting it at once; the same pilot resumes the same
  ship/seat with a session token in the port-only `LOGONREQ.ResumeToken`
  extension (P883), and the existing native join burst re-syncs the client
  world. Expiry runs the native removal (`FedSrv.CPP:4549-4566`,
  `fsship.cpp:58-68`); a voluntary `LOGOFF` is immediate. P966 trust rules: a
  wrong token refuses only the claimant (the parked seat is untouched and
  resumable); a name collision refuses the resume without retiring the seat
  (port refusal-as-flag, LOB-0033); one held seat per (mission, name) — a
  same-name drop never overwrites a parked entry; the token is never logged
  (a short non-reversible tag only). `src/Allegiance.Server/Server.ReconnectGrace.cs`,
  `src/Allegiance.ClientCore/Session/ConnectionSession.cs`
  (`EnableAutoReconnect`), `tools/reconnect-fault/`,
  `docs/reports/P813C_RECONNECT_GRACE.md`. Client opt-in:
  `$ALLEGIANCE_RECONNECT_GRACE_SECONDS`.

- **Client connection ownership (P974)** — explicit connect, initial join and
  reconnect share a cancellable owner in
  `src/Allegiance.ClientCore/Session/ConnectionSession.Ownership.cs`. A successor
  drains the previous operation and socket before resetting the world; transport
  callbacks check their owner and individual dial. Terminal disposal rejects new
  connections; receive-initiated departure returns before teardown joins the read
  thread and notifies loss. `src/Allegiance.ClientCore.Game/Game1.Session.cs` polls one
  asynchronous join with a 15-second overall deadline and Escape cancellation in
  both HUD styles. TCP establishment has a cancellable ten-second ceiling in
  `src/Allegiance.Core/Transport/TcpTransport.cs`; UDP/fallback/traversal propagate
  cancellation too. Native retry clears the preceding connection
  (`clintlib/clintlib.cpp:1298-1306`); task ownership is a modern safeguard.
  Proof: `tests/Allegiance.ClientCore.Tests/P974SessionOwnershipTests.cs`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/P974ConnectingUiTests.cs`,
  `tools/p974-session-proof/`; report:
  `docs/reports/CLIENT_SESSION_OWNERSHIP_P974.md`.

- **Trap-hack operator events (P855/SIM-0809)** — every message-validation
  failure that stands in for a native `TrapHack*` check emits one structured
  operator-log line (kind log/boot/boot-no-player, failed check text, native
  source site, player/ship/connection), rate-limited per connection. The port
  keeps log-and-ignore (native boots, `FedSrv.CPP:2749-2798`); the event
  records what native would have done. Key: `src/Allegiance.Server/TrapHack.cs`.
  Report `docs/reports/P855_REPORT.md`.

## 4. Client & HUD (`src/Allegiance.ClientCore`, `ClientCore.Game/Rendering`)

- **Headless session** — the world-state tracker driving prediction/replay.
  `src/Allegiance.ClientCore/Session/`, `Replay/`, `State/`.
- **UI skins (P981)** — canonical skin ids (`classic`, `modern`) replace the
  persisted two-value `HudStyle`. `src/Allegiance.ClientCore.Game/Rendering/Skins/`
  holds `HudSkinIds`, `HudSkinDefinition`, source-generated `HudSkinJson`,
  `HudSkinLoader`, `HudSkinCatalog` and built-in `classic.skin.json` /
  `modern.skin.json`. `HudSettings` migrates legacy `Style` to `Skin`, keeps
  `Style` as a renderer adapter, and saves only the canonical id. Report:
  `docs/reports/SKINS_1B_IDS_MIGRATION_P981.md`.

- **Runtime HUD skin switching (P988)** — `src/Allegiance.ClientCore.Game/Rendering/Skins/IHudSkinRenderer.cs`
  and `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinRenderer.cs` adapt the unchanged Classic/Modern
  drawing code; `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinResources.cs` binds the existing frame
  passes and `src/Allegiance.ClientCore.Game/Rendering/Skins/HudSkinSwitcher.cs` owns preloaded active/pending
  references. Shift+F12 and graphics row 6 queue a switch; Game1 publishes it
  after Update, then saves `Skin`. Bound legacy `Style`/`Skin` assignments
  queue through the same owner without frame polling. Training restores the active and persisted
  pre-lesson choice. Proof: `tools/p988-skin-switch-proof/`;
  tests: `tests/Allegiance.ClientCore.MonoGame.Tests/Skins/HudSkinSwitchingTests.cs`;
  report: `docs/reports/SKINS_1C_SWITCHING_P988.md`.

- **Per-message receive containment (P975)** — an inbound message whose body a codec
  rejects is dropped (counted, rate-limited `fmid`+length log, never the payload)
  instead of ending the session; every case lives in one contained dispatch,
  `ConnectionSession.DispatchInboundMessage`, and `ConnectionSession.IsMandatoryFmid`
  is the explicit mandatory list (LOGONACK only). Native: `_Utility/Messages.cpp:947-994`.
  Proofs: `tests/Allegiance.ClientCore.Tests/P975ReceiveContainmentTests.cs`; report
  `docs/reports/CLIENT_ROBUSTNESS_P975.md`.
- **Per-world ship caches (P975)** — every ship-keyed client cache (remote dead
  reckoners, Hermite/remote-timeline histories, re-seed statistics and Game1's radius/side-colour/LOD
  render maps) lives in ONE `ClientWorld.ShipCaches` container, replaced at every
  world boundary (`ResetCore`, `BeginNextMission`, `ClearTeamWorld`), so a retired id
  can never hand interpolation state, a side colour or an LOD tier to the next match.
  Proofs: `tests/Allegiance.ClientCore.Tests/P975WorldBoundaryCacheTests.cs`; report
  `docs/reports/CLIENT_ROBUSTNESS_P975.md`.
- **Autopilot (G key / pod home / cluster goto)** — the native `GotoPlan` law:
  same-sector steering, dodge, docking bays, pod pickup and cross-sector routing
  through warps. Key: `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs`
  (`TryGetAutopilotControls`, `AllWarps`, `TryResolveClusterWaypoint`),
  `Game1.Targeting.cs` (`TryResolveAutopilotGoal`),
  `src/Allegiance.ClientCore.Game/Flight/StarmapGotoRouter.cs`,
  `src/Allegiance.Core/Model/WarpDestinationResolver.cs`. **Routing is
  discovery-gated** exactly like native `FindPath` (`Igc/common.cpp:894-1010`, the
  `pship->CanSee(pwarp)` filter at `:920`/`:975`; `Igc/shipIGC.h:380-390`): since
  P797 a side only receives alephs it has seen (`FedSrv/fsmission.cpp:4181`), so a
  fixture that wants a multi-jump route must seed the map (`ShowMap`). Reports
  P540b/P699; guard test `P859RegressionProofTests`.
- **Lifepod home / rescue** — ejecting leaves a pod that flies home with the same
  autopilot: target selection (`Game1.ChooseEjectRescueTarget`, native
  `clintlib/appmsg.cpp:230-238` `FindTarget(… c_ttAnyCluster …, c_sabmRescue…)`),
  oxygen budget and docking. P977 adds own+allied targeting, side-independent
  RescueAny probes, current-cluster/warp-bracket ordering and allied bay approach
  (`src/Allegiance.ClientCore.Game/Game1.Eject.cs`, `Game1.FlightInput.cs`; native
  `Igc/common.cpp:269-333,648-729`, `Igc/shipIGC.cpp:732-735`). Proofs:
  `tests/Allegiance.ClientCore.MonoGame.Tests/P977EjectAlliedRescueTests.cs`,
  `P977AlliedEjectLiveTests.cs`; parity `docs/parity/p977-eject-rescue.csv`;
  report `docs/reports/EJECT_RESCUE_TOKEN_LOG_P977.md`. Earlier reports P699/P699c;
  live test `P699HumanPodHomeLiveTests`.
- **Player pod collision lifetime (P955)** — collision damage returns `Killed`
  even after the synchronous server callback swaps the same ship to a live pod.
  `src/Allegiance.Core/Model/Sector.cs` preserves that survivor in asteroid,
  ship and station contacts, retaining sector simulation and mission registration.
  Native: `Igc/shipIGC.cpp:1333-1334`, `FedSrv/FedSrv.CPP:11918-12021`.
  TCP/UDP oxygen and docking proofs:
  `tests/Allegiance.Core.Tests/P955PlayerPodLifetimeTests.cs`; report:
  `docs/reports/PLAYER_POD_GHOST_P955.md`.
- **Own small-hull effects (P915)** — `src/Allegiance.ClientCore.Game/Rendering/ShipEffectRules.cs`
  scales own external-view damage smoke from real hull length and camera distance,
  and gates afterburner trail/nozzle boost by the owner's mounted loadout (remote
  lifepods excluded). Native: `effect/thinggeo.cpp:699-760`,
  `WinTrek/WinTrek.cpp:7999-8006`. Proof: `tools/p915-smoke-proof/`;
  report `docs/reports/P915_REPORT.md`.
- **Mesh texture row convention (P916)** — native mesh UVs sample file rows
  unchanged, shared by binary and imported-image materials through
  `src/Allegiance.ClientCore.Game/Rendering/NativeStationTexture.cs`.
  `src/Allegiance.ClientCore.Game/Rendering/NativeMaterialParts.cs` adapts legacy
  HD atlas rows at binding (including PBR bitangent handedness); image/decal
  callers retain their explicit legacy reversal. P915's own-pod-only UV variant
  is removed. Native: `engine/modeler.cpp:723-733`,
  `engine/device3d.cpp:2245-2246`, `engine/context.cpp:237-240,659-660`.
  Corpus/export tests: `tests/Allegiance.ClientCore.MonoGame.Tests/P916TextureOrientationTests.cs`,
  `tests/Allegiance.Core.Tests/P916TextureExportTests.cs`; proof:
  `tools/p916-texture-proof/`; report `docs/reports/P916_REPORT.md`.
- **Sector asset warm-up (P862)** — a warp must not load a sector in its transit
  frame. Per-type assets (native rock/station/hull meshes + 2048² native textures,
  the planet poster, the tier's dome) are created lazily by the renderer that
  first draws them, so entering a never-drawn sector (Alderan's 134-rock field)
  used to decode several 8.4 MB RGB565 files, allocate ~240 MB and stall the
  transit frame. The fix resolves them ahead of time under a per-frame time
  budget — the own sector as soon as the mission data is known, and the sector
  behind an aleph once the own ship is on approach (side-wide discovery means the
  client usually knows it, exactly as in the 200v200 showcase) — through the SAME
  loaders the draw path uses, so a warmed asset is resident for the match and a
  second entry loads nothing. Key: `src/Allegiance.ClientCore.Game/Rendering/SectorWarmup.cs`
  (pure queue/planner/budget), `Game1.SectorLoad.cs` (seeding, aleph approach,
  `[sector-warm]` / `[sector-entry]` accounting), `Rendering/AssetLoadLog.cs`
  (`ALLEGIANCE_ASSET_LOG_MS`, the `[asset]` slow-load line); harness
  `scripts/p862-warp-stall.sh` + `scripts/p862-warp-summary.py`
  (`ALLEGIANCE_SECTOR_PREWARM=0` is the A/B baseline). Only the ACTIVE sky tier
  resolves a sector (each tier owns its own planet/dome caches), and the aleph
  destination glimpse no longer prepares a sky it would draw fully transparent.
  Report `docs/reports/P862_REPORT.md`. *Native intent:* the cluster-entry model
  load (`WinTrek`/`Igc` cluster change) — the port does the same work earlier.
- **Aleph destination glimpse (P576-B/P585/P862b)** — the opt-in (default ON since
  P607) view of an aleph's DESTINATION sector sky composited inside its funnel
  (`Rendering/AlephGlimpseRenderer.cs` + the `WarpPulseController` transit pulse
  and lens flare). Two rules matter: (1) the destination is resolved from the
  warp's PARTNER warp (`WarpDestinationResolver`, native `warpIGC.h:129-141`) —
  `DataWarpIGC.DestinationId` is a WARP id, never a sector id, and feeding it into
  the cluster table (the pre-P862b code) resolves nothing on a map whose id spaces
  do not overlap, so the glimpse silently drew nothing; (2) the destination sky is
  rendered through the SAME sky-tier renderer the current sector uses
  (`Game1.ActiveSkyTier`), so its art is shared — the sector you just left is
  already resident, and preparing its glimpse costs one 256² pass, no decode. The
  sector warm-up (`SectorWarmupPlanner.GlimpseSky`) does that pass during the aleph
  approach, because on arrival the glimpse looks BACK at the sector just left.
  The flare's occlusion test samples the P547 linear-depth target one frame late
  (`Rendering/FlareOcclusion.cs`, `ALLEGIANCE_FLARE_OCCLUSION=deferred|blocking|off`)
  instead of a synchronous readback inside the frame being drawn. Report
  `docs/reports/P862B_REPORT.md`.
- **Flight prediction / dead reckoning** — `src/Allegiance.ClientCore/Flight/`
  (`KinematicsDeadReckoner` etc.), reports `P447*/P448*`.
- **Own-ship pose authority (P890)** — `src/Allegiance.ClientCore/Flight/OwnShipSimulator.cs`
  supplies the camera and timed client poses; `src/Allegiance.Server/Server.Dispatch.cs`
  validates those poses and retains their replayed turn rates. Time-zero commands
  explicitly select server-flown scripted pilots; local training alone permits its
  native authored origin. Native: `Igc/shipIGC.cpp:3074-3082,3139-3187`,
  `training/trainingmission.cpp:134-139`. Loopback firing/turning evidence:
  `tests/Allegiance.ClientCore.Tests/P890OwnAimLoopbackTests.cs`; report:
  `docs/reports/P890_REPORT.md`.
- **Volumetric explosions (raymarch tier)** — Technique A (P573/P579) plus the
  P834 near-cost policy: coverage is measured as a fraction of the viewport
  height, volumes render at full/half/quarter resolution by coverage, the
  per-frame pixel-step budget is per screen pixel (measured on the B50) with a
  bounded adaptive fallback, and the shader clamps the march to the visible
  depth range (near plane → scene depth). **View axis (`ViewForward`)** — the
  camera's forward axis as a `float3` uniform the renderer derives from the view
  matrix (`-(M13,M23,M33)`); the clamp's cosine is `dot(rayDir, ViewForward)`
  (P834b: never `-rayDir.z`, which only holds for a camera looking down world
  −Z, and never a matrix read from the pixel shader — that corrupts the PS
  constant block, see the P834 report §9). Key:
  `src/Allegiance.ClientCore/State/VolumetricQuality.cs`,
  `State/VolumetricExplosionSampler.cs`, `State/VolumetricGeometry.cs`,
  `src/Allegiance.ClientCore.Game/Rendering/VolumetricRenderer.cs`,
  `src/Allegiance.ClientCore.Game/Shaders/Volumetric.fx`; cost harness
  `tools/p834-vol-perf/` (has `--yaw`/`--pitch` orientation sweeps and pins the
  `.mgfxo` under test with `--shader`). Reports
  `docs/reports/P579_ADOPT_VOLUMETRIC_EXPLOSIONS.md`,
  `docs/reports/P834_VOLUMETRIC_NEAR_COST.md`. *Native:* `effect/explosion.cpp`,
  `WinTrek/trekigc.cpp:95-177`.
- **HUD settings / styles** — `Rendering/HudSettings.cs` defines `HudStyle`
  Classic/Modern; **every HUD change must work in both styles**
  (`Rendering/ModernHudRenderer.cs`). *Native:* the original classic HUD layout.
- **Sim space visuals (P982, default OFF)** — independent `visuals.subtleDust`,
  `simStars`, `simGlare`, and `starStreaks` rows in
  `src/Allegiance.ClientCore.Game/Rendering/VisualFlags.cs`. The existing Debris
  level budgets the near-eye shell in `src/Allegiance.ClientCore.Game/Rendering/SubtleDust.cs`;
  direction projection and rotation history live in
  `src/Allegiance.ClientCore.Game/Rendering/SpaceVisualMotion.cs`, with batched
  points/tails in `src/Allegiance.ClientCore.Game/Rendering/SpaceStarRenderer.cs`.
  `src/Allegiance.ClientCore.Game/Rendering/HdStarSamples.cs` anchors HD tails
  to actual baked stars; `src/Allegiance.ClientCore.Game/Rendering/SimGlare.cs`
  limits sun size and ghosts. Height-aware table rows use
  `src/Allegiance.ClientCore.Game/OptionsMenuModel.cs`. All three sky tiers share the choices, including
  aleph postcards without advancing flight history. Proof:
  `tools/p982-space-proof/`; tests:
  `tests/Allegiance.ClientCore.MonoGame.Tests/P982SimSpaceTests.cs`; report:
  `docs/reports/SIM_SPACE_FLAGS_P982.md`. Native defaults retained:
  `effect/debrisgeo.cpp:98`, `effect/starimage.cpp:32-53`,
  `effect/efimage.cpp:601-613,716-745`. Opt-in look is a visual departure.
- **Visual-enhancement flags (`visuals.*`, P979)** — visual changes that may
  depart from native's look sit behind per-flag switches, **all default OFF**
  (the native path stays pinned by tests); a flag changes how things LOOK, never
  game rules or server knowledge. The table lives in
  `src/Allegiance.ClientCore.Game/Rendering/VisualFlags.cs` (`VisualFlags.Definitions`
  drives parsing, the `--visuals key=on,...` override, the Options → Visuals page
  and the startup log); the state is one flat `visuals` group in
  `player.json` (`IntroPreferences.Visuals`, unknown keys ignored), effective
  flags resolved at startup in `Game1.cs`. The first flag,
  `visuals.radialVisibility`
  (`src/Allegiance.ClientCore.Game/Rendering/RadialVisibility.cs`,
  `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` `OutsideRadialView`), draws
  objects by true camera distance instead of native's
  planar far clip (`SetZClip(5, 10000)`, `WinTrek/WinTrek.cpp:323`; projection
  shape `engine/camera.cpp:89-96,135-139`), removing the 6-10 km pop-in when the
  ship turns; `visuals.viewRange` is its range in metres. Proof:
  `tools/p979-visual-proof`; report `docs/reports/VISUAL_FLAGS_RADIAL_VISIBILITY_P979.md`.
- **Remote-ship server-time timeline (P985, experiment, default OFF)** —
  `visuals.remoteTimeline` selects `src/Allegiance.ClientCore/Flight/RemoteShipTimeline.cs`,
  an eight-sample ring ordered by the server loop tick (mapped onto the local clock by
  `RemoteTimelineClock`, a min-offset playout clock — never the TiDi-scaled PING clock) with duplicate/expiry rejection,
  late insertion, cadence+jitter-smoothed delay, P813b's shared Hermite maths and the
  existing capped dead-reckoner past the newest sample. `ClientWorld.ShipCaches`
  owns and retires it; stall/sector/death/respawn/rotation/teleport cuts reset it.
  Only `Game1.RemoteShipDrawPose` consumes the result, never aim/target/collision/lock,
  own-ship prediction or outbound state. Replay: `tools/p985-remote-timeline-proof/`;
  tests `tests/Allegiance.ClientCore.Tests/P985RemoteTimelineTests.cs`;
  report `docs/reports/REMOTE_TIMELINE_P985.md`.

- **Linear HDR scene foundation (`visuals.space.hdrPipeline`, P984)** — with the
  flag ON the 3-D scene renders into a floating-point target
  (`SurfaceFormat.HalfVector4`, RGBA16F), the PBR/HD-material path
  (`Shaders/NativePbr.fx` `NativePbrHdr`/`EffectLitPbrHdr`) lights in LINEAR space
  and writes the sRGB transfer once with no curve or clamp, and exactly ONE output
  transform (`Shaders/Tonemap.fx`: `Neutral` = today's HD-6 hue-preserving shoulder
  moved out of the PBR shader; `Aces` = Narkowicz's public fit) converts the scene
  before the HUD, which is never graded. The float target holds **display-encoded
  RGB with headroom** (every legacy writer — BasicGame hulls, sky billboards,
  SpriteBatch particles — emits display-referred values; only the shader we own is
  linear internally). Ordered pass list as data: `Rendering/HdrOutput.cs`
  `Plan`/`SceneFormat`/`Neutral`/`Aces`; transform: `Rendering/HdrOutputTransform.cs`;
  float targets + fallback: `Rendering/VfxPostProcessor.cs` (`HdrActive`),
  `Rendering/BloomPostProcessor.cs`, `Rendering/FsrPostProcessor.cs`; wiring in
  `Game1.Rendering.cs` (flight), `Game1.Inspector.cs` (showcase), `Game1.Lifecycle.cs`
  (startup log), `Game1.GpuBudget.cs` (resize). `visuals.space.hdrAces` selects the
  ACES curve for comparison. Default OFF; the OFF path is byte-identical to the
  native 8-bit pipeline (proven against a base-commit build). Proof:
  `tools/p984-hdr-proof`; report `docs/reports/HDR_FOUNDATION_P984.md`.

- **Sim palette (`visuals.space.simPalette`, P989)** — the FS2-Open-flavoured "sim"
  colour grade. It adds NO pass and NO target: it retunes the two things P984 already
  runs — the single output transform (`Shaders/Tonemap.fx` `Aces` pass at exposure
  0.9 with world saturation 0.75 applied in LINEAR light, no tint/lift) and the
  existing bloom pass (bright-pass threshold 1.2 linear = `HdrOutput.ToStored(1.2)`
  in the scene target's stored domain, glow contribution 0.08 = the pass's existing
  `BloomScale`). The values come from `docs/reports/FS2OPEN_STUDY_P978.md` §5.3 and
  were tuned on our captures. It **requires** `visuals.space.hdrPipeline`: without
  the float scene target the grade is a no-op and the Visuals row says
  "(inactive: needs HDR scene)" (`VisualFlag.RequiresKey`). Model:
  `Rendering/SimPalette.cs` (`HdrGrade`, `SimPalette.Grade`); wiring
  `Game1.cs` `SyncHdrGrade` + `Game1.Rendering.cs`, `Game1.Inspector.cs`; tests
  `tests/Allegiance.ClientCore.MonoGame.Tests/SimPaletteTests.cs`; GPU proof
  `tools/p989-palette-proof`; captures `docs/reports/P989_assets/`; report
  `docs/reports/SIM_PALETTE_P989.md`.
- **Modern HUD scale / text budget (P833, capped by P859)** — one scale for text,
  glyph art, gauge ring, target pane and own-status grid, derived from the desktop
  display density × the user `HudSettings.Size` (`Rendering/ModernHudScale.cs`:
  `Core(displayScale, size)`), and **capped by the window-height budget**
  `WindowFactor(height)` = 1.5 scale units per 1080 px (`Core(density, size, height)`,
  the overload the renderer calls) so a small window / large Size cannot inflate the
  HUD past the layout the HUD-2 proof verifies. Classic stays native fixed pixels.
  Report `docs/reports/P833_HUD_SCALE.md`; regressions and the cap:
  `docs/reports/P859_LIVE_MONOGAME_REGRESSIONS.md`.
- **HUD text layout (de-overlap)** — `Rendering/HudTextLayout.cs`: fixed
  instruments reserve their zones first, measured radar labels/blocks yield to
  them, notices yield to labels, and a fully crowded screen falls back to the
  native anchor instead of throwing. The centred ripcord countdown reserve is the
  union of the native 150-px StringImage box (`Artwork/dialog.mdl:5145-5151`,
  `:5429`) and the wider port glyph run (P859). Proof: `tools/hud2-proof`.
- **HUD golden baseline / zero-delta gate (P980)** — `tools/hud2-proof` renders 22
  scenarios × 7 resolutions (800×480 … 3840×2160) × 3 sizes × 2 backdrops (dark/bright)
  × 2 skins through the production renderers, and `--capture` writes lossless RGBA
  PNGs, a SHA-256 of the *decoded* pixels, a semantic snapshot (visible `HudElement`s,
  text boxes + bounds, inventory column fit) and a deterministic `manifest.json`.
  `--compare <baseline>` re-renders and fails on any decoded-pixel or semantic
  difference (diff images + changed-pixel counts), `--diff-captures <a> <b>` is the
  double-capture determinism proof and `--self-test` proves the compare mode detects a
  deliberately changed pixel. Baseline lives outside the repo
  (`~/allegiance-evidence/hud-goldens/<short-commit>/`); only the manifest and a small
  runnable sample are committed (`docs/reports/P980_assets/`). Determinism is
  fixture-owned: Modern's frame state is reset per combination and the one wall-clock
  animated glyph (ModernHudRenderer.cs:166) is a recorded animation mask, never a
  blessed hash. Coverage inventory: `tools/hud2-proof/coverage.py` (every `hud` and
  `screens` parity row → scenarios). Report `docs/reports/SKINS_1A_BASELINE_P980.md`.
- **HUD renderer** — `Rendering/HudRenderer.cs` (shared model +
  Classic), `Rendering/ModernHudRenderer.cs` (Modern override). P786 report.
- **Fresh-profile keys hint (P949)** — `Rendering/ControlHint.cs` (pure text +
  lifetime: 60 s or any key, once per profile), drawn by the shared
  `HudRenderer` under the native chat pane so Classic and Modern both show it;
  carried on `HudContext.ControlHint` and persisted as
  `HudSettings.ControlHintSeen` in `hud.json`. Never shows for a profile with a
  keymap (`KeyBindingStore.HasKeymap`). Wiring: `Game1.KeyBind.cs`
  (`UpdateControlHint`), `Game1.FlightInput.cs` (`UpdateFlying`).
- **Lead marker / aim predictor** — the client-solved lead and the native
  aim/missile indicators: `Rendering/ClientLead.cs` (gate + solve + the
  screen-space ramp), `Game1.Camera.cs` (`ComputeLeadIndicators`,
  `LeadGranted`, `ComputeChaseAim`), `Rendering/HudModel.cs`
  (`LeadMarkerVisible` / `AimPredictorVisible` / `ChaseAimVisible`).
  *Native:* `WinTrek/indicator.cpp:183-405`; report P804.
- **Radar** — contacts, icons, brackets, labels, overlays.
  `Rendering/Radar*.cs` (RadarBracketRenderer, RadarIconPlacement,
  RadarLabelModel, RadarProjection, RadarLod).
- **Radar label declutter** (Modern-only visual option, default on in Modern,
  off in Classic) — priority + nearest-N label selection, distance fade and the
  screen-grid count tags for crowded battles: `Rendering/RadarLabelDeclutter.cs`
  (pure selection model consumed by `HudRenderer.DrawRadarLabels`; options are
  `HudSettings.RadarDeclutterOn` / `RadarLabelMax` / `RadarLabelTelemetry`, the
  Ctrl+F6 options rows 16-18). The native label tiers it filters stay in
  `Rendering/RadarLabelModel.cs`. Report `docs/reports/P828_LABEL_DECLUTTER.md`.
- **HUD levels** (Modern-only, P836) — the Modern HUD density presets
  Full / Tactical / Combat / Cinematic / Auto, each a data preset over the P828
  declutter knobs plus a new off-screen contact rule: `Rendering/HudLevels.cs`
  (the preset table + the Auto resolver with hysteresis), persisted as
  `HudSettings.Level` in `hud.json`, set in the Ctrl+F6 options row 20 or cycled
  with the `0` key (Modern only; Classic ignores it and keeps native `.` =
  TK_CycleRadar). Off-screen ordinary contacts shrink to an edge pip
  (`RadarBracketRenderer.Draw`, `OffscreenHidden`). Report
  `docs/reports/P836_HUD_LEVELS.md`.
- **Target pane / gauges** — `src/Allegiance.ClientCore.Game/Rendering/TargetPaneModel.cs`,
  `src/Allegiance.ClientCore.Game/Rendering/ClassicGaugeSpec.cs`,
  `src/Allegiance.ClientCore.Game/Rendering/GaugeKind.cs`. Reports HUD3/HUD6/HUD7.
- **Target model preview** — the 3-D model the target pane shows for the selected
  target (native `SetTargetGeo` + `targetCamera`, `WinTrek/WinTrek.cpp:8783-8818`):
  gate/camera/light rules in `Rendering/TargetModelPreview.cs`, the 256² render
  target + per-kind draws in `Rendering/TargetModelPreviewRenderer.cs`
  (ship/station/asteroid mesh, aleph vortex via `Rendering/WarpRenderer.cs`,
  treasure model/decal and probe mesh via `Rendering/NativeTreasureRenderer.cs`).
  Reports `docs/reports/HUD21_TARGET_MODEL_PREVIEW.md`, `P805B_HUD_PROOF.md`.
  P922 preserves cached model/texture stems for ships, stations and asteroids,
  so both HUD styles follow the shared live HD material mode; treasure already
  carries them in its visual. GPU regression proof (including inherited file-level
  texture bindings): `tools/p922-target-proof/`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/P922TargetMaterialTests.cs`;
  report `docs/reports/P922_REPORT.md`.
- **Live-proof automation** — the real-window proof harness: the flag grammar and
  sequencing in `src/Allegiance.ClientCore.Game/LiveProof/` (`AutoProofPlan`,
  `AutoProofMachine`), the client actions in `Game1.AutoProof.cs`, and the
  one-command launcher `scripts/live-proof.sh` (local server + MCP bots + a
  windowed client; `--mapgen` for a seeded generated arena, `--hud-settings` for
  the style, `--auto-target-seq`/`--auto-drop-probe-at`/`--auto-summon-enemy-at`
  for scripted targets). Reports P572, P805B.
- **Starmap / minimap** — `Rendering/Minimap*.cs`, `Rendering/StarmapLayout.cs`.
- **Screens** — intro, connect, hangar/loadout, team lobby, briefing, panes.
  `src/Allegiance.ClientCore.Game/Game1.*.cs` (Game1.Hangar.cs, Game1.Panes.cs,
  Game1.IntroScreen.cs, Game1.ConnectScreen.cs).
- **Intro screen (P731/P946)** — the port's keyboard intro menu: the button set/commands and the pure key→focus
  mapping (arrows always move the selection, Tab toggles typed-text focus, Enter activates) in
  `src/Allegiance.ClientCore.Game/IntroScreenModel.cs`, resolution-adaptive menu geometry (short viewports compress
  instead of clipping) in `src/Allegiance.ClientCore.Game/IntroLayout.cs`, the views/drawing in
  `src/Allegiance.ClientCore.Game/Game1.IntroScreen.cs`, and the real-window key proof in
  `tools/p946-intro-keys-proof/`; tests `tests/Allegiance.ClientCore.MonoGame.Tests/P946IntroKeysTests.cs` and
  `tests/Allegiance.ClientCore.MonoGame.Tests/IntroLayoutTests.cs`. Native is mouse-driven ButtonPanes with no key
  handler: `WinTrek/introscreen.cpp:1212-1228`.
- **Native hangar screen (P945)** — the docked hangar view is the native
  `hangar.mdl` screen: `src/Allegiance.ClientCore.Game/Rendering/HangarScreen.cs`
  is the single geometry/string source (800×600 panes at their authored points,
  hover text table, in-turret badge, port-only verb cells),
  `HangarScreenRenderer.cs` draws it scaled and centred and
  `HangarArt.cs` loads the binary `.mdl` art case-insensitively (the eight
  button strips, the 800×600 background, the badge). The launch pane's
  `FrameImage` animation is decoded by `src/Allegiance.Core/Data/MdlFrameImage.cs`
  and driven by `HangarLaunchAnimation.cs` (0.7 s ramp in/out, 16 frames).
  `--auto-hangar` (`LiveProof/HangarProofScript.cs`, `Game1.HangarProof.cs`)
  scripts hover/click/screenshot steps for the real-window proof. Native:
  `WinTrek/WinTrek.cpp:6565,6706-6718,6836`, `WinTrek/hangarscreen.cpp:36-300`,
  `engine/button.cpp:108-290`, `engine/frameimage.cpp:110-500`. Tests:
  `tests/Allegiance.ClientCore.MonoGame.Tests/P945HangarArtTests.cs`; report:
  `HANGAR_ART_P945.md`.
- **Native sound dispatch (P871)** — the numeric `SoundID` space and the
  cue/loop routing: the generated `soundList` table
  (`src/Allegiance.Core/Audio/NativeSoundCatalog.Generated.cs`, rebuilt by
  `scripts/generate-sound-catalog.py`) plus the native gates and state machines
  (`src/Allegiance.ClientCore.Game/Audio/NativeSoundDispatch.cs`: StartSound,
  PlaySoundIf, PlaySoundEffect/offset/notification, gunner interior, MDL button
  sink) and the device mixer
  `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs`. Native:
  `WinTrek/soundinit.cpp`, `WinTrek/trekigc.cpp:930-951,4955-4983`,
  `WinTrek/WinTrek.cpp:11271-11280`, `WinTrek/trekmdl.cpp:452-478`.
- **Sound templates and audio quality (P902)** — generic sample-aligned ASR
  (`src/Allegiance.ClientCore.Game/Audio/AsrSound.cs`), source/interval-cached
  repeating-fire PCM, multiplicative pitch and effect cones
  (`src/Allegiance.ClientCore.Game/Audio/SoundTemplates.cs`), with authored
  parameters in `src/Allegiance.ClientCore.Game/Audio/NativeSoundTemplateSettings.Generated.cs`
  (regenerated by `scripts/generate-client-sound-templates.py`).
  `src/Allegiance.ClientCore.Game/Audio/AudioVoice.cs` adapts regular voices and
  ASR playback: DesktopGL borrows a MonoGame OpenAL source and reuses three
  native buffers; mobile uses the framework streaming API.
  `src/Allegiance.ClientCore.Game/Audio/AudioDevice.cs` latches the shared
  SFX/music no-device fallback. Sound Options persists Low/Medium/High native
  software voice budgets (8/16/24 desired voices); OpenAL owns the device format.
  Native: `soundengine/soundtemplates.cpp:282-365,638-659,986-1367`,
  `soundengine/ds3dvirtualbuffer.cpp:41-133,764-804`,
  `soundengine/ds3dengine.cpp:492-550`, `effect/efsound.cpp:82-100`.
  Proof: `tools/p902-audio-proof/`; report: `docs/reports/P902_REPORT.md`.
- **Positional 3D sound (P893)** — the native DirectSound3D model for world
  sounds as pure math + a per-frame mix pass: native log rolloff from the
  per-family `ThreeDSound(minDistance)` values, Doppler from emitter/listener
  velocities, stereo pan from the camera basis, cones (ported, unused by
  artwork), and the −60 dB audibility radius feeding the voice budget. Key:
  `src/Allegiance.ClientCore.Game/Audio/Sound3D.cs` (`Sound3DModel`,
  `Native3DMix`, `Sound3DMix`), the 3D pass in
  `src/Allegiance.ClientCore.Game/Audio/SoundManager.cs`
  (`ApplyVoiceBudget`), listener feeds `Game1.FlightInput.cs` (velocity) /
  `Game1.Rendering.cs` (camera pose), option `IntroPreferences.Enable3DSound` +
  `OptionsMenuModel` Sound row. Native: `soundengine/ds3dvirtualbuffer.cpp:692-830`,
  `soundengine/ds3dbuffer.h:12-15`, `soundengine/ds3dengine.cpp:236-237`,
  `WinTrek/WinTrek.cpp:646-708`, `WinTrek/treksound.cpp:60-120`.
- **Force feedback (P871)** — native `PlayFFEffect` producers and their gates:
  own-ship/gunner weapon fire, own collisions, credited damage with the impact
  direction, plus expiry/focus/disconnect stop and the settings toggle. Key:
  `src/Allegiance.ClientCore.Game/Audio/ForceFeedback.cs`,
  `Audio/MonoGameForceFeedback.cs`, `Game1.ForceFeedback.cs`,
  `IntroPreferences.EnableForceFeedback`, `OptionsMenuModel` (native
  `engine/dinput.cpp:1392-1445`, `WinTrek/trekigc.cpp:4296-4302,4985-4990`).
- **Asset preload (P871)** — core-type `Preload` requests wired to the content
  loader (model warm-up, `bmp` texture suffix, colour-key/mipmap scope). Key:
  `src/Allegiance.ClientCore.Game/Rendering/AssetPreload.cs`,
  `Game1.Preload.cs`; native `WinTrek/trekigc.cpp:4912-4937`.
- **Options menu tree** — the native root + Sound/Game/Chat submenus
  (`WinTrek.cpp:4429-4442`, `:4502-4590`). Key:
  `src/Allegiance.ClientCore.Game/OptionsMenuModel.cs`, driven by
  `Game1.BenchmarkSharing.cs` (`UpdateOptionsMenu`/`DrawPrivacySettings`).
- **New-game wizard (P871)** — server/core/map selection plus the game name and
  owner MissionParams draft, opening from Find Server (N). Key:
  `src/Allegiance.ClientCore.Game/Games/CreateGameWizard.cs`,
  `Games/LobbyGameClient.cs`, `Game1.CreateGame.cs`; native
  `WinTrek/newgamescreen.cpp:1034-1041`, `lobby/client.cpp:390-465`.
- **Server browser model** — the listing mirror, the eight native sort columns,
  the eleven game filters and the filter dialog (native `gamescreen.cpp`). Key:
  `src/Allegiance.ClientCore.Game/ServerListing.cs`, `LobbyGameSort.cs`,
  `LobbyGameMetadata.cs` (`GameParams`/`NativeGameTypes`), `Game1.Session.cs`.
- **Lobby URL precedence (P947)** — the lobby directory URL is resolved once at
  startup with the precedence explicit `--lobby` (this session) > saved connect
  profile / player.json preferences > `ServerListing.LobbyUrlDefault`, then read
  by every consumer (host internet form, Play Online fetch, directory chat,
  career/identity, new-game wizard, P944 join targets) with a one-time
  `[lobby] using … (from …)` startup log. Key:
  `src/Allegiance.ClientCore.Game/LobbyUrl.cs`, the resolution in the
  `Game1` constructor (`Game1.cs`), `Game1.DirectoryChat.cs`.
- **Chat composer / compose recipients** — the five native compose states and
  the recipient a compose sends to. Key:
  `src/Allegiance.ClientCore.Game/Rendering/ComposeMachine.cs`,
  `Rendering/ChatTargetLogic.cs`, `Rendering/ChatSubjects.cs` (the native
  `ChatSubject` ship list: individual/group picks, shift-toggle, drag group),
  `Rendering/ChatSendPlan.cs` (one chat per subject for a group).
  *Native:* `WinTrek/console.cpp:66-455,1448-1507,2078-2193`; reports P456/P784/P818.
- **Team-lobby chat pane** — the native `ChatListPane` filter rules (players-only,
  lobby channel set, Filter Chats To All). Key:
  `src/Allegiance.ClientCore.Game/Rendering/ChatPaneModel.cs`,
  `Rendering/ChatTargetLogic.cs`, `Game1.Panes.cs`
  (`UpdateReadyCheck`/`DrawReadyCheckChat`).
- **Team screen / team-lobby pool (P864d)** — the port's TEAM SCREEN is the
  side-select overlay before admission and the ready-check overlay once seated;
  both now list the mission team lobby ("Not on a team", native
  `clintlib/clintlib.h:571`) **by name** with native's per-player status column
  (`WinTrek/teamscreen.cpp:378-386` team count, `:470-510` player rows,
  `:1330-1335` lobby row in the team list). Who is in the lobby comes from the
  normal player-info traffic — ships whose `ShipEntity.SideId` is
  `SIDE_TEAMLOBBY` (-2), named by `PLAYERINFO(10)`/`EXPORT(26)` and moved by
  `QUIT_SIDE(126)`/`JOIN_SIDE(125)`. Key:
  `src/Allegiance.ClientCore/State/ClientWorld.Lobby.cs`
  (`SideTeamLobby`, `TeamLobbyMembers()`, `SideHumanCount()` = native
  `SideNumPlayers`), `src/Allegiance.ClientCore.Game/Rendering/SideSelectLayout.cs`,
  `Rendering/ReadyCheckLayout.cs`, `Rendering/ReadyCheckRoster.cs`
  (`GatherLobby`), `Game1.Panes.cs` (`DrawSideSelect`/`DrawReadyCheck`) and
  `Game1.FlightInput.cs` (a player parked on -2 is routed to the team screen). A
  lobby player requests a side with Enter (`POSITIONREQ`), a team leader accepts
  a waiting player with A (`POSITIONACK`); the team lobby also offers the
  owner's R (randomize) and the leader's O/U/N/H controls. Server half:
  `src/Allegiance.Server/Server.Lobby.cs` `SendSideRoster` sends the native
  join-time player list (`SendLobbyMissionInfo`, `FedSrv/fsmission.cpp:3940-3971`)
  to a peer still choosing a side. Proof: `tools/p864d-teamlobby-proof/`, report
  `docs/reports/P864d_REPORT.md`. *Native:* `WinTrek/teamscreen.cpp`.
- **Host editing (P853b)** — the pre-game team lobby's two host affordances, the
  client half of P853's wire: the side leader's **team name** (native
  `TeamSettingsDialogPopup`, `WinTrek/teamscreen.cpp:856-1104` →
  `FM_CS_SET_TEAM_INFO(15)` with `squadID = NA`) and the mission owner's **game
  settings** form (native `NewGameScreen`, `WinTrek/newgamescreen.cpp:1034-1130`
  → `FM_CS_MISSIONPARAMS(113)`, validated client-side with the shared
  `MissionParamsMsg.Invalid(true)`). Rules live in
  `src/Allegiance.ClientCore/State/TeamNameEdit.cs` and
  `src/Allegiance.ClientCore/State/MissionSettingsForm.cs`; rows in
  `src/Allegiance.ClientCore.Game/Rendering/TeamNameEditLayout.cs`,
  `Rendering/MissionSettingsFormLayout.cs`; wiring in `Game1.Panes.cs`
  (`UpdateReadyCheck`, `HandleTeamNameEditKeys`, `HandleMissionSettingsKeys`) and
  `Game1.Session.cs` (`OnTextInput` text entry). The form starts from the whole
  `MissionParams` block the server advertises in `MISSIONDEF(38)`
  (`src/Allegiance.Core/Wire/MissionDef.cs` `Params`,
  `src/Allegiance.ClientCore/State/MissionInfo.cs` `Params`). Keys: T / G. Proof:
  `tools/p853b-hostedit-proof/`, report `docs/reports/P853B_REPORT.md`.
- **Input** — keyboard/mouse/joystick/gamepad rebindable.
  `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs`, `Game1.KeyBind.cs`.
  P941/P949 **input layouts**: `Rendering/InputLayouts.cs` is the two selectable
  default presets — `InputLayout.Port` (the port's keyboard flight scheme:
  W/S/A/D + arrow steering, the fresh-profile default since P949) and
  `InputLayout.Native` ("Classic Allegiance", native's `defaultinputmap.mdl`
  keyboard table plus its mouse/wheel map, whose stock table binds no steering
  key so it flies with the mouse/joystick). `Rendering/InputBindings.cs`
  (`ActionMap`) resolves the live map from the selected layout, including native
  mouse/wheel bindings (`MouseButtonBinding`). Persistence is
  `Rendering/KeyBindingStore.cs` (`keymap.json`:
  `{"layout":...,"bindings":{...}}`; a fresh profile writes nothing until the
  player changes a binding, and a legacy flat file loads as the Port layout).
  `Rendering/ControlHint.cs` is the one-time fresh-profile keys hint (60 s or any
  key, persisted as `HudSettings.ControlHintSeen`). Training prompts that name a
  key are audited against the Classic preset by
  `src/Allegiance.ClientCore/Training/TrainingPromptAudit.cs`.
- **Relative mouse capture (P950)** — SDL2 relative pixel deltas for mouse flight
  and spectator free-look, with centre-warp fallback in
  `src/Allegiance.ClientCore.Game/Flight/RelativeMouse.cs`. Capture ownership and
  UI/focus/exit release live in `src/Allegiance.ClientCore.Game/Game1.RelativeMouse.cs`;
  virtual-stick response settings stay in `src/Allegiance.ClientCore.Game/Flight/MouseFlightController.cs`.
  Native intent: `effect/efimage.cpp:182-209`, `engine/camera.cpp:408-425`;
  platform adaptation and proof: `docs/reports/RELATIVE_MOUSE_P950.md`.
- **Mouse-flight options (P948)** — native's persisted `FlipY` (invert mouse Y)
  and `MouseSensitivity` (default 0.6, 0.01 steps within [0.1, 2.0]) live in the
  player's `player.json` beside the other client options
  (`src/Allegiance.ClientCore.Game/IntroPreferences.cs`, `Normalize`) and drive
  `src/Allegiance.ClientCore.Game/Flight/MouseFlightController.cs`. The pure
  apply/step/label/key-repeat/notice rules are in
  `src/Allegiance.ClientCore.Game/Flight/MouseOptions.cs`; the wiring (startup
  load, save-on-change, the Ctrl+F6 settings rows and the in-flight
  `InvertMouse`/`MouseSensitivityUp/Down` actions) is
  `Game1.MouseOptions.cs`, and the intro Options → Game Options → Mouse Options
  page is `OptionsMenuModel.cs`. Tests:
  `tests/Allegiance.ClientCore.MonoGame.Tests/P948MouseOptionsTests.cs`.
  Report: `docs/reports/MOUSE_OPTIONS_P948.md`.
- **Host LAN game (P901)** — explicit `SkirmishOptions.HostLan` exposes the embedded server
  on TCP+UDP with discovery; offline defaults remain loopback/ephemeral. Shared setup:
  `src/Allegiance.SinglePlayer/HostLanScreenModel.cs`,
  `src/Allegiance.ClientCore.Game/Game1.HostLan.cs`; owned shutdown:
  `src/Allegiance.Server/Server.LanHost.cs`. Friends join beside fixed bots with native
  numeric team balance, including the native low-rank allowance. Passwords use the
  ordinary mission join gate. Tests: `tests/Allegiance.ClientCore.Tests/P901LanHostTests.cs`;
  process/window proof: `tools/p901-lan-proof/Program.cs`; native intent:
  `docs/parity/p901-lan-host.csv`; report:
  `docs/reports/P901_REPORT.md`.
- **Internet player hosting / NAT traversal (P936, port extension)** — explicit
  `SkirmishOptions.HostInternet` uses the shared host screen and
  `src/Allegiance.SinglePlayer/InternetHostPublisher.cs` to publish and refresh a
  20-second player listing. The default-off lobby observers in
  `src/Allegiance.Lobby/NatBindingResponder.cs` / `src/Allegiance.Lobby/NatObservationStore.cs`
  issue endpoint reception receipts; `src/Allegiance.Lobby/PunchSignaling.cs`
  introduces only observed endpoints. `src/Allegiance.Core/Transport/StunBinding.cs`,
  `src/Allegiance.Core/Transport/UdpTransport.Traversal.cs` and
  `src/Allegiance.Core/Transport/NatTraversalClient.cs` share the game socket for
  discovery, bounded probes and normal UDP join. Two lobby ports detect changed
  mappings; no relay, router mapping or public STUN dependency. Local NAT shims:
  `tests/_shared/P936NatShim.cs`. Report: `NAT_TRAVERSAL_P936.md`.
- **Skirmish (offline single player)** — `src/Allegiance.SinglePlayer/`
  (`SkirmishHost.cs`, `SkirmishOptions.cs`), hosted from `Game1.Skirmish.cs`.
- **Training** — the native training missions. `src/Allegiance.SinglePlayer/Training/`.
  P785 completion: the client joins its embedded host over loopback
  (`Game1.Training.cs`), the mission clock is `TrainingClock` (port of
  `zlib/ztime.cpp`, pause + debug clock shift) in
  `src/Allegiance.ClientCore/Training/`, and the pre/post slideshows are
  `SlideshowModel` + `TrainingSlideData` (native slides from the artwork corpus's
  tm_*.mdl) driven by `Game1.IntroScreen.cs`'s TrainingSlideshow view.
  P873 binds loopback login to the host trainee through `ShipSpawner`, defers the
  script until an airborne player pose is replicated, logs nested goal paths, and
  maps mission-local voice ids in `src/Allegiance.ClientCore/Training/TrainingSoundCatalog.cs`.
  Certification (including live binding gaps): `docs/parity/training-matrix.csv`,
  `tools/p873-training-audit.py`, `docs/reports/P873_REPORT.md`.
  Native training authoring commands (asteroid TSV and narration comments):
  `tools/p873-training-tools.py`.
- **Training slideshow artwork (P943)** — the native slide images, not just captions.
  Each mission's `tm_*.mdl` (native namespace table `SlideshowNamespaces`,
  `WinTrek/WinTrek.cpp:2473-2522`) is parsed by
  `src/Allegiance.ClientCore/Training/SlideshowScript.cs` (the slides list:
  `ImportImage`/`GroupImage`/`TranslateImage`/`StringImage`/`Point`, plus the narration
  comment block above each slide); `src/Allegiance.ClientCore/Training/SlideshowLayout.cs`
  evaluates it at the native 800×600 y-up layout (GroupImage paints its first child
  last, StringImage hangs below its point) and
  `src/Allegiance.ClientCore.Game/Game1.Slideshow.cs` draws it through
  `src/Allegiance.ClientCore.Game/Rendering/SlideshowArt.cs`, scaled to the window.
  A slide whose bitmap is absent falls back to its narration caption. Tests:
  `tests/Allegiance.ClientCore.Tests/P943SlideshowScriptTests.cs`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/P943SlideshowArtTests.cs`; captures:
  `docs/reports/P943_assets/`; report: `docs/reports/P943_REPORT.md`.
- **Local training lifecycle (P882a)** — `ITrainingLifecycleSite` in
  `src/Allegiance.ClientCore/Training/ITrainingLifecycleSite.cs` separates setup,
  authoritative respawn presence and death recovery from action/condition bindings.
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Lifecycle.cs` supplies real
  equipment and queued death/last-base events; `Mission.IsTraining` keeps the lesson
  graph in charge of completion. Normal translated keys pass through
  `src/Allegiance.ClientCore.Game/Training/TrainingInputGate.cs` before handlers in
  both HUD styles. Mission5 starts docked; other lessons start airborne.
  Certification and remaining gaps: `docs/reports/P882a_REPORT.md`.
- **Training site state and drone recipes (P909)** —
  `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs` binds command
  selection, viewed cluster, quick-chat acknowledgement, equipment and native camera ids
  to the real client; independent key latches live in
  `src/Allegiance.ClientCore/Training/SiteConditions.cs`.
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs` applies native
  tech substitutions, expendable base data, requested-ship turn math and rolled spawn orientation; Mission6/10
  use the live carrier-relative recipes. Live registries and fractions feed shared
  geometry/scanner predicates. Unused native sound/turn primitives are in
  `src/Allegiance.ClientCore/Training/AdditionalPrimitives.cs`.
  Both-style loopback tests: `tests/Allegiance.ClientCore.MonoGame.Tests/P909TrainingBindingsTests.cs`.
  Exact remaining gaps and the native caller audit: `docs/reports/P909_REPORT.md`.
- **Training command ownership and saved loadouts (P918)** — all four training
  command slots acquire/release native buoy consumers and rebuild typed execution
  goals while preserving the other slots in
  `src/Allegiance.Core/Sim/Ship.CommandSlots.cs`; `src/Allegiance.Core/Sim/Buoy.cs`
  owns the visible flag, 20/2 radii and last-consumer retirement. Death,
  replacement, NPC cluster completion, trainee arrived-cluster orders,
  side-quit Reset and mission exit share this path. Legacy training arrival
  also calls the ship's shared completion API. Ordinary server command chat
  also finalizes through it (`src/Allegiance.Server/Server.ShipCommands.cs`). Ordinary multiplayer retains its existing lazy Goto ownership.
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Loadouts.cs` caches native
  base-hull loadouts for local carrier resupply; cargo mounts come from hold
  indices. Both HUDs draw the same native decal through
  `src/Allegiance.ClientCore.Game/Game1.TrainingRendering.cs`.
  Native: `Igc/shipIGC.h:731-782`, `Igc/buoyigc.h:67-88`,
  `clintlib/clintlib.h:1437-1477,1640-1724`. Proof:
  `tests/Allegiance.ClientCore.MonoGame.Tests/P918TrainingOrderTests.cs`,
  `tests/Allegiance.Core.Tests/P918CommandSlotsTests.cs`,
  `tools/p918-training-proof/`; report: `docs/reports/P918_REPORT.md`.
- **Arena order policy and combat waves (P911)** — shared native Practice Arena /
  Free Flight targeting, assistance, pursuit and hidden patrol buoys in
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs`, reached through
  the optional `ServerHost.HandlePickDefaultOrder` callback. The live plan reader
  `src/Allegiance.Core/Sim/Ship.CommandPlan.cs` preserves the distinction between
  accepted orders and navigation-created plan buoys. Arena movement and mounted
  recipes run in the real simulation; headless and connected client tests reach
  49 projectile deaths with the controlled range fixture
  `tests/_shared/P911ArenaFiringRange.cs`. Respawn removal releases deferred buoy
  holds. Native: `training/mission6.cpp:82-287`, `training/mission10.cpp:78-283`,
  `Igc/shipIGC.h:707-718,2438`. Non-training tick comparison:
  `tools/p911-arena-proof/`; original parity audit:
  `docs/reports/P911_REPORT.md`.
- **Arena patrol pickables (P933)** — `src/Allegiance.Core/Model/Sector.Pickables.cs`
  retains mixed cluster-entry order and live identities; `src/Allegiance.Core/Model/MissionMap.cs`
  preserves imported map record order. Hidden/visible and navigation buoys share
  the list through `src/Allegiance.Core/Model/Mission.Registry.cs`. The arena
  correction in `src/Allegiance.SinglePlayer/Training/TrainingHost.Arena.cs` includes
  missiles/treasures/buoys and excludes the choosing ship's own missiles by
  launcher identity. Coincident centers use native retail's zero return without
  its debug assertion. Native: `Igc/clusterIGC.cpp:500-518,539-551`,
  `training/mission6.cpp:248-266`, `training/mission10.cpp:244-262`,
  `zlib/vector.h:97-108`, `zlib/zassert.h:54,67`. Regression and live M6/M10 proof:
  `tests/Allegiance.ClientCore.Tests/P933TrainingPatrolTests.cs`; report:
  `docs/reports/P933_REPORT.md`. Closes the three P911 patrol-dependent gaps.
- **Training client integration (P910)** — local IGC death/station events and
  chat recipient/default-drone hooks bind through
  `src/Allegiance.ClientCore.Game/Training/TrainingRuntime.cs` and
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Chat.cs`; mission-specific
  queued-order timeout stays in `src/Allegiance.Server/Server.ShipCommands.cs`.
  `src/Allegiance.ClientCore.Game/Game1.TrainingIntegration.cs` owns HUD/keymap
  restoration, modal quit and all exit cleanup for both styles. The chooser
  remembers its next lesson; native `-training` starts Free Flight. Real-window
  proof: `tools/p910-training-proof/`; row audit and remaining gaps:
  `docs/reports/P910_REPORT.md`.
- **Training loadout transactions (P932)** — station default reapplication in
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Lifecycle.cs` samples the
  hull plus equipment/cargo resale value before clearing cargo. Local hangar
  purchases run synchronously through
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Purchase.cs` and
  `src/Allegiance.ClientCore.Game/Game1.Hangar.cs`; the shared
  `src/Allegiance.ClientCore/Session/ConnectionSession.cs` completion event runs
  after equipment installation and before optional station launch. Both HUD
  styles use this transaction; multiplayer keeps its request/ACK path. Native:
  `training/trainingmission.cpp:810-854`, `clintlib/clintlib.cpp:1731-1744`.
  M3's station cargo fills before the missile filler; removing its mounted
  magazine leaves no spare for lesson 9 (native authored-loadout bug).
  Proof: `tests/Allegiance.ClientCore.MonoGame.Tests/P932TrainingLoadoutTests.cs`;
  Both-style window proof: `tools/p910-training-proof/` (`purchase` mode);
  report: `docs/reports/P932_REPORT.md`.
- **Training enemy ThingSite latch (P934)** — local training binds the trainee's
  side to two visibility slots in `src/Allegiance.Core/Sim/SideVisibilityState.cs`;
  `src/Allegiance.Core/Model/Sector.TrainingVisibility.cs` maintains the shared
  enemy eye, predictable knowledge and retained spotter identity. Every other
  side reads/writes that same enemy slot; multiplayer keeps independent sides.
  Native: `WinTrek/trekigc.cpp:1996-2010,2172-2249`. Nanite wingmen are exempt
  from damage retreat; targetless support uses the ordinary default-order path,
  including the accepted attack slot in `src/Allegiance.Core/Sim/Ship.cs`.
  Native: `Igc/shipIGC.cpp:1730-1747`, `Igc/shipIGC.h:2436-2459`.
  Both-style wire regressions:
  `tests/Allegiance.ClientCore.MonoGame.Tests/P934TrainingVisibilityBindingTests.cs`;
  real-window captures: `tools/p934-training-proof/`; audit:
  `docs/reports/P934_REPORT.md` (TRN-0215 corrects an unsupported flee claim).
- **Basic Flight input and narration proof (P940)** — the real-window pilot in
  `src/Allegiance.ClientCore.Game/Game1.TrainingProof.BasicFlight.cs` checks native
  staged locks through actual keyboard/mouse handlers, both backslash variants,
  wheel and SPACE, then real docking in both HUD styles. Shared prompt-key routing
  lives in `src/Allegiance.ClientCore.Game/Game1.Training.cs`; the demo reset is in
  `src/Allegiance.ClientCore/Training/Mission2.cs`. Connected M2/M3 share simulation
  movement in `src/Allegiance.SinglePlayer/Training/TrainingHost.cs`.
  `src/Allegiance.ClientCore.Game/Training/TrainingRuntime.cs` and
  `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs` serialize native
  action lists with the host tick, preserving client-before-host lock order.
  `src/Allegiance.ClientCore.Game/Audio/TrainingVoicePcm.cs` decodes installed
  Vorbis narration for the sound manager's ordinary completion detection. The M3
  pilot in `src/Allegiance.ClientCore.Game/Game1.TrainingProof.cs` aims with real
  mouse deltas and fires at the native demonstration/kill gates, conserving ammo
  while the restored narration plays (`training/mission3.cpp:717-723,875-882`).
  Older loopback drivers in `tests/Allegiance.ClientCore.MonoGame.Tests/P881aTrainingMissionsTests.cs`
  bind the M2 client autopilot callback and damp the M3 bay approach through real wire controls.
  Proofs: `tools/p912-training-proof/`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/P940BasicFlightInputTests.cs`,
  `tests/Allegiance.ClientCore.MonoGame.Tests/P912TrainingWindowTests.cs`.
  Native: `training/mission2.cpp:157-163,298-301,391-396,516-521,585-613,725-765`,
  `training/playsoundaction.cpp:35-43`. Report: `docs/reports/P940_REPORT.md`.
- **Lesson universes and window completion (P912)** — M2 bounds use a private
  constants copy through `src/Allegiance.Core/Model/Mission.cs`; M8 cargo/tech
  and native M5 drone placement live in
  `src/Allegiance.SinglePlayer/Training/TrainingHost.cs` and
  `src/Allegiance.ClientCore/Training/Mission5.cs`. Connected M5/M8 orders use
  real simulation plans (`src/Allegiance.SinglePlayer/Training/TrainingHost.Bindings.cs`),
  with native predictable-object visibility and station combat in
  `src/Allegiance.Core/Sim/Ship.NpcUpdates.cs`. Transfers rebase the model clock
  in `src/Allegiance.Server/ServerHost.cs` (native `Igc/shipIGC.h:235-242`)
  so the arrival sector integrates the exit before testing collisions. Docked training previews stream their confirmed sector through
  `src/Allegiance.Server/Server.ShipCommands.cs` and
  `src/Allegiance.ClientCore.Game/Game1.Commanding.cs`. Shared team-pane drone
  entries and last-known status live in `src/Allegiance.ClientCore.Game/Rendering/TeamPaneLayout.cs`.
  Training sonic chat shares the current typed-chat recipient plan in
  `src/Allegiance.ClientCore.Game/Game1.Session.cs`; command height drags use
  `src/Allegiance.ClientCore.Game/Rendering/CommandCamera.cs`.
  Explicit command-view input clears a scripted cockpit pane mask in
  `src/Allegiance.ClientCore.Game/Game1.ConsoleModes.cs`
  (`WinTrek/WinTrek.cpp:6833-6843,9894-9932`).
  Friendly Defend chat arms a real escort in the training host; readiness and
  native goal construction share one replicated-world lock in
  `src/Allegiance.ClientCore.Game/Game1.Training.cs`.
  The opt-in key/pointer
  driver is `src/Allegiance.ClientCore.Game/Game1.TrainingProof.Advanced.cs`;
  window assertions and receipts are in `tools/p912-training-proof/` and
  `tests/Allegiance.ClientCore.MonoGame.Tests/P912TrainingWindowTests.cs`.
  Training joins require `src/Allegiance.Core/Transport/TcpTransport.cs` to
  queue its greeting before exposing `IsConnected`, so concurrent keep-alive
  sends cannot precede it; the merged regression is
  `tests/Allegiance.Core.Tests/Transport/TcpClientGreetOrderTests.cs`.
  The audit distinguishes completed window proof from the interrupted repeat gate.
  Native: `training/mission2.cpp:102-103`, `training/mission5.cpp:84-106,180-247`,
  `training/mission8.cpp:87-145,442-448`, `WinTrek/trekigc.cpp:1996-2010`. Audit: `docs/reports/P912_REPORT.md`.
- **Training combat branches (P917)** — the opt-in pilot in
  `src/Allegiance.ClientCore.Game/Game1.TrainingProof.cs` hunts a Mars miner with
  ordinary targeting, cloak, mouse flight and sniper fire; the escort policies in
  `src/Allegiance.ClientCore.Game/Game1.TrainingProof.Advanced.cs` time real repair
  and wingman orders. `tools/p912-training-proof/Program.cs` observes actual deaths,
  last live sectors and the exact native ending messages through the final five-second
  announcement goal. Both HUD styles run in
  `tests/Allegiance.ClientCore.MonoGame.Tests/P912TrainingWindowTests.cs`.
  `src/Allegiance.ClientCore/Training/Mission8.cs` switches Tkela only after trainee
  hull damage; `src/Allegiance.Core/Sim/Ship.cs` exempts accepted station attack
  targets from wingman retreat. Weapon mount numbers are per equipment type:
  `src/Allegiance.ClientCore.Game/Rendering/SelectedMountWeapon.cs` skips a shield
  or dispenser sharing mount zero when resolving the swapped Nanite gun
  (`clintlib/clintlib.cpp:1585-1588`). Regressions:
  `tests/Allegiance.ClientCore.Tests/P917TrainingBranchTests.cs`.
  `tests/Allegiance.ClientCore.MonoGame.Tests/Hud18TargetPaneTests.cs`.
  `src/Allegiance.Server/Server.Lobby.cs` leaves training station losses to the
  lesson graph rather than eliminating the losing side's remaining ships
  (`WinTrek/trekigc.cpp:4325-4362`, `training/mission8.cpp:629-638`).
  `src/Allegiance.SinglePlayer/Training/TrainingHost.cs` replaces a repair
  objective when a station Goto is accepted, preserving native single-plan
  ownership (`Igc/shipIGC.h:750-774`).
  Native: `training/mission4.cpp:777-844`, `training/mission8.cpp:595-650`,
  `training/GetShipIsDamagedCondition.h:32-34`, `Igc/shipIGC.cpp:1730-1747`.
  Audit: `docs/reports/P917_REPORT.md`.
- **Sampled training branch pilot (P931)** — simulation-time key edges and
  moving-sight-line steering in
  `src/Allegiance.ClientCore.Game/Training/TrainingProofPilot.cs`, with the
  cloak/intercept input policy in `src/Allegiance.ClientCore.Game/Game1.TrainingProof.Hunt.cs`
  and visible in-flight repair budgeting in
  `src/Allegiance.ClientCore.Game/Game1.TrainingProof.Advanced.cs`.
  `tools/p912-training-proof/Program.cs` records frame/simulation timing,
  emitted inputs and loaded-binary hashes. The shared pilot forecasts ordinary digital
  thrust combinations through the actual sampled frame. Its isolated user key profile keeps
  Up thrust reachable without consuming native Insert/AcceptCommand; `tools/p931-training-qualify.py` runs bounded fresh-window
  cohorts for both HUD styles. Physics regression:
  `tests/Allegiance.ClientCore.MonoGame.Tests/P931TrainingPilotTests.cs`;
  shipping `src/Allegiance.ClientCore/Training/TrainingClock.cs` retains whole
  frame intervals (native `zlib/ztime.h:50-77` limits only `_DEBUG_TRAINING`),
  with red/green coverage in `tests/Allegiance.ClientCore.Tests/P931TrainingClockTests.cs`.
  `src/Allegiance.ClientCore.Game/Game1.TrainingIntegration.cs` also retains whole
  framework frame intervals during lessons and restores the saved cap on every exit;
  `tests/Allegiance.ClientCore.MonoGame.Tests/P910TrainingIntegrationTests.cs` covers
  all exit routes in both HUD styles.
  Verdicts and native citations: `docs/reports/P931_REPORT.md`.
- **Training mission completion bindings (P881a)** — Basic Flight and Basic Weaponry run
  start-to-finish through the production path: client orders/target picks use the real
  `ORDER_CHANGE` (`src/Allegiance.ClientCore.Game/Game1.Training.cs`), the own ship's applied
  controls/state are `TrainingOwnState` in `src/Allegiance.ClientCore.Game/Training/TrainingRuntime.cs`,
  and `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs` binds autopilot, camera,
  equipment swaps/rearm and damage to the embedded host. The host flies a real station plan
  (`src/Allegiance.SinglePlayer/Training/TrainingHost.cs`) and the lesson ends on the server's
  DOCKED latch. `src/Allegiance.ClientCore/Flight/OwnShipSimulator.cs` supplies the `turnToFace`
  hull kinematics. Report: `docs/reports/P881a_REPORT.md`.

- **Training waypoint orders (P892d)** — native Warp targets resolve in both training sites;
  `src/Allegiance.ClientCore/State/ClientWorld.Orders.cs` retains local/CHATBUOY command
  geometry for legal queued-order acceptance. M4 queues its aleph order and the host applies
  constructor station data, asteroid build targets and scout attack targets in
  `src/Allegiance.SinglePlayer/Training/TrainingHost.cs`. The key-only window driver lives in
  `src/Allegiance.ClientCore.Game/Game1.TrainingProof.cs`; native P remains subject to the
  command-view filter, while G cloaks. Report: `docs/reports/P892_REPORT.md`.

## 5. Bots / MCP (`src/Allegiance.Bot`, `tools/allegiance-mcp`)

- **BotClass client** — the minimal real-client used by bots.
  `src/Allegiance.Bot/Bot.cs`, `Program.cs`.
- **MCP driver / server** — the stdio JSON-RPC tool surface that spawns and
  steers bots against a live server. `tools/allegiance-mcp/Program.cs`,
  `tools/allegiance-mcp/McpTools.cs` (tool definitions), `Catalog.cs`
  (hull/part/sector resolution), `BotManager.cs` (behavior).
- **Bot behaviors** — weave/loiter/combat, scenarios, research, defense, siege.
  `tools/allegiance-mcp/BotManager.*.cs` (Combat, Defense, Siege, StrikeMass,
  Research, Support, Coordination, Orders).
- **Bot decision golden corpus (P986)** — baseline record of the current MCP bot
  driver’s pure decisions before any goal-stack/profile refactor. Recorder seam:
  `tools/allegiance-mcp/BotDecisionRecorder.cs`; golden tests and corpus:
  `tests/Allegiance.ClientCore.Tests/P986BotDecisionCorpusTests.cs`,
  `tests/Allegiance.ClientCore.Tests/GoldenCorpus/BotDecisions/*.json`.
  Regenerate with `UPDATE_CORPUS=1`. Report:
  `docs/reports/BOT_DECISION_CORPUS_P986.md`.
- **Recorded bot decision corpus (P987)** — real seeded 2-side/3-side input
  snapshots, deduped/sampled to `GoldenCorpus/BotDecisions/recorded.json` and
  replayed by `tools/allegiance-mcp/BotDecisionReplay.cs` through the same pure
  decision cores. Recording fixture/test:
  `tests/Allegiance.ClientCore.Tests/P987DecisionRecordingLiveTests.cs`,
  `tests/Allegiance.ClientCore.Tests/P987RecordedBotDecisionCorpusTests.cs`;
  re-record with `tools/p987-bot-decision-corpus/record.sh`. Full inventory,
  uncovered reasons, drift proofs and overhead:
  `docs/reports/BOT_DECISION_CORPUS_SLICE2_P987.md`.
- **Ordered miner / Piglet routines (P905)** — explicit `spawn_bot` behaviors
  `miner`, `docker`, `deathmatch`, `chatter` in
  `tools/allegiance-mcp/BotManager.Routines.cs`, with typed state and timers in
  `tools/allegiance-mcp/NativeBotRoutines.cs`. The `bot_behavior` tool pushes a
  named C# routine or pops to the retained previous role; there is no scripting
  host. Ordered peer miners supplement the engine drones; showcase keeps its
  existing fleet and economy. Chat is opt-in and capped per worker. Native:
  `Wopr/Wopr/Strategies/Miner.cs:141-284`, `Pigs/Piglets/docker.pig:36-90`,
  `Pigs/Piglets/dm.pig:65-174`, `Pigs/Piglets/chatter.pig:217-244`.
  Local income comparison: `tools/p905-local-income-proof.py`.
  Stack/timer replacement proposals await approval in
  `docs/reports/P905_REPORT.md`; their matrix statuses remain partial.
- **Showcase role fallback (P821)** — native hull ability flags do not define
  scout/interceptor duties. Missing roles score legal small craft for the requested
  duty, retaining variety and purchase gates: `tools/allegiance-mcp/TechLoadoutSelector.cs`,
  `tools/allegiance-mcp/BotManager.TechLoadouts.cs`.
  Census/local proof: `tools/p819-side-bias-proof/P821Catalogue.cs`;
  report: `docs/reports/P821_ROLE_RESOLUTION.md`.
- **Scenario spawning** — `tools/allegiance-mcp/BotManager.Scenarios.cs`,
  `SkirmishDriver.cs`, `Showcase.cs`, `ScenarioPlanner.cs`.
- **Showcase resource opening and defensive-field proof (P953)** — an automatic
  topology rendezvous yields to a side-observed resource clash after discovery.
  If still unscouted after enemy recon and the opening raid opportunity, it
  reuses the recon slot in `tools/allegiance-mcp/SideIntel.cs` and
  `tools/allegiance-mcp/BotManager.Exploration.cs`. Later rock discoveries can
  upgrade the clash to a richer area; explicit operator targets retain ownership.
  The research
  coverage fixture in `tests/Allegiance.ClientCore.Tests/SC4ShowcaseLiveTests.cs`
  orders its normally produced mine-layer drones to defend home alephs, retaining
  both-side chaff/probe/mine minimums and requiring long-lived defensive models.
  Native layers request a location (`FedSrv/FedSrv.CPP:11518-11543`). Report:
  `docs/reports/SHOWCASE_REGRESSIONS_P953.md`.
- **Desired fleet / recovery epoch (P900)** — multiplayer showcase retains
  scenario arguments and per-side slots at the supervisor, detects corroborated
  probe loss, replaces worker processes with bounded backoff, and replays only
  uncanceled slots. `tools/allegiance-mcp/WorkerSupervisor.Recovery.cs`,
  `tools/allegiance-mcp/BotManager.Fleet.cs`; process health and requested/active/
  recovering/failed fleet counts are separate. Game-owned skirmish disables
  recovery. Usage: `docs/MCP_FLEET_RECOVERY.md`; real process restart proofs:
  `tests/Allegiance.ClientCore.Tests/P900FleetRecoveryLiveTests.cs`;
  report: `docs/reports/P900_REPORT.md`.
- **Finale re-arm / new-mission objectives (P656, P831)** — after a rotation the
  next match's finale is re-armed only from fresh per-bot state; a fresh life
  always resolves its objectives (an objective the fogged world cannot derive yet
  becomes "no fixed objective" and the clash assignment re-issues it), and a bot
  blocked past a bounded grace is excluded and named instead of holding the
  deadline forever. `tools/allegiance-mcp/BotManager.Finale.cs`
  (`RearmRotatedFinales`, `LogRearmGrace`), `tools/allegiance-mcp/BotManager.Lifecycle.cs`
  (`RefreshLife`, `LogObjectiveFallback`), `tools/allegiance-mcp/BotManager.Exploration.cs`
  (`AssignClashTargets`); report: `docs/reports/P831_FALLBACK_BOMBER_REARM.md`.
- **Finale target honesty + death evidence (P896)** — a side is only ever given a
  station from its OWN bot worlds (`ObservedFinaleStations`, `PickFinaleTarget`);
  a side that has observed nothing holds a legal `NoTarget` state and re-picks the
  moment one of its worlds reports an enemy station (`ResolveFinaleTargets`,
  `ArmFinaleSide`). A station counts as dead only on real evidence — the
  WHOLE-MISSION `STATION_DESTROYED(59)` recorded by `ClientWorld.StationWasDestroyed`
  or a station the side observed and then lost — never on absence, which under
  native fog only means "not observed" (`Fsmission.cpp:4169-4250`).
  `src/Allegiance.ClientCore/State/ClientWorld.cs`, `tools/allegiance-mcp/BotManager.Finale.cs`;
  report: `docs/reports/P896_REPORT.md`.
- **Strike wave sizing (P801, P896, P959)** — `tools/allegiance-mcp/StrikeVolley.cs`
  computes the massed wave from the real station type and granted loadouts against
  the native damage/regen law; `tools/allegiance-mcp/BotManager.StrikeMass.cs`
  holds the wing at a standoff (nose on the station, so the native missile lock
  builds before the go time), sizes it from observed losses/defenders behind
  `finale_survival_margin` (P959 default 1 = one hull per seen defender/loss;
  0 = the bare P801 kill + one spare), divides the ideal hull count by the
  measured landing share, and refuses to commit a wave whose loadout cannot damage
  the target. P959 also arms the live wing at the finale (`ArmFinaleWing`), refits
  it legally before the window (`finale_prearm_minutes`), grows it from spare
  fighters up to `finale_max_wing` and can fly one early pressure wave
  (`finale_early_pressure_minutes`, default 0 = off). Reports:
  `docs/reports/P801_BOMBER_MASSING.md`, `docs/reports/P896_REPORT.md`,
  `docs/reports/FINALE_STRIKE_MASS_P959.md`.
  P961's opt-in `finale_reachable_strike` counts threats near the staging/release
  path, caps requirements at live armed reserved hulls with a known route before
  the deadline, retargets unreachable volleys and commits useful assembly plateaus.
  The reachable subset's planner also owns the logged kill count and delivery
  feedback coefficients. Pure decisions: `tools/allegiance-mcp/FinaleAssembly.cs`; client-world adapter:
  `tools/allegiance-mcp/BotManager.ReachableStrike.cs`. Clash assignments preserve
  the committed wing's destination; legal fallback loadouts do not trigger upgrade
  trips, and one preparation refit is allowed per prearm/arm cycle. Report:
  `docs/reports/FINALE_REACHABLE_STRIKE_P961.md`.
  Local measurement harnesses: `tools/p896-strike-proof/`, `tools/p959-strike-proof/`.
- **Finale duty vs strike hardware (P733, P959, P973)** — `FinaleBomberActive` is
  the finale DUTY flag: a reserved pilot whose side has no bomber tech and no
  station-damaging weapon still relaunches with the best legal combat loadout, its
  purchase reason reads "research finale", and the server's bounded deadline ends
  the match (`docs/reports/P733_TECH_FINALE_AND_SPEED.md`). Whether a hull may be
  counted, staged or released in a strike wave is the separate predicate
  `FinaleCarriesStrikeHardware` (granted inventory + the real station damage
  matrix), which builds `FinaleStrikeMassTick`'s `armed` set.
  `tools/allegiance-mcp/BotManager.Finale.cs` (`PrepareFinaleRelaunch`,
  `ArmFinaleWing`), `tools/allegiance-mcp/BotManager.StrikeMass.cs`,
  `tools/allegiance-mcp/BotManager.ReachableStrike.cs`;
  report: `docs/reports/P959_REGRESSIONS_P973.md`.
- **Research builder rock selection (P798, P973)** — the research commander picks a
  construction target only from the side's own recorded rocks (the per-side P797/P798
  intel), never from a single peer's world: a rock a side discovers and consumes with
  its own build between two 1 Hz intel samples otherwise leaves a build on a rock no
  recorded knowledge names. `tools/allegiance-mcp/BotManager.Research.cs`
  (`ResearchSnapshot(..., recordedRocks)`), `tools/allegiance-mcp/ResearchCommander.cs`;
  report: `docs/reports/P959_REGRESSIONS_P973.md`.
- **Generated research scheduling and receipts (P942)** — canonical generated
  plans execute research before optional production through
  `tools/allegiance-mcp/ResearchCommander.cs` and the opt-in priority in
  `src/Allegiance.Core/Model/ResearchTree.cs`; authored plans retain their order.
  Native purchase/prerequisite rules are unchanged. Log-only completion times
  and per-match side totals live in `src/Allegiance.Server/Server.ResearchTelemetry.cs`.
  Paired civilization combat and full research closure proofs:
  `tests/Allegiance.ClientCore.Tests/P942ResearchCombatLiveTests.cs`;
  report: `docs/reports/P942_REPORT.md`.
- **Prosperity commander (P832b)** — when MISSIONDEF enables team money,
  `tools/allegiance-mcp/ResearchCommander.Prosperity.cs` grows/replaces miners,
  banks half observed income and funds the economic-victory bucket above a
  replacement/defence reserve. `BotManager.ResearchTactics.cs` reads enemy
  mission-wide BUCKET_STATUS; `ResearchTactics.cs` redirects half the available
  fighters toward observed miners at 10% enemy investment, retaining home
  bomber interception. Strategy is a port policy; actions and visibility follow
  native `FedSrv/FedSrv.CPP:7182-7196`, `Igc/sideigc.h:237-243`.
  Seeded proof preset: `tools/p832b-prosperity.json`.
  Report: `docs/reports/P832B_PROSPERITY_BOTS.md`.
- **Flag raids / guards (P832c)** — wire-goal-gated carrier, escort, pedestal
  guard and visible-thief interception in `tools/allegiance-mcp/BotManager.Flags.cs`.
  `tools/allegiance-mcp/ScenarioCoordination.cs` shares one raid/guard quota across
  workers; `spawn_scenario` preset `flags` requires a configured flags goal and
  suppresses the bomber finale (ordinary showcase/skirmish also detects flags).
  Native pickup/secure/respawn remain server rules; report:
  `docs/reports/P832C_FLAG_BOTS.md`.
- **Artifact hunting (P832d)** — `tools/allegiance-mcp/BotManager.Artifacts.cs`
  searches neutral sectors in expanding bands, collects side-observed artifacts and reuses the
  flag return/escort/intercept controller and worker quota. `ArtifactSighting`
  in `tools/allegiance-mcp/SideIntel.cs` carries expiring observations and
  pickup tombstones across workers. The `artifacts` scenario preset requires
  a native artifacts goal; showcase/skirmish detect it automatically and
  suppress the bomber finale. Report: `docs/reports/P832D_ARTIFACT_BOTS.md`.
- **Conquest take / hold (P832e)** — `tools/allegiance-mcp/BotManager.Conquest.cs`
  reads native-visible flag stations and GAME_STATE percentages, shares the
  flag/artifact offense/denial budget across workers, reuses legal strike refits,
  bomber fire, escort screening and landing bays. A conquest match fields one
  legal boarding preference per side and suppresses timed finales. Native station
  event context and reasons: `src/Allegiance.Core/Model/Mission.StationVictory.cs`.
  Report: `docs/reports/P832E_CONQUEST_BOTS.md`.
- **Rig driver** — `scripts/mcp_driver.py` (drives the MCP exe from a JSONL
  command file). Reports `SC1_SHOWCASE_SKILLS.md`, `P539_MCP_COMBAT_BOTS.md`.
- **Kills / deathmatch hunting (P832f)** — `tools/allegiance-mcp/BotManager.Kills.cs`
  observes public KILL_SHIP credits and PLAYERINFO counters, adds hunt/recovery
  duties to the existing `ScenarioCoordination.cs` budget, focuses weak visible
  enemy hulls, and returns damaged pilots through ordinary docking/relaunch.
  Pods earn no team kills. The `kills` scenario preset and goal-aware skirmish
  suppress timed finales; miners, research and healthy support retain their jobs.
  Seeded local recipe: `tools/p832f-kills.json`; report:
  `docs/reports/P832F_KILLS_BOTS.md`.
- **Fleet-scaled opening (P837)** — the opening's numbers are rig values and are
  scaled down for a small fleet: the clash fallback delay and the commit delay
  scale with the side's combat bots, a side's own home is never the clash
  objective, and with nothing contested known both sides commit to the same
  balanced topology midpoint between the homes. A small skirmish (≤ 12 bots) is a
  "meeting engagement" (`spawn_scenario meeting_engagement`, sent by
  `SkirmishDriver.cs`): it fights what it meets while travelling, skips the P539d
  staging barrier and keeps a single scout. The re-arm grace scales the same way.
  Key: `tools/allegiance-mcp/SideIntel.cs` (`ClashScoring.Choose`,
  `ClashScoring.Midpoint`, `ScoutOpening.ClashDelaySecondsFor`,
  `ClashFallbackSecondsFor`, `MeetingEngagementCombatBots`),
  `tools/allegiance-mcp/BotManager.Exploration.cs` (`AssignClashTargets`,
  `AssignExplorers`, `TryDriveExploration`), `tools/allegiance-mcp/BotManager.cs`
  (`DriveCombat`), `tools/allegiance-mcp/BotManager.Finale.cs`
  (`FinaleAwaitingRearm`, `RearmGraceSecondsFor`),
  `src/Allegiance.SinglePlayer/SkirmishOptions.cs` (skirmish `ShowMap` by size);
  report: `docs/reports/P837_BOT_LIVE_REGRESSIONS.md`.

## 6. Lobby (`src/Allegiance.Lobby`)

- **Server directory** — the ASP.NET app players browse to find servers.
  `src/Allegiance.Lobby/Program.cs`, `LobbySettings.cs`, `ServerRegistry.cs`.
  Stage docs: `docs/ALLEGIANCE_LOBBY_STAGE*_P17*.md`.
- **Published game settings** — the native `MissionInfo` fields (developments,
  lives, scores, zone, goal bits, rank window, in-progress) on the register
  payload and the listing, so the client's server-list filters/sort columns are
  real. Key: `src/Allegiance.Lobby/ServerListing.cs` (`GameParamsDto`/
  `GameParamsRequest`), `src/Allegiance.Server/LobbyHeartbeat.cs`
  (`LobbyGameParams.FromMission`), `src/Allegiance.Server/Server.cs`
  (`LiveGameParams`). Report `docs/reports/P806_SCREENS_LOBBY_COMMANDER.md`.
- **Directory chat (P871, port extension)** — authenticated directory-wide chat
  relay with sequences, own-echo suppression and per-account mute. **Native has
  no lobby/directory chat** (every native chat target is in-mission,
  `Igc/igc.h:2731-2734`; the lobby only ingests `FM_S_LOG_CHAT_MESSAGE(216)` for
  moderation logging, `lobby/messagesls.h:152`), so P871's cite
  `clintlib/appmsg.cpp:2640-2665` (the in-mission relay) is corrected here; the
  borrowed rules are that relay's own-echo suppression and per-player mute
  (`WinTrek/trekigc.cpp:4492`, `clintlib/clintlib.h:326`). Key:
  `src/Allegiance.Lobby/Chat/DirectoryChat.cs`, `Chat/ChatEndpoints.cs`,
  `src/Allegiance.ClientCore.Game/Chat/DirectoryChatLog.cs`,
  `Chat/LobbyChatClient.cs`, `Game1.DirectoryChat.cs`.
- **New-game requests (P871/P871b)** — the native `CREATE_MISSION_REQ`/ACK/NACK
  flow as the lobby's HTTP wizard: authoritative server/core/map catalog,
  authenticated create, owner-only parameter edits, cancel and a
  token-authenticated server claim — plus the runtime provisioning that turns a
  claim into a live mission: the heartbeat polls/claims the relay
  (`src/Allegiance.Server/LobbyHeartbeat.cs`), the sim thread assembles the
  wizard's world (`src/Allegiance.Server/Server.Provisioning.cs`,
  `PlayerGameProvisioning.cs`, which maps the native `MissionParams` onto a
  `GameConfig`), the mission becomes the join target and the client joins that
  server on the ACK (`Game1.CreateGame.cs`). The mission parameters cross HTTP as
  base64 of the native packed struct (`paramsWire`), never as JSON fields. Key:
  `src/Allegiance.Lobby/Games/`,
  `src/Allegiance.ClientCore.Game/Games/`.
- **Per-server game capacity (P883b)** — `ServerConfig.MaxGames` defaults to
  native's 99 (minimum 1); configured and player-created missions count toward
  admission in `src/Allegiance.Server/Server.Provisioning.cs`. The heartbeat's
  mission snapshot publishes `currentGames`/`maxGames` through
  `src/Allegiance.Lobby/ServerRegistry.cs` and the create-game catalog. The shared
  `src/Allegiance.ClientCore.Game/Games/CreateGameWizard.cs` displays cur/max and
  refuses full servers. Native: `FedSrv/FedSrv.CPP:9560-9562,8107,8850`,
  `lobby/server.cpp:114`, `lobby/client.cpp:361-362`,
  `WinTrek/CreateGameDialog.h:20,87`. Report: `docs/reports/P883_REPORT.md`.
- **Version advertisement** — `GET /api/update/info` (current/minimum client
  version + the signed release feed URL), the HTTP analogue of native
  `AUTO_UPDATE_INFO(253)` (`lobby/messageslc.h:45-52`). Key:
  `src/Allegiance.Lobby/LobbyEndpoints.cs`, `LobbySettings.cs`
  (`CurrentClientVersion`/`MinimumClientVersion`/`UpdateFeedUrl`).
  Report `docs/reports/P810_AUTO_UPDATE.md`.
- **Logon version gate (P894)** — the advertised minimum client version is now
  **enforced** at the logon the port's client performs: the LOGONREQ's optional
  tagged `P894` client-version extension (`src/Allegiance.Core/Wire/LogonRequest.cs`,
  `ClientVersion`) is compared with the server's resolved minimum
  (`ServerConfig.ResolveMinimumClientVersion`, `ALLEGIANCE_MINIMUM_CLIENT_VERSION`) in
  `src/Allegiance.Server/Server.Dispatch.cs` (`CheckClientVersion`); a refused client
  shows the reason + updater pointer on its connect-failure screen
  (`src/Allegiance.ClientCore.Game/Game1.Session.cs`). Native:
  `lobby/client.cpp:272-309` (`crcFileList` → `AUTO_UPDATE_INFO`, `verLobby` NACK).
- **Lobby protocol gate + approved-server list (P894)** — `LobbyProtocol`
  (`src/Allegiance.Core/LobbyProtocol.cs`) is native's `LOBBYVER_LS` match at
  registration; `src/Allegiance.Lobby/ServerAllowList.cs` +
  `LobbySettings.AllowedServers`/`ServerFilterMode` port native's `IsServerAllowed`
  (`lobby/server.cpp:24-84,131,138`). Key: `src/Allegiance.Lobby/LobbyEndpoints.cs`.
- **Hidden games and the richer listing (P894)** — paused hosts are never advertised
  and a zero-player *client-created* mission is hidden unless it is a server-owned
  arena (`ServerRegistry.IsMissionAdvertised`, native `lobby/mission.cpp:77-86`);
  listings also carry `location`, current/max games, the native percent load and the
  lobby-normalized start time, with `GET /api/servers/cores` (native master core list)
  and `GET /api/servers/stats` (native counters). Report `docs/reports/P894_REPORT.md`.

- **Web lobby Join links (P944, port addition)** — version-1
  `allegiance://join?lobby=<encoded HTTPS URL>&id=<listing id>&v=1` handoff:
  pure parser/launch isolation in `src/Allegiance.ClientCore/Session/JoinLink.cs`,
  bounded listing resolution and confirmation model in
  `src/Allegiance.ClientCore.Game/JoinLinkConfirmation.cs`, shared UI in
  `src/Allegiance.ClientCore.Game/Game1.JoinLink.cs`. Confirmation uses the browser's
  `BeginConnect` with the resolved listing and a transient punch lobby origin;
  saved lobby preferences stay unchanged. Desktop argv isolation lives in
  `src/Allegiance.ClientCore.MonoGame/Program.cs`; Cocoa's zero-window SDL URL
  event is captured by `src/Allegiance.ClientCore.MonoGame/MacJoinLinkEvents.cs`.
  OS registrations live under `packaging/`; safe web links in
  `src/Allegiance.Lobby/wwwroot/app.js`. Report: `docs/reports/P944_REPORT.md`.

- **Game-password join UX (P970)** — the mission password stays on the server
  (`FedSrv/FedSrv.CPP:648-661`), but the port now advertises the FACT so the
  player is asked before connecting: `passwordRequired` rides the server
  heartbeat (`src/Allegiance.Server/LobbyHeartbeat.cs`,
  `src/Allegiance.Server/Server.Broadcast.cs`) into the directory
  (`src/Allegiance.Lobby/ServerRegistry.cs`, `src/Allegiance.Lobby/ServerListing.cs`,
  lock in `src/Allegiance.Lobby/wwwroot/app.js`) and the client listing
  (`src/Allegiance.ClientCore.Game/ServerListing.cs`). The shared prompt model is
  `src/Allegiance.ClientCore.Game/Rendering/JoinPasswordPrompt.cs`; the refusal
  returns the player to the join screen with the field focused
  (`src/Allegiance.ClientCore.Game/Game1.Session.cs`, `Game1.JoinLink.cs`,
  `JoinLinkConfirmation.cs`), and the opt-in per-server store is
  `src/Allegiance.ClientCore.Game/SavedGamePasswords.cs`. Parity rows:
  `docs/parity/p970-join-password.csv`. Report:
  `docs/reports/JOIN_PASSWORD_UX_P970.md`.

## 6b. Auto-update (`src/Allegiance.Update`)

- **Signed release feed** — `latest.json` + `manifest-<version>.json` (one
  SHA-256 + Unix mode per application file) + the mirrored files; ECDSA P-256
  signature over the exact manifest bytes. Native analogue: the CRC file list
  over FTP (`clintlib/AutoDownload.cpp:225-282`).
- **Client updater** — apply-on-restart (`UpdateApplier`), feed check + per-file
  delta download + staging (`UpdateClient`), lobby advertisement fetch
  (`LobbyUpdateInfoClient`). Key: `src/Allegiance.Update/UpdateClient.cs`,
  `UpdateApplier.cs`, `UpdateManifestBuilder.cs`, `UpdateSignature.cs`;
  client boot hook `src/Allegiance.ClientCore.MonoGame/ClientUpdateBootstrap.cs`.
- **Apply transaction (P976)** — the apply-on-restart is failure-atomic: the
  whole staged set is verified before the first mutation, replaced/deleted files
  (and `installed.json`) are backed up in `autoupdate/apply-backup/`, targets are
  swapped by atomic rename, and a crash is recovered from
  `autoupdate/apply-journal.json` on the next boot (roll back an `applying`
  journal, finish a `committed` one); a pending update failing
  `UpdateApplier.MaxApplyAttempts` (3) times is discarded and the version is
  tombstoned in `autoupdate/discarded.json` so it is not staged again. Key:
  `src/Allegiance.Update/UpdateApplyJournal.cs` (`UpdateApplyJournal`,
  `UpdateApplyRecovery`), `src/Allegiance.Update/UpdateApplier.cs`. Fixes the
  P971-05 mixed-tree finding (`docs/reports/CLIENT_REVIEW_P971.md`); report
  `docs/reports/UPDATE_ATOMIC_APPLY_P976.md`.
- **Server self-update** — opt-in (`ServerConfig.Updates`), stages ONLY at game
  over (`src/Allegiance.Server/Server.Update.cs`, `ServerUpdateGate`), applied
  before the world loads in `src/Allegiance.Server/Program.cs`. Docker keeps
  pulling images (`docker/README.md`).
- **File-set identity (P871)** — the client's reported `LOGONREQ.crcFileList`
  (CRC-32 of its installed file list) and the server's release policy gate. Key:
  `src/Allegiance.Core/FileListCrc.cs`,
  `src/Allegiance.ClientCore/Session/ClientFileList.cs`,
  `src/Allegiance.Server/Server.Dispatch.cs` (`CheckFileList`),
  `ServerConfig.RequireFileList`/`ExpectedFileListCrc`; native
  `clintlib/clintlib.cpp:1418`, `lobby/client.cpp:272-281`.
- **Bundle version reporting (P969)** — release bundles ship
  `<appRoot>/autoupdate/installed.json` so a fresh install reports the packaged
  version instead of the "0.0.0" fallback. `packaging/package.py` writes the
  manifest with the same schema as `UpdateManifestBuilder` (full file list +
  SHA-256 + Unix mode) so the updater's delta and obsolete-file logic stays
  correct on the first upgrade. Key:
  `packaging/package.py` (`build_installed_manifest`, `write_installed_manifest`),
  `src/Allegiance.Update/UpdateApplier.cs` (`UpdateState` class),
  `src/Allegiance.Update/UpdateManifestBuilder.cs`,
  `src/Allegiance.ClientCore.Game/Game1.Session.cs` (`InstalledClientVersion`).
  Report: `docs/reports/BUNDLE_VERSION_P969.md`.
- **Feed builder** — `tools/feedbuild/` (manifest + signature + `latest.json`,
  GitHub flat-asset mode); driven by the `feed` job in
  `.github/workflows/release.yml`. Key handling: `packaging/README.md`.
  Report: `docs/reports/P810_AUTO_UPDATE.md` (design:
  `docs/P810_AUTO_UPDATE_DESIGN.md`).

## 7. Tools / proofs (`tools/`)

- **Proof harnesses** — one-off exe per task (e.g.
  `tools/p762b-sector-map-proof/`, `tools/hud-gauge-proof/`,
  `tools/collision-perf-proof/`); built by
  `tools/build-tools.sh --drop-bins` (the `-proof` projects are outside the
  solution). The screen/options proof: `tools/p806-ui-proof/` (intro options
  tree, real lobby server browser, team-lobby chat in both HUD styles).
- **Showcase side-bias audit** — `tools/p819-side-bias-proof/` runs native-map
  loopback matches with optional civilization swap; `audit.py` streams copied
  rig logs into per-side evidence and `transits.py` measures named-bomber warp
  legs. Finale station duty supersedes its old
  aleph rally in `tools/allegiance-mcp/BotManager.Finale.cs`; the showcase
  preset defaults to both sides. Report `docs/reports/P819_SIDE_BIAS.md`.
- **Position-bias evidence (P826)** — `tools/p819-side-bias-proof/audit.py`
  (`--per-match`) reconciles per-life kill/death flags with each deadline,
  separating finale phases and censored rotations; `summarize.py --position`
  aggregates the event cubes and `--map` measures the native spider geometry.
  Report: `docs/reports/P826_POSITION_BIAS.md`.
- **Corpus utilities** — `tools/cvhdump/`, `tools/fedmsgdump/`, `tools/igcdump/`,
  `tools/corpusvalidate/`.
- **Benchmark** — `tools/benchmark/` (replay benchmark) and
  `tools/bench-collector/`.
- **Meshy pipeline** — `tools/extract_meshy_textures.py`, `tools/meshy-repair/`.
- **GLB mesh pipeline (P812)** — native `.mdl` + `.cvh` → glTF 2.0 `.glb` with
  `HP_<Kind>_<Index>` hardpoint empty nodes (+Z forward) and a collision
  sidecar, plus the reverse loader stub and numeric round-trip:
  `tools/mdl-to-glb/` (commands `export` / `roundtrip` / `inspect`;
  `render-check.py` renders evidence headless in Blender). Tests
  `MdlToGlbTests`. Key finding: the `.cvh` file space is the `.mdl` space
  mirrored in z (position AND forward). Report
  `docs/reports/P812_GLB_PIPELINE.md`. Offline modding bridge — the game loads
  the imported native `.mdl`/`.cvh`, not GLB.
- **GLB → native writer (P823)** — the other half of the pipeline: a `.glb`
  (+ optional sidecar) converts to native's shipping `.mdl` + `.cvh` at pack
  time — `tools/mdl-to-glb/` commands `import` (glb+sidecar → files) and
  `verifyimport` (native → glb → files → parse-compare + byte-diff);
  `MdlWriter.cs` (binary token stream), `CvhWriter.cs` (native printf formats;
  generated hulls via quickhull + the ported `cvh/main.cpp` ellipse/adjacency
  walk), `NativeNameMapper.cs` (HP kinds → native frame names). Render check:
  `tools/p823-render-proof/`. Tests `GlbToMdlTests`. Report
  `docs/reports/P823_GLB_TO_MDL.md`.
- **Texture half of the pipeline (P824)** — native compiled texture units
  (`<stem>.bmp.mdl`: `BinarySurfaceInfo` + top-row-first RGB565 rows,
  *native* `engine/enginep.h:313`, `engine/modeler.cpp:715`,
  `engine/mdlbin.cpp`) now decode AND write: `MdlTextureDecoder` +
  `MdlTextureWriter` in `src/Allegiance.Core/Data/` (nearest-decoded-value
  RGB565 quantisation, power-of-two ≤2048 size rule with box resample,
  optional Bayer dithering off by default). `tools/mdl-to-glb` `export`
  embeds the decoded texture as the glTF base-colour PNG, `import` writes the
  GLB's image back as the native `<stem>.mdl` texture unit (`PngCodec.cs`,
  built-in minimal PNG codec; `gentexture`/`gentesthull` build the proof
  texture/hull in code); `tools/p823-render-proof --uv-check` asserts the UV
  mapping numerically. Tests `P824TexturePipelineTests`. Report
  `docs/reports/P824_TEXTURE_PIPELINE.md`.
- **Binary model optimization (P919)** — `tools/mdl-to-glb/MdlOptimizer.cs`
  retains the native namespace graph, lights, frames and every LOD; removes
  materials, folds textures/groups and combines meshes to a fixed point.
  `optimize` writes and reloads through `src/Allegiance.Core/Data/MdlFile.cs`,
  whose LOD traversal inherits enclosing texture bindings. Native:
  `effect/efapp.cpp:713-815`, `engine/geometry.cpp:327-377,1009-1399,1789-1900`,
  `mdlc/mdlc.cpp:109-152`. Tests:
  `tests/Allegiance.Core.Tests/P919ModelToolsTests.cs`; workflow:
  `docs/GUIDE.md`; verdicts and limitations: `docs/reports/P919_REPORT.md`.
- **GLB LOD bridge (P921)** — `tools/mdl-to-glb/GlbModel.cs` carries authored
  reduced levels; `tools/mdl-to-glb/GlbWriter.cs` writes named LOD parents
  with radius/order extras, `tools/mdl-to-glb/GlbReader.cs` restores them and
  warns on older geometry-free exports, and `tools/mdl-to-glb/MdlWriter.cs`
  emits native LODGeo pairs. Blender evidence/workflow and byte deltas:
  `docs/reports/P921_REPORT.md`; exact corpus proof:
  `tests/Allegiance.Core.Tests/P921LodBridgeTests.cs`; production renderer
  selection and pixel comparison: `tools/p921-lod-proof/Program.cs`.
  Native: `engine/geometry.cpp:1937-1953,1761-1773`.
- **TextureCraft material pilot (P920, evaluation only)** —
  `tools/p920-texturecraft.py` preserves native geometry/UVs and atlas texels,
  bakes four mesh maps, adds restrained procedural wear and builds a private
  one-atlas schema-3 pack. Game1 captures use `tools/p916-texture-proof/`;
  repeat commands, orientation/UV limits and verdict: `docs/reports/P920_REPORT.md`.
- **Test runners** — `tools/run-tests.sh`, `tools/build-tools.sh`,
  `tools/test-tiers/runner.py`.
  Training's complete Fast + Live cohort: `tools/run-training-tests.sh` (P882b).
- **`tools/core2txt` (P870/P870b)** — native `Core2Text`/`Text2Core` static-core
  text round trip (`FedSrv/FedSrv.CPP:10385-10480`): `.igc` → editable
  tab-delimited text (one CSV per native table, native headers/comments/columns)
  and back. All 13 static object types are ported (`src/Allegiance.Core/StaticCoreText/codecs/`), and
  `src/Allegiance.Core/StaticCoreText/CoreTable.cs` carries the shared native helpers. `.coremeta`
  preserves the version word, the record sequence, and the sparse non-text bytes
  (struct padding and the stale bytes after a string's NUL), which native itself
  cannot reproduce; `SelfTest` certifies all 100 shipped static cores are
  byte-identical after `.igc → text → .igc`. Certified by
  `tests/Allegiance.Core.Tests/P870Core2TxtTests.cs` and
  `tests/Allegiance.Core.Tests/P870StaticCoreLoaderTests.cs`.

## 8. Parity matrices (`docs/parity`)

- **Native n/a ruling audit (P868)** — all 199 original exclusions are
  classified in `docs/parity/p868-na-audit.csv`: A is a justified exclusion,
  B is behavior (implemented or explicitly partial), C needs a product
  decision (partial). General-matrix identities use `area::native_item`.
  `tools/p868-na-audit.py` checks coverage, statuses and citation integrity;
  `docs/reports/P868_REPORT.md` records decisions, remaining branches and gates.
  Later exclusions retain that inventory: `docs/parity/p903-exclusions.csv`
  records the source-confirmed unused `Ballot::Cancel`; the same validator checks
  its supplemental classification. `docs/reports/P903_REPORT.md` audits default
  NPC travel, replay identity and the distinct offline export/live text-import paths;
  tool reachability/scorer policy remains deferred.
- **`docs/parity/parity-matrix.csv`** — the master native-behaviour parity
  matrix (rows closed with `done` / `done-visual` / `n/a` per the lane rules; a `design-replaced` native_class row is credited only when complete with a `REPLACED:` reason and is reported in its own scorecard column).
- **`docs/parity/sim-rules-matrix.csv`** — sim-rule equivalence rows.
- **Rule reachability (P832a)** — every sim row is `production`, `config-only`
  (experimental opt-in), or `test-only` (including missing executable paths).
  `scripts/parity-scorecard.py` credits completed production rows only, retaining
  other eligible rows in the denominator. The **FedSrv mission rules
  (event-driven)** family inventories full native mission/handler bodies;
  `tools/p832a-mission-rules-audit.py` reconciles their source inventory.
- **`docs/parity/fedmsg-wiring.csv`** — wire-message wiring audit.
- **`docs/parity/p650-core-constants.csv`** — native float/constant tuning.
- **P874 area matrices (`docs/parity/*-matrix.csv`)** — per-area native behaviour
  inventories with the full 17-column sim-rule shape and a `reachability`
  column: `lobby-matrix.csv` (lobby server), `soundengine-matrix.csv` (audio
  behaviour), `allsrvui-matrix.csv` (server-admin capabilities), `effect-matrix.csv`
  (gameplay-timed effects; visual families are summary `n/a` rows),
  `bots-matrix.csv` (Pigs/Wopr bot rules, cross-refs `p819-bot-policy.csv`),
  `model-tools-matrix.csv` (mdlc/mdledit features ported to
  `tools/mdl-to-glb`). All wired into `scripts/parity-scorecard.py`.
- Wave reports: `docs/reports/P664_PARITY_WAVE3.md` … `P7xx_PARITY_WAVE*.md`.
- Every parity row needs an executable path + a test — see `docs/CI_TEST_GATES.md`.

## 9. Rig / runner / CI

- **Restart migration barrier (P965)** — `src/Allegiance.Server/Server.Lobby.cs`
  holds countdown, auto-restart, ready and manual starts while that fresh mission
  has a `PendingRotationTail`; completion retries preserved readiness and keeps
  absolute restart/briefing targets. Native synchronous reset precedes the next
  start check (`FedSrv/fsmission.cpp:3433-3546`, `FedSrv/FedSrv.CPP:8234-8241`).
  Loopback event-order proof (24/64 peers):
  `tests/Allegiance.ClientCore.Tests/P965RestartMigrationBarrierTests.cs`;
  report: `docs/reports/RESTART_MIGRATION_BARRIER_P965.md`.
- **P807 handoff loopback diagnostics (P960)** — bounded simulation/receipt waits
  and failure snapshots (match, condition, identities, readiness and captured console)
  in `tests/Allegiance.ClientCore.Tests/P807GameOverHandoffTests.cs`.
  Investigation and qualification: `docs/reports/P807_HANDOFF_HANG_P960.md`;
  the historical workstation-only two-handoff timeout remains unconfirmed.
- **`tools/run-tests.sh`** → `tools/test-tiers/runner.py` — the shared
  fast/live/perf tier runner (`--tier fast|live|perf|all`). Docs:
  `docs/reports/TT1_TEST_TIERS.md`, `docs/TEST_FLAKES.md`.
- **`tools/build-tools.sh`** — builds every `tools/*/*.csproj` (as `ci.yml`).
- **`.github/workflows/ci.yml`** — CI: build solution, build tools, Fast tier,
  replay benchmark. `.github/workflows/guard.yml` — the publish guard.
- **`docs/reports/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md`** — how the private
  artwork/native corpus is kept out of the public clone.

## 10. Dev tooling (P811)

- **Home geometry / pod and strike audit (P925)** — `tools/p925-home-geometry/`
  dumps native station launch/bay planes, aleph entry envelopes and seeded
  file-map rock approach lanes, and runs sampled-pose pod / three-bomber
  loopback fixtures. `tools/p925-home-audit.py` joins b63 receipts by match,
  home and civilization; fixtures: `tools/test_p925_home_audit.py` and
  `tests/Allegiance.ClientCore.Tests/P925HomeGeometryTests.cs`. Event-only
  actual-target/first-radius pod receipts live in
  `tools/allegiance-mcp/BotManager.HomeTelemetry.cs`; strike edges include UTC
  and target geometry. Existing controls and strategy are unchanged. Report:
  `docs/reports/P925_REPORT.md`.

- **Allegiance Index (AIX1/AIX2)** — session effective-player benchmark in
  `scripts/allegiance-index.py`, with stdlib fixture tests in
  `scripts/tests/test_allegiance_index.py`; formula and usage in
  `docs/PERFORMANCE.md`, batch hook in `tools/aix-batch.md`, hardware and
  reference data under `docs/benchmarks/`. Calibration, evidence coverage and
  history: `docs/reports/AIX1_REPORT.md`. AIX2 adds schema-2 rig cards (a
  machine list with roles and P/E cores), weighted cores (P + 0.5·E),
  AIX/server-core and AIX/total-core, and the shared-machine AIX-S variant
  with the client `[frameprof]` frame-time log: `docs/reports/AIX2_REPORT.md`.
  Modern tooling; no native rule.

- **`GLOSSARY.md`** — this file; update it when adding a system.
- **`scripts/dev-stack.sh`** — one-command local stack
  (`up|down|status`; `--local` processes or `--docker` compose; `--bots`,
  `--client`, `--map-gen`, `--map-seed`). See `QUICKSTART.md`.
- **`QUICKSTART.md`** — how to get the whole stack running quickly.
- **Reproducible generated maps** — `--map-seed N` /
  `MapGenConfig.Seed` → `MapGenParams.Seed` →
  `MissionMapGenerator.Generate` with a `SeededMapRandom`; the server logs
  `topology=… seed=N (…sectors/…warps/…stations/…asteroids)` on one line.
  Tests: `tests/Allegiance.Core.Tests/MapGenSeedReproTests.cs`.

## 11. Map generation (`src/Allegiance.Core/MapGen`)

- **`MissionMapGenerator.cs`** — the procedural map generator (port of native
  `mapmakerigc.cpp`), producing `MissionMap` records. Its special-asteroid
  classes are the direct port of `Igc/asteroidigc.cpp` `NumberSpecialAsteroids`
  / `GetSpecialAsterioid` (`NumberSpecialAsteroids`, `SpecialAsteroidKind`,
  `SpecialAsteroidCapability`, `SpecialAsteroidOffset`): the three
  research-path switches select Uranium 0x10 / Silicon 0x20 / Carbonaceous 0x40
  for **generated and file maps alike** (`common.cpp:3052-3058`), and the
  per-sector start index is 1 or `randomInt(1, count)` inclusive. Generated maps
  take the switches from `MapGenParams.AllowExpansionPath` /
  `AllowTacticalPath` / `AllowSupremacyPath` (native `MissionParams`, fed from
  the game config); the file-map top-up takes `Mission.Research`. Tests:
  `tests/Allegiance.Core.Tests/P939SpecialAsteroidTests.cs`.
- **`MapGenParams.cs`** — per-run parameters incl. `Seed`.
- **`MapGenContext.cs`** — per-run plumbing (port of native `CMapData`).
- **`MapRandom.cs`** — the seeded/shared RNG abstraction.
- **`MissionMap.cs`** (`Model`) — the generated map that flows through
  `Mission.Assemble` like a file-decoded map.
- Topologies: `SingleRing`/`DoubleRing`/`BigRing`/`HiLo`/`HiHigher`/`LargeSplit`/
  `InsideOut`/`Star`/`PinWheel`/`DiamondRing`/`SnowFlake`/`Grid`/`EastWest`/
  `SplitBase`/`Brawl` (reports `P397`–`P402`).
- Server integration: `src/Allegiance.Server/Server.Lifecycle.cs`
  (`ToCoreParams`, `AssembleGameMission`), `ServerConfig.MapGenConfig`.

---

## Quick orientation by concept → files

| Concept | Key files |
|---|---|
| World assembly | `src/Allegiance.Core/Model/Mission.cs`, `src/Allegiance.Core/Model/Sector.cs` |
| Wire messages | `src/Allegiance.Core/Wire/*.cs` (+ `docs/parity/fedmsg-wiring.csv`) |
| Drop / reconnect | `src/Allegiance.Server/Server.ReconnectGrace.cs`, `src/Allegiance.Server/Server.Lifecycle.cs` (`ReleasePeerWithReason`), `src/Allegiance.ClientCore/Session/ConnectionSession.cs` |
| Server boot | `src/Allegiance.Server/Program.cs`, `src/Allegiance.Server/ServerConfig.cs`, `src/Allegiance.Server/Server.Lifecycle.cs` |
| HUD styles | `src/Allegiance.ClientCore.Game/Rendering/HudSettings.cs`, `src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.cs` |
| Server browser / filters | `src/Allegiance.ClientCore.Game/LobbyGameSort.cs`, `src/Allegiance.ClientCore.Game/LobbyGameMetadata.cs` |
| Options menu | `src/Allegiance.ClientCore.Game/OptionsMenuModel.cs` |
| Chat pane filters | `src/Allegiance.ClientCore.Game/Rendering/ChatPaneModel.cs` |
| Map generation | `src/Allegiance.Core/MapGen/MissionMapGenerator.cs`, `src/Allegiance.Core/MapGen/MapGenParams.cs` |
| Seed reproducibility | `src/Allegiance.Core/MapGen/MapRandom.cs`, `tests/Allegiance.Core.Tests/MapGenSeedReproTests.cs` |
| One-command stack | `scripts/dev-stack.sh`, `QUICKSTART.md` |
| Parity tracking | `docs/parity/*.csv`, `docs/reports/P7*_PARITY_WAVE*.md` |
| Test tiers | `tools/run-tests.sh`, `tools/test-tiers/runner.py` |

## 12. Player identity (P808a)

- **Lobby identity issuer / account** — stable account ID and unique callsign;
  passkeys and optional configured GitHub/Google/Steam proofs. Design:
  `docs/P808A_IDENTITY_DESIGN.md`; HTTP: `src/Allegiance.Lobby/Identity/IdentityEndpoints.cs`,
  `src/Allegiance.Lobby/Identity/PasskeyService.cs`,
  `src/Allegiance.Lobby/Identity/ExternalProviders.cs`.
- **Identity store / migration** — SQLite by default, PostgreSQL optionally;
  accounts, passkeys, sessions, challenges, device grants and enrolled servers:
  `src/Allegiance.Lobby/Identity/IdentityStore.cs`.
- **Device authorization / join ticket** — browser approves an in-game code;
  `src/Allegiance.ClientCore/Session/LobbyIdentityClient.cs`,
  `src/Allegiance.ClientCore.Game/Game1.Identity.cs`,
  `src/Allegiance.ClientCore.Game/IdentityScreenModel.cs`.
  `src/Allegiance.Core/Identity/JoinTokens.cs` signs/verifies short-lived ES256
  tickets in native LOGONREQ.ZoneTicket; canonical names/IDs bind in
  `src/Allegiance.Server/Server.Identity.cs`. Native:
  `Inc/ClubMessages.h:42-58`, `FedSrv/FedSrv.CPP:5824-5904`.
- **Verified game server / enrollment** — operator-approved persistent server
  credential in `src/Allegiance.Server/LobbyHeartbeat.cs`; verification metadata
  is authenticated by `src/Allegiance.Lobby/LobbyEndpoints.cs`.
  Public-only trust volume and optional PostgreSQL:
  `docker-compose.yml`, `docker/compose.override.postgres.yml`, `docker/README.md`.
- **Identity visual proof** — actual Game1 window with production sign-in code,
  renderer and HTTP client: `tools/p808a-identity-proof/Program.cs`.

## 13. Career / leaderboard (P808b/P808c)

- **Squad store + squad game results (P820a/P855/SIM-0765)** — persistent
  squads with native role membership (`src/Allegiance.Lobby/Squad/SquadStore.cs`);
  a finished qualifying squad game (native `RecordSquadGame`,
  `FedSrv/FedSrv.CPP:1198-1300`, gated at `fsmission.cpp:3421-3427`) rides the
  career match report (`/api/career/results`) and credits the winner squad one
  win, each loser squad one loss, idempotent per server+match.
  `src/Allegiance.Server/Server.Lobby.cs` (`ReportMissionResultsToLobby`),
  `src/Allegiance.Lobby/Career/CareerStore.cs`. Report
  `docs/reports/P855_REPORT.md`.
- **Career store + HTTP** — per-account per-civ career statistics, rank rules,
  leaderboard and profile descriptions on the lobby:
  `src/Allegiance.Lobby/Career/CareerStore.cs`,
  `src/Allegiance.Lobby/Career/CareerEndpoints.cs`,
  `src/Allegiance.Lobby/Career/CareerRankTable.cs`. Native:
  `FedSrv/FedSrv.CPP:1045-1103` (`SetCharStats`), `Inc/ClubMessages.h`.
- **Career wire contracts / client mirror** — `src/Allegiance.Lobby/Career/CareerContracts.cs`,
  `src/Allegiance.ClientCore.Game/Career/CareerModels.cs` (`CareerDto`),
  `CareerApiClient.cs` (HTTP read/edit).
- **Career screen models** — `src/Allegiance.ClientCore.Game/Career/CharacterInfoModel.cs`,
  `Career/LeaderboardModel.cs` (native `WinTrek/CharInfoScreen.cpp` /
  `leaderboardscreen.cpp` behaviour).
- **Career screens (P808c, both HUD styles)** — rendered in the MonoGame client,
  reached from the intro (Career menu): `src/Allegiance.ClientCore.Game/Game1.Career.cs`
  (drive + fetch), `Rendering/CareerScreenRenderer.cs` (draw both styles),
  `Game1.IntroScreen.cs` (`IntroView.Career`). Real-window captures in
  `docs/reports/P808C_assets/`; proof tool `tools/p808c-ui-proof/Program.cs`.
- **Reserved callsigns (P808c)** — the lobby exposes registered names
  (`src/Allegiance.Lobby/Identity/IdentityEndpoints.cs`, `IdentityStore.RegisteredCallsigns`)
  and the server caches them short-TTL, fail-open to refuse anonymous logons that
  would take a registered name: `src/Allegiance.Server/RegisteredCallsignCache.cs`,
  `Server.Dispatch.cs`.
- **Career lobby web pages (P808c)** — read-only ladder + player profile on the
  lobby site: `src/Allegiance.Lobby/wwwroot/ladder.html`, `ladder.js`,
  `profile.html`, `profile.js`, `career.css`.
- **Server-side GetExpMult career store (P864 / SIM-0528+0761)** — the port's
  in-memory stand-in for native's persisted per-player career score that
  `GetExpMult` weighs experience with (`timePlayed · persist.GetScore()`,
  `FedSrv/fsmission.cpp:3261-3272`): a server-lifetime dictionary keyed by
  character name (`src/Allegiance.Server/Server.cs` `_careerScores`), folded at
  each game over so career-based anti-stacking accumulates across games within a
  server run (cross-restart persistence is out of scope — no stats DB, P583).
  Consumed by `PlayerScore.ApplyGameOverExperienceBalance` (`PlayerScore.cs`)
  which applies the `GetExpMult` multiplier at all three `ProcessGameOver` call
  sites (connected, departed, commander) in
  `src/Allegiance.Server/Server.Lobby.cs` `FinalizeMissionScores`. Also adds
  native `PlayerScoreObject` command-time (`StartCommand`/`StopCommand`/
  `TimeCommanded`, `igc.h:5615-5630`) and the commander score
  (`SetCommanderScore`, `igc.h:5519-5527`), feeding the game-over commander
  selection and the lobby report's CommanderWin/CommanderLoss columns. Report
  `docs/reports/P864_REPORT.md`.

## 14. Match civilizations (P825)

- **Civilization rotation (P929, preset experiment)** — per-game `civRotation` (`off`, `alternate`) in
  `src/Allegiance.Server/ServerConfig.cs`; `src/Allegiance.Server/MatchCivilizations.cs` `Rotate` shifts the resolved
  civilizations one side per match (two teams: swap on even matches) so side and civ effects separate; receipts add
  `civRotation=… civShift=…`. Test: `tests/Allegiance.Core.Tests/P929CivRotationTests.cs`.
- **Home assignment trials (P924, preset experiment)** — per-game `homeSwap`
  (`off`, `on`, `alternate`) in `src/Allegiance.Server/ServerConfig.cs`;
  `src/Allegiance.Server/HomeAssignments.cs` permutes station owners before
  research resources and mission assembly. Geometry and authored files stay
  fixed; civilizations remain with their sides. Per-game rotation counters and
  seed/home receipts live in `src/Allegiance.Server/Server.Lifecycle.cs`.
  Evidence parser: `tools/p924-side-bias-audit.py`; audit and paired rig plan:
  `docs/reports/P924_REPORT.md`. This is an experiment policy, not a native rule.
- **Creation-time random civs** — `games[].civIds` accepts JSON `null` or `-1`
  for a fresh legal draw on boot and every rotation. `games[].civSeed` pins the
  sequence; each `[matchup]` logs a directly replayable match seed.
  `src/Allegiance.Server/MatchCivilizations.cs` implements swap-remove/refill
  (native `FedSrv/fsmission.cpp:175-198`, NA defaults `Igc/igc.h:1280-1281`),
  reusing `src/Allegiance.Server/Server.TeamCiv.cs`'s legal lobby pool. Fixed
  sides are excluded from random draws. `src/Allegiance.Server/Server.Lifecycle.cs`
  precomputes plans per game/side/civ before ticking; rotations select the actual
  rolled tuple without plan I/O. JSON sentinel adapter:
  `src/Allegiance.Server/CivIdsJsonConverter.cs`.
- **Race-matrix tally** — `tools/p819-side-bias-proof/matchups.py` reads assembly
  and game-over civ tuples, tallying wins/losses/draws by civ and side position
  for any number of sides. The same proof tool's `--random-rotation` mode runs
  consecutive local showcase matches. Report: `docs/reports/P825_RANDOM_CIVS.md`.
