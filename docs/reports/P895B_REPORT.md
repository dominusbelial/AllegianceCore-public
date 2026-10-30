# P895b — UDP transport overhead

## Profile recorded before optimization

Instrumentation-only build, same P895 proof command with `--seconds 12` and
`--output .scratch/p895b/before`. The first clean 30±10 ms, four-client sample
already isolates the overhead (successful local socket sends, both endpoints;
the proxy can duplicate or discard them). These counts exclude warmup.

| Cost | Before, 12-second clean sample | Cause |
|---|---:|---|
| Data payload | 378,017 bytes | Application frames |
| Data headers | 373,256 bytes / 7,178 packets | 52 bytes repeated for every fragment/frame |
| ACK-only | 392,132 bytes / 7,541 packets | One 52-byte ACK per received fragment, including duplicates |
| Keepalive ping + pong | 5,148 bytes / 99 packets | Both endpoints send every two seconds |
| Handshake | 0 bytes in sample | Excluded by warmup; four padded 64-byte messages per connection |
| Retransmissions | 0 | No packet loss |
| Tokens (subset of headers/control above) | 237,088 bytes | 16-byte endpoint-bound token per packet |
| Published 200-client CPU ([P895](P895_REPORT.md)) | TCP 0.504 cores; UDP 1.096 cores | One polling worker per client plus per-packet ACK work |
| Worker turns / empty receives | 28,680 / 28,680 | Approximately 478 polls/s per transport; each turn ends in a WouldBlock exception |
| First-fragment queue wait | 1.069 ms mean; 2,306/7,166 over 1 ms | Send waits for next 2 ms polling turn even with pacer budget |
| Pose p95 | TCP 40.9 ms; UDP 44.6 ms | Poll waits at both endpoints plus the unchanged proxy scheduling |

This table was written before changing the scheduler or UDP wire format.
The complete qualification follows below. TCP pool remains the default; UDP remains opt-in.

## Changes

* ADU2 shares one 32-byte authenticated header across already queued frames.
  Bounded integer encoding replaces the 20-byte per-record fields; packet IDs,
  sequences and signed keys retain their complete ranges. Fragment size is
  1,144 bytes and every datagram remains at most 1,200 bytes. No batching timer
  delays data.
* Selective ACK IDs use bounded integer encoding, piggyback on outbound data
  and control packets, and otherwise flush after 20 ms or 64 pending IDs.
  ACK lists are bounded at 128 IDs, 64 per packet; an old reliable retransmit
  remains acknowledgeable regardless of how many newer packets arrived.
  A datagram is ACKed only after all its fragments were retained or superseded.
* Clients block in receive. Producers send immediately when pacing/congestion
  permits; a one-shot timer serves the next real ACK, pacing, retry, keepalive
  or idle deadline. Idle sessions have no polling wakeups. Callbacks serialize
  outside the transport lock; shutdown interrupts blocking receive and remains
  idempotent. The blocking receiver retains its socket reference across a
  timer-callback shutdown; producer/timer socket failures use the receive failure
  path. A dedicated timeout-callback disposal test covers that lifecycle.
* The server uses receive notifications, an enqueue readiness set and a bounded
  deadline heap, rather than scanning every peer on every enqueue wake. The heap
  compacts at `max(1024, 4 × peers)` entries. Sends, congestion debt, retransmission
  and supersession still share the same serialized protocol state. Dropped
  peers release payload/reassembly storage immediately, even if a stale heap
  entry temporarily retains the peer object.
* Any successful authenticated traffic refreshes the keepalive deadline. Active
  sessions no longer exchange redundant pings. The 128-bit token, padded HMAC
  address-validation handshake, endpoint binding, admission/memory limits and
  retired-token protection remain. ADU1 peers cannot handshake with ADU2; the
  existing bounded TCP fallback handles a version mismatch.

TCP wire/framing, pooled-default policy, simulation and broadcast settings are
unchanged. UDP remains default off. The game-flow test now forces one real drop
in addition to seeded 5% loss: batching can otherwise fit its small flow into too
few packets for the seeded run to lose one. No delivery assertion was weakened.

## Reproduction and measurement limits

Base: `69c37dbb`. SDK 10.0.400, 24 logical CPUs, ordinary external artwork.
The first matrix used only the added counters over the original transport;
a separate unmodified-HEAD source copy in task scratch repeated the LAN control
without simultaneous builds. The first matrix's LAN arm overlapped development
builds, so the report's before-LAN table uses that separate repeat. No repository
checkout was reverted or stashed. Final performance runs were serialized without
our gates/showcase jobs running alongside them. Recorded load averages are in
all matrix JSON files; this is still a shared host and these short samples are
not confidence intervals.

