# AllegianceCore Guide

The full guide. For a one-minute overview, see [`README.md`](../README.md) at
the repo root. This document assumes you have a clone of AllegianceCore; the
two-layer structure is: short `README.md` (front page) + this guide (the book)
+ [`DESIGN_NOTES.md`](DESIGN_NOTES.md) (the historical design log, kept
verbatim from the original README).

---

## 1. Prerequisites

- **.NET SDK 10.0.403** — CI pins this exact version
  (`.github/workflows/ci.yml`). Check with `dotnet --version`; if you see
  "Looks like you don't yet have a .NET SDK", your `PATH` doesn't point at the
  SDK you installed (Linux users: this project does **not** use the snap
  `dotnet` package, which is a broken stub — install from
  <https://dot.net> and point `DOTNET_ROOT`/`PATH` at it).
- **Original game art** — the `.igc`/`.cvh` asset corpus is **not** part of
  this repository (copyright/redistribution). Clone the community's
  FreeAllegiance art:
  ```bash
  git clone https://github.com/FreeAllegiance/Artwork ../Artwork
  ```
  The client, the server and the real-asset test suite all read from it.
- **(optional) Native C++ reference** — a clone of
  <https://github.com/FreeAllegiance/Allegiance> is used by a handful of
  native-parity tests and the two-tree corpus scan. Not needed to run the
  client or server.
- **(Windows)** Visual Studio 2026 (Community is fine) or the .NET SDK can
  open `AllegianceCore.slnx` directly. No source changes are needed to build
  on Windows; only paths differ (see section 2).

---

## 2. Path settings — every way to tell the code where the art is

Resolution order (see `docs/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md`):

1. **Environment variable** — `ALLEGIANCE_ARTWORK_PATH` and
   `ALLEGIANCE_NATIVE_SRC_PATH`.
2. **`local.env`** at the repo root — a gitignored file whose keys are the
   *same names* as the environment variables, so `source local.env` exports
   identically. Copy from `local.env.example` and edit:
   ```
   ALLEGIANCE_ARTWORK_PATH=/path/to/Artwork
   # ALLEGIANCE_NATIVE_SRC_PATH=/path/to/Allegiance
   ```
   `local.env` is read by `scripts/run-tests.sh` (the test runner) and by the
   products.
3. **Executable-relative defaults** — an installed client uses `artwork/` and
   `sky/` beside its executable, after environment / `local.env` overrides.
   These defaults never use the current working directory. Native source has
   no bundled default.
4. **Not found** — product code fails with a clear, actionable message; the
   corpus-backed tests **skip** (with a reason) instead of silently falling
   back to an owner's machine path.

**Server config `artPath`.** The server reads its art root from its JSON
config (`--config`, default `server.json`), which always wins over the env var
once loaded. See `src/Allegiance.Server/server.example.json` (generic),
`server.example.linux.json` or `server.example.windows.json` for OS-appropriate
samples. The Windows sample uses forward slashes (`C:/Users/you/Artwork`) to
avoid JSON backslash-escaping; .NET/Win32 accept both separators.

**Docker `artPath`.** The `docker-compose.yml` never bakes art into the image.
It bind-mounts `./docker/artwork-placeholder` read-only at `/artwork` and the
compose config's `artPath` (`/artwork`) is where the container sees it. Point
the volume at a real Artwork checkout (or drop the small
`PCore001.igc` + `XXV_simple.igc` subset into `./docker/artwork-placeholder`)
for a local trial.

---

## 3. Building and running

Everything runs from the repo root. Unless noted, the pattern is:

```bash
export DOTNET_ROOT="$HOME/.dotnet"      # your SDK install, not the snap stub
export PATH="$DOTNET_ROOT:$PATH"
```

### Build the whole solution

```bash
dotnet build AllegianceCore.slnx
```

`Directory.Build.props` sets `net10.0`, Nullable, ImplicitUsings,
`TreatWarningsAsErrors=true` and the latest language version — so warnings
fail the build; a clean solution prints `0 Warning(s) / 0 Error(s)`.

### Run the server

```bash
# copy a sample config, point artPath at your Artwork checkout
cp src/Allegiance.Server/server.example.json src/Allegiance.Server/server.json
# (edit server.json: artPath, port, the games[] list)

dotnet run --project src/Allegiance.Server -- --config src/Allegiance.Server/server.json
```

The default config boots `PCore001.igc` against a two-team map on port 6073 at
20 updates/sec. Useful flags: `--port`, `--artpath`, `--staticcore`,
`--ups`/`--updatespersec`, `--config`, and `--capture <file>` (writes every
sent/received packet for `tools/fedmsgdump`). Ctrl-C (SIGINT) or SIGTERM shuts
it down cleanly.

### Run the client (DesktopGL — Linux / Windows)

```bash
dotnet run --project src/Allegiance.ClientCore.MonoGame -- 127.0.0.1 6073 "My Pilot"
```

Args: `<host> <port> <player name> [flags...]`.

#### Useful client flags (checked against `Program.cs` / `LaunchOptions`)

| Flag | Effect |
|---|---|
| `--window WxH` | Fixed initial window size (e.g. `2560x1440`); default 1280×720. |
| `--borderless` | No window frame, placed at the screen's top-left — for full-height recordings (e.g. `--window 3840x2160` on a 2160-px screen). |
| `--fx native` / `--fx volumetric` / `--fx displaced` | VFX renderer: default **enhanced**; `native` forces the native P545/P263 pipeline; `volumetric`/`displaced` choose the volumetric technique (P573). |
| `--sky black` / `--sky native` / `--sky hd` | Sky tier: `black` (default, P588), `native`, or `hd`. `--sky-intensity F` sets global intensity (default 1.0); `--sky-lowres` picks the low-res asset set (Android). |
| `--aleph-glimpse on` / `--aleph-glimpse off` | Aleph destination-glimpse + transit flare (default **on**, P607). `--aleph-glimpse off` disables the glimpse/flare extras; the native arrival-star swell for visible transits stays on either way. |
| `--inspect` | Offline hull inspector; `--inspect-civ` / `--inspect-hull` preselect (P565). |
| `--ships native` / `--ships meshy` | Ship renderer (default native). |
| `--asteroids native` / `--asteroids imported` | Asteroid renderer (default native). |
| `--soft off` | Disable soft particles (default on in Enhanced). |
| `--bloom on` | Enable bloom (default off). |
| `--moons off` / `--distant-objects off` / `--galaxies off` / `--giant-objects off` | Toggle black-sky space objects (P589/P592). |
| `--debugMode` | Client-only: switch the camera FOV to native's real 50° default (vs this port's wider default). Not wired to the server's `debugMode` — set both by hand. |
| `--lobby <url>` | Point at an `Allegiance.Lobby` directory URL. |
| `--flightFov F` | Flight field-of-view override. |
| `--capture` | (Also a client capture path — see `Program.cs`.) |
| `--auto-*`, `--camera`, `--hide-hud`, `--autoshots`, `--autoshot-dir`, `--auto-dock-after`, `--dock-sweep`, `--exit-after` | P572 unattended live-proof automation flags (see `docs/LIVE_PROOF.md`). |

