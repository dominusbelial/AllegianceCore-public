# TR-2b — training missions: the embedded host side

**Task:** land the embedded-server training host so Basic Flight (Mission 2) is actually playable
offline: build the native training universe on the embedded host, replace the server-side action
handlers that TR-2 left as `TrainingHostLog` seams, and prove completion headlessly against the real
host.

**Branch:** `master` (not pushed). **Status:** host universe + in-process handlers + client wiring
landed and verified with a real-headless-completion proof; the interactive real-client in-flight run
with 1280×720 screenshots remains (see §Remaining).

---

## Architecture context

The embedded single-player host (`SkirmishHost`, P727) boots the real `Allegiance.Server.Server`
**in-process** with the MonoGame client and the client connects over loopback; the replicated
`ClientWorld` is the client's read source. Training therefore reuses that shape: a
`TrainingHost` boots the real server with the **native training universe** and exposes the
creation/command/autopilot handlers **in-process** — training is single-player, and native's training
actions all mutate the local `ImissionIGC` directly (`createwaypointaction.cpp:39`,
`setcommandaction.cpp:42`), sending no dedicated training network messages. No `Server`/`Core` code
changed; the whole host lives in `Allegiance.SinglePlayer`.

## What is done (committed)

### 1. Training host mode of the embedded host — `Allegiance.SinglePlayer/Training/TrainingHost.cs`

Boots a real `Server` with `StaticCore = PCore014.igc` (native `IGC_TRAINING_CORE_FILENAME`,
`igc.h:1002`) + `Map = training_1.igc`, one Iron-League side (civ 25), then builds the native training
universe:

