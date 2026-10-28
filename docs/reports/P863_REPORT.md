# P863 — clash tick budget

Working-tree implementation on dispatch base `b37b4efe`, 2026-10-28. No commits,
no pushes and no rig/mini/Mac access. **The rig 200v200 A/B against b49, run by
Claude, remains the acceptance test:** TiDi >= 0.95 in every five-second clash
window, p95 <= 40 ms and p99 <= 50 ms. Local measurements do not certify that gate.

## Changes and attribution

`Sector.Update` now times `sim.prepare` (crowding/consumed-rock visibility) and
`sim.models` (snapshot, eligibility checks and model updates). The existing
subsystem scopes cover the remainder. In the corrected 400-ship fixture,
`sim.*` accounts for approximately 99–100% of sector-update time in both arms
at the benchmark's 0.01 ms print precision; sector 1052 dominates the update.
This clears the requested 90% attribution threshold locally. The same scopes
will identify the rig's own distribution; these local milliseconds must not be
substituted for its measured 24.7 ms.

The largest newly attributed item was model updates, especially explosions
inside projectile updates. Every blast snapshot was checking each model's
membership with a linear `List.Contains`, including models subsequently rejected
by type/radius. A counted membership dictionary now answers those checks in
constant time. The insertion-ordered list still owns traversal, removal and
collision ordinals. Counts preserve duplicate Add/Remove semantics too. Native
sources rechecked: `Igc/clusterIGC.cpp:243-250` (capture-next update traversal),
`:409-431` (blast-time position and current-cluster eligibility), `:443-467`
(mine blast handling). Existing blast/damage rules were retained.

For `broadcastShipsUpdate`, deviation remains per-peer, order-dependent state;
sharing one rank across peers would change outputs. `ShipUpdateRanking` instead
reuses sector-local storage, keeps a bounded heap of only the HEAVY+LIGHT served
prefix, sorts that prefix by deviation and original insertion ordinal, and visits
skipped entries without sorting them. Every skipped entry still receives exactly
one +1.00 increment. Targets, scaled quotas, light-tier increments, pose floors,
cloaking, fractions and turret relays retain their existing behavior. The heap
rejects below-prefix entries with `EnqueueDequeue`, avoiding repeated heap edits.
Native references: `FedSrv/FedSrv.CPP:3614-3621,3844-3845,3923-3950,4141-4188`.
This retains the port's existing cadence/density adaptations as well.

The three cross-side sweeps are incrementalized without moving their positions
in the broadcast sequence. `CrossSideSweepCache` retains completed work by object
reference, side and ordered audience. Unchanged identity/status pairs visit only
the original refresh-phase bucket; unchanged discovery pairs visit no recipients.
Audience changes, authoritative latch removals, replacement objects and status
visibility edges re-enter the original recipient loops. Eligibility gates,
per-peer latches, packet construction and send ordering remain in the existing
sweeps. Unsigned-ID wrap cases fall back to the original full loop. Entries not
visited in a sweep are pruned, so dead worlds/objects are not retained indefinitely.
`SweepLatchSet` centralizes invalidation on removal/clear; status removals invalidate
at departure, reconnect cleanup and rotation migration. Native intent:
`FedSrv/FedSrv.CPP:10750-10876` (Show/Hide/export before status),
`FedSrv/fsship.cpp:237-260,287-291` (spotted/hidden state).

`[gcprof]` now reads the precise global allocation counter once at each report
boundary, before formatting. That exact endpoint becomes the next baseline;
report allocations therefore belong to the following window. Previously the
approximate counter could stay stale and jump between windows, and the second
read after report formatting discarded reporting allocations. Pause and generation
counters likewise reuse one endpoint. Precise allocation reads occur only at
window boundaries. Per-step allocation attribution remains approximate, as before;
it is not an exact allocation trace. The new regression allocates on a worker
thread in four consecutive windows and allocates another MiB during each report:
subsequent windows must include both worker and report allocations. Copied alone
onto the baseline profiler in scratch, that regression fails; the fixed profiler
passes it in all three final twelve-case invocations.

