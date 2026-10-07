# LIVE_PROOF.md — unattended real-window visual proofs (P572)

The visual-proof rule requires screenshots from the **real client window**
(the presented backbuffer), not `--capture`/offscreen renders, because offscreen
proofs hid real-window regressions (P547 all-geometry-invisible, P562 aleph
funnel black, P566 native ships inside-out). P572 makes that reproducible in one
command: a local server + optional MCP combat bots + a WINDOWED client that
seats, readies, launches and photographs ITSELF via `--auto-*` flags.

Everything is off by default and client-only. A client launched with no
`--auto-*` flag behaves exactly as before.

---

## The flags (normal windowed client)

All client-only launch flags; nothing is sent on the wire beyond what a player
sends.

| Flag | Meaning |
|---|---|
| `--auto-side <id>` | Pick this side on the side-select screen (falls back to the server's 30 s auto-assign if the roster is late). |
| `--auto-ready` | Send PLAYER_READY(true) automatically once seated. |
| `--auto-launch [hull]` | In the hangar, select the hull (by id or name; empty = keep the current hull) and send BUY_LOADOUT(fLaunch=true). Applies the default/current loadout (P571 default loadout when landed, else the current parts). |
| `--auto-goto <sector>` | After launch, engage the P541 starmap goto to a sector (name or id), routed through warps. |
| `--auto-target <kind>` | Frame the nearest `enemy`/`friendly`/`station`/`aleph` (reuses the client's real T/G targeting filters) and autopilot toward it. |
| `--camera chase\|cockpit` | Initial camera mode (default cockpit). |
| `--hide-hud` | Suppress the HUD overlay for a clean geometry frame. |
| `--autoshots <t1,t2,...>` | Real-backbuffer screenshots at those seconds after launch. |
| `--autoshot-dir <dir>` | Output directory for the autoshots (default `screenshots/`). |
| `--auto-dock-after <s>` | After launch, autopilot to the home station and dock (the server's real dock collision docks you). |
| `--dock-sweep <N>` | While docked, step through N hull previews in the shop, photographing each (used with `--auto-dock-after`). |
| `--exit-after <s>` | Exit the client that many seconds after launch. |

Autoshot PNGs are named `proof_<t>|<sweepNN>_s<SECTOR>_c<camera>.png` and are
written as **valid** PNGs via `SaveAsPng` (not the P566 bad-CRC encoder), so
they open in any normal viewer. The filename encodes the time (seconds since
launch), the current sector name, and the camera mode.

---

## One command: `scripts/live-proof.sh`

Boots a local `Allegiance.Server` (221 battle map `m2xspiderw.igc`, testUnlock,
optional adminCheats), optionally spawns MCP combat bots, launches the windowed
client with the automation flags, waits, collects the PNGs and shuts everything
down by exact PID. Prints the absolute path of every PNG it produced.

```
scripts/live-proof.sh \
  --autoshots 8,12,20 --exit-after 25 --chase --hide-hud --out /tmp/proof
```

Options: `--out --port --map --art --auto-side --autoshots --exit-after
--window --name --chase --hide-hud --bots --no-admin-cheats --server-config
--no-window`. The client window defaults to 2560×1440; its backbuffer (and thus
the PNG) is always the full requested size regardless of screen clipping.

---

## Recipes

### 1. Own ship in chase view (native ships)
```
scripts/live-proof.sh --chase --hide-hud --autoshots 10,15 --exit-after 20 \
  --out ~/proof/chase
```
`--ships native` is the client default (renders the original `.mdl` ships), so
no flag is needed here.

### 2. A dogfight with MCP bots (5v5)
```
scripts/live-proof.sh --chase --hide-hud \
  --bots 'spawn_scenario {"size":5,"target_sector":"Alderan"}' \
  --autoshots 12,16,20 --exit-after 26 --out ~/proof/dogfight
```
The MCP driver stays alive (holding the bots) for the whole client run and
despawns them at the end.

### 3. The Rigel aleph at 3 distances
Fly to the aleph sector, then capture as you close in. Pass client-only flags
through `--client-extra`:
```
scripts/live-proof.sh --chase --hide-hud \
  --client-extra '--auto-goto Rigel --auto-target aleph' \
  --autoshots 40,52,64,76,86 --exit-after 90 --out ~/proof/aleph
```
`--auto-goto Rigel` engages the P541 starmap goto (warp-routed) and
`--auto-target aleph` flies the client at the nearest warp funnel, so the later
frames catch the funnel at close range.

### 4. The home station
```
scripts/live-proof.sh --chase --hide-hud \
  --client-extra '--auto-goto Ganymede --auto-target station' \
  --autoshots 14,20 --exit-after 24 --out ~/proof/station
```

### 5. Hangar preview sweep across 6 hulls
```
scripts/live-proof.sh --chase \
  --client-extra '--auto-dock-after 14 --dock-sweep 6' \
  --exit-after 70 --out ~/proof/hangar
```
After 14 s of flight the client heads home, docks, and photographs 6 hull
previews (`proof_sweep00..05_...`). Give the exit deadline enough margin
(~14 s + travel + 6×interval) for all six.

### 6. Soft particles on/off A/B
Soft particles are a client launch flag (`--soft off|on`, default on in
Enhanced). Boot the server + bots once with `--no-window`, then run the client
twice directly against it:
```
scripts/live-proof.sh --no-window --out /tmp/proof-srv        # server only
src/.../Allegiance.ClientCore.MonoGame 127.0.0.1 6073 proof \
  --chase --autoshots 10 --autoshot-dir /tmp/soft-on --exit-after 14
src/.../Allegiance.ClientCore.MonoGame 127.0.0.1 6073 proof \
  --chase --soft off --autoshots 10 --autoshot-dir /tmp/soft-off --exit-after 14
```

### 7. `--ships native|meshy`
Both renderers are a client launch flag; live-proof defaults to native. To A/B
Meshy, run the client directly:
```
src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Allegiance.ClientCore.MonoGame \
  127.0.0.1 6073 proof --ships meshy --chase --autoshots 10 \
  --autoshot-dir /tmp/meshy --exit-after 14
```
against a server already booted by `scripts/live-proof.sh --no-window`.

---

## Notes

- Screenshots are real presented backbuffer frames (`GetBackBufferData` before
  `Present`, the same moment `ALLEGIANCE_AUTOSHOT` and F11 use), written as
  valid PNGs via `SaveAsPng`. `--capture`/offscreen renders do NOT satisfy the
  visual-proof rule.
- `live-proof.sh` cleans up by exact PID on exit (server, MCP driver, client),
  so it is safe to run unattended.
- The pure flag-parser and the side→ready→launch→goto→shots→dock-sweep→exit
  sequencing are unit-tested headlessly in
  `tests/Allegiance.ClientCore.MonoGame.Tests/P572AutoProofTests.cs`.
