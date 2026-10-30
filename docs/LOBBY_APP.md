# LOBBY_APP — the server-directory service (`Allegiance.Lobby` + the server heartbeat)

Durable reference for the Lobby feature area (not a per-task report — the numbered
`ALLEGIANCE_LOBBY_STAGE1_P174.md` / `..._STAGE2_P176.md` / `..._STAGE3_P177.md` docs
record the individual tasks). This page records the *why* behind the design decisions
and the operational/deployment gotchas, each confirmed against the landed code.

The motivating plan is `wiki/pages/allegiance-lobby-app-plan.md` (repo owner's stated
goal: make it easy for anyone to self-host an Allegiance server and be discoverable —
"easy for everyone to run on the internet").

Two parts:

- **`src/Allegiance.Lobby`** — a modern ASP.NET Core (Minimal API) server directory:
  game servers register/heartbeat themselves, anyone queries who's live. Purely
  in-memory.
- **`src/Allegiance.Server`'s opt-in heartbeat client** (`LobbyHeartbeat.cs`) — a
  self-hosted server announces itself to a Lobby directory. Fully opt-in: no `"lobby"`
  block in the config ⇒ zero HTTP calls.

---

## Design decisions

### In-memory vs SQLite — and why in-memory (Stage 1)

The registry is a `ConcurrentDictionary<string, ServerListing>` living entirely in
RAM (`ServerRegistry.cs`). No SQLite, no disk. Rationale:

- The state is **ephemeral by nature**: a listing is only meaningful while the server
  behind it is actively heartbeating (listings expire after `MaxAgeSeconds`, default
  90s, via `ExpirySweepService`). Durability buys nothing — there is no valuable
  long-lived record to protect.
- Correctness actually *favors* forgetting: if the Lobby process dies and restarts,
  the worst outcome is a brief gap before live servers re-register on their next beat.
  Persisting the directory would preserve **stale** listings, which are worse than none.
- Zero storage infra, zero migration surface, trivially containerizable.

The cost is the flip side: no cross-restart retention and no query/audit history. For a
server directory that was never worth keeping, that trade is the right one.

### The self-service token ownership model (Stage 1)

`POST /api/servers/register` returns `{ id, token }`. The `token` is the *ownership*
proof for that listing:

- A **heartbeat** that presents the existing token updates the same listing **in place**
  (exactly one entry, same `id`). An unmatched token is **rejected (409)** — the client
  drops it and re-registers fresh next tick (a rejected token means the server-side
  listing is gone).
- **`DELETE /api/servers/{id}?token=…`** requires the token; without it the DELETE is
  refused and the listing stays. The server's `Shutdown()` fires a best-effort
  deregister with the held token (swallowing failure so it never blocks shutdown).
- The public `ServerListingDto` deliberately omits the token (`ServerListing.cs`), so a
  directory query can never leak someone else's ownership credential.

Net effect: a self-hoster can create, refresh, and retire their **own** listing with no
accounts/auth infrastructure — exactly the "self-service" model the plan called for.
The cap (`MaxEntries`, default 384) evicts only *new* registrations, never heartbeats
to existing ids, so a crowded directory can't knock out live servers.

### `_statsLock` / `LivePlayerCount` — cross-thread-safety fix (Stage 2)

The heartbeat runs on its own timer thread, separate from the server's simulation
thread. Naively reading the sim-thread-owned `Ships`/`_peerShips` collections from the
timer thread would race (enumerating a mutable collection being mutated on another
thread).

Fix: a tiny, dedicated snapshot — `LivePlayerCount` is a field updated inside the
**same `_statsLock`** critical section that increments `_loopIterations++` each `SimPass`
(`Server.cs`, ~line 505). The heartbeat reads only `() => LivePlayerCount`; it never
touches `Ships`/`_peerShips` directly. This keeps the observable stat thread-safe with
zero contention on the hot sim path, and it's the **only** thing the heartbeat reads
from `Server`.

### Reverse-proxy `ForwardedHeaders` gotcha for rate limiting (flagged in code, restated as deployment guidance)

