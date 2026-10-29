# P880a — training framework batch 1 (training-matrix rows with queue = P880a)

Framework batch 1 ports 20 training-framework rows (AbstractTarget through GetShipHasAmmoCondition)
against the **real** production path — `ClientCore` scripts → `ITrainingSite` → the embedded
`Allegiance.SinglePlayer.TrainingHost` with ordinary replication — with real model reads (not
diagnostic dictionaries) and tests that drive the production binding. No commits, pushes, stashes,
remote-machine contact or synthetic CPU load. Changes remain in the working tree. Native citations
are relative to native `src/`.

## Summary

13 of the 20 rows are now **implemented** with a failing-on-divergence test that drives the
production binding; 7 remain **partial** with precisely what is left. The framework-weighted score
for the whole training matrix rose from **30/91 (P873)** to **112/298 weighted / 34/85 unweighted**
(the P880a rows are the difference). No new services, ports, env vars or Docker changes; no new
media.

## What changed

### Interface + host production reads (real models, not dictionaries)

`ITrainingSite` gained three real-world queries and one action the framework rows read:
`GetProbeCount()`, `GetRadarLod()`, `GetAmmo(ship)` and `DamageShipAdjustable(ship, amount)`.
`ITrainingHost` grew the matching real-model reads (`IsAutopilot`, `GetAmmo`, `GetProbeCount`,
`DamageShipAdjustable`, `GetCommandTarget`, `GetCommandId`, `InScannerRange`, `ResolveWaypoint`,
`DamageShip`) so the client site and the host site share one source of truth.

`TrainingHost` now resolves **real** models:
- `GetProbeCount` / `ResolveProbe` count and return live `Probe` models from every sector
  (`getprobecountcondition.cpp:31`), not a retained dictionary.
- `ResolveAsteroid` returns a real runtime `Asteroid` from `Sector.Mineable`
  (`getsectorcondition.cpp:27-51`).
- `ResolveWaypoint` returns a live buoy geometry surface (`createwaypointaction.cpp:34-46`).
- `GetAmmo` reads the real `Ship.Ammo`; `GetAutopilot` reads the host autopilot flag.
- `DamageShipAdjustable` reproduces `DoDamageAdjustableAction` (dodamageadjustableaction.cpp:27-36):
  real `ReceiveDamage`, arms `allWeaponsIGC`, resets `ControlData`, and lowers the tracked armour
  fraction so `GetShipIsDamagedCondition` observes it.
- `CreateDrone` now applies the wingman behaviour to the real sim `Ship.WingmanBehaviour`
  (`SetWingmanBehaviour`, createdroneaction.cpp:201-203); the tech-level / constructor-base-data
  loadout remains the documented TRN-0010 gap.

`ClientTrainingSite` (the real client binding) forwards these to the embedded host so the networked
lesson and the headless host share the same real reads; `Resolve` now handles **station, asteroid,
probe and buoy** (from the replicated world's `ClientWorld.Stations/Asteroids/Probes` and the host's
waypoints), not just ships. `GetAutopilot`, `GetCommandTarget`, `HasCommand` (with the native
absent-ship ⇒ true rule), `ShipSees`, `DealDamage`, `GetProbeCount`, `GetAmmo`, and
`DamageShipAdjustable` are all real reads/writes through the host.

### Chat and radar production bindings

- **GetChatCondition (TRN-0021)** — `ClientTrainingSite.RecordChat`/`ConsumeChat` now keep a real
  queue, and `Game1`'s real chat-send path routes each subject to the active mission's `RecordChat`
  (native `trainingmission.cpp:327-331`), so Mission 4's "chat with the squad" goal observes the
  player's own chat instead of a host-injected one.
- **GetRadarLODCondition (TRN-0027)** — a `TrainingRadarLod` tracker mirrors the client's real
  radar mode; `Game1.FlightInput` updates it on the sortie reset and the `CycleRadar` edge
  (`WinTrek.cpp:9955-9975`), and the condition reads it (`getradarlodcondition.cpp:31`).

