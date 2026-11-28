# P1101 transport allocation

A 64-peer TCP loopback allocated **9.59 MB less per 200 ticks** at the median (62.36 → 52.77 MB, **15.4%**). Send-worker roster arrays and the selected-event backing arrays disappeared from steady-state allocation samples. The three runs do not establish a whole-tick speedup.

## Brief items

Times are elapsed local work, rounded to minutes. Build and test jobs overlap.

| Item | Status | Evidence | Elapsed |
| --- | --- | --- | ---: |
| 1. Measure the base | done | Three Release processes on dispatch base `c050f9309`, each with 64 seated peers, 200 drain samples and allocation ticks by thread/type | 4 min |
| 2. Publish the send-worker roster on membership changes | done | Per-pass samples went to zero. Real partial frame survived a join, neighbor delivery and departure on its worker. | 2 min |
| 3. Transfer the TCP body to the host | done | Reference identity through TCP, mux and host, including a grown frame, under both decode modes. Borrowed input survived caller overwrite. | 2 min |
| 4. Reuse drain collections and remove charge capture | done | Warm-drain allocation test, 1,600-departure retention test and baseline event replay passed. | 2 min |
| 5. Regressions and deliberate breaks | done | 180/180 isolated executions passed. All six new cases failed on their deliberate breaks. Captured events and cross-build state, output and RNG transcripts matched. | 14 min |
| 6. Repeat measurements and report | done | Three changed runs, six-run comparison, both solution builds, 157 tool builds and full Fast failure triage below. The full Fast gate is red. | 20 min including qualification |

## Matched Release measurements

Linux, .NET SDK 10.0.404, Release JIT, server GC asserted at runtime, eight logical processors exposed to .NET. Each process booted the same server, original static core and two-sided map used by the cadence tests. Sixty-four real TCP clients greeted, logged on and seated. All 64 ships were placed in one sector. The fixture requests four send workers and parallel build, uses immutable snapshot, encode-once, the built-in P863 budget, native ship updates and the default native GotoPlan. It omits boot-time miners. Four ordinary encoded pings per peer per tick supplied **51,200 input frames** over the measurement window. The server kept its existing ping reply limiter. All peers remained present and their readers finished the window without errors.

Each run warmed up for 20 ticks, then paced 200 ticks at 20 Hz. The measured windows lasted 9.925–9.936 s. MB denotes one million bytes. Process allocation is the difference of two precise GC total-allocation counter reads. It includes the in-process clients, sampler and test driver. Tick time covers `TickSim` including its drain, excludes writing client pings and pacing sleeps. Drain time comes from the production `step:drain` profiler bucket.

| Build / run | Process MB | MB/s | Drain mean ms | Drain p95 ms | Tick mean ms | Tick p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Base 1 | 62.235 | 6.27 | .158 | .288 | .627 | .957 |
| Base 2 | 62.596 | 6.31 | .161 | .255 | .667 | 1.031 |
| Base 3 | 62.358 | 6.28 | .202 | .255 | .686 | 1.011 |
| Change 1 | 53.120 | 5.35 | .174 | .381 | .711 | 1.690 |
| Change 2 | 52.771 | 5.31 | .151 | .257 | .627 | 1.022 |
| Change 3 | 52.500 | 5.29 | .145 | .261 | .615 | 1.082 |

Median run-mean drain time moved from .161 to .151 ms. Median drain p95 moved from .255 to .261 ms. The slowest base drain took 10.28 ms, and the first changed run had the slowest tick p95. The consistent result is the reduction in allocated bytes. This small loopback does not measure a late-match 195v195 workload or predict its allocation mix.

### Allocation by owner and site

The CLR GCAllocationTick listener attributes `AllocationAmount64` to the event's OS thread and threshold-crossing object type. Reader threads are grouped by their TCP thread names. The tick driver's OS thread is identified directly. Sites are inferred from those roles and types, following PERF-F1. Per-type figures fluctuate when short-lived objects repeatedly cross a thread's sampling threshold. A zero means no matching sample during the window.