In flight, the camera keys include the P612 cinematic controls: **F8**
free-flight spectator camera (detached; WASD/QE move, mouse look, Shift/Ctrl
speed, starmap `,`/`.` to switch sector — admins get any sector), **F9**
orbit spectator camera (locks a ship/station; drag-orbit, wheel-zoom, PageUp
next subject), **F1** HUD toggle (clean frames), and **V** still cycles the
native views (cockpit → chase → …). Smooth camera transitions ease
eye/target/FOV over ~0.7 s on any framing change instead of cutting
(`ALLEGIANCE_INSTANT_CAMERA=1` restores the native cut).

### Run the Android head (experimental)

**Experimental and not complete**: there is no Android release download yet.
The Android head is **not** in `AllegianceCore.slnx` (so the DesktopGL build
and test runner never need the Android toolchain). It requires the Android
dotnet workload, a JDK and the Android SDK (see
`docs/reports/P499_ANDROID_HEAD_GAMEPAD.md`):

```bash
scripts/build-android-head.sh          # or scripts/build-android-head.sh Release
```

The signed debug APK lands in
`src/Allegiance.ClientCore.Android/bin/Debug/net10.0-android/org.allegiance.client-Signed.apk`.
Assets are staged by `scripts/pack-android-assets.sh` (run automatically).

### Run the lobby

The lobby is an ASP.NET Core HTTP directory (`src/Allegiance.Lobby/`) that
servers announce themselves to via an opt-in heartbeat. Run it:

```bash
dotnet run --project src/Allegiance.Lobby
```

It binds Kestrel's default port 5000 (or see the `Lobby` config section /
`--lobby` client flag). To have a server announce itself, copy
`src/Allegiance.Server/server.lobby-example.json` (same shape plus a `"lobby"`
block) and set `url`/`displayName`/`publicAddress`. With no `"lobby"` block a
server makes no HTTP calls at all (heartbeat is fully opt-in, and fails soft —
if the lobby is unreachable the server just retries, never crashes).

### Docker Compose (lobby + server in one command)

```bash
docker compose up --build
```

Browse the lobby at http://localhost:8080; the server announces itself to it
and listens for clients on TCP 6073. See `docker/README.md` for the three
compose configs: `compose.server.json` (production-safe default, no
debugMode), `compose.server.debugmode.json` (the 221 live-test config:
`m2xspiderw.igc`, 60 ups, debugMode on), and
`compose.server.debugmode.mapgen.json` (P402 generated-arena variant).

### Local server console

Type commands into the running server's stdin (the same console that accepts
`skills`). Commands execute on the next simulation tick. `help` prints the
syntax; malformed input prints one usage line without repeating the input.

| Command | Result |
|---|---|
| `players` | Current players across all games: ship/user ID, connection ID, name, game, team, sector, and state; includes players still choosing a side. |
| `boot <id>` | Remove that ship/user ID from its game with the native administrator boot reason; uses the moderator departure path. |
| `say <text>` | System chat to every player in every hosted game, including side selection. |
| `whisper <id> <text>` | System chat to that ship/user ID alone. |
| `override-tech <game-index> <side> <bit> on\|off` | Queue a starting technology override while the game is NotStarted. Game indices start at 0 in `status` order; sides are native slots 0–5 and bits 0–399. Applied at countdown; later calls are refused. |
| `status` | Live connection/player/mission counts, uptime, ticks, overruns, cumulative output/timeout counters, and each game's stage. |
| `chat [n]` | Show the newest `n` captured chat entries in chronological order (default 20; allowed 1–200). |
| `chat on` / `chat off` | Enable/disable live console chat and history capture; initially off. Previously captured history remains readable. |
| `alerts on` / `alerts off` | Enable/disable standalone admin alerts; initially on. A player's chat addressed to ADMIN triggers an alert. Chat capture can still retain/display ADMIN messages when alerts are off. |
| `skills` | Existing per-match skill telemetry. |

Startup admin parameters use the same masks, for example
`"techBitOverrides": [{"side": 0, "bit": 399, "setting": true}]`
inside a `games[]` entry. Unmentioned bits retain the civilization/path defaults.
Console changes are pending until countdown and survive that game's rotations;
`on`/`off` sets the latest value of an altered bit.

The history is an in-memory 200-entry ring shared across games, including
sender/target/message and UTC timestamp; it resets when the server boots.
History/status/roster commands send no client messages. Native message-box and
Event Viewer/shortcut actions become console lines, readable in the attached
terminal or existing server logs. Configured moderator, bot, harness and cheat
credentials are redacted, and control/format characters are removed before
printing names or chat. Credential fields and tickets are never listed.
The stdin backlog is bounded to 64 commands of at most 1024 characters, with
at most eight executed per tick; a full backlog rejects further submissions
without waiting. Long input produces usage. Output uses the existing bounded
nonblocking console logger (a stalled/full log sink can drop output).

For Docker, stdin must be attached **when the server container starts**.
`docker compose up -d` with the shipped configuration has no interactive stdin;
EOF only ends the input reader and the server continues running. Starting a
shell with `docker exec -it` does not connect that shell to the server's stdin.
For an interactive foreground server, stop the existing server first to free
its service ports, then run a replacement with attached stdin:

```bash
docker compose stop server
docker compose run --rm --service-ports -i server
```

Run this from a terminal (Compose allocates its TTY there). This changes how
you launch the existing service; no admin network listener or credential is
added. For a long-running detached container, set `stdin_open: true` and
`tty: true` on `server` in your local Compose override, recreate the container,
then use `docker attach --sig-proxy=false <container>`; detach with Ctrl-P,
Ctrl-Q. `docker logs` only reads output. Container/host access controls who can
reach this console.

### Bots and the MCP driver

- **Simple bot demo** — `dotnet run --project src/Allegiance.Bot -- 6073 DemoBot`
  (connect, get a ship, thrust/yaw, watch `SINGLE_SHIP_UPDATE`).
- **Combat demo** — `dotnet run --project tools/livecombat -- 0` (N independent
  1v1 lanes of real bots to a real kill).
- **`tools/allegiance-mcp`** — a Model-Context-Protocol server that scripts
  bot spawns and scenarios against a running server. It speaks JSON
  stdio to an MCP client; `scripts/mcp_driver.py` is a self-contained stdio
  driver (no machine-local dependencies). The P572 `scripts/live-proof.sh`
  uses it for `--bots 'spawn_scenario {...}'`.

---

## 4. Testing

### Full suite (the recommended way)

```bash
scripts/run-tests.sh
```

This runner builds once (in parallel) and then executes the four test
assemblies **sequentially**, so only one assembly's live-network tests tick
at a time. That avoids the cross-assembly live-socket contention that caused
flakes under a bare `dotnet test` (see `docs/P471_*` and `docs/TEST_FLAKES.md`).
It loads `local.env` for the art path. Exit 0 = every project passed.

The four test assemblies:
- `tests/Allegiance.Core.Tests` — wire codecs, payload decoders, corpus,
  simulation, bot flight, combat.
- `tests/Allegiance.Lobby.Tests` — the lobby HTTP directory.
- `tests/Allegiance.ClientCore.Tests` — the client network/game-state layer,
  live join/loadout/movement/chat/kill sessions.
- `tests/Allegiance.ClientCore.MonoGame.Tests` — headless units for the game
  client (flags, proof automata, layout).

### CI filter and test traits

CI (`.github/workflows/ci.yml`) runs the non-corpus gate:

```bash
dotnet test AllegianceCore.slnx --no-build --filter "Requires!=Artwork&Requires!=NativeSource"
```

Tests that need the real art or the native source are tagged
`[Trait("Requires", "Artwork")]` / `[Trait("Requires", "NativeSource")]` and
are **filtered out** on a GitHub-hosted runner (which has no corpus). Locally
they run and must pass. Live-network tests use
`[Collection("LiveNetwork")]` (`DisableParallelization = true`) to serialize
against the unit-test collections within an assembly.

### Flaky tests

`docs/TEST_FLAKES.md` is the single source of truth for flaky/failing tests.
Reports must cite it rather than free-text flake lists. Known load-flakes and
their status are ledged there — do not "fix" a ledgered flake without reading
it first.

---

## 5. Server configuration

Copy a sample and edit:

```bash
cp src/Allegiance.Server/server.example.json src/Allegiance.Server/server.json
```

Top-level fields: `artPath`, `staticCore`, `port`, `updatesPerSecond`,
`maxGames` (default 99, minimum 1; includes configured and player-created worlds),
`games[]` (name/map/teams/civIds/sideNames), optional `lobby` block, and
`liveSettings` (all test aids are **opt-in and off by default**):

- `debugMode` — compact P379 test topology (fast travel, auto-launching NPCs,
  initial miners). Off by default.
- `seenWarpRouting` — P923 discovery-aware server routing, **false by default**.
  Set `"liveSettings": { "seenWarpRouting": true }` and restart the server.
  Legacy NPC BFS admits only side-seen alephs or alephs in that ship's personal
  scanner view; discovery refreshes cached routes and cached misses. Native
  GotoPlan already checks visibility regardless of this flag; enabling it also
  refreshes that planner's cache on discovery. No unknown-route fallback is
  added. See [P923](reports/P923_REPORT.md) for no-route behavior and measurements.
- `testHarness` — economy grant by character-name prefix (port extension).
  Off by default.
- `testUnlock` — grant all required-tech + money (port extension). Off by
  default.
- `profiler` — per-tick profiler (P600), off by default.
- `timeScale` — **P612** admin-only sim time scale for cinematic slow motion.
  A float in **[0.1, 1.0]** (default 1.0 = normal). The server advances its
  sim clock by `period × scale`, so the whole world (physics, bots,
  broadcasts) runs in clean slow motion at `scale`× speed; connected clients
  stay in sync (their remote-ship dead-reckoning extrapolates in scaled sim
  time, so there is no rubber-banding). Admin-only: changeable at runtime
  only via the admin `timescale <value>` cheat, clamped to the same range.
  Printed at boot and in the tick-profiler header. See §7's cinematic mode.
- `adminCheats` — see below.

### Showcase match cap

`showcase.matchCapSeconds` defaults to **3600 simulation seconds** (60 minutes).
Use `0` to disable it; enabled values must be finite and between 60 and 86400.
It applies to `IsShowcasePreset` missions (research/goal-rotation showcase),
including every fresh match after game over. Offline skirmish sets it to 0
(`SkirmishOptions.CreateConfig`), so a human skirmish stays open-ended. Ordinary games,
LAN hosts and player-created lobby worlds retain their own countdown settings.

The cap sets native `MissionParams::dtGameLength` (`Igc/igc.h:1162,1526–1528`),
using the smaller nonzero length when `games[].gameLengthSeconds`, native
`goals.gameLengthSeconds`, or a goal-rotation entry already supplies a countdown.
The clock starts at STAGE_STARTED, so intermission and briefing do not spend it.
A late research finale is clamped to that end; an earlier finale keeps its own
deadline. Expiry uses native conquest/territory/prosperity/artifacts/flags, then
the optional `showcase.stationKillsDecideDeadline` criterion, then ship kills
and fewer deaths; an unresolved tie is a draw (`FedSrv/fsmission.cpp:2337–2504`;
station kills are the P914 preset extension).

