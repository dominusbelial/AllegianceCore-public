# AllegianceCore Guide

The full guide. For a one-minute overview, see [`README.md`](../README.md) at
the repo root. This document assumes you have a clone of AllegianceCore; the
two-layer structure is: short `README.md` (front page) + this guide (the book)
+ [`DESIGN_NOTES.md`](DESIGN_NOTES.md) (the historical design log, kept
verbatim from the original README).

---

## 1. Prerequisites

- **.NET SDK 10.0.400** — CI pins this exact version
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

### Run the Android head

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
