# P570 — Double station with soft particles + native ships inside-out

Branch `master` (main worktree `/home/domingo/Development/AllegianceCore`), committed
(not pushed). Two live-reported defects on client `99df9de6` (P566 native ships,
window 2560x1440, soft particles ON) are investigated; one is fixed and proven in
the real window, the other is hardened against the exact reported failure mode
with an honest account of why it could not be reproduced on the test server.

---

## Part 1 — "a second orange copy of the home station appears with soft ON"

**Live report (Domingo, Ganymede):** with soft particles ON a teal friendly
Garrison placeholder (hub + ring) is drawn AND a second, darker orange hub + ring
appears below it at a different position and orientation. `--soft off` removes it.
Server log: `[stations] side 0 sees 1 station(s)`.

### Investigation (what is NOT the cause)

Instrumented the real client (`ALLEGIANCE_SCENE_DIAG`, now removed) and ran it
against a local server on the same map (`m2xspiderw.igc`, civs 1,3 and 25,3),
soft ON, real backbuffer at 2560x1440 and 1280x720:

```
[scene-diag] mySector=1050 stationsInSector=1 drawsAfterGather=1 buildsInSector=0 warpsInSector=4
[scene-diag] station id=4328 side=0 mySide=0 pos=(1120,1560,0)
```

The colour-pass draw list (`_drawStations`, `Game1.Rendering.cs` `PrepareSceneGeometry`)
contains **exactly one** entry for the sector's single station, drawn once by the
colour loop (`:700-718`) with the friendly teal tint. The P559 restructure gathers
into `_drawStations` once per frame (cleared at `:1709`), derives `_drawStationDepth`
from it, and both the depth pass and the colour pass consume exactly-once data. There
is **no code path that runs the station loop twice or reuses a stale list**; the
sector has 0 building effects (which reuse the station mesh, `BuildingEffectRenderer`)
and 1 station, and multiple real-window soft-ON captures show a single teal station.

### Likely live causes (documented, not reproduced here)

Because the client demonstrably gathers one station, the reported second orange
copy is best explained by live-world data this test map does not reproduce:
- a second real station object in the same sector whose `SideId != MySideId`
  (a legitimately enemy/neutral station drawn with the warm tint — the client
  draws every in-sector station, not only those the server's visibility report
  "sees", `:1715-1721`), or
- a garrison under construction (a `BuildingEffect` at an asteroid reuses the
  station hub+ring mesh, drawn translucent in a dark violet that reads as
  dark/orange-brown against black, axis-aligned → ring face-on), absent on the
  test server (`buildsInSector=0`).

Neither can be reproduced against the local test mission. The `--soft off` toggle
was not reproducible either (the colour pass is soft-independent); it may have
coincided with a different live world state.

### Fix delivered — make "drawn exactly once" structural

New pure gather `Rendering/StationSceneGather.cs` (headless, unit-testable): filters
to the local sector and **deduplicates by `StationId`**, so the same station can
never be added twice in one frame even if the world source ever yields it twice
(the exact second-entry failure mode). `Game1.PrepareSceneGeometry` now gathers
through it. The per-frame colour-pass list therefore contains exactly one entry per
distinct station.

Regression test `P570StationGatherTests.cs`:
- `Same_station_yielded_twice_is_drawn_exactly_once` — **failing-first**: with the
  dedup line removed it fails ("The collection contained 2 items"); with it, `Assert.Single`
  passes.
- `Distinct_stations_in_sector_each_yield_one_entry`, `Stations_in_other_sectors_are_excluded`.

## Part 2 — native ships render inside-out in the real game (front faces culled)

**Live report (Domingo):** the current ship "looks transparent"; Claude's local test
client with `CullMode.None` in `NativeShipRenderer` made it solid. Confirmed here on
the real window: with the committed `CullCounterClockwiseFace`, the own ship in chase
view is **transparent/inside-out** (front faces culled); the roster-capture
nativedump contact sheets (orthographic) showed the same mesh **solid**.

### Root cause — orthographic vs perspective winding

Built a deterministic `cullprobe` (ortho front + perspective front/back at identity
and RY(π)) rendering the real `wc_gtsct.mdl` through the exact
`NativeShipRenderer.Draw` path:

| transform | projection | result |
|---|---|---|
| world = scale (nativedump) | ORTHOGRAPHIC | **solid** |
| world = identity / RY(π) | PERSPECTIVE | **inside-out** |

So the SAME mesh, SAME winding, SAME `CullCounterClockwiseFace` renders solid under
roster-capture's ortho projection but inside-out under MonoGame's **perspective**
projection that the real game (chase, cockpit, missile cameras —
`ChaseCamera.cs:205-207`, `CockpitCamera`, `MissileCamera`) and the hangar preview
(`Game1.Rendering.cs DrawDockedHullPreview :2631-2634`) all use. P566's
`CorrectWinding` reversal was calibrated against the ortho sheet and is therefore
**backwards for the actual game** — exactly Domingo's "winding reversed again by the
in-game transform". No matrix-determinant mirror is involved; the discriminator is
ortho-vs-perspective.

