# DISPLAY_CONSTANT_RETUNE_P338 — retune display constants calibrated against the old (pre-P333) ship scale

## Relationship to prior stages
- **Depends on**: P333 (normalize ship model render scale to real `0.5 × HullType.Length`),
  P336 (normalize imported missile models to real native .mdl size), P337 (restore real Meshy
  ship textures). P333 changed ships from a uniform ~95–160-unit render radius to a real
  per-hull-correct size; this stage retunes the *consumers* of that new scale.
- **Confirms**: kimi-k3 review (`pi-3d-pipeline-review-kimik3.log`, section 2c) — "a large family
  of display constants was tuned against the accidental ~190-unit ship scale." Each item was
  verified against the current live-flown feel; no blanket multiplier was applied.

## Scope / exclusions
- In scope: retuning the display constants listed by the review that are genuinely coupled to
  ship render scale; live-verifying each.
- Out of scope (separate tasks): P333/P336 scale-normalization mechanism itself, texture
  restoration (P337 already landed), render-state-leak fixes, native reference source
  (`~/Development/Allegiance` — never touched).

## What changed, per constant (old → new, reasoning)

### 1. Chase camera `distanceBehind: 350f, heightAbove: 90f` — DERIVED FROM REAL RADIUS (radius-adaptive)
`Rendering/ChaseCamera.cs` + `Game1.cs`.
A single fixed offset physically cannot frame both a fighter (real radius ~6.6 in PCore001) and a
carrier (radius ~150) — the old comment ("so the whole ship fits in frame") only held at the old
uniform ~95-unit size. P338 makes the offsets proportional to the framed ship's REAL rendered
radius (= `0.5 × HullType.Length` post-P333), anchored to the old reference so a
reference-radius ship frames exactly as before:
```
distanceBehind = 350 × (shipRadius / 95)     (3.68 × radius)
heightAbove     =  90 × (shipRadius / 95)     (0.947 × radius)
```
`ChaseCamera.ReferenceRadius = 95` (the pre-P333 ~95-unit fallback radius). `Matrices(...)`
gains an optional `shipRadius` param (default = ReferenceRadius, so existing call sites and the
historic framing are unchanged). Game1 feeds the local ship's real radius (`_myRenderRadius`,
captured under the Gate as `0.5 × HullLength(myHullId)`) at every chase call site; the
external-turret path feeds the *carrier's* radius; 0/unknown degrades to scale 1 (reference
framing). New unit tests pin fighter/carrier equal angular framing (see Verification).

### 2. `ShipRadiusDefault = 50` in WarpTransitParticles / DamageDebrisParticles — DERIVED FROM REAL RADIUS
`Game1.cs`, `DamageDebrisParticles.cs`, `WarpTransitParticles.cs`.
Both pools already accept a `radius` argument; Game1 was feeding the fixed 50-unit stand-in.
P338 threads the ship's real rendered radius (`0.5 × HullType.Length`) into:
- the warp-transit teleport burst (local ship, `_myRenderRadius`);
- each damage-debris emitter (`DamageEmitter.Radius`), per damaged ship's own hull.
`ShipRadiusDefault` remains only as the documented fallback when the hull is unknown. This ends
the "recalibrate by hand every time base scale changes" cycle for the particle emitters, exactly
as the review suggested. Live-verified on carriers (radius 150) — the debris vents spread across
the real hull.

### 3–9. Reviewed and KEPT unchanged (verified against live feel / reasoning)
None of these are coupled to ship render scale; changing them would be wrong:
- **`WarpRenderer.VisualScaleMultiplier` (2.5×)** — a fixed sector-scale feature (wire radius
  100 → rim ~500). A gate bigger than a fighter (and ~1.7× a carrier) is correct — "many ships
  can cross." Live warp-vs-carrier shot confirms reasonable scale. No change.
- **`RadarBracketLayout.CloseRange/FarRange` (300/4000)** — radar icon distance-fade in WORLD
  units; ship positions and sector scales did not change with P333. P250's real-m2xspiderw tuning
  still holds. No change.
- **`MissileTrailRenderer.BillboardScale` (90f)** — trail sized from the *native missile radius*
  via `MissileTrailParticles` (unchanged by P333/P336); not a ship-size quantity. No change.
- **Bolt length clamps `Min/MaxBoltLength` (4/16)** — already retuned 32/480 → 4/16 in P248
  (pre-P333); projectiles are ship-independent. No change.
