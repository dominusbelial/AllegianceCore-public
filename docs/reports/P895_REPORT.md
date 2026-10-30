# P895 — optional UDP transport

## Design note (written before implementation)

TCP pool remains the production default. `transport.protocol: "udp"` enables an
additional UDP listener on `transport.udpPort` (default: the TCP port); TCP remains
available. Clients opt in with `ALLEGIANCE_TRANSPORT_PROTOCOL=udp` or their session
configuration, attempt a bounded UDP handshake, then fall back to TCP. Protocol
selection is independent of the existing TCP `transport.mode` worker policy.

### Library decision

Evaluated upstream sources on 2026-10-29:

| Choice | Licence / deployment | Memory and scheduling | Decision |
|---|---|---|---|
| [LiteNetLib](https://github.com/RevenantX/LiteNetLib) | MIT; managed, IPv6; raw byte API is compatible with trimming/AOT without its reflection serializer | Packet pooling, reliable ordered/sequenced channels, fragmentation; library update/receive threads or manual polling. Its fixed reliable window is not congestion control; upstream [flow-control request](https://github.com/RevenantX/LiteNetLib/issues/544) remains relevant. Arbitrary coalesce keys and address-validation cookies still need a wrapper. | Good general library, but wrapper still owns the hard requirements here. |
| [ENet-CSharp](https://github.com/nxrighthere/ENet-CSharp) | MIT; IPv4/IPv6; native C dependency per platform, P/Invoke rather than reflection | Native packet allocations (custom allocator possible), service loop normally on one I/O thread, reliable channels/fragmentation and throttling. Requires native packaging across server AOT and client platforms; supersedable reliable keys need adaptation. | More deployment surface than this seam needs. |
| Focused implementation | Existing repository licence; BCL sockets/crypto only, no reflection or new native binaries; dual-stack socket | One worker per transport, bounded per-peer state, copied frame ownership; fixed receive/send scratch buffers; retained reliable fragments and reassembly allocations. | Selected. Larger protocol maintenance burden, explicitly experimental/default off pending rig/WAN A/B. |

Neither external candidate was linked or AOT-published in this branch; their
trimming/interop assessments are architectural, based on the upstream raw-byte
and polling APIs. The selected implementation was actually Native AOT published.
LiteNetLib has the smaller managed dependency/deployment burden; ENet-CSharp
requires coordinated native binary updates. Owning this protocol has the highest
maintenance responsibility, which is why the flag and impairment/semantic proofs
are retained.

The own-code allocation model is explicit: one copied payload/Frame per offered
frame, retained `Sent` objects per outstanding datagram, receive payload/fragment
bitmap per assembled frame, and socket endpoint/callback objects. Worker scratch
buffers and housekeeping lists are reused. Host FED accounting currently makes
a prefixed frame copy; this is not a zero-allocation transport. The measured
client CPU cost at 200 sessions is evidence against claiming otherwise.

### Channels and existing lanes

* `Send(..., true)`: reliable ordered frame sequence. `Send(..., false)` and
  `SendCoalescing(..., 0)` also use reliable ordered delivery, preserving TCP's
  key-zero FIFO contract (native's rare nonsequential unkeyed use is strengthened).
* `SendCoalescing(..., key != 0)`: independent latest-wins unreliable frame sequence
  per key. Pending/in-flight older generations are superseded. Best-effort frames
  expire after 500 ms locally; incomplete received frames expire too.
* `SendGuaranteedCoalescing(..., key != 0)`: independent reliable latest-wins
  sequence per key; retransmit until acknowledged or superseded. The newest value
  is never silently shed. Key zero maps to ordered delivery.
* TCP `ClassOf` remains the telemetry mapping: OneShot, BestEffortOrdered,
  ReliableStream, BestEffortStream. `NextDrainedIsOrdered` describes scheduler
  priority, not a separate delivery sequence. UDP prioritizes ordered data while
  reserving turns for streams so a lost join fragment does not stall poses.
* P813d park lane: guaranteed key-zero frames remain in the same ordered sequence;
  moving a later one-shot ahead of an earlier ordered frame would break P888
  game-flow order. The opt-in park telemetry is retained, without cross-sequence
  reordering. TCP behavior remains unchanged.

The concrete server keys remain unchanged (`Server.Broadcast.cs`):

| Key | Existing traffic | UDP channel |
|---|---|---|
| 0 | Logon/control, exports, joins, status/events, chat, kills, stage/results/rotation | Reliable FIFO; no coalescing |
| 1 | Legacy teammate SINGLE_SHIP_UPDATE batch (suppressed by native budget baseline) | Unreliable latest-wins |
| 2 | HEAVY/LIGHT remote ship updates, kept together in one frame | Unreliable latest-wins |
| 3 / 4 / 5 / 6 | Stations / asteroids / probes / missiles | Independent unreliable latest-wins keys |
| 7 / 8 / 9 / 13 | Owner missile lock / track warning / mining / cloak | Independent unreliable latest-wins keys |
| 12 | Owner SINGLE_SHIP_UPDATE, including owner mining bundle | Reliable supersedable |
| 10 / 11 | Retired / unused | No new traffic |

Both immutable-snapshot publishing sinks (`PublishMemory` / `PublishBytes`) keep
this mapping. Client, Bot and observed MCP control sends remain key-zero reliable.
TCP's two scheduler modes, `ClassOf`, `NextDrainedIsOrdered`, and park implementation
are unchanged; only their shared host type becomes `IServerTransport`.

### Wire, security and resource bounds

Versioned datagrams are at most 1200 bytes (safe under IPv6 minimum MTU). Frames
fragment at this boundary, with authenticated connection token, packet ID,
channel/key, frame sequence, total size and fragment index. Reliable fragments
have individual ACKs, independent of ordered frame delivery. Reassembly validates
all lengths/counts before allocation and bounds peer memory and sequence gaps.

A padded hello receives only an equal-sized stateless HMAC challenge bound to the
source endpoint, random client nonce and short time bucket. Echoing that cookie
proves return reachability before allocating a peer or sending game data. The
cookie becomes a connection token; all subsequent packets require the token and
same endpoint. Replay of an active handshake is idempotent. Retired tokens are
not revived. This prevents off-path spoofing/reflection amplification; it does
not encrypt traffic or protect against an on-path attacker. The application
LOGONREQ authentication remains independent.

ACK-derived smoothed RTT/variance determines a bounded RTO (100 ms–3 s), excluding
retransmitted samples (Karn). Per-peer byte-based Reno-style AIMD starts at ten
MTUs, grows on ACK, halves on timeout once per recovery interval, and has a two-MTU
minimum and 256-MTU maximum. A token-bucket pacer limits bursts and bytes/s even
when ACKs arrive in bursts. Unreliable packets participate in ACK/loss accounting;
expired unreliable packets are not retransmitted. Reliable overflow disconnects
rather than silently losing game flow. Control packets are small and bounded.

Keepalives keep NAT mappings alive; 30 s of silence loses the session. A new
socket/nonce/token is required on reconnect; no unauthenticated NAT rebinding.
Graceful close drains reliable work before repeated close notifications, with a
bounded close deadline. Shutdown is idempotent and joins the worker; callbacks
run outside transport locks to avoid client/server lock inversion.

Native references re-grepped before citing: `_Utility/DirectPlayTransport.cpp:208,270,287`
(IP service provider), `_Utility/Messages.cpp:558-563` and
`_Utility/DirectPlayTransport.cpp:548-551` (guarantee flags, nonsequential normal
traffic, 500 ms timeout). Native intent is delivery semantics, not DP8 wire
compatibility. Congestion control, cookies and bounded memory modernize that seam.

## Implementation, evidence and gates

Implementation and the requested bounded runs are complete; exact gate outcomes are recorded below.
No rig or WAN test hosts were contacted by this lane.

## Local measurements

All timed runs use a separate real server process and real `ConnectionSession`
scripted clients. Native wire pose ticks are correlated with the server’s
monotonic tick-start timestamps on the same workstation; measurements happen
before client decode acquires its world lock. Age includes tick construction,
transport scheduling and delivery. Gaps are between fresh arrivals for each
remote ship, excluding own-ship updates and duplicate/stale tick samples.
Reliable latency is a timestamped ordinary ordered chat frame sent at 2 Hz.
Each case has a 1.5 s post-seat warmup and a bounded approximately 12 s sample.
All clients stayed connected. CPU is process CPU seconds / sample wall seconds
(one fully occupied logical core = 1.0), separately for server and client/proxy.
Client CPU includes observer decoding but **excludes post-sample quantile work**.
The earlier prototype accidentally included sorting time; the boundary was
corrected and **all 18 published cases were rerun** with the corrected harness.

The workstation exposes 24 logical CPUs; SDK 10.0.400. P887 settings are explicit:
immutable snapshot + encode once, parallel build, native ship-update budget,
native GotoPlan, server/concurrent GC and TCP pool (two workers for this local
proof). Other experimental settings stay off. The controlled fixture disables
crowding damage in both arms; production defaults are unchanged. These are
transport measurements, not a three-match AIX or rig certification.

**Impairment qualification:** UDP actually loses/reorders/duplicates datagrams.
Unprivileged networking cannot drop kernel TCP segments here (effective and
bounding capability sets are zero; `unshare -Urn` returns permission denied).
The TCP proxy therefore delays 1200-byte stream chunks and models lost chunks
with a fixed 200 ms recovery stall while preserving subsequent bytes in FIFO
order. It exercises real TCP sockets/server/clients but is **not a kernel TCP
loss measurement**. The comparison applies to that controlled HOL model.
Claude’s actual `tc netem` experiment remains necessary.

The four-client cases use 30/80 ms **one-way** delay and ±10 ms seeded jitter
in both directions, plus 0/1/2/5% loss. Raw results:
[impairment.json](P895_assets/impairment.json). Times are milliseconds.

| Delay ±10 | Loss | Transport | Pose p50 / p95 / max | Longest remote gap | Reliable p50 / p95 / max |
|---:|---:|---|---:|---:|---:|
| 30 | 0% | TCP | 31.4 / 41.1 / 44.3 | 119.5 | 29.7 / 40.0 / 41.1 |
| 30 | 0% | UDP | 34.6 / 44.5 / 47.2 | 120.3 | 34.8 / 43.2 / 44.8 |
| 30 | 1% | TCP | 32.1 / 41.9 / 241.5 | 313.7 | 32.1 / 43.5 / 220.1 |
| 30 | 1% | UDP | 34.9 / 44.3 / 53.1 | 199.0 | 33.6 / 44.0 / 77.6 |
| 30 | 2% | TCP | 33.1 / 177.3 / 431.5 | 317.5 | 29.5 / 40.6 / 137.1 |
| 30 | 2% | UDP | 34.4 / 44.8 / 47.7 | 246.5 | 33.5 / 45.0 / 101.9 |
| 30 | 5% | TCP | 35.5 / 234.4 / 438.5 | 510.9 | 34.1 / 236.7 / 431.8 |
| 30 | 5% | UDP | 34.5 / 44.6 / 60.3 | 259.2 | 37.1 / 95.0 / 141.9 |
| 80 | 0% | TCP | 81.9 / 90.7 / 93.4 | 119.5 | 81.0 / 90.3 / 91.3 |
| 80 | 0% | UDP | 84.4 / 94.1 / 97.5 | 119.1 | 83.7 / 93.8 / 97.1 |
| 80 | 1% | TCP | 82.4 / 92.5 / 292.1 | 315.6 | 80.0 / 139.9 / 233.1 |
| 80 | 1% | UDP | 84.2 / 95.0 / 123.3 | 231.6 | 86.4 / 214.8 / 351.5 |
| 80 | 2% | TCP | 83.3 / 234.9 / 490.9 | 466.4 | 81.8 / 276.9 / 441.4 |
| 80 | 2% | UDP | 85.5 / 96.0 / 124.8 | 219.8 | 87.2 / 274.7 / 387.3 |
| 80 | 5% | TCP | 85.5 / 280.6 / 486.2 | 511.2 | 84.1 / 276.9 / 382.8 |
| 80 | 5% | UDP | 84.6 / 97.4 / 135.8 | 245.9 | 87.1 / 313.8 / 566.7 |

| Delay ±10 | Loss | Transport | Transport KiB/s | Server tick p50 / p95 / max (ms) | Server CPU cores | Client + proxy CPU cores |
|---:|---:|---|---:|---:|---:|---:|
| 30 | 0% | TCP | 33.4 | 0.63 / 1.13 / 3.42 | 0.057 | 0.083 |
| 30 | 0% | UDP | 98.3 | 0.43 / 1.01 / 3.26 | 0.061 | 0.189 |
| 30 | 1% | TCP | 33.6 | 0.52 / 1.14 / 6.47 | 0.050 | 0.085 |
| 30 | 1% | UDP | 97.2 | 0.50 / 1.17 / 3.48 | 0.061 | 0.171 |
| 30 | 2% | TCP | 33.6 | 0.52 / 1.27 / 2.83 | 0.050 | 0.088 |
| 30 | 2% | UDP | 96.3 | 0.45 / 1.20 / 3.23 | 0.062 | 0.184 |
| 30 | 5% | TCP | 33.5 | 0.54 / 1.16 / 3.42 | 0.051 | 0.090 |
| 30 | 5% | UDP | 93.6 | 0.48 / 1.19 / 4.68 | 0.060 | 0.181 |
| 80 | 0% | TCP | 33.5 | 0.57 / 1.35 / 6.63 | 0.056 | 0.079 |
| 80 | 0% | UDP | 97.3 | 0.51 / 0.95 / 2.81 | 0.061 | 0.169 |
| 80 | 1% | TCP | 33.4 | 0.62 / 1.17 / 2.91 | 0.051 | 0.075 |
| 80 | 1% | UDP | 97.0 | 0.45 / 1.21 / 6.12 | 0.042 | 0.183 |
| 80 | 2% | TCP | 33.9 | 0.50 / 1.23 / 6.48 | 0.056 | 0.089 |
| 80 | 2% | UDP | 95.2 | 0.56 / 1.35 / 6.38 | 0.043 | 0.172 |
| 80 | 5% | TCP | 33.5 | 0.64 / 1.14 / 6.31 | 0.054 | 0.085 |
| 80 | 5% | UDP | 92.9 | 0.44 / 0.96 / 6.12 | 0.057 | 0.174 |

Transport bytes in the proxy table include forwarded application framing and
UDP control/retransmission traffic in both directions; they exclude IP headers
and kernel TCP ACK/retransmission overhead. They are not full on-wire bandwidth.
At 5%/80 ms the model’s pose p95 is 280.6 ms for TCP versus 97.4 ms for UDP;
the longest inter-arrival gap is 511.2 versus 245.9 ms. At no loss, delay dominates
and the transports are close. These short seeded runs are not confidence intervals.

### Controlled 200-client LAN cost

A separate 200-client, zero-impairment pair uses the same P887 transport/build
baseline. Native crowding damage is disabled in **both** arms, so putting 200
ships into one observed sector does not turn the sample into a mass-ejection
burst. This is a fixture setting, not a production change. Fighter hulls are
separated on a fixed grid. Initial native-crowding diagnostics were discarded:
crowding ejected the sector and generated unequal death traffic. Raw controlled
results: [lan.json](P895_assets/lan.json).

| Metric | TCP pool | UDP |
|---|---:|---:|
| Pose p50 / p95 / max (ms) | 6.7 / 8.2 / 46.4 | 9.6 / 13.6 / 38.7 |
| Longest remote pose gap (ms) | 1000.3 | 510.0 |
| Reliable p50 / p95 / max (ms) | 0.5 / 1.1 / 3.9 | 2.8 / 4.7 / 6.9 |
| Server tick p50 / p95 / max (ms) | 4.88 / 5.60 / 8.61 | 4.98 / 6.43 / 11.35 |
| Server CPU cores | 0.424 | 0.350 |
| Client CPU cores | 0.504 | 1.096 |
| Pose samples | 1267443 | 1272683 |
| Reliable samples | 4800 | 5000 |
| Server transport egress (MiB/s) | 5.29 | 7.00 |
| Connected at end | 200 / 200 | 200 / 200 |

**LAN-neutral adoption is not established.** In this corrected sample UDP adds
5.3 ms to pose p95, 3.6 ms to reliable p95 and 32.3% transport egress.
Client CPU rises from 0.504 to 1.096 cores; the current one-worker-per-client
polling/ACK path needs attention before making a LAN-default claim. Server CPU
falls in this short pair, while tick p95 rises slightly. Maximum pose gap is
better in this particular pair, illustrating why isolated tail extrema require
longer repetitions. UDP stays experimental/default off; the rig/WAN A/B must
weigh these costs against actual WAN gains. No adoption is implied.

Scratch raw logs/TRX/binaries remain under `.scratch/p895/` (ignored). Only
small JSON summaries are retained in `P895_assets`; no media or private paths
are added. The proof has no remote-host operations or synthetic CPU load.

## Rig and WAN plan for Claude (not executed by this lane)

Keep the existing b55 scenario/core/map/civilizations/seed and three complete
matches per arm. Derive two complete configurations from that same private base
on the rig; do not use the small local proof arena as the rig workload:

```bash
mkdir -p "$OUTPUT"
for protocol in tcp udp; do
  jq --arg protocol "$protocol" '
    .transport.protocol = $protocol |
    .transport.mode = "pool" |
    .transport.udpPort = .port |
    .transport.maxConnectionsPerIp = 512 |
    .broadcast.immutableSnapshot = true |
    .broadcast.encodeOnce = true |
    .broadcast.parallelBuild = true |
    .broadcast.shipUpdateModel = "native" |
    .sim.nativeGotoPlan = true |
    .sim.nativeBroadcastOrder = false |
    .receive.decouple = false |
    .broadcast.interestTiers = false
  ' "$BASE_CONFIG" > "$OUTPUT/p895-$protocol.json"
done
```

Use the ordinary Native AOT/server-GC deployment for each arm and map the game
port for both TCP and UDP. Set `ALLEGIANCE_TRANSPORT_PROTOCOL=tcp` in both bot
workers for the control, then `udp` for the treatment. Keep the existing scale-13
showcase, two bot workers, finale policy, match rotation and random-civ seed
identical. Preserve the rig's existing worker-count and tick-profiler settings;
no other experiment changes. Verify high-bit UDP peer ids in the treatment and
ordinary TCP ids in the control. Confirm every expected bot is connected before
measuring; a fallback arm is not a UDP result. Run three whole game-over →
next-match cycles, saving server profiler/GC/egress/disconnect logs, bot logs and
player-count samples. Score each arm using the existing schema-2 rig card:

```bash
python3 scripts/allegiance-index.py \
  --server "$OUTPUT/tcp-server.log" --samples "$OUTPUT/tcp-samples.csv" \
  --bots "$OUTPUT/tcp-worker-1.log" "$OUTPUT/tcp-worker-2.log" \
  --rig docs/benchmarks/rig-221.json --label p895-tcp --json "$OUTPUT/aix-tcp.json"
python3 scripts/allegiance-index.py \
  --server "$OUTPUT/udp-server.log" --samples "$OUTPUT/udp-samples.csv" \
  --bots "$OUTPUT/udp-worker-1.log" "$OUTPUT/udp-worker-2.log" \
  --rig docs/benchmarks/rig-221.json --label p895-udp --json "$OUTPUT/aix-udp.json"
```

Compare AIX/server-core and AIX/total-core, busy-window tick p95, GC pauses,
allocation, bandwidth, disconnects, own/remote pose gaps and match outcomes. UDP
must earn adoption on the LAN baseline as well as under impairment. Keep it off
if extra bandwidth/CPU or changed combat outcomes offset latency gains.

For the client mini PC, use the actual kernel `tc netem`, applying it to **both
receive and send** game traffic, while leaving the lobby/discovery traffic out.
`CLIENT_IFACE`, `SERVER_IP`, `GAME_PORT` are the test client's interface, the
server IPv4 address, and its common TCP/UDP game port. Save the original qdisc
configuration first and run this only on the dedicated test interface. The
following is the 80±10 ms / 2% loss case (substitute delay 30/80 and loss 0/1/2/5):

```bash
# On the client mini PC only; Claude runs these commands.
sudo modprobe ifb numifbs=1
sudo ip link set ifb0 up
sudo tc qdisc add dev "$CLIENT_IFACE" root handle 1: prio
sudo tc qdisc add dev "$CLIENT_IFACE" parent 1:3 handle 30: netem \
  delay 80ms 10ms distribution normal loss random 2%
sudo tc filter add dev "$CLIENT_IFACE" parent 1: protocol ip prio 3 u32 \
  match ip dst "$SERVER_IP/32" match ip dport "$GAME_PORT" 0xffff flowid 1:3
sudo tc qdisc add dev "$CLIENT_IFACE" handle ffff: ingress
sudo tc filter add dev "$CLIENT_IFACE" parent ffff: protocol ip u32 \
  match ip src "$SERVER_IP/32" match ip sport "$GAME_PORT" 0xffff \
  action mirred egress redirect dev ifb0
sudo tc qdisc add dev ifb0 root netem delay 80ms 10ms distribution normal loss random 2%
```

Run TCP and UDP with the same stationary observer and moving scripted peers,
30 s warmup then at least 120 s per case, three repetitions. Record actual
transport selection, native wire pose tick/time plus arrival timestamps, maximum
per-remote-ship gaps including the final silent tail, guaranteed stage/chat
latency, packet captures/bytes, server tick/CPU and client CPU. Align server/client
clocks (or correlate capture sequence/timestamps with measured clock offset)
before comparing one-way pose age. Kernel TCP retransmissions in the captures
are required evidence; the local proxy's fixed recovery model cannot replace
this run. Repeat the no-loss LAN pair after removing impairment:

```bash
sudo tc qdisc del dev "$CLIENT_IFACE" ingress
sudo tc qdisc del dev "$CLIENT_IFACE" root
sudo tc qdisc del dev ifb0 root
sudo ip link set ifb0 down
```

Restore the saved qdiscs if they differed from the dedicated test setup. No rig,
mini PC, Mac, remote SSH, or remote writes were used in this implementation lane.

## Verification and delivery

The shared contract cases run with explicit TCP and UDP constructors. Raw TCP
framing, socket backpressure, writer-pool and private queue-introspection tests
remain TCP-specific; UDP does not pretend to implement TCP's byte-stream wire
format. The complete existing transport namespace is also exercised as a
regression gate.

* UDP proofs: P888-style guaranteed game-flow order with both park settings,
  5% loss/reordering/duplicates, latest pending keys, reliable supersedable
  delivery across a blackout, zero-key FIFO, large ordered/keyed fragmentation,
  invalid/spoofed/cross-endpoint/retired-cookie traffic, memory overflow, 500 ms
  best-effort expiry, no-ACK congestion debt despite supersession, IPv6,
  one-way/symmetric timeouts, fresh reconnect, graceful drain, mixed peer ids
  and handshake fallback.
* Metadata proof: real heartbeat → lobby registration → public listing,
  rollback removing `udpPort`, and invalid UDP port sanitization.
* Actual client tests: RetriedConnect plus the P660 next-match cohort on both
  protocols; observed MCP diagnostic/ref-field seams; five TCP never-parks runs
  and one UDP never-parks run.
* The current solution compiles with zero warnings. All 95 tools build. The
  server Native AOT publish is clean, with no IL2026/IL3050. New UDP/shared cases
  pass 3/3. The Fast and composite showcase exceptions remain visible in the
  gate table and flake ledger.

The server host lifecycle seam now covers peer closure, loopback/human
classification and accounting, so CRC rejection, logoff drain and privilege
checks apply to UDP peers too. TCP's worker/queue implementation remains intact.
The game browser forwards the advertised UDP port into the shared session;
there is no HUD/rendering change, so Classic and Modern use identical behavior.
No commits or pushes were made.

### Final gate receipts

[Machine-readable verification](P895_assets/verification.json), with raw local
logs/TRX in [ignored task scratch](../../.scratch/p895/). Final verification was
completed on 2026-10-30. No commits or pushes were made.

| Check | Result |
|---|---|
| Final solution build | PASS, 0 warnings / 0 errors |
| `tools/build-tools.sh` | PASS, all 95 projects, final rerun after the protocol fixes |
| Native AOT publish | PASS, no IL2026/IL3050; real published-server UDP logon smoke also validates, with no TCP fallback |
| 19 new UDP/metadata cases + 7 shared contract cases | PASS, 26/26 in each of three final isolated runs, including constructor keepalive/startup ordering |
| Affected TCP admission/thread checks | PASS, 3 isolated repetitions |
| Observed MCP diagnostics | PASS, 21/21 in each of 3 repetitions |
| Complete transport namespace | PASS, 185/185 after the final fixes; TCP-specific internals remain TCP, shared cases explicitly run both transports |
| RetriedConnect + P660 next-match cohort | PASS, 9/9 ×3 on TCP and 9/9 ×3 on UDP |
| Required TCP showcase series | Parking bound held 5/5; composite test 4/5 (one zero-hit combat-coverage miss) |
| Three extra TCP showcase qualifications | Composite pass / pass / fail; parking bound held in all three |
| UDP showcase | PASS, including combat and the parking bound |
| ONE Fast tier | FAIL retained: 11,757 pass / 3 fail / 3 optional skips; 1 passing timing offender; isolation below |
| Compose / glossary / P895 parity / whitespace | PASS; four parity rows are config-only, with no production headline credit |

One startup correction reserves the greeting before publishing `IsConnected`,
so a constructor-started keepalive cannot take ordered frame zero. A second
lifecycle correction sends a close notice over a surviving uplink after a
one-way timeout, allowing the server to retire the old connection before a
reconnect. Both have deterministic regression coverage. A receive-side keepalive
was initially mistaken for an unexpected replay response in a new raw-socket
test; draining the close notification corrected that test, and the final runs
pass. No packet-security assertion was relaxed.

The TCP composite showcase miss had reached 29/30 ships, 1,038 shots and zero
hits after 480 s, with a 0.000 s maximum parked-with-target sample. The additional
three filtered qualifications were pass/pass/fail; the latter again missed
combat coverage without violating the parking bound. These ran as separate
processes concurrently (actual scenario work, no synthetic load). The report
preserves this scenario sensitivity rather than calling the combined test green.
No flight, aiming, damage, scouting or combat policy was edited. UDP’s full
showcase test passed. Every per-run summary is in the verification JSON and the
remaining scenario issue is ledgered.

### Fast result and isolation

The ONE Fast run retained **FAIL**: 11,757 passed, 3 failed, 3 optional skips
(11,763 executions, 536.3 s). All nine infrastructure checks passed. The passing
P777 measurement exceeded the calibrated 10 s limit at 10.656 s; isolated runs
were 9.590/9.701/9.590 s. [Compact Fast receipt](P895_assets/fast.json).

The failed NPC defend case fails 3/3 in this tree and 3/3 on unmodified HEAD
`b567ee1f`. Auto-seat is pass/fail/fail here and fail/fail/pass on HEAD. The
lifepod watcher case is pass/pass/fail here (an additional block is
fail/pass/pass), while HEAD initially passes 9/9 with driver-first intake.
Reordering only the scratch baseline fixture's connection/logon initiation to
B/driver/C reproduces its identical missing-relay assertion 3/3 against unchanged
HEAD production. Its conditional same-side watcher assumes a SINGLE_SHIP_UPDATE
relay, while the native baseline carries remote poses in heavy/light updates.
This is a fixture/intake-order dependency, not a UDP treatment. The exact
[control diff](P895_assets/baseline-fixture-order.diff) and
[every isolation block](P895_assets/isolation.json) are retained; mixed results
are not hidden behind a clean-pass label. The cases are ledgered in
[TEST_FLAKES.md](../TEST_FLAKES.md#p895-verification-observations-2026-10-30).

The final startup interleaving correction was made after that single Fast run.
Its affected UDP/shared cases were requalified 3/3 and the complete transport
namespace was rerun afterward (185/185); the whole Fast tier was not repeated.
No unrelated production policy or fixture assertion was weakened to turn the
gate green.

Optional recursive scratch cleanup was rejected by automatic approval review
because `rm -f` style commands are prohibited. The report-linked ignored scratch
artifacts are retained; no cleanup permission was requested and no tracked
files were removed.

### Native-intent footer

Native used the DirectPlay IP service provider
(`_Utility/DirectPlayTransport.cpp:208,270,287`) and selected guaranteed/high
priority/coalescing versus nonsequential 500 ms normal sends
(`_Utility/Messages.cpp:558-563`, `_Utility/DirectPlayTransport.cpp:548-551`).
Guaranteed stage and game-over traffic is explicit in
`FedSrv/fsmission.cpp:1750-1762,3435-3438`; remote heavy/light updates are
non-guaranteed at `FedSrv/FedSrv.CPP:4189,4195` (all citations re-read locally).

This implementation matches that delivery intent through reliable ordered game
flow and independent latest-wins pose channels. Guaranteed coalescing retains
the newest value until ACK or supersession; expiry never silently sheds that
value. Key-zero best-effort calls are intentionally strengthened to reliable
FIFO to preserve the port's existing contract. Cookies, per-peer resource bounds,
IPv6, fragmentation, ACK-derived RTT/RTO and paced AIMD congestion control are
modern transport infrastructure; this is not a DirectPlay wire-compatible
implementation. The LAN/WAN tradeoff remains an operator A/B decision. TCP pool
stays the default.
