# P539c — MCP bots: one side doesn't acquire enemies and parks at alephs. Find the side-dependent root cause (likely a port bug, never proven to be a "server reality"), fix it, then re-run the tech-level showcase bar

## Why this task exists

**P539b** (`298f4d9b`, report `docs/reports/P539b_MCP_BOT_REAL_HITS.md` §6b)
got real kills (5, 2, 3) and a corrected metric, but **the tech-level showcase
bar failed**: in both `tech 0 vs tech 2` runs the **lower**-tech side won
(2-0, 3-0), and an equal-tech run had 0 kills. Its own table shows the real
pattern: **one side consistently under-engages**, firing 3–13× fewer real
shots (`shotsB` 854 / 240 / 1,030 vs `shotsA` ~2,850–3,200) **regardless of
tech**. P539b attributed it to two previously documented issues:

1. **One-sided cross-side target acquisition**
   (`docs/reports/P429_MCP_COMBAT_BEHAVIOR.md:150-160, 198-201`). Two
   co-located bots, one per side: only one populated `target`. The other's
   world "tagged the just-exported cross-side ship with a sector that failed
   the `ship.SectorId == myCluster` co-location gate", so it loitered.
2. **Park at the aleph** (`P429…md:191-197`,
   `docs/reports/P474_MCP_PARITY_SCENARIOS.md:125-152`). Bots on **the
   probe's side** fly to the warp and hover **at an identical coordinate**
   whatever the approach parameters, while the opposing side transits
   cleanly. In P474 the second hop (Alderan→Omega) always parked
   (`warpFound=True`, but the bot stayed in 1052).

**Both were labelled "server realities the port inherits" and never checked
against native.** Native has no side-dependent warp transit or visibility of
this kind: an aleph transits any ship that enters `warp.radius + ship.radius`,
and a ship in your sector is visible to you per the normal
detection/visibility rules (eye/signature), not depending on whether you're
side 0 or 1. A **side-dependent** result points at a port bug in **what one
side's clients are told** (the ship's sector on export, the warp records or
positions that side receives, the probe client's team leadership/side
seating), or at how the MCP harness seats its sides. **Treat it as a bug to
find, not a limitation to document.**

**Related facts already established:**
- MCP warp routing resolves the partner warp correctly
  (`tools/allegiance-mcp/BotManager.cs:1142-1155` `TryBuildWarpDestMap`;
  P540c audit).
- The **P496 warp-approach law** (`BotManager.cs:1184-1200`,
  `WarpApproachParams`, used at `:1687-1688` `CombatFlyTo`) aims at the
  transit sphere. P540b found the same "parks just outside the trigger"
  symptom for the player's autopilot and fixed it with
  `AutopilotSteering.Pursue` (fly **through** the mouth at full throttle).
  Compare the two laws.
- An "identical parking coordinate regardless of approach params" means the
  bot is steering to **a point it believes is the warp**. Check whether that
  side's world holds the right warp position/sector for that hop, or a
  stale/other-sector record.
- The MCP "probe" client (observer/leader) has been seen **taking team
  leadership** (an open follow-up). Check whether the probe's side seating
  changes what that side's bot clients receive.

**Native references to cite:** server transit (`Igc/warpIGC.*` collision →
the ship changes cluster; `FedSrv` cluster-change/`SET_CLUSTER` sends),
what a client receives about ships in its sector (`FedSrv` ship
export/visibility, the side `ttbm`/eye rules) and the client's cluster gate
for targeting (`clintlib`/`Igc` `GetCluster()` comparisons).

## Scope

1. **Reproduce per side, deterministically** (local server, 20 Hz):
   - **(a) Acquisition:** one bot per side, co-located, no target. Log each
     bot's world view of the other ship: `SectorId`, position, last
     update tick. Swap the sides and seat order, with and without the probe
     client.
   - **(b) Transit:** one bot per side flying a **2-hop** route. Log per
     tick: the bot's own cluster, the target warp's id/cluster/position/radius
     **as that bot's world holds it**, the server's view of the same warp,
     and the distance to the transit sphere.
2. **Root-cause each** to file:line, port vs native. Name the exact
   difference: the wrong sector stamped on an export, the wrong/stale warp
   record, the probe's seating, the steering law, etc. If something really
   is native behaviour, prove it with native code, not an assertion.
3. **Fix it at the root** (server, ClientCore or MCP, wherever the bug is),
   with failing-first tests. Reuse P540b's `AutopilotSteering.Pursue`
   fly-through for warp legs if the MCP law is part of the problem. Don't
   write a third steering law.
4. **Correct the record:** add dated correction notes to the P429, P474 and
   P539b reports where they called these "server realities". Don't
   silently rewrite them.

## Explicitly out of scope

- New bot tactics beyond what the fix needs.
- Damage, loadout and tech mechanics (done in P539b).
- Client rendering.

## Verification

- **Failing-first tests** for each root cause (e.g. a side-0 client gets the
  correct `SectorId` for a co-located side-1 ship; a bot's warp leg toward a
  known warp crosses the transit sphere in a closed-loop sim).
- **Symmetry proof** (local, 5v5, 240 s, 20 Hz, equal tech, both sides warp
  to a **neutral rendezvous 2 hops away**): **every bot on both sides
  completes both hops**, quoting server transit lines, and **real shots per
  side within 1.5× of each other**. Then **swap the side assignment** and
  show the same.
- **Re-run P539b's showcase bar:** 3 repeats each of `tech_a=2 vs tech_b=0`
  and `tech_a=0 vs tech_b=2` (5v5, 240 s, neutral 2-hop rendezvous), plus an
  equal-tech run. Report kills and deaths per side for each run,
  cross-checked against `[combat] … killed by …`. The bar is still **the
  higher-tech side wins every repeat, and the reversal flips it**. If it
  doesn't hold, show what still differs between the sides, with numbers.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.
  Don't re-run a failing suite until it comes up clean;** report each run
  as it happened.
- ⚠️ **Commit as soon as the code, tests and proofs are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file**
  (`git status --untracked-files=all`). Confirm with `git log -1` and a
  clean `git status`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P539c_BOT_ACQUISITION_AND_TRANSIT.md`: per-side
   repro logs, root causes with file:line plus native citations, fixes,
   tests, the symmetry proof, the showcase table, and the correction notes.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
