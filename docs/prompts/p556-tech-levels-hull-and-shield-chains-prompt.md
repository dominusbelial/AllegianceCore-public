# P556 — tech levels that change survivability, the native way: tech N advances the HULL along `successorHullID` and the shield (now mountable after P555) along its successor chain, not just the gun. Then the arena showcase bar on 221-style networked play

## Why this task exists

**Domingo (2026-10-05):** "yes scope the hull and shield tech follow-up", after
the networked arena series on 221 (P554 + Claude's prefix allow-list fix,
`7a49a9f4` on branch `fix/admin-allow-prefix`), Rigel 1059, 5v5, 240 s:

| run | tech A:B | gunfire kills A:B | shots A:B |
|---|---|---|---|
| 1 | 4:0 | 0:5 | 607:1378 |
| 2 | 4:0 | 0:4 | 698:1839 |
| 3 | 4:0 | 5:1 | 1436:829 |
| 4 | 0:4 | 5:0 | 1655:1365 |

(runs 5–6 pending at scoping time; they're in Claude's log.) **Tech 4
(`PW Gat Gun 3`, 50 raw dps, 660 range) won 1 of 4 against tech 0
(`PW Gat Gun 1`, 32 dps, 600 range); no side bias; every run a sweep.** With
225-HP **unshielded** Adv Scouts (because of the P555 mask bug), first contact
decides everything, and a better gun can't pay off.

**Native progression is hulls and parts together.** Each hull has a
`successorHullID` (`DataHullTypeIGC.SuccessorHullId`; e.g. Adv Scout 285 →
Hvy Scout 85), and parts chain by `successorPartID` (shields: Sm Shield 1 →
2 → 3 = 150/2.0, 180/3.0, 225/3.75 strength/regen; Med 1→2→3 300/400/500;
guns Gat 1→2→3).

**Depends on P555** (hull `pmEquipment` decode fix): until it lands, Sm Shields
don't mount on scouts. Build on P555's master.

## Scope (`tools/allegiance-mcp`, P539b tech-level code)

1. **Tech level N, redefined native-style** (document it in the tool help):
   - **Hull:** advance the scenario hull along `SuccessorHullId` by
     `floor(N/2)` steps (stop at the chain end), e.g. tech 2+ flies the
     successor hull. Cite native's hull successor semantics (how
     `successorHullID` is used for upgrades in `Igc`/`FedSrv`, e.g.
     replacing obsolete hulls when a development completes).
   - **Parts:** start from the **resulting hull's** native
     `PreferredPartsTypes`, then advance each part `N` steps along
     `SuccessorPartId`, keeping only what `CanMountPart` (P555-fixed) allows
     on that hull. That includes the shield chain.
   - Tool output per side: hull name, HP, shield name + strength/regen, gun,
     and the full resolved loadout.
2. Keep `tech_a`/`tech_b` on `spawn_scenario` and `spawn_bot`; tech 0 is
   exactly the hull's native preferred loadout (now with `Sm Shield 1`).
3. **Survivability metric:** per run, the mean time-to-kill per side
   (first hit on a victim → its death) from server logs, so we can see the
   tech effect even when kills are few.

## Explicitly out of scope

- Arena/relocation mechanics (P554), dodge (P553), the mask decode (P555).
- Any change to native combat math.

## Proof

- Failing-first unit tests: tech 0 = native preferred loadout (including
  `Sm Shield 1` on Adv Scout); tech 2 = successor hull + parts advanced 2
  steps (mountable only); tech 4 = …; chain ends are respected.
- **Networked arena bar** (separate-process server over TCP + standalone MCP,
  `adminCheats` enabled with `allowNames: ["mcp-*"]`. **Note: the prefix
  match is on branch `fix/admin-allow-prefix` `7a49a9f4`; if it isn't on
  master yet, cherry-pick it first and say so**): Rigel, 5v5, 240 s,
  3 × tech 4 vs 0 and 3 × tech 0 vs 4, plus 2 × tech 2 vs 0. Report per run:
  hulls and loadouts, gunfire kills, deaths, shots, time-to-kill per side.
  **Bar: the higher-tech side wins ≥ 5 of 6 at tech 4 vs 0 (both
  directions).**
- Report `docs/reports/P556_TECH_HULL_SHIELD.md`.

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
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
