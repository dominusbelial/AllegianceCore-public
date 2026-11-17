# P1048 — direct game-server WebSocket transport

Date: 2026-11-17. Items **1–5 are implemented**, with all 135 new cases passing. Full fast is **not green**:
two existing gameplay tests failed and one existing profile case exceeded the
calibrated timing limit; isolation and the ledger are recorded below. Items 6–7 remain P1048b: no production client
transport, browser login/join, flight rendering or public hosting was added.
Changes remain in the working tree; no commit, push or deployment was made.

## Implementation and seam audit

The optional listener lives in **Allegiance.Server**, because Kestrel is a host
dependency. Core and its browser variant have no ASP.NET dependency. The server's
`Microsoft.AspNetCore.App` framework reference is **private**: the embedded
single-player/desktop graph retains its Microsoft.NETCore.App-only runtime
contract. A real embedded boot/shutdown was tested with an isolated runtime root
containing no ASP.NET shared framework. The dedicated executable retains Native
AOT and its runtime-deps image.

| Former seam / assumption | Final implementation |
|---|---|
| Credential source TCP/UDP/Dual switch | Earlier attempt's `IServerTransport.PeerAddress` remains the only production lookup in `src/Allegiance.Server/Server.CredentialLimits.cs:79`; WS records the socket source or the explicitly trusted forwarded source. |
| Loopback privilege lookup | `IServerTransport.IsPeerLoopback`, `src/Allegiance.Server/Server.Privileges.cs:13`. WS always returns false, including direct and proxied 127.0.0.1. |
| Human/bot classification | Shared `IsPeerHuman` / `SetPeerHuman`, `src/Allegiance.Core/Transport/IServerTransport.cs:14`; TCP/UDP expose their existing flag. Only the ordinary validated logon calls `MarkPeerAuthenticated` (`src/Allegiance.Server/Server.Dispatch.cs:333`); marking human does not authenticate. TCP/UDP keep the default no-op authentication notification. |
| TCP send-worker and UDP receive casts | Earlier attempt's `ISendPoolDiagnostics` / `IUdpReceiveDiagnostics` remain in `src/Allegiance.Server/Server.Egress.cs:57` / `Server.SchedulingMetrics.cs:16`. The multiplexer delegates diagnostics and remaps TCP diagnostic peer IDs too. |
| Concrete Dual UDP advertisement / NAT lookup | `IUdpListenerInfo` for heartbeat/discovery; `UdpListenerPort` filters absent/zero ports (`src/Allegiance.Server/Server.cs:864`). The explicitly created UDP instance is retained for UDP-specific introduction operations, without a cast through the composed host. |
| Two-way construction | Factories receive their own event adapter; `src/Allegiance.Server/Server.Lifecycle.cs:974` composes TCP, optional UDP and independently optional WS. TCP remains the default. WS is not a third `transport.protocol` enum value. |
| One reserved peer-ID bit | `src/Allegiance.Core/Transport/ServerTransportMux.cs:8` maps complete `(listener, inner ID)` pairs to monotonically allocated opaque public IDs. All 32 inner bits are valid on all listeners. Retired IDs are never reused; overflow fails closed. `DualTransport.cs:8` uses this same mechanism. Internal numeric IDs can differ; FedPacket bytes and gameplay identities do not. |
| Client's “UDP, otherwise TCP” label | Earlier attempt's `ITransport.ProtocolName` and actual fallback lookup remain in `src/Allegiance.ClientCore/Session/ConnectionSession.cs:31`. No production WS client was added. |
| Native counters tied to TCP prefix | `src/Allegiance.Core/Transport/ServerCounters.cs:83` accepts an unchanged FedPacket, and `EgressAccounting.cs:40` accounts WS framing separately. Existing TCP partial-write accounting remains byte-identical. |
| Host entry point after adding ASP.NET | `src/Allegiance.Server/Program.cs:4` uses an explicit namespaced `ServerMain.Main`; the same argument/error/shutdown path runs, without a global `Program` collision when lobby tests reference both hosts. |

The remaining concrete `TestTransport` cast is an explicitly TCP-only internal
send-worker test seam, not an admission/source/privilege decision. No production
per-peer fact depends on a TCP/UDP/Dual cast.

`src/Allegiance.Server/Transport/WebSocketServerTransport.cs:57` starts a slim,
HTTP/1 Kestrel host only when enabled. The exact configured route negotiates
**only `allegiance-fed-v1`**. One binary WS message carries one complete FedPacket,
including CRC. There is no type-byte greeting; the join callback has an empty
untrusted name and precedes every receive. The validated ordinary LOGONREQ supplies
the canonical pilot name and identity.

