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
- **File-map generation top-up (P864c / SIM-0748)** —
  `src/Allegiance.Core/Model/Mission.FileMap.cs` replaces unavailable station
  types from original local/effect plus side development techs, then fills only
  empty asteroid/treasure lists. Shared asteroid placement/He3 budgeting lives
  in `src/Allegiance.Core/MapGen/MissionMapGenerator.cs`; file-map resource
  defaults come from native Reset. Native: `Igc/missionigc.cpp:3977-4055`.
  Report: `docs/reports/P864c_REPORT.md`.
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
- **Flight model / physics** — acceleration, drag, turn rates, afterburner.
  Key: `src/Allegiance.Core/Sim/` (`Acceleration`, `Physics`, `TurnRate`…).
  *Native:* `shipIGC.cpp`, `rotvel.cpp`.
- **Native NPC GotoPlan (P817, experimental)** — pure waypoint masks, reverse-drag
  stopping distance, warp route search, directional dodge/diversion, angular
  stopping drift, boost handling and builder/layer completion: `src/Allegiance.Core/Sim/NativeGotoPlan.cs`,
  `src/Allegiance.Core/Sim/Ship.NativeGotoPlan.cs`. Per-mission flag
  `sim.nativeGotoPlan` / `ALLEGIANCE_SIM_NATIVE_GOTOPLAN` defaults off.
  Native: `Igc/common.cpp:1157-2333`, `Igc/shipIGC.cpp:2795-2833`.
  Local A/B and independent C++ formula trace: `tools/p817-gotoplan-proof/`;
  report `docs/reports/P817_NATIVE_GOTOPLAN.md`.
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
- **Random / determinism** — the seeded RNG abstraction behind reproducible
  generation. `src/Allegiance.Core/MapGen/MapRandom.cs`.

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
  selects the catalog by exact file/team pair. Native:
  `FedSrv/FedSrv.CPP:74-82,211-244,990-998,7690-7715`.
- **Inbound native-message certification (P867)** — the 49 active cases of
  `FedSrvSiteBase::OnAppMessage`, with sender/stage/ownership/trap guards,
  effects, handler cites and equal/classified results:
  `docs/parity/p867-onappmessage.csv`; inventory/citation check
  `tools/p867-dispatch-audit.py`. Dispatch stays in
  `src/Allegiance.Server/Server.Dispatch.cs`; native chat lobby controls and
  the balance-mode team flush use `src/Allegiance.Server/Server.DispatchParity.cs`.
  Report: `docs/reports/P867_REPORT.md`.
- **Ship commands** — client intent handlers (flight, firing, docking, buying).
  `Server.ShipCommands.cs`, `Server.Docking.cs`, `Server.Combat.cs`.
- **Egress / send path** — the P605/P645 send-worker transport hardening.
  `Server.Egress.cs`, `Server.Dispatch.cs`.
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
- **Cheats / debug** — `Server.AdminCheats.cs`, `Server.Privileges.cs`;
  debug mode `docs/reports/P378_DEBUG_MODE.md`.
- **Showcase / scenario hosting** — `Server.Showcase.cs`,
  `Server.SideIntegrity.cs`.
- **Seed pinning** — `ServerConfig.BenchmarkSeed` (sim RNG) vs the P811
  `MapGenConfig.Seed` / `--map-seed` (reproducible generated maps).
- **Reconnect Grace** (EXPERIMENTAL, `reconnect.enabled`, default **off**) —
  an unrequested drop parks the player's ship for `reconnect.graceSeconds`
  (default 5 s) instead of deleting it at once; the same pilot resumes the same
  ship/seat with a session token carried in the native `LOGONREQ.dwCookie`, and
  the existing native join burst re-syncs the client world. Expiry runs the
  native removal (`FedSrv.CPP:4549-4566`, `fsship.cpp:58-68`); a voluntary
  `LOGOFF` is immediate. `src/Allegiance.Server/Server.ReconnectGrace.cs`,
  `src/Allegiance.ClientCore/Session/ConnectionSession.cs`
  (`EnableAutoReconnect`), `tools/reconnect-fault/`,
  `docs/reports/P813C_RECONNECT_GRACE.md`. Client opt-in:
  `$ALLEGIANCE_RECONNECT_GRACE_SECONDS`.

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
  oxygen budget and docking. Reports P699/P699c; live test
  `P699HumanPodHomeLiveTests`.
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
  `:5429`) and the wider port glyph run (P859). Proof: `tools/hud2-proof`
  (sweeps 1920×1080, 2560×1440, 1280×720, 1600×900, 1366×768 × Size × both skins).
