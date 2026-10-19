# TR-3 — Training Missions 3 (Basic Weaponry) and 4 (Enemy Engagement)

Branch: `master` (committed `c2d1d22a`). Not pushed.

## Scope delivered

TR-1/TR-2/TR-2b ported the training goal/condition/action framework and Mission 2 (Basic Flight),
with a headless completion proof against the real embedded host. TR-3 adds the next two native
missions as data and enables them on the training screen, plus the seams each needs that were still
open, and proves each runs to completion against the embedded host through the real input path.

1. **Mission 3 "Basic Weaponry"** — `src/Allegiance.ClientCore/Training/Mission3.cs`, a faithful port of
   `src/training/mission3.cpp` (13 sub-goals, native line cites per goal).
2. **Mission 4 "Enemy Engagement"** — `src/Allegiance.ClientCore/Training/Mission4.cs`, a faithful port of
   `src/training/mission4.cpp` (10 sub-goals, native line cites per goal).
3. **New seams** (each cited to native) + `CreateDroneAction`'s wingman-behaviour / station-type /
   tech-level loadout block.
4. **Enabled Missions 3 and 4 on the training screen** (others stay "coming soon").
5. **Proof per mission**: a headless completion test against the embedded host through the real input
   path (`TR3TrainingMissions34Tests.cs`). Offline. (Real-client interactive screenshots remain the
   deferred "remaining engineering" TR-2b identified — see *Remaining (honest)*.)
6. Parity-matrix training rows updated (screens + lobby sets).

## Missions 3 and 4 as data

Both are pure data — they build a `GoalList` of `Goal`s and run against the injected `ITrainingSite`,
exactly like `Mission2.cs`:

| | Mission 3 | Mission 4 |
|---|---|---|
| Native | `src/training/mission3.cpp` | `src/training/mission4.cpp` |
| Start sector | 1010 (`mission3.cpp:88-91`) | 1030 (`mission4.cpp:92-95`) |
| Universe | `training_1`, fighter 3 hull 414 (`mission3.cpp:163`) | `training_3`, advanced stealth fighter 486 (`mission4.cpp:110`) |
| Goals | 13 (`mission3.cpp:250-261`) | 10 (`mission4.cpp:156-166`) |
| Teaches | shield/armour gauges, target-lock, reticule, energy gun, target-enemy, machine gun, missiles, inventory, kill bonus, go home & land | chat/quickcomm, waypoint+sector orders, intersector flight, stealth/cloak + eyeball, engaging unarmed miners, defending a constructor from a scout, build + land |

M3 seeds its 10 derelict target hulls in `CreateUniverse` (`mission3.cpp:201-229`) through the site
(stable ids 1000..1009). M4 generates its stable enemy-miner/scout/builder ids (`mission4.cpp:148-153`)
in `CreateMission`, exposed for the test driver.

## New seams ported (native-cited)

Extended `ITrainingSite` and added `CombatConditionsAndActions.cs`, plus overloads to
`ObjectPointingAtCondition` and a `PlayerCurrentTarget` abstraction for the native
`CurrentTarget(new PlayerShipTarget, …)` indirection (`currenttarget.cpp:23-57`, `playershiptarget.cpp:30-34`):

| Ported class | Native file:line | Behaviour |
|---|---|---|
| `GetShipIsDestroyedCondition` | `getshipisdestroyedcondition.h:27` via `getshipisdamagedcondition.cpp:44-57` | ship absent or armour fraction == 0 |
| `GetShipIsDestroyedCondition2(type,id)` | `getshipisdestroyedcondition.h:28` | same for an explicit `(type,id)` |
| `GetCommandCondition` | `getcommandcondition.cpp:27-54` | accepted-or-current command id match; true when ship absent |
| `GetSectorCondition` | `getsectorcondition.cpp:27-51` | model's current sector id |
| `ShipSeesObjectCondition` | `shipseesobjectcondition.cpp:29-51` | `InScannerRange` |
| `DoDamageAction` | `dodamageaction.cpp:24-50` | dent shield (3% max) / drop shield to 20% or hit hull |
| `ToggleWeaponAction` | `toggleweaponaction.cpp:28-47` | window `ToggleWeapon(mount)` |
| `SwapWeaponAction` | `swapweaponaction.cpp:28-37` | window `SwapPart(eqType, mount)` |
| `RepairRearmAction` | `repairrearraction.cpp:34-74` | heal + refill energy/ammo/fuel + fill cargo with missiles/ammo |
| `ShowPaneAction` | `showpaneaction.cpp:28-38` | turn off sibling panes, on the requested pane (`console.h:48-55`) |
| `ShowEyeballAction` | `showeyeballaction.cpp:28-35` | set player ship-status detected flag |
| `SetSignatureAction` | `setsignatureaction.cpp:29-49` | `model->SetSignature(sig)` |
| `CreateDroneAction` + behaviour/station-type/tech-level | `createdroneaction.cpp:91-198` | wingman `c_wbbm*` mask, constructor station type, and the negative-tech-level weapon/booster/missile upgrade block |