The receive loop (`:188`) reassembles bounded segments before invoking ServerHost.
Its event/byte reservations, immutable decode publication and fair tick drain are
unchanged; HTTP/read/write tasks never apply simulation commands. The single
socket writer (`:233`) drains a bounded FIFO/keyed queue. Key zero remains ordered;
both keyed APIs replace unsent snapshots in place. Guaranteed entries are never
shed and replacement never resets their age. Caps include the in-flight message.
Logical retirement immediately suppresses new input/sends; final queued replies
are followed by a close frame on that same writer, with a bounded close handshake.

## Admission, provenance and limits

Configuration is `webSocket` in the existing server JSON; **enabled defaults
false**, port 6080, bind 127.0.0.1, path `/game`, empty required Origin allowlist.
No new environment variables or default Compose port mapping were introduced.
`docker/compose.server.json` explicitly keeps it disabled; Dockerfile EXPOSE is
only documentation. Full keys/examples are in `docker/README.md`, “WebSocket game
listener (P1048)”.

Production TLS terminates at an explicitly configured trusted proxy. The server
uses the **same trust policy as the lobby**, configured in its own process:
`webSocket.knownProxies` contains explicit IPs; all framework default proxy/network
trust is cleared; forwarding consumes at most one symmetric X-Forwarded-For /
X-Forwarded-Proto pair; forwarded Host is never used. Untrusted headers are
ignored. Framework automatic forwarding and hosting startup cannot silently
replace this policy. TLS/plaintext admission then uses the resulting trusted
scheme. Plain `ws` is allowed only with explicit loopback development enabled,
no proxy trust configured and both socket endpoints loopback. Backend production
ports belong behind that proxy's network boundary; no proxy route is published
by this change. An allowlisted Origin does not authenticate a player.

Values below are defaults; `WebSocketConfig.cs:9` defines them and `:37` validates
configuration. Tests named without a file are in
`tests/Allegiance.Core.Tests/Transport/P1048WebSocketTests.cs`.

