# P670 — send-pool starvation investigation

Base: `integ3` at `6c3e54ad` (workspace branch `codex/p670`). Changes remain in
this working tree; no commits or pushes. No rig/221/200v200 run was performed.

## Finding

The checked-in pool does **not** use blocking whole-frame writes. P645 already
removed that mechanism. A slow receiver exhausts its own send window and is
skipped; it does not park a shared worker in a blocking socket write. The
unchanged baseline also passes a real loopback proof with three paused readers
and sixteen healthy readers sharing **one** worker. Consequently this task adds
diagnostics and regression coverage, rather than introducing another transport
mode or a speculative send-policy fix.

The clash42 evidence supplied in the brief does not establish actual wire
throughput: its `[egress]` line counts simulation payload offers, **before**
socket I/O. P661's separate `[egress-by-type] sentBytes` already measures actual
socket acceptance. Neither measurement proves receipt by the remote application.
The supplied incident alone therefore cannot establish whether the missing
progress was at the pool, the network, or the receiving host. The new diagnostics
make those alternatives distinguishable on the next run.

## Pool path and bounds

Managed references below describe the final working tree.

1. `TcpTransport.Send` frames and enqueues the payload. It performs no socket
   I/O. Accepted sockets initially have an 8 KiB send buffer and a blocking
   10 s send timeout (`src/Allegiance.Core/Transport/TcpTransport.cs:591`). The
   greeting completes before the host calls `StartOrPool` (`:715`).
2. Pool assignment hashes connection ID modulo worker count, with exactly one
   consumer per peer (`:1886`). `AttachWorker` sets `Socket.Blocking=false`
   (`:1465`). P654's connection caps, greeting setup, and frame-read deadlines
   do not revert that setting. P646 waits for read readiness before reading a
   non-blocking socket, instead of spinning on WouldBlock exceptions.
3. Each worker scans its assigned peers once per pass (`:1954`). Each peer
   promotes one ordered frame (or stream frame when no ordered frame exists)
   into the retained in-flight slot, checks `Poll(0, SelectWrite)`, and attempts
   **one** non-blocking `Socket.Send` (`:1484`, `:1621`). Unwritable/WouldBlock
   peers retain their frame and offset and yield the turn. There is already a
   one-send-attempt budget per peer per pass, rather than an unbounded drain of
   one peer's queue. When no peer progresses, backpressured workers sleep 2 ms;
   idle workers wait up to 50 ms, or 200 ms with no assigned peers. Enqueues wake
   the assigned worker.
4. `AdvanceInflight` counts only the bytes returned as accepted, refreshes the
   peer's progress clock, and clears the frame only after completion (`:1687`).
   A blocking dedicated writer remains separate and uses `NetworkStream.Write`;
   an error/timeout with unknown partial progress closes the connection instead
   of resuming a potentially corrupt stream.
5. The ordinary pool protection is **non-blocking I/O**, not the historical
   250 ms `SendTimeout`. That setting is retained defensively; its previous
   comment falsely described normal pooled sends as blocking whole-frame writes.
   No flag or risky policy change is needed here.
6. Liveness is checked on subsequent enqueue (`:1410`): pending data plus no
   accepted bytes for 30 s gives `NoWriteProgress`; marked humans get 60 s.
   The existing guaranteed-backlog soft/hard caps remain authoritative too.
   An isolated queued frame without further enqueue is not independently reaped
   by a pool timer. This task preserves that policy; the loopback proof continues
   enqueueing, as a running simulation does.

This excludes the proposed **socket-flow-control blocking** mechanism in the
normal configured pool path. It does not guarantee a hard scheduling deadline:
worker descheduling, queue-lock contention, CPU spent in accounting, oversized
frames, or kernel/host network trouble can still reduce progress. The worker
continues to allocate its existing peer snapshot per pass; this investigation
does not claim that the entire pre-existing pool is allocation-free.

## Native rules consulted

All native citations use the owner's requested source root.

