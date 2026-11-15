# P1025 — R1 F4 + F5 + F8: UDP send errors, UDP reassembly memory, reconnect-resume orphan ship

Owner findings: **F4** (one UDP reply the OS refuses stops the listener for everyone), **F5** (UDP reassembly buffers
allocated at the peer-declared size), **F8** (a seated connection can resume a parked seat, orphaning its first ship;
`reconnect.enabled` only) from Fable's review `R1_REPORT.md`.

Base: Claude's master at dispatch (`0498f99c`). Status: complete in the working tree, **not committed**.

---

## 0. Answers in one paragraph

- **F4.** `UdpTransport.RawSend` filtered its `SocketException` catch down to three benign errno values, so any other
  send failure (EINVAL from a port-0/unroutable return address, EPERM from an egress rule, ENETUNREACH, a full
  conntrack table) escaped to the worker's fatal handler, which dropped **every** UDP peer and stopped the listener for
  good — from one 64-byte unauthenticated Hello. `RawSend` now catches every `SocketException` and returns "not sent",
  exactly as `SendTraversal` already did; the receive-side handlers keep their fatal semantics. This is native parity
  of intent: native's `SendMessages` discards the send HRESULT (`_Utility/Messages.cpp:666-674`), so a refused
  destination never reaches the messaging loop. Proof test unskipped unchanged and passing.
- **F5.** Inbound reassembly charged and allocated the *peer-declared* frame size (`new byte[size]`) against the
  **send**-queue ceiling (`MaxBufferedBytes` = 32 MiB, `DualTransport.cs:24`), reused the 4096-entry reorder window as
  its assembly cap, and never expired a *reliable* assembly. A handshaken peer could therefore pin 32 MiB of heap per
  connection with a few 1.2 KB datagrams and a 30 s keep-alive, and 1024 peers could pin 32 GiB. The receive side now
  has its own named ceilings: it reserves **only the bytes that arrived** and grows toward the arriving fragments, is
  capped at **512 KiB and 256 assemblies per peer** (both well under 1 MiB / far below 4096), and abandons an
  incomplete reliable assembly after **5 s**. The section-4 lead "UDP assembly scan" was **real and fixed**: the
  supersede path walked every assembly under the state lock on each newer keyed record; it is now one dictionary
  removal (the invariant "at most one assembly per stream key" makes it exact). Five new tests: four of them
  fail before the fix and pass after, the fifth guards that a frame still arriving is never abandoned.
- **F8.** The P813c resume ran *before* the P646 one-logon-per-connection check, so a connection that already owned a
  ship could take a parked seat; `CompleteJoinAndLaunch` then overwrote `_peerShips[from]` and the first ship stayed in
  its sector owned by nobody — the P646 orphan, repeatable for free. The resume is now gated on "this connection owns
  no ship". The choice between refusing and retiring the seated ship is **refusal**, because native's logon is once
  per connection (`FedSrv.CPP:4830-4831` assert) and native has no resume flow at all (P813c is the port's
  experimental addition): the reconnect-free model says a seated connection keeps its ship and the parked seat stays
  for its owner — retiring a ship nobody asked about would be a larger, non-native behaviour change. The frame then
  falls through to the existing P646 refusal (no ack, no allocation, rate-limited log). Proof test unskipped and
  passing; two focused tests added.
- **Tests.** 2 proof tests unskipped unchanged (F4, F8) plus 7 focused tests (5 UDP reassembly, 2 reconnect). All pass
  3× alone; the whole `P895UdpTransportTests` + `P813cReconnectGraceTests` classes (which cover the touched paths)
  pass 3× alone; one fast-tier suite run. Exact counts in §5.

---

## 1. F4 — one UDP reply the OS refuses stops the UDP listener for everyone

### 1.1 Root cause

- `src/Allegiance.Core/Transport/UdpTransport.cs:672` (pre-fix) — `RawSend` swallowed only
  `SocketError.WouldBlock`, `NoBufferSpaceAvailable` and `ConnectionReset`; every other `SocketException` propagated.
