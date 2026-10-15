# P590 — pod rescue doesn't happen: flying your pod into a friendly ship never rescues you (0 PLAYER_RESCUED on 221), and bots never come to pick up pods; port native contact rescue for client-driven ships + the `c_cidPickup` flow + bot pod pickup

## Why this task exists

Domingo (2026-10-08, live on 221 @ `5e9eabff`, 15v15 showcase): "why are bots
not saving me when they see me podded? Also if I fly to them while podded and
reach any friendly I should be instantly rescued, like in native."

**Evidence:** 221 server log, last 40 min: many ejections (`[eject] player
ship 8162 ejected -> Lifepod hull 113 …`, 8161, 8132…) and **0
`PLAYER_RESCUED`**.

**Every showcase hull can rescue** (Claude dumped `PCore001.igc` via
`StaticCore`): 329 of 447 hulls have `c_habmRescue` (0x02,
`DataHullTypeIGC.HabmRescue`, native `igc.h:727`), incl. **Adv Fighter,
Hvy Interceptor, Adv Scout, Stealth Bomber** (the showcase fleet);
`Lifepod` caps 0x00A4 (`c_habmLifepod`).

**Native:**
- **Contact rescue:** `CshipIGC::HandleCollision` friendly pair
  (`Igc/shipIGC.cpp:~1024-1036`): if one is a lifepod and the other has
  `c_habmRescue` → `RescueShipEvent` (`FedSrv.CPP:13033-13074`: AddRescue,
  PLAYER_RESCUED, place the rescued ship at the nearest friendly restart
  station). Any friendly rescue-capable ship, on contact, instantly.
- **Pickup request:** a pod selecting/commanding a friendly ship issues
  `c_cidPickup` (`WinTrek.cpp:~10650-10690`: "Tell the other person to pick me
  up as well", an individual chat command `c_cidPickup` targeting the pod);
  the rescuer gets the command (and its autopilot/drone AI flies to the pod).
  Check what native drones/AI do with `c_cidPickup` and with nearby friendly
  pods (`Igc`/`FedSrv` drone code: `c_cidPickup` handling).

**Port:** `Sector.ResolveRescueEvents` (`Model/Sector.cs:~159-165, 734-737`)
detects friendly lifepod ↔ `c_habmRescue` pairs within the sum of their spheres →
`Server.PerformRescue`. **It never fires live.** Hypothesis: both ships are
**client-driven peers** (the player and MCP bots are player peers), and the
server's contact/proximity pass skips or mis-positions client-authoritative
ships (stale positions, the collision pass excluded for client ships, a sector
mismatch, or the pod's physics skipped). Find the real cause with a live
repro.

## Scope

1. **Contact rescue works for any friendly rescue-capable ship**, including
   player and bot (client-driven) ships, exactly per native; fix the root cause;
   keep the station-rescue path (P115/P50x) working.
2. **Pickup request (native `c_cidPickup`):** in a pod, targeting a friendly
   ship and pressing the command/accept key (or the P587 right-click default
   command) sends the pickup request, as native; the rescuer gets the
   command shown.
3. **Bots pick up pods (harness behaviour, documented as such if native AI
   doesn't):** an MCP bot within N km of a friendly pod (or that received
   `c_cidPickup`) diverts to it, touches it, rescues, then returns to its
   task; at most one bot per pod; combat priority rules (don't abandon a
   dogfight at close range).
4. P578's rescue messages ("You rescued X." / "X rescued you.") and P583's +5
   rescue score must fire.

## Proof

- Live on the 221 config (local server + MCP 5v5 + the player client, P572
  tooling): the player ejects, flies the pod into a friendly bot → rescued
  instantly (server `PLAYER_RESCUED`, the client message, a screenshot);
  a bot diverts to a player pod unprompted; a pickup request makes a bot come.
  Bot ↔ bot pod rescues counted in a 15v15.
- Tests for the contact detection with client-driven positions, the
  pickup command, the bot diversion rules.
- Report `docs/reports/P590_POD_RESCUE.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