| Limit / policy | Value and source | Evidence |
|---|---|---|
| Physical + effective-source connections | 1024 global, 64/source; raw sockets include incomplete HTTP; accepted source slots include unauthenticated/closing peers (`WebSocketAdmission.cs:44`) | `Connection_flood_hits_global_or_per_IP_cap_and_slots_are_reclaimed` |
| Pending HTTP / upgrade handshakes | 256 (`WebSocketAdmission.cs:44`) | `Partial_HTTP_handshake_has_absolute_deadline_and_pending_cap` |
| Handshake attempts | 2048 global, 128/source per absolute 60 s window, separately at physical accept and every HTTP request; failed HTTP attempts charged; per-source quota checked before spending global credits (`WebSocketAdmission.cs:15`) | `Rejected_upgrade_handshake_flood_is_capped_by_source_and_globally`, `Admission_global_and_IP_windows_reset_at_the_absolute_boundary`, source-isolation quota tests |
| Source-table storage | 4096 entries; active sources retained, expired inactive sources reclaimed, full table fails closed | `Admission_table_is_bounded_windows_expire_and_live_slots_do_not_expire` |
| HTTP headers/body | 16 KiB each, 32 headers; no static/MVC/API route on this host (`WebSocketServerTransport.cs:72`) | Kestrel bounds plus raw duplicate-Origin rejection test |
| Upgrade / greeting deadline | 10 s from socket admission, not restarted by header bytes; checked before upgrade too | Partial HTTP handshake test |
| Authentication deadline | 60 s from physical admission, not from each packet; checked on receive, dispatch admission, validation notification and monitor; existing tick-owned pre-logon sweep still applies | `Authentication_deadline_is_absolute_despite_complete_valid_packets`, `Human_marking_does_not_authenticate_but_validated_logon_ends_auth_deadline` |
| Initial FedPacket envelope | 16 KiB; configurable 8–16 KiB, enough for the existing maximum join token | `Oversized_pre_and_post_logon_messages_close_even_without_EndOfMessage`, exact-cap test |
| Authenticated FedPacket | min(65536, existing `TcpTransport.ServerInboundMaxFrame`); 64 KiB existing cap at `TcpTransport.cs:171` | Oversize/exact-cap tests, `A_smaller_existing_transport_frame_cap_is_not_weakened` |
| Fragment/segment count | 256, including empty and non-final receive segments; cumulative arrival bytes capped before decoder | `Never_ending_empty_fragments_hit_fragment_count_cap`, oversize without EndOfMessage |
| Fragment progress / total assembly | 5 s between segments, 30 s absolute assembly; neither continuous progress nor a late final fragment resets it | `Stalled_fragment_hits_progress_deadline`, `Continuous_fragment_progress_cannot_restart_absolute_message_deadline` |
| Idle link | 30 s before message start, matching existing native-link tolerance | Bounded receive cancellation; authenticated-deadline test distinguishes authentication from idle |
| Outbound payload / entries | 32 MiB / 8192, including in-flight; configured byte cap cannot exceed TCP's existing hard ceiling | `Queue_caps_include_inflight_and_shed_only_non_guaranteed`, real slow-consumer entry overflow |
| Oldest guaranteed age / socket write | 60 s / 30 s, no human exemption; age survives keyed replacement (`WebSocketSendQueue.cs:19`) | `Replacing_a_guaranteed_stream_cannot_restart_its_oldest_age`, real stalled-writer tests for both reasons |
| Final close handshake | At most min(2 s, send deadline), after FIFO data and a close frame from the single writer | `Close_after_flush_retires_input_immediately_and_delivers_all_final_replies_in_order` |
| Native packet validation | Existing CRC, cbmsg and 256-message cap, unchanged (`Wire/FedPacket.cs:101`, `Server.Dispatch.cs:30`) | `P1048ServerAdmissionTests.Native_packet_CRC_message_bounds_and_count_are_still_enforced` |
| Shared ingress / fairness | Existing 4 MiB payload+event reservation, 96 B/event, 2048 events/peer, 8192 events/tick, 256 events/peer/tick (`ServerHost.cs:111`) | `P1048IngressAndLatencyTests.WebSocket_ingress_reuses_event_and_byte_reservations_and_fair_tick_draining`, both decode modes |
| Identity / credential limits | Existing signature, issuer, audience, time, single-use JTI, canonical name; existing per-connection/per-real-source credential guard | Signed logon/replay, six invalid-ticket cases, close during fragmented/queued auth, trusted-source grouping test |
| Origin / protocol / type | Exact configured Origin; one known subprotocol; binary only | Missing/wrong/duplicate Origin, missing/unknown/multiple subprotocol, text-frame cases |
| Local privilege | Always false for WS, even through a proxy from 127.0.0.1 | Direct/proxied local-role tests, forged-header test, framework forwarding-environment test |

Physical per-IP caps also count the proxy's own sockets. Operators must size that
budget for their proxy population explicitly; forwarding does not bypass the
physical resource bound. Source tables do not grow with indefinite connection
churn. A source already rejected by its own attempt quota cannot spend the global
quota for unrelated clients; a real second-loopback-source test checks this as
well as the pure guard test (the R1 F11 fairness principle). “Fragments” here conservatively counts ReceiveAsync segments as well as
wire continuations, so large frames cannot bypass the count budget.

## Verification

SDK 10.0.404, Debian 13, linux-x64; artwork available. No new NuGet package was
needed. Builds use the writable task NuGet HTTP cache and the brief's disabled
build-server/shared-compiler options.

