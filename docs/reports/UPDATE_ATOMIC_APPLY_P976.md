# P976 — Update apply is failure-atomic (P971-05)

Base: `a6d8bf47` (master at dispatch; this worktree merges `49975d5a`'s P972 report-only
change on top, no code impact). Finding: `docs/reports/CLIENT_REVIEW_P971.md:70-78`
(P971-05, "Update application is not failure-atomic").

**Verdict: fixed.** The apply now verifies the whole staged set before the first
mutation, replaces/deletes through a backed-up transaction with an on-disk recovery
journal, restores content *and* Unix modes on any error, writes `installed.json` only
at the commit point, and discards a pending update after 3 failed attempts. Every
fault boundary and every crash window has a test; on the base those tests fail with the
exact mixed tree the reviewer reported.

## 1. Before: the reviewer's reproduction (base behaviour)

The base applier (`git show a6d8bf47:src/Allegiance.Update/UpdateApplier.cs`, the loop at
`:145-159`, the catch at `:176-181`) hashed and copied staged files one at a time and had
no rollback; the catch only logged "keeping the current build" and left the marker, so
`ClientUpdateBootstrap` re-applied on every boot (`ClientUpdateBootstrap.cs:47-50`).

Two independent base reproductions were captured (scratch harness, public API only,
app dir under the system temp dir; raw output in `~/allegiance-evidence/P976/`):

| # | Scenario | Result on base |
|---|---|---|
| A | staged set has valid `game.dll`, missing `second.dll`; `old.txt` queued for removal | `applied=False`, tree has `game.dll=new-game`, `second.dll=old-second`, `old.txt` still present → **mixed tree** |
| B | obsolete `locked/old.dat` cannot be deleted (read-only directory) | `applied=False`, `game.dll=new-game`, `old.txt` not removed → **mixed tree** ("current build kept" is a lie) |
| C | journal/backup left by a crash, tree half-mutated | journal ignored (`journal-still-on-disk=True`), no recovery → mixed tree |
| D | staged file missing, apply retried | pending marker still present after 5 failed attempts → **retried forever** |

Raw logs: `~/allegiance-evidence/P976/base-repro.txt` (base library) and
`fixed-repro.txt` (the same harness against the fixed library: A/B/C/D all become
`mixed tree = False`, journal gone, `pending after 5 failed attempts = False`).

### Failing-first tests (same file, before the fix)

`tests/Allegiance.Update.Tests/UpdateApplyAtomicityTests.cs` was written first and run on
the untouched base library:

```
Failed: 4, Passed: 33, Skipped: 0, Total: 37
  Apply_with_a_missing_staged_file_changes_nothing            (game.dll new, second.dll old)
  Apply_with_a_tampered_staged_file_changes_nothing           (game.dll new, second.dll old)
  A_journal_left_by_a_crash_is_rolled_back_on_the_next_boot   (journal ignored)
  A_pending_update_that_keeps_failing_is_discarded_after_the_cap (marker still there)
```

TRX: `~/allegiance-evidence/P976/base-tests.trx`. The boundary-injection tests added
with the fix compile only against the new seam (there is no way to fail the base applier
at "after k copies" from outside), so the base evidence for those boundaries is the
harness above (B = delete failure) plus A (copy failure), which is the same failure class
the seam reproduces deterministically.

## 2. Design

### 2.1 Transaction phases

```
verify whole staged set        (no mutation)
  → journal "backing-up"       (journal.json written, app tree untouched)
  → backups into autoupdate/apply-backup/<n>   (content + recorded Unix mode)
  → journal "applying"         (first point at which the app tree may change)
  → per file: <.update-new> temp + atomic rename; then obsolete deletes
  → installed.json written     (the only place it ever changes)
  → journal "committed"
  → clear marker, journal, backups, staging
```

The `backing-up` phase exists so a crash *during* the backups can never restore a
half-copied backup over an untouched file: recovery simply abandons the attempt.

### 2.2 Journal format (`<appRoot>/autoupdate/apply-journal.json`)

Small, camelCase, source-generated (AOT-safe, `UpdateJsonContext`):

```json
{
  "version": "2.0.0",
  "phase": "backing-up | applying | committed",
  "restore": [ { "path": "game.dll", "backup": "apply-backup/0", "unixMode": "755" } ],
  "created": [ "newdir/created.dll" ],
  "createdDirectories": [ "newdir" ]
}
```

