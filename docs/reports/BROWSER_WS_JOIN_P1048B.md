# P1048B — Browser WebSocket join

Date: 2026-11-17. Items 1–5 and required gate attempts are complete.
**Fast is not green**: six existing-test failures; each passes 3/3 alone.
This report was created before implementation and updated during the work.
Changes remain uncommitted in the working tree. No commits, pushes, deployment,
public-host traffic or remote performance hosts were used.

| Brief item | Status |
|---|---|
| 1. Shared client WebSocket transport | Implemented and tested on desktop and actual single-threaded Chromium/KNI WASM. No custom WebSocket JavaScript interop. |
| 2. Validated endpoint selection / reconnect | Implemented; route and desktop Origin freeze for resume. Existing TCP/UDP selection and fallback remain green. |
| 3. Additive advertised route | Implemented heartbeat → lobby registry → list JSON → client mirror, with omission and compatibility tests. |
| 4. Signed desktop TCP / WS proof | Implemented; real ConnectionSession validates canonical identity, selects a side, readies, launches, sends accepted controls and receives authoritative updates over each transport. |
| 5. Browser loopback join | Implemented; real passkey registration and login → listing → ticket → side → launch, verified against server state in both styles and sizes. All twelve captures are reviewed below. |

## Changes and transport contract

| Location | Change |
|---|---|
| `src/Allegiance.Core/Transport/WebSocketTransport.cs:9` | One ClientWebSocket implementation for desktop and browser, binary `allegiance-fed-v1`, one message per complete FedPacket. No TCP greeting or application/wire translation. |
| `src/Allegiance.Core/Transport/WebSocketClientSendQueue.cs:5` | One writer, 1 MiB / 256 entries including in-flight data. Key zero remains FIFO/reliable; nonzero keys replace pending entries in place, preserving guarantee and age. Only unsent non-guaranteed streams can be shed; reliable overflow ends the session. |
| `src/Allegiance.Core/Transport/WebSocketTransport.cs:77` | Async send deadline 30 s, guaranteed queued-age ceiling 60 s; no socket write on the caller. |
| `src/Allegiance.Core/Transport/WebSocketTransport.cs:101` | 64 KiB receive cap plus one overflow-detection byte; binary only, at most 256 receive segments, 30 s idle/absolute message deadline and 5 s progress deadline after the first segment. Shorter than CRC width, text, close, fragment overflow and oversize end the session. |
| `src/Allegiance.Core/Transport/WebSocketEndpoint.cs:5` | Private-constructor endpoint obtained explicitly from listing/configuration; URI validation below. |
| `src/Allegiance.Core/Transport/TransportFactory.cs:6` | Explicit validated endpoint selects WS; the existing TCP/UDP/environment/fallback branch is preserved. ProtocolName is the actual `ws` or `wss` scheme. A bare `"ws"` protocol string cannot create an arbitrary endpoint. |
| `src/Allegiance.ClientCore/Session/ConnectionSession.cs:30` / `:474` | Freeze endpoint and Origin for reconnect alongside P897's frozen protocol/UDP port. Changing UI preferences cannot redirect a resume. |
| `src/Allegiance.ClientCore/Session/ConnectionSession.Ownership.cs:29` / `:130` | Browser ownership schedules through Task.Yield, never Task.Run; async socket retirement drains before replacement publication, with the existing generation/OwnerSite checks. TCP/UDP keep their shutdown path. |
| `src/Allegiance.ClientCore/Session/ConnectionSession.cs:536` / `:2350` | Browser keepalive uses cancellable Task.Delay at the existing six-second cadence; async disposal drains it and the owned transport. Sync connection/wait/shutdown/dispose entry points explicitly reject browser use. |
| `src/Allegiance.Core.Browser/Allegiance.Core.Browser.csproj:15` | Select only shared WS client transport files, ITransport, ConnectionId and the browser factory branch. No ASP.NET reference, TCP/UDP sockets or NAT workers. |
| `src/Allegiance.ClientCore.Game.Browser/Allegiance.ClientCore.Game.Browser.csproj:27` | Compile the shared ConnectionSession and ownership partials with BROWSER lifecycle selection; offline hosting and desktop orchestration remain excluded. |

