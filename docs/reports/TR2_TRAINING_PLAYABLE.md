# TR-2 — Basic Flight playable on the embedded host (partial)

**Task:** port the native training missions to the real client + embedded host: a Training |
Skirmish chooser on the intro screen, a training screen listing the native missions, bind
`ITrainingSite` to the real game, and land TR-1's deferred client items + Mission 2's HUD /
messages / sounds.

**Branch:** `master` (not pushed). **Status:** client scope complete + verified; embedded-host
server universe (Scope 3 host) not landed this session — see §Remaining.

---

## What is done (committed, `fe93d4f0`)

1. **Scope 1 — Single Player → Training | Skirmish chooser + first-run suggestion.** The intro's
   "Single Player" button now opens a two-row chooser (Training, Skirmish). Skirmish keeps P727's
   setup. A one-shot first-run dialog (native `Ui.ShowStartupTrainingSuggestion`,
   introscreen.cpp:1305 / :1055) offers "begin training now" with a "don't ask me again" tick,
   stored in `IntroPreferences.ShowStartupTrainingSuggestion`.
2. **Scope 2 — Training screen.** A new `TrainingScreenModel` lists the native missions
   (trainingscreen.cpp:50-58: 1,2,3,4,5,6,8 — mission 7 removed) with names + descriptions.
   Only **Basic Flight (Mission 2)** is playable; the rest show "(coming soon)" and are never
   hidden. Native renders the mission names/descriptions as bitmaps
   (trainingscreen.mdl `missionInfoImage = SwitchImage(missionNumber,…)`); the port uses the
   canonical textual titles.
3. **Scope 4 (client, deferred TR-1 items):**
   - **`RecordKeyPress` full key-disable switch + command-view gating** — the exact
     trainingmission.cpp:342-413 switch: TK_ConModeInvest/TargetSelf/Suicide/ConModeGameState/
     ConModeTeleport/RejectCommand swallowed except in Free Flight; TK_ViewCommand/ConModeCommand/
     ToggleAutoPilot gated on `CommandViewEnabled`; TK_PauseTM consumed without routing. `Mission2`
     leaves command view disabled.
   - **Real `turnToFace` ship-simulator math** — `TrainingTurnTo` is a faithful port of
     `turnToFace` (src/Igc/common.cpp:152-252) including the drift-compensation (mass, torque
     multiplier, turn torque, current turn rate) and max-rate clamping; `TurnToAction` applies the
     native 0.5 damping (TurnToAction.cpp:63-65) on top. Fed by the own-ship simulator's hull.
   - **Masks wired into the real flight input behind `IsTraining`** — `ApproveControls` /
     `ApproveActions` (Training.cpp:253-300) are applied to the final `sentControls`/`sentStateM`
     right before `StepOwnShipSimulation` in `Game1.FlightInput.UpdateFlying`, so the same masked
     value feeds the local sim and the wire. Inert when no mission runs.
4. **Scope 3 (client half) + Scope 5 routing — `ClientTrainingSite`** binds `ITrainingSite` to the
   real game: geometry/state reads (position, orientation, velocity, controls, stateM) come from
   the replicated `ClientWorld`; `ShowMessage` posts a real chat line, `SetHudOverlayAction` sets
   `TrainingOverlay.Current`, `PlaySoundAction` routes to the client sound system, and `TurnToFace`
   uses `TrainingTurnTo`. A `TrainingSession` drives `TrainingMission.Frame()` per frame and routes
   real key presses through `RecordKeyPress`.

## Scope 3 host — what remains (honest)