```bash
# With local.env exported, SDK on PATH, and TMPDIR/NuGet HTTP cache in task scratch:
~/.dotnet/dotnet build tools/p895-transport-proof -c Release
python3 tools/p895-transport-proof/run-matrix.py --seconds 12 --output .scratch/p895b/before
# Original HEAD proof in the isolated scratch source copy:
python3 .scratch/p895b/baseline-src/tools/p895-transport-proof/run-matrix.py \
  --seconds 12 --lan-only --output "$PWD/.scratch/p895b/before-lan-isolated"
# Same workload/commands against the final implementation:
python3 tools/p895-transport-proof/run-matrix.py --seconds 12 --output .scratch/p895b/after-final
```

The P895 arena/settings are unchanged: native ship budget/GotoPlan, immutable
snapshots, encode-once, parallel build, server GC and TCP pool with two workers;
crowding damage is disabled in both arms to preserve 200 connected ships.
Each case retains 1.5 s seated warmup and about 12 s measurement. Server and
client CPU are sampled before quantile sorting. Pose age correlates server tick
start with actual client wire arrival before the world lock. All cases retain
all expected server/client connections, with no fallback in the explicit UDP arm.

**Bytes retain P895's scope:** proxy cases count forwarded application transport
bytes in both directions; direct LAN counts server transport egress. IP/UDP
headers, kernel TCP ACKs and kernel TCP retransmissions are excluded. These are
not full physical-wire bandwidth ratios. The TCP impairment proxy preserves
FIFO and models loss by a 200 ms recovery stall; only UDP loses actual datagrams.
Kernel `tc netem` remains necessary for an actual TCP-loss comparison.

### Cost attribution after optimization

Clean 30±10 ms/four-client sample, both socket endpoints, excluding warmup;
ACK-ID and token rows are subsets of the other byte rows.

| Counter | Before | After |
|---|---:|---:|
| Data datagrams | 7,178 | 4,131 |
| Data header bytes | 373,256 | 166,718 |
| ACK-only packets | 7,541 | 1,362 |
| ACK-only bytes | 392,132 | 49,267 |
| Encoded ACK-ID bytes (subset) | 0 | 7,815 |
| Token bytes (subset) | 237,088 | 87,888 |
| Keepalive bytes | 5,148 | 0 |
| Retransmissions | 0 | 3 |
| Worker turns | 28,680 | 8,718 |
| Empty receive polls | 28,680 | 0 |
| Mean first-send queue wait | 1.069 ms | 0.053 ms |
| First sends waiting over 1 ms | 2,306/7,166 | 14/7,156 |

Before, five workers made 2390 polling turns/s. After, wake
sources were 5,692 receive notifications and 1,301
deadline expirations; 7,156 offers either sent immediately on
the client producer or signaled the server readiness set. Signals coalesce and
producer sends do not require a client worker wake, so those counters are not
additive CPU samples. The 200-client CPU comparison below measures the resulting
process cost; wake counts identify the changed scheduling work, not per-function
CPU percentages. At 200 clients plus server, empty polls fell from
1,127,470 to zero and worker turns
from 1,128,887 to
371,584. The idle regression
proof observes zero client worker/timer turns during a settled 200 ms interval
with a long keepalive deadline; the ordinary two-second keepalive still wakes
an otherwise idle connection when due.

There are 3 clean-case retransmissions (187
bytes), despite zero configured loss. Delayed ACKs, jitter and scheduling can
cross the minimum RTO; this is retained in the measured cost. The 5%/80 ms sample
records 234 before and
282 after retransmissions.
Counter summaries: [costs.json](P895B_assets/costs.json).

### 200-client LAN before/after

| Metric | Before TCP | Before UDP | After TCP | After UDP |
|---|---:|---:|---:|---:|
| Pose p50 / p95 / max, ms | 6.94/9.83/44.90 | 10.37/22.54/73.32 | 6.93/10.14/32.26 | 8.14/14.22/46.69 |
| Longest remote gap, ms | 988.2 | 530.3 | 519.6 | 995.2 |
| Reliable p50 / p95 / max, ms | 0.64/1.58/6.72 | 2.67/9.48/31.76 | 0.68/2.19/3.92 | 0.88/2.14/7.00 |
| Client CPU, cores | 0.519 | 1.188 | 0.530 | 0.406 |
| Server CPU, cores | 0.455 | 0.377 | 0.465 | 0.715 |
| Server tick p50 / p95 / max, ms | 4.92/6.06/14.93 | 5.17/11.79/25.53 | 4.94/6.43/26.36 | 4.90/6.45/12.17 |
| Server transport MiB/s | 5.29 | 6.99 | 5.28 | 6.00 |
| Pose samples | 1,267,504 | 1,278,026 | 1,277,992 | 1,267,443 |
| Connected server / clients | 200/200 | 200/200 | 200/200 | 200/200 |

