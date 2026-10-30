# QUICKSTART — get the whole local stack running

P811 — a one-command local stack: **server + lobby + optional MCP bots +
optional client**, so a contributor (or a delegated agent) can stand up the
whole Allegiance ecosystem from one command instead of several ad-hoc steps.

## Prerequisites

- .NET 10 SDK (`~/.dotnet/dotnet`, pinned 10.0.400).
- The FreeAllegiance artwork corpus (a Git checkout). Point to it with
  `ALLEGIANCE_ARTWORK_PATH` in the gitignored `local.env`, or pass `--art`.
- Python 3 for the bot driver (`scripts/mcp_driver.py`).
- Docker + Compose only if you use the container path (`--docker`).

## One command: `scripts/dev-stack.sh up|down|status`

Run the stack as **local processes** (server + lobby + optional bots + optional
client). This is the recommended default — it uses the real Release binaries
and the real artwork, and a bot can actually join.

```bash
# Minimal stack: server (file map) + lobby
scripts/dev-stack.sh up

# A procedurally GENERATED map with a fixed seed (reproducible), plus 1 bot/side
scripts/dev-stack.sh up --map-gen Brawl --map-seed 812377 --bots 1

# Same, plus the MonoGame client
scripts/dev-stack.sh up --map-gen HiHigher --map-seed 90210 --bots 2 --client

# Check what is running / tear everything down
scripts/dev-stack.sh status
scripts/dev-stack.sh down
```

### Common flags

| Flag | Meaning |
|---|---|
| `--local` / `--docker` | Run server+lobby as processes (default) or as containers via `docker compose up --build -d` |
| `--port <p>` | Server port (default 6073) |
| `--map <map.igc>` | Server file map (default `m2xspiderw.igc`) |
| `--map-gen <TYPE>` | Use a procedurally generated map of this topology (`Brawl`, `HiHigher`, …) |
| `--map-seed <N>` | Fix the generated map's RNG seed — same seed ⇒ same map |
| `--bots <N>` | Spawn N bots per side through the MCP driver |
| `--client` | Also launch the MonoGame client |
| `--config <file>` | Use an existing server config instead of generating one |
| `--no-build` | Skip the Release build |

State (PIDs, logs) lives in `.scratch/dev-stack/` (git-excluded); `down` kills
exactly the PIDs `up` started — no broad `pkill -f` patterns.

### Docker mode

`--docker` reuses the existing `docker-compose.yml` (server + lobby images).
The compose path mounts `docker/artwork-placeholder` by default, so for a
playable map bind the real artwork corpus via a Compose override (see
`docker/README.md`, `compose.override.debugserver.yml`) or supply `--art` with
the override that mounts it.

### Reproducible generated maps

`--map-seed N` flows through to the server as `--map-seed N` (or
`MapGenConfig.seed`), seeds the map generator's RNG, and the server logs one
line per generated map, e.g.:

```
[boot] game 'Dev Stack' generating map: topology=Brawl seed=812377 (1 sectors / 0 warps / 2 stations / 23 asteroids)
```

The same seed rebuilds the exact same map (`tests/Allegiance.Core.Tests/MapGenSeedReproTests.cs`
proves same seed ⇒ same map). On a dedicated server you can pass it directly:

```bash
src/Allegiance.Server/bin/Release/net10.0/Allegiance.Server \
  --config server.json --map-seed 812377
```

## Testing

```bash
tools/build-tools.sh            # build every tools/ project (incl. proofs)
tools/run-tests.sh --tier fast  # Fast tier
python3 tools/check-glossary-links.py
```

See `README.md` and `docs/` for the wider picture, and `GLOSSARY.md` for a
kept-current per-domain index of the codebase.

### Multiplayer transport preference

The client Settings panel (Ctrl+F6, also Options → Graphics Options) saves an
Auto / TCP / UDP preference for either HUD style. Up/Down scrolls the rows;
Left/Right changes the selected row. Auto uses TCP for a discovered LAN with
both ends known wired, and UDP for wireless/unknown LAN links and remote sources
when a UDP port is advertised. Without an advertised UDP port it uses TCP.
Explicit TCP/UDP wins; `ALLEGIANCE_TRANSPORT_PROTOCOL` overrides Auto. The same
row shows the active transport after fallback. Changes apply to the next join;
local Skirmish/Training and default bots remain TCP. Server listener configuration
and discovery compatibility are documented in [UDP transport](docs/UDP_TRANSPORT.md).