* `restore` — files that exist and must be put back on rollback: replaced files,
  files queued for removal, **and `autoupdate/installed.json`** (so a crash between the
  installed.json write and the commit marker restores the old version too). `backup` is
  relative to the state directory, `unixMode` is the pre-apply mode.
* `created` — files that did not exist before; deleted on rollback.
* `createdDirectories` — directories created by the apply (shallow first); removed on
  rollback when empty (never a directory that gained player files).
* Every path is re-resolved with `UpdateApplyPaths.Resolve`, which refuses anything that
  normalizes outside the app root (a hand-edited journal cannot write elsewhere).

### 2.3 Crash cases and what the next boot does

| Crash point | Journal on disk | Next boot |
|---|---|---|
| during pre-verification | none | nothing to do (no mutation happened) |
| after journal write, during backups | `backing-up` | abandon: delete journal + backups, count the attempt, keep staging for the retry; **no file touched** |
| after any copy / delete, before installed.json | `applying` | restore every backup (content + mode), delete created files/dirs + temps, drop journal, count the attempt, keep the marker |
| after installed.json, before the commit marker | `applying` | same as above — `installed.json` is in `restore`, so the version goes back to the old one |
| after the commit marker | `committed` | finish forward: keep the new tree, clear marker/journal/backups; the outcome is `Applied=true` |
| during recovery itself | unchanged | recovery is idempotent, so the next boot repeats it |
| restore fails (file locked) | kept | journal + backups are kept, attempt counted, next boot retries the restore |

Recovery runs first in `ApplyPendingIfAny`, before the marker is read and therefore
before anything of the application loads — on every launch, including an
`allegiance://` join-link launch, which skips only the network check
(`Program.cs:47-56`). `ApplyPendingIfAny` never re-attempts the apply
in the same boot after a rollback (a deterministic failure would just repeat, and an
incomplete rollback must not be buried under new mutations).

### 2.4 Failure boundaries

* **Verify** — path guard (+ new `autoupdate/` rejection), entry present in the staged
  manifest, file exists, size, SHA-256, and (Windows) not a module the running process
  has mapped.
* **Copy** — each target is written as `<target>.update-new` in the target's directory,
  the mode is applied to the temp file, then `File.Move(temp, target, overwrite: true)`
  (an atomic `rename`). This also matters on Linux: replacing a **mapped** assembly by
  rename is safe (the running image keeps the old inode), while truncating it in place
  would not be — pinned by `A_mapped_file_can_be_replaced_atomically`.
* **Delete** — a delete failure now aborts and rolls back (the base swallowed
  `IOException` per file, which is how repro B produced a mixed tree).
* **Mode** — set on the temp file before the rename, so a mode failure cannot leave a
  replaced target with a wrong mode.
* **installed.json** — written only at commit; on rollback it is restored from its backup
  (or deleted if it did not exist before).

### 2.5 Retry cap (`UpdateApplier.MaxApplyAttempts = 3`)

`UpdatePendingUpdate.Attempts` (default 0, absent in pre-P976 markers) is incremented on
every failed apply and on every crash-interrupted apply. At the cap the marker, staging
bytes and staged manifest are discarded with one log line (`[update] discarding the
staged 2.0.0 update after 3 failed attempt(s); …`), and the client stays on the installed
version. No retry happens inside one boot, so the loop is at most one apply per start.

Two details make the cap actually reachable end to end:

* **Re-staging the same version keeps its count** — `UpdateClient` carries
  `pending.Attempts` over when it stages the version named by the existing marker, so a
  client that re-checks the feed every boot cannot reset a persistent failure to
  attempt 1 forever (`UpdateClient.cs:126-136`).
* **A discarded version is not staged again** — the applier writes
  `<appRoot>/autoupdate/discarded.json` = `{"version":"2.0.0"}`, and the next feed check
  refuses that exact version with `version 2.0.0 was discarded after 3 failed applies;
  keeping the current build (a newer release will be staged)`
  (`UpdateClient.cs:68-76`, `UpdateApplier.cs:129-181`). A newer feed version stages
  normally and clears the tombstone. The release pipeline never reuses a version string,
  so a repaired build always arrives as a new version.

