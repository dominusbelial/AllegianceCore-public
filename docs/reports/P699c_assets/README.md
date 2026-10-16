# P699c evidence

The appended P699c section of [the report](../P699_POD_DOCKING.md) is the narrative entry point. This directory contains locally generated evidence; artwork and native source inputs remain external and are identified by SHA-256 in `inputs.json`.

Current validation uses only these artifacts:

- `accepted-build.log/json`: solution build before the isolated runs and suites.
- `source-manifest.json` and `accepted-binaries.json`: exact tested source and binary hashes.
- `accepted-focused-results.json`: each affected method, three separate exact-filter test processes.
- `isolate-accepted-extra-results.json`: JSON defaults and repository path guard, three separate processes each.
- `accepted-suites-results.json`: sequential, unfiltered Core, ClientCore, MonoGame and Lobby runs. P542 and P660 local opt-ins are enabled; no rig tests are invoked.
- `isolate-accepted-suite-failures-results.json`, when present: three isolated retries per full-suite failure.
- `validation-summary.json`: audited totals and SHA-256 for the compressed TRXs. Every ledger row points to its portable `.trx.gz`. These files retain case outcomes and captured output; incidental absolute `/tmp` console-log paths may be unavailable.
- `flight-results.json`, `pod-counts.json`, `load-podtrips.jsonl.gz`: final focused flight summaries and individual authoritative pod outcomes, including actual station/model and hull identity.

Run `python3 docs/reports/P699c_assets/verify.py` to audit the evidence and current binaries/inputs without rerunning tests. `accepted-validation.py` rebuilds and runs the whole sequence; `validate.py PHASE` is the underlying sequential runner. Outputs go to `/tmp/p699c-verified-validation`; creating a `CANCEL` file there prevents the runner from starting another invocation. Re-running overwrites the corresponding ledgers and TRXs.

`corpus.json` and `dump-corpus.cs` preserve the complete station-frame dump. `clash57-analysis.json` preserves counts and source hash for the supplied trace. `baseline-*`, `after-*`, `diagnostic-*`, `completed-core-*`, earlier `final-*`, `repaired-*`, and their retry/build ledgers are **development evidence**, not the current validation result. In particular, a passing early load does not reproduce the original rig failure, and an interrupted suite is not a complete pass. `before-human-sim-time/` contains the first complete passing sequence, before the final human time-dilation correction and expanded human test. Duplicate artifacts from the discarded distance-envelope experiment and canceled overlapping runners were removed.

The path-guard and JSON-default before/after triplets document deterministic corrections. Repeated physical staging failures were treated as controller defects. Only a final-suite failure followed by three isolated passes qualifies for the requested load-flake ledger.
