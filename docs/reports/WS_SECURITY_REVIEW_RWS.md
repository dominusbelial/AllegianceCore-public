# RWS — security review of the game-server WebSocket transport and browser join (P1048 / P1048b)

Date: 2026-11-18. Read-only review: nothing under `src/` was changed. Two new test files hold the proofs, all
marked skipped; no commits or pushes. Judged against RFC 6455 (handshake §4, framing/fragmentation §5, closing §7,
security considerations §10) and the OWASP WebSocket Security Cheat Sheet (Origin allowlisting, in-band
authentication, message-size and rate limits, proxy-header trust).

## 1. Summary

No way was found for a WebSocket peer to forge its source address, claim loopback, gain a local/admin privilege,
skip CRC/logon/identity checks, or collide with or reuse another peer's ID. The findings are two admission
(denial-of-service) flaws in the listener and two latent route-trust gaps on the lobby and desktop-client side.

| # | Severity | Confidence | Finding |
|---|---|---|---|
| F1 | Medium | Confirmed by test | Behind the trusted proxy, one remote address can lock every other player out of the WS route |
| F2 | Medium | Confirmed by test | Admission is keyed per full IPv6 address, so one /64 exhausts the global handshake budget and every slot |
| F3 | Low | Confirmed by test | The lobby publishes a `webSocketUrl` the registrant does not own (unverified host, player's loopback, third-party host) |
| F4 | Low | Confirmed by test | The desktop WS client follows HTTP redirects to a URL that was never validated |

Nothing rated High or Critical. F1 is the one to fix before the flag is turned on publicly: it applies to the only
supported production topology, with default limits, from a single address at about two connections per second.

**Coverage.**

- **Read in full:** `WebSocketServerTransport.cs`, `WebSocketAdmission.cs`, `WebSocketConfig.cs`,
  `WebSocketSendQueue.cs`, `ServerTransportMux.cs`, `DualTransport.cs`, `ConnectionId.cs`, `IServerTransport.cs`,
  `WebSocketTransport.cs`, `WebSocketClientSendQueue.cs`, `WebSocketEndpoint.cs`, `TransportFactory.cs`,
  `Menu.Online.cs`, `Server.Privileges.cs`, the register/refresh/projection path of `ServerRegistry.cs`, the register
  endpoint in `LobbyEndpoints.cs`, the `/api/identity` filter and `join-token` endpoint, both implementation reports
  and the listener section of `docker/README.md`.
- **Read in part:** `Server.Lifecycle.cs` (transport composition `:960-1005`, pre-logon sweep and peer events
  `:2150-2215`), `Server.Dispatch.cs` (CRC drop and the LOGONREQ validation path), `Server.CredentialLimits.cs`
  (source keying), `ServerHost.cs` (ingress reservation, transport callbacks, drain), `ConnectionSession.cs` /
  `ConnectionSession.Ownership.cs` (join, endpoint freeze, owner/transport lifecycle).
- **Skimmed only:** `TcpTransport.cs` (frame caps and greeting, by search), `LobbyHeartbeat.cs` (the `WebSocketUrl`
  property and payload only), `JoinTokens.cs` (lifetime and claim checks only), `Menu.razor`, the remaining
  `#if BROWSER` blocks of `ConnectionSession.cs` (keepalive read; the rest located by search, not read).
- **Not done:** no Native AOT publish, no Docker run, no real browser run, no real reverse proxy (forwarded headers
  were simulated from a loopback "proxy", as the P1048 tests do). The existing P1048/P1048b suites and the fast tier
  were not re-run; only the new tests were run, as the brief asks.

Tests: `tests/Allegiance.Core.Tests/RwsReviewTests.cs` (4 facts) and `tests/Allegiance.Lobby.Tests/RwsReviewTests.cs`
(1 theory, 3 rows). Each was run unskipped and failed as described below, then marked
`Skip = "RWS finding Fn — pending fix"`. Both test projects build with 0 warnings / 0 errors and the new tests
report as skipped (4 and 1).

## 2. Findings

### F1 — Behind the trusted proxy, one remote address can lock out every other player

- **Severity:** Medium (availability of the whole WS route; existing sessions survive, new joins and reconnects do
  not). **Confidence:** Confirmed by test.
- **Flaw:** `src/Allegiance.Server/Transport/WebSocketServerTransport.cs:83-85` charges the *socket* address at
  physical accept, and `:44` builds both the physical and the effective-source guard from the same settings object,
  so the same per-IP numbers apply to both (`WebSocketAdmission.cs:28` attempts, `:49-50` live connections).
- **Entry point:** the Kestrel connection delegate at `WebSocketServerTransport.cs:80`, before any HTTP is read.
- **Scenario.** Production is defined as "TLS terminates at a trusted proxy" (plaintext is refused otherwise), so
  every physical connection comes from the proxy's address. With defaults:
  - *Attempt budget.* A client at 198.51.100.1 opens 128 connections through the proxy in a minute — exactly its own
    per-source allowance; they can all be refused upgrades (wrong `Origin`). Those are also 128 physical attempts
    from the proxy address, which is the proxy's entire `handshakeAttemptsPerIpPerWindow`. Every further connection
    from the proxy is aborted at accept (TCP reset, no HTTP status) for the rest of the 60 s window. Repeat each
    window: about 2 connections/second from one address blocks all browser joins indefinitely.
  - *Connection slots.* The same client opens 64 idle, never-authenticating sockets (its own `maxConnectionsPerIp`).
    That is also the proxy's whole physical allowance; each lives up to the 60 s authentication deadline and can be
    replaced within the attempt budget. Nobody else can connect.
  - Without any attacker, the route also tops out at 64 concurrent players and 128 joins per minute per proxy.
- **Why the documented mitigation does not work.** `docker/README.md` says to "size that physical per-IP budget for
  the intended proxy population". There is only one knob for each limit, shared by both layers, so raising it for the
  proxy raises every real client's allowance to the same number; per-source isolation cannot be restored by
  configuration.
- **Tests:** `F1_one_remote_source_cannot_spend_the_proxys_handshake_budget_for_everyone` — unskipped, the victim's
  connect at line 46 failed with `WebSocketException: Unable to connect to the remote server` (connection reset by
  peer). `F1_one_remote_source_cannot_hold_every_connection_slot_of_the_proxy` — unskipped, the victim's connect at
  line 65 failed the same way. Both use default limits and the proxy configuration of the existing
  `Only_explicit_trusted_proxy_supplies_source_and_TLS…` test.
- **Suggested fix:** exempt sockets whose remote address is in `knownProxies` from the physical per-IP attempt and
  live caps (keep the global physical caps and the pending-handshake cap), or give the physical layer its own
  limits. The effective-source guard already enforces per-client limits after forwarding.

### F2 — Admission is keyed per full IPv6 address

- **Severity:** Medium. **Confidence:** Confirmed by test (pure guard test; the live-slot half is by reading).
- **Flaw:** `src/Allegiance.Server/Transport/WebSocketAdmission.cs:14` canonicalises only IPv4-mapped addresses;
  `:17-41` and `:46-50` key the table on the whole 128-bit address.
- **Entry point:** `WebSocketServerTransport.cs:127` (`_sources.TryAttempt` on the forwarded address) and `:142`.
- **Scenario.** An ordinary IPv6 subscriber controls a /64. Using 16 addresses from it
  (`2001:db8:1:1::1` … `::10`), 128 requests each, spends all 2,048 global handshake attempts; every other client
  then gets `429` until the window ends, and the attacker repeats each window. The same 16 addresses × 64
  connections fill all 1,024 connection slots with unauthenticated sockets. The existing "a rate-blocked source
  cannot exhaust the global budget" rule (R1 F11) is therefore not met for IPv6. This matters once F1 is fixed;
  today F1 is the cheaper attack.
- **Test:** `F2_one_IPv6_subscriber_prefix_cannot_spend_the_global_handshake_budget` — unskipped, failed at line 84
  with `one /64 (16 addresses) was granted 2048 attempts` (expected at most 128).
- **Suggested fix:** key IPv6 sources by their /64 (mask the low 64 bits in `Canonical`) for both attempts and live
  connections. The credential-guess limiter (`Server.CredentialLimits.cs:79-82`) keys the same way and is worth the
  same change, though it is outside this scope.

### F3 — The lobby publishes a `webSocketUrl` the registrant does not own

- **Severity:** Low today, because the only consumer is gated (see below); it becomes Medium the moment browser
  join is opened beyond loopback. **Confidence:** Confirmed by test.
- **Flaw:** `src/Allegiance.Lobby/ServerRegistry.cs:439-444` accepts any route that passes the *client* syntax
  check and stores it for new and refreshed listings (`:150`, `:175`).
  `src/Allegiance.Core/Transport/WebSocketEndpoint.cs:10-11` gives listing input and trusted configuration the
  identical rule, so a public directory accepts `ws://` loopback routes.
- **Entry point:** `POST /api/servers/register`, `src/Allegiance.Lobby/LobbyEndpoints.cs:26` and `:65-68`. The same
  handler pins `Address` to the caller's socket source and nulls `IdentityServerId` for unverified callers, but
  leaves `WebSocketUrl` untouched.
- **Scenario.** Anyone who can register (no identity needed; rate limit and the optional allow-list are the only
  gates) posts from 203.0.113.5 with `webSocketUrl` set to `ws://127.0.0.1:6080/game`, `ws://localhost:9229/json`
  or `wss://third-party.invalid/anything?x=1`. `GET /api/servers` then serves that route next to the pinned address.
  A browser that trusts the listing opens a WebSocket from the player's machine to the player's own loopback port
  or to an unrelated host, with the lobby's `Origin`.
- **What limits it now.** `Menu.Online.cs:42-51` requires a loopback page, a loopback route, `Verified` and an
  identity server ID; the join ticket is fetched only after connecting and is audience-bound to the listing's own
  identity ID, so a hostile route receives only a ticket for itself; the lobby's CSP is `connect-src 'self'`. So
  there is no present exploit against public players — but all three protections sit outside the lobby, and the
  first two are exactly what a public-join slice will remove.
- **Test:** `F3_unverified_public_registrant_cannot_publish_a_route_it_does_not_own` (3 rows) — unskipped, all three
  failed with `Assert.DoesNotContain() Failure: Sub-string found … "webSocketUrl":"ws://1…"` (and the `ws://l…`,
  `wss://…` equivalents).
- **Suggested fix:** in the lobby, keep `webSocketUrl` only for verified registrations, require `wss`, and refuse
  loopback/private hosts unless the registrant's own source is loopback; split `FromServerListing` from
  `FromConfiguration` so only trusted configuration may name `ws://` loopback.

### F4 — The desktop WS client follows HTTP redirects to an unvalidated URL

- **Severity:** Low (latent: no production desktop path sets `ConnectionSession.WebSocketEndpoint` today, and the
  browser's native WebSocket never follows redirects). **Confidence:** Confirmed by test for the redirect itself;
  the plaintext-downgrade consequence is by reading .NET's redirect rule, not tested (it needs a non-loopback host).
- **Flaw:** `src/Allegiance.Core/Transport/WebSocketTransport.cs:37-49` uses a default `ClientWebSocket`, whose
  handler auto-follows redirects; the endpoint rule (`WebSocketEndpoint.cs:20-29`) is applied once to the advertised
  URL and never to the redirect target.
- **Entry point:** `WebSocketTransport.JoinAsync` (`:30`), reached from `TransportFactory.cs:8`.
- **Scenario.** The validated route answers the handshake with
  `HTTP/1.1 307 Temporary Redirect` / `Location: ws://<other>/…`. The client reconnects there, sends its `Origin`,
  completes the upgrade and then sends LOGONREQ (with the join ticket) to a destination nobody validated. .NET blocks
  only secure→insecure redirects, so `wss`→`wss` and `ws`→`ws` are followed; a `ws://localhost` route can therefore
  land on a plaintext non-loopback host, which the validator exists to forbid. Reconnects repeat the redirect.
- **Test:** `F4_client_does_not_follow_a_handshake_redirect_away_from_the_validated_route` — unskipped, failed at
  line 107 with `Assert.ThrowsAny() Failure: No exception was thrown`: the join succeeded and the peer appeared on
  the second listener.
- **Suggested fix:** connect with an `HttpMessageInvoker` over a `SocketsHttpHandler { AllowAutoRedirect = false }`
  on desktop (the browser branch needs no change).

## 3. Checked and fine

- **Source-address forging / loopback claims.** Forwarding is off unless `knownProxies` is set; defaults for known
  networks/proxies are cleared; one hop only, symmetry required; `X-Forwarded-Host` unused
  (`WebSocketServerTransport.cs:94-102`). Probed live through a loopback "proxy": XFF with a port, IPv4-mapped and
  bracketed IPv6 forms resolve to the right address; a client-prepended XFF value with a single proxy `Proto` is
  refused (403); missing or unparsable XFF is refused; `Proto: wss` is refused; only the last hop is ever used.
  Untrusted peers' headers are ignored (existing tests). `IsPeerLoopback` is a constant `false` (`:303`), and
  `Server.Privileges.cs:11-14` is its only privilege consumer, so no WS peer is ever local-privileged.
- **Origin / subprotocol / method / path.** Exactly one `Origin`, ordinal match against a validated canonical list;
  exactly one requested subprotocol; GET only; exact path, no query (`:129-138`). Authentication is in-band
  (LOGONREQ ticket), not cookies, so cross-site WebSocket hijacking has nothing to ride on.
- **Framing.** Text frames, oversize (pre-logon 16 KiB, then 64 KiB), fragment count, 5 s progress and 30 s absolute
  assembly deadlines, idle and absolute authentication deadlines all close the peer (`:188-232`). The declared
  length is never allocated. `permessage-deflate` is not enabled, so there is no decompression bomb. Ping-only
  traffic does not reset the idle or authentication deadline.
- **Lifecycle.** A churn probe (960 connections; abort, abort mid-fragment, text frame, half-close, full close,
  oversize, concurrent `ClosePeer` / `ClosePeerAfterFlush` / sends from another thread) ended with joins = leaves and
  zero live or pending leases in both guards and zero peers. `OnPeerLeft` is exactly-once and last.
- **Send queue.** Bounded in bytes and entries including the in-flight frame; only unsent non-guaranteed entries are
  shed; keyed replacement keeps position, the guaranteed flag and the original age; guaranteed overflow, a 30 s
  write stall and a 60 s guaranteed age each retire the peer. Enqueue never blocks the simulation thread.
- **What a WS peer gets or skips versus TCP.** Nothing found. CRC, `cbmsg`, the 256-message cap, per-peer
  byte/event reservations and the fair tick drain are in `ServerHost`/`Server.Dispatch`, behind the transport, and
  apply unchanged. Identity, ticket single-use, reserved-callsign and credential-guess limits run on the same
  LOGONREQ path and key on the forwarded address. The "human" flag only affects accounting for WS.
- **Mux peer IDs.** Outer IDs are allocated monotonically under one lock, never reused, fail closed at exhaustion,
  and are removed before the leave is published; inner IDs from different listeners cannot collide
  (`ServerTransportMux.cs:124-146`).
- **Client endpoint validation.** 26 hostile forms probed: `localhost.evil…`, `127.0.0.1.evil…`, percent-encoded
  and ideographic dots, look-alike letters, `0.0.0.0`, `[::]`, IPv4-mapped non-loopback, upper-case scheme,
  missing/extra slashes, userinfo, fragment, control characters — all rejected. `ws://loopback`, `ws://0x7f.1` and
  `ws://2130706433` are accepted but .NET rewrites them to `localhost` / `127.0.0.1`, so they really are loopback;
  a scoped `wss://[::1%25eth0]` is accepted and is also loopback.
  A bare `"ws"` protocol string cannot create a transport; the endpoint and Origin are frozen for reconnects.
- **Join ticket.** 60 s lifetime, audience = the listing's identity server ID, issuer checked, single-use at the
  server, fetched only after the socket is connected, carried only inside LOGONREQ. `/api/identity` responses are
  `no-store`; POSTs require the lobby's own Origin; the session cookie is HttpOnly + SameSite=Strict. No ticket or
  token is written to a URL, storage or the console in `Menu.Online.cs`.
- **Kestrel surface.** HTTP/1 only; one terminal handler for every path (404 / 403 / 400 / 429, no body), each
  request charged; `Connection: close` on every response; 16 KiB header and body limits, 32 headers; a 10 s absolute
  accept-to-upgrade deadline; no static files, routing or other endpoints. `ASPNETCORE_URLS`,
  `ASPNETCORE_HTTP_PORTS` and `ASPNETCORE_FORWARDEDHEADERS_ENABLED` do not change the listener (probed / existing
  test). The framework reference is private to the server project.

## 4. Suspicious but unconfirmed

- **Server-to-client packets over 64 KiB would end a WS session (unconfirmed reachability).** The WS client drops
  the session on any message above 64 KiB (`WebSocketTransport.cs:12`, `:120`); the TCP client accepts frames up to
  16 MiB (`TcpTransport.cs:160`, `:1421`). The server does not cap what it packs for one peer: all PLAYERINFOs of a
  mission in one packet (`Server.Lobby.cs:5068`), the game-over roster including departed players
  (`Server.Lobby.cs:1728`). By size alone that needs roughly 850 ships or 1,090 roster entries, which I could not
  show to be reachable; if a hostile group can inflate either (for example join/leave churn growing the departed
  tail), every browser player in that mission is disconnected at that moment while TCP players are not.
- **Kestrel still reads its `Kestrel` configuration section (not attacker-reachable).** With
  `Kestrel__Endpoints__X__Url` in the environment (or an `appsettings.json` in the working directory) a second
  endpoint is bound; today `Host` then throws at `WebSocketServerTransport.cs:109` and the server fails to boot,
  which is fail-closed. Such an endpoint would bypass the physical admission delegate, which is attached only to
  the code-defined endpoint, if that `Single()` were ever relaxed.
- **128 KiB pooled buffer per unauthenticated connection (by reading).** `ReadLoop` rents 65,537 bytes
  (`:192-193`), which the shared pool rounds to 131,072, before logon, although the pre-logon cap is 16 KiB. At
  1,024 connections that is about 128 MiB held by peers that never authenticate. Bounded, but large for a small VPS.
- **`allowLoopbackDevelopment` with a same-host proxy (by reading, not tested).** If the development block from
  `docker/README.md` is used behind a local reverse proxy with `knownProxies` empty, every remote client arrives as
  127.0.0.1 over plaintext and passes the loopback exception (`:131-133`); all players then share one source for the
  connection and credential limits. `Validate()` does not reject `allowLoopbackDevelopment` combined with HTTPS
  origins. No privilege is gained (WS is never loopback-privileged).
- **`Close` cancels and aborts under the peer lock (by reading).** `WebSocketServerTransport.cs:293-300` runs
  `Stop.Cancel()` and `Socket.Abort()` while holding `peer.Gate`, sometimes on the simulation thread, so cancellation
  callbacks run inside the lock. I found no lock-order inversion and the churn probe did not hang, but the pattern is
  fragile.
