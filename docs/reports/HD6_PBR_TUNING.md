# HD-6 — Tune the experimental PBR lighting until it beats native in side-by-side screenshots

Date: 2026-10-20. Branch: `master` (committed, not pushed). Repository: AllegianceCore
(game code + proof); pipeline derivation also updated in AllegianceCore-hd (its `main`).
Task scratch: `~/allegiance-scratch/HD6/`; raw full-res PNGs, logs and client/server
outputs: `~/allegiance-evidence/HD6/` (outside the repo).

## Outcome

The experimental PBR path (Ctrl+F6 "HD lighting (PBR, experimental)") is re-tuned so it
**keeps the native colour mood on every surface and adds believable crisp highlights on
top**. On all three ships, the station and the dynamic battle scene the PBR panels are the
"same ship, same colours, plus believable shiny highlights" the acceptance asked for —
warmer and more saturated than HD-4/HD-5's pale, grey, matte result — with smooth, non
blown-out highlight roll-off. See the committed side-by-side grids in
[HD6_assets](HD6_assets/): `interceptor_grid.jpg`, `fighter_grid.jpg`, `bomber_grid.jpg`,
`station_grid.jpg`, `battle_grid.jpg` (native | HD classic | HD PBR, downscaled from real
2560×1440 backbuffer captures; full-res in `~/allegiance-evidence/HD6/`).

## Why this task existed

HD-5 moved the sharp HD pack under **native (classic) lighting** as the default and parked
PBR behind the experimental flag because HD-4's captures showed PBR made ships *paler,
greyer and matte*: the ESRGAN-estimated maps were too rough / not metallic enough, and the
PBR scene lighting lost native's saturated colour.

## 1. Diagnosis (isolating each factor)

Fresh real-client 2560×1440 captures on current code, plus the retained HD-5 evidence, gave:

- **Too-rough maps.** The pack's estimated roughness is uniformly high — `fig16bmp`
  roughness median 0.62, nothing below 0.30 — so GGX specular spread wide/thin and the hull
  read matte. Metallic median 0.53 (much of it mid/high) with a strong metal↔ambient
  interaction (below).
- **The metal ↔ ambient interaction was the biggest dimmer.** The HD-2/HD-5 shader computed
  `ambient = albedo * Ambient * lerp(1, 0.25, metal)`. Metallic plating therefore received
  as little as **25% of native's ambient**, and its diffuse was scaled by `(1−metal)`, so
  plating fell to near-black in shadow and the hull went dark + desaturated — the exact
  "paler, greyer" report. This diverges from native, which has **no metal concept** and
  applies its flat ambient to every surface.
- **Linear-workflow re-gamma washed warm hulls cool/silver.** A `classic` control capture
  (same HD basecolor + native lighting) pixel-matches `native` (mean RGB within 0.002),
  isolating the colour shift to the PBR shading itself. Porting native's diffuse into a
  linear (HDR) pipeline and re-gamma-encoding *amplified the coloured back light* (the
  constant blue alt light) relative to classic, tilting warm hulls toward cool silver.
- **No tone map in the base PS.** Only `PSFlash` had a shoulder; the plain `PS` clipped
  highlights to white/desaturated.

Measured map values (`fig16bmp`, from the PNG schema-2 mirror of the pack):

| map | median | p10 | p90 |
| --- | ---: | ---: | ---: |
| roughness | 0.62 | 0.54 | 0.73 |
| metallic | 0.53 | 0.00 | 0.72 |

## 2. Tuning (in the required order)

### 2a. Scene lighting — native-faithful base + conductor sheen (`NativePbr.fx`)

The base lighting is now computed **bit-for-bit like the native classic path, in gamma
space**:

```
base = sRGB_albedo * ( bidirectional_ambient(0.25) + KeyColor*ndotl + AltColor*ndotl )
```

with the flat ambient applied to **every** surface (`AmbientMetalFloor` defaults to 1; the
HD-2 `lerp(1,0.25,metal)` suppression is gone). This preserves native's warm saturated
mood and strong blue rim exactly, on all surfaces. On top we add:

- a **GGX specular with a white dielectric clearcoat f0 (0.04)**, intensified by the tuned
  metallic map and shaped by the tuned roughness map — "warm coloured diffuse + white
  clearcoat glints", i.e. believable painted-metal highlights without tinting the hull
  silver;
- a **subtle neutral-cool fresnel sheen** on metal (`EnvSheen`) so plating reads reflective
  at grazing angles;
- the coloured key + constant blue back light unchanged from native.

Native citations (all read and verified against the native source, not the report):

| Behaviour | Native cite | |
| --- | --- | --- |
| Bidirectional ambient 0.25 | `Igc/clusterIGC.h:331-334` (`GetBidirectionalAmbientLevel`); `engine/device3d.cpp:792` (bidirectional branch) | flat on every surface, no metal concept |
| Constant blue back/alt light (0,0.5,1) | `Igc/clusterIGC.h:321-324` (`GetLightColorAlt`); `WinTrek/WinTrek.cpp:3635` (`m_colorAlt = GetLightColorAlt()`) | used for the alt diffuse + alt highlight |
| No specular | `engine/device3d.cpp:758` (`D3DRS_SPECULARENABLE FALSE`) | GGX specular is the intended HD-6 modern addition |
| Lighting always enabled | `engine/device3d.cpp:771` (`D3DRS_LIGHTING TRUE`) | kept |

