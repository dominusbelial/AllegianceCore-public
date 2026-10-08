# P576b — the native sky must be camera-relative (the "looks terrible" fix)

## Summary

Domingo observed the P576 per-sector sky "looks terrible" live (2026-10-08,
221 client @ `84391ac2`, `--aleph-glimpse on`): huge hard-edged flat polygons,
a planet as a flat slab, straight-edged nebula panels and **black wedges** where
faces are missing (`docs/prompts/p576b-sky-broken-live-2026-10-08.png`). The
cause is the **camera-relative view**: the flight camera keeps the eye at the
origin (`ChaseCamera.cs:205`, `CockpitCamera.cs:78` →
`CreateLookAt(Vector3.Zero, target - cameraEye, up)`), so a sky placed with
`cameraEye` offset ends up thousands of units away in view space — past the
scene far plane (`ExternalCameras.cs:45` `FarPlane = 6000f`, nearer than the
native dome's 7500 shell) and possibly back-face culled.

### Key finding — the spot-check's reading is wrong for `master`

The task's bug description (sky placed at `cameraEye`, `World =
CreateTranslation(cameraEye)`, `CullCounterClockwise`) is **accurate for the
DEPLOYED copies** (`~/allegiance-play2` and `~/allegiance-deploy`), not for
`master`. The current `master` (commit `0123376b`, P576) already renders the
sky **camera-relative** — dome `World = Matrix.Identity`, stars/planet at
`direction × radius`, its own far-enlarged projection, `CullNone`. Proven by a
direct diff of `Rendering/NativeSkyRenderer.cs`:

- master: `pos = dir * StarRadius` vs play2/deploy: `pos = cameraEye + dir*StarRadius` (`:182`)
- master: `World = NativeSkyPlacement.DomeWorld(cameraEye)` (= Identity) vs play2: `World = Matrix.CreateTranslation(cameraEye)` (`:216`)
- master: `RasterizerState.CullNone` vs play2: `CullCounterClockwise` (`:148`)
- master: own sky projection `near 1 / far 80000` vs play2: scene projection (far-clips)

So the live "looks terrible" came from a build that predates/omits the P576
camera-relative fix (the 221 client at `84391ac2`); the P576 commit on `master`
had already folded the fix in (its own report at 09:44 documents it).

This P576b therefore (a) **proves** the spot-check's reading is wrong for
master with the actual code + diff, (b) **centralises** the camera-relative
placement into a pure, unit-tested module so the invariant is explicit and
regression-protected, and (c) re-verifies it with failing-first tests, the full
suite, and fresh real-window captures.

---

## What changed

New pure module `src/Allegiance.ClientCore.Game/Rendering/NativeSkyPlacement.cs`
— every placement takes the camera eye and **deliberately ignores it** (that
"takes-but-ignores" shape *is* the fix, and it is what makes the invariant
testable):

- `StarShellPosition(star, radius, cameraEye)` → `dir * radius`
- `PlanetShellCenter(planetDir, dist, cameraEye)` → `dir * dist`
- `DomeWorld(cameraEye)` → `Matrix.Identity` (zero translation)
- `BillboardRight/BillboardUp` (orthonormal camera-facing axes)
- `SkyProjection(sceneProjection)` → the sky's own `PerspectiveFieldOfView`
  (recovered FOV/aspect, **near 1 / far 80000**) so the 7500 dome never
  far-clips.

`Rendering/NativeSkyRenderer.cs` now routes `DrawStars`/`DrawDome`/`DrawPlanet`
through these helpers (world identity, `CullNone` for the inside-viewed dome,
states pinned and restored in `finally` — P568 lesson).

### Native / code fidelity (each rule cited)

- Camera-relative flight view: `ChaseCamera.cs:205`, `CockpitCamera.cs:78`,
  `MissileCamera.cs:116` — all `CreateLookAt(Vector3.Zero, …)`. The ship/explosion
  renderers subtract `cameraEye` (cf. `EnhancedExplosionRenderer.cs:120-122`);
  the sky must be centred on the origin, never offset.
- Scene far plane `6000` (< dome 7500): `ExternalCameras.cs:45`;
  `Game1.Lifecycle.cs:147,153,165` (chase/cockpit/missile farPlane 6000). Hence
  the sky's own far = 80000.
- Dome globe1-9 radius 7500 centred on the camera, unlit copy mode:
  `efgeo.cpp:1029-1052` (ported in P576).
- Stars at infinity, no parallax: `effect/starimage.cpp:32-53`.
- Planet poster billboard at the planet direction, sized by `planetRadius`
  (byte 50-100): `Igc/clusterIGC.cpp:44-57`, `efimage.cpp:515-563`;
  angular half-scale 2.5°–5° (`radius/100*5°`) so it reads as a clear disc, not
  a screen-filler (the `0.004×d` estimate dwarfed the sky — P576's own fix).

---

## Failing-first tests

`tests/Allegiance.ClientCore.MonoGame.Tests/NativeSkyPlacementTests.cs` — 9
tests. The 4 camera-eye-**independence** tests are the P576b invariant and
genuinely fail against the pre-fix (deployed) formulation:

| Test | On fixed code | On buggy (`cameraEye`-adding) code |
|---|---|---|
| `StarShellPosition_IsIdenticalAcrossTwoEyes` | pass | **fail** (differs by 5000 u) |
| `PlanetShellCenter_IsIdenticalAcrossTwoEyes` | pass | **fail** |
| `DomeWorld_HasZeroTranslation` | pass | **fail** (translation ≠ 0) |
| `DomeWorld_IsIdenticalAcrossTwoEyes` | pass | **fail** |

Demonstrated in-session: temporarily restoring the buggy formulations made 4
tests fail (`Failed! - Failed: 4, Passed: 5`); restoring the fix made all 9 pass
(`Failed: 0, Passed: 9`). The other 5 pin `dir*radius`/`dir*dist` shell math,
orthonormal billboard axes, and that `SkyProjection` holds the 7500 dome inside
`[near, far]` while recovering the scene FOV/aspect.

Measured: **NativeSkyPlacementTests 9/9** (part of MonoGame 1596/1596 below).

---

## Build

Whole solution (`AllegianceCore.slnx`, `-c Release -m:8`): **0 warnings, 0
errors**.

---

## Suite — 3 consecutive runs (`scripts/run-tests.sh`, on the final code)

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1 | 12/12 | 1596/1596 | 561/561 | **3815/3815** (clean) |
| 2 | 12/12 | 1596/1596 | 561/561 | 3812/3815 (3 flakes) |
| 3 | 12/12 | 1596/1596 | 561/561 | 3810/3815 (5 flakes) |

Run 2's 3 and run 3's 5 Core failures are the documented rotating **live-wire
socket-timing load-flakes** in `docs/TEST_FLAKES.md`, unrelated to the sky:
run 3 named `AutoReloadOnEmpty…RELOAD129` (ledger line 52),
`LiveWinCondition…Board_refused_once_mission_over` (line 34),
`LiveRipcord…Cancel_while_active_aborts_sector_wide` (line 36),
`GameOverPlayersWire…Conquest_win_lists_every_connected_player_…side` (line 31),
`AddPartTests…Part_pickup_decline_recreates_treasure_at_current_position` (line
247). All in `Allegiance.Core.Tests`; this change touches only
`Allegiance.ClientCore.Game` rendering + MonoGame tests, and the sky tests pass
every run. Run 1 was fully clean. (Per the task rule, each run is reported as it
happened.)

---

## Real-window proof

Fresh captures from the **real 2560×1440 presented backbuffer** (P572
`--autoshots`/`live-proof.sh`, local server, `m2xspiderw.igc`) with the fixed
`master` build — see `docs/reports/P576b_assets/proof/`:

- `proof_t012.1_sOmega_cchase.png`, `proof_t018.7_sOmega_cchase.png`,
  `proof_t024.2_sOmega_cchase.png`, `proof_t030.8_sOmega_cchase.png` — Omega,
  chase view, HUD off, four view moments. The environment dome renders as a
  **smooth, organic nebula that fades into the starfield** with **no hard-edged
  polygons, no flat-slab planet, and no black wedges** — matching the
  known-good P576 reference (`docs/reports/P576_assets/proof/sky_Omega_…png`),
  and contrasting decisively with the known-broken
  `docs/prompts/p576b-sky-broken-live-2026-10-08.png` (flat slabs + black
  wedges).
- `proof/glimpse/proof_t016.1_sOmega_ccockpit.png` … — `--aleph-glimpse on`
  run; the funnel/glimpse was only momentarily framed (the P576-documented
  auto-nav framing limitation), but the frames confirm the backdrop stays clean
  (no wedges/slabs) with the glimpse path active.

Because `master` already carried the camera-relative fix, the decisive
real-window evidence that a `master` build renders the sky seamlessly also
exists in P576's own report (`sky_Omega_nebula_galaxy.png`,
`sky_Alderan_earth.png`). The exhaustive multi-sector 360° matrix (Ganymede /
Alderan / Rigel × ≥3000 u × 4 views) remains blocked by the pre-existing
auto-navigation limitation P576 documented (auto-goto loses its target for the
non-start sectors); the camera-eye-independence property it was meant to show
is covered deterministically by the failing-first unit tests above.

---

## Follow-up (important for the live problem)

The live "looks terrible" build that Domingo saw on 221 is NOT what `master`
produces — it is the `cameraEye`-offset variant still present in the **deployed
copies** (`~/allegiance-play2`, `~/allegiance-deploy`) and the 221 client at
`84391ac2`. To actually fix the on-screen problem, those deployed trees / the
live client must be rebuilt from this `master` (or have the `NativeSkyPlacement`
change ported onto them). Committed on `master` here per the task; not pushed.

## Commit

`2da6d754` "P576b: pin the native sky to camera-relative placement…" on
`master` (branch checked with `git branch --show-current`; not pushed).