- `UdpTransport.cs:243-250` — `Run`'s catch treats any escaping `SocketException`/`IOException` as **fatal**: it drops
  every peer (`Drop(p, "UDP socket: …")`, so the site sees `OnPeerLeft` for all of them) and sets `_running = false`.
  The worker then exits and nothing restarts it.
- Entry: `Handshake` → `UdpTransport.cs:326` (`RawSend` of the Challenge reply to an **unverified** Hello), and every
  data/keepalive send. The triggering input is a single 64-byte datagram whose return address the kernel refuses
  (EINVAL for port 0, EPERM/ENETUNREACH for a filtered or unreachable destination).

### 1.2 Fix

`RawSend` now catches **every** `SocketException` and returns `false` ("this datagram was not sent"), which is what
`SendTraversal` (`UdpTransport.Traversal.cs:142`) already does:

- a refused *send* costs nothing but that datagram: for reliable frames the existing retention path keeps the frame
  and its congestion debt for the next RTO (`Tick`), for best-effort it is simply lost, and the keepalive waits a full
  interval — no peer is dropped, the listener stays up.
- the fatal handlers stay exactly where the report asked: **receive** side only — `Run` (`:243`), `RunClient`
  (`UdpTransport.Scheduling.cs:49`) and `ServiceClient` (`:72`), which treat a socket error as the listener's own
  failure. `Shutdown`/`Drop`'s close notification keeps its narrower filter.

### 1.3 Native verdict — parity of intent (port wire layer)

Native's send path returns the transport HRESULT upward and `SendMessages` **throws it away** — only the message
odometer is updated (`_Utility/Messages.cpp:666-674`: `hr = GenericSend(...)`, then the counter increments; the
`FAILED(hr)` handling in that file belongs to host startup, `:1196-1200`, not to a send). One destination the OS
refuses is therefore invisible to native's messaging loop and cannot end it. `docs/parity/p895-transport.csv`
P895-005 records this.

### 1.4 Test

`tests/Allegiance.Core.Tests/R1ReviewTests.cs` — `A_reply_the_OS_refuses_to_send_does_not_stop_the_UDP_listener`
(`Skip` removed, inputs and assertions untouched): a connected UDP player stays connected (`site.Left == 0`) and a
later Hello from a raw socket is still answered with 64 bytes. Pre-fix failure reproduced by Claude: the
already-connected player was dropped (`site.Left` was 1).

---

## 2. F5 — UDP reassembly buffers are allocated at the size the peer declares

### 2.1 Root causes (all five below were real)

| # | Where (pre-fix) | Defect |
|---|---|---|
| a | `UdpTransport.cs:415-421` | admission charged the peer-declared `size`, and the ceiling was `MaxBufferedBytes` — the **send**-queue hard cap, set to 32 MiB by `DualTransport.cs:24` |
| b | `UdpTransport.cs:799-806` (pre-fix) — `new byte[size]` at `:801` | the `Assembly` constructor allocated the declared size up front (+ `new bool[count]`): a record declaring 64 KiB and delivering one 1144-byte fragment allocated all 64 KiB |
| c | `UdpTransport.cs:417` | the concurrent-assembly cap was `MaxReorder` (4096), the ordered-window reorder depth |
| d | `UdpTransport.cs:475-477` | the expiry sweep had `if (!entry.Value.Reliable …)`: a **reliable** assembly never expired, so a peer's 30 s keep-alive (`IdleTimeout`) held it for the session |
| e | `UdpTransport.cs:407-413` | §4 lead "UDP assembly scan": each newer keyed record walked **all** of the peer's assemblies under `_gate` to drop the superseded ones — O(#assemblies) per arrived record, saturating the worker |

The report's scenario is exact: complete the cookie handshake, publish a greeting, then send ordered/keyed records
that each declare ~64 KiB and carry one fragment without the first sequence. Each pins its declared size; a few
datagrams pin the whole 32 MiB; `MaxConnections` = 1024 multiplies it per host.

### 2.2 Fix

Named ceilings (no `server.json` change needed — the values are transport constants like `TcpTransport.MaxConnections`;
`MaxReceiveBytes` and `ReliableAssemblyTimeout` are also per-instance init properties so a test can shorten them):

