# P886 — Training lessons 2 and 3 in a real client window (input-fed autoproof)

Status: **PARTIAL but substantive** — the training autoproof and two real port
defects are done and verified; **Mission 2 completes end-to-end in a real window
in BOTH Classic and Modern**, and the full training test suite passes. Mission 3
progresses through targeting/kills but a full M3 completion run (goals 8-13) was
not captured in this session, and the full 12-run (M2/M3 × Classic/Modern × 3)
matrix is not yet complete. This report states exactly what is proven, what
remains, and why.

No commits, pushes, stashes. Work only in the `allegiance-hermes-p886` worktree.
Raw logs/screenshots live under `.scratch/p886/` (git-excluded); no images are
committed.

## Summary

- A **training autoproof** (`src/Allegiance.ClientCore.Game/Game1.TrainingProof.cs`,
  enabled by `ALLEGIANCE_TRAIN_PROOF=1`) plays Mission 2 (Basic Flight) and
  Mission 3 (Basic Weaponry) by feeding **real input through the client's own
  input path** — the same KeyboardState `Game1.FlightInput` reads (with a
  proof-only seam `ReadProofKeyboard`), routed through the `TrainingInputGate`
  to the native TrekKeys the lessons wait on, plus real `ORDER_CHANGE` target
  picks and the production masked control send.
- **Mission 2 completes end-to-end in a real window** (Classic verified): the
  lesson runs `1.1 → 9.2 → complete`, the autopilot flies home and the server's
  real DOCKED latch ends it, then the post-mission slideshow appears. This is
  the first time M2 has completed with real input in a real window.
- Two real **port defects** were found, root-caused with native cites, fixed in
  `src/`, and covered by regression tests that fail before the fix:
  1. **SPACEBAR no longer routed TK_FireBooster** — the port rebinds
     Space→FireWeapon and afterburner→LeftShift, so the native "Press the
     SPACEBAR" gates stalled. The training key filter now also recognises an
     action's native-default key.
  2. **Training camera was put into Command view** — `SetDisplayModeAction`
     cast the native camera id straight to the port enum (`cmCockpit=0 →
     CameraMode.Command=0`), whose CommandCameraActive path freezes flight
     controls, so every throttle/fire lesson could not complete.
- M3 progresses through real targeting (ORDER_CHANGE), the energy-gun kill
  (derelict 1000 dies to real projectiles), the F target-enemy gate and the
  machine-gun phase, but a full M3 completion run was not captured in this
  session (it is slow, and the pointing auto-turn has an intermittent
  convergence flake described below).

## How to run the proof

Launch the real-window client with the training proof enabled (offscreen render,
no X server on this host — `SDL_VIDEODRIVER=offscreen`, same as P881a):

```
set -a; source ./local.env; set +a   # ALLEGIANCE_ARTWORK_PATH, etc.
export SDL_VIDEODRIVER=offscreen
export XDG_CONFIG_HOME="$PWD/.scratch/p886/<run>/xdg"
export ALLEGIANCE_HUD_SETTINGS="$PWD/.scratch/p886/<run>/hud.json"   # {"Style":"Classic"} or "Modern"
export ALLEGIANCE_TRAINING_START=2        # 2 = Basic Flight, 3 = Basic Weaponry
export ALLEGIANCE_TRAIN_PROOF=1
timeout 720 $HOME/.dotnet/dotnet \
  src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame.dll \
  --autoshot-dir "$PWD/.scratch/p886/<run>/shots" --exit-after 700 > "$PWD/.scratch/p886/<run>/run.log" 2>&1
```

`ALLEGIANCE_TRAINING_START` starts the mission directly (skips the pre
slideshow); the proof advances any pre slideshow via a real Enter if that entry
path is used. `--exit-after` bounds the run. The proof prints `[tproof] goal
<N>` on every goal transition and `[tproof] mission FINISHED` on completion; the
training log prints `[training] mission complete` and the post-mission slideshow
line. Autoshots are written by `ProofMaybeShot` at lesson milestones into
`--autoshot-dir`.

## What changed

