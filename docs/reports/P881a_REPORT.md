# P881a — training missions batch 1 (Basic Flight and Basic Weaponry)

The 20 `queue=P881a` rows now run through the **real production path**: `ClientCore` scripts →
`ITrainingSite` → `Game1` / the embedded `Allegiance.SinglePlayer.TrainingHost` with ordinary
loopback replication. The two lessons complete **start to finish in a real loopback client**:
targets are picked with real `ORDER_CHANGE`, derelicts die to real projectiles, equipment actions
mutate the real ship, and the training mission ends on the server's real **DOCKED** latch (no
manual `ShipLanded` call, no injected outcomes in the completion tests). 19 rows are
`implemented`; M3 lesson 09 remains `partial` because the port's M3 starting loadout carries no
spare missile launcher in cargo (TRN-0217, P882b's lane).

No commits, pushes or stashes. No remote machines. Native citations are relative to native `src/`.
Screenshots and raw logs live under `.scratch/p881a/` (git-excluded); no new images are committed.

## Summary

- Training matrix: **26.11% → 37.61% weighted** (340/904), **38.82% unweighted** (92/237).
- Basic Flight (`Tm2BasicFlight`) completes in a real loopback client by flying the scripted
  manoeuvres, accepting the autopilot order and letting the server dock the ship.
- Basic Weaponry (`Tm3BasicWeaponry`) completes the same way: ten real derelict hulls, real
  `ORDER_CHANGE` target picks, real gunfire destroying them, real shield/armour damage, real
  weapon toggle/rearm, then a real docking.
- Real windows (Classic and Modern) launch both lessons, render the HUD, and progress the goal
  log with no input injected. M2 now reaches goal **3.6** (previously 2.4 in P882a) — its
  commander waypoint/radar demo, auto-turn and control release all complete in a real window.

## What changed

### Core model (`Allegiance.Core`)

- **`Ship.Autopilot`** (`src/Allegiance.Core/Sim/Ship.cs`) — the native `m_bAutopilot` latch
  (`SetAutopilot`, `training/setautopilotaction.cpp:41-43`). The navigation pass now also steers a
  player ship that holds a real station plan (`ApplyNpcAutopilot`), so a scripted autopilot flies
  and docks instead of stopping at the station's outer radius. Station plans use the real bay
  approach (`TryDockApproach` → `DockApproach`, `common.cpp:1505-1545`).
- **`ShipKinematics.GetTorqueMultiplier`** is public (`shipIGC.h:542-549`) and
  **`OwnShipSimulator.TurnHull`** exposes the live mass/torque/turn-rate inputs the native
  `turnToFace` solve needs (`common.cpp:152-252`).
- The training host's idle-control value is now `ControlData.Neutral` (throttle −1 = kill,
  `ControlData::Reset`, `igc.h:1666-1670`); the old `(0,0,0,0)` drove every idle training hull
  forward at half speed.

### Training host (`Allegiance.SinglePlayer`)

- `GetCommandTarget` reads the **real** `Ship.CommandTarget` written by an ordinary client
  `ORDER_CHANGE` when the script has not set a target (`gettargetcondition.cpp:52`), and
  `GetCommandId` reads the real `CurrentCommandId`.
- `IssueCommand` mirrors current/accepted orders onto the real ship slots
  (`setcommandaction.cpp:69-84`); a station goto sets the real `PlanTarget` so the native dock
  approach flies the ship.
- `SetAutopilot`/`IsAutopilot` use the real `Ship.Autopilot` field.
- New real-ship actions: `DealDamage` (`dodamageaction.cpp:26-49`), `ToggleWeapon`
  (`toggleweaponaction.cpp:41-47`), `SwapWeapon` via `Ship.TrySwapPartWithCargo`
  (`swapweaponaction.cpp:25-32`), `RepairRearm` (`repairrearmaction.cpp:37-74`, including the
  native missile/ammo cargo fill).