Example: `"showcase": { "techMode": "research", "matchCapSeconds": 3600 }`.
`[matchup]` includes `cap=<effective seconds>` (`0` when disabled/inapplicable).
A capped countdown ending emits `[finale] cap winner side=<id|-1> by=<criterion|draw>
values=<scores> elapsed=<simulation seconds>` beside the existing deadline line;
`-1` is native `NA`. The research finale never needs to arm for the cap to work.

### Showcase map rotation

`showcase.mapRotation` is opt-in operator orchestration. Omit it (or set it to
`null`) to preserve the existing fixed `games[].map` / `games[].mapGen` assembly
and receipts. It applies to configured worlds at boot and after every game over;
player-created lobby worlds and the fixed-map client benchmarks retain their own
map. Example for two teams:

```json
{
  "showcase": {
    "mapRotation": {
      "mode": "cycle",
      "seed": 926,
      "entries": [
        { "map": "m2xspiderw.igc" },
        { "mapGen": { "type": "SingleRing" } },
        { "mapGen": { "type": "PinWheel", "seed": 123 } }
      ]
    }
  }
}
```

`entries` requires at least two objects, each containing exactly one file name
`map` (under `artPath`, no directories/whitespace) or a `mapGen` block with the
existing generator tunables. `cycle` uses list order and wraps; `random` draws
from an independent seeded stream, excluding the previous entry. Both modes use
`seed` to reproduce the sequence and fresh layout seeds. An omitted stream seed
is drawn once per configured game and logged. Unpinned generated entries get a
fresh seed each match; `mapGen.seed` pins that entry's layout, and `--map-seed N`
pins every generated entry. File entries also log a seed, used for runtime
asteroid top-ups when the authored map leaves a sector empty. Civilizations and
goals use their existing independent selection paths.

Boot parses every file and preflights every generated entry for every configured
game, including all rotated goals. Invalid topology/team combinations fail boot.
Generated types are SingleRing, DoubleRing, BigRing, HiLo, HiHigher, InsideOut,
LargeSplit, Star, PinWheel, DiamondRing, SnowFlake, Grid, EastWest, SplitBase and
Brawl. DiamondRing needs at least three teams; EastWest needs exactly two.
Resource counts must be 0–1000, `mapSize` 0 or 1, and `he3Density` finite in
0–100. Rotation requires `liveSettings.debugMode=false`. File-only rotation can
use `games[].homeSwap`; any generated entry requires `homeSwap=off` so generated
homes keep their own civilization rules. The current home-swap trial should
finish before enabling a mixed rotation.

To separate a side effect from a civilization effect on any map, including
generated ones, set `games[].civRotation` to `alternate` (default `off`). Match N
gives side s the civilization configured for side (s + N − 1) mod teams, so with
two teams the civilizations swap on even matches; homes stay with their sides.
The receipt gains `civRotation=alternate civShift=<n>`, and the `side0=`/`side1=`
fields show the civilizations actually played.

Both `[matchup]` and `[gameover]` include
`map=<file|gen:Type> mapSeed=N rotation=<mode>#<zero-based-entry-index>` plus
`rotationMatch` (one based) and `rotationSeed`. Keep the core version, tunables,
goals and civ receipt as well as these fields to replay the geography. Restart
with the same rotation seed and configuration to replay the stream; a generated
single-map replay can use `games[].mapGen` with the logged seed. These seeds do
not make combat, transport scheduling or treasure drops deterministic.
`tools/p819-side-bias-proof/matchups.py` adds `byMap` counts and winner-side tallies.

For MCP showcase march, a valid `target_sector` remains an override. When absent
or absent from the new map, the shared battle sector is a reachable neutral
sector ranked by smallest worst hop distance from every home, smallest distance
spread, smallest total hop distance, then sector id. If no neutral exists, rank
all sectors; maps without flagged homes (Brawl) use the smallest id. This picks
Alderan on m2xspiderw. Bots resolve names and the current graph again each match,
log `[battle-sector]` and clear old routes. After a derived target or changed map
activates adaptation, fresh mission generations reset scout intel; numeric overrides are remembered by
the resolved name. Normal per-role objectives (bombers, scouts, support and
mission goals) still apply. See [P926](reports/P926_REPORT.md) for coverage and
local proof receipts.

### Three-faction showcase

Set the actual game team count and give MCP an explicit `sides` list. Without
`sides`, the existing `count_a`/`count_b` and fleet arguments keep their two-side
plans, results and receipts. This is an operator opt-in; do not overlay it on a
running two-team trial. Example server configuration (merge with the existing
artwork, transport, broadcast and simulation settings):

```json
{
  "staticCore": "PCore001.igc",
  "games": [{
    "name": "Three factions", "teams": 3,
    "civIds": [18, 4, 5], "sideNames": ["Gigacorp", "Phoenix", "Omicron"],
    "maxPlayersPerTeam": 160, "homeSwap": "off",
    "showMap": false, "showHomeSector": true
  }],
  "showcase": {
    "techMode": "research", "stationKillsDecideDeadline": true,
    "mapRotation": {
      "mode": "cycle", "seed": 927,
      "entries": [
        { "mapGen": { "type": "DiamondRing" } },
        { "mapGen": { "type": "SingleRing" } }
      ]
    }
  },
  "liveSettings": { "autoNextMatch": { "enabled": true, "intermissionSeconds": 5 } }
}
```

MCP `spawn_scenario` arguments for a local 30-bot run:

```json
{
  "preset": "showcase",
  "sides": [{ "count": 10 }, { "count": 10 }, { "count": 10 }],
  "staging": true, "finale_side": "all"
}
```

`sides` has 2–6 entries in the mission roster order and must match its actual
team count. Each `count` is an integer 1–200. An optional `fleet` uses the usual
`[{"hull":"...","count":N,"tech":0}]` entries and its counts must sum to that
side's `count`. A side-level `tech` is a validated, ignored compatibility field;
all purchases still use real researched tech and dock availability. Do not mix
`sides` with legacy size/count/fleet/tech fields. Per-side results and worker
plans use `sides:[{side,requested,spawned,failed,refilled},...]`; two-side requests
also retain `sideA`/`sideB`. The internal owned driver accepts
`--skirmish 10/10/10`; the single-player chooser is unchanged.

