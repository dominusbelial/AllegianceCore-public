# P669 — encode-once ship-state fan-out

Changes are left in the working tree; no commit or push. P668 is the implementation
base. The architecture document named in the brief is absent on this checkout,
as already recorded by P668.

## Implementation and ownership

`broadcast.encodeOnce` defaults to **false**. The override
`ALLEGIANCE_BROADCAST_ENCODE_ONCE=1|true|0|false` takes precedence over JSON;
whitespace/case are accepted and other values fail boot. Enabling it requires
`broadcast.immutableSnapshot` (or `ALLEGIANCE_BROADCAST_SNAPSHOT=1`). The dependency
is validated before transport startup. Both resolved flags appear in boot output
and the profiler label.

The sim thread retains audience selection, sector/side visibility, target
promotion, the order-dependent deviation ranking, P632 density budgets and P659's
per-viewer floor. The floor now uses the same capture function as ranked updates,
so its extra heavy records participate in sharing too.

A snapshot-local cache maps **ship object identity and update time** to an owned
heavy record. A viewer's selected list contains references to these records with
that viewer's time/position references. The first encoder worker materializes a
39-byte common template under a lock; subsequent workers copy bytes 0–7 and
16–38 and encode only the eight time/position bytes against their exact original
references. Position subtraction and signed-square-root quantization still use
the original captured floats; quantized positions are never subtracted or
reconstructed to form another viewer's record.

The light tier caches each selected ship's quantized eight-byte record and its
encoded buffer once per tick. Each viewer's light list carries that buffer by
reference. The value type retains its original equality/hash behavior; a `with`
copy that changes fields cannot reuse stale cached bytes.

`broadcastShipStates` already shares one `BroadcastMessage` per moving teammate
in P668/P317. Its existing lazy buffer memoization and per-side packet sharing
remain in use. Own single-ship records stay separate because their ammo, fuel,
energy and mining/endurance fields can differ from remote/heavy records. There
is no claim of eliminating encodes that P668 had already eliminated.

Caches are cleared when capture begins and when the tick seals. Cache keys with
live Ship references never reach publication. Published DTOs contain detached
P668 pose snapshots (including copied orientation), references to common wire
records, and scalar viewer references. Managed references keep shared buffers
alive through active/pending snapshot coalescing and reliable-delivery carryover;
they are reclaimed when the last delivery is released. Recycled snapshot
containers never own reusable mutable pose buffers. No global cache, refcount
protocol, or borrowed pool buffer can overwrite an in-flight record.

**Remaining per-viewer work:** ranking, floor scheduling, selected-record order,
self exclusion, visibility, target and own fractions, heavy/light headers,
relative-position quantization, message/packet copies and CRC. Equal sector and
side do **not** imply equal streams. The reduction is O(ships) full heavy record
encodes, with O(selected viewer–ship pairs) relative slots/copies and packet
framing. This does not turn all broadcast work into O(ships).

Per-server counters expose full heavy/single/light record quantizations and
relative-slot encodings separately. The snapshot metrics also report publishing
allocation bytes. Allocation measurement sums publisher-coordinator and encoder
worker thread deltas, avoiding double counting when the coordinator participates
in `Parallel.For`; it excludes transport worker/receiver allocations.

## Native rules retained

These sources were inspected in the local native checkout. This is a new managed
encoding optimization, not a claim that native implemented this cache. P632's
density budget and P659's pose floor remain port adaptations.

