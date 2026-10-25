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
- **`src/Allegiance.Server`** — the dedicated TCP server (world sim, players,
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
- **Static core** — the parsed `PCore001.igc` asset: civilizations, hulls,
  parts, stations, asteroids, float constants. Key:
  `src/Allegiance.Core/Model/StaticCore.cs`,
  `src/Allegiance.Core/IgcReader.cs`, `src/Allegiance.Core/IgcFile.cs`.
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
  `mineigc.cpp`, `probeigc.cpp`, `chaffigc.cpp`.
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
  `src/Allegiance.Server/LobbyHeartbeat.cs`.
- **Discovery responder** — the P731 UDP "Find Server" responder.
  `src/Allegiance.Server/Discovery/`.
- **Broadcast** — sending ship/asteroid/station updates to peers, coalesced and
  interest-tiered. `Server.Broadcast.cs`, `BroadcastSnapshot.cs`,
  `Server.InterestTiers.cs`.
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
- **Ship commands** — client intent handlers (flight, firing, docking, buying).
  `Server.ShipCommands.cs`, `Server.Docking.cs`, `Server.Combat.cs`.
- **Egress / send path** — the P605/P645 send-worker transport hardening.
  `Server.Egress.cs`, `Server.Dispatch.cs`.
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

## 4. Client & HUD (`src/Allegiance.ClientCore`, `ClientCore.Game/Rendering`)

- **Headless session** — the world-state tracker driving prediction/replay.
  `src/Allegiance.ClientCore/Session/`, `Replay/`, `State/`.
- **Flight prediction / dead reckoning** — `src/Allegiance.ClientCore/Flight/`
  (`KinematicsDeadReckoner` etc.), reports `P447*/P448*`.
- **HUD settings / styles** — `Rendering/HudSettings.cs` defines `HudStyle`
  Classic/Modern; **every HUD change must work in both styles**
  (`Rendering/ModernHudRenderer.cs`). *Native:* the original classic HUD layout.
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
- **Options menu tree** — the native root + Sound/Game/Chat submenus
  (`WinTrek.cpp:4429-4442`, `:4502-4590`). Key:
  `src/Allegiance.ClientCore.Game/OptionsMenuModel.cs`, driven by
  `Game1.BenchmarkSharing.cs` (`UpdateOptionsMenu`/`DrawPrivacySettings`).
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
- **Scenario spawning** — `tools/allegiance-mcp/BotManager.Scenarios.cs`,
  `SkirmishDriver.cs`, `Showcase.cs`, `ScenarioPlanner.cs`.
- **Rig driver** — `scripts/mcp_driver.py` (drives the MCP exe from a JSONL
  command file). Reports `SC1_SHOWCASE_SKILLS.md`, `P539_MCP_COMBAT_BOTS.md`.

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
- **Version advertisement** — `GET /api/update/info` (current/minimum client
  version + the signed release feed URL), the HTTP analogue of native
  `AUTO_UPDATE_INFO(253)` (`lobby/messageslc.h:45-52`). Key:
  `src/Allegiance.Lobby/LobbyEndpoints.cs`, `LobbySettings.cs`
  (`CurrentClientVersion`/`MinimumClientVersion`/`UpdateFeedUrl`).
  Report `docs/reports/P810_AUTO_UPDATE.md`.

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
- **Corpus utilities** — `tools/cvhdump/`, `tools/fedmsgdump/`, `tools/igcdump/`,
  `tools/corpusvalidate/`.
- **Benchmark** — `tools/benchmark/` (replay benchmark) and
  `tools/bench-collector/`.
- **Meshy pipeline** — `tools/extract_meshy_textures.py`, `tools/meshy-repair/`.
- **Test runners** — `tools/run-tests.sh`, `tools/build-tools.sh`,
  `tools/test-tiers/runner.py`.

## 8. Parity matrices (`docs/parity`)

- **`docs/parity/parity-matrix.csv`** — the master native-behaviour parity
  matrix (rows closed with `done` / `done-visual` / `n/a` per the lane rules).
- **`docs/parity/sim-rules-matrix.csv`** — sim-rule equivalence rows.
- **`docs/parity/fedmsg-wiring.csv`** — wire-message wiring audit.
- **`docs/parity/p650-core-constants.csv`** — native float/constant tuning.
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
