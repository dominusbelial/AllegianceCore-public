# P472 — fix the top remaining flaky tests from the P471 ledger

## Why this task exists

P471 built `docs/TEST_FLAKES.md`, a measured per-test failure ledger, and
fixed the systemic cause (live-socket test parallelism) plus the two
pristine-tree failures. This task fixes the **individual** tests that are
still flaky after that. Check that P471's commit and
`docs/TEST_FLAKES.md` exist (`git log --oneline | grep P471`). If they
don't, **stop and report**.

The usual cause in this codebase is fixed waits: 209 test files use
`Thread.Sleep`/`Task.Delay`, and those turn into failures under CPU
contention. Candidates from earlier reports include
`JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
`LiveMoneyTests.Board_refund…`,
`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
`LiveTurretGunnerBindingTests.Board_binds_gunner…` and
`LiveTeamLobbyTests.Unready_broadcasts_team_ready_false`. **Use the
ledger's post-P471 ranking, not this list.**

## Scope

1. Take the top **5** tests by post-P471 failure rate, or fewer if fewer
   remain. For each one:
   - Find the actual race: read the test and the server path it waits on.
   - Replace fixed sleeps with a **condition wait with a generous timeout**
     that polls the real observable the assertion needs. Reuse an existing
     wait/poll helper if the test projects have one, and search before
     writing a new one. If none exists, add one small shared helper.
   - If the race is in **production code** rather than the test (for
     example a message ordering the test correctly expects but the server
     only sometimes delivers), stop on that test. Document it in the
     ledger as a real bug with a named follow-up, and don't paper over it
     in the test.
2. Update `docs/TEST_FLAKES.md`: mark each fixed test with its cause and
   fix, and leave everything else ranked.

## Explicitly out of scope

- The xunit collection/parallelism setup (P471). Don't undo or re-tune it.
- Tests outside the top 5.
- Any `[Skip]`, loosening an assertion's meaning, or deleting a test.

## Verification

- Each fixed test passes 20/20 in a loop run in isolation
  (`--filter`, repeated), **and** is clean across 3 consecutive full-suite
  runs. Put the before and after rates in the ledger.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The updated `docs/TEST_FLAKES.md`, plus the report
   `docs/reports/P472_FLAKY_TEST_FIXES.md`. The report covers each test's
   race, its fix, and any production-race findings.
