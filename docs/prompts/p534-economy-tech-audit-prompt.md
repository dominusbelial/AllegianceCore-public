# P534 — P1–P200 parity pass, part 4: economy & tech (P1–P200), excluding He3 mining constants

## Why this task exists

Domingo (2026-10-04): "another round/pass parity check, this time focused on
all the initial features we created … the first phase of this project was
kind of messy because I did not have a stable spark setup." The first ~200
P-tasks were built while the delegation setup kept crashing, so shortcuts
and half-verified values are most likely to live there. Today's live test
showed exactly that: the He3 economy ran on self-labelled test-convenience
constants (100× mining rate, 16× too-cheap ore) that a feature-presence
audit had missed.

This is one of eight read-only audit tasks (P531–P538). Their findings
become small fix P-tasks afterwards.

## Your slice

Input: `docs/audits/p1-p200/economy.txt` (36 commits).

Focus: payday amount and cadence (the live server shows 525/600 per ~minute; verify against native), starting money, purchase prices and refunds, tech/development costs, times and prerequisites, investment, treasure values, money-transfer rules. **Skip** the three He3 mining constants (P528 fixes them), but do audit the rest of the mining/offload flow.

## Method (read-only audit; this is research, not a fix)

- **Native reference:** `~/Development/Allegiance/src/` (`Inc/Messages.h`,
  `clintlib/`, `FedSrv/`, `Igc/`, `WinTrek/`, `Wopr/`). `Igc/igc.h` is
  ISO-8859 encoded. Search it with `LC_ALL=C grep -a`, or plain grep returns
  nothing.
- **Port:** `~/Development/AllegianceCore/src/`. For each commit in your
  list, use `git show --stat <hash>` to find the code it produced, then read
  the **current** code. Later tasks may have changed it.
- **Navigation aid (optional):** the combined native+port graphify graph,
  `graphify query "<q>" --graph ~/Development/allegiance-combined-graph/graphify-out/2026-09-26/graph.json`
  (also `explain "<node>"` and `path "A" "B"`). It dates from 2026-09-26, so
  its line numbers are stale. **Every claim must be verified by reading the
  source**, never the graph alone.
- **Compare values, not just presence.** The pass that preceded this one
  (2026-09-29) checked whether features *exist* and missed that He3 mining
  ran on test-convenience constants (rate ×100, capacity 150 vs core 90,
  price 5 vs core 80; fixed by P528). For each item, check **constants,
  formulas, units, timings/cadences, gates/conditions, edge cases, and
  where a value comes from** (the loaded core's data/FloatConstants vs a
  literal).
- **Known items:** the 2026-09-29 parity raw reports in
  `~/Documents/Domingo-Llm-Wiki/raw/2026-09-29-parity-{A-flight,B-weapons,C-economy,D-special-combat,E-team-meta}.md`
  list gaps already found. Don't re-report one as new. If you touch one,
  record whether it's now FIXED or still OPEN (later tasks fixed many).
- **Don't modify any production or test code.** Writing your report is
  the only change. You may run read-only commands and the existing test
  suites if useful.
- Budget your time: cover every commit in your list at least briefly, and
  spend the depth on the items most likely to diverge.

## Output

Write `docs/reports/P534_ECONOMY_AUDIT.md` with:

1. A **findings table**, one row per item:
   `item | native file:line | port file:line | verdict | severity | proposed fix (one line)`.
   - verdict ∈ **MATCH**, **DIVERGES** (value/formula/behaviour differs),
     **STAND-IN** (labelled or obvious placeholder where native data or a
     formula exists), **MISSING** (native behaviour absent),
     **UNPORTABLE** (genuinely can't apply, say why).
   - severity ∈ **HIGH** (players feel it: balance, feel, correctness),
     **MED**, **LOW** (cosmetic or log-only).
2. A short **"top fixes"** list, ranked, each sized as a candidate P-task
   (smallest vertical slice).
3. Coverage: which commits you checked and how deeply.

Report only what you verified in source; quote file:line for both sides of
every non-MATCH row. **Commit the report on `master`** (not pushed) before
you finish, and confirm with `git log -1` and `git branch --show-current`.