| Native rule | Native cite | Port |
|---|---|---|
| Start sector 1010 | `Mission2::GetStartSectorID` mission2.cpp:84-87 | `StartSectorId = 1010`; mission sectors verified to contain cluster 1010 "Neptune". |
| Static core + map | `LoadUniverse` trainingmission.cpp:655-661, 782 | `PCore014.igc` + `training_1.igc`; the map parses to 1 cluster (1010), 1 station (id 1010 "Base", type 115), 25 asteroids — matches native exactly (verified with the repo's `IgcReader`). |
| Six sides, Iron-League player side | trainingmission.cpp:680-762 | One Iron-League side (side 0, civ 25) as `Mission.Assemble` side. |
| Player interceptor hull 412 | mission2.cpp:74 "an interceptor" | Player `Ship` on hull 412, at origin facing +X/up+Z (`SetupShipAndCamera` trainingmission.cpp:128-134). |
| Commander wingman drone | `LoadUniverse` `CreateDrone("Commander",…412,0,c_ptWingman)` trainingmission.cpp:836-841; placed at (500,0,0) facing back, mission2.cpp:82-89 | Commander `Ship` hull 412, `PilotType` wingman, id 990 (matches the ported `Mission2`'s stable `CommanderShipId`, Mission2.cs:24), at (500,0,0). |
| Cluster active / in flight, no hangar | trainingmission.cpp:845-856; `SetupShipAndCamera` | Mission left `Started` so the tick runs; ships placed airborne. |

### 2. In-process handlers — `ITrainingHost` (same file)

Replaces the TR-2 `TrainingHostLog` seams for every Mission-2 action. Each maps 1:1 to the native
action and operates on the **real** `Mission`/`Ship` objects:

| Handler | Native action | Real effect on the host |
|---|---|---|
| `CreateWaypoint` | `CreateWaypointAction::Execute` createwaypointaction.cpp:34-46 | Registers a waypoint (native `OT_buoy`) the site resolves for `ObjectWithinRadiusCondition`. |
| `IssueCommand` (Goto) | `SetCommandAction::Execute` setcommandaction.cpp:69-84 | Sets a real `GotoOrder` (native `c_oGoto`, shipIGC.cpp:4053,4160) on the target ship toward a waypoint / ship / the station. |
| `SetAutopilot` | `SetAutopilotAction::Execute` setautopilotaction.cpp:41-43 | Sets/clears the ship's autopilot flag. |
| `CreateDrone` / `PlaceDrone` | `TrainingMission::CreateDrone` trainingmission.cpp:589-650 / `CreateDroneAction::Execute` | Creates and places a real `Ship` in the mission. |

The host also runs a minimal **autopilot pass** (`Step`): any ship under a Goto order flies toward its
target at cruise speed and clears on arrival — the host realisation of native's commander/autopilot
flight (`trainingmission.cpp:646` `pShip->SetAutopilot(true)`), so goal 2 (commander reaches the
waypoint) and goal 8 (player autopilots within 500 m of station 1010) resolve against the real sim.

### 3. Client wiring — `ClientTrainingSite` (+ `ClientTrainingFacilities`)

`ClientTrainingSite` now calls the in-process `ITrainingHost` for `CreateWaypoint` / `CreateProbe` /
`CreateDrone` / `PlaceDrone` / `IssueCommand` / `SetAutopilot` when a host is supplied; with no host it
keeps the explicit logged seam (absence stays auditable). The real client wires `Host` when it starts a
training host.

### 4. Proof — headless Mission-2 completion against the real host
`tests/Allegiance.ClientCore.Tests/TR2BTrainingHostTests.cs`:
- `Mission2_RunsToCompletion_AgainstTheRealEmbeddedHost` — boots the real `TrainingHost`, runs Mission 2
  to completion against it with inputs scripted **through the real input path**
  (`TrainingMission.RecordKeyPress` for the SPACEBAR gates + the applied-control gates for throttle /
  the afterburner state bit), asserts the narrative milestones in order and autopilot engagement.
- `HostActions_LandRealEffects_OnTheMission` — asserts the universe is real (player + commander are real
  `Ship` models in real sector 1010, hull 412) and each handler lands a real effect (waypoint
  resolvable, commander carries a real Goto, autopilot flag flips, a drone is created and placed).

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Start in flight, no hangar, cluster active | trainingmission.cpp:845-856, SetupShipAndCamera:128 | design | Mission Started, ships placed airborne. |
| Commander is an autopiloted wingman | LoadUniverse:646,836-841 | design | Host autopilot pass flies it when Goto'd (wingman `PilotType`); avoids the server NPC default-order AI fighting it (see §Remaining). |
| Waypoint as `OT_buoy` object in the IGC world | createwaypointaction.cpp:34-46 | design | Host waypoint record resolved by the site; no `OT_buoy` sim model (see §Remaining). |
| Goto = native `c_oGoto` objective | shipIGC.cpp:4053,4160 | design | Real `GotoOrder` on the ship, cleared on arrival. |
| Autopilot flag | setautopilotaction.cpp:41-43 | design | Host-tracked flag on the real ship. |
| `GetControlActiveCondition` reads applied client controls | getcontrolactivecondition.cpp:43 | design | Site exposes the applied controls (decoupled from the sim field so the scripted gate doesn't make the ship physically fly in the headless proof). |

## Modern alternative considered

Native runs training against the **client's own local copy** of the IGC world (a divergent
client/server world) and sends no network messages. The port's modern option — reuse the P727 embedded
single-player host so **one authoritative in-process server** owns the universe and the client renders
the replicated `ClientWorld` — is exactly the shape TR-2 recommended and TR-2b took: the training
actions become in-process calls on that authoritative host, with no divergent world and no new wire
messages. Chosen for fidelity and because the host already exists. The host autopilot pass is a small
(N=3) add outside the server tick, so the 200v200 baseline and the live-bot/transport check are not
triggered (no server tick/broadcast/transport/bot code changed).

## Test evidence (measured)

- Whole-solution build `AllegianceCore.slnx` (Debug): **0 warnings, 0 errors**.
- New tests 3× alone (boot real server; `[Trait("Tier","Live")]`):
  ```
  Run 1: Failed: 0, Passed: 2, Total: 2 (52 s)
  Run 2: Failed: 0, Passed: 2, Total: 2
  Run 3: Failed: 0, Passed: 2, Total: 2
  ```
- No `src/Allegiance.Server` / `src/Allegiance.Core` change → AOT publish gate not triggered.
- Fast-tier sweep once (`tools/run-tests.sh --tier fast`): Lobby 19/19 · ClientCore.MonoGame 2453 pass /
  3 not-executed · ClientCore 1402 pass (**includes the 2 new TR-2b tests**) · Core 4805 pass / 1 fail.
- The single failure, `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`, is a network
  setup flake (log: `GreetTimeout`/`ProtocolError`) **not** in code this task touched; the class passes
  4/4 in isolation, 3× (measured). It is already listed in `docs/TEST_FLAKES.md`.
- `P700ShowcaseLiveTests` 5× not required: no bots/flight/sim code touched.

## Remaining (honest)

The **real-client in-flight run from the intro screen with 1280×720 screenshots, and the offline
run** are the interactive half TR-2b's headless proof does not cover; they need the MonoGame client to
seat the player in the host's airborne interceptor (the standard skirmish logon/seating path, adapted
to start in flight), which is the remaining engineering. Also deferred, with reasons:

- **`OT_buoy` sim model + replication**: the sim has no buoy model, so a waypoint is a host record the
  host-site resolves; a real client would need a replicated buoy object to draw the radar gutter icon.
- **Native `DefaultLoadout` / hull equipment**: the ships are bare hulls; equipping (weapons/ammo/
  missiles/energy/fuel, trainingmission.cpp:808-891) is not ported.
- **Native ship-physics motion**: the headless proof drives the player's position via the mission's own
  `TurnToAction`/Goto reads and the host autopilot pass rather than the full server ship integrator —
  needed for a convincing real-client flight feel (cruise/boost fuel, motion indicator).

## Files changed

- Added: `src/Allegiance.SinglePlayer/Training/TrainingHost.cs` (host + `ITrainingHost` + autopilot
  pass), `src/Allegiance.SinglePlayer/Training/TrainingHostSite.cs` (host-site `ITrainingSite`);
  `tests/Allegiance.ClientCore.Tests/TR2BTrainingHostTests.cs`.
- Modified: `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs` (+
  `ClientTrainingFacilities.Host`) — creation/command/autopilot actions now route to the in-process
  host.