| Sampled MB per 200 ticks | Base 1 | Base 2 | Base 3 | Change 1 | Change 2 | Change 3 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| All threads | 62.798 | 63.058 | 63.289 | 53.644 | 53.346 | 53.077 |
| Sim/driver thread | 48.226 | 48.068 | 47.717 | 44.911 | 43.614 | 44.138 |
| TCP readers | 8.742 | 8.629 | 9.548 | 4.891 | 5.194 | 5.200 |
| Send pool (all samples were roster arrays) | 1.698 | 2.434 | 1.168 | 0 | 0 | 0 |
| Runtime pool workers | 3.280 | 3.293 | 3.487 | 3.097 | 3.482 | 2.989 |
| Publisher | .533 | .107 | .422 | .212 | .418 | .426 |
| Other threads | .319 | .528 | .946 | .532 | .639 | .323 |

| Site and sampled type, MB | Base 1 | Base 2 | Base 3 | Change 1 | Change 2 | Change 3 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Worker scan, `PeerSender[]` | 1.698 | 2.434 | 1.168 | 0 | 0 | 0 |
| Reader framing and enqueue, `byte[]` | 5.766 | 5.742 | 6.359 | .853 | 4.662 | 3.709 |
| Reader enqueue, `ReceivedEvt` | 2.976 | 2.776 | 3.190 | 4.038 | .532 | 1.491 |
| Tick selection, tuple arrays | 2.095 | 2.817 | 1.767 | 0 | 0 | 0 |
| Lane event arrays | .942 | .938 | 1.029 | 0 | 0 | 0 |
| Lane queues | .528 | .319 | .209 | 0 | 0 | 0 |
| Tick `Dictionary<uint,int>` entry arrays | .524 | .623 | .841 | 0 | 0 | 0 |

The runtime weighted a reader closure in only one base run (.110 MB). There is no reliable separate estimate for that call site. The argument overload has static factories, so its charge value needs no per-call delegate or display class.

**Reproduction.** In disposable scratch copies of the base and changed source, copy the same [measurement fixture](../../tools/p1101-transport-allocation/Measure.cs) into `tests/Allegiance.Core.Tests/P1101Measure.cs`. Load the corpus configuration and use a local NuGet cache. Run each arm three times with separate processes. The fixture prints the exact counter, profiler tuple (count, mean, p95, max, total), whole-tick timings and thread/type sample totals to the named file.

```bash
set -a
source local.env
set +a
export DOTNET_gcServer=1 DOTNET_PROCESSOR_COUNT=8
mkdir -p .scratch/p1101
dotnet build tests/Allegiance.Core.Tests -c Release
export TMPDIR="$PWD/.scratch/p1101"
for run in 1 2 3; do
  dotnet test tests/Allegiance.Core.Tests -c Release --no-build \
    --filter FullyQualifiedName~P1101Measure.Sample
  mv "$TMPDIR/p1101-transport-allocation.tsv" "$TMPDIR/measure-$run.tsv"
done
```

## Equivalence and retention

