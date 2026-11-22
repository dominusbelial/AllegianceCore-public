# Browser generation hosting

This is the deployment contract for the browser generation: how the lobby serves it, the cache
headers it intends, and the exact build/copy/config/smoke/rollback steps for the public test host.
The local proof host uses the same lobby route with the preview prefix; production uses `/play`.

## What serves what

| URL | Served by | Notes |
|---|---|---|
| `/play/` | lobby (`BrowserPreview`, ASP.NET Core) | published generation, mounted read-only; outside the `/api` limiter. `/play/` redirects (302, query kept) to the game at `/play/menu.html`; the P1047 art preview stays at `/play/index.html` |
| `/api/*`, `/identity*` | the same lobby | unchanged policies, 120 req/min per IP |
| `wss://<public-origin>/game` | game server (`webSocket`) | loopback `127.0.0.1:6080`, reached through Caddy `:443` |
| `:6073` TCP+UDP | game server | desktop client, from the lobby listing |

Caddy terminates TLS for one origin and is the only trusted proxy hop (`Lobby__KnownProxies__0` and
`webSocket.knownProxies` are `127.0.0.1`). No extra port is opened for the browser.

## Cache contract

| File class | Cache-Control |
|---|---|
| HTML entry points; `*-manifest.json`, receipts and other mutable JSON metadata | `no-store` |
| Content-addressed JS/CSS/WASM, SDK fingerprinted `_framework` files, `packs/<sha256>`, hashed `art/` and notices | `public, max-age=31536000, immutable` |
| Any remaining stable-name compatibility file | `no-cache` |

Precompressed `.br`/`.gz` sidecars are negotiated from `Accept-Encoding` with `Vary: Accept-Encoding`;
the publication tool writes gzip (not brotli) for `packs/<sha256>` and both for framework files. Caddy's
`encode` never re-compresses a response that already carries `Content-Encoding`, so nothing is compressed
twice; keep MIME types, integrity validation and same-origin credentials intact.

[`scripts/publish-browser-flight.sh`](../scripts/publish-browser-flight.sh) finalizes a complete local generation with
[`tools/p1053-browser-publish.py`](../tools/p1053-browser-publish.py). The .NET SDK's fingerprinted WASM/runtime files
keep their integrity names; remaining boot, application JS/CSS, KNI interop and
notices receive SHA-256-derived names. `menu.html`/`index.html` embed the generated
map for runtime imports. Boot preloads activate after the existing WebAssembly /
WebGL capability check, before the ordered interop script evaluation. The title
and progress shell exist in static HTML before .NET starts.

Initial staging contains the two menu font bakes and click sound. Join reads
and verifies the complete manifest, prepares the Brotli decoder, and retires
obsolete cache entries before requesting a fresh ticket. Menu-only staging
preserves previous complete join entries; per-file checks and the existing
64 MiB/1,024-entry cache limits still apply.

P1054's `sector-manifest.json` maps the recorded map/seed and sector IDs to
content-addressed original visual files. Join stages only the gameplay base and
entry visuals before the ticket. The four entry asteroid surfaces initially use
the tested 256² copies; their original 2048² files have highest delivery priority
after launch. Sector entry and speculative target/starmap prefetch stage visuals
without awaiting them in simulation or input. Missing sky textures leave the
normal clear/stars/sun components; missing remote rock textures use the existing
lit-solid geometry. Completed textures replace them after bounded row preparation.
Progress and retry stay non-modal. The sector index's hashes are retained when
committing the base manifest, so an otherwise warm base commit does not purge
streamed visuals. Bounded eviction still applies after a larger sector tour;
zero-byte warm reload is qualified for the fitting base + entry working set.

## Play now and guest admission (P1068)