Constant sets also added/renumbered to native: command ids `c_cidAttack=1`,`c_cidBuild=9`
(`igc.h:902,910`); state bits `CloakActive=2048`, `WeaponsMask=49152`, `MissileFire=0x10000`
(`igc.h:805-831`); chat `CHAT_EVERYONE=0` (`igc.h:2731`); wingman bits `c_wbbmUseMissiles=0x10`,
`c_wbbmRunAt60Hull=0x20`, `c_wbbmInRangeAggressive=0x40` (`igc.h:786-791`); and the training HUD
overlays were renumbered to the true `TrainingOverlay` enum order (`WinTrek/indicator.h:17-42`).

## Host integration

`TrainingHost` gained the mission variants (`training_1/414/1010`, `training_3/486/1030`,
`TrainingHost.BasicFlight/BasicWeaponry/EnemyEngagement`), plus real destroyed / command-target /
command-id / sector / scanner state the M3/M4 seams read, and `CreateDrone` now carries behaviour /
station-type / tech-level. `TrainingHostSite`, `ClientTrainingSite` and the test `FakeTrainingSite`
implement the full extended `ITrainingSite`. Drones are seated as real `Ship` models with the server's
own drone/wingman AI available (`Ship.NpcUpdates.cs`, SIM-0111..0118); the headless proofs script the
outcomes those AIs would produce (as TR-2b scripts throttle/afterburner for Mission 2).

`TrainingHost.StepLight(float dt)` advances the real mission clock via `Mission.Update` (~0.14 ms)
and the host autopilot pass, **without** the full server tick (~50 ms of headless networking
overhead per step). The long-narrative M3/M4 missions need thousands of elapsed-time steps, so
`StepLight` makes the headless proofs run in milliseconds instead of hours; nothing on the server
tick/broadcast/transport path changed.

## Proof per mission (headless, real input path)

`tests/Allegiance.ClientCore.Tests/TR3TrainingMissions34Tests.cs` (all `[Trait("Tier","Live")]`):

- `Mission3_RunsToCompletion_AgainstTheRealEmbeddedHost` — boots `TrainingHost.BasicWeaponry`, drives
  Mission 3 to goal 13 (landing) through `TrainingMission.RecordKeyPress`, the applied state bits
  (`WeaponsMask`/`MissileFire`), radar target picks, kills and the radar auto-re-target — then
  `ShipLanded` terminates. Asserts 3 derelict kills, both SPACEBAR and both SPACE-key prompts, and the
  landing prompt.
- `Mission4_RunsToCompletion_AgainstTheRealEmbeddedHost` — boots `TrainingHost.EnemyEngagement`, drives
  Mission 4 through chat, waypoint/order acceptance (`GetCommandCondition` via the queued-vs-accepted
  distinction), the intersector transit (sector 1031), cloak toggle, enemy-miner kill, scout
  interception + flee, builder build-complete and landing.
- `Mission34_Seams_LandRealEffects_OnTheHost` — asserts the new seams resolve real host state
  (drone behaviour carried, destroyed flips, command target/id and sector resolve).

