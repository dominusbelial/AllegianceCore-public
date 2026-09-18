# Whole-Mission Starmap Overview (P249)

## What this is

A **toggleable whole-mission starmap overlay** in the MonoGame client
(`src/Allegiance.ClientCore.MonoGame/`): every sector (cluster) in the mission
drawn as a labeled icon positioned by its AUTHORED 2-D layout coordinate, a
line for every warp edge connecting two sectors, and the player's CURRENT
sector highlighted so they always know where they are and where each gate leads
(the whole point of the feature — Domingo's "traveling is confusing" complaint
after the P244/P245/P247 warp-transit fixes).

## Relationship to native — this is a faithful rendering of native's real feature

Native ships this as an **integrated HUD pane**, not a full-screen modal:
`CSectorMap` (`WinTrek/sectormap.cpp`, 1637 lines), shown by default on launch
(`m_bShowSectorMapPane`, `WinTrek.cpp:2737`), drawn over the 3D view. Its layout
comes from authored 2-D coordinates on each cluster — `IclusterIGC::GetScreenX/
GetScreenY` (`clusterIGC.h:369-371`) — a distinct concept from in-game 3-D
position, baked into the mission data. It draws one icon per cluster positioned
by `CalculateScreenMinAndMax` + `GetClusterPoint` (sectormap.cpp:743-833,
1310-1316), and one line per warp edge by walking the whole mission warp list
(sectormap.cpp:952-976).

This port already had every datum needed, client-side, with zero wire plumbing:
- `DataClusterIGC` already decodes `ScreenX`/`ScreenY`
  (`src/Allegiance.Core/Data/DataClusterIGC.cs:35-36,75-76`) — the exact fields.
- `ClientWorld.Clusters`/`.Warps` (`ClientWorld.cs:51,57`) are populated from
  the `EXPORT(26)`/`OT_cluster`+`OT_warp` burst and cover every cluster/warp.
- The dedicated `ClusterGraph` type (`ClusterGraph.cs`) is built from the
  `CLUSTERINFO(200)` message (`ClientWorld.cs:386`) and delivered to the real
  client (`ConnectionSession.cs:345-346`); the join burst sends both the graph
  and every cluster export (`Server.cs:2009-2010, 2046-2050`). It sat **unused**
  (confirmed: zero references to `ClusterGraph` in the MonoGame client) — this
  stage is the first consumer.

So P249 is a **pure rendering/UI task**: no change to `ClientWorld`,
`ClusterGraph`, or any wire codec; no change to `~/Development/Allegiance`
(read-only).

## Toggle key — Tab, and why

`Tab`. The keybinding audit (`docs/KEYBINDING_AUDIT_P220.md`) shows every other
flight key claimed: W/S/Q/E, arrows, A/D, Shift (booster), R (quick-chat), M
(TK_ToggleMouse), V (camera), T (target), G (autopilot), Enter/Back/Escape
(chat), Space (fire), K (suicide), C (chaff), N (mine), OemTilde/[ /] (weapon
mode), LeftCtrl (missile). The brief blessed Tab **or** M; M is already taken,
so **Tab** it is — the natural, unclaimed, native-adjacent overview key. It is
edge-triggered (held key can't re-fire) and gated behind the same `composing`
check as every other toggle so typing in chat / navigating the quick-chat menu
never flips it. The on-screen header literally reads "TAB TOGGLE" so the binding
is self-documenting.

**Overlay, not a modal:** per the brief and native's "integrated pane", the map
is drawn over the 3D scene with the game still simulating underneath (a 2-D
`SpriteBatch` pane on top of the HUD, beneath the chat overlay so typed text
stays readable). It does not pause or replace gameplay.

## How the authored ScreenX/ScreenY coordinates are normalized

`Rendering/StarmapLayout.cs` (pure, `System.Numerics` only, headless-testable)
replicates native's `CalculateScreenMinAndMax`/`GetClusterPoint` (sectormap.cpp:
743-833, 1310-1316):

1. min/max of the authored ScreenX/ScreenY over every site;
2. a border fudge `c_bfr = 0.1 * max(xrange, yrange, 0.0001)` expanded on all
   sides (native sectormap.cpp:777-783);
3. aspect-ratio preservation into the usable panel area by growing the narrower
   axis (sectormap.cpp:791-826), so the map fills the panel without distortion;
4. point mapping `x = panelX + border + (ScreenX-minX)/(maxX-minX)*(panelW-2*border)`
   (native GetClusterPoint sectormap.cpp:1310-1316). A degenerate all-identical /
   single-site set is guarded (centered).

