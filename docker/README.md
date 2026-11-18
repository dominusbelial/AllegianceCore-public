# docker/ — deploy configs

P923's experimental `liveSettings.seenWarpRouting` is explicitly `false` in
`compose.server.json`. Set it to `true` in the mounted server JSON and restart
the server to gate legacy NPC BFS by ship visibility and refresh NPC route
caches on discovery. Native GotoPlan already gates visibility with it off.
No environment variable or additional service is required. See
[the routing report](../docs/reports/P923_REPORT.md).

For an encrypted native static core, set `"staticCoreEncrypted": true` next to
`"staticCore"` in the server JSON. It defaults to false: a file named
`zone_core.igc` may already be decrypted. This selects native's cumulative DWORD
XOR body transform (`FedSrv/FedSrv.CPP:9763`); it is a compatibility format,
not encryption for secrets. Mount the chosen core with the ordinary artwork.

## Optional UDP game traffic

TCP pool remains the default. `ALLEGIANCE_TRANSPORT_PROTOCOL=udp docker compose up --build`
adds the UDP listener alongside TCP. Compose maps 6073/tcp and 6073/udp by default;
`ALLEGIANCE_TRANSPORT_UDP_PORT` changes the UDP listener and mapping together.
Clients opt in with the same protocol variable and fall back to TCP if the UDP
handshake fails. See [the configuration and delivery contract](../docs/UDP_TRANSPORT.md).

## Files here

- **`compose.server.json`** — the checked-in **safe default** server config,
  used by the plain `docker-compose.yml` at the repo root (`docker compose up
  --build`). Small map (`XXV_simple.igc`), 20 updates/sec, **no
  `liveSettings.debugMode`** — this must stay production-safe (debug mode is
  opt-in, never on by default; see `docs/reports/P378_DEBUG_MODE.md`/
  `P379_DEBUG_TEST_TOPOLOGY.md`).
