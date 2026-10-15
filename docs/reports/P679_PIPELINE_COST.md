# P679 — broadcast pipeline cost

All three flags now beat the flags-off producer baseline at 256 and 512 peers,
while retaining the payload savings. At 512, the measured combined producer and
publisher allocation is 0.0686 MiB/tick. Changes remain uncommitted in the working
tree; no push was made.

## Changes and ownership

The all-flags-on tier path writes selected records directly into reusable,
capacity-bucketed packet leases. One inline value record captures each selected
ship's common HEAVY bytes per tick. A tight value-record loop copies those bytes
and writes the viewer's six-byte relative position. It retains each viewer's
actual position reference; distinct reference frames are never merged into an
approximate cohort. No live ship, orientation, mission, collection or callback
is retained by a leased packet. Its CRC is computed by the publisher.

Per-viewer candidate storage grows with population and is then reused. Cadence
and age tracking classify each visible pair once. Only due contacts enter the
sort; a heap sort with an explicit original-index tie break preserves stable
tier/deviation order without allocating comparer delegates. Skipped deviation
bumps commute, so moving them before the due-only sort preserves the old
selection. Target promotion remains first and immediate.

A 500-unit spatial grid supplies conservative cell-pair distance bounds. A
cached tier certificate remains valid while cells, sides, alliances and weapon
reach match. Exact distance is needed only where cell bounds cross a radius.
Targets and allies take their direct policy paths. Out-of-grid and non-finite
coordinates use the exact classifier; cell bounds include a conservative margin
for the legacy single-precision distance arithmetic. This is incremental
membership with a conservative spatial certificate, rather than delayed tier
transitions: every boundary still uses the exact P677 policy. Per-pair age
sampling and cadence checks remain O(viewers × visible ships); this work does
not claim to eliminate that tracking requirement.

Owner SINGLE poses, statuses, warnings, mining state, kill bonus, missile-track
warnings and lead indicators use leased packets too. Their bytes and cadence
remain unchanged. Audience lists and peer snapshots reuse storage. Score rosters
check their existing refresh deadline before constructing entries. Verbose
status and ship diagnostics now check the logging flag before building strings.
Cross-side status packets are constructed only for due deliveries. Identity
refresh packets reuse detached immutable data until ship identity, name, side,
hull or loadout version changes; departed model entries are pruned. The uncommon
generic message path remains available for all other messages.

A packet lease has one delivery owner. Coalescing transfers ownership of retained
ordered deliveries and releases superseded stream leases. The publisher releases
leases after the complete encode/send pass. Publication capture hooks receive
owned copies because those hooks can retain arrays. The size-only benchmark
observer avoids those diagnostic copies.

The transport still synchronously copies packet bytes before returning from
Send. Its frame buffers now use ArrayPool with an explicit valid length; only
that length is transmitted and accounted. Superseded and shed queue frames are
returned immediately. The sole writer retains an in-flight buffer through
partial writes and would-block responses, returning it after complete delivery.
Close releases queued buffers and never returns storage while a writer may
still be sending it. Queue nodes are reused with a per-peer cache capped at 512. The private frame
pool also caps retention at 512 arrays per size bucket (up to 1 MiB arrays);
larger frames retain their existing send/limit behavior. Private pools avoid
cross-thread contention and repeated misses in the shared pool during status
bursts.

## Profile and allocation target

Initial source and loopback profiling identified four costs:

- Repeated per-viewer LINQ filtering, stable ordering, candidate list copies,
  reclassification, record wrappers and entry arrays.
- Per-viewer deferred HEAVY encoding, intermediate message buffers and CRC packet
  buffers, followed by a separate allocated transport frame.
- Small per-owner DTOs, message/packet wrappers and arrays every tick, plus
  repeated status packets and strings even with verbose logging disabled.
- Rebuilt audience dictionaries/lists and rosters constructed before checking
  their refresh deadline.

Stage counters use GC.GetAllocatedBytesForCurrentThread around the complete
broadcast suffix. Publisher counters cover coordinator and encode-worker
threads, including synchronous transport enqueue work. Client receiver
allocations and transport drain-worker allocations are outside those counters.
Producer iteration includes the replay harness pose advancement and scheduled
keepalives, as in the P677 measurement.
The fixture measures the same boundary used by the P677 report. Packet and
transport storage retain capacity after warm-up; this trades bounded peak
storage for much less GC traffic.

The 512-peer allocation regression runs a started mission, warms 120 ticks, then measures 100 moving
steady ticks, including periodic refreshes. It requires producer <104,000 bytes,
publisher <16,000 bytes, and their **combined total <104,857 bytes/tick**
(0.1 MiB). It writes the per-stage breakdown to
[P679_assets/allocation.json](P679_assets/allocation.json).
The indexed side-global-attribute lookup also removes boxed side-list
enumerators: Debug initially spent 122,880 bytes/tick on those alone. Release
had elided those boxes, so this was found by the strict Debug regression rather
than inferred from Release timings. The fix preserves lookup order and factors;
no assertion threshold or snapshot guard was disabled.