`finale_side` accepts `all` (the N-side showcase default), `leader`, or an actual
integer side id 0–N−1. `both` remains the two-side alias. Every living faction
must meet the existing maturity gates before the finale triggers. A blind side
can arm its deadline with no strike target; only stations that side has seen
can become targets. For three or more sides, each new strike wave chooses the
nearest reachable living hostile faction from its home over public topology,
then observed player kills, observed station count and side id. Committed waves
keep their choice; elimination drops it. Scout threat intel retains every
living opponent. Unknown home ownership is not guessed from faction order;
scouts may recon a nearest anonymous home flag until they observe its owner.
The shared rendezvous retains P926's minimax rule over all home flags.

Deadline scoring already handles all teams: enabled native goal criteria first,
then the opted-in unique station-kill leader, then native ship kills and fewer
deaths; an unresolved tie is a draw. Each criterion considers all sides.
`[matchup]`/`[gameover]` name every side, and finale `values` includes every side
id. Tally with `python3 tools/p819-side-bias-proof/matchups.py --server server.log
--output tally.json`: `byPosition`, `byCiv`, `byAuthoredHome` and `byMap` cover all
factions. Three-plus-side `placementOrder` records the known winner;
`unrankedSides` explicitly leaves the other places unknown.

Every current generator except EastWest accepts three teams; DiamondRing
requires at least three. Keep homeSwap off. A suggested first rig pilot is
128/128/128 with `maxPlayersPerTeam:160`: 384 fleet clients plus four worker
probes and three research commanders is 391, below 512. Retain server GC,
immutableSnapshot/encodeOnce/parallelBuild, pool transport, native ship updates,
nativeGotoPlan and the existing clash tick budget. No rig settings are changed
by the MCP request. See [P927](reports/P927_REPORT.md) for local evidence and limits.

### Admin cheats — LAN-only, documented safety

`liveSettings.adminCheats` is an **off-by-default** port of native's
privileged cheat-code hook (`DoCheatCode`, `FedSrv.CPP:2814`). When enabled,
allow-listed character names may issue the `relocate` cheat on their **own**
ship (or an MCP-harness bot they launched) only. It is consumed as a chat
line and never broadcast.

P655 requires server-verifiable provenance for cheats, spectator visibility,
and test-harness grants. Names and prefixes are eligibility filters. When a
secret is configured, present it in `LOGONREQ.CDKey`; the server records the
authenticated connection id. An allow-listed
admin can also authenticate with a secret-prefixed command. Without a secret,
only actual loopback sockets qualify. Remote clients cannot gain privileges by
choosing a name.

For the MCP harness, set the same explicit secret in both server sections:

```json
{"liveSettings": {
  "adminCheats": {"enabled": true, "allowNames": ["mcp-*"], "secret": "YOUR_RANDOM_SECRET"},
  "testHarness": {"enabled": true, "characterNamePrefix": "mcp-", "secret": "YOUR_RANDOM_SECRET", "money": 1000000, "ignoreTechGate": true}
}}
```

Start `allegiance-mcp` with `ALLEGIANCE_PRIVILEGE_SECRET` set to that value.
MCP probes use unique `mcp-probe-w<worker>-<suffix>` names, which match the
`mcp-*` admin allow-list. Each probe asks the server to pick an eligible side;
`ALLEGIANCE_MCP_PROBE_SIDE_IDX` still forces a roster side for diagnostics.
Duplicate character names (case-insensitive within a mission, including pending
joins) receive a failed LOGONACK with `character name already in use in this mission`.
MCP startup reports logon and side-selection refusals directly.
Omitted `games[].maxPlayersPerTeam` and `maxTeamImbalance` retain their defaults
of 256 and 1 when loading JSON; authenticated harness bots bypass balance but
still obey capacity.

Its probe and bots present the token at logon; subsequent commands need no
prefix. Cross-peer relocation additionally requires an authenticated harness
target. The TCP transport is plaintext, so keep tokens on trusted networks.

**P612 — admin spectator visibility (LAN/demo-grade).** When `adminCheats` is
enabled, an **allow-listed** player's free-flight spectator camera is
**admin-unrestricted**: the server streams **all ships in the sector the
camera is viewing** (ignoring side visibility and cloak), and the free camera
may view **any** sector. The client names the viewed sector via the existing
`FM_C_VIEW_CLUSTER(104)` request; the server records it only for an admin
spectator and serves that sector's candidates to that peer with the cloak
gate relaxed. For everyone else the request is a **no-op** (native's
flying-ship gate), so a non-admin spectator sees only what its own side
already sees. P655 applies the same authenticated connection requirement to
this passive visibility privilege. See
[`SECURITY.md`](../SECURITY.md).

Committed example configs keep `adminCheats` **disabled** (the safe default).
The `server.p5xx.json` files are gitignored, per-machine debug harness configs
used for live tests; the `docker/compose.server.debugmode*.json` files are the
recorded LAN/live-test configs (the 221 host). Treat every config that enables
`debugMode`/`testHarness`/`testUnlock`/`adminCheats` as debug/LAN-only.

---

### Model modding

`tools/mdl-to-glb` is an offline authoring CLI. Build it with
`dotnet build tools/mdl-to-glb/mdl-to-glb.csproj`, then run these commands
from the repository root, using your configured artwork directory for `$ART`:

```bash
dotnet run --no-build --project tools/mdl-to-glb -- export "$ART" fig30 --out .scratch/models/edit
# Edit fig30.glb in a glTF editor; retain fig30.glbside.json next to it.
dotnet run --no-build --project tools/mdl-to-glb -- import .scratch/models/edit fig30 --out .scratch/models/native
dotnet run --no-build --project tools/mdl-to-glb -- optimize .scratch/models/native fig30 --out .scratch/models/optimized
dotnet run --no-build --project tools/mdl-to-glb -- inspect .scratch/models/optimized fig30
```

