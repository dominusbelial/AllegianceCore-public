# P582b — Volumetric black world (real cause) + volumetric/displaced timing

**Branch:** `master` (committed locally as `a4107e4f` final; amended with suite
numbers; **not pushed**).
**Baseline:** P582 `0566e088` (depth-RT feedback fix + displaced erosion) on top
of P579 `b4139993`.
**GPU/client:** real window 2560×1440, Intel Arrow Lake-S / Mesa iris
(the Volumetric default tier). Follows the P582 report; build on its tooling.

---

## 0. Outcome in one paragraph

Two things, both with failing-first tests, a whole-solution 0-warning/0-error
build and a 3× full-suite run on the final code:

1. **The black world's REAL root cause is fixed.** P582 fixed the depth-RT
   feedback loop but admitted it never reproduced the black-out and that its
   mechanism was unconfirmed. The true cause (found 2026-10-07 22:10): P579's
   volumetric **half-res pass** does `SetRenderTarget(halfRes) … draw …
   SetRenderTarget(null)` **mid-frame** after the opaque scene is drawn; with
   MonoGame's `DiscardContents` backbuffer that switch-and-restore **wipes the
   opaque world** (sky/asteroids/stations vanish; ships/effects/HUD drawn later
   survive) — the exact P547/P559 failure reintroduced by P579. Fix (option b,
   proven live by Domingo): keep the colour target fixed by restoring the
   **actual previous targets** (`GetRenderTargets()`, not `null`) and making
   every mid-frame colour target `PreserveContents` (backbuffer via
   `PreparingDeviceSettings`, the SceneRT bloom target, and the capture target).
   A DEBUG trip-wire (`RenderTargetGuard`) fires if a volatile colour-target
   excursion is ever not lossless. A/B reproduced the bug and proved the cure
   in the real window.
2. **Volumetric fireball timing compressed to native film cadence.** Both
   volumetric shaders grew AND cooled across the FULL fire+smoke-tail life
   (`tn = age/life` with life ≈ film + 2.4 s ≈ 3.9 s), so a fighter fireball
   peaked only near life-end and "played too slowly, out of sync" with the
   death. New `VolumetricTiming` model runs growth/cool on the **film clock**
   (peak at 28 % of film ≈ 0.42 s for a fighter; cool across the film), mirrored
   in `Volumetric.fx` and `DisplacedSphere.fx`; displaced's P582 erosion keeps
   the full life for the smoke-tail surface. Plus shader/render-target
   **pre-warm** at load so the first real explosion has no hitch.

---

## 1. Bug 1 — the black world: real cause + fix

### Root cause (Claude 2026-10-07 22:10; P582's report didn't reproduce it)
P579 added the half-res near+large volume pass in `VolumetricRenderer.Draw`
(~265-285): when any near+large volume exists it does
`gd.SetRenderTarget(halfRes)` → render → `gd.SetRenderTarget(null)` **mid-frame**
(after the opaque colour pass), then composites with a SpriteBatch. MonoGame's
backbuffer is `RenderTargetUsage.DiscardContents`, so switching away and back
**discards the opaque scene already drawn** — sky, asteroids, stations vanish;
only what's drawn after survives (ships drawn later, effects, HUD). This is
exactly the P547 bug P559 fixed (depth pass BEFORE the opaque colour pass,
"never switched away from"), reintroduced by P579. It also explains the whole
symptom set:
- it starts at the **first near explosion** (the first time the half-res path
  runs);
- the P573 spike (no half-res) didn't black out;
- ships survive (drawn after the wipe);
- P582's camera faced empty space, so its proof couldn't see the wipe (its own
  Honest caveats: "the black-world bug was not reproduced").

Restoring `null` is also wrong whenever the frame renders into `_post.SceneRT`
(bloom) or `_capTarget` (capture) — it drops the real colour target.

### Choice: option (b), and why
The brief preferred (a) (move the half-res pass before the opaque colour pass)
but allowed (b) as proven live. I adopted **(b)**, justified:
- **(b) is already proven live** by Domingo (2026-10-07 22:15 local patch on
  `~/allegiance-play2`): with `PreparingDeviceSettings → PreserveContents` +
  restoring `GetRenderTargets()` he saw explosions AND the world in a live
  15v15 on 221. That is validation on the target hardware (Mesa iris) that this
  environment matches.
- **(a) is a bigger, riskier reorder in a one-shot**: the near-volume half-res
  render would have to run before the opaque pass, which moves the explosion
  collection (currently read under the Gate at the explosion stage, ~`Game1.Rendering.cs:1380`)
  earlier in the frame and splits `VolumetricRenderer` into prepare/composite
  phases — a structural change with real regression surface. (b) is a minimal,
  surgical equivalence: the colour target is still never *emptied* mid-frame
  (PreserveContents) and is restored to the *exact* prior binding.