| Rule | Native file:line |
| --- | --- |
| LIGHT contains id, state and controls | `~/Development/Allegiance/src/Igc/igc.h:2423` |
| HEAVY extends LIGHT with relative time/position and common kinematics/fractions | `~/Development/Allegiance/src/Igc/igc.h:2431` |
| Signed time delta | `~/Development/Allegiance/src/Igc/igc.h:2184` |
| Position delta is calculated from original position minus viewer reference | `~/Development/Allegiance/src/Igc/igc.h:2243` |
| Viewer reference is the source ship position in the viewed cluster | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4127` |
| Fixed heavy/light quotas, deviation sort and reset/bump bands | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3844`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3923`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3937` |
| Command target occupies the first heavy slot | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4141` |
| LIGHT follows HEAVY in the same non-guaranteed send | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4183`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4189` |
| CRC follows concatenated message bytes | `~/Development/Allegiance/src/_Utility/Messages.cpp:574` |

## Byte identity

The P668 recorded-input fixture now replays three paths in the same server:
eager encoding, immutable snapshot without sharing, and immutable snapshot with
sharing. All per-peer bytes, CRCs, tick/order, coalescing keys and guaranteed flags
are compared. The 105-tick, 144-peer fixture exercises both update models,
serial/parallel projection, floor rates 0/2/3 Hz, mixed sides, per-viewer targets,
docking, moving/rotating poses, changing shields/visibility, loadouts and an NPC,
plus station/asteroid/probe/mine/missile traffic.

A second three-way replay uses 12 actual TCP players and 96 ships for 30 ticks,
mutates live positions immediately after submission, and drains through a
stream-tail barrier. It compares packet bytes and order within each pre-existing
transport lane. Cross-lane socket interleaving remains scheduling dependent, as
in P668; exact global order is checked at the publication boundary.

The new shared-record test encodes 256 different viewer references concurrently,
including saturated position deltas and wrapped time references. It compares each
record to the original codec after live ship mutation, requires exactly one full
heavy encode, and verifies that encoding a later pose cannot change earlier
bytes. Snapshot-detachment guards, stalled publisher/coalescing and shutdown
checks remain active.

## Recorded results

| Model | Parallel | Floor Hz | Packets | Bytes | Full heavy encodes, snapshot-only → shared | SHA-256 prefix (all three equal) |
| --- | --- | --- | --- | --- | --- | --- |
| port | false | 0 | 276,184 | 77,143,800 | 120,960 → 15,225 | `E9818924260ED254` |
| port | true | 2 | 276,184 | 83,832,573 | 292,467 → 15,225 | `1C4519215B2B37DF` |
| native | false | 2 | 261,064 | 22,996,941 | 264,739 → 15,225 | `3C2919674F7E403C` |
| native | true | 3 | 261,064 | 27,521,994 | 380,766 → 15,225 | `27B42CC5C6DE7E55` |

The real TCP replay matched 4,851 packets / 445,752 bytes per path, in each existing lane. Raw hashes/counts are in [identity.json](P669_assets/identity.json) and [tcp-identity.json](P669_assets/tcp-identity.json).

## Crowded TCP measurements

Environment: Ubuntu 26.04, Intel Core Ultra 9 285HX (24 CPUs), .NET SDK 10.0.400/runtime 10.0.11. Same connected session per pair; port update model, two sides, one crowded sector, 2 Hz floor, four encoder workers and four transport send workers. Each interval warms for 20 ticks and records 120 ticks at a requested 20 Hz. Transport is drained before/after measurement. The controlled replay advances poses/cadence, not Mission.Update.

The tables show medians of **three paired Release sessions** at each population. All six paired sessions assert equal published packet counts and byte totals. Timing is a local shared-machine observation; packet encoding, producer projection and transport publication overlap. These numbers are not a full-simulation or combat throughput claim.

| Players | Encode once | Heavy / single encodes per tick | Relative slots per tick | `step:broadcast*` ms | Publish pool ms |
| --- | --- | --- | --- | --- | --- |
| 256 | off | 7,470.1 / 512 | 0.0 | 11.86 | 4.92 |
| 256 | on | 256.0 / 512 | 7,470.1 | 13.64 | 4.21 |
| 512 | off | 33,851.6 / 1,024 | 0.0 | 74.43 | 34.09 |
| 512 | on | 512.0 / 1,024 | 33,851.6 | 59.93 | 18.20 |

LIGHT is unused in this port-mode crowded benchmark; native LIGHT sharing is covered by the recorded native A/B cases. Full heavy encoding drops to at most one record per selected ship each tick. Single-ship counts stay unchanged because P668 already shared teammate records. Relative-slot counts explicitly expose the work that remains per viewer.

| Players | Encode once | Projection mean / p95 iteration ms | Producer allocations MiB/tick | Publish allocations MiB/tick | Egress MiB/tick | Packets/tick |
| --- | --- | --- | --- | --- | --- | --- |
| 256 | off | 13.58 / 16.49 | 5.63 | 6.27 | 2.168 | 2,668.9 |
| 256 | on | 15.26 / 18.07 | 4.49 | 5.39 | 2.168 | 2,668.9 |
| 512 | off | 100.68 / 132.36 | 22.07 | 24.92 | 8.754 | 6,791.1 |
| 512 | on | 77.08 / 95.46 | 16.76 | 20.85 | 8.754 | 6,791.1 |

Projection mean is the measured snapshot suffix; p95 is the complete harness projection iteration, including pose advancement and snapshot setup. Producer allocations cover that iteration. Publish allocations include encoding and queue publication on the coordinator/workers, excluding transport workers and client receivers. These are measured thread deltas, not process-wide allocation estimates.

All final runs retained the seated population, used three recycled snapshot containers, and reported zero publisher failures and zero snapshot coalescings. Egress volume is unchanged at the publication boundary. Socket receive counts may still differ slightly because transport streams coalesce and ordered traffic has priority; the drained identity replay proves packet contents/order separately.

At 256 players, median projection time increased from 13.58 to 15.26 ms even as publish time and allocation fell. At 512 it decreased from 100.68 to 77.08 ms. Cache lookups and viewer wrappers remain on the producer; the full-record encoding reduction does not guarantee a faster projection at every population.

Raw per-run counters and timings are in [performance.json](P669_assets/performance.json); all 12 profiler CSVs are in [P669_assets](P669_assets/). The implementation still allocates viewer wrappers, arrays, DTOs and final packet buffers. Packet assembly/copies, CRC, ranking and floor selection remain proportional to the selected viewer–ship pairs. Keep the requested default **off**.

## Validation

Final Debug solution and Release Core-test builds completed with **0 warnings and 0 errors**. The 64 selected new/affected methods each ran three times in a separate method-filtered invocation: **258 passed case executions**, 0 failures and 0 skips. This includes live/loopback, pose codecs, floor/budget/native/parallel behavior, teammate state, shields, mining and endurance. Final Release crowded measurement cases additionally passed **3/3 at 256 players and 3/3 at 512 players** after the heartbeat harness correction.

| Unfiltered suite | Passed | Failed | Existing skips |
| --- | --- | --- | --- |
| Core | 4,329 | 0 | 0 |
| ClientCore | 933 | 0 | 1 |
| MonoGame | 2,125 | 0 | 3 |

The suites ran sequentially, unfiltered, including their live/timing lanes. Core used `P669_MEASURE=1` to exercise the updated measurement path. The default-gated P660 local 15v15 showcase was also explicitly enabled and run three times with both broadcast flags enabled; the real-socket join/leave churn test likewise ran three times with both flags enabled. These are local scenarios, not rig work. Optional private Meshy-pack checks remain unavailable when that pack is unconfigured.

The full method list, case totals, suite skip/failure names and enabled lifecycle outcomes are in [validation.json](P669_assets/validation.json). No tests were weakened, newly quarantined, or skipped to obtain a passing result. No commit or push was made.

## Reproduction

Configuration:

```json
{"broadcast":{"immutableSnapshot":true,"encodeOnce":true}}
```

Equivalent overrides are `ALLEGIANCE_BROADCAST_SNAPSHOT=1` and
`ALLEGIANCE_BROADCAST_ENCODE_ONCE=1`. Keep the default off for ordinary boot.
The workstation's `local.env` supplies external corpus paths automatically.

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p669-nuget-http dotnet build -m:8 -v:q
dotnet test tests/Allegiance.Core.Tests --no-build --filter 'FullyQualifiedName~BroadcastEncodeOnce|FullyQualifiedName~BroadcastSnapshot'
NUGET_HTTP_CACHE_PATH=/tmp/p669-nuget-http dotnet build tests/Allegiance.Core.Tests -c Release -m:8 -v:q
P669_MEASURE=1 P668_PEERS=256 dotnet test tests/Allegiance.Core.Tests -c Release --no-build --filter 'FullyQualifiedName~Crowded_real_TCP_session'
P669_MEASURE=1 P668_PEERS=512 dotnet test tests/Allegiance.Core.Tests -c Release --no-build --filter 'FullyQualifiedName~Crowded_real_TCP_session'
P669_MEASURE=1 dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
ALLEGIANCE_BROADCAST_SNAPSHOT=1 ALLEGIANCE_BROADCAST_ENCODE_ONCE=1 ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms'
```

`P669_MEASURE=1` selects the controlled benchmark extension of P668's
crowded TCP test: both measured modes use snapshots, and only encode-once changes.
It fixes input poses/cadence and resets projection state between intervals. This
measures the **broadcast suffix**, not a complete advancing simulation tick;
`step:broadcast*` and publish-pool measurements use the actual production builders,
encoder and transport. Ordinary P668 measurement behavior remains available when
this test-only selector is absent.

The first uncontrolled interval experiment was discarded because evolving
wall-time cadence yielded different egress volumes. An initial controlled
512-player experiment also exposed an existing harness gap: its raw peers sent
nothing after logon and exceeded the transport's 30-second inbound-frame deadline
during a long run. The benchmark now sends keepalives, including at drain
barriers. It does not relax transport deadlines or queue limits. In controlled
projection the heartbeat frames remain inbound test traffic; they are not pumped
into extra outgoing PING echoes. Final paired measurements assert identical
published packet counts and byte totals.

No rig, 221, or 200v200 session was run; those remain in the other lane.