Export writes a `.glb` with embedded PNG textures plus a `.glbside.json`
sidecar for collision hulls, native frame vectors and lights. Each authored
LOD also travels in the GLB with its own geometry, UVs, normals, material
and pixel radius.
Import writes the hull `.mdl`, `.cvh` and each referenced texture's `.mdl`.
It also accepts new GLB art without a sidecar, generating collision hulls
and native frames from `HP_<Kind>_<Index>` nodes. Retain material names to
preserve texture stems. Use `--texture-stem NAME` to rename a texture or
`--no-texture` for an untextured import.

Optimize reads compiled binary MDL, strips material wrappers, folds shared
textures and groups, combines meshes, and reloads its result through the
game's MDL loader. It preserves all LOD geometry, lights and frame records
and copies the `.cvh` and available referenced texture files unchanged to
the output directory. Choose a separate output directory. `inspect` prints
mesh, triangle, vertex, mount, light, LOD and collision counts.

The game loads native `.mdl` hulls and textures and `.cvh` collision files
from the configured artwork source; it does not load GLB. To try your art,
copy the generated files into a separate artwork directory with the other
required assets and launch with `--assets` pointing there. Keep the model
stem referenced by the static core, or update the core's model name.

LOD meshes sit under `LOD_<index>_px_<radius>` parents in the same scene as
base meshes. The parent custom properties `allegianceLodIndex` and
`allegiancePixelRadius` are authoritative; the name is a fallback when an
editor drops custom properties. Indices preserve authored file order (start
at zero with no gaps); radii must be finite, positive and distinct. Edit the
base and each reduced level deliberately. To change a radius, update both
the property and parent name. To add a level, create its parent and child
meshes; to remove one, renumber the remaining parents and properties.

Blender 5.2.2 was checked with an actual import/re-export: names, hierarchy
and custom properties survive; `MSFT_lod` does not. All levels share one
scene because Blender rearranges separate scenes. Hide other levels while
editing, then unhide **all** geometry before exporting. Export GLB with
**Custom Properties**, UVs and normals enabled; disable **Selected Objects**
and **Visible Objects** filters so no level is omitted. Keep parent names,
properties and material names intact. Transforms on geometry nodes are baked
on import; indices must fit native's 16-bit range. No levels are generated
automatically. A GLB with no LOD parents remains legal single-level new art.
Older exports with sidecar radii but no level geometry import with a warning
naming the model and number of lost levels; re-export the original MDL to
recover them. Mesh counts cannot reliably detect stale levels: legitimate
coarse meshes often have different counts. Review all levels after editing.

Raw native frame and collision records in the sidecar remain authoritative; editing an
existing hardpoint also requires updating those records. Textual MDL
programs, direct `.x` import, native animation controls and `mdledit`'s
image/test-scene viewer are outside this workflow. External glTF editors
provide interactive model viewing and editing.

---

## 6. Project layout and architecture

```
src/
  Allegiance.Core/               the ported engine: static-core & map decoders,
                                 the simulation (ships, weapons, projectiles,
                                 economy, docking, mining, shields, NPCs),
                                 the FEDMSG wire codecs.
  Allegiance.Server/             the dedicated TCP server: boots a core,
                                 instantiates missions, ticks them, serves
                                 clients; the live `IIgcSite` seam.
  Allegiance.Bot/                a reusable bot client + console driver.
  Allegiance.Lobby/              the HTTP lobby directory (ASP.NET Core).
  Allegiance.ClientCore/         client network/game-state layer (entity table).
  Allegiance.ClientCore.Explorer/ interactive text client (binds an in-process
                                 server; fly/fight/loadout/dock/chat REPL).
  Allegiance.ClientCore.Game/    the MonoGame game logic & rendering.
  Allegiance.ClientCore.MonoGame/ the DesktopGL app host (entry point).
  Allegiance.ClientCore.Android/  the Android head (gamepad-driven).
tests/                           four xunit assemblies (see section 4).
tools/                           diagnostics & live-proof harnesses:
                                 igcdump/cvhdump/missiondump/fedmsgdump/
                                 corpusvalidate (static corpus + wire dump),
                                 livecombat (N 1v1 bot lanes), chatdemo,
                                 allegiance-mcp (MCP bot driver), and the
                                 many *-proof harnesses (visual proofs).
docs/                            design traces, per-subsystem docs, and the
                                 per-task reports (docs/reports/).
```

Architecture notes worth knowing:

- **Server-authoritative simulation.** Inbound `FM_C_SHIP_UPDATE` only sets a
  peer ship's controls/state; only the mission tick (`Mission.Update`) moves
  ships and fires weapons. `SINGLE_SHIP_UPDATE` is broadcast after the tick on
  the sim thread — never from a network reader.
- **Two clocks.** The simulation clock advances exactly one fixed period per
  tick (deterministic, monotonic); a `Stopwatch` cadence clock measures real
  cost to decide the sleep and to log `[overrun]`. The sim never clamps,
  skips or spirals.
- **The `IIgcSite` seam** is how the missions bind to the live host
  (eventing for kills, warps, payday, projectiles, collisions, captures, …).
- **Native-fidelity rule.** Every ported rule cites the original C++ source
  `file:line` in its doc comment. Layouts are verified by exact byte
  consumption: every record stream must land precisely on end-of-body
  (the acceptance property), so a wrong field width anywhere desynchronises
  everything after it.

---

## 7. Cross-checking against the native source

The port is written against `~/Development/Allegiance` (the FreeAllegiance
C++ tree) as the primary reference. Ported methods carry the native
`file:line` they came from (e.g. `ReceiveDamage`, `shipIGC.cpp:1212`; the
cheat hook, `FedSrv.CPP:2814`; cluster auto-launch, `clusterIGC.cpp:106-140`).
Where compiled-code evidence is needed (resolved constants, inlined macros,
vtable slots), symbol-aware decompiles of the FZDebug builds are available
via the Ghidra CLI (`~/Development/allegiance-tools/ghidra/ghidra-pdb.sh`).

The `.igc` corpus is the other oracle: 210 unique files, 99 static cores +
111 maps, every one consumed exactly. `tools/corpusvalidate` pins the corpus
aggregates as a regression baseline (see `docs/DESIGN_NOTES.md` and
`docs/model.md` / `docs/formats.md` / `docs/simulation.md` / `docs/wire.md`).

---

## 8. Features / status — what's ported and what isn't

