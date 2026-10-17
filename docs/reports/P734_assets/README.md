# P734 verification evidence

The definitive results are `verification-final.json`, `summary.json`,
`build-final.log`, `focus-final-*`, `parking-*`, `showcase-ring-final.json`, `suite-*`
and `suite-retry-*`. All network scenarios use local ephemeral-port servers.
No rig/221/200v200 checks, staging, commits or pushes are invoked.

The final isolated cohort contains P708's 79 affected methods, four P707
parking methods and all eight P699c station-corpus methods: 91 methods,
273 exact-method invocations, 537 passing data cases. `docking-green-*`
adds the immediate three-run verification of the corrected idle-combat bug.

From the repository root, the underlying commands are:

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p734-nuget-http dotnet build AllegianceCore.slnx --nologo -m:8 -v:q
P734_LEDGER=verification-final.json python3 docs/reports/P734_assets/verify.py focus-final
P734_LEDGER=verification-final.json python3 docs/reports/P734_assets/verify.py parking
NUGET_HTTP_CACHE_PATH=/tmp/p734-nuget-http dotnet build docs/reports/P734_assets/showcase-ring/showcase.csproj --nologo -p:BuildProjectReferences=false -v:q
# Load local.env into the environment for the standalone driver.
dotnet docs/reports/P734_assets/showcase-ring/bin/Debug/net10.0/Allegiance.ClientCore.Tests.dll docs/reports/P734_assets/showcase-ring-final.json
P734_LEDGER=verification-final.json python3 docs/reports/P734_assets/verify.py suites
P734_LEDGER=verification-final.json python3 docs/reports/P734_assets/verify.py retries
python3 docs/reports/P734_assets/summarize.py
```

The verifier loads `local.env` without echoing it and enables both existing
local P542/P660 opt-ins. `focus-final` resumes successful nonempty exact-method
runs already in its ledger. Other modes perform fresh runs. Use a fresh evidence
directory/ledger for independent certification; do not silently append extra
full sweeps to the recorded final certification.

`run-final.py` serialized the final parking gate, two-match showcase, exactly
two complete unfiltered Core/ClientCore/MonoGame/Lobby sweeps, then failure
isolation. It first waited for the final triplets and diagnostic-controller
cleanup. All product source hashes are in `source-manifest.json`.

The successful standalone showcase uses the unchanged mixed-fleet preset at
size_scale=3 (45v45), civs 25/3, the existing SingleRing map generator and
automatic rotation. P733 is absent on master, so the brief's unlocked fallback
is used. The driver's per-IP limit is 128 and it asserts the full 90-bot roster.
Staging is enabled. Existing finale settings use both sides, five-minute
maturity, 50% relaunch share, five recent kills and escort share 1/cap 12.
Both matches end through ordinary combat before the late finale triggers:
Stage Over at 107.144 and 198.702 wall seconds, with zero simulation failures.
The second mission was observed Started before its independent game-over.
The driver never writes poses, inflicts fixture damage, invokes game over, or
sets mission stages. Each captured view is frozen immediately.

The default-map 15-minute attempt serialized by `run-final.py` did not end.
The successful compact-map follow-up ran alongside the serial suite projects,
using existing scene settings; production binaries were not rebuilt. Isolated
failure classification runs after the sweeps and after that proof has ended.

Historical attempts remain separate:

- `verification-initial.json`: pre-correction isolation and ten passing parking
  runs; original parking artifacts have the `parking-initial-*` prefix.
- `selection-audit-001`: one empty P707 filename-as-class selection. The
  verifier stopped; correct filters use `BotFixRegressionTests`. This is not
  counted as a passing or skipped test.
- `showcase-attempt-{1,2}.*`: smaller 15v15 diagnostic scenes that did not reach
  Stage Over before cancellation. The initial XXV_simple status retained
  deferred station views, so its station entries are not damage histories.
- `showcase-diagnostic-scale3.*`: an early driver attempt that encountered the
  default 64-connections-per-IP limit. The final driver explicitly configures
  128. Diagnostic cancellation at a status-write destination produces the
  retained UnauthorizedAccessException, followed by clean server shutdown;
  this is a deliberate driver stop, not a simulation-stage failure.
- `gates-attempt-*`, `suites-run.log`, and `retries-run.log`: interrupted
  controllers unexpectedly continued into two overlapping Core-only checks.
  Their follow-on projects were blocked. These are not final full sweeps.
  The two complete diagnostic TRX captures are `diagnostic-Core-2.trx.gz`
  (4,804 pass / 3 fail) and `diagnostic-Core-last.trx.gz` (4,806 / 1).
  Both expose the corrected docking assertion; two unrelated wire/transport
  failures are also retained for three-run isolated classification. One
  partial capture is labeled `.partial.gz` and excluded from counts.

`master-preservation.json` and `matrix-audit.json` compare against the merge's
master tree using read-only Dulwich object access, installed only under `/tmp`.
`merge-metadata.json` fingerprints HEAD, index, MERGE_HEAD and ORIG_HEAD.
The final summarizer checks these and source hashes. Conflict contents are
resolved; the dispatcher must stage the files to mark the index resolved.

`showcase.json.gz` / `final-showcase-run.log.gz` retain the fully admitted unstaged
45v45 default-map timeout. `showcase-ring-attempt-1*` retains the first
unstaged compact-map attempt; it did not reach a base before cancellation.
`showcase-ring-final.json` / `showcase-ring-run.log.gz` and the current
`showcase-ring/Program.cs` are the successful staged follow-up. The old
historical capture is labeled separately and is not used for certification.

The first MonoGame wrapper exited zero without a completed TRX or final
summary. Its incomplete record is archived in `verification-incomplete.json`.
It is excluded from complete sweep counts. The already-built assembly was
completed unfiltered with direct VSTest, producing `completion-MonoGame-1.*`
and `verification-mono-completion.json` (2,260 pass / 0 fail / 3 optional skips,
including the full 605-second flight soak). This completion ran alongside
Core sweep two. The second MonoGame invocation completed normally. No cause
is asserted for the initial early exit.

To reproduce the direct compiled-assembly invocation, after the solution build:

```sh
dotnet vstest tests/Allegiance.ClientCore.MonoGame.Tests/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame.Tests.dll '--logger:trx;LogFileName=completion-MonoGame-1.trx' --ResultsDirectory:docs/reports/P734_assets
```

After all suite/retry writers exited, `finish-evidence.py` archived the original
incomplete wrapper record and reconciled its successful completion into the
first sweep. `summarize.py` then validated eight nonempty completed suite rows,
exactly two per project. Earlier Core-only diagnostics and the incomplete
wrapper attempt are retained separately. All six isolated classification runs
passed; the completed sweeps total 16,520 passes, zero failures and six existing
optional skips. No extra complete full-solution sweep was run.