Both are pinned by `A_version_that_fails_the_apply_cap_is_not_staged_again_until_a_newer_one`
(loopback feed: three failing applies with the same version → marker gone, staging gone,
re-check refused, `2.0.1` stages with `attempts = 0`).

### 2.6 Windows: a file in use cannot be replaced

The client applies from the top of `Program.Main` (`Program.cs:47-51`). At that point the
runtime has already loaded — and the OS has mapped and locked — the host image and
`Allegiance.ClientCore.MonoGame.dll` (the assembly `Program.Main` itself lives in),
`Allegiance.Update.dll` and the BCL; other port assemblies load only on demand, later.
Windows refuses to replace (`MoveFileEx`) or truncate a mapped image, so a staged set that
contains one of those files can never be applied in-place from inside the process.

Handling: during pre-verification the applier checks every existing target against
`Process.GetCurrentProcess().Modules` (`UpdateApplyWindowsLock`,
`src/Allegiance.Update/UpdateApplyJournal.cs:255-289`) and fails with "loaded by the
running process" — with **nothing changed**, instead of discovering the lock halfway
through. The attempt counts against the cap, so after three starts the stage is
discarded rather than looping. If the module enumeration is unavailable the check fails
open: the rename raises `IOException`, and the transaction rolls back (still atomic, just
later). Native's answer to the identical problem was a separate `Reloader.exe` that ran
after the client exited and retried move errors for 25 s because "Allegiance.exe isn't
done exiting and has some files open" (`Reloader/Reloader.cpp:1-11`, `:108-224`);
`MoveFilePrivate` also had a `bSkipSharingViolation` mode for it
(`clintlib/AutoDownload.h:648-700`). The port keeps that role in the packaging path: the
Windows installer / portable zip replaces the tree while no client runs (packaging is out
of scope for P976). The updater itself never leaves a mixed tree on Windows — it either
applies fully or changes nothing.

## 3. Changes (file:line as of this working tree)

### New: `src/Allegiance.Update/UpdateApplyJournal.cs`

| What | Lines |
|---|---|
| `UpdateApplyJournal` (phases, path, `Read`/`Write`/`Delete`, JSON-aware null guard) | `:31-119` |
| `UpdateApplyJournalEntry(Path, Backup, UnixMode)` | `:122` |
| `UpdateApplyStep` — the boundaries the tests inject at | `:129-151` |
| `UpdateApplyTestSeam` — internal fault hook (null in production) | `:154-160` |
| `UpdateApplyPaths` — `Resolve` (containment), `Relative`, `CurrentMode`, `Replace` (temp + atomic rename), `SameContent`, `DeleteIfExists`, `TryDeleteDirectory` | `:165-252` |
| `UpdateApplyWindowsLock` — mapped-module pre-check (Windows) | `:255-289` |
| `UpdateRecoveryAction` / `UpdateRecoveryResult` | `:293-315` |
| `UpdateApplyRecovery.Recover` — abandon `backing-up`, roll back `applying`, finish `committed` | `:317-371` |
| `UpdateApplyRecovery.Rollback` — restore content+mode, delete created files/dirs/temps, keep journal on incomplete restore | `:374-449` |

### `src/Allegiance.Update/UpdateApplier.cs`

| What | Lines |
|---|---|
| `UpdateDiscardRecord` (the retry-cap tombstone) | `:27-34` |
| `UpdatePendingUpdate.Attempts` (retry cap counter; 0 for old markers) | `:25` |
| `UpdateState.DiscardPath` / `ReadDiscardedVersion` / `RecordDiscard` / `ClearDiscard` | `:43`, `:149-181` |
| `UpdateState.RecordApplyFailure` — count, or discard at the cap with the log line | `:129-147` |
| `UpdateState.ClearStaging` | `:183-193` |
| `UpdateApplier.MaxApplyAttempts = 3` | `:243` |
| `ApplyPendingIfAny` — recovery first, one apply attempt per boot | `:245-263` |
| `ApplyPending` — journal guard | `:265-270` |
| 1. pre-verify the whole staged set (path/state guard, manifest entry, size, SHA-256, Windows lock) | `:283-340` |
| 2. journal (`backing-up`) + backups of every replaced/deleted file and `installed.json`, then journal (`applying`) | `:342-375` |
| 3. apply: atomic temp+rename per file, then obsolete deletes | `:377-397` |
| 4. commit: `installed.json`, journal `committed`, cleanup | `:399-413` |
| catch → rollback → count the attempt, report the rollback result ("keeping the current build" only when the restore completed) | `:415-441` |
| `CollectMissingDirectories` | `:443-455` |