Players can open **Play now** on the lobby home or identity page, or open
`/play/menu.html?online=1` directly. The browser online panel offers **Play now**
next to **Sign in** when `/api/browser` reports an enabled build. Play now opens a
short guest disclosure and the server list directly from the lobby link. Entering
through the browser menu itself offers the guest/sign-in choice before creating a name. Signing in or creating an account
remains optional and uses the existing identity flow.

A guest gets a crypto-random `Pilot-` callsign saved only in this browser's
`localStorage` (`allegiance.guest.callsign`). The panel lets the player edit,
roll or forget it. Blocked storage still allows play with a page-session name.
The callsign is sent to game servers; it is not a reserved account name. Guest
play adds no account career/ladder record and supplies no account squad membership.
Team-level match/squad results can still be recorded; guest play does not disable
ordinary match scoring. Signing in later does not claim or transfer a guest name.
The Guest label and Sign in link stay visible in the online panel, hangar and flight
in Classic and Modern, including fullscreen (the whole page enters fullscreen). Clearing site data or using another browser/device starts
with another pilot.

The listing's `acceptsGuests` fact is derived from actual server admission:
identity omitted means standalone guest admission; otherwise it mirrors
`identity.allowAnonymous`. An absent listing field means **sign-in only**, even
though older standalone servers might accept anonymous connections. Sign-in-only
servers remain listed with disabled joins. The server's reserved-name cache
continues to reject registered names on identity-enabled servers (its existing
unreachable-lobby fail-open behavior is unchanged). Name conflicts offer a one-click
**Try another name** reroll and retry. Password and version checks still run on
the server, and guest routes retain the browser's origin/WSS/core/version gates.
The lobby still publishes WebSocket routes only for verified registrations.

To turn guests off, an operator with identity configured sets
`identity.allowAnonymous` to `false` in the server's existing JSON configuration
and restarts the server. Keep its issuer/public key/audience or enrollment settings.
The next heartbeat advertises `acceptsGuests: false` (or omits the false field),
and a no-ticket logon is refused by the server. Omitting the identity block runs
standalone admission and cannot turn guests off. Defaults are unchanged.
No new service, port, environment variable or compose setting is required.

For loopback proof only, `p1051-browser-host ... --guests 1` enables guests;
its default remains sign-in-only. `tools/p1068-browser-guest.cjs` exercises the
real lobby and server without a virtual authenticator. See the
[P1068 report](reports/BROWSER_GUEST_PLAY_P1068.md) for commands and captures.

## Developer/proof switch (P1059)

The flight toolbar has three controls that exist for the proof harnesses, not for a player:
**View nearby asteroid** (`#flight-rock`, P1054 screenshots), the typed-coordinate **Waypoint**
form (`#flight-waypoint`, `#waypoint-x/y/z`, `#waypoint-go`), and the authoritative
**`#online-pose`** readout. They are hidden from players and kept for developers/harnesses —
nothing is deleted, and every game method behind them (`Game1.ToggleRockView`,
`Game1.FlyToWaypoint`, the `_pose` snapshot) stays.

One switch, resolved by `wwwroot/dev-switch.js` before the .NET runtime starts:

| Way to set it | Effect |
|---|---|
| `?dev=1` on `menu.html` | renders the developer/proof controls for that visit, and keeps them on for later visits (`localStorage['allegiance.dev']='1'`) |
| `localStorage['allegiance.dev'] = '1'` | renders them for every visit until it is cleared |
| `?dev=0` | turns the switch off again (stores `'0'`, overriding a stored `'1'`) |
| `window.allegianceDev.setEnabled(true\|false)` | switches it live without a reload |

The default is **off**: with it off those controls are not rendered at all (not merely
CSS-hidden), so they cannot be clicked or tabbed to. They have no keyboard binding, so there is no
key path to disable. The switch only changes which page controls render: it touches no game rule,
wire message, server check, limit or privilege, and the shared desktop client does not load it.
Numerically nothing changes for a player — the game methods and their native behaviour are
identical in both states.

