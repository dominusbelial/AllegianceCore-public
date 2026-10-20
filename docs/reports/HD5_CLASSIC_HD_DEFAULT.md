# HD-5 — HD pack default: sharp ESRGAN textures with native (classic) lighting; PBR becomes "experimental"

Date: 2026-10-19 (completed night of 2026-10-19/20). Branch: `master` (committed, not pushed).
Task scratch: `~/allegiance-scratch/HD5/` (removed at end). Raw evidence: `~/allegiance-evidence/HD5/` (full-res PNGs, all client logs).

## Decision

Ships (and every other object HD-3 covers) now render the sharp HD pack basecolor
through the **native classic lighting** as the default. Full PBR remains available
but is **opt-in/experimental, default OFF**, and deferred for tuning to HD-6.

Why: with the pack, the old default routed everything through the PBR path's
*estimated* roughness/metallic maps, which shifts the hull colour cooler and paler
(measured here: classic tracks native to within ~1/255 per channel, while PBR drops
red and lifts green/blue). The HD-5 default keeps the saturated native scene lighting
while the sharper textures win up close.

## Implementation

### 1. `HdDrawMode` + `NativeMaterialParts.Draw` (central switch)

`Rendering/NativeMaterialParts.cs` — the one choke-point every renderer funnels
through. Its `bool enabled` (PBR-on) became `HdDrawMode mode` (`Off` / `Classic` /
`Pbr`), defaulting to the shared `HdMaterialRenderer.Mode` when the caller omits it:

- **Pbr**: existing `hd.TryDraw` (full estimated material) with native fallback.
- **Classic** (new default): after resolving the native texture, `hd.ClassicColor(stem,
  projectedPixels)` is substituted for the texture on the SAME native `BasicEffect`
  pass — identical lighting, blending and depth behaviour; native fallback when the
  pack material is absent. The screen footprint (`projectedPixels`, same formula as
  `TryDraw`) picks the basecolor mip so an up-close hull samples full resolution.
- **Off**: pure native, pack never touched.

`HdMaterialRenderer.TryDraw` and `ClassicColor` no longer re-check `Mode` — the caller
already selected the mode; this keeps the ship renderer's explicit per-draw override
(inspector HD-compare) coherent with the shared global mode.

### 2. `HdMaterialRenderer` — `Mode` + `ClassicColor`

`Rendering/HdMaterialRenderer.cs`:
- Replaced `Enabled` with `Mode` (the single source of truth for every renderer that
  shares this instance). `ModeFor(bool hdTextures, bool hdPbr)` is a pure mapping
  (Off / Classic / Pbr) used by Game1 and by tests.
- `ClassicColor(texture, projectedPixels)` returns the pack **basecolor** as a UNORM
  (sRGB-encoded) texture — BasicEffect consumes the encoded samples directly, so the
  classic material renders with the exact native lighting and blending. Screen-size
  mip selection mirrors the PBR resolution policy.
- `MapsFor(…, unlit: true)` now residents **basecolor only** (`["basecolor"]`) for both
  compressed and PNG schemas (was 5 maps for PNG). The classic path keeps a separate
  `material.Key|unlit` residency entry so it coexists with PBR and is ~1/5 the byte cost.