- **`compose.server.debugmode.json`** — the exact **live-test server config**
  actually running on the shared remote test host (192.168.0.221,
  `~/allegiance-test/`) as of 2026-09-26: `m2xspiderw.igc` (the only map with
  asteroids for economy/mining testing), 60 updates/sec, and
  `liveSettings.debugMode: true` (the P379 Omega/Plato/Alderan test topology +
  P381/P382's auto-launch/initial-miner-spawn). This was a hand-edited,
  **uncommitted** drift on that host until now — captured here specifically so
  it survives a host loss instead of depending on session memory/luck to
  reconstruct.
- **`compose.server.debugmode.mapgen.json`** — the P402 **generated-arena**
  debug config: identical to the one above except the mission is procedurally
  generated (`"mapGen": { "type": "Brawl" }`) instead of loading
  `m2xspiderw.igc`. Swap the game entry's `"map": "..."` for a `"mapGen"`
  block (any of the 15 topologies — `Brawl`, `SingleRing`, etc.); note
  `teams`/`civIds`/`sideNames` stay at the **game** level, they are not inside
  `mapGen` (the generator reads them from the enclosing game). Drop this file
  in place of the other two in step 3 below to stand up the generated-arena
  debug server. JSON has no comments, so this is a separate file rather than an
  inlined commented-out block (which would not parse).
- **`compose.override.debugserver.yml`** — the `docker-compose.override.yml`
  actually in place on that same remote host: `network_mode: host` (so the
  container's TCP listener is directly reachable, no port-mapping NAT),
  `cap_add: NET_ADMIN`, and a volume mount binding the host's real artwork
  checkout via `$ALLEGIANCE_ARTWORK_PATH` (set it on the host before
  `docker compose`, or it falls back to `docker/artwork-placeholder`), plus
  `compose.server.json`. Also previously uncommitted/remote-only.
- **`artwork-placeholder/`** — a tiny stand-in artwork subset for the plain
  local-trial `docker-compose.yml` path (see its own comments).

## Recovering the live-test deployment on a fresh/replacement host

If the remote test host is ever lost or rebuilt, this is how to stand the
exact same test server back up from the repo alone (no session memory
needed):

```bash
# 1. clone this repo to ~/allegiance-test on the target host
git clone <this repo's URL> ~/allegiance-test
cd ~/allegiance-test

# 2. clone the real game art tree — NOT part of this repo (2.2GB of binary
#    game assets, a separate public repo). The server refuses to boot
#    without it (missions/hulls/parts/stations are all defined in these
#    .igc files). Any path works as long as the volume mount below points
#    at it; this workflow's convention is a sibling directory named
#    allegiance-test-artwork:
git clone https://github.com/FreeAllegiance/Artwork.git ~/allegiance-test-artwork

# 3. drop the live-test config + override into place
cp docker/compose.server.debugmode.json docker/compose.server.json
cp docker/compose.override.debugserver.yml docker-compose.override.yml
# only if this host's artwork clone landed somewhere other than
# ~/allegiance-test-artwork, update the volume path in
# docker-compose.override.yml to match

# 4. build and run
docker compose build server
docker compose up -d server
```

**Do not `rsync --delete` a local `src/` tree directly into a deployed
checkout's `src/` without checking first** — this workflow's normal redeploy
step (`rsync -avz --delete .../src/ host:~/allegiance-test/src/`) only
touches `src/`, but any ad-hoc file placed *inside* `src/` on the remote
that isn't in the local tree will be silently deleted on the next sync.
Config/compose files live outside `src/` for exactly this reason — but if a
future workflow ever widens the rsync scope, treat any local-only edit as at
risk and copy it back into the repo (like this one) rather than leaving it
as the only copy.

## P808a accounts and server identity

`docker compose up --build` now initializes a fresh trial stack: a one-shot Alpine
container downloads two checksum-verified public native files into `trial-artwork`.
It needs outbound HTTPS once. Production artwork mounts can replace that volume.
The lobby migrates its SQLite database before becoming healthy. Named volumes:
`lobby-identity` (database, private signing and provider-state protection keys),
`lobby-trust` (public key only), and `server-identity` (enrollment credential).
The server mounts only the public trust volume; it cannot read the lobby's private
key or database. `docker compose down` preserves identities. `down --volumes`
permanently deletes them and is appropriate only for disposable trials.

Open `http://localhost:8080/identity.html`, choose a callsign and create a passkey.
Copy the displayed account ID to `ALLEGIANCE_LOBBY_OPERATOR_ID` in your environment,
then recreate the lobby (`docker compose up -d lobby`). The server prints an
approval code and URL. In that browser, review and approve the **game server** code.
Its next poll persists the server credential; its listing becomes verified.
Later starts reuse the credential. Until approval it is listed as unverified and
allows anonymous joins. In the game intro, F2 starts player sign-in, displays the
code and URL and opens a browser. F2 also cancels a pending sign-in or signs out.
Signed-in clients use the listed server ID to request a short-lived join ticket.

For production set `ALLEGIANCE_LOBBY_PUBLIC_URL=https://your-lobby-origin` and
`ALLEGIANCE_LOBBY_OPERATOR_ID=<account ID>`, terminate TLS at a trusted reverse
proxy, and configure `Lobby__KnownProxies__0` for its IP if forwarding client IPs.
The server's `--identity-issuer` uses that same public origin. Game-server config
`identity` accepts `issuer`, `publicKeyFile`, `audience` and `allowAnonymous`.
An empty audience uses the server ID in `lobby.enrollmentFile`. Set
`allowAnonymous=false` to require an account. Identity is opt-in outside Compose:
legacy server configurations remain anonymous and ignore legacy opaque tickets.
For a remote server, transfer/pin the public PEM through a trusted operator path;
never mount the lobby's private volume. A public-key response is available at
`GET /api/identity/public-key` for inspection, not automatic trust-on-first-use.

Lobby directory settings (P894): `Lobby:LobbyProtocolVersion` (default 1; a server
stating a different version is refused, native `LOBBYVER_LS`), `Lobby:AllowedServers`
(native's approved-server list; empty = accept all, loopback always allowed; add
`Lobby__AllowedServers__0` in a private override), `Lobby:ServerFilterMode`
(`allow`/`block`), `Lobby:GameFactor` / `Lobby:MaxLoad` (the native percent-load
inputs, 5 / 300), and `Lobby:CurrentClientVersion` / `MinimumClientVersion` /
`UpdateFeedUrl` (the `/api/update/info` advertisement the client updater reads). The
game server enforces `minimumClientVersion` at logon (`ALLEGIANCE_MINIMUM_CLIENT_VERSION`),
advertises its `lobby.location` string, and its `lobby.minimumClientVersion` is now the
same enforced value.

The lobby's `Lobby:Identity` configuration also supports `PublicUrl`, `Store`
(`Sqlite` or `Postgres`), `ConnectionString`, `SigningKeyFile`, `PublicKeyFile`,
`OperatorAccountIds`, and provider blocks `GitHub`, `Google`, `Steam`. Provider
blocks default off. In a private ignored compose override, set, for example:

```yaml
services:
  lobby:
    environment:
      Lobby__Identity__GitHub__Enabled: "true"
      Lobby__Identity__GitHub__ClientId: "${ALLEGIANCE_GITHUB_CLIENT_ID}"
      Lobby__Identity__GitHub__ClientSecret: "${ALLEGIANCE_GITHUB_CLIENT_SECRET}"
```

Google uses equivalent `Google__Enabled`, `Google__ClientId`,
`Google__ClientSecret`; Steam uses `Steam__Enabled` and `Steam__ClientSecret`
(the Steam Web API application key). Provider callback paths are
`/signin-github`, `/signin-google`, `/signin-steam` on the public lobby origin.
Configure forwarded HTTPS at your TLS proxy; never enable a provider with dummy
credentials. Provider login creates a separate account with a chosen unused
callsign; it never silently links to an existing callsign or email.

PostgreSQL is optional:

```sh
# Supply ALLEGIANCE_POSTGRES_PASSWORD securely in your shell environment first.
docker compose -f docker-compose.yml -f docker/compose.override.postgres.yml up --build
```

The override adds PostgreSQL 17 with a named volume and healthcheck; lobby startup
waits for it. It publishes no database host port. Migrations run automatically on
both engines. Changing engines does not copy existing identities: migrate the data
explicitly, preserve the issuer and signing key, and back up both database and keys.
Sessions last 12 hours, device approvals five minutes, join tickets 60 seconds.
Game sessions are held in memory and revoked on sign-out; browser sign-out revokes
its own session. Existing accepted matches continue after sign-out. Back up keys;
key loss invalidates outstanding tickets. Rotation requires distributing a new
trusted public key to game servers; this slice intentionally has no key overlap or
account-recovery UI.

The live PostgreSQL integration test accepts `P808A_POSTGRES_TEST_CONNECTION` in the
test runner's environment (a standard Npgsql connection string). Use a disposable
test database: each run creates and drops a unique schema. This is a test harness
setting, not a lobby/server deployment variable; the Compose PostgreSQL service
stays private to its network unless an operator explicitly adds a test-only port.

## Server self-update (P810) — leave it OFF under Docker

The server can stage auto-updates from a signed release feed between matches
(`server.json` `updates` section; strictly opt-in, default off, staging happens
only at game over and the staged build applies at the next process start).
Under Docker do NOT enable it: the image owns the binaries, and the updater
would fight the image with the container's filesystem. Keep the `updates`
section absent in `docker/compose.server.json` and pull new images instead
(`docker compose pull && docker compose up -d` — the same quiet-point restart
guarantee applies). Bare-metal/binary deployments are the updater's audience.
See `docs/reports/P810_AUTO_UPDATE.md` and `docs/P810_AUTO_UPDATE_DESIGN.md`.

The NPC native travel planner (P817) is on by default. Set
`ALLEGIANCE_SIM_NATIVE_GOTOPLAN=0 docker compose up --build` for legacy steering;
`false` or `off` also disables it. The environment overrides the server JSON
`sim.nativeGotoPlan` boolean. This changes miner, constructor, layer, carrier
and NPC wingman travel; player autopilot and MCP pilot controls keep their own
controllers. See `docs/reports/P817_NATIVE_GOTOPLAN.md` for comparison evidence
and the separate rig acceptance recipe.

P788c's experimental native ship-broadcast scheduling is off by default. Set
`ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER=1` before starting Compose to project
started-mission ship/status updates before simulation advances; `0` retains the
post-update schedule. The equivalent server JSON setting is
`sim.nativeBroadcastOrder`; the environment overrides it. The boot banner and
profiler label record the resolved value. Keep the other broadcast settings the
same when comparing the two modes. See
[`P788C_NATIVE_TICK_ORDER_2.md`](../docs/reports/P788C_NATIVE_TICK_ORDER_2.md).

P906 adds two independent experimental server JSON settings (both default
`false`): `sim.nativeShipPhases` gathers all ships' preplot, plot and execute
steps before ordinary model updates; `sim.nativeCollisionQueue` uses one swept
contact timeline with bounce recalculation and defers movement until contacts
and accumulated mine damage finish. Set these in the mounted server config.
Keep `sim.nativeBroadcastOrder` off while testing either new setting. Do not
combine experiments for acceptance: each requires Claude's separate rig A/B.
See [`P906_REPORT.md`](../docs/reports/P906_REPORT.md) for coverage and limitations.

### Native game goals (P832a)

Set `games[].goals` in the mounted server JSON, for example
`"goals": {"teamKills": 20}`. Fields are `conquestPercentage` (0 disables),
`territoryPercentage` (100 disables), `teamKills`, `flags`, `artifacts`,
`teamMoney` (0..9 times the core's economic-victory price), and
`gameLengthSeconds`; all other zero values disable that goal. Goals may combine.
Omitting `goals` preserves the existing countdown/finale endgame.

`ALLEGIANCE_GAME_GOALS='{"teamKills":20}' docker compose up -d server` replaces
all configured games' goals; an empty environment value preserves the JSON.
Native server CLI alternatives are `--goal conquest|territory|kills|flags|artifacts|prosperity|countdown|none`
or `--goals '{"teamMoney":2.5}'`. CLI overrides environment, which overrides JSON.
These select the rules and initial objectives; showcase bots acquire goal-specific
strategies in later P832 slices. See [P832a](../docs/reports/P832A_GAME_GOALS.md)
for thresholds, presets and local/rig recipes.

### Native create-game options: friendly fire and allied visualization (P856)

Native's create-game screen carries `bAllowFriendlyFire` ("Allow friends to
damage friends", `Igc/igc.h:1149`) and `bAllowAlliedViz` (`Igc/igc.h:1127`).
Both are **off** by native default (`MissionParams::Reset`, `Igc/igc.h:1243,1246`),
which is what omitting them preserves. Set them per game:

```json
{"games": [{"name": "Main", "map": "XXV_simple.igc", "teams": 2,
            "allowFriendlyFire": true, "allowAlliedViz": true}]}
```

or for every configured game at once:

```sh
ALLEGIANCE_GAME_ALLOW_FRIENDLY_FIRE=1 ALLEGIANCE_GAME_ALLOW_ALLIED_VIZ=1 \
  docker compose up -d server
```

The CLI equivalents `--friendly-fire 0|1` and `--allied-viz 0|1` override the
environment, which overrides the JSON. With friendly fire off, same-side and
allied damage is suppressed for ships, stations, missiles and armed probes
(sensor and RescueAny probes stay damageable, `Igc/probeigc.h:74-89`). With
allied visualization on, allied sides see each other's models, share scans, get
the allied home-cluster reveal at start and see an armed probe's first shot
without a scanner of their own. Both settings reach joining clients in the
`MISSIONDEF(38)` parameter bitfield block and are published to the lobby
directory (the client's GAME DETAILS panel shows `FF FIRE:` / `ALLY VIZ:`).
See [P856](../docs/reports/P856_GAME_OPTIONS_FF_ALLIEDVIZ.md).

### Verified duplicate logons (P864c)

By default, a new logon using a character already seated in the mission removes
that old player with `QSR_DuplicateLocalLogon` and admits the newcomer **only if
both connections have the same verified lobby AccountId**. Different accounts,
anonymous peers and pending side selections keep refusing duplicate names.
Native source: `FedSrv/FedSrv.CPP:629`, `FedSrv/fsmission.cpp:1248-1273`.

Set server JSON `"alwaysRefuseDuplicateLogons": true` to keep the former
always-refuse policy. `ALLEGIANCE_ALWAYS_REFUSE_DUPLICATE_LOGONS=1 docker compose
up -d server` overrides JSON; `0` forces the default policy and an empty value
preserves JSON. No legacy CD key, claimed Steam ID or character name establishes
account ownership.

File maps now replace unavailable station types using development plus the
original station's local/effect techs, then populate only empty asteroid/treasure
lists (`Igc/missionigc.cpp:3977-4055`). File-map resources use native Reset defaults:
home/neutral He3 rocks 2/4, special rocks 1/1, generic rocks 20/20, starting treasures
0/5, He3 density 1 (`Igc/igc.h:1284,1315-1328`). Generated-map presets keep their
existing resource settings.

Native bot ownership transfer can be enabled with `botAuthenticationSecret` in
server JSON (default `null`, disabled). A validated bot supplies it through
`LOGONREQ.CDKey`; admin and test-harness access do not grant this role. Keep
credentials in a private config override. No additional service or port is needed.
The server reads the native `maps.txt` catalog from its artwork mount and sends
`STATIC_MAP_INFO` after validated logon. Each entry is `teams,friendly name,file`
after a first-line entry count. Map files remain in that same artwork mount.

Named core leases and standalone character IDs are process-local memory stores;
they need no additional Docker volume. The existing artwork mount supplies core
and map files. Core cache entries retire with their last consumer; standalone
IDs reset when the server process exits.

### LAN hosting ports

The desktop client's **Host LAN game** needs no Compose service. It embeds the
same server and bot driver already shipped in the player bundle. Default ports
are TCP+UDP 6073 for play and UDP 6074 for discovery. For a dedicated container
that should answer LAN discovery, an operator can additionally publish
`6074:6074/udp` in a private override. A custom desktop host port uses discovery at port + 1.
`ALLEGIANCE_LAN_PASSWORD` is a private child-process value supplied by the desktop
host to its bot driver, never an operator environment setting or a Compose secret.
Game-config `password` defaults empty; the existing mission password gate enforces
it for humans and bots, including after match rotation. LAN mode does not publish
through the lobby. See the [player hosting instructions](../QUICKSTART.md#host-friends-from-the-client).

### Internet player hosting

P936 optionally makes this lobby the STUN observer and introduction service for
desktop **Host internet game**. This is separate from LAN mode and dedicated
server registration. Enable it in the operator environment:

```sh
ALLEGIANCE_LOBBY_PLAYER_HOSTED_GAMES=true docker compose up --build -d lobby
```

Compose publishes **3478/udp** and **3479/udp**. With hosting off (the default),
the lobby binds neither socket and rejects player-hosted registration and punch
requests. With it on, both UDP ports must reach the same lobby process as HTTPS
signaling, directly and without a UDP proxy that replaces the peer's source
address. The HTTP listener remains 8080; terminate public HTTPS at your trusted
reverse proxy. Configure `Lobby__KnownProxies__0` in a private override if that
proxy supplies forwarded source addresses. The approved-server filter remains
active for player hosts. Pin a single lobby instance: observation receipts and
introductions are process-local and intentionally ephemeral.

| Compose environment | Lobby setting | Default |
|---|---|---|
| `ALLEGIANCE_LOBBY_PLAYER_HOSTED_GAMES` | `Lobby:PlayerHostedGames` | `false` |
| `ALLEGIANCE_LOBBY_STUN_PRIMARY_PORT` | `Lobby:StunPrimaryPort` | `3478` |
| `ALLEGIANCE_LOBBY_STUN_SECONDARY_PORT` | `Lobby:StunSecondaryPort` | `3479` |
| `ALLEGIANCE_LOBBY_STUN_BIND_ADDRESS` | `Lobby:StunBindAddress` | `::` (dual stack) |

Custom UDP ports must be distinct and map to the same port numbers externally:
the `/api/nat/config` response advertises the actual listener ports. `0` is for
ephemeral loopback fixtures only. Hosts refresh their mapping and listing every
five seconds, poll introductions once a second, and disappear within 20 seconds
without a heartbeat (or the directory's shorter configured maximum age). Each
player host advertises one occupied game slot; it cannot accept the dedicated
create-game relay. Joining its current match remains available. No host enrollment credential is required, and player
listings remain unverified unless the existing enrollment path authenticates
them. HTTPS protects endpoint receipts and introduction secrets. Internet joins
require direct UDP and have no TCP/relay fallback. No public STUN dependency,
UPnP or NAT-PMP traffic is introduced.

P991's `SHIP_STATUS(90)` change gate is on by default (since 2026-11-10, after a
400-bot rig A/B: SHIP_STATUS bytes −87 %, about 3 % of egress): a ship's status
is sent to a side only when it differs from the copy that side already has
(native's rule, `FedSrv/FedSrv.CPP:3665-3719`) instead of the port's three
periodic refreshes. Set `broadcast.shipStatusChangeGate` to `false` in the
mounted server JSON, or `ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE=0` before
starting Compose, to get the old refreshes back. The environment override accepts `0`/`1` or `false`/`true`;
empty preserves JSON. Off is byte-identical to the pre-P991 wire. See
[the P991 measurements](../docs/reports/SHIP_STATUS_CHANGE_GATE_P991.md).

P994's compact gunfire arrays are default off. Set `broadcast.gunfireBatch` in
server JSON or `ALLEGIANCE_BROADCAST_GUNFIRE_BATCH=1` before Compose starts.
Empty preserves JSON; `0`/`1` and `false`/`true` are accepted. New clients decode
both legacy 12/213 and the port-only `GUNFIRE_BATCH(221)`; enable this only when
all connected clients support 221. Audiences and authoritative damage stay the
same. See [the P994 report](../docs/reports/GUNFIRE_BATCH_P994.md).

P993's distance cull of the `PROJECTILE_INSTANCE(12)` relay is default off. Set
`broadcast.fireAudienceCull` in the mounted server JSON, or
`ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL=1` before starting Compose, to relay a
round only to viewers whose own ship is within the client's own 5 900 u
projectile draw radius (`ProjectileDrawRadius`, shared with
`ProjectileVisibility.VisibilityRadius`) plus the round's and the viewer's travel
during the round's lifespan. The shooter's own peer and admin free-camera views
are never culled, and `PROJECTILE_HULL_HIT(213)`'s audience is untouched (bots
consume it). The environment override accepts `0`/`1` or `false`/`true`; empty
preserves JSON. Off is byte-identical to the pre-P993 wire (pinned). See
[the P993 measurements](../docs/reports/FIRE_AUDIENCE_CULL_P993.md).

P968's publisher experiment is default off. Set `broadcast.pendingStreamIndex`
in the mounted server JSON, or `ALLEGIANCE_BROADCAST_PENDING_STREAM_INDEX=1`
before starting Compose, to index the pending snapshot's `(peer, key)` streams
and bypass freshness checks for ordered events. The environment override accepts
`0`/`1` or `false`/`true`; empty preserves JSON. It applies only when immutable
snapshots are enabled and does not change transport or broadcast scheduling.
See [the P968 measurements](../docs/reports/PUBLISHER_STREAM_INDEX_P968.md).

### Civilization strategy experiment (P1000/P1001)

Server JSON `"bots": { "civStrategy": true, "civStrategyCoverageShare": 0.15 }`
chooses and executes a loaded-core portfolio through native donations, constructor
orders and legal fleet purchases. Default: **false**. The existing
`ALLEGIANCE_BOTS_CIV_STRATEGY=1` / `true` / `on` enables it; `0` / `false` / `off`
disables it, overriding JSON. Empty preserves JSON. Compose passes this variable
through. No additional environment variable or service is required.

Coverage share accepts `0..0.25` (default `0.15`). `[civ-strategy]` retains its
bounded choice/research/economy/damage receipts; `[civ-coverage]` records distinct
selection, grant, launch, firing, hit and completion stages. Workers learn the
policy through the existing selected-plan briefing, including remote supervisors
and owned skirmish drivers. See
[the P1001 report](../docs/reports/CIV_STRATEGY_COMMANDER_P1001.md) for gates,
assumptions and the paired rig plan. The switch remains off pending rig review.

## Optional browser preview (P1046)

The lobby route `/play/preview/` is absent by default. It serves a small KNI
viewer, with no login, joining or game connection. The desktop and Android builds
are independent.

For a local publish, run `scripts/publish-browser-preview.sh OUTPUT_DIRECTORY`
with a new generation directory. Set `BrowserPreview__Enabled=true`,
`BrowserPreview__BundlePath=OUTPUT_DIRECTORY`, and `ALLEGIANCE_ARTWORK_PATH` to
the existing Artwork checkout (or `BrowserPreview__ArtworkPath` in settings).
The bundle directory is **outside** the lobby's ordinary `wwwroot`. Restart the
lobby after switching generations; retain the previous directory for rollback.
Publishing does not install a workload or include artwork.

Compose includes an optional one-shot exporter:
`docker compose --profile browser-preview run --build --rm browser-preview`.
It writes the browser bundle into the `browser-preview` volume, read-only in the
lobby. Run the exporter with the lobby stopped when updating that volume. Then
set `ALLEGIANCE_BROWSER_PREVIEW_ENABLED=true` and recreate the lobby.
The lobby also needs the two manifest-listed artwork files (`fig05.mdl` and
`fig05bmp.mdl`) in its read-only `/artwork` mount. The trial downloader only supplies
core/map files: use a private compose override to mount the same full Artwork
checkout used by the stack. An enabled preview fails startup if its bundle or
verified art is missing; disabling it requires neither.

No additional port, listener, API privilege, identity setting, thread isolation
header or server change is involved. Existing API limits continue to apply.
The optional build/export service never needs a network game server.

### Browser game menu qualification (P1047)

The existing browser head also has a separate `menu.html` entry. It compiles the
shared Game1 intro/Options partials against KNI through the Game.Browser and
Core.Browser libraries. The ordinary `/play/preview/` scout page and its v1 lobby
allow-list are unchanged. No browser login, connection session, LAN discovery,
offline host or flight is enabled.

This slice uses a **local static generation**, rather than extending the lobby's
v1 artwork registry to game packs. Build `tools/p1047-menu-opens` and run it once
for Classic and once for Modern with an isolated XDG profile and the existing
artwork configuration. Those real desktop recordings are required inputs to:

```sh
scripts/publish-browser-menu.sh OUTPUT_DIRECTORY CLASSIC_TRACE.json MODERN_TRACE.json
python3 tools/p1047-static-server.py OUTPUT_DIRECTORY
```

The local tool binds only loopback, on port 8047 by default; open
`http://127.0.0.1:8047/play/preview/menu.html`. It serves compressed bytes from the
completed generation. The publisher also stages the unchanged two-file scout
allow-list so the original preview works on that same local server. See
`docs/reports/BROWSER_SEAMS_P1047.md` for complete capture/recording commands,
measurements and qualification limits. Original artwork/font bytes stay in the
operator's generated directory, outside the repository.

The immutable asset cache is bounded to 64 MiB/1,024 entries in the **browser's
Cache API**, with IndexedDB holding residency metadata and non-secret settings.
These stores belong to the user agent, require no container volume or database,
and add no compose service, listener, secret or environment variable. Browser
storage refusal leaves verified assets resident in memory and reports that
preferences cannot survive closing the tab. Saved passwords, credentials and
identity tokens have no persistent browser path. The existing bundle-exporter
Dockerfile includes the new library sources; Docker execution remains a separate
qualification on lanes that have Docker installed.

## WebSocket game listener (P1048)

The server's optional direct WebSocket listener is **off by default**. Configure
`webSocket` in the mounted server JSON, not in lobby settings. No new environment
variables, client transport or browser join UI are introduced in this slice.
TCP/UDP ports and default admission behavior are unchanged. The server remains a
self-contained Native AOT executable in the runtime-deps image; ASP.NET is a build
framework reference, not a managed runtime requirement in that image. The reference
is private to the server, so desktop and embedded single-player consumers retain
their existing Microsoft.NETCore.App runtime requirement.

For a standalone loopback development server (outside Docker), add:

```json
"webSocket": {
  "enabled": true,
  "bindAddress": "127.0.0.1",
  "port": 6080,
  "path": "/game",
  "allowedOrigins": ["http://localhost:8080"],
  "knownProxies": [],
  "allowLoopbackDevelopment": true
}
```

Clients must request **only** `allegiance-fed-v1`. Each binary WebSocket message
contains one complete ordinary FedPacket, CRC included; fragments are reassembled
before the shared decoder. There is no type-byte greeting. Authentication remains
`LOGONREQ.ZoneTicket` and the existing configured signature/issuer/audience/time/
single-use checks. Keep production `identity.allowAnonymous` false. Tokens stay in
memory and out of URLs, headers, configuration, logs and asset caches. P1048b will
fetch a fresh join ticket after asset staging; audio/pointer activation requires a
fresh user gesture when entering flight.

For production, terminate TLS at an explicitly trusted reverse proxy and set
`allowLoopbackDevelopment: false`, an exact HTTPS `allowedOrigins` list and explicit
proxy IPs in `knownProxies`. This uses the **same policy as Lobby.KnownProxies**:
clear all framework default trusted networks/proxies, allow only configured IPs,
consume at most one matching `X-Forwarded-For`/`X-Forwarded-Proto` pair (header
symmetry required), never trust forwarded Host. The game server has its own JSON
list because it is a separate process; configure the same actual trust boundary
as the lobby. Untrusted forwarded headers are ignored. Every WS peer, including a
source of 127.0.0.1 through a trusted proxy, reports **false** for local privilege
checks. Origins do not authenticate a pilot or confer any role.

The application rejects plaintext unless both socket endpoints are loopback,
loopback development is explicitly enabled and no proxy trust is configured. A
container's bridge address is not a loopback exception. For container use, bind
its internal listener explicitly and use a trusted TLS proxy; a private compose
override may add `ports: ["127.0.0.1:6080:6080"]`. The default compose file publishes
no WebSocket port. Restrict the backend port to that proxy; do not expose an
unauthenticated forwarding service. No public proxy/hosting route is added here.

P1048B adds optional `webSocket.publicUrl`, for example
`"wss://games.example.invalid/game"`. The server publishes `webSocketUrl` in its
lobby heartbeat only when the listener is enabled and this URL is configured.
The lobby keeps the route only for an identity-verified registration, on both
creation and refresh. It requires `wss` and a public host: private, loopback,
link-local, unspecified and multicast IP literals and local DNS names are dropped.
Only a registrant whose actual source is loopback may advertise a local/private
WSS host, or a plaintext `ws` route whose host is itself loopback, for local
development (P1051); no other plaintext route is ever listed. DNS names are checked
syntactically, including converted internationalized DNS labels, without resolving
them: verification trusts the enrolled operator
to control its advertised DNS route, not a domain-ownership or DNS-rebinding proof.
Invalid/unknown routes are omitted without rejecting an otherwise valid
registration. Old servers omit the optional field and old clients ignore it.
The route is never inferred from request headers. Userinfo, fragments and
non-WebSocket schemes are rejected.

Clients parse directory input with `WebSocketEndpoint.FromServerListing`: public
`wss` only, including for the browser and desktop. Operator-owned
`FromConfiguration` additionally permits `ws` loopback; it is used by direct
local transport tests and is not a bypass for listing input. One narrow local
exception: when the lobby the client reads is itself on loopback,
`FromLoopbackDirectoryListing` also accepts a loopback `ws`/`wss` route (the
local operator's own server); private/LAN and other plaintext routes are still
refused. A client of a non-loopback lobby always uses the strict rule.
Desktop handshakes (including reconnects) refuse HTTP redirects; the native
browser WebSocket API already fails redirect responses.
Leaving the key absent preserves the default-off discovery contract. No extra
Compose mapping or environment variable is introduced; the existing optional
listener mapping and trusted proxy policy above apply.

The browser menu reads routes only from a selected lobby listing and retains its
existing loopback-only join gate; with a loopback lobby it uses the local
exception above, so local browser play works. Public browser joining stays off.
The starter-pack staging, ticket ordering and shared client session remain in
place. Public hosting, other cores, password prompts, client update qualification,
pointer capture and flight controls remain outside this slice. The earlier local
proof proxy relays to a fixed loopback listener and is not a deployable gateway.

Other `webSocket` settings (all numeric limits must be positive; deadlines finite
and at most one hour):

| JSON key | Default | Meaning |
|---|---:|---|
| `maxConnections` / `maxConnectionsPerIp` | 1024 / 64 | Physical socket and effective source admission ceilings, including pre-logon peers |
| `maxPendingHandshakes` | 256 | Concurrent physical HTTP handshakes and concurrent upgrades |
| `handshakeAttemptsPerWindow` / `handshakeAttemptsPerIpPerWindow` | 2048 / 128 | Separate global/source fixed-window caps at physical accept and every HTTP request; failures are charged |
| `handshakeWindowSeconds` / `maxTrackedSources` | 60 / 4096 | Bounded attempt tables, retained across disconnects; full tables fail closed |
| `handshakeTimeoutSeconds` | 10 | Absolute accept-to-upgrade deadline, not restarted by header bytes |
| `authenticationTimeoutSeconds` | 60 | Absolute accept-to-validated-LOGONREQ deadline; the shared transport pre-logon sweep also remains active |
| `fragmentProgressTimeoutSeconds` / `messageTimeoutSeconds` | 5 / 30 | Progress between receive segments and absolute assembly deadline |
| `idleTimeoutSeconds` | 30 | Silence before a message starts |
| `preLogonMaxMessageBytes` | 16384 | Initial envelope, configurable 8192–16384; accommodates the maximum join token |
| `maxFragments` | 256 | Receive segments per message, including empty/non-final segments |
| `maxQueuedBytes` / `maxQueuedEntries` | 33554432 / 8192 | Hard outbound payload and entry caps, including the in-flight message; byte cap cannot exceed the existing TCP hard ceiling |
| `maxGuaranteedAgeSeconds` / `sendTimeoutSeconds` | 60 / 30 | Oldest guaranteed queued/in-flight age and single socket-write deadline, with no human exemption |

The authenticated inbound ceiling is the smaller of **65536 bytes** and the
existing `transport.maxFrameBytes` setting. CRC/cbmsg/message-count validation,
per-peer byte/event ingress reservations, per-tick fairness and credential limits
are shared with TCP/UDP. Explicit `knownProxies` sockets bypass only the physical
per-IP attempt/live caps so clients do not share a proxy-wide client allowance.
Global physical caps and the pending-handshake cap still apply; the effective
forwarded source always receives its own per-source caps. Non-proxy sockets
retain the physical per-IP caps and cannot forge forwarded sources. Both
admission layers and TCP/WS credential guesses group IPv6 sources by /64;
IPv4 and IPv4-mapped IPv6 use the same IPv4 key. Exact proxy trust and recorded
peer addresses are never masked.
Send queues preserve ordered zero-key traffic and replace only unsent keyed
snapshots; replacing a guaranteed snapshot does not reset its age. Stalled readers
are disconnected. WebSocket/TCP retransmission can still block later messages
behind earlier wire bytes; coalescing cannot retract bytes already sent.

See [the implementation and proof report](../docs/reports/BROWSER_WS_TRANSPORT_P1048.md)
for the measured AOT join, security tests, TCP comparison and lane limitations.
