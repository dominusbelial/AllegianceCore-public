# HUD-21 — target pane 3-D model preview (native targetGeo/targetCamera into a reused RenderTarget2D)

Branch: **master** (direct commit `HUD-21:`). Base: HEAD containing FONT-1 and
HUD-19. Closes parity row 409 (`docs/parity/parity-matrix.csv`, area `hud`,
"Target model preview", previously `partial`).

## What this does

The classic bottom target pane now shows a 3-D model of the target (the native
`GeoImage(targetGeo, Rect(0,0,96,96), targetCamera, true)` at
`Artwork/dialog.mdl:4224-4231`) instead of leaving the pane empty. Three new
files plus wiring:

- `Rendering/TargetModelPreview.cs` — the PURE rules (no GraphicsDevice):
  eligibility gate, view/projection matrices, the `TurnTo` up fallback, the
  radius-1 framing and the native light. Headless-testable.
- `Rendering/TargetModelPreviewRenderer.cs` — owns ONE 256×256 render target
  (≥ 96·2.1 = 201.6 px, the largest pane scale) and draws the eligible target's
  native mesh into it before the frame's colour target is bound.
- `Game1.Rendering.cs::RenderTargetModelPreview` — resolves the selected target
  (source-ship climb, native mesh + texture, orientation, spin) under the Gate,
  renders it, and rebinds+re-clears the colour target.
- `HudRenderer.cs` / `ModernHudRenderer.cs` — a `Texture2D? TargetModelPreview`
  input drawn into `PaneRect` before the oval/word art (native child order).

### Eligibility (`TargetModelPreview.Eligible`)

The native `SetTargetGeo` gate (`WinTrek/WinTrek.cpp:8783-8818`): a preview is
shown only for a ship/station/asteroid that is not the player's own ship, is in
the player's cluster, is visible (CanSee) and resolves to a native mesh.
Ships are replaced by `GetSourceShip()` first — the port climbs `ShipEntity.ParentId`
(the turret/drone → carrier link) to the root parent. Warp/probe/mine/
missile/treasure and no-native-mesh fall back to the empty viewport.

### View & projection

`View` = eye at `−normalize(dp)·8` looking at the origin, up = the player's up
except the `TurnTo` ∓forward fallback when |cos| > 0.99
(`WinTrek.cpp:328-330,438-444`; `zlib/orientation.cpp:220-231`), with
`dp=(0,1,5)` when |dp|² < 1 (`WinTrek.cpp:438-440`). `Projection` =
`Matrix.CreateOrthographic(2,2,1,20)` — the `m_focus==0` ortho branch of
`Camera::Evaluate` (`engine/camera.cpp:46-69`), the native ZClip 1..20
(`WinTrek.cpp:329`) and `GeoImage::Render`'s `Scale2(0.5·rect.XSize())`
(`engine/image.cpp:1164-1165`), so a radius-1 object fills the whole 96 px pane.

### Radius-1 framing

`nativeScale = 1 / geo.MeshRadius` (the net of `TargetGeo::Render`'s
`Scale3(1/m_radius)` at `effect/thinggeo.cpp:373-381` and `RenderTargetGeo`'s
`m_scale = radius/m_radiusMesh` at `thinggeo.cpp:1185-1193,1482-1573`), with the
target's OWN orientation (asteroids include their current spin). The native
`RY(π)` flip is already folded into the ship/station/asteroid renderers. Every
target therefore renders at radius 1 (its bounding sphere fills the pane)
whatever its real size — matching native.

### Light sign derivation (the to/from question)

Native `GeoImage` defaults `m_vecLight(0,0,1)`, white, non-bidirectional,
ambient 0 (`engine/image.cpp:1120-1129`). `Device3D::DirectionalLight` stores
the vector unchanged (`device3d.cpp:444-452`, view-transform line commented
out); at render `m_vecLight = m_matLight·(0,0,1) = (0,0,-1)` (`device3d.cpp:295-297,734-752`)
becomes `D3DLIGHT9.Direction`. Reusing P529's reasoning
(`docs/reports/P529_ASTEROID_LIGHTING.md`): a D3D directional light lights the
face whose normal **opposes** `Direction`, so the brightest face here has
normal `=−Direction=(0,0,+1)` — i.e. the face pointing **toward the camera**
(the native preview camera is fixed at `(0,0,8)` looking at the target, and
`m_porientTransformTarget` maps the player-facing side toward it). Native's
GeoImage light is therefore a **headlight aligned with the preview camera**.

