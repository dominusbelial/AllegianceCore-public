# Sector Info Pane on the Starmap (P272)

Follow-up to P249 (whole-mission starmap). Adds cluster **selection** and a
per-cluster **detail pane** to the P249 starmap — the port of native's
`SectorInfoPane` (`WinTrek/sectormap.cpp:139-538`).

## What this is

While the starmap is open (Tab), the pilot cycles the selected cluster with
`,` / `.` (prev / next), and a detail pane appears in the top-left of the map
panel showing that cluster's name, asteroid count, per-capability asteroid
breakdown and helium ore — populated from the **client's own decoded wire
data**, never fabricated.

## Choice: keyboard cycling, not mouse hover — and why

Native's map is purely mouse-driven (`.HitTest` → `SelectCluster`, sectormap.cpp:
189, 1435-1444) and the brief explicitly allowed either keyboard cycle or mouse
hover **"if mouse position is already available in that mode"**. In this port the
mouse is **not a map picker**: the only mouse input wired into the MonoGame
flight controls is the M virtual-joystick steering axis (`Game1.cs:735-748,
947-960`), which deflects the ship — there is no map-cursor/picking path. So a
dedicated keyboard pair is the established starmap-navigation idiom here (Tab
toggles the map). Keys chosen: **`,` (OemComma) and `.` (OemPeriod)** — the one
genuinely free, natural prev/next pair; every other key is claimed (arrows =
yaw `Game1.cs:914-915`, brackets = weapon cycle `:887-895`, OemTilde = group
fire `:877`, M/V/K/C/N/T/G all taken, per the P220 keybinding audit). Mouse hover
over the map is a clean named follow-up if a real picker is ever wired.

## What data is real (client-side), and the one honest caveat

Everything the pane shows comes from the client's own decoded static-core
exports, exactly as native consumes them:

- **Cluster name / positions**: `DataClusterIGC` from the `OT_cluster` export
  decode (`Data/DataClusterIGC.cs:35-37`), via `ClientWorld.Clusters` — reused
  for the P249 layout, not rebuilt.
- **Asteroid count + per-capability grouping**: `DataAsteroidIGC.AabmCapabilities`
  (decoded from `OT_asteroid` at offset 64, `Data/DataAsteroidIGC.cs:38,86`) —
  the same field the client already ships to the GPU for rock rendering (P251-P253).
  Grouping follows native `DrawAsteroidIcons` exactly: a rock forms a special
  group only when `(aabm & ~c_aabmBuildable) != 0` (sectormap.cpp:296-305); the
  buildable-only bits `c_aabmBuildable=0x08` (igc.h:765) are excluded from the
  groups but still counted in `Asteroids: N`.
- **Helium presence + ore**: `DataAsteroidIGC.Ore` summed over rocks carrying
  `c_aabmMineHe3=0x01` (igc.h:761), exactly native `GetHeliumInCluster`
  (sectormap.cpp:350-370). The `He3`/`He3-rich` capability split also follows
  native (0x02 `MineLotsHe3` is its own icon group and is NOT summed into the
  He3 line, sectormap.cpp:360-366 — pinned by a unit test).

**Honest caveat — per-side "seen" ore is a named follow-up, not faked.**
Native sums `OreSeenBySide` (sectormap.cpp:365) and optionally greys the line
unless a He3 rock is *eyed* by my side (`HeliumAsteroidVisibileInCluster`,
sectormap.cpp:373-390 — which native itself disabled, :499-503). Per-side
ore *visibility* is server-side-only in this port (P132/P134 `SideVisibility`,
`Sim/Asteroid.cs:101-124`) and is **not decoded to the MonoGame client**. So the
pane shows the client's **static-record `Ore`** and **capability bit** (both
genuinely decoded), not a side-filtered "what I can currently see" figure. That
strict eye-visibility refinement is a clean follow-up (would need per-side ore
visibility plumbed to the client); the number shown is real static-core data.

## Live proof (`.verify_p272/`)

Offscreen MiniGame harness (P234/P235 sync-in-LoadContent recipe, mirroring
P249): boots a **real** in-process `Allegiance.Server` on m2xspiderw + a **real**
`ConnectionSession`, waits for the real join-sync burst (CLUSTERINFO graph +
OT_cluster/OT_asteroid exports + own ship), then renders the **real**
`StarmapRenderer` + **real** `SectorInfoPaneRenderer` exactly as
`Game1.DrawStarmap` gathers them.