Connection publication, no client OnPeerJoined/OnPeerLeft callbacks, ordered
receive delivery, exactly-once unexpected OnSessionLost, and silent intentional
shutdown mirror `TcpTransport.cs:480-563,1377-1391,1397-1455`. Failed/cancelled
handshakes never publish a session or emit loss. The reader owns receive/loss
callbacks; the sender aborts the socket on failure so callbacks cannot race
between two writers/readers. Callbacks run outside the transport gate, avoiding
inversion with ConnectionSession.Gate. DisposeAsync drains continuations without
blocking WASM. Browser synchronous Join rejects before the desktop GetResult.

The native latest-wins intent is already documented on ITransport:
`_Utility/Messages.cpp:548-563`, `FedSrv/FedSrv.CPP:4189-4196`, and
`FedSrv/fsship.cpp:544`. This adds a transport realization; it adds no game rule
or native-parity row. Existing CRC, identity, authority, ingress and admission
limits remain unchanged and are covered by the 135-case P1048 regression.

## Endpoint validation

Every row is tested through both FromServerListing and FromConfiguration in
`tests/Allegiance.Core.Tests/Transport/P1048BClientWebSocketTests.cs:43`.
Neither browser UI nor this API offers a free-text destination. The browser join
handler additionally requires an actual selected object from its fetched list.

| Input | Result |
|---|---|
| `wss://game.invalid/game` | Accepted, protocol `wss`. |
| `wss://game.invalid:443/game?version=1` | Accepted; a route query is allowed. |
| `wss://game.invalid/path@name` | Accepted; @ in a path is not userinfo. |
| `ws://127.0.0.1:6080/game`, `ws://localhost/game`, `ws://[::1]/game`, `ws://127.1/game` | Accepted loopback, protocol `ws`. |
| `ws://game.invalid/game`, `ws://192.0.2.1/game` | Rejected: plaintext non-loopback. |
| `wss://user:password@game.invalid/game`, `wss://@game.invalid/game` | Rejected: userinfo, including an empty userinfo delimiter. |
| `wss://game.invalid/game#fragment`, `wss://game.invalid/game#` | Rejected: fragments, including an empty delimiter. |
| `wss://game.invalid:0/game` | Rejected: unusable port. |
| `https://game.invalid/game`, `tcp://localhost:6073`, `/game`, empty string | Rejected: scheme/absolute endpoint contract. |
| leading whitespace, a backslash | Rejected; controls and routes longer than 2,048 characters are also rejected. |
| factory protocol `"ws"` without a listing/config endpoint | Rejected; no implicit free-form destination or TCP fallback. |

The browser UI intentionally gates this slice to loopback. The transport endpoint
validator itself accepts public WSS routes, as requested; no public join was
attempted and no TLS certificate/hosting behavior is claimed.

## Discovery and browser path

`src/Allegiance.Server/Transport/WebSocketConfig.cs:10` adds optional
`webSocket.publicUrl`. Enabled + configured advertises a validated canonical URL;
disabled or absent leaves it null. `Server.Lifecycle.cs:330,366` passes it to the
heartbeat. `LobbyHeartbeat.cs:56,251,681` emits optional `webSocketUrl` with null
omission. `src/Allegiance.Lobby/ServerListing.cs:75,141,246` adds trailing optional
fields; `ServerRegistry.cs:150,175,424,490` validates and relays the route on both
registration and refresh. The desktop mirror is
`src/Allegiance.ClientCore.Game/ServerListing.cs:77`; mission expansion retains it.

