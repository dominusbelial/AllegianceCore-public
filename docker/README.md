# docker/ — deploy configs

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

The experimental NPC native travel planner (P817) is off by default. Set
`ALLEGIANCE_SIM_NATIVE_GOTOPLAN=1 docker compose up --build` to enable it;
`0`, `false`, or `off` disables it. The environment overrides the server JSON
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