| Check | Passed / Failed / Skipped |
|---|---|
| New P1048 tests, three isolated processes | **Passed 135/135 × 3**, zero skips. Includes 67 inherited R1/R1b cases through actual WS sockets and the production ingress drain, rather than copied assertions. |
| Broader Core server/transport/identity/credential selection | **683 passed, 1 failed, 2 skipped**, 686 total. The sole failure was the previously ledgered Perf console test; it then passed **3/3 alone**. Details below. |
| Existing Transport namespace within that selection | **257 passed**, plus the one console timing failure. UDP P895 **20/20**, UDP-overhead P895b **14/14**; TCP pool/thread-per-peer, coalescing, graceful-close, ingress, deadline, connection-cap and ownership tests passed. |
| Identity / credential / reconnect | P808 loopback **3/3**; P1022 guard **7/7**, wired credential **8/8**, relays **8/8**; P1023 queue/deadline/short-frame **14/14**; P813c reconnect **16/16**. |
| Existing R1b native authority | P1030 combat **12/12** + honest-client live **1/1**; P1031 treasure **3/3**; P1032 constructor oracle **11/11**; P1033 squads **22/22**; P1034 boarding **14/14**; P1029 reposition **6/6**; P1026 side departure **13/13**. |
| Desktop discovery / selected transport / fallback / reconnect | P897 **774/774 passed**. Existing TCP/UDP preferences, labels, selection and reconnect behavior remain green. |
| Lobby identity / R1 / limits / preview regression selection | **64/64 passed**. |
| Full solution build, including both browser projects | **Passed, 0 warnings / 0 errors**; final source gate recorded after implementation. |
| `tools/build-tools.sh --drop-bins` | **Passed all 143 projects**, zero warnings/errors. |
| Final Native AOT publish + published-binary join | **Passed**, zero build/trim/AOT warnings; canonical ticket logon, side selection, ping echo and replay refusal from the native executable. |
| Embedded runtime without ASP.NET | **Passed**: ordinary server Boot/Shutdown through the proof tool under an isolated Microsoft.NETCore.App-only runtime root. Desktop runtimeconfig also names only Microsoft.NETCore.App. |
| Final full fast tier | **15,287 passed / 2 failed / 7 skipped**, 15,296 total, 998.1 s; one timing offender. All **135 P1048 cases passed**; nine infrastructure checks passed. Projects serial; calibrated limit 12.773 s. Exact-case isolation below. |
| Glossary / parity / whitespace | Passed (glossary **949 paths**); parity scorecard unchanged, no new native-rule credit; `git diff --check` clean. |
| Docker Compose config / image execution | **Skipped**: Docker is not installed. JSON parsed; Compose/EXPOSE changes are documentation/default-off only. Real AOT executable proof is independent of an image claim. |
| Android / training | No Android workload installed; no Android build claim. No training logic changed, so the separate training-change gate is not applicable. |

The broader selection's sole failure was
`ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout`:
“peers must keep receiving per-tick updates while stdout is stalled”. It passed
three exact-method reruns (540, 546, 542 ms); this existing Perf/shared-console
family is already ledgered. No assertion, timeout, tier or game behavior was
relaxed. `P760ServerCallbacksTests`' two existing dock-gate cases were skipped.
The final fast-tier observations are appended to `docs/TEST_FLAKES.md`.

The final-source fast failures were:

| Case | Full fast | Three exact-case isolated reruns |
|---|---|---|
| `MineCombatTests.Live_crossing_broadcasts_a_mine_pop_ahead_of_the_ship_not_at_the_field_centre` | No matching mine explosion observed after the fixture's pump; `First` threw | **pass / pass / pass** |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | Unexpected `AsteroidMined` broadcast | **fail / pass / pass**; the first rerun had the same unexpected broadcast |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates(civ: 5)` | Assertions passed; **15.490 s** exceeded **12.773 s** | **pass / pass / pass**, each below the calibrated limit |

The mining-capacity case also failed an isolated rerun in the baseline partial
P1048 ledger (pass/pass/fail); it remains an existing unresolved gameplay/network
observation. No simulation, assertion, timeout or tier was changed to hide it.
The final-source fast run's earlier carrier/defender offenders both passed this
time; their first-run failures each passed 3/3 exact-case isolation. All outcomes,
including the isolated mining failure, are in `docs/TEST_FLAKES.md` and the
[sanitized verification record](BROWSER_WS_TRANSPORT_P1048_assets/verification.json).
This report does **not** claim a fully green fast gate.

The 67 WS authority reruns inherit the existing **P1022RelayBudgetTests (8)**,
**P1030CombatGatesTests (12)**, **P1032BuildOrderOracleTests (11)**,
**P1033SquadSideTests (22)** and **P1034BoardingParityTests (14)**. Their adapter
uses actual ClientWebSocket input, the real ServerHost reservations/drain and the
ordinary dispatcher, while keeping the original fixtures' explicit simulation
clock. Existing TCP/direct cases remain alongside them. Extra end-to-end tests
boot the normal server with TCP+UDP+WS simultaneously and authenticate signed
canonical identities over each listener. No production client ITransport is part
of this slice.

## Native AOT and runtime proof

Final-source real Release linux-x64 AOT publish compiled Kestrel with **zero build, trim or AOT warnings**.
`file` identifies the output as a stripped native ELF; `ldd` lists only libc/libm
and the platform loader, with no managed .NET/ASP.NET runtime dependency. The
runtime-deps Dockerfile remains in place. The publish directory contains **no managed DLLs**.
The [sanitized native proof result](BROWSER_WS_TRANSPORT_P1048_assets/aot-proof.json)
records the native artifact hash/size and successful join. The child shut down
cleanly after SIGTERM (5 ticks, zero overruns). Final publish restored entirely
from installed packages through an empty local feed, with network auditing
disabled for that offline restore; no package was downloaded.