The port reproduces that invariant: the key light's propagation is
`+normalize(dp)` (from the camera toward the target), which BasicEffect lights
as normal `=−propagation` = toward the camera. So the **camera-facing side is
lit** — confirmed in every real-client screenshot below (each model reads lit
on the visible side). The light is encoded as a `SectorLighting` with a single
white key, black alt (the renderers' `SectorLightingEffect` always enables
Light1, so black is the "no alt light" encoding) and ambient 0 — exactly
native's single-white-no-alt-ambient-0 GeoImage light.

### Rendering order (P559/P582b-safe)

The preview is rendered in `Game1.Rendering.cs` right after
`PrepareSceneGeometry`, BEFORE the soft-particle depth pass and before the
frame's colour target is bound — so the save/switch/restore is lossless
(nothing has been drawn to the colour target yet; the frame cleared it at
`:317`). After the preview, the colour target is rebound (the same selection as
`:593`) and re-cleared black, and `RenderTargetGuard.AssertColourTargetRestored`
trips in DEBUG if the restore is not lossless. The saved-target array is cached
across frames so the steady-state path allocates nothing per frame.

### Compose in the pane

`DrawTargetPane` calls `DrawTargetModelPreview(x0,y0,s)` first, painting the RT
into `PaneRect` (native y-up lower-left (270,9), a 96·s square) with a white
alpha tint, BEFORE the oval/word art — the native `GroupImage::Render` child
order (`image.cpp:954-972`, `dialog.mdl:4218-4242`), so the oval sits on top of
the model. When the input is null the pane looks exactly as it does today (the
empty fallback). `ModernHudRenderer.DrawTargetPane` reuses the same helper.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
| --- | --- | --- | --- |
| Preview gate (own ship / cluster / CanSee / no mesh → empty) | `WinTrek.cpp:8783-8818` | design | `Eligible` + per-kind resolution; ships use the `GetSourceShip` parent climb |
| Player-relative orientation (TurnTo up fallback, dp default) | `WinTrek.cpp:431-452`; `orientation.cpp:220-231` | design | `View` (eye at −dp·8, player up, ∓forward near-parallel) |
| Ortho camera (focus 0, ZClip 1..20) | `camera.cpp:46-69`; `WinTrek.cpp:328-330` | design | `CreateOrthographic(2,2,1,20)` |
| Radius-1 framing | `thinggeo.cpp:373-381,1185-1193,1482-1573` | design | `nativeScale = 1/MeshRadius` + own orientation/spin |
| GeoImage light (white, no alt, ambient 0, headlight toward camera) | `image.cpp:1120-1129`; `device3d.cpp:444-452,734-752,295-297` | design | Single white key, black alt, ambient 0, propagation from camera |
| Paint after gauges, before oval | `image.cpp:954-972`; `dialog.mdl:4218-4242` | design | `DrawTargetModelPreview` before the oval art |
| Mesh RY(π) flip | `thinggeo.cpp:1501-1503` | design | Already folded into the native renderers |

## Modern alternative considered

Native renders the preview into a fixed 96 px z-buffered pane with a per-frame
re-wrapped geometry. The port renders once per frame into a reused 256×256 RT
(the same RT every frame, no reallocation) and composites it as a sprite — the
modern pattern (one small offscreen pass + a sprite blit) versus native's
in-place image-space 3-D draw. A further optimisation (bake only on target
change, not every frame) was considered but rejected: the preview follows the
target's live orientation/spin, so it must re-render each frame; the cost is a
single 256² mesh draw, measured at ~0 ms below the frame quantum (see
Verification).

## Out of scope (gaps)

- Alephs (`CreateAlephGeo`), probes, missiles, mines and model treasure fall
  back to the empty viewport (each needs its own geometry path in the port).
- Meshy / catalogue / procedural stand-in ship art — a hull with no native mesh
  shows no preview (fallback).
- Flares / lights / engine trails inside the preview (`RenderTargetGeo`
  flares / `m_pgeoLights`) and the cloak fade.
- The other `GeoImage(targetGeo…)` pane variants (`dialog.mdl:910,1033,3465,4384,4502`),
  `TargetExperienceImage`, the additive `hudBlendMode` on the oval art, fonts (FONT-1).
- The hangar/loadout spinning-ship preview is untouched.

## Verification

