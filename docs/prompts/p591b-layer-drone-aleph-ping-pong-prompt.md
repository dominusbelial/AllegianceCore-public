# P591b — mine-layer drones ping-pong through alephs on 221 and never lay: side 0 Rigel↔Alderan ×34, side 1 Organia↔Gaia ×378

## Why this task exists

Live on 221 @ `937388a8` (P591 `59c7ee21` deployed), showcase 15v15 with
`minefields {side: both}` (2026-10-09 14:06):
- `[drone] drone-type bucket '.Caltrop' complete on side 0 → spawned drone
  ship 8102 (drone type 200, pilotType 5, hull 'Mine Layer Drone',
  laysExpendable 1)`, the same for side 1 → ship 8103. (P591's bucket fix works.)
- **No `[layer] laid` line in 20+ minutes.** Instead:
  - ship **8102: 34 warp crossings**, `Rigel (1059) → Alderan (1052)` via
    warp 2186, then immediately `Alderan → Rigel` via warp 2187, repeating.
    It arrives in the target sector and bounces straight back.
  - ship **8103: 378 crossings** `Organia (1051) ↔ Gaia (1062)` via warps
    2174/2175: stuck near its own home, never progressing.
P591's report claims the ping-pong fixed ("1 crossing vs 24", reusing
`AutopilotSteering.Pursue`), but that was its own map/run. It doesn't hold on
the 221 config for either side.

## Scope

1. Reproduce on the 221 config (local server, `~/allegiance-deploy-compose.server.json.local`
   with the 13-sector `m2xspiderw` map, a showcase 15v15 with minefields,
   or just the two layers ordered to Alderan): log each drone's plan target,
   route (path of clusters), the chosen warp each tick near alephs, its
   position relative to the arrival aleph after each crossing, and why it
   re-enters.
2. Root-cause it against native: how native drones avoid re-entering the
   arrival aleph (warp cooldown/`m_warpState`/`ResetWarpState`, the
   `GotoPlan` routing `FindPath`, the arrival offset after a jump, which warp
   the plan targets), with file:line. Likely suspects: the arrival point
   inside the exit aleph's trigger radius plus no post-jump cooldown; the
   plan still pointing at a warp in the previous sector; the side-1 route
   computing a 2-cycle (Organia↔Gaia) because the target buoy/sector isn't
   resolved for that side.
3. Fix it natively; make sure **pod-home (P560) and bot routing** also don't
   ping-pong (same path).
4. Prove the minefield laid: both sides' layers reach the target and log
   `[layer] laid MINE …` near the Alderan alephs, ships then take mine damage
   (P452) and the P574 per-hit pops play (real window).

## Proof

Before/after crossing counts per drone (target: ≤ the route length), the
`[layer] laid` lines for both sides, a real-window frame of the field/pops,
tests for the re-entry guard and the route resolution per side.
Report `docs/reports/P591b_LAYER_PING_PONG.md`.

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
