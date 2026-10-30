# UDP transport and automatic client selection (P895 / P897)

The game client defaults to **Auto** for remote multiplayer. In Settings
(Ctrl+F6, or Options → Graphics Options), use Up/Down to reach **Multiplayer
transport**, then Left/Right to cycle **Auto / TCP / UDP**. The same row shows
what the connected session actually uses, including TCP after fallback. The
preference is saved as `Transport` in `hud.json` for both Classic and Modern.
It applies on the next connection; there is no mid-match switch.

| Setting / connection | Selection |
|---|---|
| TCP | TCP, regardless of discovery/link facts |
| UDP | Prefer UDP; bounded handshake falls back to TCP |
| Auto, no advertised UDP port | TCP |
| Auto, LAN/multicast/subnet, both ends wired | TCP |
| Auto, LAN/multicast/subnet, either end wireless or unknown | UDP |
| Auto, lobby/Tailscale/manual/favorite, UDP port advertised | UDP |

`ALLEGIANCE_TRANSPORT_PROTOCOL=tcp|udp` overrides **Auto**, preserving the
existing operator override. An explicit Settings TCP/UDP choice wins. Skirmish
and Training always use their local TCP connection. Bots, MCP observers and
plain `ConnectionSession` callers retain their existing TCP default (or their
explicit protocol/environment override); they do not evaluate Auto.

Link detection asks the OS for the local address routing to the server, matches
that address to an adapter, then classifies it. Windows uses the adapter type;
Linux checks sysfs wireless/phy80211 markers before considering a physical
Ethernet device wired. Unix Ethernet alone does not prove a wired connection.
An explicit Wireless80211 type is accepted on any platform; otherwise macOS,
Android, virtual adapters, VPNs, inaccessible facts and failures stay unknown.
Unknown favors UDP on discovered LANs. No detection packet is transmitted.

Discovery v1 replies add optional `UdpPort` and `HostLink` (wired, wireless,
unknown). The host link describes its route back to the querying client, so
multi-adapter hosts do not advertise one unrelated global adapter. Missing
fields on old servers mean no UDP port / unknown link; old clients ignore
additions. Lobby/heartbeat fields are unchanged. Manual entries without port
metadata use TCP in Auto; use explicit UDP (and the optional port override)
when the listener is known separately.

The server continues to default to TCP with pooled workers. Enable its
additional UDP listener to make Auto selection available:

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
| `ALLEGIANCE_TRANSPORT_PROTOCOL` | `tcp` (default) or `udp` (adds UDP) | Overrides game Auto; plain session/bot/MCP: `tcp` default or `udp` preference |
| `ALLEGIANCE_TRANSPORT_UDP_PORT` | Optional UDP port; zero means ordinary port | Optional direct-connect UDP port; zero means ordinary port |

For example, launch the server and clients with
`ALLEGIANCE_TRANSPORT_PROTOCOL=udp`. A client attempts address validation for up
to three seconds, then falls back to the original TCP endpoint if UDP is blocked.
An established UDP session uses the existing session-loss/reconnect policy.
`ConnectionSession` and `Bot` also expose `TransportProtocol` and `UdpPort`
properties for programmatic configuration. Lobby heartbeats/public listings
include `udpPort` only when enabled; discovery and the game browser pass that
port to the session. Auto selects a preference once at join; reconnect freezes
and reuses that preference and UDP port. Classic and Modern use that same session.
One `[monogame] transport=... selected=...: reason` line records the actual
protocol after the handshake and the selection reason.

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
  P895b's ADU2 format shares one 128-bit token and packet ID across queued
  frames, encodes record fields as bounded integers, and piggybacks selective
  ACK IDs. Standalone ACKs wait at most 20 ms; data never waits to fill a batch.
  Both UDP endpoints must use ADU2; an older UDP listener fails the bounded
  handshake and the client uses its existing TCP fallback.
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
`host-udp.json` and `client-udp.json` record packet kinds/bytes, retransmissions,
worker wake sources and first-send queue waits, excluding warmup. Bytes exclude
IP/UDP headers and TCP's kernel ACKs/retransmissions. Clients use blocking
receives, immediate paced producer sends and a one-shot deadline timer; an idle
session waits for actual socket activity or its keepalive/timeout deadline.
See [P895b's report](reports/P895B_REPORT.md) for the before/after qualification.
