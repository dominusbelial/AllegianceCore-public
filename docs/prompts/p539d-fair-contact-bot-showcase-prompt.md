# P539d — make bot fights fair at contact: find why one side arrives late and staggered (and fix it or stage), no ramming on arrival, count collision deaths, add an arena mode, then the full tech-level showcase bar, on the real 221 configuration

## Why this task exists

**P539c** (`39d85b7a`, `docs/reports/P539c_BOT_ACQUISITION_AND_TRANSIT.md`)
established that warp transit and cross-side acquisition **work for both
sides** on the current code. Good: that's settled. But its conclusion ("a
high-variance melee, not side-dependent") contradicts its own data. **Side A
out-fired side B in all 6 runs:**

| Run | Shots A : B |
|---|---|
| equal-tech, 240 s | 576 : 158 |
| probe on side 0, 200 s | 3214 : 217 |
| probe on side 1, 200 s | 3210 : 1519 |
| tech 2 vs 0, 150 s | **4000 : 11** |
| tech 2 vs 0, 150 s | 2014 : 499 |

Its own mechanism (§3) is a **side difference it didn't trace**: side-B bots
"arrive individually into side-0's settled position, ram and are destroyed".
**Why does side B arrive later and one at a time?** Also:
- **Collision deaths aren't counted as kills** (one run: 7 ejections vs 3
  kills).
- The showcase bar was only partly re-run: 2 of 6 runs, at 150 s instead
  of 240 s, with no reversal.
- **P539c tested a different map** (in-process `m2xspiderw` debug: homes
  Omega 1053 / Plato 1060, each one warp from Alderan 1052). **The showcase
  server (221) uses a 13-sector map: side 0 home Ganymede (1050), side 1
  home Organia**, with `spawn_scenario {size, target_sector:"Alderan"}`.
  P540b noted Ganymede is two warps from Alderan. **Route lengths per side
  may differ there.** Measure on 221's configuration: the same server
  config (`~/allegiance-deploy-compose.server.json.local`, 20 Hz, civIds
  [25,3], testUnlock) on a local server, not the debug map.

**Bot loadout today (P539b, native preferred):** `PW Gat Gun 1`, no shield
(`Sm Shield 1` doesn't mount the Adv Scout in native either). 225 HP glass
cannons, so first contact decides a lot.

## Scope (`tools/allegiance-mcp`, plus MCP scenario tools)

1. **Measure arrival per side on 221's config:** for each bot, the launch
   time, each warp hop time (server `[warp] … crossed` lines), and the
   arrival time in the rendezvous sector. Report hops per side, the first and
   last arrival, and the spread. Find the cause of any lateness or
   staggering (route length, launch order or rate, spawn geometry, per-bot
   pathing) with file:line.
2. **Fair contact:**
   - **Staging (default for `spawn_scenario` with a target sector):** each
     side gathers at a staging point (e.g. inside the last sector before
     the rendezvous, or just past the arrival aleph) until all its living
     bots are there, or a timeout. Then the whole side moves in together.
     Both sides engage within a few seconds of each other. Cite native drone
     wing/formation behaviour if any exists; if not, document staging as a
     harness choice.
   - **No ramming on arrival:** the approach to enemies uses the stand-off
     ring (P539) **during** the approach too, plus cross-side collision
     avoidance (steer around a ship in the path, as the friendly-fire gate
     does). Measure collisions before and after.
3. **Count collision eliminations:** per side, report kills by gunfire
   (`[combat] … killed by ship X`), collision/other deaths (ejects without a
   shooter) and total deaths, so outcomes add up.
4. **Arena mode for showcases:** an option, e.g. `spawn_scenario
   {…, arena: true}`, that launches both sides **directly into the target
   sector at mirrored positions** (equal distance from the centre, facing
   each other) via the existing launch/relocation path. Travel then can't
   decide outcomes. Report it in the tool output. If the server can't launch
   into a non-home sector, say how it's done (e.g. launch at home, then a
   supported relocation) and cite it.
5. Keep the P539c `DiagnoseWorlds()` diagnostic.

## Explicitly out of scope

- Server damage, collision and transit code (proven symmetric in P539c),
  unless measurement shows a real server asymmetry. Then prove it with
  native code before touching it.
- Loadout and tech mechanics (P539b), except using them.
- Client rendering.

## Verification

- **Failing-first unit tests:** staging holds until the side is assembled
  (or the timeout); the stand-off-on-approach goal never targets inside the
  ring; collision deaths are counted; arena positions are mirrored and
  equidistant.
- **Arrival proof (221 config, travel mode with staging, 5v5):**
  per-side arrival table before and after; both sides' first contact within
  a few seconds; collisions before vs after.
- **Symmetry (equal tech, 5v5, 240 s):** in **both** travel-with-staging and
  arena modes, real shots per side within **1.5×**. Then swap side
  assignment and show the same.
- **The full showcase bar, arena mode AND travel-with-staging:** 3 × `tech 2
  vs 0` and 3 × `tech 0 vs 2`, 5v5, **240 s each**, plus 2 equal-tech runs.
  Report gunfire kills, collision deaths and total deaths per side for every
  run, cross-checked against `[combat]` lines. **The bar: the higher-tech
  side wins every run, and the reversal flips it,** at least in arena mode.
  If travel mode still doesn't meet it, show why with numbers.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.
  Don't re-run a failing suite until it comes up clean;** report each run as
  it happened. **Don't shorten windows or skip runs.** If time runs short,
  say which runs are missing.
- ⚠️ **Commit as soon as the code, tests and proofs are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file**
  (`git status --untracked-files=all`). Confirm with `git log -1` and a
  clean `git status`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P539d_FAIR_CONTACT_SHOWCASE.md`: the arrival
   analysis, fixes, the collision accounting, arena mode, the symmetry
   tables and the full showcase table. Add a dated note to P539c's report
   correcting its "not side-dependent" conclusion.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