- The DEBUG guard + `PreserveContents` on all three colour targets give (b) the
  same guarantee (a) provides structurally: between the start of the opaque
  colour pass and the HUD, no colour-target contents are discarded.

### The fix
- `Game1.cs` — `PreparingDeviceSettings` sets
  `PresentationParameters.RenderTargetUsage = PreserveContents` (backbuffer).
- `VfxPostProcessor.cs` — `SceneRT` (bloom colour target) constructed with
  `RenderTargetUsage.PreserveContents`.
- `Game1.Lifecycle.cs` — `_capTarget` constructed with
  `RenderTargetUsage.PreserveContents`.
- `VolumetricRenderer.cs` — the half-res pass captures
  `var saved = gd.GetRenderTargets();` before switching and restores them after
  (`SetRenderTargets(saved)`, or `SetRenderTarget(null)` when saved is empty),
  instead of unconditionally `SetRenderTarget(null)`.
- `Rendering/RenderTargetGuard.cs` (new) — `RestoreIsLossless(reboundIdentical,
  preservesContents)` pure rule + DEBUG-only `AssertColourTargetRestored` trip-
  wire that fires if a volatile excursion is not lossless (a bare
  binding-equality check canNOT see a DiscardContents wipe — the target is
  rebound to the "right" target, just emptied — so the preserve clause is
  mandatory). Called at the restore site.
- Retained `ALLEGIANCE_BW_NOFIX=1` A/B diagnostic (like P582's `P582_NO_UNBIND`)
  reverts to the old `SetRenderTarget(null)` + `DiscardContents` behaviour so
  the cure can be demonstrated in the identical scene.

### Failing-first
`P582bBlackWorldGuardTests` (new class, absent on today's tree → failing-first,
same convention as P573/P582): `RestoreIsLossless(false, _)` (restore to a
different target than left) is NOT lossless; `RestoreIsLossless(true, false)`
(DiscardContents backbuffer rebound identically — the exact old bug in the
normal path) is NOT lossless; `(true, true)` (identical + preserve, the fix) IS
lossless; the truth table is exactly an AND. These encode the old buggy pattern
as failing and the fix as passing.

### Real-window proof (2560×1440 backbuffer, Omega sector, chase camera,
soft particles ON, `--fx volumetric`, fighter death injected at 150 u via
`ALLEGIANCE_VOL_PROOF` so the half-res path runs)
A/B in the identical deterministic scene (retained `ALLEGIANCE_BW_NOFIX`):

| frame | state | what the real backbuffer shows |
|---|---|---|
| A (fix) before (age −0.1 s) | station in view | large station on the left, ship, HUD, starfield |
| A (fix) during (age ~1.3 s) | fireball + world | bright central fireball; **starfield persists** in the periphery (opaque pass intact) |
| B (nofix) during (age ~0.5 s) | total black-out | whole frame ~black (center mean 1.9, periphery 1.4, 96 % near-black) |
| B (nofix) during (age ~1.4 s) | black void | fireball + ship + HUD only; **no stars, no station, no geometry** (pure black void) |

Vision-confirmed: the B (nofix) during frame is "pure black void … no visible
stars … no asteroids, stations, or other world geometry" — exactly the classic
black-world signature (opaque pass wiped; ships/effects/HUD survive); the A
(fix) during frame keeps "scattered faint stars" in the background — the opaque
pass was not wiped. (Starfield/geometry are drawn in the opaque colour pass at
`Game1.Rendering.cs:548-550` ff., so their survival is the direct discriminator.)
The DEBUG trip-wire logged **0** firings with the fix across all runs.

Honest caveat on the metric: absolute pixel thresholds are contaminated by the
HUD (which is drawn after the wipe and survives in BOTH A and B), so the
quantitative near-black % alone does not separate them; the authoritative
separation is (i) the vision-confirmed star/geometry survival vs void and (ii)
the B total-blackout frame (center 1.9, 96 % near-black). The A/B uses a moving
(auto-target Station) chase camera, so frames a second apart differ; the
discriminating frames are taken ~0.3–0.6 s apart with near-identical camera
poses. World-recovery is also confirmed: in a stable straight-flight camera the
pre-explosion and post-explosion baselines are identical (mean 2.93 both).

---

## 2. Bug 2 — volumetric/displaced fireball timing too slow + out of sync