The producer still allocates immutable LiveShip owner-state replacements and
some uncommon periodic records. The claim is the numeric steady-state budget,
not literal zero allocation across every broadcast category or population churn.

## Native rules preserved

These native locations were inspected locally under
`~/Development/Allegiance/src/`. Pooling, spatial certificates and configurable
interest tiers are managed implementation policies.

| Rule | Native evidence |
| --- | --- |
| Ship global attributes resolve from the owning side | `~/Development/Allegiance/src/Igc/shipIGC.cpp:4528-4546` |
| Larger deviation has priority | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3614-3621` |
| An in-cluster viewer source position is the position reference | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4127-4132` |
| Command target occupies the first heavy slot | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4141-4146` |
| Own ship and promoted target are excluded from other entries | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4148-4154` |
| HEAVY adds relative kinematics to the LIGHT common fields | `~/Development/Allegiance/src/Igc/igc.h:2423-2442` |
| Time is a two-byte signed reference offset | `~/Development/Allegiance/src/Igc/igc.h:2178-2195` |
| Relative position is three signed square-root deltas | `~/Development/Allegiance/src/Igc/igc.h:2198-2247` |

Wire identity is independently pinned by the pre-P677 four-configuration
transcript hashes, rather than relying only on two current implementations
agreeing. The new span writer test checks 200 changing poses/rotations against
the canonical codecs. A stalled-publisher regression exercises pooled ordered
leases and stream replacement. A transport regression sends varying lengths,
mutates caller storage immediately, and checks exact receipts in both transport
modes.

## Measurements

Results and raw CSVs are under [P679_assets](P679_assets/). The Release benchmark
uses real loopback TCP connections, seating, send queues, four publish workers,
four transport workers, two sides, one sector, and the P677 250-unit grid. It
warms 20 ticks and measures 120 moving projection ticks at a recorded 20 Hz clock.
This is the complete broadcast suffix, not a full advancing combat simulation.
Receivers share the test process, so wall timings include their contention and
GC pauses even though receiver allocations are outside the per-thread counters.
TCP payload excludes frame headers and TCP/IP overhead; the drain barrier is
excluded. Age histograms are selection ages, not end-to-end delivery ages.

A clean HEAD source export under `/tmp/p679-baseline` uses the same replay and
size-only observer, providing all three **before** configurations. It is built
without making a commit or modifying Git state. Before cells are one session per population; after cells are medians of three
sessions at each peer count. Times come from a shared machine; iteration p95 and
raw per-viewer payloads remain in the JSON.

| Peers | Flags | Producer before mean / p95 ms | Producer after mean / p95 ms | Publish before / after mean ms |
| --- | --- | --- | --- | --- |
| 256 | off | 22.65 / 25.34 | 20.67 / 23.53 | 0.00 / 0.00 |
| 256 | P668 + P669 | 15.87 / 18.46 | 11.11 / 13.41 | 4.39 / 3.69 |
| 256 | P668 + P669 + tiers | 21.84 / 24.40 | 11.83 / 12.63 | 3.91 / 2.05 |
| 512 | off | 93.82 / 99.92 | 80.83 / 86.31 | 0.00 / 0.00 |
| 512 | P668 + P669 | 84.07 / 101.22 | 60.91 / 69.67 | 20.08 / 17.48 |
| 512 | P668 + P669 + tiers | 100.34 / 115.87 | 49.64 / 52.59 | 15.11 / 5.83 |

| Peers | Flags | Producer before → after MiB/tick | Publish before → after MiB/tick |
| --- | --- | --- | --- |
| 256 | off | 10.7029 → 7.3484 | 0.0000 → 0.0000 |
| 256 | P668 + P669 | 4.4956 → 3.3298 | 5.3934 → 2.6745 |
| 256 | P668 + P669 + tiers | 5.4145 → 0.0339 | 3.3211 → 0.0028 |
| 512 | off | 40.7962 → 28.9047 | 0.0000 → 0.0000 |
| 512 | P668 + P669 | 16.7723 → 13.1307 | 20.8506 → 10.7533 |
| 512 | P668 + P669 + tiers | 19.0122 → 0.0646 | 11.0030 → 0.0040 |

Off-path improvements are shared: transport frame/node reuse, audience reuse,
lazy cross-side status construction and the roster deadline check also reduce
the final flags-off cost. Snapshot/encode-once without tiers retains the
legacy per-viewer ranked/floor pose representation.

| Peers | TCP off / tiers before MiB | TCP off / tiers after MiB | After paired TCP reduction | Combined all-on allocation MiB/tick |
| --- | --- | --- | --- | --- |
| 256 | 260.26 / 121.14 | 260.26 / 121.14 | 53.5% | 0.0368 |
| 512 | 1050.45 / 424.16 | 1050.45 / 424.16 | 59.6% | 0.0686 |