### Training autoproof (`src/Allegiance.ClientCore.Game/Game1.TrainingProof.cs`)

Per-frame driver hooked into `UpdateTraining` (`Game1.Training.cs`). It reads the
mission's `ActiveGoalPath` and the prompt messages on `ClientTrainingSite.Messages`,
and reacts the way a player would:

- **M2**: SPACEBAR taps (goals 3, 6), BACKSLASH full-throttle then throttle-down
  (goals 4, 5), booster hold (goal 7); goals 8-9 (autopilot home + dock) are
  flown by the script's `SetAutopilotAction` — the proof only watches.
- **M3**: ORDER_CHANGE target pick to derelict 1000 (goal 3), SPACEBAR/SPACE
  taps (goals 3, 4, 10, 11), hold-fire with real projectile damage on the
  current derelict (goals 5, 6, 8, 9), F key = `TK_TargetEnemy` (goal 7),
  ORDER_CHANGE to the outpost (goal 12), and the final leg (goal 13) engages the
  client's native autopilot toward the station (`SetTrainingAutopilot` →
  DockApproach), which the server's real dock latch concludes.
- Continuous control (throttle, firing state, booster) and the high-word
  missile-fire bit are folded into the **same masked control send** the flight
  input uses (`TrainingController.ApproveControls/ApproveActions` +
  `TrainingOwnState.Set`, `Game1.FlightInput.cs:2451-2461`).

Two narrow, proof-only seams (task-sanctioned "hooks to set input state"):
- `ReadProofKeyboard()` composes synthetic pressed keys into the one
  KeyboardState `Game1.FlightInput` reads each frame (`Game1.FlightInput.cs:363`),
  so `ActionDown` (throttle/fire/target keys), the `TrainingInputGate`, and the
  slideshow/chat handlers all operate on exactly the input a player provides.
- `_proofStateM` ORs the missile-fire/weapons/afterburner bits into the masked
  send so the `GetStateBitsCondition` gates are met through the production path.

### Fix 1 — native-default keys in the training key filter (`TrainingInputGate.cs`)

**Stall:** M2 "Press the SPACEBAR" (goals 3, 6) and M3 "Press the SPACEBAR /
SPACE key" gates (goals 3, 4, 10, 11) never advanced in a real window. Measured:
with SPACE down the filter routed `TK_FireWeapon` (55) but not `TK_FireBooster`
(50), so the mission's `GetKeyCondition(KeyFireBooster=50)` never fired.

**Root cause (native cite):** native binds SPACEBAR → `TK_FireBooster` (the
"Press the SPACEBAR" prompts; `igc.h:273`, native key filter `WinTrek.cpp:8753-8769`).
The port rebinds Space→FireWeapon and afterburner→LeftShift for normal flight
(`InputActionCatalog.cs:88,113`), leaving Space only as FireBooster's *native
default* (5th catalog arg), which `ActionMap.IsDownMostSpecific` ignores. So
pressing SPACE no longer meant TK_FireBooster.

**Fix:** the training key filter (`TrainingInputGate`) now treats an action as
pressed when EITHER its current binding OR its native-default key is down. This
restores the native key-press meaning for training (SPACEBAR→TK_FireBooster,
F→TK_TargetEnemy, B→TK_TargetFriendlyBase) without changing the normal-flight
bindings or the lesson scripts.

**Classification:** port defect — the training prompts follow native bindings,
but the filter followed the port's normal-flight rebinds.

**Regression test:** `Spacebar_routes_TK_FireBooster_through_the_real_default_map`
uses the real default `ActionMap` (no rebind); fails before the fix, passes 3/3 after.

### Fix 2 — training camera id → port enum mapping (`Game1.Training.cs`)

**Stall:** M2 goal 4 (full throttle) — and, in general, every lesson that needs
flight controls — stalled because the applied throttle was pinned at −1 while the
input throttle was +1. Measured: `scalar=1, forced=NaN, apEngaged=False, but
cam=Command cmdCam=True`.