- **HUD renderer** — `Rendering/HudRenderer.cs` (shared model +
  Classic), `Rendering/ModernHudRenderer.cs` (Modern override). P786 report.
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
- **Local training lifecycle (P882a)** — `ITrainingLifecycleSite` in
  `src/Allegiance.ClientCore/Training/ITrainingLifecycleSite.cs` separates setup,
  authoritative respawn presence and death recovery from action/condition bindings.
  `src/Allegiance.SinglePlayer/Training/TrainingHost.Lifecycle.cs` supplies real
  equipment and queued death/last-base events; `Mission.IsTraining` keeps the lesson
  graph in charge of completion. Normal translated keys pass through
  `src/Allegiance.ClientCore.Game/Training/TrainingInputGate.cs` before handlers in
  both HUD styles. Mission5 starts docked; other lessons start airborne.
  Certification and remaining gaps: `docs/reports/P882a_REPORT.md`.
- **Training mission completion bindings (P881a)** — Basic Flight and Basic Weaponry run
  start-to-finish through the production path: client orders/target picks use the real
  `ORDER_CHANGE` (`src/Allegiance.ClientCore.Game/Game1.Training.cs`), the own ship's applied
  controls/state are `TrainingOwnState` in `src/Allegiance.ClientCore.Game/Training/TrainingRuntime.cs`,
  and `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs` binds autopilot, camera,
  equipment swaps/rearm and damage to the embedded host. The host flies a real station plan
  (`src/Allegiance.SinglePlayer/Training/TrainingHost.cs`) and the lesson ends on the server's
  DOCKED latch. `src/Allegiance.ClientCore/Flight/OwnShipSimulator.cs` supplies the `turnToFace`
  hull kinematics. Report: `docs/reports/P881a_REPORT.md`.

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
- **Showcase role fallback (P821)** — native hull ability flags do not define
  scout/interceptor duties. Missing roles score legal small craft for the requested
  duty, retaining variety and purchase gates: `tools/allegiance-mcp/TechLoadoutSelector.cs`,
  `tools/allegiance-mcp/BotManager.TechLoadouts.cs`.
  Census/local proof: `tools/p819-side-bias-proof/P821Catalogue.cs`;
  report: `docs/reports/P821_ROLE_RESOLUTION.md`.
- **Scenario spawning** — `tools/allegiance-mcp/BotManager.Scenarios.cs`,
  `SkirmishDriver.cs`, `Showcase.cs`, `ScenarioPlanner.cs`.
- **Finale re-arm / new-mission objectives (P656, P831)** — after a rotation the
  next match's finale is re-armed only from fresh per-bot state; a fresh life
  always resolves its objectives (an objective the fogged world cannot derive yet
  becomes "no fixed objective" and the clash assignment re-issues it), and a bot
  blocked past a bounded grace is excluded and named instead of holding the
  deadline forever. `tools/allegiance-mcp/BotManager.Finale.cs`
  (`RearmRotatedFinales`, `LogRearmGrace`), `tools/allegiance-mcp/BotManager.Lifecycle.cs`
  (`RefreshLife`, `LogObjectiveFallback`), `tools/allegiance-mcp/BotManager.Exploration.cs`
  (`AssignClashTargets`); report: `docs/reports/P831_FALLBACK_BOMBER_REARM.md`.
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
  `docs/reports/P812_GLB_PIPELINE.md`. Prototype only — the game still ships
  `.mdl`.
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
- **Test runners** — `tools/run-tests.sh`, `tools/build-tools.sh`,
  `tools/test-tiers/runner.py`.
  Training's complete Fast + Live cohort: `tools/run-training-tests.sh` (P882b).
- **`tools/core2txt` (P870/P870b)** — native `Core2Text`/`Text2Core` static-core
  text round trip (`FedSrv/FedSrv.CPP:10385-10480`): `.igc` → editable
  tab-delimited text (one CSV per native table, native headers/comments/columns)
  and back. All 13 static object types are ported (`tools/core2txt/codecs/`), and
  `tools/core2txt/CoreTable.cs` carries the shared native helpers. `.coremeta`
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

- **`tools/run-tests.sh`** → `tools/test-tiers/runner.py` — the shared
  fast/live/perf tier runner (`--tier fast|live|perf|all`). Docs:
  `docs/reports/TT1_TEST_TIERS.md`, `docs/TEST_FLAKES.md`.
- **`tools/build-tools.sh`** — builds every `tools/*/*.csproj` (as `ci.yml`).
- **`.github/workflows/ci.yml`** — CI: build solution, build tools, Fast tier,
  replay benchmark. `.github/workflows/guard.yml` — the publish guard.
- **`docs/reports/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md`** — how the private
  artwork/native corpus is kept out of the public clone.

## 10. Dev tooling (P811)

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
  `mapmakerigc.cpp`), producing `MissionMap` records.
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
