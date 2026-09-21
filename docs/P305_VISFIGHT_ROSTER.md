# P305 — vizfight `roster`: one bot per distinct ship model + full-roster screenshots

Extends `tools/vizfight` with a fourth entry point, `roster`, that boots a real
9-faction server and spawns **one real Bot per distinct P287 ship model** (72
ships in PCore001), seats each on its faction's side, parks it in a
faction-grouped grid in one test sector, and **launches every bot through the
real `SendBuyLoadout(fLaunch:true)` wire path** — the same hangar-shop "Launch"
message a real player sends. The goal: a real in-game roster a single screenshot
can show, exercising the real end-to-end path
(wire → server hull validation → `HullModelResolver` → client render).

## What "one bot per distinct ship model" means

`RosterPlanner.Plan` (new, `Rendering/RosterPlanner.cs`, next to
`HullModelResolver`) folds `HullModelResolver.ReachableAssets` down to exactly
one entry per **distinct real model** — not the 447 hull-stat variants, most of
which share one of the 72 real meshes under different names. It returns:

- one `RosterEntry` per ReachableAssets asset (72 total), each paired with a
  **real witness hull** that resolves to that asset under its faction, plus a
  deterministic faction-grouped grid slot (column = faction, row = class);
- a `FactionOrder` mapping the 9 manifest civ keys to their real
  `CivilizationId` (side id == column index), so each side's civ is real.

## The roster server

`vizfight roster [webPort]`:

1. Plans 72 entries from the real PCore001 (bios 13, gt 16, ic 16, rix 12,
   belters/dreg/giga/oh/tf 3 each).
2. Boots `m2xspiderw.igc` with **9 teams** — one side per P287 faction, each
   side's `CivilizationId` set so the client's per-ship civ resolution is real.
3. Spawner seats the Nth connecting peer (one real `Bot` per entry) on the Nth
   entry's faction side at its grid slot with its real hull (ships are placed
   directly in sector 0; hull hot-swap isn't implemented server-side, so the
   spawn hull is the requested hull — the same pattern `showcase` uses).
4. Every bot sends `SendBuyLoadout(hullId, fLaunch:true)` over the wire; the
   server's `ApplyLoadout` validates the hull in the core, grants parts and
   replies `BUY_LOADOUT_ACK`.
5. Writes `roster_dump.json` (asset/civ/class/hull/ship-id/grid per bot) for the
   capture harness, and serves the spectator page over the WebSocket.

## Screenshots (the live proof)

Captured with a throwaway /tmp harness (`/tmp/p305verify`) reusing the **real**
`HullModelResolver` + `ShipRenderer.Draw` + compiled P287 content models — the
exact rendering path the in-game client uses — sourced from the live server's
`roster_dump.json`. Delivered as JPEG reference sheets in `docs/P305_roster/`:

- **`roster_all.jpg`** — all 72 distinct models in one image, grouped by faction
  (9 columns, headers like `BIOS (13)`, `GT (16)`), over a cell grid.
- **`roster_<civ>.jpg`** — one legible sheet per faction with a green
  `civ-class` label under every ship (e.g. `roster_gt.jpg` = 16 models in a 4×4
  grid, labels `gt-ass` … `gt-tt`).
- **`roster_net_bios_wire.jpg`** — the true networked end-to-end proof: a real
  `ConnectionSession` client joins the live roster server over the wire, seats
  on the Bios side, and renders the 13 Bios ships its client **actually
  receives as same-side teammates** (EXPORT/UPDATE) through the real resolver →
  renderer, labelled `bios-*`. The wire-received hull ids exactly match the
  launched roster bots (e.g. ship 8096 → hull 290 → `bios-bomber`), closing the
  loop wire → server hull validation → `HullModelResolver` → client render.
- **`roster_legend.md`** — the full asset → civ/class/hull/model/display mapping.
- **`roster_variety.txt`** — objective per-asset geometry (triangle counts,
  bounding radii) proving real model variety.

Each ship is drawn at a **normalized footprint** (uniform tile) — the standard
technical-reference-sheet convention — so a 300-unit carrier doesn't dwarf a
10-unit fighter; the real models are unchanged, only the per-tile scale is
flattened.

### Split rationale

The combined single frame genuinely can't read every ship legibly (72 low-poly
models in one 2000×1600 image is dense), so it is complemented by the per-faction
sheets with per-ship labels — a "small number if a single frame can't fit ~70
ships legibly" case, as the task allows. There is a second, platform-level reason
for per-faction presentation: a networked client only receives same-side
teammate ship exports (cross-side detection/visibility isn't wired — P131's
primitives are unwired), so a single in-game client cannot view all 9 factions'
ships at once. The `roster` server therefore hosts all 72, and the reference
render pulls the real spawned hull/civ/position from `roster_dump.json`.

### Verifying real variety (not all ships identical)

Untextured low-poly Meshy meshes defeat LLM vision (it reads them as
"terrain/rock"), so variety is verified objectively — the P242 lesson: distinct
**PNG byte sizes** (71/72 distinct in the single-render pass) and distinct
**model geometry** (41/72 distinct triangle counts; the boundings radii differ
widely, e.g. `ic-ejectpod` r=1.405 vs `giga-fighter` r=0.978). Per-faction sheets
render up to 16 clearly distinct labeled models.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
- Tests run twice:
  - `Allegiance.ClientCore.MonoGame.Tests`: **576 pass** both runs (incl. +5 new
    `RosterPlannerTests`).
  - `Allegiance.ClientCore.Tests`: **157 pass** both runs.
  - `Allegiance.Lobby.Tests`: **12 pass** both runs.
  - `Allegiance.Core.Tests` (3221 total): run 1 → 2 flake
    (`LiveTurretGunnerBindingTests` — the expected one — and
    `StationsUpdateLiveTests`); run 2 → 4 flake (adds `BotFlightTests
    Abrupt_disconnect`). These are the pre-existing live-wire timing flakes
    documented since P108-P114/P288 — **none touch the MonoGame renderer or the
    vizfight tool this task changes** (git diff confirms zero edits under
    `Allegiance.Core`/`Allegiance.Server`), so none are regressions.

## Out of scope (untouched)

- The tick-rate/bot-count stress matrix (separate P306 follow-up).
- The ~371 unmapped hull-stat variants' fallback path (later sanity check).
- Native source `~/Development/Allegiance` (read-only).
