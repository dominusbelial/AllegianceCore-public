# P1009 — P990 telemetry accounting overhead

Investigation on base `bcdcd8a0`, comparing original P990 `015019bf` on the
same lane host. The original revision also fails: this is a host/measurement
problem, not evidence of a later telemetry regression. No production code changes.

## Finding and method

The first experiment alternated ten fresh-process executions of each revision
using the original harness, Debug, default JIT settings, SDK 10.0.404, artwork
present, on the lane's Ryzen 9 9955HX (16 online logical CPUs; no cgroup CPU
quota). No competing workload was started. Host load varied; unrelated lane
work was not controlled. Raw receipts: `.scratch/p1009/original/`.

| Original harness | Original P990 | Base |
|---|---:|---:|
| Passed / Failed / Skipped | 0 / 10 / 0 | 0 / 10 / 0 |
| ns/call, median [min, max] | 31.7 [30.0, 34.1] | 31.8 [31.2, 33.0] |
| ms/mixed tick, median [min, max] | .22365 [.2117, .2405] | .22435 [.2203, .2331] |
| projection p95 ms, median [min, max] | 4.889 [4.623, 5.133] | 4.573 [4.342, 4.877] |
| p95 share %, median [min, max] | 4.6435 [4.235, 4.914] | 4.9155 [4.561, 5.368] |

The median accounting difference is 0.3%, inside overlapping spreads. Production
`BroadcastTelemetry.cs` is identical at both revisions (Git blob
`30b507464bffa98057b93386ff875fe577c094bc`). Only the test method's name changed.
There is no stable passing historical per-call baseline to bisect: nine old
runs fail that assertion; old run 5 is just inside it (displayed as 30.0 ns)
and instead fails the share assertion. Gunfire batching is absent from the
timed workload; P1002 already exercised its ON/OFF controls.

The main checkout's Git metadata is read-only in this lane. The historical
worktree therefore belongs to a local shared scratch clone, with its own writable
metadata; the main checkout remains untouched.

Historical restore initially found the original vulnerable transitive Cbor
9.0.0 dependency. Only the base's existing Lobby project dependency pin to
9.0.24 was copied into the historical worktree. NuGet audit stayed enabled.
A writable copy of the existing NuGet HTTP cache avoided writes to the
read-only home cache; no package audit suppression was used.

## Native references and root cause

Re-verified mounted native sources: `Inc/messagecore.h:76-85` defines the
`cbmsg/fmid` header; `FedSrv/FedSrv.CPP:4179-4194` sends per-player heavy updates
with optional appended light bytes. These explain the measured parse/split
work, not the choice of performance budgets. Native defines no 30 ns or 3%
telemetry rule. The matching heavy/light wire parity rows are annotated with
this test-only investigation; their parity status does not change.

Original root-cause locations in
`tests/Allegiance.Core.Tests/P990BroadcastLaneTelemetryTests.cs`:

- `:511-518`: 20 warm-up invocations followed by a single 50-repeat accounting
  block; no block distribution, allocation check or collection evidence.
- `:715`: projection warm-up called `Advance()` only. That updates ship
  poses and flushes snapshots, but does not call `TestProjectRecordedTick`.
- `:539-546`: absolute bounds derived from a different host. The original
  report's 20.8–22.4 ns and ~5.9 ms projection do not describe this host.

The old comment also claimed 30 ns would catch the older dictionary result
of 25–28 ns; that inequality is false. This task does not claim to catch every
historical implementation change below a chosen safety margin.

## Fix and derivation

The second experiment copied the revised method unchanged into both worktrees
but retained the original assertions during calibration: ten fresh-process
executions per revision, each with **15 paired accounting blocks** and ten
projection p95 blocks. All twenty still failed the original share assertion;
all twenty now passed 30 ns. Raw receipts: `.scratch/p1009/method/`.

| Warmed method | Original P990 | Base |
|---|---:|---:|
| Passed / Failed / Skipped with old bounds | 0 / 10 / 0 | 0 / 10 / 0 |
| ns/call, median [min, max] | 27.9 [27.5, 28.8] | 27.9 [27.4, 28.4] |
| ms/mixed tick, median [min, max] | .19695 [.1939, .2032] | .19650 [.1935, .2002] |
| projection p95 ms, median [min, max] | 4.436 [4.401, 4.529] | 4.3985 [4.374, 4.570] |
| p95 share %, median [min, max] | 4.438 [4.392, 4.543] | 4.4725 [4.290, 4.550] |
| paired current/reference ratio, median [min, max] | 1.0025 [.788, 1.006] | 1.0025 [.993, 1.006] |

Current accounting is 0.23% faster by tick-median, not slower. Warm-up reduces
the observed ~31.8 ns to ~27.9 ns without a production change. It cannot make
this host's projection denominator equal the original report's ~5.9 ms.

Final method:

1. Actually project during the initial >=1 s projection warm-up and the 15
   warm-up ticks before each block. Measure ten 120-tick blocks, taking each
   block's p95 then their median. Keep GC-inclusive projection times because
   collections are real projection costs; log collections and allocations.