`tools/p1048-ws-proof/Program.cs` starts that exact published executable on
loopback with a generated in-memory signing key (only its public key is written),
validates a canonical ticket, selects side 0, checks the same ship ID, exchanges
an ordinary ping and verifies replay refusal. It sends SIGTERM to its exact child
PID and waits for clean shutdown, with a bounded kill fallback. No token is put
in a URL/log/config; there is no bridge or arbitrary upstream address. The
`--embedded` mode separately boots the ordinary default-off server for the runtime
boundary proof.

## Measured loopback latency

The in-test client uses ClientWebSocket directly. A metered stream below WS
framing counts actual socket bytes after upgrade/warmup; TCP uses actual sender
write counters. Each run alternates 128 sequential echoes per transport after
16 warmup rounds, with the same 20-byte complete Ping FedPacket.

| Isolated run | WS RTT p50 / p95, ms | TCP RTT p50 / p95, ms |
|---|---:|---:|
| 1 | 0.0204 / 0.0464 | 0.0192 / 0.0514 |
| 2 | 0.0171 / 0.0314 | 0.0183 / 0.0468 |
| 3 | 0.0156 / 0.0336 | 0.0174 / 0.0548 |

The [machine-readable latency measurements](BROWSER_WS_TRANSPORT_P1048_assets/latency.json)
retain these three runs. All three measured WS overhead as **6 bytes client→server** (masked) and
**2 bytes server→client**, versus **5 bytes/frame TCP**. These exclude TCP/IP,
HTTP upgrade, TLS, proxy, simulation tick and network-loss costs. They establish
small-message loopback behavior only, not a 200v200 or WAN latency claim. Ordered
reliable delivery still has head-of-line blocking after bytes leave our queue.

## Reproduction and disposition

```bash
export NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1048/nuget-http
~/.dotnet/dotnet build AllegianceCore.slnx --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
~/.dotnet/dotnet test tests/Allegiance.Core.Tests --no-build --filter FullyQualifiedName~P1048
~/.dotnet/dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true \
  -o .scratch/p1048/aot --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
~/.dotnet/dotnet run --project tools/p1048-ws-proof -- \
  .scratch/p1048/aot/Allegiance.Server "$ALLEGIANCE_ARTWORK_PATH" .scratch/p1048/aot-proof
```

Per-run TRX and build/proof logs are local under `.scratch/p1048/` and the test
projects' ignored TestResults directories. The final fast command is
`tools/run-tests.sh --tier fast --parallel 1 --results-dir TestResults/p1048-final-fast-2`;
projects run serially with unchanged assertions, tiers and calibrated timing limits.
The first run is `TestResults/p1048-final-fast/summary.json`; the final-source
rerun after the admission fairness fix is `TestResults/p1048-final-fast-2/summary.json`. The brief is baseline-tracked and unchanged;
it is absent from the working-tree change set. The published native server and embedded runtime proof both shut down cleanly;
all test/build processes are reaped before final handoff. No public host, rig, mini, Mac or VPS was contacted.

Unfinished scope: **none of items 1–5 deferred**. Docker-only validation is
unavailable, and the full fast gate retains the unrelated failures/timing
observation above; those limitations are explicitly not claimed resolved. P1048b still owns the production desktop/
browser client transport and browser join. It must fetch the short-lived join
ticket **after asset staging** and require a **fresh gesture** for audio/pointer
activation. Existing “not available in the browser” paths remain explicit.

## Native intent

Preserve native FedPacket/message layouts, authenticated connection ownership,
CRC retirement (`FedSrv/FedSrv.CPP:4591–4603`), canonical ticket identity
(`:5834–5878`) and guaranteed/coalesced delivery intent
(`_Utility/Messages.cpp:548–563`). Existing parity rows/tests remain the source of
game-rule evidence. This is a transport adaptation plus bounded public admission,
not a new gameplay port; no new native rule or parity CSV credit is claimed.

## Modern approach

A default-off, AOT-compatible in-server listener feeds the same authoritative
message path. Explicit proxy trust, strict Origin/protocol/type checks,
independent absolute deadlines, bounded source/queue storage and a single writer
make its admission and resource ownership reviewable without changing simulation
rules or enabling the browser client early.
