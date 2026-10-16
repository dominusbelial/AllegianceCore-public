# P661 — per-type server egress accounting

Codex lane: `codex/p661`, base `32ceeccd`. Changes remain in the working tree;
no commits, pushes, deployment, or rig configuration changes were made.

## What is measured

Profiling enables one host-local `EgressAccounting` table at transport boot.
Client transports keep the null path. The table covers every ushort fmid
(0–65535), split into `human/ordered`, `human/stream`, `harness/ordered`, and
`harness/stream`. Harness means the TCP greeting name starts with the literal,
case-sensitive `mcp-`; all other greeting names are classified as human. This
diagnostic classification does not change the existing human liveness policy.

The lane follows P634's actual queue selection: coalesce key zero is ordered;
nonzero is stream. A guaranteed own-ship frame is therefore **stream**, despite
its guaranteed flag. Each recipient contributes separately, including packets
sent outside the normal broadcast helpers.

Every active `(fmid, peerKind, lane)` row contains:

| Field | Meaning |
| --- | --- |
| `offeredBytes`, `offeredMessages` | Accepted into a live sender, before replacement/shedding. |
| `sentBytes`, `sentMessages` | Bytes accepted by successful socket writes; messages counted when their last byte is accepted. |
| `coalescedBytes`, `coalescedMessages`, `coalescedFrames` | Old pending stream traffic superseded by P606 latest-wins replacement. |
| `droppedBytes`, `droppedMessages`, `droppedFrames` | Non-guaranteed stream traffic removed by P634 capacity shedding. |

Message bytes include their four-byte FEDMESSAGE headers. A concatenated packet
is walked by `cbmsg`, so HEAVY/LIGHT or repeated same-type messages are counted
individually. A discarded frame contributes **one frame per distinct fmid** in
that frame. Summing frame counts across types consequently does not give a
unique transport-frame count.

The synthetic fmid **65536** accounts for the five-byte outer transport header,
four-byte packet CRC, and any unparsed malformed remainder. For valid packets
this is exactly nine bytes per frame. It has zero message/frame counts and is
excluded from the top-type rankings. Including these rows reconciles total
framed bytes exactly. CRCs are not recomputed and malformed data is not rejected
by the instrumentation.

Pool partial writes contribute only their accepted byte ranges, without
double-counting messages. Would-block/zero-byte writes contribute nothing.
Blocking writes that throw have unknowable progress and contribute no confirmed
sent bytes; pending traffic abandoned at disconnect/shutdown is not classified
as capacity shedding. `sentBytes` measures socket acceptance, not remote receipt,
TCP/IP headers, retransmissions, or NIC counters.

The existing `[egress] window: bytes=...` line remains unchanged: it counts bytes
offered by simulation send helpers, including packet CRC but excluding outer
transport headers, before coalescing. It can also include attempts against a peer
already removed from the transport. Treat its ~30 MB/s as offered traffic rather
than proof that all those bytes reached a peer. P661 distinguishes the two.

## Window and line format

`TickProfiler.WindowReported` emits exactly one `[egress-by-type]` summary per
profiler window, including empty windows. The subscriber is removed at server
shutdown. Rates use monotonic **elapsed wall seconds** since the previous drain,
including real overruns, rather than nominal tick count or scaled sim time.

```text
[egress-by-type] windowSecs=5.012 offeredBytes=... sentBytes=... coalescedBytes=... droppedBytes=... overheadSentBytes=... sentSplit=[human/ordered=...,human/stream=...,harness/ordered=...,harness/stream=...] topSent=[176:FM_S_HEAVY_SHIPS_UPDATE bytes=... pct=... msgs/s=... avg=... coalescedFrames=... droppedFrames=...;...] topOffered=[176 bytes=... pct=... msgs/s=... avg=...;...]
```

Both rankings contain at most ten fmids, summed across lanes/peer kinds,
descending by their respective bytes (numeric fmid breaks ties). `pct` is the
percentage of the corresponding **total framed bytes**, so framing and omitted
types explain why the top ten need not sum to 100%. `msgs/s` is messages divided
by actual window seconds; `avg` is bytes divided by message count, or zero if no
message completed. During partial writes spanning windows this ratio can differ
from the complete message size. Principal native message types have names;
numeric fmid is authoritative for every type, including unnamed extensions.

Counter increments and resets use `Interlocked`. There is no global send lock or
snapshot barrier: bytes and message/frame increments in progress can straddle a
window boundary. Every individual counter is conserved across successive
windows. This small boundary skew is intentional; the concurrent-drain test
checks conservation. Do not infer an exact queue delta from one window's
`offered - sent - coalesced - dropped`, especially around disconnects.

## Full table on demand and reading it on 221

Enable the existing profiler before starting the instrumented server:

```json
"profiler": { "enabled": true, "reportIntervalSeconds": 5 }
```

Alternatively use the existing `ALLEGIANCE_TICK_PROFILER=1` and
`ALLEGIANCE_TICK_PROFILER_INTERVAL=5` environment variables. No separate P661
flag is needed. Profiling off allocates no accounting table and emits no P661
summary. Enabling it requires the normal server start/configuration procedure;
this working tree has not been deployed.

Claude can follow the Docker server logs from the deployment directory on the
rig and retain adjacent queue/profiler lines:

```bash
docker compose logs --since 2m --follow server \
  | rg --line-buffered '\[egress-by-type\]|\[egress\]|\[p634-queue\]|\[tickprof\]'
```