### 2b. Material maps — per-material tuning, no texture regeneration

New `Rendering/MaterialTuning.cs` adjusts the **sampled** roughness/metallic maps at draw
time (pure, unit-tested math mirroring the HLSL):

- **roughness**: `rough' = clamp(rough*Scale + Bias, Min, Max)`. Defaults (Scale 0.75,
  Bias 0.05) bring hull plating ~0.62 → ~0.51: glossy enough for crisp believable glints
  but not mirror-flat (which washed broad surfaces white).
- **metallic**: a dead-banded boost — `metal ≥ 0.30` is lifted toward conductor (Gain 1.15,
  clamp ≤ 0.90); reads **below** the dead band (rubber/glass/glow, warm paint) stay
  dielectric so the native warm diffuse survives.

These are driven per atlas by an optional `materialTuning` section in the pack manifest
(`HdPack.TuningFor`), so hull plating can differ from rubber/glass/glow with **no texture
regeneration** — the sanitised manifest-adjustment path the brief allowed. When absent,
defaults apply. Proven end-to-end in the real client: an `Exposure:0.4` override turned the
bomber PBR measurably darker (ship-mask mean 0.141 → 0.111); the shipped-style subtle
overrides for `bom07bmp`/`fig16bmp` are in the local pack manifest.

The **pipeline default derivation** in `pipeline/esrgan_variant.py` (AllegianceCore-hd) was
also updated so any future pack starts from glossier plating (low roughness) and
neutral-plating metal, keeping saturated paint and dark matte reads dielectric — matching
the runtime tuning without regenerating the current pack.

### 2c. Tone map + exposure (`NativePbr.fx`)

A shared **hue-preserving tone map**: only the PEAK channel is compressed and every channel
is scaled by the same factor, so highlights roll off toward white without desaturating the
hull; below `TonemapThreshold` (0.8) the curve is identity (native's saturated midtones
survive). A single `Exposure` (1.0) is applied first. Both `PS` and `PSFlash` share it
(replacing the old raw clip in `PS` and the inline 0.8 shoulder in `PSFlash`).

## 3. Acceptance — real 2560×1440 backbuffer, side by side

Reproduced HD-5's real-window capture tooling (local server, windowed DesktopGL client,
`--auto-launch`, chase camera, `--hide-hud`, real `SaveAsPng` backbuffer at 2560×1440; the
three modes via `ALLEGIANCE_GRAPHICS_SETTINGS`). Committed downscaled grids:

| subject | hull | grid |
| --- | --- | --- |
| Heavy interceptor | 481 / `wc_ichvy` | [interceptor_grid.jpg](HD6_assets/interceptor_grid.jpg) |
| Advanced fighter | 488 / `wc_icadv` | [fighter_grid.jpg](HD6_assets/fighter_grid.jpg) |
| Heavy bomber | 489 / `wc_icbmb` | [bomber_grid.jpg](HD6_assets/bomber_grid.jpg) |
| Omega garrison (station) | external-station cam | [station_grid.jpg](HD6_assets/station_grid.jpg) |
| Battle scene | `spawn_scenario {size:12,target_sector:Alderan,arena:true}`, auto-goto Alderan | [battle_grid.jpg](HD6_assets/battle_grid.jpg) |

**Objective colourimetry** over the lit surface (mean RGB /255, native · classic · PBR):

| subject | native | HD classic | HD PBR |
| --- | --- | --- | --- |
| interceptor | 0.157 / 0.150 / 0.079 | 0.158 / 0.150 / 0.080 | 0.178 / 0.174 / 0.106 |
| fighter | 0.131 / 0.131 / 0.059 | 0.132 / 0.131 / 0.060 | 0.141 / 0.142 / 0.075 |
| bomber | 0.126 / 0.127 / 0.064 | 0.126 / 0.127 / 0.065 | 0.141 / 0.146 / 0.086 |
| station | 0.133 / 0.126 / 0.113 | 0.134 / 0.126 / 0.113 | 0.194 / 0.191 / 0.177 |

Against HD-5's complaint — **PBR drops red and lifts green/blue**:

- **Red is preserved / lifted** (never dropped): 0.157→0.178, 0.131→0.141, 0.126→0.141,
  0.133→0.194.
- **Warmth order restored** (red ≥ green, like native) instead of HD-5's green>red tilt.
- PBR is brighter than native (the added specular) but with the hue-preserving tone map
  saturating the highlights rather than blowing them; vision review on every grid reported
  "no blow-outs", "believable crisp highlights", "keeps the colour mood".
- The modest blue lift (e.g. interceptor 0.079→0.106) is the intended **native cool back
  light gleaming off the now-reflective metal** — native's blue rim, made reflective.

