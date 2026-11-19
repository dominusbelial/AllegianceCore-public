# P1056 — Timer-bound flakes: P739 warp-bomb one-ULP bound (+ P660 countdown 14.9 s)

Report name: `docs/reports/TIMER_BOUNDS_P1056.md`.

## Context
Two timer tests fail in the Fast tier on most sweeps and are ledgered many times in `docs/TEST_FLAKES.md` (search
`P739TimerLiveTests` and `P660NextMatchLiveTests`). Unlike most flakes, P739 also fails **when run alone**
(fail/fail/pass, 2/1/0 — see the P1053 and P1052 entries). Read `AGENTS.md`, `GLOSSARY.md`, the P739 and P660
reports in `docs/reports/` (search the P-numbers), and `tests/Allegiance.Core.Tests/P739TimerLiveTests.cs`.
**Write the report FIRST** (skeleton + a status line per item) and keep it current. **Do not end your turn until
items 1–3 are done or the 4-hour cap is reached.**

## Items
1. **P739 warp-bomb deadline (primary).** `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission`
   (`:45-84`) asserts `Assert.InRange(captured - armedAt, delay - .1, delay)` with an inclusive upper bound; failures
   are exactly one ULP over (`5.0000000000000009`, `7.0000000000000009`). Claude's reading: the production deadline is
   `armedAt + delay` in floating point, so `(armedAt + delay) - armedAt` can round to `delay + 1 ULP` depending on the
   clock value — a test-arithmetic artefact, not a late bomb. **Confirm or correct this**: find where `WarpBombTime`
   is set (production file:line), its types (float constant 32 → double?), and the native rule (cite the native
   warp-bomb arm/blast code and its time type). Then fix in the right place:
   - if production is correct, make the assertion exact and deterministic (e.g. compare `captured` with the
     production expression `armedAt + (double)delay` for equality, or bound with an explicit epsilon that is justified
     in the comment) — never widen to hide a real lateness;
   - if production rounds or converts in a way that differs from native (e.g. float precision loss on a large
     mission clock), fix production to match native and say what changes for players (likely nothing visible).
   Add a deterministic unit test that reproduces the one-ULP case without the live server (choose clock values that
   round up; state them), failing before the fix and passing after.
2. **P660 countdown.** `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump`
   (`enabled: False, dilation: "off"`) fails in sweeps with **14.900000000054206 outside [14.94, 15.06]** and passes
   alone. Find why one tick (0.1 s) is lost under load: is the countdown start sampled one tick late relative to the
   value the test reads, or does the test read the clock at a different point than production? Fix the test's
   sampling point if production is correct (cite the native countdown rule), or production if it is wrong. Prove with
   a deterministic reproduction (ordered steps; no busy loops, no synthetic load).
3. **Proof.** Each fixed test 20× alone (fresh processes) with 0 failures, plus 3 runs of the whole
   `P739TimerLiveTests` and `P660NextMatchLiveTests` classes inside a bounded parallel run (`--parallel` as the Fast
   tier uses) — report counts. Update `docs/TEST_FLAKES.md`: mark both families resolved with root cause and commit-free
   description (file:line).

## Gate
Build 0/0 + `tools/build-tools.sh`; new tests 3× alone; one `tools/run-tests.sh --tier fast` (non-task failures 3×
alone + ledger). If production code changes: the server/transport suites touched green with counts, and server
Native AOT publish 0 trim/AOT warnings. Footer: Native intent / Modern approach.

## Not in scope
Other flake families (DeadLatchRespawn, rejoin roster time, etc.) — list any you see, do not fix. No change to game
rules, timings players see, wire layout, tiers, quarantine or other tests' bounds.

---
## Common rules
- Behaviour parity with native; cite native `file:line` for every timing rule. Lane environment: MS-A2 Debian 13
  container, own clone; your worktree is the current directory (own branch). SDK `~/.dotnet/dotnet`. Artwork
  `/srv/corpus/Artwork`, native reference `/srv/corpus/Allegiance/src/` (read-only; `local.env`). **Do not leave the
  brief file in the change set** (check `git status`; do not commit it).
- **Never `git stash`**, never `pkill -f`/`pgrep -f`, **no synthetic CPU load**, scratch in `.scratch/p1056/`, do not
  commit or push, do not contact the rig, mini, Mac, VPS or any public host. Public-repo hygiene: no secrets, no
  personal paths/IPs/hostnames in committed files (evidence paths as `<lane evidence dir>`, worktree as
  `<lane worktree>`). Every process you start must end before you finish — stop by exact PID. Build-hang workaround:
  `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`.
- **Time:** 4 hours max; report first and kept current. A status report is not a stopping point.
- **Report:** root cause per test with file:line, native citation, what changed, reproduction tests, the 20× alone
  and parallel counts, fast-tier results, anything unfinished.
