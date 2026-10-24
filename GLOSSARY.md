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
- **Starmap / minimap** — `Rendering/Minimap*.cs`, `Rendering/StarmapLayout.cs`.
- **Screens** — intro, connect, hangar/loadout, team lobby, briefing, panes.
  `src/Allegiance.ClientCore.Game/Game1.*.cs` (Game1.Hangar.cs, Game1.Panes.cs,
  Game1.IntroScreen.cs, Game1.ConnectScreen.cs).
- **Input** — keyboard/mouse/joystick/gamepad rebindable.
  `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs`, `Game1.KeyBind.cs`.
- **Skirmish (offline single player)** — `src/Allegiance.SinglePlayer/`
  (`SkirmishHost.cs`, `SkirmishOptions.cs`), hosted from `Game1.Skirmish.cs`.
- **Training** — the native training missions. `src/Allegiance.SinglePlayer/Training/`.

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

## 7. Tools / proofs (`tools/`)

- **Proof harnesses** — one-off exe per task (e.g.
  `tools/p762b-sector-map-proof/`, `tools/hud-gauge-proof/`,
  `tools/collision-perf-proof/`); built by
  `tools/build-tools.sh --drop-bins` (the `-proof` projects are outside the
  solution).
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
| Server boot | `src/Allegiance.Server/Program.cs`, `src/Allegiance.Server/ServerConfig.cs`, `src/Allegiance.Server/Server.Lifecycle.cs` |
| HUD styles | `src/Allegiance.ClientCore.Game/Rendering/HudSettings.cs`, `src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.cs` |
| Map generation | `src/Allegiance.Core/MapGen/MissionMapGenerator.cs`, `src/Allegiance.Core/MapGen/MapGenParams.cs` |
| Seed reproducibility | `src/Allegiance.Core/MapGen/MapRandom.cs`, `tests/Allegiance.Core.Tests/MapGenSeedReproTests.cs` |
| One-command stack | `scripts/dev-stack.sh`, `QUICKSTART.md` |
| Parity tracking | `docs/parity/*.csv`, `docs/reports/P7*_PARITY_WAVE*.md` |
| Test tiers | `tools/run-tests.sh`, `tools/test-tiers/runner.py` |