2. Create all frames, peer tables, pose entries and sample arrays outside the
   accounting timer. Warm both accounting implementations together for >=1 s.
   Keep the original 49 calls/peer x 144 peers (7,056 calls/tick), including
   the peer lookup and array-loop costs; this is a mixed-workload average,
   not a direct measurement of each method's isolated latency.
   Dividing this accounting workload by the separately measured OFF projection
   p95 is a workload budget, not an observed ON-minus-OFF server slowdown. The
   existing rig-scale projection test covers that integrated question.
3. Time 15 paired blocks of 128 workloads each, alternating current/reference
   order, and use the median paired ratio. Record block values and the median
   accounting ms/tick. Reject an accounting pair if GC counts or pause duration
   change, with a finite retry limit; assert zero measured-thread allocations.
4. Compile a frozen copy of the original 700-line implementation only in the
   test assembly, with a distinct namespace. Its original Git blob is recorded
   in its header; no runtime Git access or dependency on production internals
   for the reference computation. Preserve the entire source so retirement,
   parsing and table behavior are not approximated by a hand-written mock.

**Budget derivation:** replace the absolute 30 ns with a paired median ratio
**<=1.35** against that immutable reference, retaining the original ~35%
per-call safety margin. The independent actual tick-share cap becomes **6%**:
the measured ~4.5% x the original ~1.3 share margin = ~5.85%, rounded up.
This changes an assertion only after both historical and current code prove
the old bound hardware-dependent. The 411-peer whole-projection 1.5 ms guard
remains unchanged. The final test's name describes its relative budget and
small-share guard instead of promising the obsolete absolute limits.

**Measurement checks and limits:** Stopwatch reports high-resolution timing at
1,000,000,000 ticks/s; accounting blocks are ~25 ms, so timer granularity cannot
explain a 10 ns/call difference. All 300 calibration accounting pairs report
zero allocations and zero GC rejection. Projection sees 41–49 collection ticks
per 1,200 measured ticks and ~184–194 KB allocated per tick; its p95 is not a
pure CPU constant. Across calibration runs its median p95 spans only
4.374–4.570 ms on base. No tiering/OSR switches are used for qualification;
elapsed-time warm-up allows asynchronous compilation instead of assuming 20
calls suffice. Debug configuration and runtime settings match across revisions.

An early exploratory revised-method execution (before projection GC logging)
observed 55.1 ns current / 51.72 ns reference and a 7.072% share, with blocks
varying .2765–.4102 ms. It is retained as a disturbed run, not silently included
in the stable calibration. One historical calibration process also measured
34.80 ns reference versus 27.5 ns current (ratio .788). A separately compiled
reference is not an exact clock oracle: JIT/code placement and scheduling can
favor either copy. The relative allowance is a material-regression guard, not
a claim to resolve sub-percent changes or detect every slowdown below 35%.
The independent share guard limits a slower reference's ability to mask cost.
Perf qualification uses the runner's existing idle/load check; no load-limit
override or synthetic competing CPU workload is used.

## Tests and gate

Final isolated qualification: **5 passed / 0 failed / 0 skipped per revision**.
The same final test source and frozen reference were copied into the historical
worktree; its production implementation remains P990. Every run below contains
15 accounting pairs and ten projection blocks.

| Revision / run | ns/call | Reference ns | Ratio | ms/tick | OFF p95 ms | Share % |
|---|---:|---:|---:|---:|---:|---:|
| P990 / 1 | 28.3 | 28.18 | 1.003 | .1996 | 4.376 | 4.562 |
| Base / 1 | 28.3 | 28.19 | 1.004 | .1996 | 4.397 | 4.541 |
| P990 / 2 | 28.2 | 27.97 | 1.008 | .1989 | 4.345 | 4.577 |
| Base / 2 | 28.6 | 28.34 | 1.007 | .2017 | 4.356 | 4.631 |
| P990 / 3 | 27.8 | 27.81 | 1.001 | .1965 | 4.321 | 4.547 |
| Base / 3 | 28.7 | 28.37 | 1.011 | .2023 | 4.376 | 4.622 |
| P990 / 4 | 28.9 | 27.74 | 1.042 | .2037 | 4.353 | 4.680 |
| Base / 4 | 28.2 | 28.36 | .997 | .1992 | 4.396 | 4.532 |
| P990 / 5 | 28.7 | 28.50 | 1.009 | .2027 | 4.363 | 4.645 |
| Base / 5 | 27.8 | 28.14 | .989 | .1962 | 4.417 | 4.443 |

Durable receipts: [all 50 runs](P1009_assets/runs.csv), including the original
and calibration failures and per-arm numbers; [all 1,200 block measurements](P1009_assets/blocks.csv).
All 150 final accounting pairs report zero GC rejections and zero allocations.
Historical per-call median [range]: 28.3 [27.8, 28.9] ns; base: 28.3 [27.8, 28.7]
ns. Their share medians are 4.577% and 4.541%, respectively.

