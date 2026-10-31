# P898 — TCP pose delivery under delay

Base: `b4a4721a`, Codex worktree/branch `codex/p898`. Changes are left uncommitted.
No rig, mini, Mac or other test host was contacted.

## Cause and fix

The server's fixed 8 KiB `SO_SNDBUF`, many small `TCP_NODELAY` sends, and the
pool's `SelectWrite` gate exhaust **kernel packet memory**, rather than link
bandwidth. The relevant sites are `TcpTransport.cs:217,668-674` (socket sizing),
`:1746-1768,1798-1805` (readiness gate), `:1847-1873` (promotion before
readiness), `:1551-1597` (pending-key superseding), `:2001-2057` (nonblocking
sends), and `:1068-1108` (client/server reader). Paths are under
`src/Allegiance.Core/Transport/`. Linux reports a doubled 16,384-byte send-memory limit. `SelectWrite`
requires free memory of at least half the already queued memory, so it stops
admitting sends around 10,923 bytes of queued memory. Small packets cost much
more kernel memory than their payload. Increasing RTT retains those packet
allocations until ACKs return. The exact readiness inequality is
`SO_SNDBUF_actual - wmem >= wmem / 2`, verified in this host's
`linux-headers-7.0.0-42/include/net/sock.h:1105-1113,1424-1427`
([upstream helpers](https://github.com/torvalds/linux/blob/v6.17/include/net/sock.h#L1016-L1023)).
Setting `SO_SNDBUF` also disables automatic send
buffer tuning ([kernel documentation](https://www.kernel.org/doc/html/next/networking/ip-sysctl.html)).

The observed sequence is: readiness fails; the pool waits/retries; an already
promoted frame remains in flight and cannot be superseded; newer frames with
the same key supersede each other in the pending stream lane. Ordered frames
still precede pending stream frames. The extra delay and missing poses occur
before delivery, with very short nonblocking socket writes. The client reads
complete frames on its dedicated blocking reader and has no deliberate delay
or explicit small receive-buffer override.

The fix is **`transport.tcpBatchWrites: true`**, default **false**. Pool workers
gather up to 32 already pending complete transport frames, with a 16 KiB gather
budget, into one nonblocking scatter/gather send. An oversized head retains the
existing single-frame partial-write behavior. There is no wait to accumulate a
batch. Fewer small TCP packets reduce kernel send-memory pressure. The wire
framing, per-frame accounting and guaranteed delivery remain intact. A partial
write reserves the rest of its bounded batch; new higher-priority traffic follows
that committed batch. Uncommitted traffic retains park/ordered/stream priority.
Dedicated writers and client sends retain their existing path.

The 8 KiB socket buffer, 2 ms pool retry, queue caps, socket send timeout,
30-second no-progress and inbound-idle deadlines, and existing 60-second human
write grace remain unchanged. Batch bytes remain charged to backlog limits;
buffers remain leased until a send returns, including during concurrent close.
This avoids hiding a dead reader behind an enlarged socket buffer.

A discarded probe experiment, retaining the small buffer but attempting sends
without `SelectWrite`, improved the initial 80 ms run from 118/212 ms to
112/159 ms p50/p95 and 17.85 to 25.94 KB/s, but did not remove the limit. It is
not a shipping option. Batching alone produced 80.9/81.5 ms, 114 ms worst gap,
and 30.14 KB/s in the initial 20-second trial. These are preliminary trials;
the corrected repeated sweep below is the final evidence.

## Measurement method

Direct `ip netns add` and `unshare -Urn` failed without privilege. A **local
container with `NET_ADMIN`** succeeded without sudo. All final delayed runs use
real kernel netem on that container's private loopback, MTU 1500, zero configured
jitter/loss, and queue limit 10,000. A configured one-way delay of 80 ms produces
approximately 160 ms TCP RTT. This is **not** the P895 stream-proxy model. The
host network namespace is untouched. The matrix tool rejects host-network
containers and verifies the worktree bind mount.

The existing P895 dense fixture uses a separate server process, real TCP sockets
and real `ConnectionSession` clients, pool mode with two send workers, immutable
snapshots/encode-once/parallel-build, native ship updates and native GotoPlan,
server GC, seed 895 and the fixed `XXV_simple.igc` map. Its crowding penalty is explicitly off
in both arms to retain all 200 fixture clients. Controls are sent every 50 ms;
timestamped ordered chat samples are sent twice per second. Age is measured
before client world-state dispatch. No synthetic CPU load was created.

P898 also fixes a proof-tool bug: at zero RTT the socket reader could beat the
tick-log reader. The old immediate lookup silently discarded such poses and
fabricated long gaps. `PoseTiming` now records the original arrival time and
correlates missing tick timestamps after the sample; a deterministic regression
test covers both age and gap. The earlier interrupted sweep is excluded from
the final tables. This was an observer defect, not a game-client receive delay.

The final sweep has three independent 12-second samples per delay/arm
(four clients; 48 runs), following the fixture's 1.5-second post-seat warmup. Table p50 and
p95 are **medians of per-run quantiles**, bytes/s and pose counts are per-run
medians, and gaps are the worst across repetitions. Rates include TCP outer
framing and differ from UDP's protocol overhead. Raw receive queues, send queues,
send memory, retransmission and segment counters are sampled with `ss -tinm`.
Transport CSVs capture write duration, superseding, readiness failures,
application in-flight/queued bytes and no-progress age. Kernel `Send-Q` includes
unacknowledged and unsent bytes; it is separate from application in-flight bytes.

## Results

| One-way ms | Off pose p50/p95 ms | On pose p50/p95 ms | Off / on KB/s | Off / on worst gap ms |
|---:|---:|---:|---:|---:|
| 0 | 0.8/1.7 | 1.0/2.7 | 29.78 / 29.51 | 117.4 / 121.1 |
| 10 | 10.8/11.7 | 10.8/11.7 | 29.85 / 29.76 | 106.2 / 120.2 |
| 20 | 20.8/21.8 | 20.9/22.1 | 29.77 / 29.79 | 129.9 / 116.9 |
| 30 | 30.8/43.1 | 30.8/31.9 | 29.80 / 29.74 | 121.7 / 118.3 |
| 40 | 50.9/73.0 | 40.8/41.8 | 29.80 / 29.77 | 165.3 / 118.0 |
| 60 | 90.4/149.5 | 60.8/61.7 | 23.12 / 29.85 | 484.3 / 122.0 |
| 80 | 117.0/208.1 | 80.9/81.9 | 17.45 / 29.77 | 532.6 / 145.0 |
| 120 | 147.3/241.0 | 121.0/122.6 | 10.14 / 29.71 | 1446.6 / 212.6 |

The threshold is traffic-dependent, not a universal RTT constant. In this
fixture the first extra tail is at **30 ms**, the median moves at **40 ms**, and
sustained throughput falls between **40 and 60 ms**. At 80 ms, batching restores
36 ms of median delay and 126 ms of p95 delay, and raises bytes/s by 71%.
The baseline has 41% fewer bytes/s than the on arm, matching the brief's symptom.
All 192 sampled client sessions (four in each of 48 independent runs) remain
connected. At 120 ms some residual superseding/tail gaps remain; the experiment
is not a claim that TCP head-of-line blocking has disappeared.

The following medians are over the three 80 ms runs (12 seconds each); maximum
write/queue figures use the worst repetition. Superseding counts cover **all
keyed frames**, while pose samples count fresh remote HEAVY/SINGLE poses.

| Transport observation | Off | On |
|---|---:|---:|
| Fresh pose samples per run | 966 | 2,258 |
| Superseded keyed frames per run | 3,066 | 0 (one in the worst run) |
| `SelectWrite` failures per run | 25,216 | 301 |
| Socket write calls per run | 3,270 | 1,451 |
| Longest measured write turn, ms | 0.0277 | 0.0424 |
| Maximum application queued bytes, all four peers | 7,856 | 302 |
| Maximum application in-flight bytes, all four peers | 4,769 | 130 |
| Median per-socket kernel send-memory use, bytes | 11,156 | 5,658 |
| Mean TCP payload per data segment, bytes | 62.8 | 243.2 |
| Median run's maximum per-socket `Send-Q`, bytes | 4,427 | 6,009 |
| Observed retransmitted-byte counter increase | 0 | 0 |

Both arms report `tb16384` throughout. Both have zero `notsent` at the socket
samples: this is mostly **ACK-retained packet memory**, not an unsent-byte pile.
The on arm carries more payload in flight with less kernel memory. Server
receive queues sampled zero; client receive queues are usually zero and peak
at only 57 bytes off / 274 bytes on in these windows. These samples show no sustained
client-reader backlog; sender-side backpressure explains the extra 37 ms. No qdisc drops were recorded.
The pool's writes themselves are short; the stalls are readiness/backpressure
between writes. The on-arm write timing includes bounded batch bookkeeping.

Full per-run values and kernel samples summarized for review:
[delay-matrix.json](P898_assets/delay-matrix.json). Raw socket and transport CSV
receipts: [matrix-final](../../.scratch/p898/matrix-final/).

### Loopback at 200 clients

Three 20-second repetitions per arm, normal loopback MTU 65536, **no qdisc or
proxy**. Every run retained all 200 server/client sessions. This is the P895
dense scripted-client fixture, rather than a 200-player AI clash.

| Median measurement | Off | On |
|---|---:|---:|
| Pose p50 / p95, ms | 6.31 / 7.97 | 5.91 / 7.20 |
| Worst pose gap across runs, ms | 541.1 | 527.1 |
| Transport bytes/s | 5,556,821 | 5,542,850 |
| Fresh pose samples per run | 2,119,505 | 2,114,190 |
| Sampled server ticks per run | 403 | 402 |
| Server tick p95, ms | 5.64 | 5.62 |
| Server CPU, cores | 0.362 | 0.300 |
| Client CPU, cores | 0.457 | 0.413 |
| Write calls per run | 669,435 | 193,593 |
| Superseded frames | 0 | 0 |

There is no observed loopback regression: p95 improves by 9.6%, server CPU by
17%, client CPU by 9.5%, and throughput/pose counts are within 0.3% (approximately
one tick in a 20-second window). The roughly half-second largest pose gaps are
present in both arms under the native many-ship ranking budget. They are not
an added batching delay. Full receipts: [loopback200.json](P898_assets/loopback200.json)
and [raw runs](../../.scratch/p898/loopback200/).

### MTU and UDP controls

The MTU control uses three 20-second TCP samples per arm at MTU **65536**.
The UDP control uses three 12-second samples at each delay, MTU 1500. All use
real private-container kernel netem, the same four-client fixture, zero configured
jitter/loss, and no proxy.

| Control | Pose p50/p95 ms | Worst gap ms | KB/s |
|---|---:|---:|---:|
| TCP 80 ms, MTU 65536, batching off | 117.5/211.4 | 644.8 | 17.75 |
| TCP 80 ms, MTU 65536, batching on | 81.0/81.6 | 138.1 | 30.21 |
| UDP 30 ms, MTU 1500 | 31.2/31.9 | 126.9 | 35.78 |
| UDP 80 ms, MTU 1500 | 81.1/81.7 | 116.1 | 35.81 |

The TCP slowdown persists at normal loopback MTU 65536 and disappears with
batching there too. It is not specific to the MTU-1500 setup. UDP still delivers
near the configured delay, while the TCP on arm reaches similar age quantiles.
All control clients stayed connected and all recorded qdisc drop counters were
zero. These are local kernel results; NIC offloads and a real WAN still belong
to Claude's longer rig A/B.

Compact receipts: [mtu-control.json](P898_assets/mtu-control.json),
[udp-control.json](P898_assets/udp-control.json); raw controls are in
[MTU runs](../../.scratch/p898/mtu-control/) and
[UDP runs](../../.scratch/p898/udp-control/).




### Dead-reader and ordering checks

Three real-socket repetitions per arm pause a reader with a 1 KiB receive
buffer, enqueue 256 guaranteed 512-byte payloads (each small enough to gather),
and continue offering keyed frames. With the no-progress budget shortened to
400 ms for the test and restored afterwards, disconnects are all
`NoWriteProgress`: off **446.6/447.0/447.5 ms**, on **446.9/407.9/407.4 ms**.
The on arm is not slower at recognizing this stalled reader. The complementary
P670 case retains three paused readers and sixteen healthy readers on one
worker; both arms drop the three stalled peers under the same two-second test
budget without starving healthy delivery. Production budgets remain 30 seconds
(strict) / 60 seconds (human), with the original receive-idle timeout as well.

Deterministic gather tests cover partial progress across frame boundaries,
would-block resumption, priority after a committed batch, the 32-frame/16 KiB
budgets, graceful final replies, and buffer leases during concurrent close.
Actual socket tests pin the 8 KiB option in both pool and explicitly selected
legacy dedicated mode; the dedicated writer ignores the pool experiment.

## Configuration and Claude's rig plan

Keep the winning baseline settings and TCP pool in both arms. Change only:

```json
{"transport":{"mode":"pool","tcpBatchWrites":false}}
```

```json
{"transport":{"mode":"pool","tcpBatchWrites":true}}
```

Merge this field into the existing transport configuration; preserve worker
count, human grace, all broadcast settings, P863 budgets, native GotoPlan and
server GC. No new environment variable, port or service is introduced. It also
applies to TCP connections in UDP-enabled servers, including fallback clients.
An explicit thread-per-peer mode ignores the option. A boot banner identifies
the enabled experiment. Profiler `[send-peer]` rows add `inflightBytes`, lifetime
`supersededFrames` and lifetime `pollBlocked`; difference lifetime counters
between report windows.

Claude should alternate off/on/on/off at 200 players, at least three 120-second
windows at each of 0/30/80/120 ms one-way delay and 0% loss. Use the same private
netns and MTU 1500, capture qdisc drops plus `ss -tinm`, pose ages/counts/gaps,
ordered-event latency, send counters, tick p95, CPU/GC and disconnect reasons.
Repeat a stalled-reader and a bidirectional-blackhole trial with ordinary and
human peers; compare disconnect latency against the off arm. Then repeat the
normal 200-player clash with no impairment. Leave the option off until the rig
A/B accepts it. UDP comparison is a control, not required to use the TCP fix.

Reproduction (Release proof built, local container prepared as described above):

```sh
python3 tools/p895-transport-proof/run-tcp-delay-matrix.py \
  --container p898-netem --seconds 12 --repeats 3 \
  --output .scratch/p898/matrix-final
python3 tools/p895-transport-proof/summarize-tcp-delays.py \
  .scratch/p898/matrix-final --output .scratch/p898/summary.json
```

## Gates and evidence

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **PASS**, 0 warnings / 0 errors, final source |
| `tools/build-tools.sh` | **PASS**, all 97 projects, no warnings/errors |
| Server Native AOT, Release/linux-x64/self-contained | **PASS**, no IL2026/IL3050 or other warnings |
| New/affected focused tests | **16/16, three times**, no skips; covers both pool option arms, dedicated-mode controls, partial writes, shutdown ownership, small-frame dead readers and P670 fairness |
| Transport suites | Earlier 215-case cohort clean; final expanded filter **216 passed / 1 existing Perf-case flake** out of 217; that exact stdout-receipt case then passes 3/3 alone |
| One Fast tier | **FAIL**, 11,903 passed / 10 failed / 3 optional skips, 535.8 s; all 9 infrastructure checks pass; 3 timing offenders |
| Showcase never-parks with artwork | **5/5 pass**, serial standalone repetitions |
| Glossary links / parity scorecards / Python compile / diff whitespace | **PASS** |

Fast failure breakdown: five outside-repository path-fixture cases conflict with
worktree-local `TMPDIR` (all five fail 3/3 in that topology; all nine class cases
pass 3/3 with the same physical scratch mounted outside the checkout); one UI
profile case hits the read-only real profile (fails 3/3, then passes 3/3 with
scratch `XDG_CONFIG_HOME`); the known wingman `DefendTarget` failure persists
3/3; the P896 kill-evidence, missile-tip and pod-rescue cases each pass 3/3 alone.
The two additional passing Fast timing offenders also pass 3/3 below their
10-second limit. The final transport-only stdout receipt flake passes 3/3 alone.
No unrelated behavior was changed to hide these outcomes, and Fast was not rerun.
Details and exact method names are in [TEST_FLAKES.md](../TEST_FLAKES.md).

Compact, public-safe receipts including all isolation outcomes:
[gates.json](P898_assets/gates.json). Raw receipts/logs remain under
[`.scratch/p898/`](../../.scratch/p898/) (including `tests/`, `fast/`, final
build/publish logs, socket snapshots and send CSVs). Build/cache data stays out
of the public evidence. The compact report evidence totals less than 2 MB and adds
no media. The temporary netem container has been removed. No commits or pushes
were made.

The final AOT command was:

```sh
~/.dotnet/dotnet publish src/Allegiance.Server/Allegiance.Server.csproj \
  -c Release -r linux-x64 --self-contained -p:PublishAot=true \
  --artifacts-path .scratch/p898/aot-artifacts -o .scratch/p898/aot
```

The single Fast invocation was `tools/run-tests.sh --tier fast --no-build
--results-dir .scratch/p898/fast`. Focused and transport runs used the final
Debug build with `--no-build`; no warning suppressions were introduced.


## Native-intent footer

This is a modern TCP scheduling adaptation, **not** an implementation of a new
native simulation rule or DirectPlay wire compatibility. Native
`_Utility/Messages.cpp:558-563` selects guaranteed/high-priority/coalesced sends
without timeout and nonsequential best-effort sends with a 500 ms timeout;
`FedSrv/FedSrv.CPP:4181-4195` sends HEAVY/LIGHT poses best effort. Native
`clintlib/clintlib.cpp:2047-2087` supplies the 30-second link-dead intent and
six-second ping checks. TCP retains its existing reliable byte stream and
latest-wins pending queues. Classification and executable evidence are recorded
in [p898-tcp-batching.csv](../parity/p898-tcp-batching.csv); no additional native
parity credit is claimed for this default-off transport experiment.