### Key condition (TRN-0024, partial)

`GetKeyCondition.Start` now drains stale presses so a press before the goal began cannot satisfy it
(`getkeycondition.cpp:35-43`). Still partial: the live `Game1` input gate forwards only its six
special training keys, so arbitrary `GetKeyCondition` keys depend on the mission's key-disable
switch — general key forwarding through the live flight-input path is the remaining work.

### Command view (TRN-0006, TRN-0025, partial)

`CommandAcknowledgedCondition` and `GetPickedCondition` read host-authoritative state
(`AcknowledgeCommand` / `SetPicked`), but the real queued-order **AcceptCommand** input and the
command-map **mouse pick** are not yet bound to those writes — those two remain Game1 input
bindings, listed in the matrix as partial with the precise remaining step.

### New framework primitives (missing → implemented)

- **CreateObjectAction (TRN-0011)** — a general arbitrary-type creation primitive dispatching over
  the specialised site methods (`createobjectaction.cpp:24-40`).
- **DoDamageAdjustableAction (TRN-0016)** — damage a chosen ship, reset controls, fire all weapons.
- **EndMissionAction (TRN-0018)** — `TrainingMission.Terminate()` so the next `Frame` completes
  (`endmissionaction.cpp:31-35`).
- **GetProbeCountCondition (TRN-0026)**, **GetRadarLODCondition (TRN-0027)**,
  **GetShipHasAmmoCondition (TRN-0029)** — the inverted native bug is kept
  (`getshiphasammocondition.cpp:32-34`: `GetAmmo() >= count ? false : true`).

## Per-row table

| Row | Native | Status | What is real | What remains |
|---|---|---:|---|---|
| TRN-0001 | AbstractTarget | **implemented** | Resolve station/asteroid/probe/buoy/ship against real world + host | — |
| TRN-0004 | AddRespawnAction | partial | Registration + spawn-pass + spawnOnAdd on real host (mission6) | Drone loadout/expendable recipe application (see TRN-0010) |
| TRN-0006 | CommandAcknowledgedCondition | partial | Reads host field; M5 uses it | Real queued-order AcceptCommand → AcknowledgeCommand binding |
| TRN-0010 | CreateDroneAction | partial | Real Ship created + wingman behaviour applied | Tech-level / constructor base-data loadout to sim hull; spawn-at-ship |
| TRN-0011 | CreateObjectAction | **implemented** | General creation primitive (dispatch by ObjectType) | — (test-only) |
| TRN-0012 | CreateProbeAction | partial | Probe count + resolution real via Probe models | CreateProbe does not yet place a real Probe/wire export |
| TRN-0013 | CreateWaypointAction | partial | Waypoint creation + resolution as buoy real | On-screen buoy display not wired |
| TRN-0014 | CurrentTarget | **implemented** | GetCommandTarget(c_cmdCurrent) via host; cache semantics intact | — |
| TRN-0015 | DoDamageAction | **implemented** | Real armour change on the host ship | — |
| TRN-0016 | DoDamageAdjustableAction | **implemented** | Real damage + all-weapons + controls reset | — (test-only) |
| TRN-0018 | EndMissionAction | **implemented** | Terminates the active mission | — (test-only) |
| TRN-0020 | GetAutopilotCondition | **implemented** | Real host autopilot flag | — |
| TRN-0021 | GetChatCondition | **implemented** | Real chat-send → RecordChat queue | — |
| TRN-0022 | GetCommandCondition | **implemented** | Accepted/current command + absent-ship true | — |
| TRN-0024 | GetKeyCondition | partial | Stale-clear on Start; consume-one-per-Evaluate | Live general key forwarding |
| TRN-0025 | GetPickedCondition | partial | Host picked state settable/readable | Command-map mouse pick → SetPicked binding |
| TRN-0026 | GetProbeCountCondition | **implemented** | Real world probe count | — (test-only) |
| TRN-0027 | GetRadarLODCondition | **implemented** | Real client radar mode | — (test-only) |
| TRN-0028 | GetSectorCondition | **implemented** | Sector for ship/station/asteroid/probe/buoy | — |
| TRN-0029 | GetShipHasAmmoCondition | **implemented** | Real ammo; inverted native bug kept | — (test-only) |

