# P591c — find and fix WHY our drones (and bots/pods) steer back toward the aleph they just arrived through; native routing never does this. Relabel P591b's re-entry guard as a non-native safety net

## Why this task exists

**P591b** (`91815dbb`, `docs/reports/P591b_LAYER_PING_PONG.md`) stopped the
mine-layer ping-pong on 221 (side 1 1453 crossings → 2) by adding a
**re-entry guard**: record `LastArrivalWarpId` and refuse to re-transit that
warp until the ship has cleared its transit volume (`Sector.ResolveWarpCollisions`).
Its commit message says native "holds m_warpState until it clears". **That's
inaccurate.** Native (`FedSrv/fsship.cpp:71-137` `CFSShip::HitWarp`;
`:1185-1191`) only has a **timer**: `warpNoUpdate → warpWaiting →` 2 s later
`warpReady`, with no arrival-warp memory. On exit native places the ship at the
destination aleph oriented along it, with velocity `backward × (speed +
c_fExitWarpSpeed (15))` (ported: `ServerHost.cs:~879-932`,
`ExitWarpSpeedBonus = 15`).

So in native a drone **doesn't turn back** toward its arrival aleph. Its
routing (`GotoPlan` / `Waypoint::GetGotoPosition`, `Igc/common.cpp:~1324+`,
cluster path / `FindPath`) aims at the **next** waypoint in the destination
sector (the buoy / next warp), and the exit push carries it clear. **Ours
turned back** (side 0 bounced Rigel↔Alderan after *arriving* in its target
sector; side 1 Organia↔Gaia near home), which points to a **routing/plan bug
in the port**: e.g. the route/plan target still pointing at a warp in the
previous sector after a crossing, a 2-cycle path for side 1, the buoy/target
not resolved in the destination sector, or the shared steering
(`AutopilotSteering.Pursue`, reused by P560) recomputing toward the arrival
warp.

## Scope

1. **Temporarily disable P591b's guard** (test seam) and reproduce on the 221
   config/map (`m2xspiderw`, both sides' `.Caltrop` layers ordered to
   Alderan). Log per tick after each crossing: the current sector, the plan
   target, the computed cluster path, the chosen next warp and the steering
   goal. Find exactly why the goal points back at the arrival warp.
2. Compare with native's drone path/goal selection (cite file:line) and fix
   the root cause natively so drones **never** target the warp they just came
   through unless the route truly requires it. Check bots (MCP combat/stage
   routing) and pods (P560 pod-home) on the same path.
3. Re-enable P591b's guard as a **documented non-native safety net**: fix its
   code comment + an addendum in the P591b report correcting the "native
   holds until clear" claim; add a log line counting each time the guard
   actually blocks a re-entry (it should be ≈ 0 after the routing fix).
4. Proof: with the guard **off**, both layers reach Alderan with minimal
   crossings (= route length) and lay; with it **on**, the guard-block counter
   stays ≈ 0 over a 15v15 showcase. Tests for the plan-target update after a
   crossing and the path per side.
Report `docs/reports/P591c_DRONE_ROUTING_ARRIVAL_ALEPH.md`.

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
