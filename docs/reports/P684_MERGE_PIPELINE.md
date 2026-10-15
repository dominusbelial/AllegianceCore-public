# P684 — merge the broadcast pipeline into integ7

Resolved the five conflicted files in the working tree. No Git write command,
staging, commit, push, or merge continuation was performed; Claude owns the merge
commit. The index will still report the original unmerged paths until Claude
stages the edited resolutions.

## Resolution and integration fixes

- Retained P668 immutable snapshots and the bounded publisher, P669 shared
  encoding, P677 interest tiers, and P679 pooled packets, transport frames and
  reusable projection buffers. Configuration defaults remain off. Tiers still
  imply snapshots and encode-once, and T2 remains bounded below by P659's
  `minUpdateHz` (and above by the server tick rate).
- Kept P670's worker write timing, per-peer/host enqueued and socket-written
  counters, measured wall-time windows, and retained departed-peer totals.
  Pooled-frame accounting, partial writes and in-flight backlog now use the
  frame's valid length, rather than rented-array capacity. Pooled-copy tests
  also assert exact framed enqueue/write totals in both transport modes.
  Payload-offer accounting runs atomically in the central publication sink,
  before transport lookup/framing/coalescing; publisher-coalesced deliveries
  are reported separately by the existing snapshot metrics.
- Began capture before the transport drain while retaining P673's drain/trace
  measurement. Kept P671's TCP keep-alive/read-deadline split and P673's receive
  decoupling. Snapshot and P670 diagnostics both emit at the existing window
  boundary.
- Kept P672's guaranteed ordered roster snapshots/deltas, harness exclusion,
  newcomer/stage refreshes and periodic authoritative refresh. Removed stale
  references to the superseded `_lastScoreRosterTick` map, including the replay
  reset seam; replays reset the actual delta baseline.
- Fixed an integration allocation regression: the unchanged P672 roster body
  allocated **61,462 bytes/tick** in the P679 512-peer check, raising producer
  allocation to **126,048 bytes/tick**. An allocation-free due check now avoids
  grouping and recipient sets on quiet ticks, while checking newcomers, stage,
  full-refresh deadlines, cadence and departed recipients. New tests exercise
  those wire behaviors with the pipeline off/on. Evidence for the failing
  measurement is in [exploratory-allocation.json](P684_assets/exploratory-allocation.json).
- Extended P682 retirement to clear the pipeline's shared heavy/light records,
  interest snapshot, tier-heavy and identity-packet caches, reusable peer array,
  teammate lists and side-sector sets. Rotation invalidates projection caches
  even without migrated viewers; disconnect/reseat retirement also invalidates
  them. Detached submitted snapshots and their pooled leases keep their existing
  publication ordering. Viewer retirement continues merging both ordinary and
  tier age windows and byte totals instead of resetting transport accounting.
- Retained P682's fresh warning/graph/roster sync and bot purchase/launch path,
  and P655's integrity and delivery checks. Kept both branches' flake-ledger
  entries.

## Regression coverage

`P682NextMatchCombatLiveTests` now has four cases: port/native × pipeline off/on.
Each uses production MCP spawning, purchases, launch, automatic conquest rotation,
ready-up, countdown, ClientWorld visibility and advancing enemy poses, projectile
hits and credited kills in both matches. All three pipeline flags are configured
on in each enabled case. The rotation-state regression also runs off/on, checks
that reusable peer state was populated, and verifies immediate cache retirement,
retained byte accounting and fresh world sync before the next broadcast.

`P684MergeTests` checks quiet roster ticks, immediate new-recipient snapshots,
immediate stage snapshots, and cadence-respecting departure deltas on the ordered
lane in both modes. The existing pooled-copy test now checks P670's exact framed
byte totals as well as content and copy ownership.

The four P669/P677 pinned transcript prefixes predated P672. Their expected
values were refreshed to the integrated roster-delta transcript after the
unchanged P672 body produced the same four new hashes and all per-recipient
byte/order/lane comparisons passed. The tests retain every off/snapshot/encode-once
comparison and all encode-count, lane, coalescing and failure assertions. Final
per-run JSON artifacts retain the complete hashes and packet/byte counts.

