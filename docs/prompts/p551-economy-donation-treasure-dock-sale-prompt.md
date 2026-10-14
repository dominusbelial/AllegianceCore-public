# P551 — economy parity: donations honour CanBuy (with refund), cash treasure credits the picker like native (fix the client/server mismatch), and dock ore sale needs c_sabmUnload + own side

## Why this task exists

From P534, **re-verified by Claude's spot-check (2026-10-05; wiki
`allegiance-p1-p200-parity-audit`)**:

1. **Bucket donation lacks native's `CanBuy` gate and refund.** Port
   `Economy.cs:~510-571` spends whenever the wallet covers it. Native wraps
   the spend in `if (pside->CanBuy(pbucket))` and otherwise refunds in full
   (`FedSrv.CPP:7150-7208`). Also: native sends the `c_didTeamMoney` bucket
   status to the whole mission (`FedSrv.CPP:7191-7195`); the port sends it
   side-only (`Economy.cs:571`).
2. **Cash treasure goes to the side pot but tells the client the picker got
   it.** Port `Docking.cs:~954` does `side.Money += amount` while
   broadcasting MONEY_CHANGE with `sidTo` = the picker. Native credits the
   autodonate target or the picker's wallet (`FedSrv.CPP:13589-13606`).
3. **Dock ore sale: wrong premise in P534, real gap elsewhere.** Native
   **does** sell ore on dock (`Igc/shipIGC.h:441-449`, `SetStation`, gated on
   **`c_sabmUnload` and own side**). The port's dock sale
   (`ServerHost.cs:~788-805`) lacks that gate (NPCs happen to pick
   `SabmUnload` stations, `Ship.cs:5780`). Add the gate. Also port native's
   NPC teleport-offload leg (`shipIGC.cpp:1969-1990`) only if it's
   small; otherwise list it as a follow-up. Fix the mis-citing port comment
   at `ServerHost.cs:772-775`.

## Explicitly out of scope

- Multi-game mission scoping of money/payday (only with >1 configured game).
- Building cost via the research bucket (P537 item; separate).

## Proof

- Failing-first tests: a donation to a bucket the side can't buy → refused
  and refunded, wallet unchanged; a cash treasure → the picker's wallet (or
  the autodonate target) and MONEY_CHANGE agree with the server state; a ship
  docking at a station without `c_sabmUnload` (or enemy) sells nothing, while
  at an Unload station it sells as before.
- **Live loopback:** the P528b miner economy loop still runs (miners sell,
  sales broadcast), and a player picks up cash treasure (wallet changes on
  the client HUD match the server).
- Report `docs/reports/P551_ECONOMY_PARITY.md`, plus a dated note in P534.

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
