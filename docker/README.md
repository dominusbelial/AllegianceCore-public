# docker/ — deploy configs

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