## Verification

Build: **0 warnings, 0 errors**; see [build.log](P684_assets/build.log).
Conflict-marker scan and `git diff --check` are clean.

| Required repeated check | Final result |
| --- | --- |
| Pipeline byte-identity/determinism, snapshot/encode-once, P677/P679 and new roster integration cases | **43/43 ×3**, no failures or skips |
| P682 two-match combat plus rotation/world-state cases, flags off/on | **8/8 ×3**, no failures or skips |
| Client tier-pose extrapolation cases | **6/6 ×3**, no failures or skips |
| P660 15v15 showcase with all pipeline flags enabled | **1/1**, bots/humans rotate and finale rearms |

The four complete transcript artifacts are identical across all three runs;
each artifact also has identical off/snapshot/encode-once hashes. The 512-peer
allocation measurements are **67,076 producer + 2,521–2,533 publisher bytes/tick**
(**69,597–69,609 combined**), below the unchanged 104,857-byte combined limit.
Roster allocation is **2,490 bytes/tick**, down from 61,462 before the due check.
See the per-run [allocation and transcript artifacts](P684_assets/final) and
[two-match proof](P684_assets/final/two-match-proof.json), which retains each
mode's shots, hits and kills for both games in all three runs.

Unfiltered assemblies run sequentially, including the existing live-timing lane:

| Assembly | Passed | Failed in full run | Skipped |
| --- | ---: | ---: | ---: |
| Lobby | 19 | 0 | 0 |
| Core | 4,445 | 1 | 0 |
| ClientCore | 964 | 0 | 0 |
| MonoGame | 2,143 | 0 | 3 existing optional Meshy-pack cases |

Core's sole full-run failure is
`Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events`
(6,672 measured bytes against its unchanged zero-byte assertion). After all
four assemblies completed, it passed **3/3 fresh exact-method isolated runs**.
The P661 accounting implementation, test, warmup, zero-byte assertion and traits
were not edited. This full-run-only allocation observation is recorded in
[TEST_FLAKES.md](../TEST_FLAKES.md); no reproducible regression remained in the
isolation audit.

The initial added pooled-telemetry assertion used one diagnostic worker slot,
but the pool can configure several workers. That fixture error was corrected to
allocate `PoolWorkerThreadCount()` slots. Both corrected transport-mode cases
passed in the full Core run, and the final preserved-feature batch passed
**85/85** after all suites and isolation audits.
The initial fixture failure is retained in
[exploratory-fixture-results.json](P684_assets/exploratory-fixture-results.json)
and its [raw log](P684_assets/exploratory-fixture.log)/compressed TRX. This fixture correction is not classified as a flake.

The final 85-case batch covers P655 integrity/delivery, P670 send telemetry,
P671 keep-alive, P673 receive decoupling, P659 cadence, roster traffic, pooled
buffers and partial-write checks. Final validation exits **0** with no uncleared
failures under the three-isolated-passes rule.

See [results.json](P684_assets/final/results.json) for per-run counters. Raw test
results are retained as `*.trx.gz` beside their logs; decode with `gzip -dc`.
These are local loopback/simulation checks, not a deployment or remote rig run.

Reproduce from the repository root:

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p684-nuget-cache dotnet build --no-restore -m:8 -v:m -p:UseSharedCompilation=false -nodeReuse:false
python3 docs/reports/P684_assets/validate.py all
```

The runner uses the local artwork/native corpus, clears broadcast environment
overrides so each test's explicit configuration takes effect, enables the local
P660 showcase, runs assemblies sequentially, and records logs, TRX results and
JSON counters. It runs the complete pipeline group and P682 group three times,
then the preserved-feature checks, an enabled-pipeline P660 showcase, and all four
unfiltered assemblies. Any full-suite failure gets three exact-method isolated
reruns after every assembly has completed. No timeout, quarantine trait or skip
is relaxed.