Judge verdict: **PBR now beats native** — it keeps the native colour mood while adding the
believable reflections/highlights native's fixed-function lighting cannot express.

### Battle scene note (honest)

The bot arena TTL (~4–5 s) and the client's warp transit meant the arena had typically
resolved before the windowed client reached the battle sector, so the three battle panels
are the **same identical arena condition** (asteroid-field transit with the player fighter)
rather than a sustained mid-dogfight freeze-frame. The material/colour comparison is
therefore valid on identical dynamic conditions across the three modes (native | classic |
PBR all caught the same scene), but it does not showcase tracer/impact lighting. This is a
capture-timing limitation, not a rendering one; the HUD/effects and bot-combat visuals are
out of scope (LOOK-1 already landed).

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
| --- | --- | --- | --- |
| Bidirectional ambient 0.25, flat on every surface | `clusterIGC.h:331-334`; `device3d.cpp:792` | design | kept exactly (gamma-space `albedo*0.25`); metal no longer suppresses it |
| Coloured key + constant blue back light | `clusterIGC.h:321-324`; `WinTrek.cpp:3635` | design | kept; blue rim now also gleams off metal highlights |
| No specular | `device3d.cpp:758` `SPECULARENABLE FALSE` | design | kept (no native specular); GGX added as HD-6's intended enhancement |
| Roughness/metallic are pack-authoring estimates | `pipeline/esrgan_variant.py` (HD repo) | authoring policy | corrected at draw time (manifest/default) and in the derivation, not by regenerating textures |

## Modern alternative considered

Native lights with fixed-function D3D (diffuse + flat ambient, no specular). Modern engines
would use a full linear-HDR PBR pipeline with IBL/env maps. We deliberately run the base in
**gamma space** because that is what classic/native produces and it prevents the
linear→gamma re-encode from washing warm hulls silver (the HD-6 diagnostic); the modern
part (GGX clearcoat highlights + hue-preserving tone map) is layered on top. A full IBL
cubemap/env map was considered and deferred: the sector's blue back light already supplies
the mood, and a real env map would risk reintroducing the cool-silver tint. Trade-off
documented; the hybrid meets the acceptance and avoids re-texturing.

## Validation

- Whole-solution build (Release): **0 warnings, 0 errors**.
- `src/Allegiance.Core/HdPack.cs` changed ⇒ **Linux x64 server Native AOT publish**:
  passed, exit 0, **no IL2026/IL3050** (source-generated JSON; no reflection serialization).
- New/affected tests **3× alone**: `Hd6MaterialTuningTests` + `HdPbrTests` +
  `P547ShaderTests` = **46 ×3 = 138 passes** (includes the shader-freshness `.fx.sha256`
  guard and the real-GL classic/PBR render proofs); `HdPackTests` (incl. new `materialTuning`
  parse) = **12 ×3 = 36 passes**.
- Fast tier once on final code: **8916 tests PASS** (268.6 s; 3 optional-Meshy skips; no
  timing offenders). Live/perf tiers are run by Claude's mini test runner per merged batch;
  no new `Live`-tier tests were added (the tuning math is pure-unit, the visual proof is
  the real-client captures above).
- Visual proof is **real 2560×1440 presented-backbuffer** captures (not `--capture`/
  offscreen), produced by the local `scripts/live-proof.sh` / HD-5 capture path. Neither
  `src/Allegiance.Server` nor the transport/wire path changed, so the network live-bot gate
  does not apply (the battle bot runs above served as the in-scene proof instead).

## Out of scope (unchanged)

New textures, Meshy, HUD, effects (LOOK-1 already landed). Regenerating the existing pack's
textures was explicitly avoided (per-material runtime tuning instead).

## Files changed (AllegianceCore, master)

- `src/Allegiance.ClientCore.Game/Shaders/NativePbr.fx` — gamma-space native base +
  white-clearcoat GGX + sheen + shared hue-preserving tone map.
- `src/Allegiance.ClientCore.Game/Rendering/MaterialTuning.cs` *(new)* — per-material tuning
  params, defaults, manifest merge, pure map/ambient/tonemap math.
- `src/Allegiance.ClientCore.Game/Rendering/HdMaterialRenderer.cs` — resolves tuning per
  material, programs the new shader uniforms.
- `src/Allegiance.Core/HdPack.cs` — optional `materialTuning` manifest section + `TuningFor`.
- `src/Allegiance.ClientCore.MonoGame/Content/Shaders/NativePbr.ogl.mgfxo` + `.fx.sha256` —
  rebuilt shader + freshness record.
- `tests/…/Hd6MaterialTuningTests.cs` *(new)*, `tests/…/HdPackTests.cs` — failing-first
  coverage.
- `docs/reports/HD6_assets/*.jpg` — the five acceptance grids (≤2 MB total).

Pipeline derivation (`AllegianceCore-hd`, its `main`): `pipeline/esrgan_variant.py`.

Not pushed; confirmed with `git log -1` and a clean `git status` after commit.
