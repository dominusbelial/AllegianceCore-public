# P471 — test-suite flake ledger + the systemic cause

## Why this task exists

The full suite is now noisy enough that a real regression can hide as "a
known flake". This tasks exists because P439's report
(`docs/reports/P439_MISSION_OVER_ROTATION.md:~224-240`) names about 15
different "pre-existing live-wire timing flakes" across its runs, and two of
them fail even on the pristine tree. Counting test-name mentions across
`docs/reports/*.md` (107 reports mention flakes) gives the recurring
offenders:

| Mentions | Test |
|---|---|
| 10 | `JoinFlowLiveTests.EntityTable_TracksARealTickingServer` |
| 8 | `LiveMoneyTests.Board_refund…` |
| 5 | `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` (fails on pristine tree per P439) |
| 5 | `LiveWinConditionTests.Board_refused_once_mission_over` (fails on pristine tree per P439) |
| 4+2 | `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` |
| 4+3 | `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live` |
| 4 | `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` |
| 4 | `GameOverPlayersWireTests` / `ConquestTerritoryLiveTests.Conquest_win…` (green alone, fails under full-suite load) |
| 3 | `FullSessionKillIntegrationTests.FullSession_ThreePeers…` |

What the structure suggests, which you must verify rather than assume:

- **No xunit parallelism configuration exists anywhere.** There's no
  `xunit.runner.json`, `CollectionBehavior` or `maxParallelThreads` in
  `tests/`. By default xunit runs test classes in parallel, so dozens of
  loopback-socket live servers tick at once. "Green in isolation, fails
  under full-suite socket load" is the classic signature of that.
- **209 test files use `Thread.Sleep`/`Task.Delay`.** Fixed waits turn into
  failures under CPU contention.

## Scope

1. **Measure first.** Time one full `/home/domingo/.dotnet/dotnet test` run.
   Budget the remaining runs to fit the session. Aim for at least 3
   full-suite runs, and fewer only if the timing forces it, saying so.
   Record per-test failure counts across the runs, and the failures that
   also reproduce on a single isolated run (`--filter`). Write the ledger
   to `docs/TEST_FLAKES.md` with these columns: test | failures/runs |
   fails-in-isolation? | suspected cause | status. This file becomes the
   **single source of truth**. From now on, reports cite it instead of
   free-text flake lists.
2. **The two pristine-tree failures** (`LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`,
   `Board_refused_once_mission_over`). If they fail deterministically,
   they are **real bugs or stale tests, not flakes**. P439's mission
   rotation is the likely suspect for `Board_refused_once_mission_over`,
   because a mission is no longer permanently `Over`. Find out which. Fix
   the test if the behaviour change is intended and native-correct, citing
   native. Fix the code if the test is right. Either way, remove them from
   the "flake" category.
3. **Systemic fix.** Based on step 1's evidence, put the live-socket
   test classes (the ones that start a real `Server` and connect over
   loopback) into a shared xunit collection that doesn't run in parallel,
   or set a bounded `maxParallelThreads`. Use whichever measurably removes
   the load-induced class of failures at an acceptable suite time. Report
   the suite wall-time before and after. Pure unit tests keep running in
   parallel.
4. Re-run the full suite at least 3 times after the change and update
   the ledger with the before and after numbers.

## Explicitly out of scope

- Rewriting individual tests' fixed sleeps into condition waits. That is
  **P472**, the next task, and it works from this task's ledger. The only
  exception is step 2's two tests.
- Changing production behaviour, except as step 2 requires, with native
  citations.
- Deleting or `[Skip]`-ing any test to get green. If a test has to be
  quarantined, it must be listed in the ledger with its reason and a named
  follow-up. Prefer not to quarantine anything.

## Verification

- `docs/TEST_FLAKES.md` exists with real before and after counts from real
  runs.
- The step-2 tests pass deterministically, 3/3, and the reason is
  documented.
- After the change: 3 consecutive full-suite runs, with every remaining
  failure listed in the ledger. Report the suite wall-time before and after.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. `docs/TEST_FLAKES.md` (the ledger) and the report
   `docs/reports/P471_TEST_FLAKE_LEDGER.md`. The report covers the
   methodology, the step-2 finding, the systemic change with its measured
   effect, and which tests P472 should take on.