`RateLimiting.cs` partitions the register/heartbeat limiter by the request's **immediate
source IP** (`context.Connection.RemoteIpAddress`). Behind a reverse proxy (nginx/Caddy/
Cloudflare) **every request arrives from the proxy's IP**, so the partitioning collapses
into a single shared bucket — the rate limit becomes meaningless, and one abusive
self-hoster could throttle everyone (or vice-versa).

**Deployment guidance:** if you put the Lobby behind a reverse proxy, configure
`ForwardedHeaders` middleware to trust **only that proxy** so `RemoteIpAddress` carries
the real client IP. This is deliberately left out of `RateLimiting.cs` (it is deployment
configuration, not app policy); it is flagged as a code comment at the partition point.

---

## Operational gotchas

### The Artwork volume-mount requirement (this task — Stage 3)

The `.igc`/`.cvh` corpus is a **large, separate, external checkout**, deliberately not in
git and never needed to `dotnet build`/`publish` — only to *run* the server. The image
ships with no artwork; a real deployment must **bind-mount** it (e.g.
`-v /host/path/to/Artwork:/artwork:ro`) and the container's own config must set
`artPath` to the **in-container** mount point (`/artwork`). At boot `ValidatePaths()`
throws if `artPath` doesn't exist, so getting this wrong fails loudly rather than
silently. See `src/Allegiance.Server/Dockerfile` header and `docker-compose.yml`.

### The Server image is config-free by design