`P1048BRouteTests` exercises the real heartbeat and HTTP directory: an old server
payload omits the field and remains readable, an old client shape ignores the new
field, refresh can add/remove it, invalid untrusted routes are not published,
and configuration requires both an enabled listener and an explicit public URL.
No request-body cap, limiter, CSRF, Origin, trusted-proxy policy, FedPacket or wire
layout changed. Docker documentation and default-off Compose comments reflect
the key; no production mapping, service or environment variable was introduced.

`src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:25` provides same-origin
identity/list HTTP; `:53` joins only a selected advertised route. The complete
verified starter snapshot must exist first. It parses the resident binary core
and supplies MissionCoreResolver before join; native extensionless `PCore001`
and listing `PCore001.igc` resolve to the same resident core. Only then does the
owned join callback request a short-lived ticket. Cookie sessions remain HttpOnly
and SameSite=Strict; credentials/tickets are absent from URLs, profile storage,
asset caches and committed evidence. The ordinary LOGONREQ carries the ticket.

`:88` selects a server-advertised side; `:101` sends real ready/buy-and-launch
requests, clears the prior loadout ACKs, and requires both a fresh launch ACK and
a new SET_CLUSTER generation before publishing the in-game state. The launch click activates audio
before awaiting network work; the existing independent audio button also requires
a fresh click. No pointer/fullscreen auto-activation exists. The shared intro's
Play Online command and the browser toolbar enter the same flow. Other cores,
password games, unverified identity listings, native client update qualification,
public joining, LAN/direct-connect/offline modes, mission briefings and flight
controls/rendering parity remain explicitly unavailable.

Measured browser limitation: optional Brotli-compressed MISSION_BRIEFING_INFO(996)
decoding throws PlatformNotSupportedException in this WASM runtime. The existing
non-mandatory-message containment keeps the session alive; the UI explicitly
labels mission briefings unavailable. No packet transcoding, wire change or
custom transport JS was introduced to hide this limitation. Flight snapshots,
side assignment, launch and canonical identity are decoded by the shared session.

## Desktop signed join and message comparison

`tests/Allegiance.Core.Tests/Transport/P1048BJoinTests.cs:22` starts a fresh local
server per transport, generates a P-256 signing key in memory, and uses the actual
ConnectionSession/factory. A ticket binds `P1048BPilot` despite a spoofed request
name. Both scenarios select side zero, send ready, reach Started, buy/launch,
and send yaw/pitch/throttle. The test waits until the server's controls match the
sent values within wire quantization tolerance, position changes, and more
server bytes arrive. SINGLE_SHIP_UPDATE is required. Every wait is bounded;
there is no background server loop or synthetic load in this test.

The assertion compares the full occurrence/order of transaction milestone types:
LOGONACK(2), JOINED_MISSION(95), PLAYER_READY(45), BUY_LOADOUT_ACK(5), SET_CLUSTER(7).
The actual sequence and complete unfiltered decoded streams are retained in
[desktop-join.json](BROWSER_WS_JOIN_P1048B_assets/desktop-join.json). It does not
claim equality of timestamps, message bodies or all tick-broadcast positions.

The final retained run has **468 TCP / 437 WS decoded messages**. Both have
38 EXPORTs and 316 BUCKET_STATUS messages. Their milestone sequence is exactly
`[2, 95, 7, 5, 45, 5, 7]`. Examples of timing/coalescing differences:

| Message | TCP occurrences | WS occurrences |
|---|---:|---:|
| HEAVY_SHIPS_UPDATE(176) | 8 | 7 |
| SINGLE_SHIP_UPDATE(78) | 4 | 4 |
| SHIP_WARNING_UPDATE(218) | 33 | 8 |
| STATIONS_UPDATE(57) | 2 | 1 |
| ASTEROIDS_UPDATE(179) | 2 | 1 |