No new runtime flags, services, stores, ports or environment settings were added.
The legacy projection switch is an internal testing seam, not configuration.
Parity evidence was appended to SIM-0066, SIM-0470, SIM-0824 and SIM-0825, and the
glossary names the new scheduling/ranking helpers.

## Local measurements

Intel Core Ultra 9 285HX, 24 logical CPUs; .NET SDK 10.0.400, Release JIT, workstation/concurrent
GC in the test/bench processes. No synthetic CPU load. The full requested
400-ship load runs locally. These are separate simulation and projection
measurements, not a synchronized end-to-end tick, a bot-AI benchmark or a rig TiDi
measurement. Local verification processes, including real showcase bots, were
also running; paired arms alternate order to reduce order/load bias.

The existing `tools/P835Bench` needed two fixture corrections in **both** arms:
use the production `PCore001.igc`, and use negligible salvo/mine damage to retain
the ships. Previously random lethal salvos eliminated the ships during warm-up,
leaving an empty-sector benchmark. Profiler configuration also moves after warm-up
so warm-up time is not folded into the first recorded tick. Both arms finish
with **400 ships and 240 projectiles**. Collision geometry and damage dispatch
still execute. The baseline is the dispatch source exported into scratch, with
only the two timing scopes and identical corrected harness added.

Each sector arm has 60 warm-up ticks and 500 measured ticks, seed 835,
400 ships / 240 sustained projectiles / 60 initial mines, `m2xspiderw.igc`.
Mean below is amortized per measured tick. For conditional scopes such as the
10-pass visibility sweep, p95 is over ticks where that scope ran. Values are
medians of three independent processes per arm, not a pooled percentile.

| Sector/update bucket | Before mean ms | Before p95 ms | After mean ms | After p95 ms |
|---|---:|---:|---:|---:|
| Mission.Update wall | 3.72 | 6.32 | 1.87 | 5.72 |
| `step:update` | 3.70 | 6.30 | 1.85 | 5.69 |
| `sector:1052` | 3.66 | 6.21 | 1.79 | 5.64 |
| `sim.models` | 2.73 | 4.21 | 0.67 | 1.59 |
| `blast.apply` (inside models/combat) | 2.50 | 4.00 | 0.42 | 0.89 |
| `sim.combat` | 0.67 | 1.48 | 0.61 | 1.39 |
| `combat.detect` (inside combat) | 0.49 | 1.09 | 0.51 | 1.09 |
| `sim.visibility` | 0.17 | 9.26 | 0.23 | 9.39 |
| `sim.mining` | 0.13 | 0.41 | 0.16 | 0.40 |
| `sim.collision` | 0.06 | 0.22 | 0.08 | 0.18 |
| `sim.prepare` | 0.01 | 0.02 | 0.02 | 0.03 |

Mission.Update mean/p95 by run: before **4.37/7.52, 3.72/5.82, 3.65/6.32 ms**;
after **1.87/5.72, 1.85/5.66, 2.11/6.22 ms**. The model/blast reduction is clear;
visibility and GC tails still limit p95. Nested buckets must not be added to
parents. There is no measured combat-detection improvement claimed.

The existing recorded-snapshot projection seam now includes optional discovery
and times all three sweeps. `Recorded_clash_projection_measures_three_runs_of_both_arms`
uses 400 ships and 400 viewers, alternating sides, real map rocks/warps,
`shipUpdateModel=native`, minUpdateHz=2, immutable snapshot + encode-once,
parallelBuild=true/maxPar=15. Each arm warms 30 ticks and measures 150, flushing
each snapshot. This measures established clash traffic after initial catch-up;
first-sighting and visibility-edge bursts still visit all recipients and deliver
all required messages. Their five-second-window effect belongs in the rig A/B.
The reference arm retains full stable ranking and the original
per-recipient sweeps; both arms share the candidate filtering buffer, so this
comparison conservatively excludes that buffer's allocation saving.