Harnesses that click a developer/proof control open the page with `?dev=1`
(`tools/p1059-browser-devcases.cjs` for the switch itself; `tools/p1051-browser-flight.cjs`,
`p1051-browser-security.cjs`, `p1048b-browser-join.cjs`, `p1053-browser-polish.cjs`,
`p1054-browser-sectors.cjs` and `p1058-browser-launch.cjs`). Classification and native citations:
`docs/reports/BROWSER_DEV_CONTROLS_P1059.md`.

P1070 adds the docked hangar's HTML launch controls to the same switch: in the Hangar state
`#online-launch`, `#launch-status` and `#launch-reason` render only with `?dev=1`, because the hangar and the full
ship loadout are drawn and driven in the game window (`docs/reports/BROWSER_HANGAR_LOADOUT_P1070.md`). The pane's
other controls (server list, password, sides, briefing, flight toolbar, disconnect) are untouched. A harness that
needs to launch without the switch uses the game window's own verbs through the JS hooks
`window.gameMenu.hangar()`, `window.gameMenu.pointer(x, y, down, wheel)` and `window.gameMenu.launch()`;
`tools/p1070-browser-hangar.cjs` is the reference and `p1059-browser-devcases.cjs` / `p1062-browser-signin.cjs` were
moved onto them.

## Client build identity and the game password

The server advertises `minimumClientVersion` and **enforces it at LOGONREQ**. The browser bundle
writes `client-version.json` at publication (`p1053-browser-publish.py --client-version <release>`,
or `BROWSER_CLIENT_VERSION=<release>` for the shell script) and reports that value in `ClientVersion`.
A generation published without one cannot join a server that requires a version — the logon is refused
with the update reason, exactly as for a desktop build reporting `0.0.0`.

The game password is typed into the browser join panel and sent in the ordinary `MissionPassword`
field; the server's existing join gate compares it and answers `DPR_BadPassword`. Nothing stores it.

## Build the images and the generation (workstation)

Set the release the desktop bundles and the browser generation both report. It must be at least the
server's `minimumClientVersion`.

```sh
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/browser/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=<artwork checkout>
export BROWSER_CLIENT_VERSION=<release>          # e.g. 0.1.6

# 1. Images (repo Dockerfiles, unchanged).
docker build -f src/Allegiance.Lobby/Dockerfile  -t allegiance-lobby:<release>  .
docker build -f src/Allegiance.Server/Dockerfile -t allegiance-server:<release> .

# 2. Browser generation, atomically, from the committed traces
#    (tools/browser-traces/flight-2026-11; the live host's generations were built this way).
scripts/publish-browser-generation.sh .scratch/browser/generation

# 3. Copy to the host (placeholders; use the operator's own access path).
docker save allegiance-lobby:<release> allegiance-server:<release> | gzip > images-<release>.tgz
scp images-<release>.tgz <host-ip>:~/allegiance-public/
tar -C .scratch/browser/generation -czf generation-<release>.tgz .
scp generation-<release>.tgz <host-ip>:~/allegiance-public/
```

## Full local compose run with Caddy (workstation)

This is the same shape as the host, with a local origin and Caddy's internal CA:

```sh
cd docker/public
cp server.example.json server.json          # replace <public-origin> with localhost,
                                            # create the enrollment and the game password
mkdir -p browser-generation
tar -C browser-generation --strip-components=0 -xzf <workstation>/generation-<release>.tgz
# The lobby/server images are the ones built above from the repo Dockerfiles.
PUBLIC_HOST=localhost ACME_EMAIL=unused RELEASE=<release> \
  ALLEGIANCE_MINIMUM_CLIENT_VERSION=<release> docker compose up -d
docker compose ps                            # lobby healthy, caddy/server running
# Caddy uses its local CA for localhost; trust it once if Chrome must accept it:
docker compose exec caddy caddy trust
curl -sI https://localhost/play/ | head -5
```

## Deploy (host)