- `GetFraction` for ships reads the real armour fraction.
- Training drones created with a player pilot type (`c_ptPlayer` = 10, e.g. M3's derelicts) are
  no longer marked NPCs (`mission3.cpp:222`): they get no server AI and stay at their authored
  positions.

### Client binding (`Allegiance.ClientCore.Game`, `Allegiance.ClientCore`)

- `ClientTrainingSite` binds the remaining actions to real facilities: own-ship orders go through
  the real `ORDER_CHANGE` path, `SetAutopilot` engages the client's native autopilot toward the
  ordered target, `SetDisplayMode` sets the camera, `ShowPane` sets the console panes, and
  `ToggleWeapon`/`SwapWeapon`/`RepairRearm`/`DealDamage` reach the host's real ship.
- `TrainingOwnState` (`TrainingRuntime.cs`) is the client's own applied controls/state mask
  (native `GetShip()->GetControls()/GetStateM()`), written by `Game1.FlightInput` after
  `TrainingController.ApproveControls`. The training conditions read it for the own ship, so the
  high-word missile-fire bit (which never rides the 16-bit wire state) and the exact masked
  controls are visible.
- `ITrainingSite.FrameDeltaSeconds` supplies the native `GetWindow()->GetDeltaTime()` to
  `TurnToAction` (`TurnToAction.cpp:61`); the old code passed the absolute mission clock, making
  the corrective turn orders of magnitude too small (M2 goal 2 / M3 goal 5 could stall).
- `ClientTrainingSite.IsShipDestroyed` treats a latched-dead `ShipEntity` (`IsDead`) as destroyed,
  not merely a missing world entry.

### Mission data fixes (`Allegiance.ClientCore`)

- `TurnToAction` gained the native lazy `CurrentTarget` form; Mission3's auto-turn now re-resolves
  the player's current target every frame instead of the target captured at mission build time
  (which was null and fell back to the station, so the auto-turn fought the pointing check).
- `PlayerCurrentTarget` keeps an eliminated original latched; it only re-anchors when the fetched
  target is still alive (`currenttarget.cpp:39-49`).
- Mission3's `TK_TargetEnemy` is the native id **80** (`igc.h:303`), not the invented 111; the
  real `TrainingInputGate` delivers it from the F binding. `TK_TargetCenter` (90) and
  `TK_TargetFriendly` (77) were added to `TrainingMissionIds`.

## Per-row certification

All rows use `tests/Allegiance.ClientCore.MonoGame.Tests/P881aTrainingMissionsTests.cs`, which
joins a real embedded host over loopback (`ConnectionSession`), runs the production
`TrainingSession` pump over the replicated `ClientWorld`, and sends ordinary wire inputs. The
completion tests do not call `SetCommandTarget`, `DestroyDrone`, `SetAppliedControls` or
`mission.ShipLanded()`.

| Row | Native rule and citation | Status | What is real / what remains |
|---|---|---|---|
| TRN-0087 | Mission 2 `CreateMission`, `mission2.cpp:118` | **implemented** | Whole lesson start→finish in a real loopback client; real commander waypoint, real dock ends it. |
| TRN-0088 | M2 lesson 01, `mission2.cpp:150-204` | **implemented** | Control masks + real cockpit camera binding. |
| TRN-0089 | M2 lesson 02, `mission2.cpp:207-280` | **implemented** | Real buoy creation/resolution, commander Goto + host autopilot, real auto-turn (own hull kinematics). |
| TRN-0090 | M2 lesson 03, `mission2.cpp:283-376` | **implemented** | SPACEBAR through the real training key gate (`TK_FireBooster`). |
| TRN-0091 | M2 lesson 04, `mission2.cpp:377-457` | **implemented** | Full-throttle gate reads the masked own-ship controls sent on the wire. |
| TRN-0092 | M2 lesson 05, `mission2.cpp:458-504` | **implemented** | Throttle-off gate, same real own-ship controls. |
| TRN-0093 | M2 lesson 06, `mission2.cpp:505-651` | **implemented** | Gauges/turning over real timers and masks. |
| TRN-0094 | M2 lesson 07, `mission2.cpp:652-775` | **implemented** | Booster gate reads the client state mask. |
| TRN-0095 | M2 lesson 08, `mission2.cpp:776-823` | **implemented** | Real station `PlanTarget`/`Autopilot`; native bay approach; `GetAutopilotCondition` reads the real latch. |
| TRN-0096 | M2 lesson 09, `mission2.cpp:824-859` | **implemented** | Ends on the server's DOCKED edge (`trekigc.cpp:4131`); no manual landing call in the test. |
| TRN-0097 | Mission 3 `CreateMission`, `mission3.cpp:233` | **implemented** | Whole lesson start→finish in a real loopback client. |
| TRN-0098 | M3 lesson 01, `mission3.cpp:286-337` | **implemented** | Real control lock + cockpit camera. M3 starting equipment is TRN-0217 (P882b). |
| TRN-0099 | M3 lesson 02, `mission3.cpp:338-424` | **implemented** | `DoDamageAction` dents the real shield/hull (`dodamageaction.cpp:26-49`). |
| TRN-0100 | M3 lesson 03, `mission3.cpp:425-494` | **implemented** | Real `ORDER_CHANGE` pick writes `Ship.CommandTarget`; `GetTargetCondition` reads it. |
| TRN-0101 | M3 lesson 04, `mission3.cpp:495-546` | **implemented** | Scan/target overlays + SPACEBAR through the real gate. |
| TRN-0102 | M3 lesson 05, `mission3.cpp:547-664` | **implemented** | Lazy current-target auto-turn + real weapon fire gate. |
| TRN-0103 | M3 lesson 06, `mission3.cpp:665-744` | **implemented** | Real projectiles destroy the locked target; eliminated target stays latched. |
| TRN-0104 | M3 lesson 07, `mission3.cpp:745-822` | **implemented** | Native `TK_TargetEnemy` 80 via the F binding; real face-new-target. |
| TRN-0105 | M3 lesson 08, `mission3.cpp:823-904` | **implemented** | Real weapon toggle, real ammo spend, real target destruction. |
| TRN-0106 | M3 lesson 09, `mission3.cpp:905-991` | **partial** | `SwapWeapon` performs the real cargo-aware swap and the missile-fire gate reads the client state bit, but the default M3 loadout has no spare launcher in cargo, so nothing is swapped and the kill is gunfire. Remaining: native M3 cargo/equipment (TRN-0217, P882b), then the missile launch/damage. |

## Native intent

**Design.** Goal/constraint/wait ordering and the native narrative are unchanged; the completion
tests traverse every M2 lesson and M3 lessons 01-09 in order. The own ship's orders, target picks,
keys and controls are ordinary wire inputs; the mechanics are real server models. Both HUD styles
share the same logic; the real-window captures in both styles render Classic and Modern HUDs.

**Era constraints.** Native direct `trekClient` operations become in-process host mutations plus
ordinary loopback replication; no training protocol, service, port or environment variable was
added. Autopilot for the real client engages the existing client-side native autopilot (the client
owns its pose, P508); in a headless/loopback test the host's real navigation pass flies the same
station plan. A training buoy still has no runtime `OT_buoy` model, so its target lives in the
host's waypoint table; everything else resolves real models.

**Native bugs.** Kept: HasAmmo's inverted meaning, `ResetAction`'s commented-out body, and the
asteroid-Y formula. Port defects corrected to native semantics: `ControlData::Reset()` throttle
(−1 = kill) for idle training hulls, `CurrentTarget`'s eliminated-original latch, `c_ptPlayer`
derelicts being non-NPCs, and `turnToFace`'s frame delta. No native bug was "fixed" or changed.

## Verification

- Solution build: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **93 tool projects**, 0 warnings/errors.
- New suite `P881aTrainingMissionsTests`: **7/7, three times alone** (M2 completion 67 s, M3
  completion 55 s per run; the other five cases are binding/divergence checks, including one
  Game1-facility wiring test and the native `TK_TargetEnemy` gate).
- One Fast sweep: **11,679 tests**, 11,675 passed, 3 failed, 3 optional skips; infrastructure
  passed. The gate is **red**, with all three failures pre-existing/ledgered:
  `ShopLauncherMountNumberingLiveTests` (passes alone on this tree),
  `NpcWingmanDefendLiveTests` (fails identically in a clean HEAD worktree under `.scratch/base/`),
  and the `AddPartTests` treasure quarantine flake (the runner's automatic retry passed). The
  same two training lanes' baseline failures (`TR3...Mission3`, `TR4...Mission5`) were confirmed
  in the clean HEAD worktree; this task did not introduce them. M4 (which the baseline passes) was
  preserved after a target-precedence correction and passes in the final tree.
- New-suite extra runs: **7/7 twice more** (three total, alone).
- `python3 scripts/parity-scorecard.py`: matrix validates; training **37.61% weighted
  (340/904), 38.82% unweighted (92/237)**.
- Showcase never-parks: **5/5 serial passes** (403 s, 228 s, 247 s, 222 s, 228 s; raw logs
  `.scratch/p881a/showcase-1..5.log`).
- Real windows: `SDL_VIDEODRIVER=offscreen` (no X server/Xvfb is installed here; no window opened
  on the desktop), scratch `XDG_CONFIG_HOME` and `ALLEGIANCE_HUD_SETTINGS`, `--autoshots
  5,50,100`, bounded `timeout`, `--exit-after 105`. Classic and Modern both launched with the real
  host, HUD and loopback join, exited 0, and captured three 1280×720 PNGs each:
  `.scratch/p881a/m2_Classic`, `m2_Modern`, `m3_Classic`, `m3_Modern` (plus a second
  `m2_Modern_b` run). Goal logs: M2 **1.1 → 3.6** (both styles; one Modern run stalled at 2.6
  and the rerun reached 3.6 — a convergence timing flake, not a logic difference), M3 **1.1 →
  3.5** (both styles). Screenshots were inspected (Classic corner gauges, Modern gauges, external
  ship view, real contacts). No images are proposed for the repository.
- `docker compose config --quiet` unchanged (no service/port/env changes).

## Notes for review

- TRN-0106 is deliberately `partial`: this batch did not take TRN-0217 (M3 starting equipment and
  cargo), which is P882b's row. Once a missile launcher is in cargo, `SwapWeapon` already loads it
  through the real swap path; the missile launch/damage will then be exercised by that lane.
- The `IsNpc` change for `c_ptPlayer` training drones is native-correct and is what stopped the
  derelicts from receiving server AI; `ZeroThrottle = ControlData.Neutral` is required for any
  idle training hull to hold position.
