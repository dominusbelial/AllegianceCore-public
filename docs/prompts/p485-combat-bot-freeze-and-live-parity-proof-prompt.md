# P485 — fresh combat bots freeze on normal maps; then the first live proof of capture, base alerts and elimination

## Why this task exists

P474 (`d4bbe9ad`) built `run_scenario` with `bomber_run` and
`capture_attempt`, but neither could complete live
(`docs/reports/P474_MCP_PARITY_SCENARIOS.md` §3). Two blockers:

1. **Normal maps** (`m2xspiderw`, `XXV_simple`, `debugMode=false`): a freshly
   seated opposing-side combat bot **freezes in place**. Its live position
   never changes from spawn across a 36 s+ window. This is the P429-documented
   "combat bots freeze" behaviour (`docs/reports/P429_MCP_COMBAT_BEHAVIOR.md`,
   live-environment limitations).
2. **The second-hop aleph parking**, fixed by **P484**. Check that P484's
   commit exists (`git log --oneline | grep P484`). If it doesn't, **stop and
   report**.

As a result, **P450** (capture needs downed shields), **P451** (base-under-attack
alerts) and **P455/P455b** (elimination) have wire-level tests but **no
live end-to-end proof**.

## Scope

1. **Root-cause the freeze with evidence.** For a frozen bot, capture
   whether it was launched (undocked) and has a real ship in a cluster,
   which controls it is sending, and what the server does with them. A
   wrong `ControlData`, a seat or launch race, a docked-but-thinks-launched
   state, or anything else is possible. Fix it at the root. If it's
   server-side, cite native; if it affects humans, say so.
2. **The live proof run.** With P473's harness grant on, run `bomber_run` and
   `capture_attempt` live, on both a normal map and the debug map. Paste the
   structured outcomes into the report. The acceptance criteria:
   - `capture_attempt` shows **refusal at full shields, then capture
     after the escort downs them**, with the observed side change (P450);
   - `bomber_run` shows a real shield and hull decline and the defending
     side's **under-attack alert** (P451);
   - if the bombers kill the defender's last restart station: the
     `SIDE_INACTIVE` / "is no more." / `GAME_OVER` sequence (P455/P455b).
3. If the live run exposes a real bug in P450/P451/P455, report it with
   evidence and a named follow-up, and **don't** fix it here. The purpose is
   proof.

## Explicitly out of scope

- New presets (minefield/elimination-only are P474b, still a named
  follow-up).
- The aleph parking (P484).
- The MCP commander tools (P486).

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471's
  sequential runner), 3 consecutive runs. Cite `docs/TEST_FLAKES.md` for any
  remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P485_LIVE_PARITY_PROOF.md`.
