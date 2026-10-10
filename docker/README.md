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