- `internal const int DefaultMaxReceiveBytes = 512 * 1024` — the host's per-peer inbound reassembly ceiling
  (`MaxReceiveBytes`). **Why host-only:** the host already refuses any inbound frame above
  `TcpTransport.ServerInboundMaxFrame` = 64 KiB (`UdpTransport.cs:392`), so 512 KiB is eight concurrent full frames;
  a **client** must still reassemble the server's larger frames (up to `MaxFrame` = 16 MiB, e.g. the 96 KiB proof
  frame in `P895UdpTransportTests`), so `ReceiveCap` stays `MaxBufferedBytes` on the client side. `DualTransport`'s
  32 MiB set therefore now applies to the send queue only.
- `internal const int MaxAssemblies = 256` — concurrent incomplete assemblies per peer (was `MaxReorder` 4096).
  Justification: at most **one** assembly can exist per non-zero stream key (a newer sequence supersedes the older,
  and `seq < Newest` is refused), so only the ordered window can accumulate; the sender's congestion window is at
  most `256 × Mtu` (`:590`), i.e. ≤ 256 datagrams in flight.
- `public TimeSpan ReliableAssemblyTimeout { get; init; } = TimeSpan.FromSeconds(5)` — an incomplete reliable
  assembly expires after **5 s without progress**: the deadline is measured from `Assembly.LastProgress` (the last
  *newly stored* fragment), not from creation, so a legitimate large frame that is still arriving fragment by
  fragment keeps its reservation, while a stalled one (a lost fragment, or a peer that only sends keep-alives) is
  abandoned. 5 s is above the maximum retransmission RTO (3000 ms, `:588`), so a legitimately lost fragment is
  retransmitted into a fresh assembly. Best-effort keeps the native 500 ms lifetime measured from creation
  (`BestEffortAssemblyMs`, `_Utility/Messages.cpp:559-563`). `PeerNextDue` schedules both deadlines
  (`AssemblyDeadline`) so the worker wakes for them.