- `EmissiveTexture` (treasure decal / warp glow, native's glow-equivalent) now gates on
  `Mode != Off` instead of `Enabled` — unchanged in Classic/Pbr.

### 3. Ship renderer

`Rendering/NativeShipRenderer.cs` — `Draw` derives the mode as:
`hdOverride` false ⇒ `Off`; true (inspector force-on) ⇒ `HdPbrEnabled ? Pbr : Classic`;
else ⇒ the shared `_hd.Mode`. This makes the ship follow the global setting in normal
gameplay (consistent with every other renderer) while preserving the inspector
HD-compare semantics.

### 4. Every other renderer

`NativeStationRenderer`, `NativeMissileRenderer`, `NativeAsteroidRenderer`,
`ProbeRenderer`, `ProjectileModelRenderer`, `NativeTreasureRenderer`, `WarpRenderer`
need no per-renderer change: their `geo.Materials.Draw(...)` calls omit `mode`, so they
follow `HdMaterialRenderer.Mode` — the single global setting. Treasure/warp glow
continues through `EmissiveTexture`.

### 5. Settings — `graphics.json` + Ctrl+F6 menu

- `UpscalingSettings` gained `HdPbr` ("HD lighting (PBR, experimental)"), **default off**.
- `Load` migrates a legacy saved `HdPbr` (the old benchmark-driven toggle, formerly
  default-on) onto the new flag **only when the file predates the field**: a player who
  previously applied an HdPbr=on benchmark recommendation keeps PBR on; everyone else
  gets the new experimental default (off).
- Ctrl+F6 (`Game1.GpuBudget.UpdateGraphicsSettings`) gained an `HD lighting (PBR,
  experimental)` row (11 rows now; HUD rows 6–9 unchanged, new row at index 10).
  `SyncHdMaterialMode()` pushes `HdTextures`/`HdPbr` into `_hdMaterials.Mode` (null-safe
  for menu unit tests). `HD textures` row shows the classic-path residency.
- `Game1.Benchmark.ApplyBenchmarkSettings` persists `s.HdPbr` into `_upscaling` and
  re-syncs the mode.

### 6. Benchmark (P729)

`BenchmarkModels.BenchmarkRecommendation.HdPbr` keeps its name (so P729's
recommendations share the field) but its default is now **false**, and
`BenchmarkPolicy.Recommend` no longer turns PBR on for non-GPU-bound machines — the
experimental PBR stays off and the HD-classic default is the baseline.

## Native behaviour classified

The classic HD path introduces **no new shading rule** — it substitutes the sampled
texture on the unchanged native `BasicEffect` pass. The lighting it preserves is the
native sector lighting already validated in earlier ports.

| Behaviour | Native cite | Class | What the port does |
| --- | --- | --- | --- |
| Sector lighting (bidirectional ambient 0.25) | `SectorLighting.cs:38,96` (native bidirectional ambient constant; sector-derived) | intended design | Reused unchanged via `SectorLightingEffect.Apply` in classic mode — same as native path |
| Basecolor sampled as encoded sRGB for BasicEffect | HD-4 pack colorSpace `basecolor: sRGB` (manifest) | design | `ClassicColor` loads basecolor as UNORM so encoded samples feed BasicEffect directly |
| Single material fallback to native | HD pack `Resolve` (missing/hash-mismatch ⇒ null) | design | Classic falls back to the native texture when the material is absent |
| Mip selection by screen footprint | HD-4 policy | era constraint → intent kept | Classic mirrors the PBR resolution policy so up-close is full-res |

## Modern alternative considered

Native caches a single texture and lets BasicEffect do fixed-function lighting.
Modern engines would use a full physically-based pipeline with real albedo/normal/
roughness (the PBR path). HD-5 does not abandon that: the choice is **which path is
default**. We ship **classic (native lighting + sharp basecolor)** as default because
the estimated PBR maps are not yet tuned (HD-6) and visibly shift colour; once HD-6
tunes them, flipping the experimental flag to default-on is a settings change, not a
code change. Residency-wise, classic only caches basecolor (~1/5 of the PBR set), a
cheaper default.

## Tests

Failing-first, all on today's code before the fix:

- `Mode_selection_per_setting` (Theory, 4 cases) — `HdMaterialRenderer.ModeFor`
  (Off/Classic/Pbr) per the two settings.
- `Hd_lighting_flag_defaults_off_and_roundtrips` — new default off; explicit on/off persists.
- `Legacy_saved_HdPbr_migrates_to_the_new_flag` — no-field file inherits a previously
  applied HdPbr=on benchmark pref; an explicit file field is authoritative; no prefs ⇒ off.
- `Classic_hd_uses_basecolor_with_native_lighting_and_falls_back_on_real_gl`
  (`tools/hd2-proof --classic-test`, real GL): mode selection; classic renders the HD
  basecolor (native blue vs HD green) under native lighting; classic pixel-matches a
  native-lighting + HD-basecolor reference; classic responds to a changed sector lamp;
  classic resident == basecolor-only (5460 B) vs PBR 5-map (27300 B); a missing material
  falls back to native.
- Existing `HdAtlasRendererTests` (10; ship/station/missile/probe/projectile/asteroid/
  aleph/treasure, compressed GL) and `P729BenchmarkTests` (9) updated to the new
  `Mode`-based toggling and re-run green.

## Real-client proof (real 2560×1440 presented backbuffer)

Own local server (`scripts/live-proof.sh` family; local port, never 221) + the windowed
client joined, auto-launched each hull (`--auto-launch <hull>`), chase camera,
`--hide-hud`, real-backbuffer autoshots. Three modes via `ALLEGIANCE_GRAPHICS_SETTINGS`
(native: HdTextures off; classic: on + PBR off; PBR: on + PBR on). Full-res PNGs and all
client logs in `~/allegiance-evidence/HD5/`. Committed assets (≤2 MB) in `HD5_assets/`:

- Per-subject **native | HD classic | HD PBR** comparisons (downscaled):
  [`fighter-comparison.jpg`](HD5_assets/fighter-comparison.jpg),
  [`interceptor-comparison.jpg`](HD5_assets/interceptor-comparison.jpg),
  [`bomber-comparison.jpg`](HD5_assets/bomber-comparison.jpg),
  [`station-comparison.jpg`](HD5_assets/station-comparison.jpg). The station is the
  Omega asteroid garrison in `--camera station` (ExternalStation).
- **2× zoom crops** (native | HD classic, same region) showing classic keeps colour
  while being sharper up close: [`fighter-zoom2x.jpg`](HD5_assets/fighter-zoom2x.jpg),
  [`interceptor-zoom2x.jpg`](HD5_assets/interceptor-zoom2x.jpg),
  [`bomber-zoom2x.jpg`](HD5_assets/bomber-zoom2x.jpg),
  [`station-zoom2x.jpg`](HD5_assets/station-zoom2x.jpg).

Objective colourimetry over the lit ship surface (mean RGB, per channel /255):

| subject | native | HD classic | HD PBR |
| --- | --- | --- | --- |
| fighter | 88.8 / 84.1 / 68.1 | 89.3 / 83.8 / 67.8 | 82.3 / 86.4 / 71.9 |
| interceptor | 108.7 / 95.2 / 75.9 | 108.9 / 94.8 / 76.0 | 95.3 / 94.1 / 79.0 |
| bomber | 76.0 / 74.8 / 67.7 | 75.1 / 73.5 / 67.0 | 77.2 / 85.0 / 79.8 |

Classic tracks native to within ~1.3/255 (usually <0.5); PBR drops the red channel and
lifts green/blue — the cooler/paler shift that motivates this default. Independent of
these captures, the GL test proves classic ≡ native lighting + HD basecolor by
construction (pixel match to a native-lighting reference).

### Residency in classic mode (basecolor only)

Deterministic GL (uniform 32×32): classic resident **5460 B** = one basecolor map vs
PBR **27300 B** = 5 maps (5×). Real pack at the captured 1440p footprint (from client
logs, `|unlit` keys = classic basecolor):

| material | classic (basecolor) | PBR (full set) |
| --- | --- | --- |
| fighter hull `fig02ops1bmp` | 1.33 MiB | 2.33 MiB |
| interceptor hull `fig16bmp` | 1.33 MiB | 2.33 MiB |
| bomber hull `bom07bmp` | 1.33 MiB | 2.33 MiB |
| station/scene `bgrnd03bmp`/`cwbaybmp`/`ss24abmp` | 5.33 MiB | 9.33 MiB |

Whole classic scene resident at capture ≈ **17 MiB** (well under the 512 MiB budget).

## Validation

- Whole-solution build: **0 warnings, 0 errors** (Debug and Release).
- New/affected tests **3× alone**: HD-5 tests 7×3, `HdAtlasRendererTests` 10×3,
  `P729BenchmarkTests` 9×3, `ModernHudSettingsInputTests` 1×3 — all pass.
- Fast tier once on final code: **8901 tests PASS** (270.9 s; 3 optional-Meshy skips).
- `src/Allegiance.Server` / `src/Allegiance.Core` untouched ⇒ no AOT-publish gate
  (gate clause applies only when those change).
- Visual proof: real 2560×1440 presented-backbuffer captures (not `--capture`/offscreen),
  produced by the local `scripts/live-proof.sh` client path.

## Out of scope (next task)

Tuning the PBR materials (HD-6), regenerating textures, HUD, effects.