Harness log (real data, matches an independent mission decode to the digit):
```
[p272] REAL graph: 13 clusters, 44 warps, 134 asteroids; my sector = 1050 ('Ganymede')
[p272] selected 'Alderan' (1052) -> 134 asteroids, HasHe3=True, He3ore=100602, groups=[He3x9]
[p272] pane lines:
   Alderan Sector
   Asteroids: 134
   He3: 9
   He3 ore: 100602
```

Three frames, all vision-confirmed:
- `starmap_noselection.png` — map only (name/positions/warp edges + current
  sector), **no** info pane → proves the pane appears only when a cluster is
  selected (the must-fail wiring).
- `starmap_alderan_pane.png` — **the required proof**: pane shows
  `Alderan Sector / Asteroids: 134 / He3: 9 / He3 ore: 100602` — real
  populated data (all 134 m2xspiderw rocks are in Alderan; 125 buildable-only +
  9 He3, He3 ore 100602), not placeholder text.
- `starmap_emptycluster_pane.png` — selecting Ganymede (0 rocks) shows
  `Ganymede Sector / Asteroids: 0 / He3 ore: 0` gracefully (no group lines).

**Must-fail (unit, proven red then green):** temporarily removing the
`& ~c_aabmBuildable` exclusion in `SectorInfoPaneLayout.Build` turned
`BuildableOnlyRocks_AreCounted_ButFormNoSpecialGroup` red; reverted green. The
`NextSelection` wrap/cycle tests likewise only pass because the selection helper
exists, and the noselection frame shows the pane cannot appear without a
selection (removing `CycleSelectedSector` or the pane Draw call kills it).

## Structure — the pure/renderer split (as P249)

- `Rendering/SectorInfoPaneLayout.cs` (**pure**, `System.Numerics` only,
  headless-testable): capability constants (igc.h:761-766), `Build()` (grouping
  + helium aggregation + display lines), `NextSelection()` (wrap-around cycle),
  `LineOrigins()` (the 2-D line stacking math).
- `Rendering/SectorInfoPaneRenderer.cs` (MonoGame): paints the translucent panel
  and the display lines from the pure `Data`. Own SpriteBatch session, like
  `StarmapRenderer`.
- `Game1.cs`: `_selectedSectorId` + `,`/`.` edge-triggered cycling in
  `UpdateFlying` (gated on `!composing && _starmapVisible`), `CycleSelectedSector()`
  pulling the ordered cluster ids + player's sector under the Gate lock, and
  `DrawStarmap` gathering that cluster's asteroids from `ClientWorld.Asteroids`
  (filtered by `ClusterId`) into `SectorInfoPaneLayout.Build`.

No change to native source (`~/Development/Allegiance`, read-only). No wire,
codec, `ClientWorld` or `StarmapLayout` change (reuses P249's cluster-position
math; extends, not rebuilds).

## Out of scope (not built — named)

- **Mouse hover / click picking** on the map (native's native path) — no picker
  wired into flight controls; a follow-up if one is added.
- **Click-to-warp / autopilot targeting from the map** — P256 handles autopilot
  targeting priority elsewhere; this pane is informational display only.
- **Per-side "seen"/eyed ore** in the He3 figure — server-side P134 visibility
  isn't decoded client-side (named follow-up above).
- **Station icons in the pane** (native `DrawStationIcons`, sectormap.cpp:238-278
  commented out) — not in this task's bar.
- **Probe count line** (sectormap.cpp:328-344) — client probe data is per-deploy
  (P260), not a per-cluster static roster; named follow-up.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **392 pass / 392** (includes 12 new
  `SectorInfoPaneLayoutTests`), **2 clean runs**.
- `Allegiance.ClientCore.Tests`: **123 pass / 123.**
- `Allegiance.Core.Tests`: **3125 pass / 3126** — only the documented pre-existing
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  machine-state flake (fails persistently in isolation AND on the pristine tree;
  zero refs to this stage's symbols — this stage is client-only). Across multiple
  full runs the only other failures were documented intermittent live-network
  flakes (`LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`,
  etc.), each confirmed green in isolation.

## Files

- `src/.../Rendering/SectorInfoPaneLayout.cs` (new) — pure content/selection logic.
- `src/.../Rendering/SectorInfoPaneRenderer.cs` (new) — paints the pane.
- `src/.../Game1.cs` — `,`/`.` selection + `DrawStarmap` pane wiring.
- `tests/.../SectorInfoPaneLayoutTests.cs` (new) — 12 pure-logic tests.
- `.verify_p272/` — live-proof screenshots.