```sh
# 0. Pull or load base images once: caddy:2.10-alpine, alpine:3.22.2.
# 1. Directory layout (outside the repo):
#    ~/allegiance-public/{docker-compose.yml,Caddyfile,server.json,browser-generation/}
gunzip -c images-<release>.tgz | docker load
rm -rf ~/allegiance-public/browser-generation
tar -C ~/allegiance-public -xzf generation-<release>.tgz
rsync -a --delete <workstation>/docker/public/ ~/allegiance-public/

# 2. Fill the private values (never in git):
#    PUBLIC_HOST, ACME_EMAIL, RELEASE, ALLEGIANCE_MINIMUM_CLIENT_VERSION,
#    ALLEGIANCE_LOBBY_OPERATOR_ID; server.json: <public-origin>, <game-password>,
#    <enrollment-server-id>. Keep server.json mode 0600.

# 3. Firewall: only 22, 80, 443/tcp+udp, 3478-3479/udp, 6073/tcp+udp.
# 4. Start:
cd ~/allegiance-public && docker compose up -d

# 5. Enroll the server's identity (operator device flow, as in the dev stack),
#    then restart the server so its audience/issuer match.
```

Lessons from the live host (2026-11):

- **Optional extras.** `compose.override.example.yml` (STUN, player-hosted games, local fetch
  script) is copied to `compose.override.yml` beside the stack. Delete the Caddyfile `email` line to
  register ACME with no contact address.
- **A generation-only update** means uploading the new generation beside the old one
  (`rsync --copy-dest=<old>` sends only changed files), swapping the directory names, then
  `docker compose up -d --no-deps --force-recreate lobby`. The server and its battle keep running.
  The lobby keeps its server list in memory, so the list is **empty until the server's next heartbeat**
  (up to `heartbeatIntervalSeconds`, 25 s). The server re-registers under a new listing id.
- **A server restart** races Caddy on first boot (enrollment/heartbeat "connection refused", retried
  every 5 minutes). `docker compose restart server` also restarts Caddy, which depends on it.
- **Test on the maps you serve.** Generated maps (`mapGen`) and random civs reach browser paths that
  the static `XXV_simple` map never did: point-list stars (fixed in `6bc0dade`), and station, asteroid
  and texture coverage (P1073). Before switching a public host to a new rotation or civ mode, fly a
  browser guest on it.

The production stack is [`docker/public/docker-compose.yml`](../docker/public/docker-compose.yml)
(host networking, Caddy, lobby, server, artwork), with
[`Caddyfile`](../docker/public/Caddyfile), [`server.example.json`](../docker/public/server.example.json)
and [`lobby.env.example`](../docker/public/lobby.env.example). The lobby image is the repo
Dockerfile unchanged; the browser generation is **mounted** (`./browser-generation:/browser:ro`)
rather than baked into the image, so a generation swap is atomic, needs no image rebuild, and the
past generation can be kept for rollback.

## Smoke checks (exact)

```sh
# 200 + WASM MIME + precompressed negotiation + immutable content-addressed file
curl -sI https://<public-origin>/play/ | grep -Ei 'HTTP/|location'   # 302 → /play/menu.html
curl -sI https://<public-origin>/play/index.html | grep -Ei 'HTTP/|cache-control|content-security-policy'
curl -sI -H 'Accept-Encoding: br' https://<public-origin>/play/_framework/<fingerprinted>.wasm \
  | grep -Ei 'HTTP/|content-type|content-encoding|vary|cache-control'
curl -sI https://<public-origin>/play/menu.html | grep -Ei 'HTTP/|cache-control'   # no-store
# CSP carries wasm-unsafe-eval and exactly the same-origin wss route
curl -sI https://<public-origin>/play/menu.html | grep -i 'content-security-policy'
# WSS upgrade through the origin (101 Switching Protocols; no second port)
# --http1.1: a plain Upgrade over an HTTP/2 connection is refused with 400.
curl -si --http1.1 --max-time 4 -o /dev/null -w '%{http_code}\n' \
  -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
  -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' -H 'Sec-WebSocket-Protocol: allegiance-fed-v1' \
  -H 'Origin: https://<public-origin>' https://<public-origin>/game
# Identity + listing + a browser login and join
curl -s https://<public-origin>/api/servers | head -c 400
# Then in Chrome: open https://<public-origin>/play/ (it opens the game menu), sign in, join the game
# (type its password), select a side, ready, launch; the desktop client joins
# the same listing with the same password and both ships move/fire.
```