Periodic HEAVY_SHIPS_UPDATE(176), SINGLE_SHIP_UPDATE(78), SHIP_STATUS(90),
STATIONS_UPDATE(57), ASTEROIDS_UPDATE(179), warning updates (208/214/218), own
mining/cloak (206/220), and occasional discovery EXPORT(26) can differ in count
and interleaving. The fixture pumps ticks while awaiting actual socket delivery,
so TCP and WS completion occurs at different tick boundaries. Their unchanged
join transactions retain order; all raw types, including these differences,
remain visible in the evidence. This is a real socket/protocol comparison, not
a latency benchmark or a claim of deterministic scheduler timing.

## Browser evidence

`tools/p1048b-browser-host/Program.cs` starts the actual lobby application on
loopback with its real limits and identity endpoints, enrolls the game server
through the existing operator/device-store flow, and ticks the real server.
`tools/p1048b-loopback-proxy.cjs` serves only local proof assets and fixed local
API/WS destinations. WS upgrade bytes, frames, Origin and subprotocol pass
unchanged to the P1048 listener; this is not a WS→TCP game gateway. It preserves
the existing static proof host's CSP verbatim and the production lobby policies.
It is not a production hosting implementation.

`tools/p1048b-browser-join.cjs` uses Chromium's virtual authenticator with the real
production passkey page: register → sign out → sign in, then stage the KNI menu,
select style, list, join, side and launch. It asserts server-side canonical player
presence and a launched ship, ticket-after-staging ordering, no unexpected browser
errors, and disconnect cleanup. The unchanged identity limiter required pacing
between groups of scenarios; no limit was raised. `localhost` is used for the
WebAuthn relying-party identity after the IP-host registration attempt failed.
The original scout preview also passes a separate scout/rotate/zoom smoke check.
An early exact-name resident-core resolver failed on the native extensionless
core name; normalizing the existing `.igc` suffix fixed that join. The packaged
single-process Chromium also requires a fresh browser per scenario; the harness
now closes each exact child before launching the next. Compact captures exposed
a low panel border at 800×480; centering within the viewport fixed the fit.

All twelve final PNGs were opened and visually inspected, including the compact
panel fit. They show the actual KNI intro underneath the browser session panel;
they do not show a flight HUD or claim flight rendering parity. Classic/Modern
are real persisted style selections; the join panel has shared behavior/layout.

| State and visible content | Modern 1080p / 800×480 | Classic 1080p / 800×480 |
|---|---|---|
| List: signed-in canonical callsign, real local server and mission join action | [1920](BROWSER_WS_JOIN_P1048B_assets/list-modern-1920x1080.png) / [800](BROWSER_WS_JOIN_P1048B_assets/list-modern-800x480.png) | [1920](BROWSER_WS_JOIN_P1048B_assets/list-classic-1920x1080.png) / [800](BROWSER_WS_JOIN_P1048B_assets/list-classic-800x480.png) |
| Side select: live Alpha/Bravo roster and actual `ws` label | [1920](BROWSER_WS_JOIN_P1048B_assets/sides-modern-1920x1080.png) / [800](BROWSER_WS_JOIN_P1048B_assets/sides-modern-800x480.png) | [1920](BROWSER_WS_JOIN_P1048B_assets/sides-classic-1920x1080.png) / [800](BROWSER_WS_JOIN_P1048B_assets/sides-classic-800x480.png) |
| In game: server-assigned ship/sector/position and incoming bytes, fresh-click audio, explicit unsupported features | [1920](BROWSER_WS_JOIN_P1048B_assets/flight-modern-1920x1080.png) / [800](BROWSER_WS_JOIN_P1048B_assets/flight-modern-800x480.png) | [1920](BROWSER_WS_JOIN_P1048B_assets/flight-classic-1920x1080.png) / [800](BROWSER_WS_JOIN_P1048B_assets/flight-classic-800x480.png) |

Final Chromium measurements, server snapshots and PNG byte total are in
[browser-proof.json](BROWSER_WS_JOIN_P1048B_assets/browser-proof.json).