| Projection step | Before mean ms | Before p95 ms | After mean ms | After p95 ms |
|---|---:|---:|---:|---:|
| `step:crossSideExportSweep` | 2.861 | 3.144 | 0.166 | 0.368 |
| `step:crossSideDiscoverySweep` | 1.379 | 1.555 | 0.042 | 0.061 |
| `step:crossSideShipStatusSweep` | 3.040 | 3.417 | 0.316 | 0.665 |
| `step:broadcastShipsUpdate` | 16.954 | 19.288 | 11.485 | 12.852 |

Per-run mean/p95 milliseconds (before -> after):

| Run | Export | Discovery | Status | Ships |
|---|---|---|---|---|
| 1 | 2.822/3.076 -> 0.166/0.368 | 1.337/1.465 -> 0.042/0.071 | 2.892/3.266 -> 0.307/0.642 | 16.340/17.787 -> 11.485/12.779 |
| 2 | 2.861/3.144 -> 0.152/0.332 | 1.379/1.555 -> 0.038/0.061 | 3.040/3.417 -> 0.316/0.665 | 16.954/19.288 -> 11.447/12.852 |
| 3 | 2.983/3.442 -> 0.246/0.479 | 1.500/1.699 -> 0.044/0.059 | 3.316/4.228 -> 0.522/1.099 | 18.076/23.418 -> 18.008/33.962 |

The ranking remains serial within the clash sector because later peers consume
its updated deviation. Encoding remains in the existing detached publisher;
sharing records/encoding was already implemented before P863.

## Proof and verification

`P863ClashBudgetTests` exercises 200 ships/viewers for 125 generated input ticks
in native mode, with both serial/ranked-only and parallel/2 Hz floor settings.
It compares **every byte of every per-peer packet**, tick, delivery key, guarantee
flag and all 25,000 per-tick ship-deviation values. Each setting compares 610,589
messages; respective payload totals are 35,443,335 and 54,268,947 bytes. Coverage
includes target promotion, equal deviations, cloaking/visibility edges, docking,
side changes, recipient departure/late join, explicit latch eviction, static
asteroid/warp discovery, probe/treasure creation and removal, and refresh phases.
Additional tests cover stable bounded selection including NaN/infinite deviations
and duplicate references, uint phase wrap, negative IDs, repeated latch clearing,
audience churn and update-snapshot membership mutations/duplicates.

- Final solution build: **0 warnings, 0 errors** (`build-final.log`).
- All **91 tool projects** built successfully, **0 warnings/errors** (`tools-build-clean.log`).
- Server **linux-x64 NativeAOT publish succeeds**, with no warnings/errors (`aot-final.log`).
- All **12 final new cases pass 3/3 alone**: 12/12 each invocation, approximately 36.7 / 33.7 / 33.8 seconds including the paired measurements.
- Targeted existing broadcast/cadence/native-update/blast/profiler regression set: **98 pass / 4 pre-existing golden-prefix failures**, detailed below.
- Exactly **one Fast sweep**: **11,275 pass / 1 fail / 3 skip**, 350.5 s; infrastructure and coverage checks pass. It is **not a green Fast gate**. The failure is the known read-only profile-editor configuration path; the unchanged ten-case class passes with writable scratch configuration. The timing guard additionally flags P750's corpus test (11.238 s; unchanged seven-case class passes 3/3 alone) and the new 200-peer proof (12.971 s, every assertion passed). The final crowded proof is classified Perf, consistent with TT-1's existing crowded snapshot tests; the smaller selector/cache invariants remain Fast. The original sweep result is retained, with no second Fast invocation. See `docs/TEST_FLAKES.md`.
- Showcase never-parks: **5/5 completed passes on the final server binary, each covering two matches; durations 209.34 / 187.45 / 240.76 / 231.90 / 198.72 seconds. An earlier pass and an interrupted partial invocation are excluded**.
- Glossary links, parity scorecard and `git diff --check`: **pass**.

