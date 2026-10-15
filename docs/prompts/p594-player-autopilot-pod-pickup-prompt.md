# P594 — player autopilot targeting a friendly POD should fly into it and rescue it (native "pod pickup" goto: intercept course, full speed, skip collision with the target, no 60 u "arrived" stop)

## ⓪ FIRST: turn the P591b re-entry guard OFF by default (Domingo, 2026-10-09)

P591c (`f9103182`+, `docs/reports/P591c_*.md`) fixed the mine-layer ping-pong
**natively** (no braking before intermediate warps → the native +15 exit push
clears the arrival aleph). P591b's `LastArrivalWarpId` re-entry guard
(`Sector.ResolveWarpCollisions`, non-native) now only fires for **combat bots**
drifting back into their arrival aleph (100+ blocks per live run), which in
**native would warp back out** after the 2 s `m_warpState` re-arm. Domingo:
"turn the guard off by default". So: make the guard **opt-in** (a server
setting/config flag, default **off**, documented as a non-native QoL option),
keep its code + the block counter, and add a test that the default is off and
that the drones still reach and lay with the guard off (P591c's proof). Do this
before the pod-pickup work and mention it in the report.

## Why this task exists

Domingo (2026-10-09, live 221): "why does my autopilot not rescue pods from our
team?" Contact rescue works (P590: friendly rescue-capable ship touches a pod →
`PLAYER_RESCUED`), and bots now divert to pods. But the **player's autopilot**
toward a targeted friendly pod stops short and never touches it.

**Native** (`Igc/common.cpp:1430-1446`, `Waypoint::GetGotoPosition`, `case
OT_ship`, "mmf pod pickup"): if either the ship or the target is a lifepod
(`c_habmLifepod`):
```cpp
Vector dp = itsPosition - myPosition;  Vector dv = target->GetVelocity();
float t = solveForImpact(dp, dv, pship->GetHullType()->GetMaxSpeed(), 0.0f, &direction);
gpm = c_gpmFast;                      // full speed
*pvectorGoto = itsPosition + t * dv;  // intercept point
*ppmodelSkip = m_pmodelTarget;        // don't avoid the pod: fly INTO it
```
(The same applies with a pod flying to a rescuer.)

**Port:** the client autopilot (`Game1.FlightInput.cs`, `AutopilotSteering`)
treats a pod like any target: **P524 obstacle steering avoids it**, and
`targetStationary && distance <= AutopilotArrivalRadius (60 u)` disengages
"arrived" (`Game1.FlightInput.cs:~2072`, `AutopilotSteering.cs:44`), so the
player hovers next to the pod without contact. Check P575's `DockApproach`
path doesn't intercept this case.

## Scope

1. Port the native pod-pickup goto into the shared steering
   (`AutopilotSteering`/`DockApproach` planner, P575): an intercept point via a
   ported `solveForImpact`, full speed, **skip avoidance for the target**, and
   **no "arrived" disengage** for a pod target (stay engaged until the rescue
   happens or the target becomes invalid: rescued, dead, docked).
2. Applies to the player autopilot (target a friendly pod + G), to a pod's own
   autopilot toward a rescuer/target (both directions per native), and to
   bots (P590 diversion should use the same intercept).
3. Disengage with native semantics after `PLAYER_RESCUED` (the pod is gone).

4. **Being OFFERED the rescue (native order flow; Domingo: "so I will be offered
   the option to rescue?"):** native's pod pilot sends `c_cidPickup` to a rescuer
   (`WinTrek.cpp:~10650-10690`); the rescuer gets it as a **queued command**
   (HUD order line + the native "new command" sound); accepting it
   (`TK_AcceptCommand`, the port's **Home**: `KeyBindingTable.cs:110`,
   `Game1.FlightInput.cs:~1481-1521`, `world.MyQueuedCommand`) promotes it,
   sets the target to the pod (`SetTarget`/`SetAccepted`, `:~8823-8890`), and the
   native "execute" variant (`TK_ExecuteCommand`) also engages autopilot
   (`AcceptQueuedCommand`, `:10626-10712`, the `bExecute` branch). Verify end to
   end that a pod's pickup request (P590's server path,
   `Server.ShipCommands.cs`) **reaches the player as a visible pending order**
   with sound, that Home accepts it (target = the pod) and the execute key
   accepts + engages autopilot → this task's intercept → rescue. Also: when
   the player is the pod, check the native way to request pickup from a
   friendly ship works (P590) and the rescuer bot acknowledges it.
   Proof: frames of the pending "pick me up" order on the HUD, the accept, and
   the rescue.

## Proof

Live (221 config, MCP bots, P572 tooling): eject a bot near the player,
target its pod, press G → the player intercepts, touches it → server
`PLAYER_RESCUED` + "You rescued X." (P578) + score +5 (P583); a moving pod
is intercepted (frame strip). Tests: `solveForImpact` vs native, avoidance
skipped for the target, no arrived-disengage for pods.
Report `docs/reports/P594_PLAYER_AUTOPILOT_POD_PICKUP.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 2 consecutive runs **on the final code** (reduced from 3 per Domingo, 2026-10-09: the flakes are known/ledgered). Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 2× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 2× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
