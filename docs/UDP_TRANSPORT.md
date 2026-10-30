# Optional UDP transport (P895)

TCP with pooled send workers remains the default. The experimental UDP path aims
to keep fresh poses moving when reliable game-flow packets need retransmission.
It is default off pending the rig and WAN A/B described in
[the report](reports/P895_REPORT.md).

Enable the additional listener in the server JSON:

```json
{
  "transport": {
    "mode": "pool",
    "protocol": "udp",
    "udpPort": 6073
  }
}
```

This is a fragment to merge into an ordinary server configuration. TCP continues
to listen on the ordinary `port`. `udpPort` omitted or zero uses that same port
number for UDP. `protocol: "tcp"` disables UDP; `mode` still selects the TCP
worker policy. Existing broadcast and simulation defaults remain the baseline.

Environment overrides:

| Variable | Server | Client / bot / MCP host |
|---|---|---|
| `ALLEGIANCE_TRANSPORT_PROTOCOL` | `tcp` (default) or `udp` (adds UDP) | `tcp` (default) or `udp` (prefer UDP) |
| `ALLEGIANCE_TRANSPORT_UDP_PORT` | Optional UDP port; zero means ordinary port | Optional direct-connect UDP port; zero means ordinary port |

For example, launch the server and clients with
`ALLEGIANCE_TRANSPORT_PROTOCOL=udp`. A client attempts address validation for up
to three seconds, then falls back to the original TCP endpoint if UDP is blocked.
An established UDP session uses the existing session-loss/reconnect policy.
`ConnectionSession` and `Bot` also expose `TransportProtocol` and `UdpPort`
properties for programmatic configuration. Lobby heartbeats/public listings
include `udpPort` only when enabled; the game browser passes that port to the
session. The user still opts in to UDP. Classic and Modern use that same session.

Open/forward the configured UDP port as well as the existing TCP port. Compose
already maps both; use:

```bash
ALLEGIANCE_TRANSPORT_PROTOCOL=udp docker compose up --build
```

The default Compose mapping is 6073 for TCP and UDP. A custom
`ALLEGIANCE_TRANSPORT_UDP_PORT` changes both sides of its UDP mapping. The normal
TCP port remains available for mixed clients and fallback. UDP discovery is a
separate service on its existing port; this setting does not replace discovery.

Delivery contracts:

* Guaranteed and key-zero frames are reliable and ordered, including joins,
  world exports, stage changes, chat, kills and game over.
* Nonzero best-effort coalescing keys have independent latest-wins sequencing,
  no retransmission, and a 500 ms send/reassembly lifetime.
* Guaranteed coalescing keys retransmit the newest value until ACKed or
  superseded. Superseding one stream never skips a reliable game-flow sequence.
* Datagrams fit 1200 bytes; larger frames are fragmented. Ordered frame delivery
  waits for missing ordered fragments; pose keys can progress independently.
* Address-bound cookies prevent an unvalidated source from triggering large
  replies. Tokens prevent off-path spoofing. This provides no encryption.
* A paced, bounded Reno-style congestion window counts all data, including
  superseded packets until ACK/loss. Reliable queue overflow disconnects the peer.
  The server shares TCP's connection/per-IP/pending-handshake limits, inbound
  frame cap, idle timeout and queue hard cap. Limits apply per listener.

`TransportFactory.ActiveTransport` reports the resolved client transport for
local diagnostics. In server logs, mixed-listener UDP peer ids have the high bit
set, so a run can verify UDP was actually used instead of silently measuring a
fallback. The optional P813d park setting retains UDP key-zero FIFO; all ordered
frames are already reliable. TCP's park behavior is unchanged.

Reproduce the bounded local proof (requires the ordinary external artwork):

```bash
~/.dotnet/dotnet build tools/p895-transport-proof -c Release
python3 tools/p895-transport-proof/run-matrix.py --seconds 12
```

The proof starts only local server/client processes and shuts them down. UDP uses
actual lossy/reordering datagrams. The TCP proxy models a lost chunk with a fixed
200 ms recovery stall; it does **not** measure kernel TCP loss recovery. Results
must retain that distinction. The 200-client loopback pair uses the native ship
budget, immutable snapshots, shared encoding, parallel build and server GC.
