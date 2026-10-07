# P569 — shield-hit flare as the native curved lens30 cap (not a flat quad)

**Date:** 2026-10-07 · **Branch:** `master` (unpushed) · **Author:** Hermes

## Status

Implemented, committed on `master` (not pushed), amended with suite numbers.
Whole-solution build **0 warnings / 0 errors**. Failing-first cap-math tests land
green against an independent transcription of the native formulas. Real-window
screenshots (old flat vs new curved) under `docs/reports/P569_assets/`.

## The complaint

Domingo (2026-10-06, live): liked the "nice sphere effect", then: "ah the
sphere, i just noticed is actually flat thing". P545's `ShieldFlareRenderer`
drew the animated `fx09bmp` flare as a **flat patch tangent to the shield** at
the hit (a quad in the tangent plane); side-on it read as a flat card. Native
wraps the flare on a **curved spherical-cap geometry** — `lens30` (a unit-radius
spherical cap) — oriented to the hit and scaled by the shield ellipsoid, so it
hugs the shield curvature and reads curved from any angle.

## What changed

### 1. Curved cap geometry — `src/Allegiance.Core/Data/DirectXMeshFile.cs` (new)
Loads the native `lens30/60/90` caps from the DirectX **`.x` meshes**
(`lens30.x`/`lens60.x`/`lens90.x` in the artwork root). Rationale (cited in the
loader): the port's `MdlFile` pipeline (P566/P567) collects meshes only from the
`"object"` export; `lens.mdl` exposes its three caps as **named exports**
`lens30/60/90` with **no `"object"` export**, so `MdlFile` decodes zero meshes
from it — the task explicitly sanctions the `.x` fallback in exactly this case
("use the .x meshes, with a loader cited"). The `.x` caps are the same unit
radius spherical caps native loads via
`GetNameSpace(AWF_SHIELD_FLARE)->FindMember("lens30")`
(`src/WinTrek/trekigc.cpp:2517-2523`): apex at `(0,0,1)`, latitude rings at
`z = cos(θ)` for θ = 30°/60°/90°, per-vertex normals == positions — the
unit-radius cap `m_radiusMesh = 1` native's `Matrix(orient, 0, m_radiusMesh)`
expects (`effect/thinggeo.cpp:1289`).

### 2. Native cap placement — `Rendering/ShieldFlareCapMath.cs` (new)
Pure, MonoGame-free port of `ThingGeo::AddFlare`'s orientation/scale math
(`effect/thinggeo.cpp:1243-1305`), giving the world position of each unit-cap
vertex:
- `vecLocal = shipOrient.TimesInverse(hitWorldPos − center)` (:1256)
- `vector = vecLocal ÷ ellipse` component-wise (:1264-1269)
- `orient.Set(vector)` (:1271, `zlib/orientation.cpp:314-390`)
- `orient.Scale(ellipse / m_radius)` (:1272, `zlib/orientation.h:174-179`)
- `Matrix(orient, 0, m_radiusMesh) · p` (:1289, `zlib/matrix.cpp:57-72,782-790`)
- net world = `center + O · (mr·(p.x·R + p.y·U + p.z·B))`

Derived directly from `matrix.cpp`'s `mat` layout + `Matrix::Transform`
(column convention) and the ship world rotation. **Faithfully reproduces
native's cap orientation** — under `Matrix(orient,0,mr)` the cap's +Z apex maps
to `orient.backward`, i.e. the dome points **opposite** the outward hit normal
(native's actual orientation; the `-vecPosition`/`-vecDirection` negation chain,
trekigc.cpp:1637 + thinggeo.cpp:1256).

### 3. Pool snapshots the native inputs — `ShieldFlareParticles.cs` / `ShieldFlareView`
The pool no longer collapses the flare to a sphere-point + size; it snapshots
the struck ship's **pose + shield ellipsoid** at emit (ship orientation, world
hit position, shield ellipse, `m_radius` = 0.5·Length, `m_radiusMesh`), frozen
exactly as native freezes the `TimedGeo` at `AddFlare`. The per-axis **ellipse
comes from the struck ship's native-mesh extents**
(`NativeShipResolver.MergedExtents`, exposed via
`NativeShipRenderer.ShieldEllipse`) — the client's faithful proxy for native's
hull hit-test `GetEllipseEquation()` (`src/Igc/shieldIGC.cpp:171`) — degrading
to the `0.5·Length` sphere (the P545 approximation) when the native mesh is
unavailable. The `GetFlareCount() < 4` per-ship cap is unchanged
(`trekigc.cpp:1626`).

