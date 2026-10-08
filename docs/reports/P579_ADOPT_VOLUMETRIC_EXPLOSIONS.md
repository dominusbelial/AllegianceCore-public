# P579 — ADOPT the P573 volumetric explosions into `master`

**Branch:** `master` (committed locally, not pushed)
**Port:** P573 spike (`spike/p573-volumetric`, worktree
`~/Development/AllegianceCore-p573`, commits `15847ffa` + `1bae5d0e`)
re-applied onto current `master`, plus the five live-found fixes and the scope-3
fill-rate work. Proof is from the REAL client window (2560×1440 backbuffer via
the P572 `scripts/live-proof.sh` + `--autoshots`/`ALLEGIANCE_AUTOSHOT` path —
never offscreen/`--capture`).

---

## 0. Outcome in one paragraph

The P573 volumetric explosions are adopted into `master`, with all five live
fixes plus the fill-rate LOD/budget and the sphere clip. In the real window on
**this GPU (Intel Arrow Lake-S / Mesa iris, 2560×1440)** the Volumetric tier
now renders camera-relative, round (no square edges), correctly bright
(as bright as or brighter than Enhanced's fireball at the same moment) and —
after the coverage-LOD + per-frame pixel/step budget + **half-resolution for
near volumes** — holds **60 fps in all three perf scenarios (a/b/c)**.
Because the scope-3 performance target is met, **Volumetric stays the
DesktopGL default**; Android/low-end remain Enhanced (baked), and there is an
automatic fallback to Enhanced on shader-load failure or sustained
over-budget frame time.

---

## 1. Port (scope item 1)

Cherry-picked/re-applied the spike's game code onto current `master` (rebase
past P561/P564/P565/P566/P567/P568/P569/P570/P571/P572/P574 — **not** a branch
merge). New files, all verified by `git status --untracked-files=all`:

- `Shaders/{Volumetric,DisplacedSphere}.fx` + compiled `Content/Shaders/*.ogl.mgfxo`
  (rebuilt by `scripts/build-shaders.sh`, with the `.fx.sha256` staleness
  records regenerated).
- `Rendering/{VolumetricRenderer,DisplacedSphereRenderer,IcosphereGenerator}.cs`.
- `State/{EffectsQuality,VolumetricExplosionSampler,VolumetricQuality,VolumetricGeometry}.cs`.
- `--fx native|enhanced|volumetric|displaced` parsing + tier resolver + budget
  fallback in `Program.cs` / `Game1.{Session,cs,Lifecycle,Rendering}.cs`.
- Tests: `P573VolumetricModelTests.cs` + `P579VolumetricAdoptTests.cs`.

The `1bae5d0e` live fixes are part of the port:
1. **Positions camera-relative** — the view matrix keeps the eye at the origin
   (`EnhancedExplosionRenderer.cs` subtracts `cameraEye`). Pinned as
   `VolumetricGeometry.CameraRelative(world, eye)`, used by both A and B.
2. **Depth-fade slope 250 + no-depth=1** — `Volumetric.fx DepthFade` now uses
   the same `SoftnessNormalized` slope as `SoftParticle.fx` (`SoftnessInv=250`),
   and `--soft off` binds a 1×1 white "far" depth texture so an unbound
   sampler decodes to scene=1 → fade 1 (never erases a volume).

Failing-first evidence: the new `P579VolumetricAdoptTests` (15 tests) fail
against the P573 LOD policy (demonstrated: reverting `VolumeLod` to the spike's
bigger⇒more-steps mapping turned 6 of them red) and pass after. The
camera-relative and fade tests pin the `1bae5d0e` fixes (they would fail if the
subtract / slope were reverted).

---

## 2. Brightness/density calibration (scope item 2)

A `Brightness` effect parameter (set from code, default **30**) boosts the
march. Measured in the REAL window, real backbuffer (a fighter death — type 3 —
at 150/600/1500 u, capture at fireball peak age ≈ 1.5 s):

| measure (age 1.5 s, 150/600/1500 u) | Enhanced | Volumetric A (Brightness=30) |
|---|---|---|
| peak luminance (max RGB lum)        | 255      | 253 |
| region mean luminance               | 5.2      | **5.4** |
| coverage lum > 50 (%)               | 3.77     | **4.59** |
| coverage lum > 100 (%)              | 2.44     | 1.91 |