All flags on is cheaper than the final flags-off producer baseline:

- 256 peers: **11.83 vs 20.67 ms**, a 42.8% reduction.
- 512 peers: **49.64 vs 80.83 ms**, a 38.6% reduction.

All six after sessions retained their seated population with zero snapshot
coalescings and zero publisher failures. T0/T1/T2 maximum selection ages remain
0.05/0.20/0.50 seconds, with no never-served pairs or pairs over one second after
warm-up. The selected relative-slot counts remain about 23,009 / 81,052 per tick
at 256 / 512; their surrounding allocation and encoding work is removed.

At 512 peers the mean is close to the 50 ms nominal tick budget and p95 exceeds
it. These numbers establish lower broadcast cost; they do not establish 20 Hz full
simulation capacity for 512 combatants.

## Verification

Both Debug solution and Release Core-test builds precede validation. After the
final grid-boundary fallback, all affected tier/combat/cost methods were repeated
three times again; unchanged method results are retained in the same ledger.
[final-build-reruns.json](P679_assets/final-build-reruns.json) names that additional rerun set. The runner
selects all 80 P677 new/affected methods, the new cost/lifetime/spatial/codec
methods and benchmark, and the affected identity/status, logging, TCP, send-worker, priority, liveness and
egress methods: 281 methods total. It runs each method three times in its own
dotnet-test invocation, then unfiltered Core, ClientCore and MonoGame assemblies,
including live/loopback lanes. A failed invocation triggers three additional
isolated runs; 3/3 passes identify a load flake per `docs/TEST_FLAKES.md`.
Explicit tier-enabled local lifecycle and 15v15 showcase runs are also included.
No rig/221/200v200 lane is run.

All **281 selected methods** completed **three separate method-filtered invocations**
(843 invocations; 1002 case executions). No selected case was skipped.
The new allocation, cache invalidation, spatial, span-codec and stalled-lease
regressions passed 3/3. The saved four-configuration packet transcript matched
on each repetition. The final Debug and Release builds had 0 warnings and 0 errors.

| Unfiltered assembly | Passed | Failed in assembly | Existing skips |
| --- | --- | --- | --- |
| Core | 4357 | 0 | 0 |
| ClientCore | 939 | 0 | 1 |
| ClientCore.MonoGame | 2125 | 0 | 3 |

All six explicit tier-enabled local lifecycle/showcase invocations passed.
Both three-session Release measurement sets passed. No rig/221/200v200
scenario, new skip, quarantine change, assertion relaxation or deadline
relaxation was used.

One exploratory coalescing timing failure passed 3/3 fresh audits and 3/3
final repetitions; see [the audit](P679_assets/exploratory-flake.json).

The final Debug allocation regression measured **72,558 producer +
4,118 publisher bytes/tick**
(**0.0731 MiB/tick combined**).
Most residual producer bytes are immutable owner LiveShip replacements;
the remote pose stage itself allocated 152 bytes/tick.
Raw results are in [isolated.json](P679_assets/isolated.json),
[suites.json](P679_assets/suites.json), [enabled.json](P679_assets/enabled.json),
and [measurements.json](P679_assets/measurements.json).

## Defaults and reproduction

For crowded **port-mode** sessions, recommend all three flags **on**, with the
existing full-rate T0, 5 Hz T1, and max(2 Hz, minUpdateHz) T2 settings. The measured
producer is 42.8% cheaper at 256 peers and 38.6% cheaper at 512 than the final off
path, while retaining 53.5% / 59.6% TCP payload savings. The combined measured
allocation at 512 is 0.0686 MiB/tick, below the 0.1 MiB target.

Keep the source configuration defaults opt-in until the separate rig lane
confirms deployment capacity and the native-mode traffic tradeoff. This work
establishes a cheaper pipeline for the measured port workload; 49.64 ms at 512
still needs CPU headroom before promising a full 20 Hz simulation at that size.
Native mode retains its byte/determinism guarantees but has no new crowded
bandwidth-capacity measurement here.

Recommended crowded port configuration:

```json
{"broadcast":{"shipUpdateModel":"port","immutableSnapshot":true,"encodeOnce":true,"interestTiers":true,"tier1Hz":5,"tier2Hz":2,"minUpdateHz":2}}
```

Configuration switches remain opt-in so existing deployment settings and
explicit off paths retain their established behavior. The tier switch implies
snapshots and encode-once. Reproduce with:

```sh
dotnet build -m:8 -p:UseSharedCompilation=false -v:q
dotnet build tests/Allegiance.Core.Tests -c Release -m:8 -p:UseSharedCompilation=false -v:q
python3 docs/reports/P679_assets/validate.py
```

Set `P679_INCLUDE_BEFORE=1` with the clean source export described above to
repeat the before measurements; otherwise the runner preserves the archived
before ledger. Source paths for
artwork and native references come from this workstation's `local.env`.
