# P1072 — WebSocket bots: drive allegiance-mcp over the browser transport for load tests

Lane: codex. Base: master **after the P1071 merge** (P1071 edits `tools/allegiance-mcp/Program.cs` and
`BotManager.cs`; do not start from an older base). Five-hour cap from your start time.
Report: `docs/reports/WEBSOCKET_BOTS_P1072.md`, written before implementation and kept current.

## Why

Browser players connect over WebSocket (P1048 listener, P1052 security, P1061 VPS route). Every load test
we have run, including the 200v200 baseline, drove the bots over TCP. So the browser-facing server path has
never been under load:
- `WebSocketServerTransport`;
- the per-connection `WebSocketSendQueue` and its backpressure and drop rules;
- fragment and idle timeouts;
- the admission limits;
- the TLS proxy hop.

The public perpetual battle (P1071) will mix humans in browsers with bots, so this path will carry real traffic.

The client plumbing already exists. Only the bot driver is missing:
- `TransportFactory.CreateClient` (`src/Allegiance.Core/Transport/TransportFactory.cs:7`) returns a
  `WebSocketTransport` whenever it is given a `WebSocketEndpoint`. That transport is `ClientWebSocket`-based and
  builds for desktop (`#if BROWSER` only switches the browser path) and can send an explicit Origin.