- **Workers.** The same lock linearizes append, removal and the start of a pass. Append preserves the old prefix. Removal copies the two surviving ranges in order. A worker already traversing the old array can finish that pass, exactly as with the previous list snapshot. Assignment, enqueue pulses, partial-write offsets, blocked and idle waits, stop handling and the single consumer per peer follow the existing paths. [TCP transport](../../src/Allegiance.Core/Transport/TcpTransport.cs)
- **Body ownership.** For accepted data, `ReadFrame` creates a private array, initially at most 4 KiB. Each growth stops at the declared length and the function returns only after filling it. Synchronous socket reads retain no user buffer. Short frames exit before the callback. After checking length and peer state the host reader transfers the array as its last use. The mux looks up the mapped ID and forwards the same reference without storing it. An admitted event owns that body until processing completes. The optional predecoder reads it through the event and its existing publication barrier. Per-message copies and decoding still take the existing path. Borrowed-span producers copy after successful admission. Native queue ownership and CRC validation are documented in `_Utility/Messages.cpp:313–329,916–935,1012–1022`, and the existing bad-CRC response in `FedSrv/FedSrv.CPP:4591–4603`. This ports no new gameplay or wire rule. [Transport contract](../../src/Allegiance.Core/Transport/ITransport.cs) · [Listener adapter](../../src/Allegiance.Core/Transport/ServerTransportMux.cs) · [Inbound decode](../../src/Allegiance.Server/InboundPacket.cs)
- **Drain.** Budget reservation and rollback, tick-entry FIFO and decoder cutoffs, sorted peer rotation, per-peer share, carry-over and final (peer, sequence) sort use the same inputs and operations. A queue becomes eligible for reuse only when its final event has been dequeued. Each handling batch is detached while callbacks run and cleared in a finally block. A nested drain therefore gets a separate list. Simulation, RNG, packet builders and output scheduling consume the same ordered events. [Server host](../../src/Allegiance.Server/ServerHost.cs)
- **After a mass leave.** At most 512 empty queues of at most 32 reference slots each survive (16,384 slots total). Oversized queues are discarded on emptying. The list of selected events retains at most 8,192 slots and clears all event references. The handled dictionary retains at most 1,024 slots. The active-lane dictionary and order list also shrink when capacity exceeds 1,024 and active lanes fall to 512 or fewer. Live deferred work keeps its original admission bounds and releases bytes and event slots at handling time. The test drains more than 8,192 events from 1,600 departing peers, including an oversized lane, checks all five capacity limits and empty accounting, then measures 24 warmed drains.

## Captured replay and deliberate breaks

Four complete TCP ping bodies with distinct client stamps and CRCs were captured at the unchanged host's receive event. The baseline replay queued joins, those bodies and leaves for three peers in descending peer order. A budget of four and share of two exercise carry-over and rotate the starting peer. The test compares all 18 baseline events across five nonempty drains plus the empty sixth drain by event kind, peer, join name and full body bytes, in both decode modes. Existing P673 tests separately compare simulation state and outgoing wire responses. [Replay and ownership tests](../../tests/Allegiance.Core.Tests/Transport/P1101IngressTests.cs)

A separate Release replay ran the P673 donation and flight-control fixture on the dispatch source and the changed source. The changed driver submitted owned arrays. Both drivers recorded 20 per-tick ship state snapshots (pose bits, controls, state flags, wallet, hull and part count), 120 dispatched input frames and 40 recipient-tagged output packets. Those 180 lines matched byte for byte. A second comparison seeded the mission RNG and recorded its next value after each tick. With all 20 RNG checkpoints included, `cmp` matched all 14,641 bytes. SHA-256 was `da0c045149bbdc046fcf768ca3d823c47fdfbf18c1486ff9ce0dc89bf8496860`. These are scratch-only observations of the existing [P673 fixture](../../tests/Allegiance.Core.Tests/Transport/P673ReceiveDecoupleTests.cs).

| Deliberate change in a scratch build | Observed failure |
| --- | --- |
| Replace a worker's entire roster with only the newly assigned peer | After its neighbor left, the first peer timed out reading the rest of the partially written 256 KiB frame. |
| Copy the owned array in the host | Both decoder modes received equal bytes in a different array and failed reference identity. |
| Sort selected peer IDs descending | Both replay cases delivered peer 3 first on tick zero. Baseline starts with peer 1's join and first receive. |
| Allow 1,024 spare queues | Mass departure retained 1,024, exceeding the asserted 512. |

The six healthy cases passed before injecting the four independent faults into a separate build. All six failed there. [Worker membership test](../../tests/Allegiance.Core.Tests/Transport/TcpTransportSendWorkerPoolTests.cs)