The embedded server's **training universe** (the `TrainingMission::LoadUniverse` / `DefaultLoadout` /
`CreatePlayerInfo` mapping, trainingmission.cpp:651-894) and the server-side handlers for
`CreateWaypointAction` / `CreateDroneAction` / `PlaceDrone` / `IssueCommand` / `SetAutopilot` are
**not implemented in this session**. Consequently `ClientTrainingSite`'s creation/command actions
are explicit logged seams (`TrainingHostLog`), and the **real-client run / headless-completion proof
and 1280×720 screenshots could not be produced**. This is a scoped follow-up, not a fabricated
claim: TR-2's UI and every client-side deferred item are real and tested; the "playable on the
embedded host" end-to-end proof awaits the host universe.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| First-run training suggestion, once until cleared | introscreen.cpp:1305,1055 | design | `IntroPreferences.ShowStartupTrainingSuggestion`; cleared on train / don't-ask-again. |
| Big single-player button trains; chooser | introscreen.cpp:1221/:1558 | design | Intro `SinglePlayer` → Training \| Skirmish chooser. |
| Mission list 1,2,3,4,5,6,8 (7 removed) | trainingscreen.cpp:50-58 | design | `TrainingScreenModel.Missions`; Mission 2 playable only. |
| Key-disable "XXX hack" + command-view gate | trainingmission.cpp:388-401 | design | Ported exactly; Free Flight exempt; `CommandViewEnabled` gate. |
| `turnToFace` ship-sim corrective turns | common.cpp:152-252; TurnToAction.cpp:62 | design | `TrainingTurnTo`; 0.5 damping at the action. |
| ApproveControls/ApproveActions behind IsTraining | Training.cpp:253-300 | design | Applied at the real flight send path. |
| Universe construction / drone-waypoint server actions | trainingmission.cpp:651-894 | design | Seams left for the host (Scope 3 host). |

## Modern alternative considered

Native builds the training universe as a full IGC core (LoadIGCStaticCore + LoadIGCFile,
trainingmission.cpp:651-782) and runs the training on the **client** against a local copy of the
world. The modern option — reuse the embedded single-player host (P727) so a single authoritative
server owns the universe and the client renders the replicated world (no divergent client/server
world) — is exactly the shape the port takes (SkirmishHost + ClientWorld), and is the recommended
way to land the remaining host work. The turn-math port is deliberately the native algorithm
(small, well-understood); no modern equivalent improves the player-visible result.

## Test evidence (measured)

New `TR2TrainingTests` (12) — `training` collection + TR2 tests, run 3× alone:
```
Run 1: Failed: 0, Passed: 36, Total: 36
Run 2: Failed: 0, Passed: 36, Total: 36
Run 3: Failed: 0, Passed: 36, Total: 36
```
Covers: turn-math sign/zero/behind + reduction-on-apply; RecordKeyPress disable / command-view /
Free-Flight / pause; chooser Training|Skirmish order; training screen list with Basic Flight the
only playable mission; startup-suggestion flag persistence.

Whole-solution build `AllegianceCore.slnx` (Debug): **0 warnings, 0 errors** (verified twice:
baseline + after changes). No `src/Allegiance.Server` / `src/Allegiance.Core` change → AOT publish
gate not triggered. Fast-tier sweep: see §Full-suite disposition (all four fast projects ran; one
documented load-flake, 3/3 in isolation).

## Full-suite disposition (measured)

`tools/run-tests.sh --tier fast` on the committed tree:
- **Allegiance.Lobby.Tests** 19/19 · **Allegiance.ClientCore.MonoGame.Tests** 2416 pass / 3 existing optional skips · **Allegiance.Core.Tests** 4802 pass · **Allegiance.ClientCore.Tests** 1335 pass / **1 fail** (includes all 36 training/TR-2 tests passing).
- The single failure, `CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`, is the documented P699c load-flake and passed **3/3 in isolation** (1 s each). It is not training/UI code.
- A concurrent `allegiance-codex-tt1b` suite was running on this shared box (the P731 hazard); it also degraded the live `P727SkirmishLiveTests.Local_start_battle…` to 207 s (above the 10 s Fast limit) — both live-network timings were load-affected. Dispositions logged in `docs/TEST_FLAKES.md` (§TR-2 observation).

## Files changed

- Added: `src/Allegiance.ClientCore/Training/TrainingTurnTo.cs`;
  `src/Allegiance.ClientCore.Game/TrainingScreenModels.cs`;
  `src/Allegiance.ClientCore.Game/Training/ClientTrainingSite.cs`,
  `TrainingRuntime.cs`; `src/Allegiance.ClientCore.Game/Game1.Training.cs`;
  `tests/Allegiance.ClientCore.Tests/TR2TrainingTests.cs`.
- Modified: `src/Allegiance.ClientCore/Training/TrainingMission.cs` (RecordKeyPress +
  CommandViewEnabled), `TrainingMissionIds.cs` (TrekKey constants);
  `src/Allegiance.ClientCore.Game/Game1.IntroScreen.cs` (chooser + suggestion + training screen),
  `Game1.FlightInput.cs` (mask wiring + per-frame training pump), `IntroPreferences.cs`;
  `tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj` (Game reference).