Chromium **153.0.8010.0**, software ANGLE/SwiftShader, single-threaded WASM.
The twelve PNGs total **742,596 bytes** (below 2 MiB).
All four runs retained **2,561,241 resident asset bytes**. Timings below include
Playwright click handling, browser scheduling, asset parsing already completed,
real server ticks and UI polling; they are functional observations, not network
latency benchmarks.

| Style / viewport | Join to side screen (ms) | Ready/launch to in-game panel (ms) |
|---|---:|---:|
| Modern 1920×1080 | 296 | 246 |
| Modern 800×480 | 166 | 7887 |
| Classic 1920×1080 | 179 | 139 |
| Classic 800×480 | 107 | 8338 |

## Build, size and verification

| Gate | Result |
|---|---|
| Solution including both browser projects | Passed, 0 warnings / 0 errors on the final repeat. |
| `tools/build-tools.sh` | Passed: all 144 projects, no warnings/errors. Existing local packages only; build-server workaround retained. |
| New tests, three isolated selections | 49 passed / 0 failed / 0 skipped ×3 separate processes on final source, including accepted-control verification. |
| Existing P1048 suite | Passed: 135 / 0 / 0. Explicit class exclusions avoid accidentally excluding P1048Boarding/BuildOrder through the `P1048B` prefix. |
| Broader Core server/transport/identity/credential/reconnect/R1 selection | Passed: 721 passed / 0 failed / 2 skipped (723 total), including 46 then-existing new cases; 675 existing passed and two existing P760 skips. |
| P897 / join ownership | Passed: 774 P897 cases plus 12 ownership cases, 786 / 0 / 0. |
| Lobby identity / R1 / limits / preview selection | Passed: 56 / 0 / 1; optional PostgreSQL integration unavailable. |
| Existing browser seam project | Passed: 23 / 0 / 0. Boundary now intentionally includes ConnectionSession while excluding TCP/UDP/NAT/offline/native types. |
| Browser menu publish | Passed, trimmed Release; no wasm-tools/AOT workload installed or claimed. Same original starter pack and font/artwork checks. |
| Dedicated server Native AOT | Passed Release linux-x64, 0 build/trim/AOT warnings. Published native binary passed signed canonical logon, side selection, ping and replay-refusal proof. Local GCC linker selected explicitly. |
| Full fast tier | **Failed**: 15,333 passed / 6 failed / 6 skipped; 15,345 covered in 913.7 s. All 49 new and 135 P1048 cases passed. Every failed fact passed 3/3 alone; details below. |
| Glossary / parity / whitespace / scripts / public hygiene | Passed: 956 glossary paths, parity matrices, whitespace and JS syntax; tracked and untracked content scanned with no secrets, personal emails, home paths or brief in the change set. All 12 images reviewed. |
| Docker Compose config / image run | Not run: Docker is not installed. No image execution is claimed. |

Same-SDK baseline HEAD and current Release publish, including identical generated
packs, excluding duplicate `.gz`/`.br` files:

| Payload | Baseline bytes | Current bytes | Delta |
|---|---:|---:|---:|
| Raw | 19,844,420 | 20,025,022 | +180,602 |
| Per-file gzip-9 | 6,529,139 | 6,600,974 | +71,835 |
| Per-file Brotli-11 | 5,052,964 | 5,114,799 | +61,835 |

These are offline bundle sizes, not browser transfer or cold-start measurements.
[Exact size accounting](BROWSER_WS_JOIN_P1048B_assets/bundle-size.json).

## Final Fast gate and isolated failures

The complete Fast run covered **15,345 cases in 913.7 s**: **15,333 passed,
6 failed, 6 skipped**. Nine infrastructure checks passed. The calibrated limit
was **11.579576 s**; the failed nanite, asteroid-combat and side-elimination
cases took **21.865 / 14.326 / 16.730 s** respectively. There were no additional
passing-only timing offenders. All **49 new cases** and **135 existing P1048
cases** passed in the full sweep.