`Allegiance.Server` resolves `server.json` relative to its working directory
(`Program.cs`), overridable with `--config`. The image deliberately contains **no** config
file (`src/Allegiance.Server/server.json` is the maintainer's personal dev file). A real
deployment bind-mounts its own config (start from
`src/Allegiance.Server/server.lobby-example.json`, which adds a `"lobby"` block) at the
path `--config` points at. This mirrors P175's "don't bake in personal paths" rule.

### CI / corpus-testing limitation (found in this task — Stage 3)

CI (`dotnet build AllegianceCore.slnx`) is the hard gate and needs no artwork. `dotnet
test` however is **known to fail on a runner with no Artwork/Native-source trees**:
dozens of corpus-backed tests resolve them from `$ALLEGIANCE_ARTWORK_PATH` /
`$ALLEGIANCE_NATIVE_SRC_PATH` and, unset, fall back to `/home/domingo/...` which doesn't
exist on a GitHub-hosted runner — `Corpus.cs`/`CvhCorpus.cs` throw
`DirectoryNotFoundException`. There is no trait system to filter these cleanly, so the
test step is `continue-on-error` (it runs and surfaces all results; only build gates the
job). A proper fix — checking a small representative corpus subset into the repo, or a CI
step that fetches the real corpus — is a tracked follow-up (see
`ALLEGIANCE_LOBBY_STAGE3_P177.md`), not implemented here.

---

## Running both apps together locally

From the repo root:

```
docker compose up --build
```

- `lobby` — builds `src/Allegiance.Lobby/Dockerfile`; browse the directory at
  `http://localhost:8080`.
- `server` — builds `src/Allegiance.Server/Dockerfile`; listens on TCP `6073`, announces
  itself to the lobby via the heartbeat.

Startup order **doesn't matter**: the heartbeat fails soft (Stage 2) — if the lobby isn't
reachable on a beat, the server just retries next tick; it never crashes or blocks boot,
so there's no `depends_on` gating.

Before `up`: `docker/compose.server.json` is the server's config (bind-mounted in). Its
`artPath` is `/artwork`; provide the artwork via the `server` volume — either drop a small
subset (`PCore001.igc` + `XXV_simple.igc`) into `docker/artwork-placeholder/`, or repoint
that volume at a real Artwork checkout (see `docker/artwork-placeholder/README.txt`). The
Lobby itself needs no config file or external assets.

**HTTPS/TLS** is out of scope for both apps; the recommended way to add it in production
is a terminating reverse proxy (Caddy/nginx) in front of the Lobby.

### P655 directory integrity

Listings advertise the registering socket's source IP; the submitted `address`
is ignored. Heartbeats require both the ownership token and the same source IP.
Each source can hold four listings (`Lobby:MaxEntriesPerAddress`); registration
caps are atomic, and expired entries disappear from reads before the sweep.
The body limit is 16 KiB. All requests have a 120/minute source budget, and
registration/heartbeat additionally has its existing 30/minute budget.

Behind a proxy, configure `Lobby:KnownProxies` with the immediate proxy's IPs,
for example `{"Lobby":{"KnownProxies":["127.0.0.1"]}}`. Only those proxies may
supply one `X-Forwarded-For` hop. With no configured proxies, forwarded headers
are ignored. Do not add arbitrary networks to this list. Names remain public,
unverified labels; control and Unicode format characters are stripped.

### P894 — directory gates, hidden games and the richer listing

The 2026 parity pass on `docs/parity/lobby-matrix.csv` (LOB-0004/0005/0007/0008/
0009/0010/0017/0018/0020/0026/0027/0029/0035) ported the lobby behaviours that only
existed as advertisement before. Every rule cites the native source it ports.

- **Lobby protocol gate (LOB-0004).** A server states `lobbyProtocolVersion` in its
  register/heartbeat payload (`Allegiance.Core.LobbyProtocol.Version`). A stated
  version that differs from `Lobby:LobbyProtocolVersion` (default 1) is refused with
  native's `LOGON_SERVER_NACK` text (`lobby/server.cpp:93,141-151`). A pre-P894 host
  that omits the field is accepted, so existing community servers keep listing.
- **Approved-server list (LOB-0005).** `Lobby:AllowedServers` (empty by default) is
  native's `Allegiance.cfg [Lobby] NumberOfServers/Server1..N` list read by
  `IsServerAllowed` (`lobby/server.cpp:24-84`): loopback is always allowed (`:131`),
  entries match with native's `strncmp(ip, entry, strlen(ip))` rule (the entry starts
  with the caller's address — an exact address in practice), and
  `Lobby:ServerFilterMode` is `allow` (default) or `block`. A refused source gets
  native's reason with HTTP 403. Commas are not a separator: use
  `Lobby__AllowedServers__0`, `__1`, … in Compose.
- **Enforced minimum client version (LOB-0020/LOB-0026).** The listing still
  advertises `minClientVersion`, and the game server now *enforces* it at the logon
  the port's client actually performs: the client sends its installed release in the
  LOGONREQ's optional `P894` extension and an older (or silent) client is refused with
  a reason naming the required build and the updater, shown on the client's
  connect-failure screen. Set it with `minimumClientVersion` (top level) or
  `ALLEGIANCE_MINIMUM_CLIENT_VERSION`; the legacy `lobby.minimumClientVersion` still
  works and is the same value the heartbeat advertises. Empty = no minimum.
- **Paused servers are hidden (LOB-0007).** A heartbeat may set `paused: true`; a
  paused host is never advertised (native `FM_S_PAUSE` + "never advertize paused
  games", `lobby/server.cpp:266-272`, `lobby/mission.cpp:77-78`). `GET /api/servers/{id}`
  still answers for that id.
- **Zero-player games (LOB-0008).** Native advertises a mission only when
  `!paused && (nNumPlayers > 0 || fMSArena)` (`lobby/mission.cpp:77-86`), where
  `fMSArena` is a server-owned mission that exists without a player. The port's
  servers are exactly that (persistent, `objectModelCreated: true`), so they stay
  listed empty; a *client-created* game (`objectModelCreated: false`, P871's
  create-game relay) is hidden until its first player arrives. Hiding every empty
  server would empty the directory, which is the opposite of native's intent.
- **Richer listing (LOB-0009/0010/0017/0018/0027/0029/0035).** `location` (native
  `szLocation`), current/max game counts, the native percent load
  `(games*5 + players)/maxLoad` (`lobby/server.h:17,70-73`) and the lobby-normalized
  mission start time (`lobby/mission.cpp:61`) ride the listing; `GET /api/servers/cores`
  relays the master core list (native `BuildStaticCoreInfo`, `lobby/server.cpp:100-120`)
  and `GET /api/servers/stats` answers the native counters
  (`lobby/counters.h:4-26`, `lobby/lobbyapp.cpp:396-423`), including how many paused /
  empty missions the advertise rules hid. Server connect/disconnect/expiry are logged
  (`LE_ServerConnected`/`LE_ServerDisconnected`, `lobby/server.cpp:401-425`).