## Verification

Each of 52 affected methods ran alone three times in a fresh Debug test host with all theory rows and the local artwork. All **180/180** cases passed across 156 hosts, with zero skips. Discovery assigned all six new cases to Fast. The existing crowded methods kept their Perf classification and ran separately at their default 256 peers.

| Coverage | Passing case executions across three runs |
| --- | ---: |
| Worker pool, including membership during a partial write, subsequent idle delivery, shutdown and existing backpressure/churn cases | 27 |
| Existing partial-write reassembly, both transport modes | 6 |
| Broadcast snapshot loopback, including three crowded scenarios and the recorded per-lane packet comparison | 12 |
| P315 flow control and P646 bounded drain | 30 |
| P1023 admission, event-count limits and short frames in both modes | 33 |
| Oversized frame isolation, oversized frame boundary and handler exception barrier | 9 |
| P673 worker decode, malformed messages, bounded selection, state/packet replay and real socket bursts | 30 |
| Listener ID routing, failed bind and WebSocket ingress budgets | 18 |
| New ownership, captured replay and bounded reuse tests (the new writer test is counted above) | 15 |

Both full solution builds completed with **0 warnings and 0 errors**, in Debug (56 s) and Release (75 s). The complete repeat of `tools/build-tools.sh --drop-bins` built all 157 standalone tool projects with zero warnings and errors. The glossary link check passed for 1,195 distinct paths. The packaged measurement fixture also ran independently in a scratch checkout and produced its thread/type TSV.

**Local-only rule deviation.** MSBuild used the scratch package feed from the start. The first Debug solution build also invoked a nested .NET tool restore for MGCB. That restore populated a fresh public NuGet service-index cache and reported a newer tool version. The nested restore had inherited its own default feed configuration. I corrected the CLI's user-level NuGet configuration to clear package sources and use the scratch feed, and directed non-loopback HTTP proxy traffic to a closed local port. Both final solution builds passed again with a fresh scratch HTTP cache, which stayed empty. The three pinned KNI shader packages missing from the initial standalone tool sweep came from an existing on-disk cache. All gameplay, profiling and replay traffic in the P1101 fixtures used loopback.

### Full Fast sweep

The unfiltered Fast run took 758.8 s. Of 16,230 discovered cases, **16,215 passed, eight failed and seven were skipped**. All nine infrastructure checks passed. Fifteen passing executions also exceeded the calibrated 13.498 s case limit. All six P1101 cases passed in this sweep.

- Two default-scale P1091 HUD framebuffer checks reproduced the known software-renderer hashes at width 1920. Three isolated hosts each passed 19 companion rows and failed those same two hashes.
- Five P644 bundle-path cases found the real checkout's ancestor configuration above the scratch temp directory. A directory-descriptor alias gave the isolated fixtures a logically external pathname with physical data in scratch. All seven rows in the four affected methods passed three times (21/21).
- One P890 skirmish/port aim case crossed the peak threshold. Three isolated hosts passed all 12 theory executions. As an additional control, 20 hosts using Debug Core and Server binaries rebuilt from the dispatch source passed all 80 cases. The exact peak failure is confined to the sweep in this evidence. Earlier runs ledgered sustained drift for the same case.
- The sweep timing results include one newly ledgered P704 role-selection case. Its three isolated runs passed all 21 theory rows, with per-host maximum case times of 1.89, 2.51 and 1.31 s. The remaining over-limit methods have prior ledger entries. [Flake ledger](../TEST_FLAKES.md)

The test manifest assigns the P928 supplemental golden to Live. It was outside this Fast run.

All 19 failed or over-limit methods ran three times each in isolation. Across 57 hosts, 216 executions passed and the six default HUD image comparisons failed. Every case that exceeded the sweep time limit finished within it on all three repeats. The worst individual timing-case repeat was 10.64 s. The full sweep remains red.
