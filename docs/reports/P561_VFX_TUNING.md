# P561 — VFX tuning pass on P546/P547: size, plume, chase-view occlusion, density

Branch: `master`. Enhanced-path-only tuning of the P546/P547 VFX (native path
untouched). Addresses Domingo's live-on-221 notes ("smoke so heavy they block the
external view; size doesn't match the ships; looks flat / like lots of ovals from
far"), prioritizing, per his 2026-10-06 update: **size first**, then no-discrete-
ovals, then own-ship chase-view visibility, then density.

Three commits-of-record folded here as one P561: the code+tests, then the 3× suite
numbers (amended). Committed on `master`, not pushed.

---

## What was wrong (measured in code)

| Complaint | Root cause (P546) |
|---|---|
| Oversized smoke | Enhanced trail half = `p.Size · 2.4 · (0.5 + 2.2(1−rem))` ≈ **3.2–4.3× native** (native = `p.Size·0.5·SmokeSizeScale`). Explosion smoke half = `radius·0.30·(1+2.2(1−rem))` ≈ up to **0.96× the blast radius** — a puff as big as the whole explosion. |
| Dense/opaque | `PeakAlpha 0.65`, `MaxSmokePerExplosion 14`, `Linger 1.2s`, `MaxSmokeOnScreen 320`. |
| Blocks the chase view | `ParticleNearFade.Flight (6,45)` only fades smoke within 45 u of the eye; the own ship sits ~30–76 u ahead, so its own engine/missile/damage smoke sits between camera and ship and blankets it. |
| Flat / hard oval rim | Each puff is one camera-facing quad; the CC0 smoke frames are filled to the cell edge (measured: Cloud01 rim alpha ~0.78 mean, **38% of pixels > 0.9 alpha**), drawn as flat cards with a hard elliptical/rectangular rim. |

## The fixes (all Enhanced-path only; native stays byte-identical)

1. **Native-derived size (≤ 1.5× native).** New pure helper
   `Rendering/EnhancedSmokeSizing.cs`:
   - `NativeTrailHalf(pSize, rem) = pSize · 0.5 · NativeParticleCurves.SmokeSizeScale(rem)`
     (the size native actually renders — native `SmokeBehavior::Render`,
     `particlegeo.cpp:51-54`).
   - `EnhancedTrailHalf = NativeTrailHalf · TrailScaleNominal(1.25) · jitter[0.92,1.028]`,
     so the **worst case ≤ 1.285× native < the 1.5 cap** (`TrailMaxScale`). The
     P546 path was 3.2–4.3× — a ~2.7–3.4× size cut.
   - Explosion smoke `ExplosionSmokeHalf = radius · ExplosionSmokeBase(0.053) ·
     NativeParticleCurves.SmokeSizeScale(rem)` → **≤ 0.175× the blast radius**
     (native `AddExplosion`, `trekigc.cpp:95-177`, has no smoke; the reference is
     the fireball `radiusExplosion`). P546 was up to 0.96×.
   - Applies to missile (`MissileTrailRenderer.cs`), engine (`ShipEngineTrailRenderer.cs`),
     damage (`DamageDebrisRenderer.cs`) and explosion (`EnhancedExplosionRenderer.cs`) smoke.

2. **No discrete ovals.** `TrailSubPuffs = 3` overlapping sub-puffs per native
   trail particle (`EnhancedSmokeSizing.SplatTrailPuffs`), each smaller
   (`TrailSubScale 0.68`) with per-puff random offset/rotation/jitter/life →
   a continuous plume, not beads. Per-puff **slow spin over life**
   (`EnhancedSmokeField.SpinMaxRadians 1.4`), **per-puff random frame offset**,
   **size jitter**, and **feathered sprite rims**: `VfxAssets.RadialFeather`
   multiplies each smoke-frame pixel's RGB+alpha by a radial smoothstep (1 inside
   ~72%, 0 at the edge) — RGB scaled with alpha so premultiplied blending stays
   correct. Distance-aware fade: `ParticleFarFade (650→2600 u, floor 0.35 alpha /
   0.75 size)` so a distant trail reads as a thin line, not a string of ovals.

3. **Chase-view own-ship visibility.** New `ChaseOccluder` (pure): projects the
   own ship's centre + right/up radius to NDC, builds an inflated screen ellipse,
   and `AlphaFactor` fades any puff that is **nearer than the ship** and **inside
   the ellipse** (smoothstep core→edge). Wired in `Game1.Rendering.cs` per frame,
   active only in the non-cockpit modes; the near-fade is now camera-mode aware
   (`ParticleNearFade.Chase (3,20)` vs cockpit `Flight (6,45)`), so the chase view
   keeps the ship's plume visible while the occluder prevents it covering the hull.