This summarizes the current `Status`/`Implemented`/`Not implemented` sections
from the historical README (preserved in full in `docs/DESIGN_NOTES.md`).

**Done and live:**

- Static-core & map-file decoding — every shipped object type; `.cvh`
  collision-hull format.
- The FEDMSG wire — framing, CRC, logon handshake, and the game messages
  (logon, set-cluster, ship-update, single-ship-update, chat, kill-ship,
  mission-def, joined-mission, player-info, and the object-creation/exports:
  ship, cluster, warp, station, asteroid; buy-loadout-ack, payloads, dock,
  bucket, capture, chaff/mine/probe/chaff visuals, etc.).
- Simulation: flight model, combat (firing + damage + death + friendly fire),
  shields (absorption + regeneration + collision softening), economy, docking,
  mining, autonomous NPCs (auto-launch, initial miners, plantarget cycles),
  warps, payday, territory/capture, kill-ship scoring.
- Dedicated server + MonoGame client (DesktopGL + Android), lobby, bots, MCP.
- Showcase + session performance recorder (P789): every match records fps,
  frame-ms percentiles, time dilation and ships in view at O(1)/zero-alloc
  per frame; the game-over screen shows an additive performance panel with
  the Showcase score (`ships × clamp(fps 1% low/60) × clamp(tidi/100) × 10`,
  ranked only for the fixed seed-729 tune Showcase); `--benchmark` keeps the
  headless/CI path. See `docs/reports/P789_SHOWCASE_BENCHMARK_MERGE.md`.
- Chat — everyone/team/individual/all-sector/friendly-sector/leaders/wing.

**Intentionally not implemented (documented, not missing):**

- Runtime map objects that never appear in a shipped map (`OT_ship`,
  `OT_treasure`, `OT_mine`, `OT_probe`, `OT_buoy`, `OT_bucket`) — they carry
  zero records in the whole corpus, so a decoder would be an unfalsifiable
  guess.
- Native's own abandoned/deferred paths: `Allies` chat forwarding (commented
  out by native's team), `Admin` chat target (marked `//NYI` in native).
- Out-of-scope port machinery: COM/ATL/AGC, NT service, admin COM, Steam, SQL,
  single-instance mutex, public-lobby advertising.

The most current and detailed status lives in `docs/DESIGN_NOTES.md` and the
per-task reports under `docs/reports/`.

---

## 9. Troubleshooting

- **`dotnet --version` prints "Looks like you don't yet have a .NET SDK"** —
  your `PATH` isn't pointing at the SDK. Set `DOTNET_ROOT`/`PATH` (do not use
  the snap `dotnet` stub on Linux).
- **The client/server fail with an actionable message about the art path** —
  the corpus isn't at any configured location. Set `ALLEGIANCE_ARTWORK_PATH`
  or `local.env`, or the server config's `artPath`.
- **Tests skip with a reason** — they need `Requires=Artwork`/`Requires=NativeSource`
  data; point the env at the checkouts (see section 2).
- **A test that passes alone fails in the full suite** — that's the
  cross-assembly live-network contention class; run via
  `scripts/run-tests.sh` (sequential assemblies) and check `docs/TEST_FLAKES.md`.
- **The server prints a loud ADMIN CHEATS / DEBUG MODE warning** — it's
  enabled in the config; that's expected for a LAN test harness and a
  deliberate opt-in, but never run such a config on a public server.
- **Android head won't build** — the Android workload/JDK/SDK env isn't set
  up; see `docs/reports/P499_ANDROID_HEAD_GAMEPAD.md` §toolchain.

---

## Related docs

- [`../README.md`](../README.md) — the short front page.
- [`DESIGN_NOTES.md`](DESIGN_NOTES.md) — the historical design log (kept
  verbatim from the original README; the per-subsystem design traces).
- [`../CONTRIBUTING.md`](../CONTRIBUTING.md), [`../SECURITY.md`](../SECURITY.md),
  [`../CODE_OF_CONDUCT.md`](../CODE_OF_CONDUCT.md).
- `docs/formats.md`, `docs/model.md`, `docs/simulation.md`, `docs/wire.md`,
  `docs/FEDMSG.md`, `docs/BOT.md`, `docs/LIVE_PROOF.md`,
  `docs/TEST_FLAKES.md`, and `docs/reports/` for per-task detail.


## Player packaging and releases