### Fix — ship the raw native winding so CullCounterClockwiseFace keeps the outside

`NativeShipResolver.RenderIndices` (new) returns the raw DirectX winding; it is what
`NativeShipGeometry.Build` ships to the GPU. With the raw winding + real perspective +
`CullCounterClockwiseFace`, the near (visible) surface winds window-clockwise and is
**kept** — the ship renders solid. Applied to every `NativeShipRenderer.Draw` /
`DrawDepth` call, so the chase view, remote ships, the hangar preview and the
soft-particle depth silhouette are all consistent. Per-vertex normals are untouched
(they were already correct, P566).

### Failing-first test — `P570NativeCullingTests.cs`

Pure (no GPU): parses the real `fig05`/`wc_gtsct` `.mdl`, classifies the geometric
near surface winding-independently (global outward-sign from the bulk of the mesh),
and asserts the near surface is KEPT by `CullCounterClockwiseFace` under the game's
perspective transform. Verified: **fails on the old `CorrectWinding`** (0% kept,
"winding is reversed, ships render inside-out") and **passes with the raw winding**
(~100% kept), for both hulls.

### Real-window proof (real backbuffer, `ALLEGIANCE_AUTOSHOT`, window 2560x1440)

- Before (committed `CullCounterClockwiseFace` + `CorrectWinding`): own ship in
  chase view **transparent/inside-out** — `bug2_before_insideout_softoff.png`.
- After (raw winding): own ship **solid** with culling ON, both soft OFF and
  soft ON — `bug2_after_solid_softon.png`, `bug2_after_solid_softon2.png`
  (zoom of the hull shows opaque painted surfaces, no see-through).

The hangar preview and remote ships share the identical `NativeShipRenderer.Draw` +
perspective path proven solid here, so they are fixed by construction. The P566
nativedump sheet (orthographic) now shows the native row inside-out — a diagnostic
trade-off of choosing the winding that the real perspective game needs; noted rather
than left silent.

Also added to the P559 `ALLEGIANCE_AUTOSHOT` live-proof hook: the one-shot screenshot
now dismisses the briefing popup first so the real-backbuffer proof frame isn't
obscured (`Game1.FlightInput.cs`).

## Build / tests

Whole-solution build: **0 warnings, 0 errors**. Full suite via
`scripts/run-tests.sh` (P471) — see Suite results below, recorded as each run
happened on the final code (per `docs/TEST_FLAKES.md`, only documented live-wire /
load flakes can appear in the untouched Core/ClientCore assemblies).

### Suite results (3 × sequential, per-assembly)

Recorded as each run happened on the final code (each assembly as its own
foreground run, per the P570 one-shot session rule):

| Run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1357/1357 | 544/545 | 3765/3766 |
| 2 | 12/12 | 1357/1357 | 543/545 | 3761/3766 |
| 3 | 12/12 | 1357/1357 | 543/545 | 3763/3766 |

The **MonoGame** assembly — the project holding every changed rendering file and
both new P570 test classes — is **1357/1357 green in all 3 runs**. Every failure is
in the untouched `Core` / `ClientCore` live-wire assemblies and matches the
documented load-flakes in `docs/TEST_FLAKES.md` (ClientCore `FullSessionKill…` /
`ShipDeleteGoneLive…` / `ReadyCheckLive…` families; Core `LiveMoney…Board_refund`,
`LiveWinCondition…Multi_mission`, `GameOverPlayersWire…`, `LiveRipcord…Re_target`,
`ConquestTerritoryLive…`, `MineCrossSideVisibility…` — the same counts recur across
P551/P559). None touch P570's code; each run was reported as it happened and the
failing assemblies were not re-run chasing green.

## Files

- `src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs` — ship raw native
  winding via `RenderIndices` (the inside-out fix).
- `src/Allegiance.ClientCore.Game/Rendering/NativeShipResolver.cs` — `RenderIndices`
  decision + rationale.
- `src/Allegiance.ClientCore.Game/Rendering/StationSceneGather.cs` — NEW pure,
  deduplicated station colour-pass gather (exactly-once).
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — station gather routed through
  `StationSceneGather`.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — AUTOSHOT hook dismisses the
  briefing before the real-backbuffer proof shot.
- `tests/.../P570NativeCullingTests.cs` — NEW failing-first (perspective winding).
- `tests/.../P570StationGatherTests.cs` — NEW failing-first (exactly-once stations).
- `docs/reports/P570_DOUBLE_STATION.md` — this report.
