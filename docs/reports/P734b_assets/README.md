# P734b merge evidence

The report is appended to [P709_PARITY_WAVE12.md](../P709_PARITY_WAVE12.md#p734b-merge).

- `verification.json`: exact isolated/suite commands, counters, failure details,
  per-test skips, output, and compressed TRX filenames. Every invocation has its
  own process and log. Full suites are unfiltered direct VSTest assembly runs.
- `cohort.json`: deduplicated union of both waves' consumer cohorts, P707/P699c
  regressions, client sound-event tests, and the added chat merge regression.
- `verify.py`: runs focused triplets, five parking invocations, **one** suite
  sweep, or failed-method triplets. It refuses to overwrite existing invocation
  names and refuses a second full sweep.
- `run-gates.py`: initial serial controller. Finished focus/parking and stopped
  after the first showcase was canceled, before launching any suite. Its log
  is `gates-attempt-1.log`. The single final sweep was launched separately
  through `verify.py suites`, followed by `verify.py retries`; the log is
  `suites-run.log`. Independent showcase attempts overlapped that sweep; the completed proof
  is recorded separately.
- `showcase/`: local server/MCP driver adapted from P734. The completed proof
  uses Brawl, 45 ships per side (12 bombers), civs 25/3 and the authorized
  `unlocked` fallback because P733 is absent. No forced damage, poses,
  counters, stages or game-over calls.
- `showcase.json`: completed two-match mission-stage/combat/base/finale timeline.
- `showcase-receipt.txt`: actual station destruction, side deactivation, Stage
  Over and automatic rotation receipts from the server.
- `showcase-diagnostics.json`, `showcase-attempt-*.{json,log}.gz`: earlier
  incomplete, canceled scenes, excluded from gate certification. Their driver
  variants are retained as `showcase/Program-attempt-*.txt`.
- `matrix-audit.json`: row-by-row reconciliation against master, P734 and P709;
  unchanged periodic rows, eight implemented P708 rows, and LF check.
- `ours-preservation.json`, `ClientWorld.diff`, `automatic-merge.diff`: source
  preservation relative to P734 and reviewed existing-file diffs.
- `native-reviewed.txt`, `native-manifest.json`: native file/line review and hashes.
- `source-manifest.json`: built source/test fingerprints, rechecked at completion.
- `showcase-source-manifest.json`: completed standalone driver source hashes.
- `index-preservation.json`: read-only comparison of tracked working files to
  the merge index, including its two unchanged conflict entries.
- `merge-metadata-{before,after}.json`: read-only HEAD/index/MERGE_HEAD/ORIG_HEAD
  hashes and branch ref. No Git commands, staging, commits or pushes are used.
- `summarize.py`: verifies completed evidence and writes `summary.json`.

The machine-local `local.env` values are consumed without recording their
contents. The writable NuGet HTTP cache is `/tmp/p734b-nuget-http`. Local P542
and P660 opt-ins are enabled. Rig/221/200v200 runs are excluded by the brief.
The 2026-10-17 light gate overrides the historical reports' two-sweep/ten-parking
requirements. Claude's mini runner owns the second sweep.