4. **Density/opacity.** `PeakAlpha 0.65 → 0.40` (so the extra sub-puffs keep the
   cumulative screen alpha inside the ≤~15% blown-out budget), `MaxSmokePerExplosion
   14 → 10`, `LingerExtraSeconds 1.2 → 0.8`.

Scope 4 (higher-res 256 px atlases / multi-decal fireballs) was **optional this
run** and intentionally left out (VRAM is already fine at 9.89 MiB; the feather +
subdivision + far-fade addressed the visual complaint without a content re-extract).
Scope 5 (soft particles ON, P559) is preserved — `--soft`, `--bloom`, the Enhanced/
Native switch and the P546/P547 tests all still pass.

## Size sheet (next to an Adv Fighter & a Scout, same distance)

Engine-trail smoke half-size as a multiple of the ship's **radius**
(`p.Size = radius·0.5`), birth → mid-life → end. Representative radii: fighter 7.0 u,
scout 5.5 u (length = 2·radius, the port's `radius = 0.5·HullType.Length`).

| Life `rem` | Native half/r | Enhanced max half/r | **P546 half/r** |
|---|---|---|---|
| 1.00 (birth) | 0.188 | 0.241 | 0.600 |
| 0.50 | 0.469 | 0.602 | 1.920 |
| 0.00 (end) | 0.750 | **0.964** | **3.240** |

So the P546 end-of-life puff was **3.24× the ship's radius** (≈6.5× its diameter);
P561 tops out at **≤0.96× radius (~1.9× diameter)** — a puff now comparable to the
ship, matching native proportions (always ≤ 1.285× native).

Metres (fighter radius 7.0): engine-smoke half at birth/mid/end = 1.69 / 4.22 / 6.75 u
(old 4.20 / 13.44 / 22.68 u). Missile-trail smoke (p.Size 0.45): ≤ 0.87 u (old 2.92 u).

Explosion smoke vs the exploding hull (fireball drawn at `radiusExplosion`):

| Blast | Explosion radius | max smoke half | fireball half | vs hull (~fighter r 7) |
|---|---|---|---|---|
| SmallShip | 35 | **6.1 u** (≤0.175×) | 13–19 u | ~0.87× fighter radius |
| BigShip | 165 | **28.9 u** (≤0.175×) | 63–89 u | – |

P546 explosion smoke was up to 0.96× the blast radius (33.6 u for a SmallShip);
P561 keeps it ≤ 0.175× — a plume that hugs the hull instead of swallowing it.

## Native fidelity (cite native file:line for every rule)

- Trail native half — `SmokeBehavior::Render`, `particlegeo.cpp:51-54` (`fSize =
  m_fSize·(0.75 + 2.25·(1−countdown))`), ported already in `NativeParticleCurves.SmokeSizeScale`.
- Enhanced trail cap ≤ 1.5× native — P561 scope 1; worst case measured 1.285×.
- Explosion radius reference — `WinTrekClient::AddExplosion`, `trekigc.cpp:95-177`
  (radiusExplosion values mirrored in `ExplosionPresets`); native has **no** explosion
  smoke, so the fireball radius is the native size reference (stated in the code).
- Trail emitter sizes — `thinggeo.cpp:758-917` (`AddThrustParticles` rocket case),
  missile `0.25 + radius·0.1` (`missileigc.cpp:53`), already in the particle pools
  (`ShipEngineTrailParticles.cs`, `MissileTrailParticles.cs`) — unchanged here.
- Near/camera fade re-tuned per mode — native camera enum `WinTrek/Treki.h:108-126`;
  `InternalCamera` (cockpit) vs external, `Treki.h:145`.

Spot-checks of the task's native reading were **confirmed**: the enhanced path was
indeed ~3.2–4.3× native (measured 4.32× at rem=0 above), and the CC0 smoke frames do
have hard rims (measured 38% > 0.9 alpha) — so both the size cap and the feather were
justified by measurement, not assumption.

## Failing-first tests

`tests/Allegiance.ClientCore.MonoGame.Tests/P561VfxTuningTests.cs` (8 tests). Ran red
**on the P546 code** (5 failed / 3 passed), then green after the fix (8/8):

| Test | Red reason (P546) |
|---|---|
| `Enhanced_trail_smoke_never_exceeds_1_5x_native` | `enhanced 12.96 > 1.5× native 3.000` |
| `Enhanced_trail_half_jitter_stays_under_cap` | nominal×jitter exceeded the cap |
| `Explosion_smoke_stays_a_small_fraction_of_the_blast` | `type 1: > 0.2×radius 25` |
| `Enhanced_smoke_opacity_and_density_knobs_are_lowered` | `PeakAlpha 0.65 not lowered` |
| `Trail_subdivision_produces_more_but_smaller_puffs` | no subdivision on P546 |
| `FarFade_reduces_alpha_and_size_with_distance_only_far_away` | no far fade on P546 (passed once added) |
| `ChaseOccluder_fades_smoke_between_camera_and_own_ship` | no occluder on P546 (passed once added) |
| `ChaseOccluder_inactive_when_ship_behind_camera` | n/a (new feature) |

The explosion-size test also caught a real off-by-margin bug in my first constant
(0.061 pushed the max puff to 0.201×radius > the 0.2 gate) → corrected to 0.053 (0.175×).

Existing VFX coverage re-run green: **128/128** (EnhancedVfx, NativeParticleCurves,
Missile/ShipEngineTrail, DamageDebris, Explosion, P547, P561 subsets).

## Whole-solution build

`dotnet build -m:8` → **0 warnings, 0 errors**. Includes the roster-capture proof
tool (`VfxProof546/547`) updated to the new `EnhancedSmokeField.Draw` / explosion
`Draw` signatures (it exercises the real game renderers).

## Metrics & proofs

Measured this session: size-parity invariants (above), failing-first red→green,
0-warn/0-err build. The **own-ship-visibility %, blown-out % and the real-window
2560×1440 chase/volley/damaged-ship screenshots** (task's "Proof" section) require
the live client harness (P572 `--auto-*` + a running server + a scripted 4-missile
volley / damaged-ship scenario with soft ON). **Not executed this session** — the
code, tests, build and size sheet above are complete and measured; the live-window
visual capture is the remaining validation step and is called out here rather than
fabricated. `tools/roster-capture` (P546/P547) is the intended instrument for the
dense-combat blown-out percentage and the far-distance plume frames.

### Full suite, 3× (sequential per-assembly, `scripts/run-tests.sh` methodology)

The single-invocation `run-tests.sh` exceeds this box's 420 s foreground timeout
mid-suite (the P529/P542 failure mode — a run died after MonoGame), so each of the
four assemblies is executed in sequence as its own invocation, which preserves the
script's whole point (only one assembly's live TCP sockets tick at a time).
`scripts/run-tests.sh` and `dotnet test` per-assembly are equivalent for isolating
the cross-assembly contention the ledger describes. Numbers as they happened:

| run | Lobby | MonoGame (this change) | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1406/1406 | 544/545 (1 flake; clean 545/545 on rerun) | 3768/3773 (5 live-wire) |
| 2 | 12/12 | 1406/1406 | 542/545 (3 flakes) | 3771/3773 (2) |
| 3 | 12/12 | 1406/1406 | 543/545 (2 flakes) | 3769/3773 (4) |

- **MonoGame — the assembly this change is in — passed 1406/1406 in all three
  runs**, including the 8 new `P561VfxTuningTests` and all pre-existing VFX tests.
- Every Core/ClientCore failure is the documented live-wire load-flake class
  (`docs/TEST_FLAKES.md`: `GameOverPlayersWire…Conquest_win…leak`, `LiveRipcord`,
  `AddPartTests`, `ShipDeleteGone…`, `LiveWinCondition Board_refused`,
  `AutoReloadOnEmpty`, `ShipAsteroidCollision`, `WarpBombTests`, cross-side
  visibility, treasure pickup, cargo reload…) — none touch rendering; ClientCore
  also ran fully clean once (545/545). No regression attributable to P561.

## Files

- `src/Allegiance.ClientCore.Game/Rendering/EnhancedSmokeSizing.cs` (new) — size/far-fade/occluder math.
- `src/Allegiance.ClientCore.Game/Rendering/EnhancedSmokeField.cs` — richer `Puff`, far-fade, occluder, per-puff spin/frame/jitter.
- `src/Allegiance.ClientCore.Game/Rendering/EnhancedVfx.cs` — `ParticleNearFade.Chase`.
- `src/Allegiance.ClientCore.Game/Rendering/MissileTrailRenderer.cs`, `ShipEngineTrailRenderer.cs`, `DamageDebrisRenderer.cs` — native-derived sub-puff smoke, new params.
- `src/Allegiance.ClientCore.Game/Rendering/EnhancedExplosionRenderer.cs` — smoke size from native radius, new params, knob changes.
- `src/Allegiance.ClientCore.Game/Rendering/VfxAssets.cs` — radial feather of the three smoke atlases.
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — per-frame visibility bundle, camera-mode near fade, own-ship occluder.
- `tools/roster-capture/VfxProof546.cs`, `VfxProof547.cs` — updated to new `Draw` signatures.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P561VfxTuningTests.cs` (new).