### `src/Allegiance.ClientCore.MonoGame/Program.cs`

* P976 — a join-link launch still runs the apply/recovery before anything loads
  (`:47-56`): the link launch skips the *network* check, never the crash
  recovery, so an interrupted apply cannot survive into a `allegiance://` start.

### `src/Allegiance.Update/UpdateClient.cs`

* P976 — refuse to re-stage a discarded version, clear the tombstone for a different one
  (`:68-76`).
* P976 — carry the failed-apply count over when re-staging the same version
  (`:126-136`).

### `src/Allegiance.Update/UpdateManifestBuilder.cs`, `Allegiance.Update.csproj`

* `UpdateApplyJournal` added to the source-generated JSON context (`:18`).
* `UpdateFeed.DiscardFile` (`:41`) and `UpdateFeed.IsStatePath` (`:47-52`) — the applier refuses to install into, or delete
  from, `autoupdate/` (the marker, staging, journal, backups and installed.json live
  there).
* `InternalsVisibleTo Allegiance.Update.Tests` for the fault seam.

### Tests: `tests/Allegiance.Update.Tests/UpdateApplyAtomicityTests.cs`

21 test cases (16 `[Fact]` methods plus 2 `[Theory]`s carrying 5 cases) that hash **every**
file in the app tree (SHA-256 + Unix mode, state dir excluded) before and after and
compare, plus the version:

* `Apply_with_a_missing_staged_file_changes_nothing`, `Apply_with_a_tampered_staged_file_changes_nothing`
* `A_failure_before_the_first_copy_changes_nothing` (verify)
* `A_failure_after_k_copies_restores_everything(k=1,2,3)` (copy, also a created file in a created dir)
* `A_failure_during_the_deletes_restores_the_deleted_file`, `An_undeletable_obsolete_file_rolls_the_apply_back`
* `A_failure_during_the_mode_set_restores_everything`, `A_failure_during_the_backups_changes_nothing`
* `A_marker_naming_an_out_of_bounds_path_is_refused` (`autoupdate/installed.json`, `../escape.dll`)
* `A_journal_left_by_a_crash_is_rolled_back_on_the_next_boot` (hand-written journal)
* `A_crash_mid_apply_is_rolled_back_by_the_next_boot`, `A_crash_during_the_backups_is_abandoned_without_touching_the_tree`,
  `A_crash_after_installed_json_restores_the_old_version`, `A_crash_after_the_commit_marker_keeps_the_new_build`
* `An_incomplete_rollback_keeps_the_journal_and_retries_on_the_next_boot`
* `A_mapped_file_can_be_replaced_atomically`
* `A_pending_update_that_keeps_failing_is_discarded_after_the_cap`
* `The_success_path_still_applies_everything` (success path unchanged)

A source-order guard from P944 was updated for the new boot branch:
`tests/Allegiance.ClientCore.MonoGame.Tests/P944JoinLinkUiTests.cs:85-95` (it passes 10/10).

plus, in `tests/Allegiance.Update.Tests/UpdateClientLoopbackTests.cs`:
`A_version_that_fails_the_apply_cap_is_not_staged_again_until_a_newer_one` (cap end to end
through a real staging check; attempt-count carry-over; tombstone; newer version stages).

Native reference lines cited above were re-grepped in `~/Development/Allegiance/src/`
(`clintlib/AutoDownload.h:230-278`, `:262-268`, `:648-700`;
`clintlib/AutoDownload.cpp:964-978`; `Reloader/Reloader.cpp:1-11`, `:108-224`).

## 4. Verification

