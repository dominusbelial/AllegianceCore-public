# AllegianceCore

![In-game screenshot (native FreeAllegiance art)](docs/images/hero-in-game.png)

**AllegianceCore** is a from-scratch C#/.NET 10 port of **Microsoft's
_Allegiance_** — the 2001 space-combat RTS/team shooter Microsoft released as
open source (MIT, July 2017). It reproduces the game's simulation, wire
protocol and data formats in modern C#, with no Windows/DirectX/COM machinery
to lean on.

Today it ships a **playable MonoGame client** (Windows, Linux, macOS Apple
silicon and Android), **offline single player** (Skirmish, the native training
missions, spectating), a **dedicated TCP server**, a **lobby**, **autonomous
bots** and an **MCP bot driver**. Everything is cross-checked against the
445k-line native C++ source (`src/`) for byte-exact fidelity.

## What works today

**Playing**

- **Single player, offline**: Skirmish against bot teams (both races, size,
  technology, difficulty, game speed Normal/Fast/Showcase), with an AI
  commander or **you** as commander. **Start as spectator** watches the whole
  battle (free camera, ship/station orbit, "hottest fight" follow) and lets you
  drop in. Pause with Ctrl+P.
- **Training**: the native training set runs offline on an embedded host:
  Basic Flight, Basic Weaponry, Enemy Engagement, Command View, Practice Arena,
  Nanite and the Free Flight dev mission. Mission 1's slideshow isn't ported yet.
- **Multiplayer**: lobby server discovery (sort/find), ready check and
  briefing, scoreboard and pilot info. After game over a scoreboard
  intermission runs, then the next match starts automatically.
- **Showcase**: the intro's Showcase entry launches big AI battles (Small
  5v5 up to Grand 200v200 at Showcase pacing) plus the fixed-seed settings
  tune (seed 729, 5v5, 150 s ×3) that suggests and applies graphics settings.
  Every match ends with a performance panel beside the game-over tables —
  fps/1 % low, frame-ms percentiles, time dilation, ships in view and the
  one-number Showcase score (ranked only for the fixed tune) — with an
  optional settings Apply. Sharing results is opt-in and anonymous.

**Client**

- Flies, fights, docks, mines, warps and ripcords against a real server.
  Cameras: cockpit, chase, turret, missile and external.
- **Native HUD**: the original's radar contacts and icons, brackets, target
  pane with a live 3-D model preview, own-ship gauges, equipment pane, flight
  indicators, console text, chat, warnings and damage flash. Fonts and anchors
  follow the native layout. A **Modern HUD** skin offers scaling (75–150 %) and
  a colour-blind safe palette.
- Command view, minimap and starmap, order/command icons, investment,
  loadout and teleport panes, quick chat, and native UI and simulation sounds.
- **Visuals**: volumetric explosions, bloom and flash lighting, an effects
  quality setting, and an optional HD sky pack. Native asteroid models, aleph exit wobble, the ripcord spark
  ramp.
- Keyboard, mouse, joystick and gamepad, all rebindable (F6). F1 shows every key.
- Crash-safe: a rolling log plus a crash log on every run, and missing
  optional art never takes the game down.

**Server and simulation**

- Boots a real core, runs missions and ticks the full simulation: economy and
  research, mining, construction (constructors build on real rock sizes),
  docking, shields, combat, missiles, chaff, mines, probes, cloak, friendly
  fire, ejection, lifepods and rescue probes. Also aleph warps with the native
  trigger cone, ripcord, and the finale/last-base endgame.
- Scales to **200v200**: the native prioritised ship-update budget, a pooled
  transport, parallel per-player update builds, and EVE-style **time
  dilation** under load. See *Recommended settings* below.
- **Wire protocol**: the FEDMSG framing and game messages are reproduced
  round-trip and byte-exact against the native reference. Port-only messages
  are being retired in favour of native ones.

**Bots**

- Headless bots fly real missions through `tools/allegiance-mcp`, an MCP
  server that spawns scenarios and full showcase matches.
- A research commander picks tech and issues attack and repair orders. Bots
  run strike waves on the softest enemy station with defend escorts, plus
  cloak, nanite repair support, chaff/probes/mines, ammo trips and
  medevac/rescue-probe drops. Skirmish uses the same bots.
- **How a bot match plays**: like chess, a match has three phases.
  - **Opening**: under native fog of war, scouts sweep the map sector by
    sector until they find the enemy base. Miners start the economy and the
    commander starts research.
  - **Middlegame**: the fleets stage and meet in the contested sector. Pilots
    eject, get rescued and relaunch in better ships as research unlocks them.
    Strike waves hit the softest enemy station.
  - **Endgame**: once research matures, the finale arms. Bombers refit, mass
    and strike the enemy home. If no base falls, the side with more kills at
    the deadline wins.