Mean first-send queue wait at 200 clients plus server fell from 3.715 to 0.464 ms. This includes pacing/congestion and queueing, at the counter's 1 ms clock resolution.

[Original matrix](P895B_assets/before.json), [isolated before LAN](P895B_assets/before-lan.json),
[final matrix](P895B_assets/after.json). Original published P895 values remain
in [P895_REPORT.md](P895_REPORT.md); fresh controls here avoid silently treating
measurements made on different host-load windows as identical.

### Same impairment matrix

One-way delay ±10 ms; times are milliseconds. `T/U` means TCP/UDP. Both protocols
retain identical seeds and fixture commands; packetization changes which actual
UDP datagrams the seeded proxy drops. All other CPU, tick, p50/max and byte
measurements remain in the linked JSON, including the TCP model qualification.

| Delay | Loss | Before pose p95 T/U | After pose p95 T/U | UDP longest gap before/after | UDP reliable p95 before/after | After bytes U/T |
|---:|---:|---:|---:|---:|---:|---:|
| 30 | 0% | 40.9/44.6 | 49.1/41.9 | 121.3/119.5 | 43.3/40.8 | 1.516× |
| 30 | 1% | 42.4/44.4 | 127.3/42.0 | 151.1/203.0 | 43.6/41.6 | 1.477× |
| 30 | 2% | 183.9/51.8 | 185.8/42.1 | 221.3/205.8 | 127.6/92.7 | 1.499× |
| 30 | 5% | 249.1/48.9 | 235.6/42.2 | 312.8/210.1 | 142.1/125.4 | 1.551× |
| 80 | 0% | 94.9/94.3 | 90.9/92.2 | 120.5/188.0 | 93.7/91.7 | 1.525× |
| 80 | 1% | 135.5/94.9 | 191.2/92.4 | 215.3/214.9 | 131.2/159.7 | 1.508× |
| 80 | 2% | 233.5/95.4 | 238.7/92.3 | 243.4/249.0 | 236.1/244.4 | 1.471× |
| 80 | 5% | 283.3/101.5 | 285.5/92.6 | 286.9/273.6 | 274.1/282.4 | 1.505× |

### Targets

| Target | Result | Evidence / remaining reason |
|---|---|---|
| Bytes ≤1.5× TCP | MISSED within P895's accounting scope | Clean proxy 1.516×; worst proxy case 1.551×; 200-client egress 1.137×. Opportunistic batching preserves immediate sends, so batch size depends on when producers and the single sender run. Sparse frames still carry the 32-byte header/128-bit token. Batching takes already queued work to preserve immediate sends. |
| Client CPU ≤1.3× TCP | MET | 0.767× (0.406 vs 0.530 cores). |
| LAN pose p95 within +1 ms of TCP | MISSED | UDP 14.22 vs TCP 10.14 ms: +4.09 ms. Poll waits are removed, but the single UDP sender/state lock still serializes packet construction/socket work versus the two-worker TCP pool. Higher server CPU (0.715 vs 0.465) and tick p95 (6.45 vs 6.43 ms) are consistent with residual sender/scheduling cost; they are not a complete trace decomposition of that tail. |
| No loss-resilience regression | QUALIFIED / not an unconditional pass | All loss-case pose p95 values improve or remain close, and 5%/80 ms gives 92.6 vs modeled TCP 285.5 ms, gap 273.6 vs 514.5 ms. Some UDP longest-gap/reliable tails worsen versus the fresh old-UDP samples. Batching correlates loss across records and changes the seeded drop sequence; 12 s extrema cannot establish a statistical no-regression bound. Semantics/recovery tests are mandatory independent evidence below. |

The byte/latency results and mixed tail extrema keep UDP experimental. This
lane does not claim LAN-neutral adoption or completion of a kernel-loss trial.

## Gate and delivery

