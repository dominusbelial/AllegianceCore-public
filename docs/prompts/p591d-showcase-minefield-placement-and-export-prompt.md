# P591d — showcase minefields are laid but in the wrong place and invisible: they land at Alderan's Cirrus aleph (far from the fight) and their EXPORT goes to "0 same-side peer(s)"

## Why this task exists

Live on 221 @ `0f67a59d` (P591b's re-entry guard), showcase 2×15v15 with
`minefields {side: both}` (2026-10-09 20:52). The layers now route correctly
(2 crossings each: 8102 Ganymede→Rigel→Alderan, 8104 Organia→Gaia→Alderan)
and **lay**, then are consumed (native):
```
[mine] ship 8102 deployed -> EXPORT(26)/OT_mine 1 pos (2168.7, -2769.5, 338.2) life 600 -> 0 same-side peer(s)
[mine] ship 8104 deployed -> EXPORT(26)/OT_mine 1 pos (2573.8, -2797.6, 299.0) life 600 -> 0 same-side peer(s)
[combat] ship 8102 killed by (none) …   (consumed after laying)
```
Two problems:
1. **Placement:** both fields sit at Alderan's **Cirrus aleph** (warp 2150 at
   (2120, −2800, 313)), not at the alephs where the showcase fights
   happen: **Rigel aleph** (2187 at (−2240, 5560, 23)) and **Juniper aleph**
   (2183 at (−5400, 2360, 90)). **0 mine hits** in the run. The showcase
   should place each side's field across the **enemy's arrival aleph** (or the
   contested aleph): find where the build order's buoy/target comes from
   (P560 `spawn_scenario` minefields → `LayerBuildOrder`/ArmLayerBuild → buoy
   position) and why it picked Cirrus.
2. **Export audience:** the minefield EXPORT went to **0 same-side peers**,
   while bot-dispensed mines (`[fireexpendable] … fired mine 12 … to 33
   same-side peer(s)`) reach everyone. So no client renders these fields
   (not even the layer's side). Native: how is a laid minefield announced
   (`FedSrv` `LayExpendable` → create mine → which audience: the side, and
   enemies once spotted?) Port the audience rule; likely the drone's owning
   peer/side lookup is empty for an NPC layer.

## Scope

Fix both natively (placement per the showcase's intent: at the
enemy/contested arrival aleph within the target sector, with the native buoy
semantics; export audience per native). Re-check P452 damage and P574
per-hit pops with ships actually crossing the fields.

## Proof

A 15v15 showcase on the 221 config: both fields at the right alephs, EXPORT to
N>0 peers, the real window showing the field (P563 visuals) and violet pops as
ships cross, mine hits > 0 in the server log. Tests for the placement choice
and the export audience.
Report `docs/reports/P591d_SHOWCASE_MINEFIELD_PLACEMENT_EXPORT.md`.

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
