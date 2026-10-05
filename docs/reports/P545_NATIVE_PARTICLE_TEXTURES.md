# P545 — replace the port's generated white soft-blob particles with native's real effect textures and colours

**Status:** implemented, tested, proof-measured, committed on `master` (not pushed).
**Date:** 2026-10-05.
**Problem (Domingo, 2026-10-04):** dense 15v15 combat and missile launches collapse into a
large white patch that hides the actual impacts. Eight port renderers drew a **procedurally
generated white soft blob** (`GenerateSoftSprite`, a white disc `(1,1,1,a)`) with **additive**
blending; stacked where fire concentrates they summed to solid white.

## Root cause (verified against native source)

The port's assumption was that the white comes from additive blending. It does **not** — native's
particle decals are *also* additive. The real difference is the **texture and the tint**:

- Native `Context::DrawDecal` defaults to `BlendModeAdd` (`engine/context.h:929`), implemented as
  SRCALPHA/ONE in `engine/RasterD3D9.cpp:295-302`. Smoke, fire, spark and glow decals are all
  additive.
- But native tints the **real grey/brown artwork** with a **dim colour**, so additive stacking
  converges to grey, never white. E.g. `SmokeBehavior` (`particlegeo.cpp:45-49`) tints
  `e_smokebmp` by `RGBA(0.25c, 0.2c, 0.25c, 0.25c)` — max colour 0.25, max alpha 0.25. Each
  additive smoke puff contributes `tex · 0.0625` at birth; many puffs stay grey.
- The port drew a **white** sprite (RGB 1) with full-bright colours and a 90× size inflation, so
  overlapping puffs summed past 1.0 → white.

So the fix is *native parity*: use the real textures + native dim colours/alpha/sizes, keeping
the (correct, native) additive blend. **Smoke stays additive** — the task's conditional
(“if native says not additive”) is resolved *against* a blend change; native is additive.

## What changed

**Shared infrastructure (new files in `src/Allegiance.ClientCore.Game/Rendering/`):**

- `NativeEffectTextures.cs` — the shared native-effect-texture cache (load/decode once per stem,
  reused by every renderer). Resolves both binary RGB565 `.mdl` strips (via `MdlTextureDecoder`)
  and the `e_smoke`/`e_fire`/`e_tele` **PNG-script** `.mdl`s (`ImageFromFile("….png", true)`) to
  the referenced PNG. Missing/undecodable art → logged once, `null`, renderer falls back to the
  soft sprite. Has pure, GPU-free `Resolve()`/`IsPngScript`/`ExtractPngReference`/`SliceFrameCount`
  so the mapping is unit-testable headless.
- `NativeParticleCurves.cs` — pure, MonoGame-free ports of the native Smoke/Fire/Spark colour and
  size curves (`particlegeo.cpp`).
- `ParticleBillboardBatcher.cs` — the shared batched billboard drawer (real texture, additive,
  optional screen-plane rotation, oriented patches, axis-spanning decals) + state hygiene
  (blend/depth-read-no-write/rasterizer restored — the P339/P525/P529 lesson).

**Renderer table (before → after):**

| Renderer | Before (white blob) | After (native art + curve) | Native citation |
|---|---|---|---|
| `MissileTrailRenderer` | white soft disc, 90×, bright colours | real `e_firebmp`/`e_smokebmp` at native size, native Fire/Smoke curves | `particlegeo.cpp:24-129`, `missileigc.cpp:53-55` |
| `ShipEngineTrailRenderer` | white soft disc, bright colours | real `e_firebmp`/`e_smokebmp`, native curves | `particlegeo.cpp:24-129` |
| `DamageDebrisRenderer` | white soft disc | real `e_smokebmp`, native dim grey (0.25c) | `thinggeo.cpp:699-760` → SmokeBehavior |
| `WarpTransitParticleRenderer` | white soft disc, hot blue | real `e_telebmp`, native Spark hot-blue | `particlegeo.cpp:209-250` |
| `ShieldFlareRenderer` | white soft disc, ice-blue | **animated `fx09bmp`** (9 frames) as a patch **tangent on the shield** at the hit, 0.75 s | `trekigc.cpp:1624-1645`, `artwork.h:209`, `thinggeo.cpp:1290-1301` |
| `HullHitSparkRenderer` | 2 additive white blobs at radius/2 | (a) `fx11bmp`+`f103bmp` surface flash grown 0.1→2.0 over ~0.19 s, hull-aligned; (b) 2 debris bits as **lit `utl10` geometry** | `thinggeo.cpp:1322-1340`, `efgeo.cpp:655-755` (HullHitGeo), `efgeo.cpp:795-846` (BitsGeo) |
| `EngineNozzleGlowRenderer` | white soft disc | real `f101bmp` | `thinggeo.cpp:1419-1443`, `efart.h:12` |
| `NavStrobeRenderer` | white soft disc | real `f101bmp` | `efapp.cpp:171-222`, `efart.h:14` |

- `GenerateSoftSprite` is **removed** from every renderer; the only remaining white-blob use is
  `NativeEffectTextures.SoftFallback` (procedural, logged once per missing stem — scope item 6).