- **`CockpitCamera.forwardOffset` (14f)** — the local hull is not drawn in cockpit mode and bolts
  spawn at ship centre regardless of ship size; ship-independent. No change.
- **`ChaffRenderer.HalfExtentScale` (2f)** — sized relative to the chaff's OWN radius (native
  5–15), not the ship. No change.
- **`ProjectileNearCamClearance` (15f)** — projectile geometry paired with the (P243-thinned)
  bolt width; ship-independent. No change.

## Live proof (real MonoGame client, chase-camera capture mode)
Added `vizfight proof <webPort> [viewerHull]` (tools/vizfight) + `run_retune_proof.sh`, and a
`chase` camera flag to the client capture (`--capture ... [chase]`) so the proof frames exercise
the PRODUCTION `ChaseCamera` path (not the overview camera). Scenes use real Bot peers on one
side, real `SendBuyLoadout` launch, a real map warp (m2xspiderw), and damaged companions
(`SetFraction(0.40)` + gentle cruise so the low fraction is broadcast — P332 lesson). Captures
are under `~/.verify_p338/`:

- **Chase framing — small fighter** (`Interceptor`, real Length 13.22, radius 6.61):
  `framef/` — whole ship centered, well framed with margin (vision: "proportionally framed well,
  neither a tiny dot nor zoomed past the edges"). Objective: `myRad=6.61, eyeDist=25.14`.
- **Chase framing — large capital ship** (`Attack Carrier [SR]`, Length 300, radius 150):
  `framec/` — whole carrier framed with margin. Objective: `myRad=150, eyeDist=570.61`.
  Both ships subtend the SAME angle (radius/distance = 0.263 in both) — radius-adaptive framing
  verified; under the old fixed 350 offset the fighter would be a 1° speck and the carrier ~74%
  of the frame.
- **Warp gate next to a ship** (`warp/`) — the carrier framed in front of a real warp funnel;
  gate rim ~500 vs carrier length 300 (scale comparison; the gate dwarfs a fighter ~10×, correct).
- **Damage debris on hit ships** (`deb4/`) — two damaged carriers (fraction 0.40, 6 vents each,
  real radius 150) stream visible grey/white smoke plumes; vent spread follows the real hull.
- **Missile trail** — NOT live-producible: `PCore001.igc` contains no `DataPartTypeIGC.MissileLauncher`
  part, so no missile can be fired in this harness. Verified analytically (see #4 above) and kept
  unchanged. Documented honestly; no fabricated screenshot.

## Build / tests
- Whole-solution build `AllegianceCore.slnx -c Release`: **0 warnings, 0 errors** (final).
- New unit tests (`ChaseCameraOrientationTests`, +3): 12/12 pass in isolation.
- Full suite run 1: Lobby 12/12, MonoGame 609/609, ClientCore 162/162, Core.Tests 3261 passed / 8 failed.
- Full suite run 2: Lobby 12/12, MonoGame 609/609, ClientCore 162/162, Core.Tests 3262 passed / 7 failed.
  All Core.Tests failures are Live*-network wire tests (InboundFloodSafety, StationsUpdate, ShipsUpdate,
  LiveWinCondition, LiveTurretGunner, LiveTreasurePickup, CloakVisibilityGate) in `Allegiance.Core.Tests` —
  the documented parallel-contention/ephemeral-port flakes. The failure COUNT varies 8→7 between two
  identical runs, the signature of environmental contention; each verified PASS in isolation (11/11). None
  of the changed files touch server wire logic (all changes are in the MonoGame client), so these are not
  regressions. MonoGame.Tests — the project actually containing every changed line — is fully green both runs.

## Files
- `src/Allegiance.ClientCore.MonoGame/Rendering/ChaseCamera.cs` (radius-adaptive framing)
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` (real-radius feeding; chase-capture mode; `_myRenderRadius`)
- `src/Allegiance.ClientCore.MonoGame/Program.cs` (chase capture flag)
- `src/Allegiance.ClientCore.MonoGame/Rendering/DamageDebrisParticles.cs` (ShipRadiusDefault → fallback)
- `src/Allegiance.ClientCore.MonoGame/Rendering/WarpTransitParticles.cs` (ShipRadiusDefault → fallback)
- `tests/Allegiance.ClientCore.MonoGame.Tests/ChaseCameraOrientationTests.cs` (+3 tests)
- `tools/vizfight/Program.cs` (proof scenario mode) + `tools/vizfight/run_retune_proof.sh` (launcher)