- **Reserve only what arrived.** A new assembly is created with `reserve = index * Payload + fragmentBytes` (the
  highest byte offset that actually arrived, ≤ the declared size) and grows toward later fragments by doubling,
  capped at the declared size; every growth is charged to the same ceiling **before** the allocation, so the ceiling
  is never exceeded. `Assembly.Data.Length` is exactly what `p.ReceiveBytes` counts (invariant: per-peer bytes == the
  sum of its assemblies' reserved lengths), and `Assembly.Size` keeps the declared size for the inconsistent-fragment
  check. A peer that spoofs a high fragment index pays for the offset it asks for and is refused at the ceiling,
  instead of pinning 64 KiB per 1.2 KB datagram.
- **§4 lead fixed (cheap and real).** Per-key supersede is now
  `p.Assemblies.Remove((key, p.Newest.GetValueOrDefault(key)))` — one removal, no scan. This is **exact**, not an
  approximation: `seq < Newest[key]` is refused earlier in the same method, and every admitted `seq == Newest[key]`
  shares the single assembly for that key, so the sequence being superseded is precisely the one whose key tuple is
  `(key, Newest[key])`.
- **Cap semantics unchanged in kind:** exceeding the byte or assembly ceiling drops the peer with the existing
  "UDP reassembly cap" reason (`DisconnectReason.QueueHardCap`), as overflow already did — it is a protocol violation,
  not a slowdown.

### 2.3 Native verdict — deliberate port hardening

Native has **no** application-level reassembly to diverge from: DirectPlay delivers whole messages, and
`FedMessaging::GenericSend`/`SendMessages` build one bounded out-buffer message (`_Utility/Messages.cpp:549-607`,
`assert(cbToSend <= GetBuffOutSize())` at `:662`). The port's fragmentation/reassembly layer is its own wire design
(`docs/UDP_TRANSPORT.md`), so sizing it is port hardening, not a parity change; native's only relevant rule — the
500 ms nonsequential/normal-send lifetime (`_Utility/Messages.cpp:559-563`) — is kept for best-effort assemblies.
Rows P895-003 (unchanged, still true) and the new P895-006 in `docs/parity/p895-transport.csv` record this.

### 2.4 Tests (four fail before the fix and pass after; the fifth guards the deadline)

`tests/Allegiance.Core.Tests/Transport/P895UdpTransportTests.cs`, driven by a new `RawPeer` helper (a real UDP socket
that completes the ADU2 cookie handshake and greeting, then sends records with test-chosen declared size / fragment
index):

| Test | Pre-fix result (measured) |
|---|---|
| `Receive_reassembly_reserves_only_the_bytes_that_arrived` | failed: "a 64 KiB frame with one 1144-byte fragment reserved 65536 bytes" |
| `Per_peer_incomplete_reassembly_is_capped_below_a_megabyte` | failed: "a peer pinned 786432 bytes of inbound reassembly (ceiling 524288)" |
| `Concurrent_incomplete_assemblies_are_capped` | failed: the peer was never refused (`hs.Left == 1` timed out; 300 assemblies accepted) |
| `Incomplete_reliable_reassembly_expires_while_the_peer_stays_alive` | failed: `Assemblies` still 1 after 3 s (`Expected 0, Actual 1`) |
| `Reliable_reassembly_in_progress_is_not_abandoned` (guard, same design) | passes before and after: a 5-fragment frame arriving over ~750 ms with a 300 ms deadline is delivered, so the deadline cannot abandon a frame still making progress |

After the fix: reserved bytes stay at one fragment, the peak reservation is ≤ 512 KiB and the peer is then refused,
> 256 concurrent assemblies are refused, and an incomplete reliable assembly is dropped at its deadline **while the
peer keeps pinging and stays connected** (legit peer unaffected: `Peers == 1`, `Left == 0`). The existing
`Ordered_and_keyed_large_frames_fragment_reassemble_with_loss` (96 KiB frame, real loss/reorder through
`ImpairedUdpProxy`) proves the grow-as-arrived reordering path still reassembles correctly.

---

## 3. F8 — a connection that already has a ship can resume a parked seat

### 3.1 Root cause

- `src/Allegiance.Server/Server.Dispatch.cs:161` — the P813c resume ran before the P646 one-logon-per-connection check
  at `:174`.
- `Server.ReconnectGrace.cs:269` — the name-collision check excludes the requesting connection itself, so a seated
  connection never trips it.
- `Server.ReconnectGrace.cs:342` → `Server.Lobby.cs:4630` — the resume republishes the parked ship and
  `_peerShips[from]` is overwritten; connection 2's first ship stays in its sector, owned by nobody and never parked.
- Each repetition leaves one permanent ghost ship plus one ship id in use — the P646/H3 orphan that the ordinary logon
  path already refuses.

### 3.2 Fix — refuse the resume (chosen over retiring the seated ship)

`Server.Dispatch.cs:161` → the resume now requires the connection to own no ship:

```csharp
if (_reconnectGraceEnabled && !_peerShips.ContainsKey(from) && TryResumeParkedPeer(from, req))
    continue;
```

**Why refusal, not "retire the seated ship first":** native's model is one player per connection, created at logon
and destroyed at disconnect — `assert (pfm->fmid != FM_C_LOGONREQ); // shouldn't have a pfsPlayer yet if logging on`
(`FedSrv/FedSrv.CPP:4830-4831`). Native has **no** reconnect/resume flow at all (P813c is the port's experimental,
default-off addition), so a seated LOGONREQ is a protocol violation in native terms, and the reconnect-free model's
only sane answer is to refuse it. Retiring the seated ship would invent a native-incompatible rule (a seated player's
ship destroyed by their *own* re-logon) to serve an attacker; refusal keeps every legitimate path — the seated
player's ship, the parked seat, its owner's later resume — untouched and side-effect free.

The frame then falls through to the existing P646 branch, so the refusal is the same as any duplicate logon: no ack,
no ship-id allocation, no side-select hold, one rate-limited `[logon] … DUPLICATE LOGONREQ from a seated peer —
rejected` line. The parked entry is not consumed or modified.

### 3.3 Native verdict

Refusal-as-flag is the port's existing duplicate-logon ruling (LOB-0033, `docs/parity/lobby-matrix.csv`) applied at
the resume: the native assert (`FedSrv.CPP:4830-4831`) says the frame must not arrive at all, and native's
`OnDestroyConnection` → `delete pfsPlayer` (`FedSrv.CPP:4549-4566`) is what keeps ship ownership single. No parity row
changes: the reconnect feature itself is port-only and not a native row (P813c's report covers it).

### 3.4 Tests

- Unskipped unchanged: `R1ReviewTests.A_seated_connection_cannot_resume_a_parked_seat` — passes. Pre-fix it failed
  (`Assert.Contains() Failure: Item not found in collection`: the collector's own ship was no longer owned by any
  connection).
- New `P813cReconnectGraceTests.Seated_connection_cannot_take_or_retire_a_parked_seat`: the seated connection's
  name+token attempt is refused (`DUPLICATE LOGONREQ` in the log), `ReconnectResumedTotal == 0`, the parked seat is
  still parked (`ReconnectParkedShipCount == 1`, same ship id, still in its sector), the collector's ship is still
  owned — and the **real owner then resumes the same ship** from a fresh connection (zero side effects proven). Fails
  before the fix ("Sub-string not found": no refusal line — the resume had succeeded).
- New `P813cReconnectGraceTests.Seated_connection_re_logon_with_reconnect_off_allocates_nothing`: with
  `reconnect.enabled = false` the same re-logon is the ordinary P646 refusal — ship count unchanged, no parking, no
  resume. The config-off arm is unchanged by the guard.
- The whole existing library still passes: `P813cReconnectGraceTests` (16 tests: the 14 existing incl.
  `Grace_off_by_default_…`, `Reconnect_within_grace_resumes_the_same_ship_and_seat`,
  `Wrong_token_refuses_only_the_claimant_…`, `Name_collision_keeps_the_seat_parked_…`,
  `Death_during_the_gap_is_not_resurrected`, `Game_over_during_the_gap_ends_the_grace`,
  `Docked_player_resumes_still_docked`, `Turret_gunner_resumes_into_the_same_turret`), `P864cDuplicateLogonTests`
  (8 rows), `P966ServerTrustBoundaryTests` (3), `LiveLogoffTests` (10) — all executed and passing inside the fast
  tier — and the full fast tier itself.

---

## 4. Limits and configuration

| Limit | Value | Kind | Where |
|---|---|---|---|
| UDP inbound reassembly ceiling per peer (host) | 512 KiB (`DefaultMaxReceiveBytes`) | named constant + `MaxReceiveBytes` init property | `UdpTransport.cs` |
| Concurrent incomplete assemblies per peer | 256 (`MaxAssemblies`) | named constant | `UdpTransport.cs` |
| Incomplete reliable assembly lifetime | 5 s (`ReliableAssemblyTimeout`) | init property, named default | `UdpTransport.cs` |
| Incomplete best-effort assembly lifetime | 500 ms (unchanged, native) | named constant `BestEffortAssemblyMs` | `UdpTransport.cs` |
| Client inbound reassembly ceiling | unchanged (queue ceiling, up to 16 MiB frames) | — | `UdpTransport.ReceiveCap` |

No `server.json` key was added: these are transport-layer constants in the same family as `TcpTransport.MaxConnections`
/ `MaxPendingHandshake` / `ServerInboundMaxFrame`, documented in `docs/UDP_TRANSPORT.md`. UDP is opt-in
(`transport.protocol = udp`); with the default TCP transport F4/F5 are unreachable, and F8 needs
`reconnect.enabled = true` (default off).

Legitimate play untouched: a host inbound frame is ≤ 64 KiB, so 512 KiB covers eight concurrent incomplete frames
(a real client has one ordered frame in flight plus a few pose keys); 256 assemblies is the sender's maximum
in-flight datagram count (`Cwnd ≤ 256 × Mtu`); 5 s is above the maximum retransmission RTO (3 s). The 96 KiB
host→client proof frame still reassembles (client path unchanged).

---

## 5. Gate

**Build.** `dotnet build AllegianceCore.slnx` (with `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`): **0 warnings, 0 errors**. `tools/build-tools.sh`: **all 140 tool
projects built**, 0 warnings, 0 errors. Release AOT publish of `src/Allegiance.Server`
(`-c Release -r linux-x64 --self-contained -p:PublishAot=true`): succeeded, **no IL2026/IL3050**
or any other warning; the output is a 21.9 MB native ELF executable.

**Task tests 3× alone** (filter `P895UdpTransportTests` + `P813cReconnectGraceTests` +
the two unskipped `R1ReviewTests`), each run a fresh process with artwork and `--no-build`:

| Run | Passed | Failed | Skipped |
|---|---|---|---|
| 1 | 38 | 0 | 0 |
| 2 | 38 | 0 | 0 |
| 3 | 38 | 0 | 0 |

38 = 20 `P895UdpTransportTests` (15 existing + 5 new) + 16 `P813cReconnectGraceTests`
(14 existing + 2 new) + 2 unskipped proof tests. Duration ~21 s per run.

**One full fast-tier suite** (`tools/run-tests.sh --tier fast --no-build`): 14,618 tests, 535.2 s —
**14,598 passed / 5 failed / 15 not executed**, plus 5 passing executions above the calibrated
11.526 s limit (P744 factor 1.153). The task's own tests all pass inside the suite. All five
failures and all five slow entries are unrelated to UDP transport/reconnect and were already in
`docs/TEST_FLAKES.md`; each was reproduced 3× alone (and the two Core methods once more with
every P1025 change reverted) and the observations are recorded in the new
"P1025 gate observations" section there. In short:

- `P739TimerLiveTests…(delay: 5)` — `5.0000000000000009` against an inclusive 5 s bound;
  deterministic on the reverted tree too (already led floating upper-bound family).
- `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` — 8.10000038
  against `[4,8]`, passes 6/6 alone (led clock family).
- `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` — intermittent, led mining
  family, passes on the reverted tree.
- `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` — 5,424 B
  allocated after warmup; 3/3 alone, led allocation-counter family.

No limit, tier, assertion or timing allowance was relaxed. Evidence: `~/allegiance-evidence/P1025/`
(`build.log`, `runs3.log`, `aot-publish.txt`, `tiers-fast/summary.json`, `tiers-fast/fast/*.log`).

---

## Native intent

Native has no application-level UDP reassembly, so F5's ceilings are port infrastructure. What native *does* state and
this change keeps: a send failure is the destination's problem and never the messaging loop's
(`_Utility/Messages.cpp:666-674`, `GenericSend` returns an HRESULT that `SendMessages` discards); nonsequential /
non-guaranteed messages expire 500 ms after they are offered (`Messages.cpp:559-563`); and a connection carries at
most one player — a LOGONREQ from an already-seated connection is a protocol violation that native asserts against
(`FedSrv/FedSrv.CPP:4830-4831`), whose one-player-per-connection lifetime is destroyed at disconnect
(`FedSrv.CPP:4549-4566`).

## Modern approach

F4 makes the port's own wire layer match that intent: a `SocketException` on send is a per-datagram "not sent", so the
existing retransmission and congestion accounting does the recovery and the receive handlers remain the only
listener-fatal path. F5 sizes the port's fragmentation layer by what a peer has actually delivered rather than what it
claims: a per-peer reservation that grows with arrival, two explicit ceilings (512 KiB / 256 assemblies) that are
constant, auditable and far under the attack budget, a deadline that reuses the existing scheduler, and a per-key
supersede that is a dictionary removal instead of a scan of every assembly under the state lock. F8 keeps the port's
experimental reconnect feature inside the native one-player-per-connection rule: a seated connection cannot resume,
the refusal is silent and allocation-free (the P646 path), and the parked seat stays exactly as resumable as the
P966 trust rules require. All three fixes are state on the server — no wire id, frame or field changes — so clients,
bots, the MCP harness, skirmish/training and the rig stay byte-identical.
