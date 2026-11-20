# Browser build ready for the public test host — P1061

Status: **complete**. Baseline `3dd3e18a`. Started 2026-11-20 15:59, finished 2026-11-20 18:22 (lane local
time). No commits, pushes, deployment or external network access. The lane never contacted the host.

## Deployment shape (item 0)

| Piece | Process | Address in production |
|---|---|---|
| `/play/` files | lobby (ASP.NET Core, the same app that serves `/api`) | `127.0.0.1:8080` (Kestrel loopback only) |
| lobby API, identity, `/identity.html` | the same lobby process | `127.0.0.1:8080` |
| `wss://<public-origin>/game` | game server process (`Allegiance.Server`, `webSocket`) | `127.0.0.1:6080` |
| TLS + routing | Caddy | `:443` (Let's Encrypt), the only public HTTP origin |
| desktop join | game server TCP/UDP | `:6073` (both protocols) |
| STUN | lobby | `3478/3479 udp` |

Caddy terminates TLS and routes `/play/*` and every lobby path to `127.0.0.1:8080`, and `/game` (a
WebSocket upgrade) to `127.0.0.1:6080`. The browser never opens a second port: the page and its
`wss` route share the one 443 origin. Settings that differ from defaults:

| Setting | Default | Production |
|---|---|---|
| `BrowserPreview:Enabled` | `false` | `true` |
| `BrowserPreview:Prefix` | `/play/preview` | `/play` |
| `BrowserPreview:BundlePath` | `browser-preview` | `/browser` (read-only mounted generation) |
| `BrowserPreview:PublicOrigin` | empty | `<public-origin>` (the one `connect-src` WSS entry) |
| `BrowserPreview:WebSocketPath` | `/game` | `/game` |
| `Lobby:KnownProxies:0` | empty | `127.0.0.1` |
| `Lobby:Identity:PublicUrl` | `http://localhost:8080` | `https://<public-origin>` |
| `webSocket.enabled` | `false` | `true` |
| `webSocket.bindAddress` / `port` / `path` | `127.0.0.1` / `6080` / `/game` | same (loopback, behind Caddy) |
| `webSocket.allowedOrigins` | empty | `["https://<public-origin>"]` |
| `webSocket.knownProxies` | empty | `["127.0.0.1"]` |
| `webSocket.allowLoopbackDevelopment` | `false` | `false` |
| `webSocket.publicUrl` | unset | `wss://<public-origin>/game` |
| `minimumClientVersion` | unset | the release the desktop and browser builds report |
| game `password` | empty | a file-held secret, never in the repo |

The lobby image is the repo Dockerfile unchanged; the browser generation is **mounted**. A generation
is produced by a publish pipeline that needs the SDK and the artwork corpus, can be swapped atomically
without rebuilding or restarting the lobby image, and must keep the previous generation for rollback.
Baking it into the image would couple a 326 MB content-addressed tree to every lobby rebuild and make a
rollback an image rollback.

## What changed (file:line)

| Source | Change and reason |
|---|---|
| `src/Allegiance.Lobby/BrowserPreview/BrowserPreviewSettings.cs:13,22,26` | Production prefix (`/play`), `PublicOrigin` and `WebSocketPath`, with closed validation (absolute simple path, not `/api`, one canonical HTTPS origin). |
| `src/Allegiance.Lobby/BrowserPreview/BrowserPreviewHosting.cs:22,37,59-69,138` | Host cache contract (`no-store` HTML/manifests, immutable fingerprinted/content-addressed `packs/<sha256>`, `no-cache` stable names), extensionless pack MIME, and the scoped CSP that adds exactly the same-origin `wss` route only when `PublicOrigin` is set. Still before the rate limiter and still default-off. |
| `src/Allegiance.ClientCore.Browser/Pages/BrowserJoinPolicy.cs` (new) | One closed browser join decision: HTTPS page, same-origin secure route, verified identity server, starter core, and a reported build identity when the server requires a minimum. Password games are admitted; the server still checks the password. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:51,63,67,81,113,166,188` | Loads `client-version.json`, sends `ClientVersion` and the typed `MissionPassword` in LOGONREQ, and returns to the join screen when the server answered a refusal (a correctable password). The old loopback-only and password/version refusals are gone. |
| `src/Allegiance.ClientCore.Browser/Pages/BrowserServer.cs:16` | Source-generated `BrowserClientVersion` contract. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor:12,50` | Player-facing password field (`#join-password`, `oninput`) and non-loopback labels. |
| `src/Allegiance.ClientCore.Browser/wwwroot/index.html`, `menu.html` | Relative `<base href="./">` so the same generation serves at `/play/preview/` and `/play`; pack-provenance notice link. |
| `tools/p1053-browser-publish.py:53,86,114`; `scripts/publish-browser-flight.sh:28-31` | Optional `--client-version` writes `client-version.json` into the generation; absent means "not reported" (server refuses fail-closed). |
| `tools/p1061-browser-provenance.py` (new) | Per-pack attribution/provenance manifest plus the Meshy/HD-pack fail-closed check; run by the publish script. |
| `docker/public/docker-compose.yml`, `Caddyfile`, `server.example.json`, `lobby.env.example` (new) | Host-networking production stack, Caddy site block, production server JSON and lobby settings (placeholders only). |
| `docker-compose.yml:21-28`, `docker/README.md` | New browser-preview settings reflected in the dev stack; production WebSocket topology documented. |
| `docs/BROWSER_HOSTING.md` | Step-by-step deploy + rollback checklist, smoke checks, local compose run and the remaining TLS work. |
| `tools/p1051-browser-host/Program.cs:29-71,86-112,294-340` | Proof host now runs the production shape: one HTTPS origin, lobby `/play/`, trusted proxy, enforced minimum version, password game, desktop join from the lobby listing, and a real old-version refusal. |
| `tools/p1051-loopback-proxy.cjs` | Proof-only Caddy shape: TLS on `https://localhost:8443`, one overwritten forwarding hop, `/game` upgrade to the loopback listener. |
| `tools/p1051-browser-flight.cjs:12-14,67-77` | Same-origin `/play` page, TLS, the wrong-password refusal then the real password, `/play/` pack paths. |
| `tests/Allegiance.Lobby.Tests/P1061BrowserHostingTests.cs` (new), `P1046BrowserPreviewTests.cs:79-88` | Route/MIME/negotiation/cache/CSP/limiter tests at `/play`, and the changed host cache class. |
| `tests/Allegiance.ClientCore.Game.Browser.Tests/P1061BrowserJoinPolicyTests.cs` (new) | Same-origin, scheme, port, verification, core, version and password admission rows. |
| `tests/Allegiance.Core.Tests/P1061JoinGateTests.cs` (new) | Real TCP listener: 0.0.0 refused, missing version refused, wrong password `DPR_BadPassword`, missing ticket refused, matching client admitted. |
| `tools/p1061-deploy-check.py` (new) | Docker-less static validation: compose shape, host networking, Caddy routes, server JSON, Dockerfile COPY sources, no real host/IP/secret literals. |
| `tools/p1051-browser-security.cjs`, `p1053-browser-polish.cjs`, `p1054-browser-sectors.cjs`, `p1058-browser-launch.cjs`, `p1059-browser-devcases.cjs` | Follow the new origin, `/play` route, TLS and password; no assertion weakened. P1058's click observation window is 40 s (from 20 s) because the fixture mission's native auto-start countdown is ~12–15 s. |
| `GLOSSARY.md` | P1061 entry and the configurable prefix. |

## Item 1 — lobby `/play/`, production-grade

- MIME: `application/wasm` for `.wasm`, `application/octet-stream` for `.dat` and the extensionless
  content-addressed `packs/<sha256>` (previously unservable through the closed registry because the
  content-type provider has no extension to map).
- Precompressed negotiation: `br` then `gzip` then identity by quality, one `Content-Encoding`,
  `Vary: Accept-Encoding`; 304 on matching ETag; HEAD supported.
- Cache classes: HTML and every JSON metadata file `no-store`; SDK fingerprints (`.xxxxxxxxxx.wasm/js/dat/css`),
  20-hex content-addressed application files, `packs/<sha256>` and art-manifest entries immutable; other
  stable names `no-cache`.
- Static bundle requests terminate in the preview middleware before `UseRateLimiter`, so the 120 req/min
  global API window never sees them. Proof (security + flight harnesses on one host): **2,246 `/play/` requests, all 200, 0 × 429**,
  while the pages' API calls were 32 × 200 + 2 × 401.
- CSP is scoped to the prefix: `default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'
  wss://<origin><WebSocketPath>; ...`. A non-`/play` response keeps the lobby's original CSP. Tests:
  `P1061BrowserHostingTests` (9 methods, theory rows included) and the updated P1046 suite.

## Item 2 — game WebSocket behind one Caddy hop

Production settings: `enabled`, loopback `127.0.0.1:6080`, path `/game`, `allowedOrigins =
["https://<public-origin>"]`, `knownProxies = ["127.0.0.1"]`, `allowLoopbackDevelopment = false`,
`publicUrl = wss://<public-origin>/game`. No check was relaxed; plaintext is refused unless both ends are
loopback **and** no proxy is configured.

The loopback proof ran this exact topology with a self-signed TLS origin (`https://localhost:8443`),
Caddy-shaped proxy, and a forwarded client address in the documentation range (`203.0.113.7`). Through
the proxy, a crafted upgrade with `Origin: https://evil.invalid` returned **403** and the configured
origin returned **101**. The forwarding source/TLS is accepted only from `127.0.0.1`, and the transport
reports the forwarded address (never loopback privileges): `P1048WebSocketTests` untrusted-forwarded,
trusted-proxy and origin tests, `RwsReviewTests` F1, plus `P1061JoinGateTests` (missing ticket). The
full suite results are in the gate section.

## Item 3 — browser and desktop in one mission

**What the browser sent at the baseline:** LOGONREQ with only `CharacterName`, `FedsrvVer`,
`CrcFileList`, `DwCookie` and `Time` (`src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:141-143`
at `3dd3e18a`), and the UI refused password games (`:61`) and any advertised minimum version (`:63`);
online play itself was loopback-only (`:41`). A dev build reports `0.0.0`, and the server refuses it
(`Server.Dispatch.cs:2815-2830`).

**What it sends now:** the published generation's own release identity from `client-version.json`
(`Menu.Online.cs:67-76,163-166`) and the password the player types in `#join-password`
(`Menu.razor:50`). The server's ordinary checks are untouched: `CheckClientVersion`
(`Server.Dispatch.cs:2815`) and the mission password gate (`Server.MissionsCookies.cs:41-45`,
`DPR_BadPassword`). The browser never stores the password or packs it into a URL/cache, and clears it
after a successful join/disconnect.

Loopback proof through the production shape (`tools/p1051-browser-host` + `tools/p1051-loopback-proxy.cjs`
+ `tools/p1051-browser-flight.cjs`), Chromium 153, real lobby enrollment/passkey/ticket:

| Evidence | Result |
|---|---|
| Lobby listing consumed by both clients | `verified=true`, `passwordRequired=true`, `minClientVersion=2.0.0`, `webSocketUrl=wss://localhost:8443/game`, `address=127.0.0.1`, real port |
| Old build refused | A real TCP logon reporting `0.0.0` was answered: “your client (0.0.0) is older than this server's minimum (2.0.0)” |
| Wrong game password refused | Browser filled a wrong password; server logged `refused: game password incorrect (DPR_BadPassword)`; UI stayed on the join screen |
| Correct password admitted | Browser joined the listed mission; desktop joined the same listing with the same password |
| Both ships in one mission | Browser ships 8099/8104/8108/8112 (side 0) and desktop ships 8100/8103/8107/8111 (side 1), both flying in sector 1053 in the four scenarios |
| Browser saw the desktop | Harness assertion that the browser's contact list contains `DesktopPilot`; desktop saw browser ship poses in its own world |
| Shots both ways | Browser fire created 727–767 drawn projectiles and the desktop received that browser launcher; desktop fire was received by the browser (`shotLaunchers` contains the desktop ship) |
| Cold/warm/stale/storage-refused | 577 pack requests cold, 0 warm, 1 stale-only, 578 with in-memory retry; all four joined and launched |
| Static route/limiter | 2,246 `/play/` responses all 200, zero 429 |

Screenshots (Classic and Modern, 1920×1080 and 800×480) are in
`docs/reports/BROWSER_PUBLIC_HOST_P1061_assets/`. The configured vision model was unavailable in this
container, so they were inspected with a PNG decoder, luminance statistics and an ASCII preview: dark space fields with
sparse stars, a dense central object/asteroid, and HUD text bands top and bottom — real rendered frames,
not blank placeholders. The harness's drawn-ship/projectile/HUD metrics are the semantic proof.

## Item 4 — deployment files

- `docker/public/docker-compose.yml` — host networking for artwork/lobby/server/caddy, no port
  mappings, private `server.json` mount, read-only generation mount, production env.
- `docker/public/Caddyfile` — TLS, `/game` → `127.0.0.1:6080` (HTTP/1.1 upgrade), everything else →
  `127.0.0.1:8080`, overwritten `X-Forwarded-For`/`X-Forwarded-Proto`, HSTS, `encode zstd gzip`
  (Caddy never re-encodes a response that already carries `Content-Encoding`).
- `docker/public/server.example.json` — production `webSocket`, `minimumClientVersion`, password game
  (password placeholder), identity issuer and enrollment audience placeholders.
- `docker/public/lobby.env.example` — the lobby settings, all placeholders.
- `docs/BROWSER_HOSTING.md` — deploy/rollback checklist, exact smoke checks, local compose run.
- `tools/p1061-deploy-check.py` — static validation result: PASS (compose services and host networking,
  Caddy routes, server JSON, Dockerfile COPY sources, no real host/IP/email/secret literals). Docker is
  not installed, so `docker compose config` itself remains for Claude.

## Item 5 — licence audit

| Served component | Licence | Notice in the bundle | Check |
|---|---|---|---|
| FreeAllegiance artwork | MIT, © 2017 FreeAllegiance | `artwork-NOTICE.<hash>.txt` (1,071 B) | 574 asset entries carry “FreeAllegiance Artwork; MIT” provenance |
| KNI 4.3.9001 | Microsoft Public License (Ms-PL) | `THIRD-PARTY-NOTICES.<hash>.txt` | present with source pin |
| MonoGame / Mono.Xna portions | Ms-PL / MIT | same file | present |
| nkast.Wasm 10.0.3 | MIT | same file | present |
| .NET 10 browser runtime, ASP.NET Core / Blazor | MIT | same file + the SDK/runtime notices appended at publish | 112,457 B file |
| Google Brotli decoder | MIT | same file, “License notice for Brotli” | present |
| DejaVu fonts | Bitstream Vera / DejaVu | `font-NOTICE.<hash>.txt` | 17 font assets carry the provenance |
| Application JavaScript | first-party (repo) | — | no third-party JS library in the bundle (only `_framework/dotnet*.js/blazor.webassembly.js`, covered by the .NET notice) |

Per-pack attribution/provenance: `artwork-provenance.json` (254,333 B) lists 15 packs / 604 unique
assets with source name, content hash, size and the pack builder's provenance string; every entry has
provenance. Meshy/HD-pack check: zero hits in every manifest's asset fields (the manifest's own
checklist label is the only textual occurrence of those words). Raw manifest/notice hashes are in
`~/allegiance-evidence/P1061/`.

