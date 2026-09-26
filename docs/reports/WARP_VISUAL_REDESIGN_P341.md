# WARP_VISUAL_REDESIGN_P341 — camera-facing billboard core for the aleph (warp gate)

Date: 2026-09-22. Landed on `master` (not pushed — Domingo pushes).

## Diagnosis (from the kimi-k3 consultation, read in full before starting)

`pi-aleph-warp-design-kimik3.log` confirmed the complaint is STRUCTURAL, not a
tuning problem: the `WarpMeshFactory`/`WarpRenderer` gate is a hollow,
open-ended, symmetric-hourglass surface of revolution. Head-on it reads as a
tunnel (the only angle it was tuned for); from any oblique angle the depth axis
collapses into the view plane and the mesh degenerates into a bowtie/hyperboloid
silhouette — the "manifold" Domingo sees. Additive blend made the deep-navy rims
functionally invisible from the side, so the gate read smaller/dimmer obliquely
than head-on. The single brightest element (the white-hot center pinch) is the
most degenerate — a zero-radius point with no silhouette from the side.

Prescribed fix (implemented here): Layer 1 = a **camera-facing radial-gradient
glow disc at the warp center** (native's own "a bright spot in the center" flavor
text rendered literally), which structurally guarantees identical apparent
size/brightness from every direction; Layer 2 = brighten the funnel's rim colors
slightly so its outer extent survives the additive blend obliquely. Layer 3 (an
ambient-particle sphere) is explicitly out of scope this task.

## Implementation

### Layer 1 — the billboard glow core

- **`Rendering/WarpCoreMeshFactory.cs` (new)** — a pure-logic producer (no
  MonoGame types on its surface, same discipline as `WarpMeshFactory`) emitting a
  `WarpMesh` radial disc: concentric rings in the local XY plane (normal = +Z),
  unit radius, with a radial vertex gradient — white-cyan core (alpha 1) fading
  to transparent deep-navy rim (alpha 0, `alpha = (1−u)^Falloff`), so under the
  renderer's additive blend it reads as a soft self-lit glow, not a hard coin.
  Deterministic (MathF only). The mesh is deliberately a FLAT disc; keeping it
  camera-facing is the renderer's job.

- **`Rendering/WarpRenderer.cs`** — the disc is billboarded in the draw. The
  renderer extracts the core's world transform from the funnel's `world`
  (uniform scale from row-1 length, translation from row 4) and **composes the
  billboard rotation from the view matrix's basis vectors** — the standard
  technique: unit `toCam = -relPos` (camera sits at the origin of the
  camera-relative world, P207 floating-origin rule) is the disc's normal; the
  camera's world-space up is recovered as column 2 of the view rotation block
  (`view.M12/M22/M32`), respecting camera roll; right = camUp × toCam, up =
  toCam × right (right-handed, with a degenerate-case fallback when camUp ∥
  toCam). The core world = billboard × `CreateScale(CoreRadiusFraction × s)` ×
  translation, drawn LAST (over the funnel's pinch) under the same additive /
  depth-read / cull-none state. `CoreRadiusFraction = 1.0` sizes the disc to
  exactly the funnel's rim radius (radius × `VisualScaleMultiplier`), per the
  consultation's "sized to roughly the rim radius".

  The core's billboard is exposed as `public void DrawCore(world, view, proj)`
  (a clean seam) — `Draw()` calls it after the funnel; the live-proof harness
  calls it alone to isolate the disc.

### Layer 2 — brighten the funnel's rims

`WarpMeshFactory` rim stops raised from deep navy `(0.040, 0.070, 0.320)` to a
mid-blue `(0.092, 0.268, 0.688)` so the funnel's outer extent survives the
additive blend when viewed obliquely. The funnel stays as the "whirlpool"
motion/structure element around the new core; it is no longer solely
responsible for carrying the read.

### Scope boundaries honoured
- PURE visual change. No change to the warp's gameplay trigger radius/collision
  behaviour (same "visual only, sim untouched" rule as P333). No change to
  `Allegiance.Core` sim/server/wire.
- Native reference source (`~/Development/Allegiance`) untouched.
- Layer 3 (ambient particle shell) NOT done — separate larger piece, noted.

## Verification

### Whole-solution build
`AllegianceCore.slnx` builds with 0 warnings, 0 errors (includes the new
`tools/warp-visual-proof` harness, added to the slnx).

### Test suite
Full `dotnet test AllegianceCore.slnx` run twice on the final code:
- `Allegiance.Lobby.Tests`: 12/12 pass both runs.
- `Allegiance.ClientCore.MonoGame.Tests`: 621/621 pass both runs (includes the
  5 new `WarpCoreMeshFactoryTests` + the updated `WarpMeshFactoryTests` for the
  mid-blue rims).
- Remaining failures across runs are the pre-existing live-wire TIMING flakes:
  none reference the warp renderer (the failing test projects do not even
  reference the `Allegiance.ClientCore.MonoGame` project this change lives in —
  verified via csproj). Characterized in isolation:
  - `LiveTurretGunnerBindingTests.Board_binds_gunner...` — the documented
    persistent machine-state flake (fails on the pristine tree; don't chase).
  - `ReadyCheckLiveTests.Only_the_mission_owner_can_force_team_ready...`,
    `LiveCountdownStartPingTests.StartGame_owner_all_ready...` — pass green in
    isolation (fail only under full-suite concurrency).
  - `GameOverPlayersWireTests.Conquest_win...` — intermittent (flaps 1/3 green
    in isolation); a P194-era multi-mission wire test unrelated to P341.
  The failure set shuffled between runs (run A vs run B), confirming timing
  flakiness rather than a deterministic regression from this change.

### Live multi-angle proof (the deliverable)
`tools/warp-visual-proof/` (new harness, mirroring `tools/hullswap-proof`):
boots a REAL in-process server on `m2xspiderw.igc` (44 real warps exported),
connects a REAL MonoGame-rendering client (`ConnectionSession`), grabs warp
#2167 'Terra' (radius 100), and renders through the REAL `WarpRenderer` from
three distinct camera angles. Artifacts in `.verify_p341/`:

Full gate (funnel + billboard core), three angles:
- `warp_headon.png` — down the axis: reads as a clean round portal, bright
  white-cyan center fading to blue rim.
- `warp_side90.png` — 90° oblique: coherent glowing anomaly; the camera-facing
  core is round and vivid at the center (NOT flattened), with the funnel's
  three spiral arms reading as a vortex around it.
- `warp_intermediate.png` — diagonal: coherent glowing warp portal; the bright
  round core is the dominant, anchoring element (reviewer read: "a clean,
  effective energy portal"; the funnel's hourglass crossing is secondary).

Billboard mechanism proved in isolation (funnel hidden, `DrawCore`):
- `warp_core_headon.png`, `warp_core_side90.png`, `warp_core_intermediate.png`
  — the glow disc is a PERFECTLY ROUND, radially-symmetric circle at all three
  angles, including 90° side-on where a non-billboarded disc would flatten to a
  line. This is the definitive proof that the core always faces the camera.

## Deliverables committed
1. `WarpCoreMeshFactory` (new) + `WarpRenderer` billboard core + `WarpMeshFactory`
   rim brightening, committed on `master` (not pushed).
2. `WarpCoreMeshFactoryTests` (new) + updated `WarpMeshFactoryTests`.
3. `tools/warp-visual-proof/` (new reusable harness) + `AllegianceCore.slnx`
   registration.
4. This writeup + the 6 live screenshots in `.verify_p341/`.