- The trail **line** (`ShipMotionTrailRenderer`, `trailbmp` `TrailGeo efgeo.cpp:524`) is a separate
  ribbion renderer, unchanged — the effect *particles* are what caused the white patch.

## Native colour/size curves (ported)

- **Smoke** (`SmokeBehavior`): `RGBA = (0.25c, 0.2c, 0.25c, 0.25c)`, `c = countdown`;
  size `0.75 + 2.25·(1−c)` (0.75→3.0×).
- **Fire** (`FireBehavior`): 10% ramp-up white `(1,1,1,0.5)`, grows 0.5→1.0× (exp ⅔); then
  yellow→red `(1, c, 0.25c, 0.5c²)`, shrinks 1.0→0.5×.
- **Spark** (`SparkBehavior`): hot blue `(1.5k, 2k, c, k)`, `k = ½c²`.
- Verified headless by `NativeParticleCurvesTests`.

## Tests (failing-first)

`tests/Allegiance.ClientCore.MonoGame.Tests/`:
- `NativeParticleCurvesTests.cs` — smoke colour at c=1 is exactly `(0.25, 0.2, 0.25, 0.25)` and
  fades to 0; smoke size 0.75→3.0×; fire ramp-up (white, 0.5→1.0) and ramp-down (yellow→red,
  shrink); spark hot-blue.
- `NativeEffectTextureResolutionTests.cs` — every renderer's stem resolves to the real native
  file (`e_smokebmp.mdl`, `e_firebmp.mdl`, `fx09bmp.mdl`, `fx11bmp.mdl`, `f103bmp.mdl`,
  `f101bmp.mdl`); `e_smoke`/`e_fire`/`e_tele` are PNG scripts resolving to their `.png`;
  `fx09` is a 9-frame strip; missing/junk art → `None` (the logged soft-sprite fallback).
- `HullHitSparkParticlesTests.cs` (added) — each hit now also spawns one surface flash with a
  valid tangent frame, expiring after the fast 0.2 s life while the 2-s bits survive.

Unit run on the final code: **70 passed, 0 failed** for the particle/pool suites.
Full-suite numbers are amended below.

## Headless visual proof (`roster-capture particles`, offscreen)

Measures the **blown-out-pixel share** (all channels ≥ 250) in each scene, old (pre-P545 white
blob) vs new (native textures). Contact sheets in `docs/reports/p545/`.

| Scene | old | new |
|---|---|---|
| missile launch t=0.0 s | 10.34% | 0.00% |
| missile launch t=0.1 s | 100.00% | 0.01% |
| missile launch t=0.3 s | 100.00% | 0.01% |
| missile launch t=0.6 s | 78.05% | 0.00% |
| **volley (4 missiles, chase view)** t=0.2 s | 100.00% | 0.00% |
| volley t=0.5 s | 77.44% | 0.00% |
| volley t=1.0 s | 36.11% | 0.00% |
| **dense combat (10 ships)** | 12.25% | 0.01% |
| **asteroid hits (rock r=60)** | 96.43% | 0.04% |

Vision-checked: the old frames are solid-white/washed out (volley t=0.2 = 100% blown — the exact
“fills the view and makes flying difficult” symptom); the new frames show distinct dim grey smoke,
orange fire, blue shield flares and lit debris chunks, no washout.

## Build

`dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors**.

## Suite (3 consecutive runs on the final code, assemblies sequential)

Measured on the final code via `scripts/run-tests.sh` methodology (one test
assembly at a time — the P471 anti-flake runner), foreground, 3 consecutive
runs of each assembly:

| Assembly | run 1 | run 2 | run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/0 | 12/0 | 12/0 |
| Allegiance.ClientCore.MonoGame.Tests | 1292/0 | 1292/0 | 1292/0 |
| Allegiance.ClientCore.Tests | 472/2 | 471/3 | 472/2 |
| Allegiance.Core.Tests | 3731/3 | 3731/3 | 3729/5 |

**Every** failure is a live-wire / load-flake in the `TEST_FLAKES.md` ledger
(e.g. `FullSessionKillIntegrationTests…CrossSideKill`,
`ClientWarpApproachLiveTests…two_hops_to_target`,
`LiveRipcordTests…flying_in_sector`, `LoadoutChangeLiveTests…passenger_change`,
`LiveTeamLobbyTests…`). All the named failures **pass when run in isolation**
(verified: ClientCore 2/2, Core 13/13), i.e. they are the documented
cross-contention live-socket timing flakes, **not** regressions. P545 touches
only rendering/particle code, which these server-timing tests never exercise;
the non-live unit suites (Lobby, MonoGame incl. all P545 tests) are **3/3
green**.

## Files

New: `NativeEffectTextures.cs`, `NativeParticleCurves.cs`, `ParticleBillboardBatcher.cs`,
`NativeParticleCurvesTests.cs`, `NativeEffectTextureResolutionTests.cs`,
`tools/roster-capture/ParticlesProof.cs`, `docs/reports/p545/*.jpg`, this report.
Modified: the 8 renderers, `HullHitSparkParticles.cs`, `Game1.{cs,Lifecycle.cs,Rendering.cs}`,
4 proof tools (constructor wiring), `tests/.../HullHitSparkParticlesTests.cs`.