From a logged-on, seated character allowed by the existing
`liveSettings.adminCheats` configuration, send the ordinary chat command
`egress`, or `<configured-secret> egress` when that hook requires a secret.
It passes through the existing enable/name/secret checks and prints the **last
completed window** to the server console as a single `[egress-by-type-table]`
JSON line. It consumes the command without broadcasting it and does not drain
or reset counters. Before any completed window, it prints an explanatory line.

The JSON is AOT-compatible through `ServerJsonContext`. It contains
`windowEndUtc`, `windowSeconds`, and `rows`; each row has `fmid`, `peerKind`,
`lane`, and all ten counters in the table above. Only nonzero rows are emitted;
missing rows represent zeros. No file is written to the profiler CSV path and
no additional network diagnostic protocol is introduced.

To save and inspect the latest requested table locally on the rig:

```bash
docker compose logs --since 2m --no-log-prefix server \
  | sed -n 's/^.*\[egress-by-type-table\] \({.*}\)$/\1/p' \
  | tail -n 1 > /tmp/p661-egress-window.json

jq '.rows | sort_by(-.sentBytes) | .[] | select(.fmid != 65536)' \
  /tmp/p661-egress-window.json

jq '.rows[] | select(.peerKind == "harness" and .lane == "stream")' \
  /tmp/p661-egress-window.json
```

During a 200v200 run, compare `topOffered` with `topSent`, then inspect the split
rows for the large fmids. Large coalesced bytes show savings already achieved;
large offered bytes with falling sent bytes and rising `[p634-queue]` depth show
backpressure. This implementation does not itself identify the clash41 top
types: those measurements require running this build on the rig.

## Overhead

Storage is fixed at **20,971,840 counter bytes (~20 MiB)** per profiled host:
four groups × 65,537 entries × ten `long` counters. It is allocated once at
boot, independent of peer count or traffic volume. Each sender holds a table
reference, a peer-kind flag, and an in-flight lane flag; existing frame arrays
and queues are reused.

Normal accounting costs one header walk and two atomic adds per message plus
two for framing. Offered and sent events are separate walks. Discard events add
a frame counter and inspect preceding headers to avoid duplicate per-type frame
counts; they allocate no scratch collection. The high-water fmid is updated by
compare/exchange only when a larger id appears. Snapshot work scans up to that
high-water mark, not the entire ushort range in ordinary traffic.

The Debug allocation/overhead test measured **0 allocated bytes** for 400,000
events over a prebuilt three-message frame and **85.6 ns/event** in the focused
run. This is a local uncontended microbenchmark, not a rig CPU forecast.
Formatting, sorting, snapshot arrays, and optional JSON allocate only per
window/request. With profiling off, the send hooks are null checks and no
header walk or atomic accounting executes. No queue policy, priority, coalesce
key, capacity, socket timeout, payload, or broadcast cadence changed.

## Verification

The ten focused P661 cases passed, covering known byte/message mixes, all four
lane/peer splits, repeated-type frame counts, partial-write boundaries, malformed
and empty packets, fmid 65535, concurrent writers with window drains, zero hot
path allocations, top-ten/rate formatting, real queue coalescing/shedding and
failed-write hooks, both live transport modes, and server profiler/admin JSON
integration.

The three-peer TCP loopback proof ran in **thread-per-peer and pool** modes.
Both received fmids **2, 26, 78, 175, 176**, **15 messages**, and exactly
**669 offered / 669 sent framed bytes**, with one human and two harness peers.
The guaranteed own-ship fmid 78 was correctly classified as stream. The
corpus-backed server integration also booted a real map/core, logged on and
seated a peer, forced a profiler window, and checked the authorized admin dump
against the unchanged completed snapshot.

The full solution build completed with **0 warnings / 0 errors**. Restore used
`NUGET_HTTP_CACHE_PATH=/tmp/p661-nuget-http-cache` because the sandbox cannot
write NuGet's default cache under the home directory; vulnerability auditing was
left enabled. The repository's sequential runner was invoked as:

```bash
NUGET_HTTP_CACHE_PATH=/tmp/p661-nuget-http-cache scripts/run-tests.sh
```

| Assembly | Main passed | Main skipped | Main failed | LiveTiming result |
| --- | ---: | ---: | ---: | --- |
| Lobby | 12 | 0 | 0 | No matching tests |
| MonoGame | 1969 | 3 | 0 | No matching tests |
| ClientCore | 865 | 0 | 0 | 3 passed, 0 failed |
| Core (includes the 10 new P661 cases) | 4144 | 0 | 0 | 18 passed, 0 failed |

The MonoGame skips are the three pre-existing optional Meshy pack checks.
**7,011 passed, 3 skipped, 0 failed** across the full runner. Its final status was
`ALL TEST PROJECTS PASSED (main clean + quarantine cleared)`, exit **0**. All 21
LiveTiming tests passed directly; no isolation retries were needed. Main-pass
durations were Lobby 187 ms, MonoGame 4 m 21 s, ClientCore 3 m 46 s, and Core
4 m 19 s.

Local validation logs: `/tmp/p661-focused.log` (detailed focused results and
microbenchmark), `/tmp/p661-full-suites.log` (0/0 build and all suite/lane totals).
`git diff --check` is clean. The branch/base and uncommitted working-tree state
were checked again after verification.
