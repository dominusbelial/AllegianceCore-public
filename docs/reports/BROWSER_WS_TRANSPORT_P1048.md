# P1048 — browser WebSocket transport

Status: **incomplete**. Only part of the transport seam audit is implemented; there is no WebSocket listener or browser join. Do not treat this work as public admission support.

| Item | Status |
|---|---|
| 1. Transport seams | Partial: source, protocol and diagnostic interfaces; UDP advertisement now uses an interface; listener composition and ID layout remain. |
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
| Per-peer protocol | `IServerTransport.PeerProtocolName` defaults to the listener's protocol; `DualTransport` delegates to the owning listener. The mixed TCP/UDP test checks both labels. |
| Privilege loopback | Already uses `IServerTransport.IsPeerLoopback` in `src/Allegiance.Server/Server.Privileges.cs:12`. A future WS implementation must always return false. |
| TCP send pool | Optional `ISendPoolDiagnostics`, used in `src/Allegiance.Server/Server.Egress.cs:57`. |
| UDP receive scheduling | Optional `IUdpReceiveDiagnostics`, used in `src/Allegiance.Server/Server.SchedulingMetrics.cs:16`. |
| Client UDP type check / otherwise TCP | `ITransport.ProtocolName` defaults to `unknown`; TCP and UDP supply their labels, and the active fallback supplies its actual label in `src/Allegiance.ClientCore/Session/ConnectionSession.cs:31`. |
| Lifecycle construction, heartbeat and UDP configuration | `IUdpListenerInfo` in `src/Allegiance.Core/Transport/IServerTransport.cs` supplies the bound UDP port to heartbeat, discovery and logging in `src/Allegiance.Server/Server.Lifecycle.cs`. Construction still selects and configures `DualTransport`; composition remains incomplete. |
| Dual peer-ID layout | Still reserves one bit in `src/Allegiance.Core/Transport/DualTransport.cs`; a third listener cannot safely use it yet. |

The TCP/UDP address implementations themselves are unchanged. The lobby's trusted-proxy configuration is owned by `src/Allegiance.Lobby/Program.cs`; the game server does not currently consume it. A WebSocket listener needs an explicitly configured and tested equivalent trust boundary before forwarded source addresses can be used. `tests/Allegiance.Core.Tests/Transport/P895UdpTransportTests.cs:289` and `:327` check actual fallback labels and mixed listener source addresses.

## Limits

No WebSocket limits are implemented. All requested WebSocket admission, handshake, fragmentation, ingress, queue, provenance and authentication limits remain outstanding. No existing limits were intentionally changed.

## Verification

| Check | Result |
|---|---|
| Server build, offline restore | Passed, 0 warnings / 0 errors. |
| P895 UDP transport suite | Passed 20/20 on this run; earlier lane runs were 20/20 once and 20/20 twice in concurrent processes. The new mixed-listener assertion was additionally run alone three times. |
| Glossary link check | Passed, 945 paths. |
| Full solution build | Passed, 0 warnings / 0 errors, using the workspace NuGet HTTP cache. |
| Mixed TCP/UDP listener test, including UDP advertisement interface | Passed 1/1 on each of three isolated runs. |
| Mixed TCP/UDP listener test with per-peer protocol assertion | Passed 1/1 on each of three isolated runs in attempt 3. |
| Selected TCP/UDP connection and inbound limit tests | Passed 31/31 once; this is not the full required regression set. |
| Build tools | Passed: all 142 projects. |
| Fast tier | Failed: 15,151 passed, 3 failed, 7 skipped; eight cases exceeded the calibrated 11.545 s limit. See `TestResults/tiers-20261117T192005.353904Z/summary.json` (local, generated). All ten distinct failures/timing offenders were rerun three times individually: nine passed 3/3; the mining broadcast case passed twice and failed once. Details: `docs/TEST_FLAKES.md`. |
| Full server/identity/credential regression suites and P1047 browser proof | Not run. Browser project built as part of solution; no separate browser proof was run. |
| Docker compose config | Not run; Docker is not installed. |

## AOT and latency

Not measured. No WebSocket implementation exists to publish or benchmark. No AOT or TCP/UDP equivalence claim can be made from these partial checks.

## Unfinished work

Complete item 1, then implement and test items 2–5. Items 6–7 belong to P1048b and are out of scope. In particular, do not add a third listener before collision-free peer routing and provenance are established. Validate all brief gates, including real Native AOT publish and a published-binary join, and measure loopback latency. No commit, push or deployment was made. The brief had pre-existing working-tree modifications and was left untouched; it must be excluded from any later change set.

## Native intent / Modern approach

Native game rules and FedPacket layouts remain unchanged; the intended WebSocket endpoint is a transport adaptation. No native rule was ported by this partial seam change.
