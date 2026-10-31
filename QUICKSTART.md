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

### Host friends from the client

Choose **Host LAN game** on the main menu (Classic or Modern). Type a game name,
choose a TCP/UDP game port (default **6073**) and optionally a password, then start.
The host plays normally; the top status line shows this PC's IPv4 addresses,
port, and live player count (humans plus bots). Choose the address on the same
network as your friends; a Tailscale interface address is also usable.

Friends choose **Play LAN / Find Server** or **Direct Connect** and type
`address:port`. On either screen, **Tab** switches to the optional password field;
Tab again returns to the list/address. Passwords are masked and are not saved in
player preferences. The LAN password is an access gate, not encrypted transport.
Bots keep their seats; friends join beside them. Team capacity is 11 for Small or 21 for Medium, including humans, bots and helper pilots. Choose the smaller team; native balance permits two extra
low-rank players. AI commanders handle research. Hosts use research tech at normal
speed; pause, unlocked tech, offline resupply and benchmark overrides are unavailable.

Allow **TCP and UDP 6073**, plus **UDP 6074** for discovery, on the host's firewall.
For another game port, discovery uses that port plus one. Standard automatic LAN
scanning uses the default discovery port; with a custom port, friends can use
Direct Connect or add a favorite for that endpoint. A blocked firewall commonly
means no LAN listing or a connection timeout. Windows may show a firewall prompt:
allow the game on your private network. On Linux allow these ports in your active
firewall; on macOS allow incoming connections for the app if prompted. Nothing
opens the firewall automatically.

Internet friends need TCP/UDP port forwarding to the host, or a reachable
Tailscale IPv4 address in Direct Connect. No NAT traversal, relay, internet lobby
listing, or host migration is provided. **Ctrl+F10**, **End LAN game for everyone**,
leaving from the hangar/game-over screen, or closing the app ends the host and
notifies guests. A dropped guest can join again through the normal server path;
a grace/resume window is only available when configured as on a dedicated server.