A refused upgrade (403) for a wrong `Origin`, 404 for any path outside the published generation, and a
`429` only after 120 `/api` requests in a minute (never for `/play/` static files) are also expected.

## Rollback

```sh
cd ~/allegiance-public
docker compose down                                  # stops the public stack
mv browser-generation browser-generation.<old>       # keep the old generation
tar -C . -xzf generation-<previous>.tgz              # previous complete generation
docker compose up -d
# Full rollback to the previous images:
docker load < images-<previous>.tgz && RELEASE=<previous> docker compose up -d
```

The lobby keeps old immutable files while tabs reference them, so a generation swap does not break
an already-open page; entry pages are `no-store`, so a reload always picks the new generation.

## What remains for Claude's TLS run

- DNS for `<public-origin>` and the Let's Encrypt certificate (Caddy obtains it automatically).
- The host firewall rule set above.
- `docker compose config` on the host (Docker is not installed in the lane).
- The real `PUBLIC_HOST`, operator id, enrollment and game password, all outside git.

The local proof proxy continues to send `no-cache` for HTTP files (it predates these hosting headers);
warm zero pack transfer is provided by the verified browser Cache API independently of them.


## Local battle proof (P1065)

`p1051-browser-host ARTWORK GENERATION SCRATCH --bots N --seconds 900` accepts
0–10 combat seats per side, with zero/off as the default. It spawns an N v N
scenario through the existing MCP driver, targets Ixion (one actual aleph from
Alpha's home), then removes one friendly bot to reserve the browser pilot's
seat. With the pilot aboard the battle is N v N, including at N=1. The MCP
probe remains docked and is excluded from flying combat counts. All fleet
purchases use the existing explicit proof-host wallet/tech grants.

The host keeps anonymous admission disabled. A local filesystem broker creates
lobby fixture accounts and issues normal signed join tickets for each bot; the
MCP `--logon-file` option supplies its build version, mission password and broker
path, and refuses non-loopback hosts. This broker is in the proof tool only,
absent from production server/lobby binaries. The deploy checker rejects its
flags or paths in public deployment placeholders. The host handles its stop
file, SIGINT, SIGTERM and optional time limit, then waits for the exact owned
MCP-driver and proxy PIDs to exit; the driver/MCP own their child PIDs.

Publish scripts also generate `battle-manifest.json` and `battle-receipt.json`.
They declare original hull/missile mesh and texture closures plus the missing
`exp22bmp`/`fx18bmp` effect surfaces. Verified per-hull requests begin only for
visible current-sector ships; contacts and radius-sized stand-ins continue
through cold loads or failures. Missile files share the mechanism; their scene
pass and explosion-film tiling/upload belong to task B. The join manifest is
unchanged. Original files keep `packs/<sha256>` URLs, the 64 MiB/1,024-entry
persistent cache rules, and per-file size/SHA verification. The served artwork
provenance includes these on-demand files.

Use `tools/p1065-browser-battle.cjs` with the preinstalled P1051 harness modules
for contact/draw counts, 1/5/10 frame pacing, actual WASM memory-buffer sizes and
both HUD styles/sizes. This is headless correctness evidence, not a GPU speed
qualification. Exact build/publish/run commands and the lane's browser
availability are in [the P1065 report](reports/BROWSER_BATTLE_SHIPS_P1065.md).