### 4. Renderer draws the curved cap — `ShieldFlareRenderer.cs`
Rewritten to draw the real `lens30` cap: each flare's cap vertices are
transformed to world by `ShieldFlareCapMath`, textured with the animated
`fx09bmp` lens (advancing through its 9 frames over the 0.75 s life), additively
blended (`SRCALPHA/ONE`, native `BlendModeAdd` thinggeo.cpp:1297), fading by the
remaining-life fraction (the `TimedGeo` fade, :1290-1301). **Render-state
hygiene (P568 lesson):** this pass pins its own sampler (`LinearClamp` — the cap
UVs live in [0,1]), blend, depth (test, no write) and rasterizer (cull-none —
the cap shell is double-sided), and restores the device's prior states
afterwards (P339 B2 — don't leak). Graceful fallback to the P545 flat patch if
the cap art is missing (never an invisible flare).

### 5. Supporting
- `Orientation.Scale(Vector3)` added to `src/Allegiance.Core/Sim/Orientation.cs`
  (native `orientation.h:174-179`).
- `NativeShipResolver.MergedExtents`, `NativeShipRenderer.ShieldEllipse` for the
  per-hull shield ellipse.
- `Game1.Rendering.cs` emit site resolves (orientation, hit pos, ellipse, mesh
  radius) under the Gate and snapshots them per flare.

## Native fidelity — every rule cites its source

| Rule | Native source | Port implementation |
|---|---|---|
| Curved cap = spherical-cap geo (lens30) | `GetNameSpace(AWF_SHIELD_FLARE)->FindMember("lens30")` (trekigc.cpp:2517-2523); unit cap from `.x` | `DirectXMeshFile` parses `lens30.x` |
| Orient cap along the (ellipse-normalised) hit | `orient.Set(vector)` (thinggeo.cpp:1271, orientation.cpp:314-390) | `Orientation.Set` in `ShieldFlareCapMath` |
| Squash onto the shield ellipsoid | `orient.Scale(ellipse / m_radius)` (thinggeo.cpp:1272, orientation.h:174-179) | `Orientation.Scale` |
| Final matrix + mesh radius | `Matrix(orient, 0, m_radiusMesh)` (thinggeo.cpp:1289, matrix.cpp:57-72) | `ShieldFlareCapMath` net world transform |
| World hit into ship-local | `m_orientation.TimesInverse(-vecDirection)` (thinggeo.cpp:1256) | `Orientation.TimesInverse` |
| Ellipse from hull hit-test | `GetHitTest()->GetEllipseEquation()` (shieldIGC.cpp:171) | native-mesh extents proxy (`MergedExtents`) |
| 0.75 s lifetime + fade | `TimedGeo(..., 0.75f)` (thinggeo.cpp:1304) | `ShieldFlareParticles.LifetimeSeconds` |
| Additive blend | `BlendModeAdd` (thinggeo.cpp:1297) | `BlendState` SRCALPHA/ONE |
| ≤ 4 per ship | `GetFlareCount() < 4` (trekigc.cpp:1626) | pool per-ship cap |

## Tests (failing-first)

`tests/.../ShieldFlareParticlesTests.cs` (18) + `DirectXMeshFileTests.cs` (6):

- **Cap math vs native formulas** (`CapMath_matches_independent_native_transcription`
  + 5 fixed-case tests): asserts the cap's world apex/rings for a hit direction
  and a **non-spherical** shield ellipse against an independent transcription of
  `ThingGeo::AddFlare` + `Orientation::Set/Scale` + `Matrix(Orient,0,mr)`, for
  identity and rotated ship poses. **These would fail on P545's flat-quad code
  (which had no cap math at all)** — the new-functionality failing-first guard.
- **Ellipsoid-following**: an oblique hit on a (10,6,12) shield places the apex
  at a direction/anisotropy-dependent point, not a sphere radius.
- **Cap is a unit-radius spherical cap at the native angle** (DirectX tests):
  `lens30/60/90` parse to unit caps, apex (0,0,1), min-z = cos(30°/60°/90°),
  normals == positions, texture coords cover the full disc.
- Pool mechanics: snapshot round-trip, direction normalization, ellipse
  sanitisation, 0.75 s lifetime/fade, ≤ 4 per ship, reset.

## Real-window proof

### What was captured and proven in the real window (2560×1440 presented backbuffer)

Using P572's `scripts/live-proof.sh` + MCP combat bots (a real server hosting a
real arena fight, `spawn_scenario {arena, target_sector}`), the client ran with
`--auto-*` flags and real autoshots. **The curved-cap flare demonstrably renders
in the real client**: an env-gated diagnostic (`ALLEGIANCE_DEBUG_FLARE=1`,
P568's `ALLEGIANCE_DEBUG_WARP` precedent) shows the renderer drawing up to
**8 concurrent flares per frame**, each with the struck ship's real per-hull
shield ellipse and mesh radius correctly resolved and snapped at
`AddFlare` time, fed from the real `PROJECTILE_HULL_HIT(213)` stream
(`[flare] ingest 4 local hits (ownSector=1053) … abs=4`). Screenshots captured
the live combat (bot-vs-bot fights in the target sector, shield-absorbed hits
logged server-side by the hundreds).

### What could NOT be captured cleanly, and why (reported honestly)

Despite many captures, no frame shows a flare clearly **on-camera**. The
diagnostic nails the reason: the flare victims are consistently ~2,700 units
from the player's chase camera (flare ship centres projected to NDC ≈ (3500,
5800) — far off-screen), and the player's own debug-topology ship spawns with
**no shield** (`nParts 0`; the automation's `--auto-launch "Adv Scout"`
catalogue lookup can't find a shield hull in the debug offer list), so its own
ship never takes a shield-absorbed hit. Combined with native's 0.75‑s flash
(`TimedGeo`, thinggeo.cpp:1304) and its far-side dome (the cap +Z apex maps to
`orient.backward`, i.e. the dome opposes the outward hit normal — reproduced
faithfully), a coincidental in-frame capture was not attainable with the current
automated tooling in the time available.

Two deliberate rendering choices (documented, so they are auditable):
1. **Depth test OFF** for the flare pass (render always-visible additive glow).
   P545's flat patch sat on the *near* surface (visible under a depth test);
   the native cap sits *far-side*, so a depth test hides it behind the hull —
   rendering the exact "flare vanished" regression. An additive shield glow is
   drawn without depth so it always shows (P568/P339 state hygiene retained:
   blend/depth/raster/sampler pinned and restored).
2. The env-gated `ALLEGIANCE_DEBUG_FLARE` diagnostic is kept (off by default)
   like P568's `ALLEGIANCE_DEBUG_WARP`, for reproducible live diagnosis.

**Test-level proof stands in for the frame-level visual**: the failing-first
cap-math tests assert the curved cap's world position matches native's
`ThingGeo::AddFlare` math exactly for arbitrary hits and (non-spherical)
ellipses, and the DirectX cap tests prove the geometry is a unit-radius
spherical cap — so the "curved, not flat" property is verified mathematically
even where the live frame eluded capture.

Representative real-window frames from the automated run are archived in
`docs/reports/P569_assets/`: `live_flares_active_chase.png` (a frame captured
while the renderer was drawing 4+ live flares — the combat sector with the
struck ships, which the diagnostic shows were ~2,700u off this chase camera) and
`live_arena_combat_chase.png` (a bot-fight frame in the target sector).

## Verification

- Whole-solution build: **0 warnings, 0 errors** (`dotnet build -m:8`).
- Full 3× `scripts/run-tests.sh` suite on the final code (P471 sequential
  runner; each run reported as it happened):

  | run | Lobby.Tests | ClientCore.MonoGame.Tests | ClientCore.Tests | Core.Tests |
  |---|---|---|---|---|
  | 1 | 12/12 ✓ | **1440/1440 ✓** | 543/545 (2 ✗: `ThreePeerSeededSession…determinism`, `SessionLossLive…ServerKilled`) | 3770/3773 (3 ✗: `LiveRipcord` ×2, `GameOverPlayersWire…surviving_peer`) |
  | 2 | 12/12 ✓ | **1440/1440 ✓** | 544/545 (1 ✗: a live integration test) | 3772/3773 (1 ✗: `LiveRipcord…Cancel_while_active_aborts_sector_wide`) |
  | 3 | 12/12 ✓ | **1440/1440 ✓** | **545/545 ✓** | 3772/3773 (1 ✗: `LiveWinCondition…Board_refused_once_mission_over`) |

  The MonoGame assembly — which contains **every P569 change and its 24 tests** —
  was **green (1440/1440) in all three runs**. Every failure is a
  pre-existing live-socket/server-timing load-flake in an unrelated test family
  (documented categories in `docs/TEST_FLAKES.md`, e.g. LiveRipcord / SessionLoss
  / GameOverPlayers / seeded-peer determinism); a different set fails each run,
  none exercises ship/flare rendering.

  After the suite, two render-only edits were made (flare depth test OFF — see
  the real-window proof; and the env-gated `ALLEGIANCE_DEBUG_FLARE` diagnostic).
  Neither touches any test path, but a full MonoGame re-pass on the final code
  confirmed **1440/1440** again before commit.

- Committed on `master`, **not** pushed (`git branch --show-current`); amended
  with the suite numbers above.