- `~/Development/Allegiance/src/clintlib/clintlib.cpp:2047` sets the native
  30-second dead-time constant; `:2064` tests it **together with unanswered
  pings** before reporting session loss. The port's no-write-progress rule uses
  the same duration but is a server-side socket-progress policy, not a literal
  implementation of that native client condition.
- `~/Development/Allegiance/src/_Utility/Messages.cpp:558` gives guaranteed
  messages guaranteed/high-priority/coalesce flags; `:559` gives ordinary
  messages nonsequential/no-completion flags; `:563` selects unlimited guaranteed
  TTL versus 500 ms for ordinary messages. The managed retained-frame and
  ordered/stream distinction is preserved.
- `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4189` and `:4195` send heavy/light
  ship updates with `FM_NOT_GUARANTEED`. Those stream frames remain eligible for
  existing shedding/coalescing. No native protocol or gameplay rule changed.

The managed TCP framing, worker pool, per-write diagnostics, and thresholds below
are port-specific mechanisms; they are not attributed to native DirectPlay.

## Diagnostics

`src/Allegiance.Core/Transport/SendDiagnostics.cs` provides fixed counters and
value snapshots. `TcpTransport.DrainSendWindow` (`:860`) fills reusable spans
without allocations. Worker timing uses a short bookkeeping lock, **never held
across socket I/O**. Peer progress bookkeeping uses the existing queue lock to
sample the retained frame/offset consistently. Global host counters survive
peer removal, so a departing peer's final writes are not lost.

Every profiler callback through `Server.Egress.cs:13` emits:

| Line | Meaning |
| --- | --- |
| `[send-pool]` | Measured wall seconds; host enqueued/written bytes; live peers and workers; total remaining queued **plus in-flight** bytes; queue growth since the previous sample. Also emitted with `workers=0` for dedicated mode. |
| `[send-worker]` | Worker ID and sampled Idle/Scanning/Writing/Backpressure state; write-call count; aggregate busy-write time; longest single call; current ongoing call age. |
| `[send-peer]` | Live peer ID; its enqueued/written byte deltas; queued plus in-flight bytes; time without progress while pending data exists. Fully idle peers are omitted. |
| `[send-stall]` | One incident per growing-queue window if written bytes fall below 10% of enqueued bytes, or measured write rate falls below 25% of the preceding nonzero write rate. Diagnostic only. |

Timing brackets the actual socket send (or test sink), excluding queue promotion,
readiness checks, egress parsing and disconnect teardown. A call still in
progress is visible **before it returns**; busy time is split across windows
without loss or double-counting. Longest/current duration is elapsed wall time,
which includes descheduling during the call and is not proof of time blocked
inside the kernel. Worker states are instantaneous samples, not state histograms.

All transport enqueue/write numbers include the five-byte outer framing header.
Enqueued bytes include entries subsequently coalesced or shed; existing P661
coalesced/dropped counters explain that difference. Written means OS/socket
acceptance, **not** TCP acknowledgement or application receipt. Concurrent row and
aggregate samples may straddle a window boundary; they are not an atomic
snapshot of the whole host. Departed peers contribute to host totals but have
no subsequent live-peer row.

At its existing disconnect-summary cadence, `[egress]` now explicitly reports
`payloadOfferedBytes` (simulation payload offers), `enqueuedBytes` (actual
transport enqueue), and `writtenBytes` (actual socket acceptance), with distinct
per-peer rates and measured wall seconds. The latter two have matching framing
scope. `[egress-by-type]` retains its existing fields and adds explicit
`sentScope=socket-accepted-including-framing` and
`offeredScope=enqueued-including-framing` labels. The `p619c-ab.sh` parser accepts
both old and new `[egress]` formats.

Hot diagnostic updates and the span sampler allocate zero bytes after warmup,
verified by an allocation assertion over 1,000 samples with a real connected
peer. Reusable server arrays allocate on the first profiler callback (or a cap
increase). Log formatting and the existing P661 report snapshots still allocate
at report time; no claim of allocation-free logging is made.

Interpretation for the next rig incident:

- Growing queues + low writes + long Writing/current-call ages: investigate
  actual send-call stalls or worker scheduling; the ongoing-call age is available
  even if no completion returns.
- Growing queues + low writes + Backpressure states: sockets are refusing data;
  inspect acknowledgements, advertised receive windows and the receiving host/link.
- Growing queues + low writes + Scanning states with short socket calls: inspect
  queue-lock contention, accounting and worker scheduling outside socket calls.
- Healthy socket acceptance while all bot read ages rise: investigate the path
  after local acceptance. The original enqueue-only `[egress]` cannot support
  that conclusion by itself.

## Loopback and verification

New tests in `P670SendStallTests.cs` cover the shared-worker paused-reader proof,
partial/WouldBlock byte and in-flight accounting, departed-peer conservation in
both modes, active writes spanning report boundaries, stall/recovery thresholds,
and diagnostic allocations. The existing P661 real-server profiler/admin test
also asserts that the new transport lines are emitted.

The main proof connects **3 paused readers with 1 KiB receive buffers and 16
healthy readers**, all on **one** worker, enqueues 2 MiB of guaranteed data per
paused peer, and checks complete ordered/content-correct frames on every healthy
peer over repeated rounds. Its delivery bound is 1.5 s; paused peers must be
attributed exactly three `NoWriteProgress` drops, with no healthy departures.
The timeout is tightened to 2 s for this test and restored afterward. P661
accounting is enabled, exercising the actual host accounting hooks as well.

Three isolated final-tree runs measured maximum round delivery latencies of
**3.646 ms, 3.523 ms, and 2.636 ms**. Each delivered **76,176 healthy wire bytes**
over 69 rounds, observed an initial **6,265,551-byte** retained backlog, and
recorded exactly **3 NoWriteProgress** drops.

A separate executable compiled the **unchanged baseline** `TcpTransport.cs` and
`EgressAccounting.cs` read via `git show HEAD:path`, in `/tmp/p670-baseline`, and
ran the same 3-paused/16-healthy/one-worker traffic proof. It passed: **69 rounds,
2.453 ms maximum delivery, 3 NoWriteProgress drops**. Thus a failing-before
send-policy regression is not applicable: the suspected blocking-send defect is
already fixed in the base. New telemetry assertions verify the previously
missing observability rather than claiming to repair the clash42 root cause.

The build passed with **0 warnings and 0 errors**. NuGet's default HTTP cache was
read-only in this sandbox; redirecting `NUGET_HTTP_CACHE_PATH` to
`/tmp/p670-nuget-http` allowed the normal restore/build, including vulnerability
data retrieval. No build policy or package checks were disabled.

Final verification results are recorded in [P670_RESULTS.json](P670_RESULTS.json).
The matrix runs each of 86 new/affected method filters **3 times alone**, then
unfiltered Core, ClientCore and MonoGame suites sequentially. It includes all
loopback/live tests and enables the optional local 15v15 P660 showcase; it does
not run the excluded rig/221/200v200 lane.

| Final unfiltered suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,309 | 0 | 0 |
| ClientCore | 933 | 1 | 0 |
| MonoGame | 2,125 | 0 | 3 |

The 258 isolated invocations passed **264 cases** with no failures or skips
(theory filters include both mode cases). The sole full-suite failure was
`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`:
logon drain order differed from connection order, producing sides `(0, 1, 0)`.
After all three assemblies completed, it passed **3/3 exact-method isolated
reruns**. Under the brief's rule this is a load flake, recorded in
[TEST_FLAKES.md](../TEST_FLAKES.md); its assertion, deadlines and traits remain
unchanged. The local 15v15 showcase passed. MonoGame's only skips were the three
existing optional Meshy-pack availability checks.

`bash -n scripts/p619c-ab.sh`, old/new egress parser checks, and final
`git diff --check` passed. The durable JSON includes the individual invocation
outcomes, loopback measurements, exact failing output, baseline source hashes,
and baseline reproducer source/project. No code or test assertion changed after
the successful build and validation matrix. No commits or pushes were made.