`Rendering/StarmapRenderer.cs` draws the translucent panel, one teal line per
warp edge, a ring+dot+name per sector, and the current sector as a bright gold
double-ring + "YOU" tag. Named follow-up candidates are deliberately **not**
drawn (see Out of scope).

**Degeneracy check — honest finding:** the live proof used the real
m2xspiderw map (13 sectors, 44 warps). The authored coordinates are well
distributed — ScreenX span [0.00, 1.60], ScreenY span [0.20, 1.40] — mapping to
a clean symmetric diamond/hex ring with the current sector (Ganymede) at one
vertex. **Not** a corner pile or wild scale mismatch; the min/max fit handles it
regardless (the anti-collapse unit test `FarApartClusters_SpreadAcrossThePanel_
NotPiled` + must-fail proof redden when the spread is neutralized).

## Live proof (`.verify_p249/p249_starmap.png`)

A `/tmp` MiniGame harness (the established P207/P218/P227 offscreen-screenshot
technique, rendered synchronously in LoadContent per P234/P235) boots a **real**
in-process `Allegiance.Server` on m2xspiderw, connects a **real**
`ConnectionSession`, waits for the whole-mission graph + cluster exports + ship
assignment to populate, then renders the **real** `StarmapRenderer` to an
offscreen `RenderTarget2D` and saves it.

Harness log:
```
[boot] game 'P249 Starmap' ready: map=m2xspiderw.igc, 13 sectors, 2 sides, 44 warps, 134 asteroids
[clusterinfo] peer 1 'p249probe' -> CLUSTERINFO(200) 13 cluster(s), 44 warp(s)
[export] peer 1 'p249probe' -> 13 cluster(s): 1050:'Ganymede'*, ..., 1062:'Gaia'
[p249] graph: 13 clusters, 44 warp edges, 13 sites with authored positions
[p249] authored ScreenX range [0.00,1.60]; ScreenY range [0.20,1.40]
[p249] current sector = 1050 ('Ganymede')
[p249] saved .../p249_starmap.png (60513 bytes)
```

Vision-confirmed screenshot: a translucent teal-bordered panel headed
`STARMAP | YOU: Ganymede | TAB TOGGLE`, showing **13 labeled sector icons
connected by teal warp lines** in a clean symmetric diamond layout, with the
current sector **Ganymede** highlighted in **bright gold** with a unique
concentric-ring icon and a "YOU" label — unmistakable, readable, real-map (not a
jumble).

**Must-fail proven (unit):** temporarily neutralizing `StarmapLayout`'s spread
(`scaleX=scaleY=0`) turned 3 layout tests red
(`FarApartClusters_SpreadAcrossThePanel_NotPiled`,
`Mapping_IsMonotonic_AndOrdered`, `SingleCluster_IsCentered`), then green after
revert. The renderer/wiring is inherently must-fail: remove the `Tab` toggle or
the `DrawStarmap()` call and no map can ever appear.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- `Allegiance.Core.Tests`: **3066 pass / 3067 total** — only the pre-existing
  documented `LiveTurretGunnerBindingTests` timing flake (fails on the pristine
  tree; not chased; this stage is client-only, zero Core/Server references).
- `Allegiance.ClientCore.Tests`: **83 pass** (one intermittent live-network
  flake seen once, green on rerun — the documented flaky class).
- `Allegiance.ClientCore.MonoGame.Tests`: **258 pass** (baseline 251 + 7 new
  `StarmapLayoutTests`), **2 clean runs**.

## Files

- `src/.../Rendering/StarmapLayout.cs` (new) — pure authored-coord → panel fit.
- `src/.../Rendering/StarmapRenderer.cs` (new) — draws the overlay pane.
- `src/.../Game1.cs` — `Tab` toggle + `DrawStarmap()` gather/render wiring.
- `tests/.../StarmapLayoutTests.cs` (new) — 7 pure-logic tests.

## Out of scope / follow-up candidates (NOT built — named for the future)

- **Station / asteroid icons on the starmap** — native draws station icons by
  class/side (`sectormap.cpp:1159+`) and asteroid icons; this task's bar is
  clusters + warp edges only.
- **Per-side ownership coloring** — native tints sectors by `GetClusterOwners`
  / `GetClusterPopulation` (sectormap.cpp:11-76, 1049+); not drawn here.
- **Ripcord-availability markers** (sectormap.cpp:987-1032) — not drawn here.
- **The local current-sector tactical radar** (nearby ships/stations/asteroids
  as blips) is a separate task (P250) — none of it built here.

No native source was modified (read-only).