### Root cause
`Volumetric.fx` `MarchAcc` and `DisplacedSphere.fx` `VS` both did
`tn = age/life` with `life = u.Life = FilmLife + SmokeTailSeconds` (~1.5+2.4 =
3.9 s for a fighter), then
`grow = smoothstep(tn)` (so radius reached full only at age ≈ life, i.e. ~3.9 s)
and `cool = tn` (cooled to smoke across the whole 3.9 s). Net: the fireball
started as a small dim dot, swelled across the whole smoke tail, and peaked
late — the "plays too slowly, lands out of sync" (Domingo, P582 follow-up).

### Native fidelity
Native keeps every fireball film frame fully visible and plays it at 20 fps
(`effect/explosion.cpp:183` `frame = (int)(time*20.0f)`, `:187-189` removes the
decal only once `frame >= frameCount`). A fighter (SmallShip) alternates
`exp25` (32 f → 1.6 s) and `exp22` (30 f → 1.5 s); its fireball is bright almost
from birth and holds through the whole film, peaking hot within the first few
frames — not a slow whole-life ramp. Enhanced (the in-sync reference) renders a
flash in the first ~0.12 s and a full-intensity fireball that fades over the
film (`EnhancedExplosionRenderer.cs:132,150`).

### Fix
New pure `Rendering/VolumetricTiming.cs` (unit-testable; mirrored in both
shaders, keep in lock-step):
- `PeakFilmFrac = 0.28` → growth reaches full radius by 28 % of the film
  (fighter 1.5 s → **0.42 s**, inside the native film peak 0.3–0.5 s);
- `BirthRadiusFrac = 0.18` → the first frame already shows a hot core (native
  film frame 0 is fully visible, not a dot);
- `CoolStartFrac = 0.25` → stays white-hot through the peak, cools to smoke
  across the rest of the film;
- all smoothsteps (C2, no kink at the film/tail boundary).

The renderers pass `u.FilmLife` as the shader growth/cool clock
(`VolumetricRenderer` vertex `Life = shaderLife`; `DisplacedSphereRenderer`
`_pLife = filmClock`) while displaced's **erosion** keeps the FULL life
(`ErodeClip` tn/filmFrac unchanged, P582) so the surface still holds through
the film and erodes over the smoke tail. After the film the shader `tn`
saturates to 1 → full-radius cold smoke drifting through the tail.

