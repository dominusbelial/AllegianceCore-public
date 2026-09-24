# P360 — Persistent Mini-Starmap (open/closed states), not just a full-screen toggle

Date: 2026-09-23 · Branch: `master` (committed locally, NOT pushed — Domingo pushes)

## What native's real open/closed behaviour turned out to be (confirming/correcting the brief)

Reading the full `SectorInfoPane` class in `~/Development/Allegiance/src/WinTrek/sectormap.cpp`
(the brief quoted lines 140-168; the open/closed toggle is lines 139-538) **corrects the
brief's framing in one important way**: native does NOT have a single widget that is the
whole dual-mode map. It has TWO separate classes created together by
`SectorMapPaneFactory::Apply` (sectormap.cpp:1629-1637):

- **`SectorInfoPane`** (sectormap.cpp:139-538) — the widget with the literal "open/closed"
  view. It loads THREE background images: `consectorinfobmp` (small/closed),
  `conesectorinfobmp` (large/open) and `consectorexpandbmp` (the expand-toggle button
  graphic) (sectormap.cpp:156-158). The constructor starts it on the LARGE image
  (sectormap.cpp:161); clicking the expand button (bottom-right, `GetButtonOffset`,
  sectormap.cpp:205-211) flips `m_pimageBkgnd` small⇄large (the `Button` handler,
  sectormap.cpp:213-235). `Render` draws the background, the expand button, the selected
  sector's NAME, station icons + flying-ship icons aggregated per side (sectormap.cpp:424-488)
  in BOTH sizes, and — only when LARGE — a helium figure and asteroid-icon column
  (sectormap.cpp:491-515). Importantly, the small/closed view does **not** draw warp
  topology; it is a compact sector-*asset-info* widget (name + icons).
- **`SectorMapPane`** (sectormap.cpp:553-1637) — the actual map: the whole-mission warp
  lattice (one line per warp, sectormap.cpp:952-985), one ring+dot per sector positioned
  by authored `ScreenX/ScreenY` via `CalculateScreenMinAndMax`/`GetClusterPoint`
  (sectormap.cpp:743-833, 1310-1316), ownership colours, population dots, and ripcord
  markers. Its visibility is driven by a mode mask (`ChildChanged`, sectormap.cpp:1391-1398)
  — this is the toggled map.

So the brief's "single persistent HUD widget that starts small and expands" merges what native
keeps as (a) an always-attached sector-info pane with a small/large flip and (b) a separately
toggled full map. The part the brief remembered as missing — an **always-on compact map in the
HUD** — is real in native: the P249 overview itself recorded that native shows the sector map
in the HUD by default (`m_bShowSectorMapPane`, `WinTrek.cpp:2737`), whereas this port had hidden
its whole map behind Tab.

## Which existing port file(s) already covered what (investigated before building — not assumed)

This port had BOTH pieces but in a split, Tab-hidden form:

- `StarmapLayout.cs` (P249, pure) + `StarmapRenderer.cs` (P249) = the port of native's
  `SectorMapPane` — the warp-topology map. `StarmapLayout.Fit` faithfully replicates
  `CalculateScreenMinAndMax`/`GetClusterPoint`; `StarmapRenderer` draws the panel, warp edges,
  sector rings and the current-sector highlight. **But** it was only ever rendered when the
  `starmap`/Tab key set `_starmapVisible` — nothing on screen otherwise.
- `SectorInfoPaneLayout.cs` (P272, pure) + `SectorInfoPaneRenderer.cs` (P272) = the port of
  native's `SectorInfoPane` CONTENT (asteroid/helium readout), drawn as a text pane anchored
  to the full map panel — which itself is only shown when the map is toggled.

**Gap confirmed:** neither construct provided native's *always-on* small "closed" state. The
persistent corner mini-map was genuinely missing. This task adds exactly that, reusing the
existing `StarmapLayout` sector-position/warp-edge maths rather than rewriting it.

## What was actually built

1. **Persistent, always-visible compact corner mini-map** — `StarmapRenderer.DrawMini`
   (`Rendering/StarmapRenderer.cs`), placed by a new pure `MinimapGeometry.CornerPanel`
   (`Rendering/MinimapGeometry.cs`) into the bottom-right corner. It fits the SAME authored
   `ScreenX/ScreenY` cluster data through the SAME `StarmapLayout.Fit` as the full map, then
   draws one ring+dot per sector, one line per warp edge, the CURRENT sector bright gold, and
   the names of the sectors DIRECTLY warp-connected to the player drawn dimmed beside their
   dots — "current sector + nearby connections at a glance" made literal. Non-neighbour names
   are omitted to keep the small pane uncluttered.

2. **Always-on in normal flight** — `Game1.DrawStarmap()` is now called unconditionally in the
   Flying draw branch (no toggle key needed); it internally picks the collapsed corner mini-map
   or the expanded full pane from the new `Game1._minimapExpanded` state.