Players can use the self-contained Linux x64 / Windows x64 archives from
[Releases](https://github.com/dominusbelial/AllegianceCore-public/releases).
Extract the whole archive, run `allegiance.sh` or `play.bat`, and edit
`player.json` under the per-user `AllegianceCore` configuration directory
(`$XDG_CONFIG_HOME` or `~/.config` on Linux; `%APPDATA%` on Windows). The same
directory holds `keymap.json`. An empty Server uses the in-client lobby;
configure Lobby or Server/Port and Pilot with the address from your operator.
Explicit client arguments override player.json. Linux still requires a working
OpenGL desktop and system audio libraries; see `README-PLAYERS.txt`.

Maintainers: `python3 packaging/package.py --rid linux-x64` (or `win-x64`)
fetches pinned art/sky and publishes the client with its runtime. Local checkout
and offline NuGet overrides, manifest regeneration, census/self-check commands,
archive size gates and Claude's tag → CI → verify checklist are documented in
[`packaging/README.md`](../packaging/README.md). The release workflow builds
both archives and checks an extracted Linux and Windows bundle before uploading
a release. Manual `workflow_dispatch` only builds artifacts. Codex leaves the
working tree for Claude's review and never tags, pushes or publishes.

## Host a LAN game

The main menu's **Host LAN game** runs the server inside your client and supplies
its bots. Choose a name, optional password and port; Small or Medium fleets are
available. Your friends join beside the bots using **Play LAN / Find Server** or
**Direct Connect**. The host's status bar shows addresses, game port and current
players (humans and bots). Guest password entry is **Tab** on either join screen.
See [the hosting quickstart](../QUICKSTART.md#host-friends-from-the-client) for
firewall ports, custom-port discovery, internet/Tailscale connections and ending
the session. Closing the host ends the game for everyone. Ordinary offline
skirmishes still accept no LAN guests.

## Join from a lobby web page

Install the client from [Releases](https://github.com/dominusbelial/AllegianceCore-public/releases),
then open your community's lobby web page and choose **Join** beside a server.
The browser opens an `allegiance://join?lobby=<encoded URL>&id=<listing id>&v=1` link.
The client looks up the current listing and shows the lobby host, server name,
verification status, dedicated/player-hosted status and player count. Review these,
use **Up/Down** to choose a mission when the server has several, and press **Enter**
to confirm. **Tab** edits the password; Enter finishes editing, then Enter confirms.
**Esc** cancels to the intro. A different lobby is identified explicitly and does
not replace the lobby saved in `player.json`. Unknown or expired listings and
unreachable lobbies show an error; Esc returns to the intro.

Linux portable bundles register the URL handler with `./install-desktop.sh` after
you place the bundle in its permanent folder. AppImage desktop integrations must
retain the bundled `MimeType=x-scheme-handler/allegiance;` and `Exec=… %u` fields;
select that desktop entry with `xdg-mime default <entry.desktop> x-scheme-handler/allegiance`.
The Windows setup registers it for the current user and removes it on uninstall;
portable Windows archives can open the same link as one quoted argument to
`Allegiance.exe`, `play.bat` or `play.ps1`. macOS app bundles declare the scheme
and handle SDL's URL Apple Event. Windows and macOS registration/runtime still
need platform verification; see [P944 report](reports/P944_REPORT.md).

Links support HTTPS lobbies, plus HTTP localhost/loopback for local development.
Version 1 is assumed when `v` is omitted; unknown versions/actions are refused.
The link carries a lobby and listing ID, never game connection parameters. The
client ignores extra query fields, suppresses all other launch switches on a
link launch, and asks before connecting. Dedicated and player-hosted listings
use the same connection and punch flow as the in-client browser. Opening a link
may start a second client; there is no forwarding to an existing process.

## Host for internet friends without router changes

Start the client with `--lobby https://lobby.example.org` (use your community's
actual lobby), then choose **Host internet game**. Choose a name, optional
password, game port and Small/Medium fleet, and start. The host joins as a normal
pilot; friends choose **Play Online** on that same lobby, select the
**[player hosted]** listing and join. In the browser, **Tab** edits the optional
game password; **Enter** finishes editing, then **Enter** joins. Classic and
Modern share this setup and join flow. The host status bar reports whether its
internet listing is available. Closing the host ends the game for everyone.

The lobby administrator must opt in with `Lobby:PlayerHostedGames=true`
(`ALLEGIANCE_LOBBY_PLAYER_HOSTED_GAMES=true` in Compose). The approved-server
filter still applies. Publish the lobby's two UDP STUN ports, default **3478 and
3479**, at the same public address as HTTPS signaling, preserving both port
numbers. `Lobby:StunPrimaryPort`, `Lobby:StunSecondaryPort` and
`Lobby:StunBindAddress` configure them; Compose equivalents and deployment
details are in [docker/README.md](../docker/README.md#internet-player-hosting).
The default setting is off and binds neither UDP observer. No new game-server
container or public STUN provider is required.

Allow the client's UDP traffic through the local firewall. Discovery and
connection probes use the actual game socket. Both peers need compatible UDP
mapping/filtering; STUN does not guarantee access through every router. If it
says **Direct connection not possible on this network**, forward the TCP/UDP
game port or use a Tailscale address through **Direct Connect**. There is no
relay fallback yet. A failed probe is bounded and never silently switches this
internet join to TCP. Player listings expire within 20 seconds without a fresh
heartbeat (or sooner if the administrator configures a shorter directory age); the host refreshes its mapping and listing every five seconds.

IPv4 is preferred. IPv6 direct access is supported when the lobby and both
peers use global unicast IPv6 and the HTTP/UDP observations have the same source
address. Use a global IPv6 lobby URL or a lobby hostname with only global AAAA
records for that path. Private/link-local IPv6 and mixed-family introductions
are refused; this version does not gather multiple candidates or bridge address
families. Two observers can detect a changed mapping, but a successful check
cannot classify every real NAT. The later two-network hardware check remains
necessary. Ordinary **Host LAN game**, dedicated servers, offline skirmishes,
and manual TCP connections retain their existing defaults.

### Civilization strategy experiment (P1000/P1001)

`bots.civStrategy` defaults off. `ALLEGIANCE_BOTS_CIV_STRATEGY` accepts `1/0`,
`true/false`, `on/off`; empty preserves JSON. Enabled servers choose a loaded-core
primary/counter portfolio using public opponent specifications and send its
runtime order through the existing research briefing. Authored plans are unchanged.
The commander uses up to four independent research jobs and two construction
jobs, repairs lost prerequisites, leases side-observed rocks, and keeps two
complete defensive fits plus two replacement miners in reserve. Combat decisions
continue while research or construction waits.

Fleet duties and legal purchases follow that profile, live GA and observed
opponent hulls/public attributes. Unknown enemy shields use an explicit core
preferred-fit assumption. Rescue, repair, mine-layer, boarding, gunner and finale
obligations retain their existing controls and hardware gates. Rotation rebuilds
policy from the new civ/settings while retaining the fleet's stable slots.

`bots.civStrategyCoverageShare` defaults to `0.15`, accepts `0..0.25`, and bounds
exploratory purchase slots and full-fit resource estimates. Required support and
finale duties take priority. Opening research earns a matching bounded coverage
allowance; blocked openings can switch to useful ready work. Once the opening
finishes, the remaining reachable research/station/drone tail proceeds.

`[civ-strategy]` retains choices, exact completion timestamps and bounded minute/
final windows for grants, damage, economy and stations. `[civ-coverage]` separates
worker selections from server-observed grants, launches, firing, hits and completed
mechanisms; an unexercised or unavailable mechanism is never reported as complete.
Coverage can allocate a turret carrier and gunner only when the bounded share
provides two crew slots; an explicit `ALLEGIANCE_GUNNERS=off` still disables them.
Ripcord coverage uses an observed friendly anchor while ordinary flight continues. The default-off path retains the existing
commander and purchase policy. See `docs/reports/CIV_STRATEGY_COMMANDER_P1001.md`
and `tools/p999-civ-strategy/receipts.py`.