[Machine-readable receipts](P895B_assets/verification.json),
[Fast receipt](P895B_assets/fast.json), and
[non-task failure ledger](../TEST_FLAKES.md#p895b-gate-2026-10-30).

| Check | Result |
|---|---|
| Solution build | PASS, 0 warnings / 0 errors |
| `tools/build-tools.sh` | PASS, all 96 projects |
| Server Native AOT publish | PASS; no IL2026 / IL3050 |
| New + P895 + shared contract cohort | PASS, 40/40 ×3 in the final clean isolated block (14 new cases); shared contracts construct both TCP and UDP |
| Complete transport namespace | PASS, 199/199; TCP framing/worker internals remain TCP-specific |
| RetriedConnect + P660 cohort | TCP PASS, 9/9 ×3 in the clean repeat; UDP PASS, 9/9 ×3; the raw-TCP retry fixture deliberately incurs bounded fallback in the UDP-preference arm |
| Showcase never-parks | PASS, TCP 5/5 and UDP 1/1, including combat coverage |
| ONE executed Fast sweep | FAIL retained: 11,863 passed / 19 failed / 3 optional skips; 1 passing timing offender; 11,885 executions, 538.3 s; 9/9 infrastructure checks |
| Every non-task failure / timing offender | Three selected-method isolated repetitions recorded; persistent and mixed results retained, not called clean |
| Glossary / parity / Compose / whitespace / path hygiene | PASS |

The final 40-case isolated block ran after the shutdown and retirement fixes,
without other task test work. The three clean TCP client cohort repetitions also
pass. UDP-preference client runs include a TCP-only retry fixture, which causes
20 bounded UDP handshake fallbacks and explains about 60 seconds of extra fixture
runtime; the real-server P660 paths use the UDP listener. The new timeout-callback
shutdown test passes in both the focused blocks and Fast.

The Fast failures consist of 11 lobby methods that hit the host inotify limit
(each passes 3/3 alone), five path-fixture executions that assume temp directories
are outside a repository (their four methods each fail 3/3 with the required
in-repository `TMPDIR`), a profile-editor finally-block that cannot restore a
read-only user profile (fails 3/3, then passes 3/3 with a writable scratch
`XDG_CONFIG_HOME`), the known NPC defend assertion (fails 3/3), and docking
status-history intermittency (fail/fail/pass). The timing offender is functional
3/3 at 9.892/9.982/10.072 s; one isolated run remains above the unchanged 10 s
limit. A separate unchanged TCP retry fixture observed 19 rather than 20 accepts
during overlap; it passes 3/3 alone and in the clean client-cohort repetitions.
No unrelated production rule, test assertion, timing limit or quarantine changed.

One orchestration mistake is retained in the receipts: interrupting early shell
wrappers did not stop their child qualification processes. Their remaining work
overlapped and their duplicate Fast launch requests waited on the shared runner
mutex, then failed the existing result-directory guard before executing tests.
Exactly one Fast sweep executed. The final clean focused block was run after
that overlap. Performance windows were separate from all gate/showcase work.

Showcase cases used two concurrent real scenarios, with load snapshots retained;
no synthetic load was added. Each case has its full summary in verification.json.

Raw commands, logs and TRX are retained in [ignored task scratch](../../.scratch/p895b/).
Only small JSON summaries are in `P895B_assets`; no media was added. NuGet's HTTP
cache and temporary directory were redirected into task scratch because the
sandbox cannot write the shared user cache. No commits, pushes, stashes, remote
hosts, rig/mini/Mac operations or synthetic CPU load were used.

## Updated rig/WAN plan

Rig adoption testing remains deferred while the local byte/latency targets are missed.
The next local profile should address sparse-packet header cost and the serialized
UDP sender. Keep the complete baseline configurations, three full matches per arm and
bidirectional kernel `tc netem` matrix from [P895](P895_REPORT.md#rig-and-wan-plan-for-claude-not-executed-by-this-lane).
No port/env/service/default changes are needed. Both endpoints must use this
ADU2 build and treatment must verify actual UDP selection, not fallback. Add the
packet-kind/ACK/header and wake-source counters to each receipt and collect full
IP captures so UDP/TCP kernel overhead is counted equally. Keep identical
native budget/GotoPlan, immutable snapshots, encode-once, parallel build and
TCP pool settings. Repeat no-loss LAN before impaired runs; investigate the
remaining single-sender/lock cost before using the rig run as an adoption gate.
Use at least three 120 s impaired samples after 30 s warmup to judge longest-gap
and reliable tail changes; the short local proxy maxima do not settle that
question. Claude owns those remote/kernel runs; none were attempted here.

## Native-intent footer

Native `_Utility/Messages.cpp:558-563` and
`_Utility/DirectPlayTransport.cpp:548-551` select guaranteed/high-priority/coalesced
sends without expiry and normal nonsequential sends with 500 ms expiry. Those
intents and the P895 ordered/latest-wins contracts remain tested; the four rows
in `docs/parity/p895-transport.csv` remain config-only. ADU2 packet layout,
integer encoding, HMAC tokens, ACK coalescing, congestion pacing and thread/timer
scheduling are modern transport infrastructure, not claims of native DirectPlay
wire compatibility. No new native gameplay rule or production parity credit is
claimed.

## Claude review notes (2026-10-30)

Parsing, ACK-after-retain and retransmit filtering checked by reading the code. One change: a keepalive whose send fails
now waits a full interval; before, a stale `LastSend` made the next wait 0 until the socket buffer drained.
Not a report target but worth carrying into any rig run: LAN **server CPU rose to 0.715 cores vs TCP 0.465** (1.54×; it
was 0.377 before P895b), and the longest remote gap in that LAN sample was 995 ms vs 520 ms. `PeerNextDue` walks every
in-flight packet, stream and assembly per peer per tick, which is the first place to look.