## Item 6 — lane container proof

Docker is not installed and was not installed. `tools/p1061-deploy-check.py` validates the Dockerfiles
and both compose files statically (a small block-YAML reader, not a claim to be `docker compose config`).
The workstation commands (build images, publish generation with the release identity, save/copy, full
local compose run with Caddy and a trusted local CA) are in `docs/BROWSER_HOSTING.md`. What remains for
Claude: run `docker compose config` and the real Caddy build on the workstation/host.

## Gate results

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (final source, hang workaround) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **148/148 tool projects, 0 warnings / 0 errors** |
| Isolated, 3× in fresh processes: lobby route (`P1046` + `P1061BrowserHostingTests`) | **39/39 ×3** |
| Isolated, 3×: browser join policy + listing (`P1061BrowserJoinPolicyTests` + `P1052BrowserListingTests`) | **35/35 ×3** |
| Isolated, 3×: version/password/ticket (`P1061JoinGateTests`) | **1/1 ×3** |
| Isolated, 3×: proxy provenance/origin (`P1048WebSocketTests` untrusted/trusted/origin + `RwsReviewTests` F1) | **8/8 ×3** |
| Whole lobby suite | **287 passed / 0 failed / 3 skipped** |
| `tools/p1051-browser-flight.cjs` (production shape, Chromium 153) | **green**: 4 scenarios, stale-cookie refusal, context-loss message, no secrets; 765,016 PNG bytes |
| `tools/p1051-browser-security.cjs` | **green**: 9/9 cases (cross-origin route refused, wrong/expired/replayed ticket, stale cookie, back navigation, no zombies, no secrets) |
| `tools/p1058-browser-launch.cjs` (Modern, cases 0/100/500/2000/resume/hold, each a fresh browser) | **green, exit 0**: 0/100/500/2000 launch with one `[ready]`; resume blocks with the visible “Reconnecting…” reason and later launches; hold shows the disabled affordance with the reason and launches after release; no silent drop, favicon present, zero 404s, quiet console. The click observation window was widened from 20 s to 40 s because the fixture mission's native auto-start countdown is ~12–15 s; no assertion changed |
| One `tools/run-tests.sh --tier fast` (last) | **15,685 total: 15,675 passed / 3 failed / 7 not executed**, 571.2 s, 9/9 infrastructure checks, calibrated limit 11.562 s. **Red**, see below |
| `tools/check-glossary-links.py` | **1031 paths** |
| `tools/p1061-deploy-check.py` | **PASS** (compose shape, host networking, Caddy, server JSON, Dockerfiles, placeholders) |