| Step | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | PASS — all 123 tool projects built, 0 warnings |
| `tests/Allegiance.Update.Tests` (3× alone, and in every fast sweep) | **54 passed, 0 failed, 0 skipped** each time |
| Packaging unit tests (`python3 -m unittest discover -s packaging -p 'test_*.py'`) | PASS — 23 tests |
| AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | PASS — no IL2026/IL3050 warnings (the server references the changed library) |
| `python3 scripts/parity-scorecard.py` | PASS — 700 rows validated, autoupdate 100 % |
| `python3 tools/check-glossary-links.py` | PASS — 680 paths |
| One full fast-tier suite (`tools/run-tests.sh --tier fast`, final tree) | 13,642 tests, **FAIL** — 6 non-task failures, all qualified below |
| Target class touched by the boot-order change: `P944JoinLinkUiTests` | 10/10 pass |
| Non-task failures, 3× alone | `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_…`, `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again…`, `P761bCommandRelayTests.QueuedStationGoto_…`, `P739TimerLiveTests.Warp_bomb_notice_…(delay: 7)`, `GameOverPlayersWireTests.Rejoined_player_…`: **pass/pass/pass** each; `MissileLockLiveTests.MissileLock_BuildsPartialThenFull…`: 1 pass / 2 fail, the known **open** partial-tip receipt failure (ledgered since P935, fails isolated cohorts too; no lock/missile/tip code changed). All six plus the earlier sweeps' flake sets are ledgered in `docs/TEST_FLAKES.md`. |

Three Fast sweeps were run for this task (host load 3.7 / 14.4 / 3.9). All three kept
`Allegiance.Update.Tests` at 54/54. The first two failed only on the differently-sampled
non-task families above (the failure set moves with load — the signature of the existing
ledger, not of this change). The **second** sweep also caught one task-caused failure:
`P944JoinLinkUiTests.Program_guard_runs_before_training_benchmark_automation_and_update`
asserts the exact link-launch source line, which the §3 `Program.cs` change replaced. That
source-order guard was updated to the new branch (link launch must not reach the feed
check, must still run the apply/recovery) and the class now passes 10/10
(`tests/Allegiance.ClientCore.MonoGame.Tests/P944JoinLinkUiTests.cs:85-95`); the third
sweep ran the final tree. Two calibrated 10 s timing exceedances in these sweeps
(`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured`,
`P750NativeAsteroidTypesTests…`) are runtime-only and pre-existing.

`local.env` is present in this worktree, so the gate's environment-dependent tests were
not skipped: `Allegiance.Update.Tests` reports **0 skipped** (54/54 executed) in every run;
the Fast sweeps' 3 `NotExecuted` cases are the documented optional Meshy-pack ones
(`MeshyPackTests`), unrelated to `local.env`.

## 5. What is deliberately not done

* The feed/signature format and the packaging scripts are unchanged (out of scope).
* A Windows self-update of a *loaded* assembly still needs the installer/relauncher path;
  the applier detects it and fails cleanly (see §2.6). Implementing a Reloader-style
  helper was out of scope (packaging/out of scope) and is not needed for atomicity.
* Everything else in the P971 review is untouched.

---

### Native intent

Native downloaded only the changed files (`clintlib/AutoDownload.cpp:225-282`, local
analysis `:993`), then `MoveFilesThenRestartOrRelogOn` (`:964-978`) called
`CAutoDownloadUtil::MoveFiles` (`clintlib/AutoDownload.h:230-278`), which iterated the
staged files and `MoveFile`d them one at a time — `MoveFilePrivate` (`:648-700`) even
deleted the destination first. On the first failure it returned `false` and the caller
could only report "Error while moving downloaded files (be sure dest file isn't already
open)"; the files already moved stayed moved (only the single file it was registering was
moved back, `:262-268`). A dedicated `Reloader.exe` existed to run the move after the
client exited, retrying sharing violations for up to 25 s
(`Reloader/Reloader.cpp:1-11`, `:108-224`). Intent: *get the corrected file set in place
before the game runs; never run a half-updated client*. The atomicity we add is the part
native never had.

### Modern approach

A signed-manifest updater applies through a real transaction: verify the whole staged set
(path containment, size, SHA-256, mapped-module check) before the first mutation; journal
the intent into `autoupdate/apply-journal.json`; back up every file the transaction
touches (content + Unix mode) inside `autoupdate/`; swap each target by an atomic
same-directory rename; commit `installed.json` only at the end; roll everything back on
any error and recover from a crash/power loss on the next boot (roll back `applying`,
finish `committed`); bound a bad stage at three attempts. Windows' in-use-file limitation
is detected up front rather than surfacing mid-transaction, and the port's equivalent of
native's Reloader is the packaging/installer path.
