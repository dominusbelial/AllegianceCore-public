# Wire

Extracted from README.md 2026-08-29 (it had reached 1,096 lines).

## The FEDMSG wire — framing + logon handshake

The beginning of the bot-facing transport. WOPR/Pigs already speak this wire
protocol, so getting the framing right means a future server can accept those
existing bots **unchanged** — a free differential-parity harness against the
original. Detailed spec (layout tables, `file:line` references, deviations) in
**`FEDMSG.md`**; this is the summary.

### The wire format

- **Message** = `cbmsg`(u16) + `fmid`(u16) + fixed fields + variable blobs,
  all **little-endian**. `cbmsg` is total size incl. itself; `fmid` is the id
  (1 = `LOGONREQ`, 2 = `LOGONACK`). No `#pragma pack` on messages — MSVC default
  alignment (each member to its size, struct to its largest member).
- **Variable items** come first as `ib`/`cb` (u16,u16) pairs — `ib` = byte
  offset of the item from message byte 0, `cb` = length (0 = absent; includes
  the trailing NUL for strings). The blobs themselves are appended after the
  entire fixed region (`PFedMsgCreate`, `_Utility/Messages.cpp:338-439`).
  `CB_ZTS` strings are trailing-space-trimmed then NUL-terminated.
- **Packet** = concatenation of messages + a single trailing 4-byte CRC over
  the message bytes only. `cbmsg` walks the messages; the CRC is checked on
  receipt. Algorithm: reflected IEEE CRC-32 table, seed `0xFFFFFFFF`, **no
  final XOR**, result `0 → 1` (`_Utility/CRC.cpp`). Vendors calling it "plain
  CRC-32" are wrong about the final inversion; the C++ wins.
- **`MSGVER = 216`** is the client's `fedsrvVer`. The server accepts only
  `fedsrvVer == 216`; on any mismatch it replies with a `LOGONACK` carrying
  `fValidated = false` and a reason string (`FedSrv.CPP:5824, 6051-6082`) — not a
  socket drop.

### What is ported

`src/Allegiance.Core/Wire/` (byte-level encode/decode only — no sockets, no
DirectPlay, no async/DI/reflection, no new NuGet packages):

- `FedMessageHeader` — the 4-byte `cbmsg`/`fmid` base.
- `VarItem` — an `ib`/`cb` pair; generic variable-item encode/decode helper.
- `WireWriter`/`WireReader` — little-endian cursors reproducing the C++
  `PFedMsgCreate` placement (fixed region then blobs).
- `FedMsgCrc` — the exact CRC variant above.
- `FedPacket` — concatenate messages + append/verify the CRC; message walk.
- `LogonRequest` / `LogonAck` — the two handshake messages, round-trippable.

### Beyond these two, what was added and why

- **STEAM fixed fields** (`steamAuthTicket[1024]`, `steamAuthTicketLength`,
  `steamID`, `drmHash[50]`) are included in `LogonRequest` because modern
  clients populate them and they change the fixed size/offset math (1128 vs
  36 bytes). Decode is tolerant: the ticket is stripped of trailing NULs, so a
  server need not insist on exact steam payloads.
- `FedPacket` (concatenation + trailing CRC) because the handshake rides a
  packet and the CRC-over-correct-range guarantee lives here.
- No other `DEFINE_FEDMSG` of the 136 was touched.

### What a bot connection still needs (NOT done)

- Transport over a socket matching the DirectPlay session semantics; the
  handshake alone is byte framing, not connection establishment.
- The lobby / encryption / CD-key scrambling path — out of scope by design.
- Handling the **real client's stale fifth var item** that aliases
  `steamAuthTicket[0..3]` (FEDMSG.md §2). Our decoder follows the *declared*
  four-var-item layout; to accept an unpatched modern client verbatim we would
  need to tolerate that aliased pair.
- Authenticating the zone ticket / character (server-side auth is unimplemented;
  the current port only frames and round-trips the messages).

### Verification

19 new tests under `tests/Allegiance.Core.Tests/Wire/`: message round-trip
(including variable items), **byte-exact** checks against hand-built buffers
composed from the spec, the CRC against a published check value and an
independent block-vs-step equivalence, packet corruption → `FedCrcException`,
and a wrong `MSGVER` producing the rejected-ack it should.

### TCP transport over the wire — `src/Allegiance.Core/Transport/`

The loopback proof from TRANSPORT_SEAM.md §Recommendation: a real TCP socket
carrying the already-shipped `Wire` codec, one host + one client (bot role),
handshaking `LOGONREQ`/`LOGONACK`. No C++ edits. This section is the summary;
spec rationale and the C++ trace are in **`TRANSPORT_SEAM.md`**.

**Interfaces** (adapted, not transliterated — no `HRESULT`, no `DPID`):