## Verification (light gate)

- Solution build **0/0** (no warnings/errors).
- `tools/build-tools.sh` — all **92** tool projects built, 0/0.
- New C# suite `P880aFrameworkBindingTests`: **12/12, three times alone** (includes four
  `[Tier("Live")]` tests that boot the real embedded `TrainingHost`).
- **Fast tier** (`tools/run-tests.sh --tier fast`): 11,503 tests. The gate is **red** only on
  pre-existing, unrelated live/timing flakes, each ledgered: `ShopLauncherMountNumberingLiveTests`
  (TEST_FLAKES:18,90,193 — passes alone), `P780PodRescueProbeLiveTests` (live-positioning timing —
  passes alone), `NpcWingmanDefendLiveTests` (deterministic pre-existing P872 failure,
  TEST_FLAKES:3540, no `Allegiance.Core` source changed here). `Allegiance.ClientCore.Tests`
  passed **1958/1958** including the new `P880aFrameworkBindingTests`.
- `python3 scripts/parity-scorecard.py`: matrix validates (0 CRLF; statuses/weights/reachability
  legal); framework score `112/298` weighted, `34/85` unweighted.
- Showcase never-parks test **5×**, all pass.
- **Real-window check** (scratch `XDG_CONFIG_HOME`, `ALLEGIANCE_TRAINING_START=3` Basic Weaponry,
  `--autoshots`, bounded `timeout`): the lesson launched in a real window with the player bound in
  space and advanced **through goal 3.5** with live voice narration (`vo_sal_commencescan`,
  `tm_3_01..tm_3_11`); goal-log progress saved at `.scratch/p880a/m3-goal-log.txt`. Ran in both HUD
  styles — Modern (`.scratch/p880a/shots_m3/`) and Classic (`.scratch/p880a/shots_m3c/`). Inspected:
  the modern gauged HUD (AMMO/ENERGY/ARMOR/SHIELD/THRUST/FUEL, RADAR Default/COCKPIT) and the
  classic corner-panel layout with an "ARMOR STRENGTH GAUGE" training overlay both render in-window
  with the player ship and team chat. The implemented rows read world/host state and are
  HUD-style-agnostic (no rendering code changed), so Classic and Modern are both supported.
  Screenshots are `.scratch/`-only, git-excluded, none committed.
- The pre-existing `Mission8_RunsToCompletion_AgainstTheRealEmbeddedHost` ("bomber never crossed
  into sector 1052") gap remains — it is ledgered in `docs/TEST_FLAKES.md` and `P873_REPORT.md`
  line 139 as an existing headless Nanite fixture gap, reproduced identically with the original HEAD
  `TrainingHost.cs`; no `Mission8`/`TR5` source file was changed by this task.

## Native intent

Framework ports preserve native goal/constraint/wait ordering, the inverted `HasAmmo` meaning and
the absent-ship⇒true `GetCommandCondition` rule (P873 lists these as native bugs to keep). Read paths
are real model reads against the replicated world / embedded host — diagnostic-dictionary outcomes
were replaced with real `Probe`/`Asteroid`/`Ship`/`Station` resolution and real ammo/autopilot/
command reads. The remaining partial rows are exactly the places where a real Game1 input binding
(queue-order accept, command-map mouse pick, general key forwarding) or a real sim loadout
application is still outstanding, each documented in the matrix `notes` column.

Raw task logs remain under `.scratch/p880a/` (none committed). Public evidence contains no absolute
home paths and no new images.