Numbers (measured this session): each of the three TR3 tests **passes 3/3 × 3 isolated runs**
(`Failed: 0, Passed: 3` each run, sub-second with `StepLight`).

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Radar auto-re-targets the next same-type hull when one dies | mission3.cpp:750-753, currenttarget.cpp:39-49 | design | `PlayerCurrentTarget` lock re-anchors on target change; the headless driver re-targets after a kill (after the destroy gate observes the dead victim) |
| `GetShipIsDestroyed` treats a missing ship as destroyed | getshipisdamagedcondition.cpp:53-55 | design | reproduced exactly |
| Queued orders don't satisfy `GetCommandCondition` until accepted | getcommandcondition.cpp:44-49 | design | host records only accepted/current command ids; the driver accepts |
| Long mission narrative requires much simulated time | trainingmission.cpp:167-266 | era constraint | `TrainingHost.StepLight` advances the real clock cheaply (skips headless server-tick network overhead) |
| `GetShipIsDestroyed`-on-`CurrentTarget` can resolve a null target to "destroyed" | getshipisdamagedcondition.cpp:53-55 | design (gameplay) | reproduced; the driver keeps a live radar target so the destroy gates behave |
| Inter-sector travel / NPC combat outcomes | shipIGC.cpp warp/AI | design | universe transit is real (sector 1031); combat *results* scripted as in TR-2b's scripted-throttle approach |

## Modern alternative considered

Native runs the training logic against the client's own local IGC copy. As in TR-2b, the port reuses
the embedded authoritative host, so training actions are in-process and there is no divergent world.
New here: instead of driving the long missions through the full server tick, `StepLight` advances the
authoritative mission clock directly (kept authoritative, no duplicated timer) — the modern choice of
letting the headless proof spend real sim time without paying per-tick network/broadcast overhead.

## Verification (measured)

- Whole-solution build `AllegianceCore.slnx` (Debug): **0 warnings, 0 errors**.
- No `src/Allegiance.Server` / `src/Allegiance.Core` source change → AOT publish gate not triggered.
- New TR3 tests (Tier=Live) **3× alone**: `Failed: 0, Passed: 3` × 3 runs.
- Corrected the TR-2 screen test that asserted "only Basic Flight playable" to the TR-3 behaviour;
  passes 3/3 alone.
- Fast tier once: Lobby 19/19 · ClientCore.MonoGame 2477 pass / 3 not-executed · ClientCore.Tests
  one failure in code touched (the screen test, fixed) → re-ran ClientCore fast tier: **1408 pass /
  0 fail** · Core 4815/4815.
- `P700ShowcaseLiveTests` 5× not required: no bots/flight/sim/server-tick/transport code changed.

Raw logs: `~/allegiance-evidence/TR3/` (test logs, TRX). Small evidence (report + parity edits) is
in-repo; no screenshots are included because the interactive real-client run is deferred (below).

## Remaining (honest)

The **interactive real-client runs** — Basic Flight from the intro screen → Single Player → Training →
mission → completion with real 1280×720 backbuffer screenshots, and the analogous M3/M4 runs — are
not delivered this task. As TR-2b recorded, this is the remaining engineering: the MonoGame client
must seat the player into the host's airborne interceptor through the standard logon/seating path
adapted to start in flight, then navigate the training screen and pump the mission against the live
rendered session. The framework, missions, host and headless proofs are all real and verified; the
pixel-level interactive half needs that client seating work (a TR-4-sized client integration).
Because real windows weren't produced, no screenshots are attached — I have not fabricated any.

Also deferred, matching TR-2b: an `OT_buoy`/`OT_warp` sim model (M4's aleph is a Ship-id stand-in
with the same geometric semantics), native loadout/equipment and full combat ballistics in the
embedded host (the proofs script kill outcomes).

## Files changed

Added: `src/Allegiance.ClientCore/Training/Mission3.cs`, `Mission4.cs`,
`CombatConditionsAndActions.cs`; `tests/Allegiance.ClientCore.Tests/TR3TrainingMissions34Tests.cs`.
Modified: `ITrainingSite.cs`, `CreationActions.cs`, `GeometricConditions.cs`, `TrainingMissionIds.cs`
(ClientCore); `TrainingHost.cs`, `TrainingHostSite.cs` (SinglePlayer); `Game1.Training.cs`,
`ClientTrainingSite.cs`, `TrainingScreenModels.cs` (Game); `FakeTrainingSite.cs`,
`TR2TrainingTests.cs` (tests); `docs/parity/parity-matrix.csv`.
