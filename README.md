# AllegianceCore

![In-game screenshot (native FreeAllegiance art)](docs/images/hero-in-game.png)

**AllegianceCore** is a from-scratch C#/.NET 10 port of **Microsoft's
_Allegiance_** — the 2001 space-combat RTS/team shooter Microsoft released as
open source (MIT, July 2017). It reproduces the game's simulation, wire
protocol and data formats in modern C#, with no Windows/DirectX/COM machinery
to lean on.

Today it ships a **playable MonoGame client** (Windows / Linux DesktopGL and
Android), a **dedicated TCP server**, a **lobby**, **autonomous bots** and an
**MCP bot driver** — a complete headless server + real client, cross-checked
against the 445k-line native C++ source (`src/`) for byte-exact fidelity.

## What works today

- **MonoGame client** — flies, fights, docks, mines and warps against a real
  server; cockpit / chase / turret cameras, HUD, radar, minimap/starmap,
  target-picking and joystick input.
- **Dedicated server** — boots a real core, instantiates missions, ticks the
  simulation, and serves TCP logon clients; economy, docking, mining, shields,
  combat, friendly fire, dying/death and autonomous NPCs.
- **Wire protocol** — the FEDMSG framing and game messages (logon, chat, ship
  updates, kill ship, loadout, exports…) reproduced round-trip- and
  byte-exact against the native reference.
- **Lobby** — HTTP heartbeat registration so clients can discover running
  servers.
- **Bots + MCP** — headless bots fly real missions; `tools/allegiance-mcp`
  scripts spawns, scenarios and arenas.
- **Verified against native** — every ported rule cites the original C++
  `file:line`; the corpus-backed suite parses 210 real `.igc` maps exactly.

See **[`docs/GUIDE.md`](docs/GUIDE.md)** for the full guide: prerequisites,
getting the original art, building/running everything, client flags, testing,
architecture, and troubleshooting.

## Download and play

Download the Linux x64 `.tar.gz` or Windows x64 `.zip` from
[GitHub Releases](https://github.com/dominusbelial/AllegianceCore-public/releases).
Extract the whole folder and run `allegiance.sh` (Linux) or `play.bat` (Windows).
The bundles include .NET, native artwork and sky; no SDK, git or `local.env`
is needed. See the included `README-PLAYERS.txt` to configure your operator's
server address and pilot name. F1 shows help; F6 rebinds keys. Verify downloads
against the release's `SHA256SUMS.txt`. Windows builds are unsigned.

Release bundles become available after the first reviewed release workflow run.
For local packaging and the release checklist, see
[`packaging/README.md`](packaging/README.md).

## Quick start

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

**What you should see:** the client logs on and opens the **READY CHECK** screen. Pick a
team, press **Y** to ready up — a game starts once every team with a player in it is ready,
so alone you start right away — and after a 15 s countdown you launch into space.
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

**Server** — add these to your `server.json` (use the `m2xspiderw.igc` map for the
showcase battles below):

```json
"broadcast":    { "parallelBuild": true, "shipUpdateModel": "native" },
"transport":    { "mode": "pool" },
"liveSettings": { "timeDilation": "off" }
```

…or set them per run with environment variables (same effect, no file edits):

```bash
ALLEGIANCE_BROADCAST_PARALLEL_BUILD=1 \
ALLEGIANCE_SHIP_UPDATE_MODEL=native \
ALLEGIANCE_TRANSPORT_MODE=pool \
dotnet run -c Release --project src/Allegiance.Server -- --config src/Allegiance.Server/server.json
```

- `parallelBuild` builds each player's updates in parallel (byte-identical output).
- `shipUpdateModel: native` uses the original game's prioritised ship-update budget —
  roughly a fifth of the bandwidth of `port` at 400 players, and what keeps bot hosts
  responsive in big battles.
- `transport: pool` serves all connections from a small worker pool.
- Server GC is on by default. `timeDilation: auto` (EVE-style slow motion under load) is
  available but off while we tune it.

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

`size_scale` 13 ≈ 195 per side; `mode: "march"` launches each side at home and sends
wings through different alephs, `"arena"` spawns both fleets directly in Alderan for an
instant clash. ~390 bots are a heavy load: run the bot tool on its own wired machine,
not on the server or the machine you play on.

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