Every one of the six failed cases then passed **three times alone**, one exact
fact in each fresh test process. No assertion, threshold, quarantine or tier was
changed. These isolated passes do **not** make the failed sweep green.

| Exact case | Fast failure | Three repetitions alone (seconds) |
|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | Remote death latch never arrived after 2,500 ticks | **pass / pass / pass**, 1.344 / 1.315 / 1.324 |
| `NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire` | Repair target never reached full; latest health fraction 0.554 | **pass / pass / pass**, 3.099 / 3.042 / 3.071 |
| `AsteroidCombatLiveTests.Lethal_hit_kills_asteroid_removes_from_mineable_and_broadcasts_destroy_to_whole_mission` | Lethal cross-side hit did not kill the asteroid | **pass / pass / pass**, 0.416 / 0.420 / 0.426 |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | Accumulated roster time 8.25 outside [4, 8] | **pass / pass / pass**, 0.566 / 0.528 / 0.519 |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | Unexpected AsteroidMined broadcast after capacity (fraction 228) | **pass / pass / pass**, 1.754 / 1.759 / 1.758 |
| `SideEliminationLiveTests.Debug_mode_elimination_still_declares_winner_with_zero_players` | Lethal hit did not destroy the debug station | **pass / pass / pass**, 0.326 / 0.325 / 0.326 |

These tests exercise existing repair, death, asteroid, mining, debug elimination
and roster-clock behavior. Those implementations were not changed. Several have
long-ledgered receipt/clock families; isolation does not establish a root cause.
The task does change client transport/lifecycle seams, so its targeted TCP/UDP,
identity, credential and reconnect results are reported separately rather than
using isolation as proof of unrelatedness. The observations are added to
`docs/TEST_FLAKES.md`.

[Full gate accounting](BROWSER_WS_JOIN_P1048B_assets/fast-summary.json) ·
[All eighteen isolated results](BROWSER_WS_JOIN_P1048B_assets/isolation.json) ·
[Build/test/media verification](BROWSER_WS_JOIN_P1048B_assets/verification.json).

## Reproduction and remaining scope

Use the installed SDK and read-only artwork configured in `local.env`; before
all builds/tests/restores set
`NUGET_HTTP_CACHE_PATH=$PWD/.scratch/p1048b/nuget-http`.
Restore uses the preinstalled local NuGet package directory as the source; no new
package download is needed. Standalone builds use
`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`.

Publish the menu with `scripts/publish-browser-menu.sh` and the two P1047 desktop
open traces (or regenerate them with `tools/p1047-menu-opens`). Build and run
`tools/p1048b-browser-host` with artwork, published wwwroot and a fresh scratch
directory. It binds only local proof ports 8048/8049 plus an ephemeral game port.
Pass a local npm installation directory containing Playwright 1.65.1 and
`@sparticuz/chromium` 153.0.0, followed by that scratch `state.json`, as the two
arguments to `node tools/p1048b-browser-join.cjs`. No browser downloads are required.
Touch the host scratch `stop` file to stop its server/lobby and exact proxy child.

No required implementation item is left unfinished; all gate attempts and
required failure repetitions are complete. The full Fast run remains failed as
recorded above. Every proof server, lobby, proxy, Chromium and test process started
by this lane ended. The brief is retained only in ignored task scratch and is
absent from the change set. Public hosting/TLS/CDN, full
client update/core/password paths, flight controls/renderer parity and optional
mission briefing decompression remain outside this slice. No remote deployment
or broad browser compatibility claim is made.

## Native intent / Modern approach

Preserve the existing native LOGONREQ/ticket identity, side/ready/launch/input,
authority, CRC, visibility, throttles and FedPacket ordering. Native rules and
wire layouts are unchanged. The modern addition is a bounded asynchronous WS
transport, explicit advertised route, and a source-shared WASM session that uses
the event loop and verified resident assets. Both HUD style selections use the
same join behavior; unsupported browser capabilities remain explicit.