- `ITransport` — `Host(port=0)`, `Join(address, port, playerName)`,
  `Shutdown()`, `IsConnected`, `LocalId`, `BoundPort`, and
  `Send(target, ReadOnlySpan<byte>, guaranteed)`. Group fan-out and CRC are
  deliberately NOT here (§2 of the seam): the transport deals only in ids and
  bytes; the caller packs/validates with `FedPacket`.
- `ITransportSite` — `OnPeerJoined(id, name)`, `OnPeerLeft(id)`,
  `OnSessionLost()`, `OnReceive(from, ReadOnlySpan<byte>)`. These fire on
  background threads; an implementor wanting `FedMessaging`' single-owner
  dispatch funnels them through a queue, exactly as the `m_csMsgList`
  serializer does today.
- Idiom choices (asked-for in the task): **exceptions** over result/`HRESULT`
  codes; `ConnectionId` as an immutable `readonly record struct` wrapping a
  `uint` (value equality, opaque). I kept it unsigned-32-bit to mirror `DWORD`.

**The framing decision (the crux)** — the transport adds its **own outer length
prefix** and does not rely on `cbmsg`. Reasons:

- FEDMSG.md §3 is explicit: *"there is no outer packet length; the
  socket/transport supplies the packet length."* The delimiter is ceded to the
  transport.
- `cbmsg` is per-**message**, not per-packet. A packet is a concatenation of
  zero-or-more messages + a trailing CRC, and nothing on the wire says how many
  messages precede the CRC. Without an outer length the receiver cannot know
  the packet boundary, so it cannot know where the CRC is or validate it.
  Relying on `cbmsg` would mean assuming exactly one message per packet and
  re-parsing message framing inside the transport.
- The prefix lives **outside** the CRC (which covers only message bytes), so a
  frame's body is byte-for-byte the codec's output and `FedPacket.Unpack` works
  on it unchanged.

Frame header = `type(1) + length(4, LE)`. `type` distinguishes a one-shot
**Greet** (the joining peer's name) from a **Data** frame (a `FedPacket`). The
name must ride in-band because TCP has no peer-metadata channel — this is the
TCP analog of the two `GetClientInfo` calls DirectPlay made at `CREATE_PLAYER`.

**OnPeerJoined before any OnReceive** (Recommendation constraint 1): the host
consumes the greeting, mints the id, stores the stream, and only then calls
`OnPeerJoined(id, name)`; datagram dispatch cannot start until that returns.

**Sync-not-async**: sockets were the one place async was permitted, and we chose
**not** to spend it — the transport runs blocking `Read` loops on background
threads (one accept loop, one per peer). A blocking loop is dramatically easier
to make correct for stream reassembly (partial reads) than an async state
machine, and this proof has no throughput demand. Async/`await` appears only in
`tools/loopback`'s top-level `TaskCompletionSource` waits, i.e. outside the
library. `guaranteed` is accepted and ignored — TCP is already reliable.

**Proof** (`tests/.../Transport/TcpLoopbackProofTests.cs`, +2) over a real
loopback socket: both directions of `LOGONREQ`/`LOGONACK`, CRC validated both
ways, corrupted byte → `FedCrcException`, `OnPeerJoined` sequenced before
`OnReceive`, and **partial reads** — a data frame driven byte-wise in two
writes separated by 150ms and reassembled. Plus `tools/loopback` (two OS
processes; `run.sh` orchestrates them): host binds an ephemeral port, prints
it, the client joins and they exchange the handshake, each process printing
its half. **1,465 tests green.**

### What the C++ side still needs (post-proof)

The proof settles the transport+codec seam; the C++ seam is separate work in
`Allegiance/` (read-only here, out of scope):

- Wrap DirectPlay behind the same `ITransport` (`DirectPlayTransport`) or do the
  in-place `Messages.cpp` rewrite (~30 functions per §5.1). The message framing,
  CRC, out-box, and public API stay untouched.
- **Server-side group fan-out** (§3.3): translate `SendTo(group)` into a
  per-member `Send` loop on the C# host. Not needed for a bot client, which
  only ever does reliable unicast to the server connection.
- **Session GUID in the connect hello** (§3.4) so the client's enum-on-join can
  be skipped. Our Greet currently carries only the name; a future revision adds
  the instance GUID.
- **Best-effort semantics** reduce to "disconnect = NAK" on reliable TCP
  (§3.2). The ping-loss heuristic changes but stays functional.

### Surprises worth recording

- `ManualResetEventSlim` signaling in the first cut of the test harness misled
  (the event reported set with no matching record); switching to plain
  list-polling waits removed the fragility entirely — worth remembering for any
  threaded test here.
- Flipping a byte then recomputing the CRC is **self-consistent** — you must
  flip inside the already-packed packet for a real corruption test. Obvious in
  hindsight, easy to get wrong.
- No NuGet packages were added (`System.Net.Sockets`/`System.Net` are BCL);
  `/snap/bin/dotnet` (a broken stub) was avoided, as was the read-only
  `Allegiance/` tree.