The four pre-existing `BroadcastSnapshotTests.Recorded_ticks_have_identical_per_peer_bytes_order_and_lanes`
golden-prefix checks fail identically on untouched dispatch source. The starting
revision was exported to scratch and run independently; its four observed hashes
match P863 exactly. The off/on/encode-once equality checks preceding the golden
assertions pass. The unrelated NT-2 golden baseline was left untouched.

| Mode / parallel / floor | Expected prefix | Dispatch and P863 observed prefix |
|---|---|---|
| port / false / 0 | BF92D4A2003D66CC | D0ED7EEB37594AF5 |
| port / true / 2 | D74C1E1854875051 | B16780AEF82384FC |
| native / false / 2 | 3C1876D181A6A011 | 227A25DD0857FB42 |
| native / true / 3 | E06266AEDF1980C0 | 420FA677AEEA2B8F |

Reproduction (use the configured external artwork corpus):

```sh
~/.dotnet/dotnet build AllegianceCore.slnx
DOTNET=~/.dotnet/dotnet tools/build-tools.sh
~/.dotnet/dotnet publish src/Allegiance.Server -c Release -r linux-x64 -o .scratch/p863/aot
~/.dotnet/dotnet run --project tools/P835Bench -c Release -- 400 240 60
~/.dotnet/dotnet test tests/Allegiance.Core.Tests -c Release --filter FullyQualifiedName~P863ClashBudgetTests
DOTNET=~/.dotnet/dotnet tools/run-tests.sh --tier fast
~/.dotnet/dotnet test tests/Allegiance.ClientCore.Tests -c Release --filter FullyQualifiedName~P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch
```

Raw local evidence is git-excluded under `.scratch/p863/`: `final-sector-{before,after}-{1,2,3}.log`,
`verified-new-tests-{1,2,3}.log`, `regression.log`, `baseline-goldens.log`,
`showcase-{1,2,3,4,5}.log`, `build-final.log`, `aot-final.log`, `tools-build-clean.log`,
`fast.log`, `profile-isolated.log` and `counter-before-regression.log`.
`showcase-4-interrupted.log` retains a partial run from the multi-run shell that
ended with SIGTERM; it is not counted as a completed test. `showcase-1-early.log`
retains an additional early pass, also excluded from the final five. The final
showcase binaries match the verified server DLL SHA-256
`8f5dd9613d4889791fa63ff0c9831bcbea02c5e7a0a297ace90e7c14eaf52bc0`.
 The one fast invocation also writes its
normal `TestResults/tiers-20261028T155234.320355Z/` evidence. Writable
`NUGET_HTTP_CACHE_PATH` and `XDG_CONFIG_HOME` directories in scratch resolve the
sandbox's read-only home cache/configuration paths; no runtime deployment changes
are involved.

## Native intent and a modern approach

**Classification: behavior-preserving performance adaptation.** Native updates
models and collision work serially (`Igc/clusterIGC.cpp:243-348`), ranks cluster
ships before assembling per-player packets (`FedSrv/FedSrv.CPP:3923-3950,4029-4188`)
and exports/statuses on visibility events. It is inaccurate to claim native was
strictly limited to tiny games: `FedSrv/FedSrv.H:19-26` allows 500 players plus
500 drones per server and 200 players per standalone game; non-standalone's game
limit is 0x7fff. `Igc/igc.h:1334,1452` defaults to 100 per team and validates up
to 200. Those capacities do not establish a performant sustained 200v200 clash.
The port also performs client visibility/rendering compensation and per-viewer
pose floors that native's local client simulation did not require. This change
preserves those established semantics; it does not restore native's different
random deviation jitter, quotas or simulation phase ordering.

A better modern approach is to retain ordered simulation and packet semantics
while eliminating repeated work: explicit membership indexes, bounded selection,
versioned audiences and event-invalidated recipient work, followed by immutable
publication. Further work should use the rig's new attribution to decide whether
visibility, cadence bookkeeping or combat geometry is actually next. Moving the
same repeated scans to Rust would not remove their scaling cost. Broader parallel
simulation or changed update rates would require separate behavioral evidence.