**Verified against native**

- Every ported rule cites the original C++ `file:line`. The corpus-backed
  suite parses 210 real `.igc` maps exactly. The fast test tier alone runs
  ~10,000 tests, and CI builds every tool project and the player bundles.

See **[`docs/GUIDE.md`](docs/GUIDE.md)** for the full guide: prerequisites,
getting the original art, building/running everything, client flags, testing,
architecture, and troubleshooting.

## Download and play

Download the Windows x64 setup `.exe`, Linux x64 AppImage, or a portable bundle from
[GitHub Releases](https://github.com/dominusbelial/AllegianceCore-public/releases).
Windows setup installs per-user by default; Linux users can make the AppImage
executable and run it. Portable `.tar.gz` (Linux) and `.zip` (Windows) downloads
remain available: extract the whole folder and run `allegiance.sh` or `play.bat`.
On macOS Apple silicon (macOS 14+), open the ZIP and drag the `.app` to Applications.
Every format includes .NET, native artwork and sky; no SDK, git or `local.env`
is needed. Start without arguments for the intro screen; Single Player → Skirmish
works offline. See the included `README-PLAYERS.txt` for installation, unsigned
SmartScreen/Gatekeeper steps and multiplayer configuration. F1 shows help; F6
rebinds keys. Verify downloads against the release's `SHA256SUMS.txt`.

The first public release, **v0.1.0**, is out. For local packaging and the release checklist, see
[`packaging/README.md`](packaging/README.md).

## Quick start

> P811 — the fastest way to stand up the whole ecosystem (server + lobby +
> optional bots + optional client) is `scripts/dev-stack.sh up|down|status`;
> see **[QUICKSTART.md](QUICKSTART.md)**. `GLOSSARY.md` is the kept-current
> per-domain index of the codebase.

Prerequisites: the **.NET SDK 10.0.400** (CI pins this exact version) and a
clone of the **original game art**.

```bash
# 1. Set up the .NET SDK (adjust to your install)
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --version        # must print 10.0.400

# 2. Get the original art (not included in this repo), plus the optional sky pack
git clone https://github.com/FreeAllegiance/Artwork ../Artwork
git clone https://github.com/dominusbelial/AllegianceCore-sky ../AllegianceCore-sky   # HD stars, nebulae, galaxies

# 3. Copy the local config template and point it at both (absolute paths;
#    UNCOMMENT the lines — remove the leading '#')
cp local.env.example local.env
#    ALLEGIANCE_ARTWORK_PATH=/absolute/path/to/Artwork
#    ALLEGIANCE_SKY_PACK_PATH=/absolute/path/to/AllegianceCore-sky

# 4. Build
dotnet build AllegianceCore.slnx

# 5. Create a server config and point artPath at your Artwork checkout
cp src/Allegiance.Server/server.example.json src/Allegiance.Server/server.json
#    edit server.json: "artPath" -> your Artwork path (and pick a games[] map)

# 6. Run the server
dotnet run --project src/Allegiance.Server -- --config src/Allegiance.Server/server.json

# 7. Run the client, pointing at the server
dotnet run --project src/Allegiance.ClientCore.MonoGame -- 127.0.0.1 6073 "My Pilot"
```

For **offline single player**, after the whole-solution build and artwork setup,
run the client without host/port arguments and choose **Single Player → Skirmish**.
The client owns the local server and bot driver. Select both races, size,
technology and difficulty; **Ctrl+P** pauses/resumes and **Ctrl+F10** ends the game.
A downloaded player bundle includes everything needed for this mode.

**What you should see:** the client logs on and opens the **READY CHECK** screen. Pick a
team, press **Y** to ready up — a game starts once every team with a player in it is ready,
so alone you start right away — and after a 15 s countdown you launch into space.
After game over, connected players enter the next match automatically: a default
30-second scoreboard intermission, then the normal 15-second briefing. See
[automatic next-match configuration](docs/NEXT_MATCH.md) to change or disable it.
Both timers follow simulation time under dilation.
**F1** shows every key; **F6** rebinds them. If the log says
`HD sky asset '…' unavailable`, the sky pack path isn't set (step 3).

The client and server need the art to render/run; `local.env` keys mirror the
`ALLEGIANCE_ARTWORK_PATH` / `ALLEGIANCE_SKY_PACK_PATH` / `ALLEGIANCE_NATIVE_SRC_PATH`
environment variables (`local.env` is gitignored — never commit machine paths). The sky
pack is optional: without it the client runs with a plainer sky and logs
`HD sky asset '…' unavailable` once per missing layer.

## Recommended settings (what we run for 200v200 tests)

This is the configuration we use for our 195v195 "march" battles (two full fleets
travelling the map and meeting at Alderan, with the economy running). It's tuned for
big battles on a LAN; a small game runs fine on the defaults.

Since P887 the performance baseline below **is the server default** — a fresh server
already runs it, so the 200v200 config only needs the per-IP connection limit and the
map (the ~390 bots all originate from ONE Mac bot host, and the default per-IP
ceiling of 64 would refuse them):

**Server** — add these to your `server.json` (use the `m2xspiderw.igc` map for the
showcase battles below):

```json
"transport":    { "maxConnections": 2048, "maxConnectionsPerIp": 1024 },
"liveSettings": { "timeDilation": "auto" }
```

…or set them per run with environment variables (same effect, no file edits):

```bash
ALLEGIANCE_TRANSPORT_MAX_CONNECTIONS=2048 \
ALLEGIANCE_TRANSPORT_MAX_PER_IP=1024 \
dotnet run -c Release --project src/Allegiance.Server -- --config src/Allegiance.Server/server.json
```

- `maxConnectionsPerIp` (default 64) is raised to 1024 for the big battles — the
  default is a sane limit for a public server but would refuse the rig. Leave it at
  64 for anything internet-facing.
- Server GC is on by default.
- `timeDilation: auto` (the default) is EVE-style slow motion under load: when one crowded
  sector makes ticks run long, the game slows slightly instead of stuttering. In our 200v200
  tests it kept every tick under 150 ms with no disconnects, at about 0.92× speed on
  average (0.73× at the heaviest moment). Set `"off"` to disable.

### Performance settings (the P887 baseline)

These are the server defaults that make up the measured performance baseline. Every
future change is A/B'd on the 200v200 rig **on top of this baseline**.

| Setting (default) | What it does | Rig evidence (batch → result) |
|---|---|---|
| `broadcast.immutableSnapshot` + `broadcast.encodeOnce` (on) | Capture owned wire data on the sim thread, encode each ship once per snapshot, publish off-thread. Payloads byte-identical. | b42 vs b39: clash-peak tick 44 ms vs 71–80 ms; tick overruns ~1k vs 22–40k |
| `broadcast.parallelBuild` (on) | Builds each player's updates in parallel (byte-identical output; ≤ cores−1 workers). | P603: byte-identical payloads; clamps to cores−1 |
| `broadcast.shipUpdateModel: "native"` (native) | Uses the original game's prioritised ship-update budget (native parity). | P604: native parity, ~1/5 the bandwidth of `port` at 400 players |
| `sim.nativeGotoPlan` (on) | Native NPC GotoPlan steering (native parity). | b52/b55: perf neutral (16.2 ms median tick), more station kills |
| `transport.mode: "pool"` (pool) | Serves all connections from a small send-worker pool (≈ core count) instead of one writer thread per peer. | P605: workers default ≈ core count |
| Server GC (`ServerGarbageCollection`) | Server-grade garbage collector for the dedicated server. | baseline b55: worst GC pause 36 ms at 394 connections |

P863 (b50 vs b49) — median tick 35.8 → 16.3 ms, TiDi windows 5.6 % → 0.6 %. Current
baseline (b55, 200v200): median tick 16.2 ms, TiDi window p95 ≈ 20 ms, 0 overrun
windows, worst GC pause 36 ms at 394 connections.

**Options that stay off, and why:**

- `broadcast.interestTiers` — per-viewer interest rates. Halves update age (0.22 s)
  for +40 % bandwidth and +6–18 ms peak tick (b39–b43); not worth it at the baseline.
- `sim.nativeBroadcastOrder` (SIM-0141, native parity) — +7 ms/tick median on b51;
  re-test after P863b; adopt if < ~2 ms.
- `receive.decouple` — off; local A/B only (P673), never A/B'd on the rig.

Inbound decoding can also run on the existing receive workers with
`"receive": { "decouple": true }` or `ALLEGIANCE_RECEIVE_DECOUPLE=1` (default off;
the environment overrides config). Commands still apply at tick boundaries in
peer-id/arrival order, with bounded fair carry-over. For diagnosis, add
`"measure": true` to that section: `[inbound]` reports each tick's message
counts/bytes by fmid and peer, and `[inbound-window]` retains a 100-tick histogram
including drain time. These detailed logs are opt-in. See the
[P673 report](docs/reports/P673_RECEIVE_DECOUPLE.md) for local A/B evidence.

**Private games (by invitation, P854)** — a host can restrict a game to an
invitation list by adding per-side lists to that game's entry in `server.json`:

```json
"games": [
  { "name": "Members Only", "map": "XXV_simple.igc", "teams": 2, "civIds": [1, 3],
    "sideNames": ["Alpha", "Bravo"],
    "sideInvitations": { "0": ["PilotA", "PilotB"], "1": ["PilotC"] } }
]
```

`sideInvitations` maps a side index to the list of players allowed to join it
(matches native `MissionParams::nInvitationListID` + the per-row
`(iTeam, szSubject)` SQL invitation lists; the port loads them from config at
mission assembly instead of SQL). In a **squad game** (`"squadGame": true`) each
entry is a **squad name** matched against the player's active squads; otherwise
each entry is a player **callsign**, matched case-insensitively — exactly native
`CFSSide::IsInvited` (fsside.cpp:34-64). A player invited to no side is refused
at mission entry (`DPR_PrivateGame`); a player invited to one side cannot join
another. Empty/absent = a fully public game (the default).

**Client** — always a Release build; join *before* bots spawn so you see the whole battle:

```bash
dotnet run -c Release --project src/Allegiance.ClientCore.MonoGame -- <server-ip> 6073 "My Pilot" --window 2560x1440
```

In game: **F1** shows the live key bindings, **F6** opens Controls to rebind keys.

**Bots (optional)** — `tools/allegiance-mcp` is an MCP server that drives real bot
clients. A 200v200 is one tool call:

```json
{ "tool": "spawn_scenario",
  "args": { "preset": "showcase", "target_sector": "Alderan", "size_scale": 13, "mode": "march" } }
```

`size_scale` 13 ≈ 195 per side; `mode: "march"` (the only battle start since P795
removed the arena mode) launches each side at home and sends wings through different
alephs to converge on Alderan. ~390 bots are a heavy load: run the bot tool on its own wired machine,
not on the server or the machine you play on.

## Performance: the 200v200 baseline

Compare session scores with the [Allegiance Index (AIX)](docs/PERFORMANCE.md#allegiance-index).

**Target.** Two full teams of ~195 players each (≈ 394 connections) playing complete matches unattended: economy,
research, scouting, strike waves, finale, game over, then the next match. The server ticks at 20 Hz, a 50 ms budget per
tick; a busy window passes at p95 ≤ 40 ms and p99 ≤ 50 ms.

**How we measure.** A mini PC (Ryzen 9 6900HX, Native AOT server in Docker) runs the server. 390 bots run on a separate
machine over wired Ethernet. Each change is an A/B batch of 2–3 full matches, never cut mid-match. The server's 5-second
`[tickprof]`/`[gcprof]` windows give tick time, GC pauses, heap and time dilation. We also check that the battle stayed
real (kills, station kills, research, strikes, disconnects). New ideas ship behind a default-off flag and are kept only
if the A/B improves without changing native behaviour.

**How it started vs. now:**

| | Start (Oct 9–12, 2026) | Now (rig batch b55) |
|---|---|---|
| Load | ~94 players already used 93 % of one core; 301 players ran at 60 % speed | 394 connections at full speed, 0 windows behind |
| Tick time | p95 1.2–1.8 s at 392 players (GC storms) | median 16.2 ms, window p95 ≈ 20 ms |
| Worst GC pause | multi-second stalls | 36 ms |

The steps in between: parallel per-player updates, a send-worker pool, the original game's ship-update budget, server
GC, time dilation, an immutable encode-once broadcast snapshot, and the big-fight tick budget. These settings are now the
**baseline**: every later change is measured against them. The full story, numbers per step, and the options we tested
and left off are in [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## More

- **[Sky pack](https://github.com/dominusbelial/AllegianceCore-sky)** — optional HD sky art (star field,
  nebulae, deep-field galaxies, distant and giant sky objects; NASA/ESA/Hubble/Webb imagery,
  credited). Clone it and set `ALLEGIANCE_SKY_PACK_PATH` (see Quick start).
- **[Full guide](docs/GUIDE.md)** — everything: art paths, Docker, the Android
  head, client flags, testing, architecture, troubleshooting.
- **[Contributing](CONTRIBUTING.md)** — build/test steps, the native-reference
  rule, coding conventions, PR checklist.
- **[Security](SECURITY.md)** — reporting and the server's known non-hardened
  areas (it is a LAN/demo-grade server).
- **[Licence](LICENSE)** — MIT. Original-game logic derived from Microsoft's
  Allegiance (MIT); see **[`NOTICE`](NOTICE)** for the required attribution.
- **[Credits](CREDITS.md)** — third-party imagery and effect art attributions.

Optional art packs (e.g. alternative ship models) are **not** included in this
repository; see the full guide for how to add one.