- `ConnectionSession.WebSocketEndpoint` / `.WebSocketOrigin` (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:33-35`)
  select it.
- `WebSocketEndpoint.FromConfiguration` (`WebSocketEndpoint.cs:41,58`) accepts `wss://` anywhere and `ws://` only on loopback.
- `tools/p1048-ws-proof` and `tools/p1051-browser-host` already connect real .NET clients over WebSocket.
- `tools/allegiance-mcp` never sets any of this. Sessions are created at `BotManager.cs:230` (probe), `:730`
  and `:1162` with no transport choice, and `TransportProtocol` / `ALLEGIANCE_TRANSPORT_PROTOCOL` reach only tcp/udp.

## Items

### 1 — Transport selection in allegiance-mcp (default unchanged = tcp)

- Add options, accepted both on the command line and as env vars like the other bot settings
  (`ALLEGIANCE_BOT_*` style, see `McpTools.cs`):
  - `--transport tcp|udp|ws` (default tcp, so behaviour is unchanged when omitted);
  - `--ws-url <url>`, required for `ws`, parsed with `WebSocketEndpoint.FromConfiguration`;
  - `--ws-origin <origin>`, required for `ws`, because the server's `allowedOrigins` check rejects bots without it;
  - `--ws-fraction 0..1`, the share of bots that use WebSocket while the rest stay on tcp. The choice is per bot,
    deterministic by bot index, and logged. This is how one battle mixes transports, as the public battle will.
- Apply the choice to **every** session the manager creates: the probe, side bots, the commander path at
  `:1162`, reconnects and P1071 backfill refills. Reconnects keep the bot's original transport, never a silent
  switch (see the `FallbackTransport` comment at `TransportFactory.cs:32-33`).
- TLS for the rig: the rig server is not on loopback, so it needs `wss://`. Add an opt-in `--ws-ca-file <pem>`
  that trusts exactly that CA for bot WebSocket connections. Pin the CA with a custom `RemoteCertificateValidationCallback`
  that builds the chain against that root only. It must never disable validation and must never be
  "accept any cert". Default: the system trust store.
- Each bot logs `[transport] mcp-… ws wss://… (origin …)` once at logon. `ActiveTransportProtocol` reads
  `ws`/`wss` for WebSocket bots, because `WebSocketTransport.ProtocolName` is the URI scheme
  (`WebSocketTransport.cs:20`). Show it in the existing status output.
- Worker processes (`--workers N`, `--worker i/N`) must pass the options through to their children.

### 2 — Measurement: TCP and WebSocket side by side

Reuse what exists; don't build a new metrics system.
- Server: the egress record in `src/Allegiance.Server/Server.Egress.cs` and the WebSocket send queue's
  `EgressEvent.Dropped` (`WebSocketSendQueue.cs:35,38`). The existing benchmark metrics
  (`ALLEGIANCE_BENCHMARK_METRICS`, tick timing) must split by transport: sends, bytes, drops, queue high-water
  marks, and disconnects by reason (idle, send timeout, queue bound, fragment timeout). If a counter that the
  split needs does not exist, add it to the existing metrics output, behind the same flag.
- Bot side, per transport: logon time, snapshot or update age seen by the bot, reconnect count.
- `tools/ws-load-report.py` (stdlib only) reads the server metrics output and bot logs and prints a
  TCP-versus-WebSocket table, with a `--json` option. Add fixture tests.

### 3 — Proof (loopback, artwork-gated live tests)

- A real server with `webSocket.enabled` on loopback (`allowLoopbackDevelopment`, `ws://127.0.0.1`, an allowed
  `http://127.0.0.1` origin), the backfill/showcase rules from P1071 and two sides:
  1. **All-ws smoke:** 2v2 bots over `ws`. They log on, launch, fight, the match ends and auto-next starts a
     second match. Every bot reports transport `ws`.
  2. **Mixed:** 10v10 with `--ws-fraction 0.5`. Exactly 10 bots are on each transport, no bot crosses over after a
     reconnect, and the report table shows both columns.
  3. **Admission:** with the default `maxConnectionsPerIp` (64) and `handshakeAttemptsPerIpPerWindow` (128),
     show what one-IP bot fleets hit (`WebSocketConfig.cs:21-25`, `WebSocketAdmission.cs:21-33`), and that raising
     those two values in a test config admits them. Do **not** add a bot exemption list. The only exemption today
     is `knownProxies` (`WebSocketServerTransport.cs:44`). Record the server-side values a 100-bot test needs.
  4. **CA pin:** a `wss://` loopback endpoint with a test CA (generated in the test, never committed). The bot
     connects with `--ws-ca-file`, and the same bot refuses a server certificate from a different CA.
- A short loopback comparison on this lane: the same 10v10 (or larger if the lane holds it) once all-tcp and once
  all-ws, 5 minutes each. Report tick p50/p95/p99, sends/bytes/drops and bot-seen update age. These are lane
  numbers, not rig numbers; say so.

### 4 — Rig-ready files (not run against the rig)

- `docker/rig/` (or beside the existing rig experiment files; find them, don't invent a location) gets an
  **experiment-only** compose file, used with `-f` and never replacing the untracked rig override. It contains:
  - a TLS proxy sidecar (Caddy with an internal CA, or the proxy already used for the VPS `wss` route;
    cite `docker/public/`) in front of the rig server's WebSocket port;
  - `webSocket.knownProxies` set to the sidecar;
  - the raised per-IP values from item 3.
  It must also show how to export the sidecar CA for the bots' `--ws-ca-file`.
- A spawner flag for the existing spawn scripts (`tools/allegiance-mcp/scripts/`, plus P1071's `bots-spawn-perpetual.sh`)
  that passes `--transport ws --ws-url --ws-origin --ws-ca-file --ws-fraction`. Scripts refuse non-loopback
  targets unless an explicit `--i-mean-it-host` style argument is given. Claude runs the rig and VPS steps.
- `docs/WEBSOCKET_LOAD_TEST.md` covers how to run the A/B (all-tcp, then all-ws, then 50/50) on the rig
  and later against the VPS route. It also says what this test does **not** measure: browser-side WASM frame
  time and rendering cost need real browsers (Playwright harness), a handful at a time.

## Gate

Light gate:
- solution build 0/0 and `tools/build-tools.sh`;
- every new test 3× alone;
- one full Fast sweep at the end;
- `ws-load-report.py` fixture tests;
- the existing P1048/P1052 WebSocket security tests unchanged and passing;
- showcase never-parks 5× only if you touch bot strategy or sim code (you shouldn't; say so either way).

Flakes go in `docs/TEST_FLAKES.md` with 3× isolated reruns. Use `$HOME/.dotnet/dotnet`, `NUGET_HTTP_CACHE_PATH` and
`--disable-build-servers`.

## Not in scope

- Any rig, lane-bots or VPS change, deploy or run. Claude does those after asking Domingo.
- Changing WebSocket security defaults: allowed-origin rules, plaintext-only-on-loopback, admission defaults,
  or certificate validation in general. Test configs may raise limits; shipped defaults stay.
- A server-side bot exemption list or any bot-specific server rule.
- Headless-browser load (many Chromium instances). That is a different, later test.
- Performance changes to the WebSocket transport. Measure only. Any idea goes in the report as a candidate,
  behind a default-off flag for a later rig A/B.

## Common rules

- No commits, pushes, package installs or deploys; leave changes in the working tree.
- Never `git stash`; never `pkill -f`; stop only processes you started, by PID.
- Native first: native has no WebSocket and no bot driver. Say so, and cite the native transport (DirectPlay
  `FedSrv`/`clintlib` connection setup) when you describe what WebSocket bots stand in for. Footer:
  "Native intent / Modern approach", both sections.
- No home paths, hostnames, IPs (beyond loopback and documentation ranges) or secrets in committed files. Never commit
  test CA keys.
- Report: what was done per item, tests with counts, the loopback numbers, and anything unfinished and why.