Fast failures: `DeadLatchRespawnLiveTests...` and `AsteroidMinedLiveTests...` each passed **1/1 ×3**
alone (existing ledgered families); `HelpOverlayTests.ControlsSections_FitThe720pManualBand` fails
**0/1 ×3** alone and fails identically on the untouched base `3dd3e18a` in a clean worktree (pre-existing
catalogue/layout mismatch, untouched by this task). All three are appended to `docs/TEST_FLAKES.md`.
Five passing cases exceeded the calibrated per-test limit (13.2–18.0 s) under lane load; all passed.
No Server/Core source changed, so the AOT publish gate does not apply; no client rendering changed, so
the training gate does not apply.

## Evidence

- Bounded assets (625,627 B, under the 2 MB rule): `docs/reports/BROWSER_PUBLIC_HOST_P1061_assets/`
  — browser proof, station/hangar and two-ship frames for Classic+Modern at 1920×1080 and 800×480,
  security proof, launch-case summary, static-request summary and the loopback host summary.
- Raw lane evidence (`<lane evidence dir>` = `~/allegiance-evidence/P1061/`): build/build-tools logs,
  the final Fast log, raw host/proxy/harness logs, the full P1058 launch directory and the flight directory.
- `docs/TEST_FLAKES.md` records the P1061 Fast observations and the base-control result for the
  pre-existing `HelpOverlayTests` failure.

## Remaining for Claude's TLS run

- DNS and the Let's Encrypt certificate for `<public-origin>`.
- Firewall: 22, 80, 443/tcp+udp, 3478–3479/udp, 6073/tcp+udp only.
- Fill `PUBLIC_HOST`, `ACME_EMAIL`, `RELEASE`, operator id, enrollment audience and game password
  outside git; `docker compose config`; `docker compose up -d`.
- The proof proxy's certificate is self-signed; Chrome's real TLS path (Caddy) is untested by a browser
  on this lane.
- Publish the generation with `BROWSER_CLIENT_VERSION` equal to the desktop release and at least the
  server's `minimumClientVersion`.

## Unfinished / limitations

- The container's configured vision model returned 404, so cited screenshots were reviewed with a PNG
  decoder/ASCII preview and pixel statistics, not semantic vision (the numeral/metric assertions are the
  semantic proof).
- The P1053/P1054/P1059 browser harnesses were updated to the new origin/route/password but not rerun
  (out of this brief's gate); the P1051 flight and P1051 security harnesses were rerun green.
- `docker compose config` and a Caddy binary validation cannot run in this container.
