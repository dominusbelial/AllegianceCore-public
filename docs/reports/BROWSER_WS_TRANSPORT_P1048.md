# P1048 — browser WebSocket transport

Status: **incomplete**. Only part of the transport seam audit is implemented; there is no WebSocket listener or browser join. Do not treat this work as public admission support.

| Item | Status |
|---|---|
| 1. Transport seams | Partial: source, protocol and diagnostic interfaces; listener construction and ID layout remain. |
| 2. Server WebSocket transport | Not started. |
| 3. Admission and limits | Not started. Existing TCP/UDP policies unchanged. |
| 4. Security tests | Not started for WebSocket. |
| 5. Native AOT packaging | Not tested. |
| 6. Client transport | Not started. |
| 7. Browser join | Not started. |

## Seam audit

| Former concrete lookup | Current state |
|---|---|
| Credential source TCP/UDP/Dual switch | `IServerTransport.PeerAddress`; delegation in `src/Allegiance.Core/Transport/DualTransport.cs:52` and use in `src/Allegiance.Server/Server.CredentialLimits.cs:79`. |
| Privilege loopback | Already uses `IServerTransport.IsPeerLoopback` in `src/Allegiance.Server/Server.Privileges.cs:12`. A future WS implementation must always return false. |
| TCP send pool | Optional `ISendPoolDiagnostics`, used in `src/Allegiance.Server/Server.Egress.cs:57`. |
| UDP receive scheduling | Optional `IUdpReceiveDiagnostics`, used in `src/Allegiance.Server/Server.SchedulingMetrics.cs:16`. |
| Client UDP type check / otherwise TCP | `ITransport.ProtocolName` defaults to `unknown`; TCP and UDP supply their labels, and the active fallback supplies its actual label in `src/Allegiance.ClientCore/Session/ConnectionSession.cs:31`. |
| Lifecycle construction, heartbeat and UDP configuration | Still concrete in `src/Allegiance.Server/Server.Lifecycle.cs`; incomplete. |
| Dual peer-ID layout | Still reserves one bit in `src/Allegiance.Core/Transport/DualTransport.cs`; a third listener cannot safely use it yet. |

The TCP/UDP address implementations themselves are unchanged. `tests/Allegiance.Core.Tests/Transport/P895UdpTransportTests.cs:289` and `:327` check actual fallback labels and mixed listener source addresses.

## Limits

No WebSocket limits are implemented. All requested WebSocket admission, handshake, fragmentation, ingress, queue, provenance and authentication limits remain outstanding. No existing limits were intentionally changed.

## Verification

| Check | Result |
|---|---|
| Server build, offline restore | Passed, 0 warnings / 0 errors. |
| P895 UDP transport suite | Passed 20/20 once, then 20/20 twice in concurrent independent test processes. The required three isolated runs have not been established. |
| Glossary link check | Passed, 945 paths. |
| Full solution build | Failed during restore: read-only NuGet cache and missing packages. The initial default restore attempted package vulnerability retrieval despite the loopback-only constraint; subsequent restore used an empty package source list. |
| Build tools, fast tier, server/identity/credential regression suites, browser build and P1047 proof | Not run. |
| Docker compose config | Not run; Docker is not installed. |

## AOT and latency

Not measured. No WebSocket implementation exists to publish or benchmark. No AOT or TCP/UDP equivalence claim can be made from these partial checks.

## Unfinished work

Complete item 1, then implement and test items 2–6 in order and the browser stretch item if feasible. In particular, do not add a third listener before collision-free peer routing and provenance are established. Validate all brief gates, including real Native AOT publish and a published-binary join, and measure loopback latency. No commit, push or deployment was made. The user-supplied brief remains untracked and is not part of the changes to be published.

## Native intent / Modern approach

Native game rules and FedPacket layouts remain unchanged; the intended WebSocket endpoint is a transport adaptation. No native rule was ported by this partial seam change.