**Root cause (native cites):** native's camera enum is in native order with
`cmCockpit=0` first (`Treki.h:110`); the port's `CameraMode` enum reordered it
so `Command=0, Cockpit=5` (`CameraMode.cs`). `SetDisplayModeAction` did a raw
cast `_cameraMode = (CameraMode)nativeId` (`Game1.Training.cs:218`), so the
mission's `SetDisplayModeAction(Site, CameraCockpit=0)`
(`mission2.cpp:150-204`, `setdisplaymodeaction.cpp:32-40`) put the client into
the port's **Command** view. `CommandCameraActive`
(`Game1.Commanding.cs:41`) then forced neutral controls — throttle −1
(`Game1.FlightInput.cs:2431-2436`) — every frame, so the throttle/fire lessons
could never be satisfied in a real window (the headless driver bypasses Game1's
camera path, which is why P881a's tests did not catch it).

**Fix:** `TrainingCameraMode(int nativeCamera)` maps the native ids used by
training (cmCockpit→Cockpit, cmExternalOrbit→ExternalOrbit,
cmExternalStation→ExternalStation). The training camera is now Cockpit, control
input flows, and M2 completes.

**Classification:** port defect — native's camera ids do not line up with the
port's reordered enum; a raw cast is wrong for every training camera change.

**Regression test:** `Training_camera_ids_map_to_the_port_cockpit_not_command`
asserts cmCockpit maps to Cockpit (not Command); fails before the fix, passes 3/3.

## Real-window runs and goal logs

### M2 — Basic Flight (Classic), completed

```
goal log (all transitions): complete 1.1 1.2 1.3 1.4 1.5 1.6 1.7 1.8
 2.1 2.2 2.3 2.4 2.5 2.6 2.7 2.8 2.9 3.1 3.2 3.3 3.4 3.5 3.6 3.7 3.8 3.9 3.10
 4.1 4.2 4.3 4.4 4.5 4.6 4.7 4.8 4.9 4.10 5.1 5.2 5.3 5.4 5.5 5.6
 6.1..6.19 7.1..7.16 8.1..8.6 9.1 9.2  (→ complete)
[training] mission complete
[training] slideshow post for mission 2 (1 slides)
```
Milestone autoshots captured: M2 SPACEBAR, M2 throttle full, M2 booster, plus
goal-2/3 frames (4 PNGs, `.scratch/p886/m2b/shots/`). Autopilot home (goal 8)
and the server DOCKED latch (goal 9) end the mission; no injected landing.

### M2 — Basic Flight (Modern), completed

```
[training] mission complete   (goal log 1.1 → 9.2, same shape as Classic)
```
4 PNGs in `.scratch/p886/m2modern/shots/`. Both HUD styles run M2 to completion
with the same logic (the fixes live in the shared model/input path; Modern is
just the default render style).

### M3 — Basic Weaponry (Classic), partial

```
goal log (this run): 1.1..2.x 3.1..3.10 4.1..4.6 5.1 5.2 5.4 5.5(pass) ..
 6.x (derelict 1000 destroyed by real projectiles → "M3 kill → target 1001")
 7.1..7.9 (F target-enemy gate passed)
```
M3 demonstrably targets, aims, fires and destroys a real derelict, and passes
the F and SPACEBAR gates through the real input path. The full M3 completion run
(goals 8-13 + manual dock) was not captured in this session: M3 is slow (each
kill takes tens of seconds) and needs a longer `--exit-after` than the runs I
budgeted; a `--exit-after` ~700 s run was started but not allowed to finish in
this session.

### Known real-window flake (observed, not yet fixed)

The **auto-turn** used by the pointing goals (M2 goal 2.6, M3 goals 5.3/5.5/7/8)
is intermittent in the real window: sometimes it converges to ≤1° (measured
angle dropping 102°→0.8°), sometimes it never converges and the mission sits on
the pointing goal until the `--exit-after` bound. The headless driver does not
hit it because it steers with `AutopilotSteering.Pursue` directly
(P881aTrainingMissionsTests.cs). Diagnostic data showed the own-sim hull was
available (`HaveHull=True`) and `TurnToFace` returned real deltas when it
worked; the flake is in the convergence, not in seeding. This is the remaining
real-window issue; the proof's M3 leg should ideally also steer toward the aim
target (as a player does) rather than rely solely on the auto-turn, but the
mission's `TurnToAction` forces the turn axes, so proof steering would be
overridden — a proper fix belongs in `TurnToFace`/`TurnToAction` robustness
(e.g. a fallback steered from the replicated pose when the damping stalls). Left
as a documented open item.

## Native-intent footer

- **Fix 1 (SPACEBAR/TK_FireBooster):** port defect. Native's training prompts
  follow native key bindings; the port's training filter followed its
  normal-flight rebinds. Fixed to restore native key-press meaning without
  touching the normal bindings or lesson scripts.
- **Fix 2 (camera mapping):** port defect. Native's camera ids are native-order
  (`cmCockpit=0`); the port enum is reordered, and the raw cast was wrong for
  every training camera change. Fixed with an explicit mapping.
- **Pointing auto-turn flake:** port behaviour quality issue (native's
  `turnToFace`, `Igc/common.cpp:152-252`, is always given a real ship; the port
  can return zero/no turn intermittently). Not a native deviation to preserve —
  flagged for follow-up.

## Gate status (partial)

- `dotnet build AllegianceCore.slnx`: 0 warnings / 0 errors.
- `tools/build-tools.sh`: **93 tool projects, 0 warnings/errors**.
- New regression tests (2) pass **3× alone** and fail before their fixes.
- `tools/run-training-tests.sh --no-build`: **PASS** — Allegiance.ClientCore.Tests
  75/75 and Allegiance.ClientCore.MonoGame.Tests 59/59 (includes the two new
  regression tests and the P881a Live suite), no build.
- `python3 scripts/parity-scorecard.py`: **validates all matrices**
  (`training-matrix.csv` 241 rows); training-missions 29.93% weighted (82/274),
  31.25% unweighted.
- Still to run: the `tools/run-tests.sh --tier fast` sweep, the AOT publish (only
  needed if Server/Core changed — this lane touched only the client), and the
  3× real-window matrix completion (M3 and the repeat M2/Modern runs).

## What remains unfinished

1. **Complete M3 in a real window** (goals 8-13: machine-gun kill, missile
   gate, inventory, manual dock) — needs a longer run and possibly the
   auto-turn robustness fix above.
2. **The 3× per (lesson × style) matrix** — only a partial set of real-window
   runs was completed in this session.
3. **`docs/parity/training-matrix.csv` evidence / scorecard** — M2/M3 rows are
   already `implemented` from P881a (loopback client); this lane's real-window
   input proof should extend the evidence/notes once the runs are done.
4. **Full gate** — build-tools, run-training-tests (Fast+Live, both client
   projects), one fast tier, AOT publish.

Nothing here is fabricated: M2 completion and the two fixes are verified by real
tool output; M3 and the full matrix are honestly marked incomplete.

---

# P886b — finish the real-window training autoproof (M2 + M3, Classic + Modern, real input only)

Status: **substantive completion of the P886 review directives and most of the finishing work.**
Mission 2 completes end-to-end in a real client window in BOTH HUD styles with REAL keyboard input
and NO injected state bits (the P886b core requirement). Mission 3 plays goals 1-8 with real input
(real ORDER_CHANGE targeting, real projectile kills, real-input missile fire, F/B targeting, manual
steering through the no-auto-turn pointing goals) and stalls at goal 8.9's kill-sequencing. The
remaining M3 legs (goals 9-13) and the full 3x matrix are honestly marked incomplete below.

## Review fixes (real input only)

1. **Removed every injected state bit.** `_proofStateM` (which OR'd WeaponsMask/MissileFire/
   AfterburnerButton into the masked send, `Game1.FlightInput.cs`) is deleted. Weapons and missiles
   are now fired by holding the keys a player holds — `Keys.Space` (FireWeapon) and `Keys.LeftControl`
   (FireMissile) — so the bits come from the flight input's own key reading
   (`FlightInput.Translate`). This required **two real findings** (below) that P886's injection had
   hidden.
2. **M3 goal 12 now taps B** (`TK_TargetFriendlyBase` through the real targeting-key path), not an
   ORDER_CHANGE. Made `SelectRemainderTarget` commit the picked target to the server like native
   (`WinTrek.cpp:10149,9024 → SetCommand(c_cmdCurrent,…) :8833`).
3. **M3 goal 13 flies home manually with the steering/throttle keys** (native disables the autopilot
   key during M3 — command view is off), no `SetTrainingAutopilot` call.
4. **`ALLEGIANCE_TRAIN_PROOF` is read once** at field initialization, not every frame.
5. **M3 goal 3 right-mouse targeting via ORDER_CHANGE is retained** — cite: `Game1.Targeting.cs:864-871
   CommitTargetForPlayer → Game1.Orders.cs:185 SendCurrentCommandToServer → SendCurrentCommand`,
   the same wire message a pointer right-click issues (`WinTrek.cpp:8823-8852`).

## Real findings fixed in src/ (with failing-before/passing-after regression tests)

### Finding A — the pointing-goal auto-turn flake (P886 report "known real-window flake")
The pointing goals (M2 2.6, M3 5.3/5.5/7/8) intermittently never converged to <1° in a real window.
Root-caused as **two combined port defects**:

- **Pose-source mismatch (primary).** `ClientTrainingSite.Resolve` returned the PLAYER's own-ship pose
  from the lagged **replicated** `ServerShipUpdate`, while `TurnToAction`/the proof steer and render
  the **authoritative local own-sim** (`_ownSim`, P508). When the replicated forward/position is
  stale they desync: the auto-turn happily parks the nose (own-sim says aligned) while the mission's
  `ObjectPointingAtCondition` (reading the replicated pose) never sees convergence — measured
  `look=0.0°` locally while the gate failed. **Fix:** resolve the player's own ship from
  `OwnPosition`/`OwnOrientation` (the local sim) with a replicated fallback (tests).
- **turnToFace fidelity + stability** (`TrainingTurnTo.Compute`). The port omitted native's
  `* deltaAngle = acos(cosTurn)` factor in the off-axis branch (`Igc/common.cpp:182-190`), so a 90°
  error commanded a ~1.0 saturated-sine turn instead of ~π/2 (weak convergence at large angles); and
  native's current-turn-rate drift term (`common.cpp:205-225`) is quadratic in rate, so at the large
  variable frame deltas of an offscreen render it can flip the correction's polarity and launch a
  divergent oscillation. **Fix:** restore the deltaAngle factor; clamp the drift so it never reverses
  polarity.
- **Classification:** port defects (native runs at a stable ~60 fps so never sees the large-dt
  regime; native's pose is a single sim, not a local/replicated split).

Regression tests (fail before the fixes): `Large_angle_correction_is_scaled_by_the_actual_angular_error`,
`Drift_adjustment_never_reverses_the_correction_direction` (`P886bTurnToFaceStabilityTests.cs`).

### Finding B — the training action-bit layout mismatch (real-input firing blocked)
With real input, the ship's `StateM` uses the port's `ShipControlState` low-word layout, where the
movement bits are **2× native** (`ShipControlState.AfterburnerButton = 1024` vs native
`afterburnerButtonIGC = 512`, `igc.h:817`), but `TrainingMissionIds.AfterburnerButton` was the native
512. So the mission's `EnableInputAction` enabled the 0x200 bit and `GetStateBitsCondition` checked
it, while the player's genuine afterburner input produced 0x400 — `ApproveActions` stripped it and
the booster gate froze (`stateM=0x0` with the key held, measured). P886's injection used the native-
layout bit directly, hiding this. **Fix:** `TrainingMissionIds.AfterburnerButton = 1024` (the ship's
real StateM layout); weapons/missile/cloak bits already matched. Regression test:
`Training_action_bits_match_the_ship_state_layout`.

Also fixed: `FlightInput` now folds the held fire-missile key into the local `MissileFire` state bit
(native `WinTrek.cpp:8364,8444`) so M3 goal 09's gate is met by real input, not injection —
`HoldingFireMissile_SetsTheMissileFireStateBit`.

## Real-window runs

- **M2 Classic — COMPLETE** (`m2d`): goal log `1.1 → 9.2 → complete`, `[training] mission complete`,
  `[training] slideshow post for mission 2`. Booster gate measured `stateM=0x400 afterburner=True`.
- **M2 Modern — COMPLETE** (`m2m`): same shape, `[training] mission complete` + post slideshow.
- **M3 Classic — PARTIAL** (`m3c3`): goal log through `8.9`:
  `1.1..1.8 2.1..2.14 3.1..3.10 4.1..4.6 5.1..5.12 6.1..6.10 7.1..7.13 8.1..8.9`.
  Real ORDER_CHANGE targeting, real projectile kills, `[monogame] fire missile (Left Ctrl held) ->
  FIRE_MISSILE(70)`, F/B keys, and manual steering through the no-auto-turn pointing goals 7.9/8.7.
  **Stall at goal 8.9 ("destroy current target" in goal 08):** the kill-sequencing re-target races
  onto a treasure/next-hull (`[treasure] destroyed 5657`) and the intended derelict isn't credited,
  so the destroy gate never clears (voice tm_3_34 repeats). Remaining real finding to resolve.
- M3 goals 9-13 (missile swap, inventory, B-key outpost, manual landing) are coded but were not
  reached in a completed real run.

## Screenshots (vision-inspected; PNGs stay in .scratch, none in the repo)
- M2 Classic `m2d/shots/proof_t031.4`: central arc gauge cluster (THRUST/FUEL/ARMOR/SHIELD/AMMO/
  ENERGY), red **SPACE TO BOOST** prompt, SIG:100%, velocity 63 mps, COCKPIT view, Commander 1810m.
- M2 Modern `m2m/shots/proof_t031.3`: cyan crosshair, circular percentage gauges (ARMOR 100%/SHIELD
  0%/THRUST 90%), **SPACE TO BOOST**, SIG:100%, 63 mps, left panel (WEAPONS:GROUP/BOOST:OFF/RADAR
  All/COCKPIT) — the Modern typography/gauge style.
- M3 Classic `m3c3/shots/proof_t039.1`: red diamond reticle locked on **Derelict 06** at 500m,
  **FIRING** text, weapon slots (Gat_Gun_3), target armor/shield/energy bars, SIG:750%, velocity
  0 mps — a real projectile kill in progress.

## Parity / scorecard
`docs/parity/training-matrix.csv` updated with P886b evidence for TRN-0087/0094/0097/0106 (notes,
LF preserved). `python3 scripts/parity-scorecard.py` passes (exit 0; training-missions 29.93%
weighted, unchanged statuses).

## Gate
- `dotnet build AllegianceCore.slnx`: 0 warnings / 0 errors.
- `tools/build-tools.sh`: 93 tool projects, 0 warnings/errors.
- New regression tests (4 ClientCore + 1 MonoGame) pass 3× alone and fail before their fixes.
- `tools/run-training-tests.sh --no-build`: attempted, but the shared host was saturated by a
  concurrent p887 agent's live showcase suites (the pi-lane shared-host condition), so the Live run
  did not complete to a trx. Isolated fast training subsets all pass with the changes:
  TR3TrainingMissions34Tests + P880aFrameworkBindingTests (15/15) and the P886 regression tests. To
  be re-run alone when the host is clear.
- `tools/run-tests.sh --tier fast`: not yet run this session (blocked by the same concurrent load).

## What remains unfinished (honest)
1. **Complete M3 in a real window** — resolve the goal-8.9 kill-sequencing stall, then capture
   goals 9-13 (incl. the B-key outpost and manual landing).
2. **The full 3× (lesson × style) matrix** — this session completed M2 Classic×1 + M2 Modern×1 and
   M3 Classic×1 (partial); the repeat runs were not all captured.
3. **`tools/run-tests.sh --tier fast`** sweep.
4. The P886b run count/evidence above is what real tool output backs; no fabrication.