3. **Expand/collapse** — the port's existing Tab binding now expands the mini-map into the full
   centered starmap pane (+ the real sector-info pane) and collapses it back. This fits the
   keyboard-centric HUD conventions: the mouse is the M virtual-joystick steering axis (not a
   picker), and Tab + `,`/`.` are already the established map idioms, so a key — not a mouse
   click — is the expand trigger (the brief explicitly left this to the port's conventions).
   The mini-map caption self-documents it (`MINIMAP YOU: <sector> [TAB] EXPAND`), matching the
   full map's existing `STARMAP | YOU: <sector> | TAB TOGGLE` header.

4. **Consequence fixed** — the `,`/`.` cluster-selection cycling (and its info-pane readout)
   is now gated to the EXPANDED view only, since the collapsed mini-map draws no info pane.

5. **Help text** — `KeyBindingTable.cs` Tab entry updated to "Expand/collapse the always-on
   mini-map (full starmap)".

6. **Pure geometry + tests** — `MinimapGeometry` is `System.Numerics`-only and headless-testable
   (the established discipline of `StarmapLayout`/`SectorInfoPaneLayout`); `MinimapGeometryTests.cs`
   adds 4 tests pinning the corner placement, the size caps, and the never-overflow guarantee.

## Live verification (real server + real client, real proof)

Harness `tools/minimap-proof` (standalone, mirrors `tools/targetinfo-proof`): boots a REAL
in-process server on the REAL multi-sector map `m2xspiderw.igc` (13 sectors, 44 warps), connects
a REAL `ConnectionSession` client that joins and flies, then renders the REAL
`StarmapRenderer.DrawMini` and the REAL expanded `StarmapRenderer.Draw` + real
`SectorInfoPaneRenderer` to offscreen PNGs in `.verify_p360/`.

Harness log (accuracy against the real ClusterGraph/EXPORT data):
```
[clusterinfo] peer 1 -> CLUSTERINFO(200) 13 cluster(s), 44 warp(s)
[export] peer 1 -> 13 cluster(s): 1050:'Ganymede'*, ... , 1062:'Gaia'   (44 warp(s), 134 asteroid(s))
[proof] graph: 13 clusters, 44 warp edges, 13 positioned sites
[proof] CURRENT sector #1050 'Ganymede'  (1050)
[proof] neighbours (warp-linked to current): Terra, Bug Nebula, Rigel, Plato
[proof] wrote .../p360_minimap_collapsed.png
[proof] wrote .../p360_minimap_expanded.png
```

Vision-confirmed frames:
- **`p360_minimap_collapsed.png`** — a compact bottom-right panel shows the full warp lattice;
  the current sector **Ganymede** is highlighted gold (with a "YOU" marker), and the warp-
  neighbours' names (Terra, Plato, Rigel, …) are drawn dimmed beside their dots; the footer reads
  `MINIMAP YOU: Ganymede [TAB] EXPAND`. This is the state that is on screen during NORMAL FLIGHT
  with **no toggle key pressed** — the exact gap this task fixed.
- **`p360_minimap_expanded.png`** — the same data expands to the full centered pane
  (`STARMAP | YOU: Ganymede | TAB TOGGLE`) with all 13 named sectors and the warp lattice, plus
  the real sector-info pane (`Ganymede Sector / Asteroids: 0 / He3 ore: 0`) — the expanded view
  Tab brings up, and collapsing returns to frame 1.

Accuracy confirmed: the mini-map's highlighted sector (#1050 Ganymede) and its neighbour set
(Terra, Bug Nebula, Rigel, Plato) are exactly the player's real sector and the real warp
neighbours from the live `ClusterGraph`, not fabricated.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors**.
- Full suite (`/home/domingo/.dotnet/dotnet test AllegianceCore.slnx`), run twice:
  - Run 1: Lobby 12 ✔ · MonoGame 662 ✔ · ClientCore 168 ✔ · Core.Tests **3275/3279**
    (4 failures — GameOverPlayersWireTests etc., the documented live-wire timing-flake family).
  - Run 2: Lobby 12 ✔ · MonoGame 662 ✔ · ClientCore 168 ✔ · Core.Tests **3273/3279**
    (6 DIFFERENT failures — ProjectileBroadcastTests, LiveWinConditionTests,
    LiveTurretGunnerBindingTests, LiveRipcordTests, LiveRelaunchTests, LiveMoneyTests — the same
    documented rotating live-wire timing-flake family; P359's report names these exact tests as
    the flaky class). None touch this rendering-only change; the 4 new `MinimapGeometryTests`
    pass in both runs.

## Files

- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — always-on draw + `_minimapExpanded` expand/collapse.
- `src/Allegiance.ClientCore.MonoGame/Rendering/StarmapRenderer.cs` — new `DrawMini` + helper.
- `src/Allegiance.ClientCore.MonoGame/Rendering/MinimapGeometry.cs` — new, pure corner placement.
- `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs` — Tab help text.
- `tests/Allegiance.ClientCore.MonoGame.Tests/MinimapGeometryTests.cs` — new, 4 tests.
- `tools/minimap-proof/` — new live-verification harness.
- `docs/STARMAP_OVERVIEW_P249.md` / `docs/SECTOR_INFO_PANE_P272.md` — prior stages' design docs.

No change to `ClusterGraph`, warp-edge data, or networking (out of scope, confirmed untouched);
no native source modified. P354-P359 untouched (P359 confirmed landed on master at 7e941d2c).