- Whole-solution build: **0 warnings / 0 errors**.
- New `Hud21TargetModelPreviewTests` (16) + affected suites 3× alone:
  **197/197 ×3 PASS** (HUD-21, TargetPaneModel, HudModel, Hud11/12/12b/13/14/15,
  Font1, RendererDisposalGuard, AlephGlimpse). Allocation test: `Eligible`,
  `View`, `PaneRect`, `Lighting` 10 000× → **0 bytes**.
- One full fast-tier run (`tools/run-tests.sh --tier fast`): **9,674 tests,
  270.5 s, PASS** (Lobby 24, ClientCore.MonoGame 3087, ClientCore 1577, Core 4971;
  3 NotExecuted = live-tier). The single first-run failure
  (`RendererDisposalGuardTests`) was my new disposable field — registered in the
  guard set and fixed. The other first-run failure (`P761bCommandRelayTests`,
  untouched `Allegiance.Core`) passed 3/3 in isolation and is already logged in
  `docs/TEST_FLAKES.md`.
- `P700ShowcaseLiveTests…never_parks…` 5×: **PASS ×5** (1m50s/1m50s/2m03s/1m50s/1m50s).
- `python3 scripts/parity-scorecard.py`: clean; matrix `{6}` columns, LF only.
- Failing-first: the eligibility/matrix/allocation tests target new pure code
  that did not compile on the pre-change master.

## Real-client screenshots (P572 live-proof.sh, real backbuffer)

Captured from the actual windowed client (`ALLEGIANCE_AUTOSHOT` real-backbuffer
path, production `Allegiance.ClientCore.MonoGame`) against a local server,
classic skin. Downscaled copies are in `docs/reports/HUD21_assets/`; full-res
originals in `~/allegiance-evidence/HUD21/shots-full/`.

**(a) Enemy fighter, 2560×1440** (`preview_enemy_fighter_2560.jpg`): the target
pane shows a 3-D fighter (`mcp-b001-2245: Adv Fighter`). The model is an
angular fuselage + wings, **lit on the visible camera-facing side**, centered
in the pane, ~150 px wide (a slim fighter silhouette within the radius-1
bounding sphere — a fighter doesn't fill the square the way a station does).
The player is alive in flight.

**(b) Station, 2560×1440** (`preview_station_2560.jpg`): a blocky, multi-faceted
garrison (`Omega Debug Garrison`) fills the pane, **lit on the visible side**
(bright panels vs shadowed facets), centered, ~200 px.

**(c) Asteroid, 2560×1440** (`preview_asteroid_2560.jpg`): an irregular lumpy
uranium rock (`U2523: Uranium`) centered in the pane, **lit on the visible
side** (bright upper-right highlight, shaded left), ~200 px.

**(d) No target (empty fallback), 2560×1440** (`preview_no_target_2560.jpg`):
with no target selected, the central pane is **empty** (transparent, showing
space) — exactly the pre-change look, confirming the fallback.

**(e) Station, 1920×1080** (`preview_station_1920.jpg`): the station model
renders in the pane at the lower resolution too, lit and centered.

**Frame-time delta (preview on vs off), real client:** measured with
`ALLEGIANCE_VOL_PERFLOG` in the real client on the same map, once with an
eligible asteroid target (preview ON every frame) and once with no target
(preview OFF). Both runs log the identical vsync-quantized frame pattern
(min 16.67 ms = 60 fps floor, with periodic ~483 ms software-rasterizer stalls
in this llvmpipe environment; avg ~465 ms). The preview adds **no measurable
frame-time delta** — a single 256² RT mesh draw sits far below the 16.67 ms
frame quantum. Raw CSVs: `~/allegiance-evidence/HUD21/perf-on.csv`,
`~/allegiance-evidence/HUD21/perf-off.csv`.

## Files

- `src/Allegiance.ClientCore.Game/Rendering/TargetModelPreview.cs` (new)
- `src/Allegiance.ClientCore.Game/Rendering/TargetModelPreviewRenderer.cs` (new)
- `src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs` (+ `TargetModelPreview` input, `DrawTargetModelPreview`, PaneRect call)
- `src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.cs` (+ one-line helper call)
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` (+ `RenderTargetModelPreview`, render+rebind after `PrepareSceneGeometry`, HUD feed)
- `src/Allegiance.ClientCore.Game/Game1.cs`, `Game1.Lifecycle.cs` (+ renderer field, init, dispose)
- `tests/Allegiance.ClientCore.MonoGame.Tests/Hud21TargetModelPreviewTests.cs` (new)
- `tests/.../RendererDisposalGuardTests.cs` (+ registered field)
- `docs/parity/parity-matrix.csv` (row 409)