### Failing-first
`P582bVolumetricTimingTests` (new class, absent on today's tree): peak time for
a fighter in the native 0.3–0.5 s window; growth ≥0.985 at the peak; first-frame
visibility (birth radius floor + growth underway by ~0.12 s); growth and cool
monotonically non-decreasing through film+tail; `Old_whole_life_curve…` shows
the old curve was only ~0.18–0.24 radius at the new peak while the fix is ~1.0;
sampler wiring keeps the film clock shorter than the full life.

### Real-window timing frame-strips (fighter death at 300 u; backbuffer)
Warm (orange fireball) pixel coverage vs age — three techniques, same death:

| age (s) | Enhanced | Volumetric (fix) | Displaced |
|---|---|---|---|
| ~0.1 | 0.26 % | **0.29 %** | 0.32 % |
| ~0.5 | 0.45 % | 1.57 % | 0.33 % |
| ~0.9 | **0.77 % (peak)** | **2.20 % (peak)** | 0.32 % |
| ~1.2 | 0.39 % | 2.18 % | 0.25 % |
| ~2.0 | 0.42 % | 2.18 % | 0.30 % |
| ~2.3 | 0.36 % | 2.01 % | 0.33 % |
| ~6.0 | 0.25 % (gone) | 1.85 % (faint smoke) | 0.24 % (gone) |

Convergence vs Enhanced:
- **First visible frame**: volumetric appears at the first strip age (~0.1 s) at
  0.29 % ≈ Enhanced 0.26 % — within one frame (old code: growth still at the
  ~0.18-r floor → a small dim dot).
- **Peak age**: volumetric peaks at ~0.9 s, the SAME age Enhanced peaks
  (~0.77 % at 0.9 s). (Individual sub-volumes reach full radius at 0.42 s per
  the unit tests; the on-screen aggregate peaks at ~0.9 s as the four staggered
  decals accumulate — matching Enhanced's own 0.9 s aggregate peak, so the two
  converge.)
- **Bright fireball duration**: volumetric's bright fireball is gone by ~2 s
  (like Enhanced); by ~6 s only a faint brown smoke cloud remains (the smoke
  tail, vision-confirmed "explosion is effectively over") vs Enhanced gone.
- **Displaced**: holds its fireball through the film and erodes over the tail
  (P582) — coverage is flatter because the surface is opaque and smaller; it is
  fully gone by ~6 s, lasting longer than before the P582 erosion fix.

### Frame-time trace around the first explosion (no hitch, pre-warm)
`ALLEGIANCE_VOL_PERFLOG` on a 150 u fighter death (2560×1440, `--fx volumetric`):
in the explosion window (5.9–7.5 s, first half-res draw at t≈6 s) **0 of 95
frames exceeded 25 ms; average 16.67 ms** — the first volumetric draw is
smooth. The only >40 ms outliers (83–333 ms) occur at shutdown (t=11.9–20.2 s),
not at the explosion. Pre-warm at load forces every LOD technique's pass
compile and allocates the half-res target up-front, moving the first-use cost
off the blast.

---

## 3. Files

New:
- `src/Allegiance.ClientCore.Game/Rendering/RenderTargetGuard.cs`
- `src/Allegiance.ClientCore.Game/Rendering/VolumetricTiming.cs`
- `tests/Allegiance.ClientCore.MonoGame.Tests/P582bBlackWorldGuardTests.cs`
- `tests/Allegiance.ClientCore.MonoGame.Tests/P582bVolumetricTimingTests.cs`

Changed:
- `src/Allegiance.ClientCore.Game/Game1.cs` (backbuffer PreserveContents + gate)
- `src/Allegiance.ClientCore.Game/Rendering/VfxPostProcessor.cs` (SceneRT preserve)
- `src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs` (_capTarget preserve + Preload calls)
- `src/Allegiance.ClientCore.Game/Rendering/VolumetricRenderer.cs` (restore saved targets + guard; FilmLife clock; Preload)
- `src/Allegiance.ClientCore.Game/Rendering/DisplacedSphereRenderer.cs` (film clock for growth/cool; Preload)
- `src/Allegiance.ClientCore.Game/Shaders/Volumetric.fx`, `DisplacedSphere.fx` (+ recompiled `.ogl.mgfxo` + `.sha256`)

All new files verified via `git status --untracked-files=all`; committed on
`master`, not pushed (`git branch --show-current` = master).

## 4. Build

Whole-solution Release build: **0 warnings, 0 errors**.

## 5. Full suite, `scripts/run-tests.sh` (P471), 3 consecutive runs on the final code

(cited vs `docs/TEST_FLAKES.md`)

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1564/1564 | 544/545 | 3770/3773 |
| 2 | 12/12 | 1564/1564 | 542/545 | 3771/3773 |
| 3 | 12/12 | 1564/1564 | 544/545 | 3772/3773 |

The **MonoGame** assembly — which holds every P582b test (16 new, all green) —
is fully green in all three runs. Every failure is a documented chronic
live-wire/load flake from `docs/TEST_FLAKES.md` / prior P-reports, and the set
ROTATES across runs (run 1: FullSessionKill CrossSide, SideElimination,
LiveWinCondition Board_refused, GameOverPlayersWire Conquest; run 2:
ReadyCheck mission owner, FullSessionKill CrossSide, CrossSideCombatVisibility
warp, LiveChatSectorWire, AddPart treasure-recreate; run 3:
ThreePeerSeededSession deterministic visibility, StationUnderAttack alert).
This "flaky subset rotates every run, none a regression" is the exact
P322/P361-established env-flake signature; none of the failing tests touch the
P582b renderers/shader tests. (Totals: Lobby 12, MonoGame 1564, ClientCore 545,
Core 3773.)

## 6. Honest caveats

- The black-world pixel metric is HUD-contaminated (the HUD survives the wipe in
  both A and B), so the discriminator is the vision-confirmed star/geometry
  survival vs pure-void plus the total-blackout frame; the moving chase camera
  means frames a second apart differ (mitigated by comparing near-adjacent
  frames). Option (b) relies on PreserveContents on all three colour targets,
  which the DEBUG guard enforces going forward.
- Volumetric brightness (P579's `Brightness=30`) makes the additive fireball
  read "warm" (R>G>B) for longer than Enhanced under the coarse warm-pixel
  metric; the vision check confirms the bright fireball is gone by ~2 s and only
  faint smoke remains at ~6 s. This is a brightness/coverage note, not a timing
  defect.
- The peak-time unit test asserts 0.42 s (film peak of a single sub-volume);
  the on-screen aggregate peak is ~0.9 s, which is the same as Enhanced's and
  therefore the correct convergence.
- The `ALLEGIANCE_BW_NOFIX=1` gate (off by default) is a retained A/B diagnostic,
  like P582's `P582_NO_UNBIND`.
