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

## Quick start

Prerequisites: the **.NET SDK 10.0.400** (CI pins this exact version) and a
clone of the **original game art**.

```bash
# 1. Set up the .NET SDK (adjust to your install)
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --version        # must print 10.0.400

# 2. Get the original art (not included in this repo)
git clone https://github.com/FreeAllegiance/Artwork ../Artwork

# 3. Copy the local config template (points at your art checkout)
cp local.env.example local.env      # then edit ALLEGIANCE_ARTWORK_PATH

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

The client and server need the art to render/run; `local.env` keys mirror the
`ALLEGIANCE_ARTWORK_PATH` / `ALLEGIANCE_NATIVE_SRC_PATH` environment variables
(`local.env` is gitignored — never commit machine paths).

## More

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