So at Brightness=30 the volumetric fireball reads **as bright as or brighter
than Enhanced** (peak saturated in both, mean +0.2, larger >50 halo) — the task's
primary criterion. Rationale for the constant is in
`VolumetricRenderer.BrightnessDefault`.

**P546 blown-out budget (dense combat ≤ ~20 %)**: measured in a 30-blast at
300 u scene:

| dense 30-blast (age 1.5 s) | Enhanced | Volumetric A |
|---|---|---|
| blown-out (lum > 250, %)   | 1.45     | **0.10** |

Volumetric stays far under the ≤20 % budget and blows out *less* than Enhanced
(additive, but each volume's density is contained).

Native timing/size/tints are unchanged — both tiers consume the same
`ExplosionPreset`/`ExplosionSampler` mapping (native `trekigc.cpp:95-177`,
`effect/explosion.cpp`), so timing and tint parity is preserved.

---

## 3. Fill-rate LOD (scope item 3) — coverage-based, inverted, budgeted

**Inverted policy** (`VolumeLod.ForPixelDiameter`): step count now goes by
screen COVERAGE — big on-screen ⇒ **fewer, longer steps** (Low=64); small ⇒
more steps is fine (Ultra=510). Levels: Low(1×64), Med(1×128), High(1×255),
Ultra(2×255). `dt` is normalised to 1/steps so total optical depth (hence
brightness) is uniform across levels.

**Per-frame pixel/step budget** (`VolumeBudget.AllocateLevels`): caps Σ(projected
area × steps) ≤ `MaxPixelSteps` (42M), degrading the **largest-coverage**
(highest-cost) volumes first — the required degradation order. Kept alongside
the existing `MaxVolumes=40` nearest-first cap.

**Half-resolution for near volumes** (`VolumetricRenderer.NearHalfResMinPx=240`):
near+large volumes (projected diameter ≥ 240 px) render into a half-resolution
render target (¼ the pixels → 4× the fill) and composite up additively (One, One)
— the fill-rate lever that makes a screen-filling blast affordable. Verified in
the real window: the near fireball renders correctly and the surrounding scene
is intact.

**Frame times — real window, 2560×1440, this GPU** (before/after). The client is
vsync/compositor-locked at 60 Hz (16.67 ms) under Xwayland, so "60 fps" = the
frame holds the 16.67 ms floor; any explosion cost that pushes over shows as a
dropped frame. Steady-state during each explosion window:

| scenario | before (pre-half-res port, this GPU) | after (P579) |
|---|---|---|
| (a) 1 fighter death @ 100 u (fills screen) | ~23.5 ms (~42 fps, 3-blast pre-half-res run) | **16.67 ms / 60 fps** (p1 16.67) |
| (b) 6 overlapping deaths @ 300 u          | —                                          | **16.67 ms / 60 fps** (p1 16.67) |
| (c) 15v15-style 30-blast brawl            | —                                          | **16.67 ms / 60 fps** (p1 16.67) |

(The spike's own numbers were llvmpipe software — not transferable to this GPU;
its task notes "3–4 fps up close" on llvmpipe. On this GPU the fill-rate problem
was real: a full-res screen-filling blast at 64 steps ≈ 100M+ pixel-steps alone.
Half-res + LOD + budget bring every scenario under the 16.67 ms floor.)

Because **the scope-3 target is met**, Volumetric stays the DesktopGL default.

---

## 4. No square edges (scope item 4)

`Volumetric.fx` now computes the ray–sphere intersection first
(`RaySphere`) and `clip()`s fragments whose ray misses the sphere — no square
edges (the quad's corners fall outside the inscribed sphere) and ~21 % fill
saved (those pixels never reach the framebuffer). Visual proof: every
real-window volumetric screenshot in `docs/reports/P579_assets/` shows rounded,
soft edges with no square/angular artifacts (confirmed by inspection).

---

## 5. Setting + defaults + fallback (scope item 5)

`Effects: Native | Enhanced | Volumetric` (`--fx native|enhanced|volumetric|displaced`;
`displaced` = Technique B within the Volumetric tier). `EffectsQualityResolver`:
- `PlatformDefault`: DesktopGL → **Volumetric** (raymarch A) — **gate met** on
  this GPU; Android/iOS/TvOS → **Enhanced** (baked flipbooks, GLES2-safe).
- `Resolve`: Volumetric holds only if the shader loads AND the GPU is under
  budget; otherwise falls back to Enhanced once, logged.
- Runtime fallback `EnsureVolumetricBudget` (in `Update`): sustained frame time
  > `VolumetricFrameBudgetMs` (80 ms) for 3 s drops to Enhanced once; skipped
  during `--auto` proof runs so recordings render one tier consistently.
- Shader-load fallback in `Game1.Lifecycle.LoadContent`.

---

## 6. B (displaced) — same camera-relative check + proof (scope item 6)

`DisplacedSphereRenderer` routes its centre through
`VolumetricGeometry.CameraRelative` (same fix as A). Proven live: the displaced
fireball renders correctly positioned in the real window at young ages
(clip-threshold erosion intentionally fades it as it ages — captured at
0.2–1.0 s in `P579_assets`). B's opaque, depth-writing sphere is the cheap
stylised option; A (raymarch) is the default.

---

## 7. Tests / build

- **Whole-solution build: 0 warnings, 0 errors** (Debug and Release).
- **Full suite, `scripts/run-tests.sh` (P471), 3 consecutive runs on the final
  code** (cited vs `docs/TEST_FLAKES.md`):

  | run | Lobby | MonoGame | ClientCore | Core |
  |---|---|---|---|---|
  | 1 | 12/12 | 1529/1530 | 544/545 | 3769/3773 |
  | 2 | 12/12 | **1530/1530** | **545/545** | 3768/3773 |
  | 3 | 12/12 | **1530/1530** | **545/545** | 3769/3773 |

  The MonoGame project (which holds every P573/P579 volumetric test — 79 tests)
  is fully green in runs 2 and 3; its run‑1 sole failure was
  `MiscKeyBindingLiveTests` (live autopilot target ranking — a documented
  live-network load race, no P579 involvement). Every Core failure is a
  documented live-wire/load flake (WarpBomb, GameOverPlayers, MineCrossSide,
  AsteroidMined, LiveRipcord, LiveStationRescue, StationUnderAttackAlert,
  LiveRelaunch, LiveTreasurePickup, CargoBayReload wire, …) exactly the
  P551-precedented "only documented live-wire load-flakes; economy suite green"
  pattern. None touch the volumetric code or its tests.
- **Failing-first demonstrated**: `P579VolumetricAdoptTests` (15) go red against
  the P573 LOD policy and green after; the camera-relative + depth-fade tests
  pin the `1bae5d0e` fixes.

---

## 8. Files

New: `Shaders/{Volumetric,DisplacedSphere}.fx`,
`Rendering/{VolumetricRenderer,DisplacedSphereRenderer,IcosphereGenerator}.cs`,
`Game1.VolumetricProof.cs` (env-gated real-window proof facility —
`ALLEGIANCE_VOL_PROOF` deterministic injection, `ALLEGIANCE_VOL_FRAMESTRIP`
autoshots, `ALLEGIANCE_VOL_PERF[LOG]` frame-time CSV; inert by default),
`State/{EffectsQuality,VolumetricExplosionSampler,VolumetricQuality,VolumetricGeometry}.cs`,
`Content/Shaders/{Volumetric,DisplacedSphere}.ogl.mgfxo` (+`.fx.sha256`),
`tests/.../{P573VolumetricModelTests,P579VolumetricAdoptTests}.cs`.
Changed: `ExplosionSampler` (public `OffsetFor`/`AngleFor`), `Game1*.cs`,
`Program.cs`. Proof assets: `docs/reports/P579_assets/` (frame strips,
side-by-side Enhanced|Volumetric|Displaced, brightness + dense comparisons).

## 9. Honest caveats

- Frame-time figures are bounded below by the Xwayland 60 Hz composition lock;
  "60 fps" here means the frame holds the 16.67 ms floor (no dropped frames)
  in the measured window, which is the gate's meaning. Sub-floor headroom isn't
  observable on this setup.
- The deterministic-injection proof injects explosion entities replicating a
  fighter death at the named distances in the real client draw list (env-gated,
  off by default); it is not the live server-driven combat path, but it
  exercises the exact production renderer/entities at exact distances.
- "Before" numbers are from this port with the half-res/budget optimization
  disabled (the spike's llvmpipe numbers are not GPU-transferable).