Guard controls: the existing `P744_SIMULATE_REGRESSION=2` test-only control
fails the relative assertion as intended: measured 27.9 ns/call, ratio 1.000,
guarded ratio 2.000 > 1.35 (**0/1/0**, expected failure). This scales the
measurement; it does not introduce CPU load. With
`DOTNET_TieredCompilation=0`, the unscaled test passes (**1/0/0**): 29.5 ns/call,
29.47 ns reference, ratio 1.004, .2083 ms/tick, 4.582 ms OFF p95, 4.547% share.
Both controls have zero accounting allocations and zero rejected pairs.

The complete solution build passes with **0 warnings / 0 errors**, 50.55 s,
using `--no-restore --disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`. A previous interrupted attempt exhausted disk;
the completed build had 52 GB available. All build outputs are task-local.
The first tool-build pass found missing restore assets for standalone projects.
Their restore uses installed package archives and a loopback-only mirror of the
cached official NuGet vulnerability database (2026-11-14 05:29:55 UTC), with
audit enabled for all dependencies. A control restore of vulnerable Cbor 9.0.0
fails with **NU1902**, proving that offline audit still rejects it. No public
host is contacted for the resumed gates.

`tools/build-tools.sh --drop-bins` passes: **all 138 tool projects**, each with
**0 warnings / 0 errors**. `--drop-bins` is the existing CI disk-conservation
mode; outputs referenced by tests or the solution are retained. The wrapper adds
the build-server workaround, skips the completed offline restore and disables
automatic MGCB tool restore (the tool is already installed).
Glossary link validation passes (812 paths), parity-scorecard validation passes,
and `git diff --check` passes.

The filtered Perf invocation (`TEST_FILTER=FullyQualifiedName~P990
tools/run-tests.sh --tier perf --no-build`) discovers both P990 Perf cases but
refuses to measure: after its 60-second idle-check window, load/CPU is 1.73
against the unchanged 0.80 limit. No Perf cases execute in this attempt. The
five isolated benchmark qualifications above remain valid separate evidence;
this refusal is not counted as a passing tier gate.

The one full Fast suite completes in **563.018 s**, within its 110-minute
allowance: **14,254 passed / 4 failed / 7 skipped**, all **14,265** discovered
Fast cases accounted for. Infrastructure checks pass **9/9**; both quarantine
lanes pass (**19/0/0**). The gate remains **FAIL**: missile-lock partial-tip,
warp own-status, auto-seat simulated-time boundary, and empty-pack drop receipt
failures, plus four timing offenders against the calibrated **11.521485 s**
limit (missile lock and the successful varied-loadout, Nanite-support and P999
profile cases). These match existing ledger signatures and execute no changed
production path. P990's changed benchmark is Perf, so it is not part of Fast.

All seven failed/slow methods then run **3 times alone** in fresh, serial
processes, retaining every theory parameter: **56 passed / 1 failed / 0
skipped**, 21 invocations. Missile lock is **fail / pass / pass** (38.633 /
1.573 / 1.632 s), keeping the existing partial-tip issue open. Warp, auto-seat
and drop-part pass all three. All varied-loadout (3 cases), Nanite-support (2)
and P999 profile (10) cases pass every invocation, individually below the
unchanged Fast limit. Exact names, durations and classification are recorded
in the P1009 update to `docs/TEST_FLAKES.md`; full per-case receipts and gate
summaries are retained in [qualification.json](P1009_assets/qualification.json).
No failing assertion is relaxed and no second Fast suite is used to replace
the failed one.

After every isolation process ends, the filtered Perf gate passes its unchanged
idle check (two samples at **0.444 load/CPU**) and runs **2 passed / 0 failed /
0 skipped**, 19.1 s:

- Mixed accounting: **32.5 ns/call**, reference **32.26 ns**, paired median
  ratio **1.007**, **.2293 ms/tick**, OFF p95 **5.433 ms**, share **4.220%**.
  Fifteen pairs, zero GC rejections, zero allocations. Individual projection
  p95 blocks vary **4.283–9.811 ms**, illustrating residual host scheduling
  noise even after the idle check; all block values are in qualification.json.
- Unchanged 411-peer rig projection: OFF/ON median means **9.365 / 9.485 ms**,
  delta **+.120 ms**, below the existing **1.5 ms** bound. Report cost **3.70
  ms**; 55,896 pose stamps. This guard's implementation and assertions are
  untouched.

Final gate disposition: solution and all tools **PASS**, changed test **5/5
per revision**, filtered Perf **PASS**; the one full Fast sweep remains
**FAIL** on its ledgered non-task failures and timing offenders. Every required
isolation is complete. `scripts/publish-guard.sh bcdcd8a0 HEAD` passes for the
existing implementation commit; the final working-tree diff passes whitespace
and public-content checks. No new production code, service, port, environment
configuration, HUD or media is introduced. The brief is retained only in
ignored scratch. No commit or push was made during this completion pass.

## Native intent

Preserve the existing broadcast payloads, ordering, cadence and visibility rules.

## Modern approach

Keep diagnostic accounting cheap and measure its steady-state cost with a
repeatable, hardware-aware performance guard.
