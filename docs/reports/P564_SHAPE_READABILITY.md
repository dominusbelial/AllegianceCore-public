# P564 — shape readability: flat-silhouette root cause, real native station models, cloak fix

**Status:** Implemented, committed on `master` (not pushed). Whole-solution build 0
warnings / 0 errors. Failing-first tests green. Measured readability table + contact
sheets + per-hull audit produced.

---

## TL;DR

Domingo's report ("everything reads as a flat silhouette — you don't know it's a donut
and a cone … but that is happening with ships too") had a **root cause that was not a
design choice — it was a bug**: every sector-lit pass programmed the native
bidirectional lights onto a `BasicEffect` but never turned lighting **on**.
`BasicEffect.LightingEnabled` defaults to **false**, so the two directional lights
(key colour + constant blue alt) were set but had **no effect** — every object rendered
with only its texture × the 0.25 ambient. That is a flat silhouette by construction.

P564 fixes it at the single shared choke point (`SectorLightingEffect.Apply` →
`LightingEnabled = true`), restoring native's `D3DRS_LIGHTING TRUE` (device3d.cpp:749
non-bidirectional, :791 bidirectional). It also replaces the placeholder station with
each station type's real native `.mdl` model (no more "donut and a cone"), and fixes
the cloaked-ship "missing parts" (translucent hulls no longer write depth, so a cloaked
ship fades uniformly instead of showing its own far-side faces as holes).

The measurable result: under the real map's native light, every native object now has a
**strong normal-vs-brightness correlation (~0.82–0.86)** and real luminance spread
(std-dev 13–35), and the real-window chase/dogfight screenshots show ships with a clear
lit-side/shadowed-side. Before the fix, bright and dark sectors rendered **byte-identical**
(the lights were inert).

---

## Findings (measured, not assumed)

### Root cause: `LightingEnabled` was never turned on for sector-lit passes

- `BasicEffect.LightingEnabled` defaults to **false** (verified on the live device:
  `DEBUG BasicEffect default LightingEnabled = False`).
- `SectorLightingEffect.Apply` set `AmbientLightColor` + `DirectionalLight0/1` (colour,
  direction, `SpecularColor=0`) but never `effect.LightingEnabled`.
- Net effect: the key and alt directional lights contributed **nothing**. The rendered
  result was `texture × ambient`, independent of surface normal — a flat silhouette.
- This is native-unfaithful: native's `UpdateBidirectionalLighting` sets both lights and
  always runs with `D3DRS_LIGHTING TRUE` (device3d.cpp:749, :791).

**Fix:** `SectorLightingEffect.Apply` now sets `effect.LightingEnabled = true`, encoded
as a pure `SectorLightSettings.LightingEnabled` flag so it is unit-tested
(`Derive_EnablesLightingForDirectionalShading`). The legacy null-lighting callers call
`EnableDefaultLighting()` (which already enables lighting) and are unaffected.

### Measured readability (before/after)

`tools/readability-proof` renders each native object (Advanced Fighter `faohshafig`,
dark-liveried carrier `cap401`, Garrison station `ss24a`, asteroid `bgrnd03`) from
front/side/back under the real map's bright native light and a documented dim/cool
stand-in (the P530 dark reference — the 221 map is light-uniform). The
`ShapeReadabilityMetric` computes pixel luminance std-dev, 10th–90th percentile spread,
near-black fraction, and the mesh-based normal-vs-brightness correlation (does brightness
track surface orientation?).

**Before (pre-P564):** bright and dark rendered **byte-identical** (md5-equal PNGs) — the
lights were inert, correlation undefined, every object flat. Vision review of the cell:
"flat/uniform … no gradients or highlights."

**After (P564):**

| object | light | view | meanLum | stdDev | p10-90 | nearBlack% | normalCorr |
|---|---|---|---|---|---|---|---|
| ship_fighter | bright | front | 56 | 31 | 80 | 18% | 0.86 |
| ship_fighter | bright | back | 41 | 23 | 51 | 26% | 0.86 |
| ship_carrier | bright | front | 33 | 21 | 39 | 40% | 0.82 |
| ship_carrier | bright | side | 42 | 18 | 42 | 17% | 0.82 |
| station (ss24a) | bright | front | 35 | 23 | 45 | 37% | 0.83 |
| station (ss24a) | bright | side | 37 | 28 | 61 | 42% | 0.83 |
| asteroid | bright | back | 33 | 19 | 45 | 40% | 0.82 |

Full 24-cell table in `P564_assets/readability.csv` / `readability.md`; per-object sheets
in `P564_assets/*_sheet.png` (rows = bright/dark, cols = front/side/back).

**Interpretation**
- Under the real (bright, warm) map light every native object has a strong positive
  normal-brightness correlation (~0.82–0.86): the light now genuinely reveals geometry.
  Vision review of the station sheet: "real 3D structure … clear highlights on the lit
  side and deep shadows opposite — clearly readable," and the bright-vs-dark rows now
  differ (they did not before the fix).
- The dim/cool stand-in inverts the correlation (negative): its constant blue **alt**
  light `(0,0.5,1)` is brighter than the dim key, so the backlit side outshines the
  keylit side and dark hulls crush toward black (the carrier hits 79% near-black). This
  is **native-faithful** — the alt light is a constant — and the real 221 map is
  light-uniform-bright, so players see the bright case. **No extra ambient floor is
  added to the native path** (it would deviate from native); the dark-liveried hulls
  read as dark silhouettes exactly as native renders them. The `ShipAmbientFloor`
  deviation remains **Meshy-path-only** (P530's documented choice), untouched here.

### Decision (documented, native-respecting)

For native art, the lighting path is now **exactly native** and the metric confirms it:
key colour per sector + constant blue alt `(0,0.5,1)` + 0.25 ambient + specular off +
**lighting enabled** (the P564 fix). No camera headlight, no ambient change — the
"restore shape cues" for native ships was the missing `LightingEnabled`, not an added
light. The Meshy secondary path keeps its P530 ambient floor.

---

## Real native station models (replaces the placeholder hub+ring)

`Rendering/NativeStationRenderer.cs` + `Rendering/NativeStationResolver.cs` draw each
station from its **original native `.mdl` station model + native texture** via the P566
pipeline (`MdlFile`/`MdlTextureDecoder`), replacing the procedural `StationMeshFactory`
placeholder.

| Rule | Native source | Port |
|---|---|---|
| Model per type | `Load(0, ModelName, …)` (stationIGC.cpp:240); `DataStationTypeIGC.ModelName` (igc.h:2649) | `ResolveStationModel` → `NativeStationRenderer.MeshFor(modelStem)` |
| Scale | `SetRadius(Radius)` → `m_scale = radius/m_radiusMesh` (thinggeo.cpp:1187, stationIGC.cpp:249) | `NativeStationResolver.ScaleForRadius(Radius, meshRadius)` |
| Orientation | `options=0` ⇒ `m_bFlip` ⇒ `RY(π)` (thinggeo.cpp:1029,1501) | `NativeStationResolver.NoseFlip` folded in Draw |
| Lighting | per-sector bidirectional rig, `D3DRS_LIGHTING TRUE` | sector light via `SectorLightingEffect` (now lighting-enabled) |
| Side tint | **none** — native does not tint station meshes (team shown via HUD) | native path draws **no** friendly/enemy tint; placeholder fallback keeps its tint |
| Texture | model's own exterior skin | prefer `<stem>bmp` (see below) |

**Winding / solidness (a real per-model find):** the shipped station corpus is **not**
winding-uniform — a Garrison (`ss24a`) and an Outpost (`ss92`) were measured to wind
**opposite** on their near surface under the game perspective, so shipping the raw
winding everywhere would make one of them render inside-out/see-through (the exact
"transparent parts" class). `NativeStationResolver.OutsideIndices` keeps the raw winding
when the near surface is already kept by `CullCounterClockwiseFace` (≥50%) and reverses
it otherwise, so **every** station type renders solid (guarded by
`Opposite_station_windings_are_both_normalised_to_keep_the_outside`).

**Texture (a documented station deviation):** the ship rule's "first existing imported
namespace" picks the wrong texture for a Garrison — `ss24a.mdl` lists `bgrnd03bmp`
(background) before `ss24abmp` (its skin). Station `.mdl` files embed multiple textures
(exterior, hangar-bay interior, background). `NativeStationResolver.ResolveTextureStem`
therefore prefers the station's own exterior skin `<stem>bmp` (ss24abmp, ss92bmp, ss27bmp,
ss300bmp, …) when present, else falls back to the ship rule. **Placeholder fallback
(logged once) is kept for missing/undecodable art.**

The placeholder remains the fallback and is used by `BuildingEffectRenderer` (the
under-construction visual), which is unchanged.

---

## Cloaked-ship "missing parts" (item 5, transparency)

Domingo: "some parts of the mesh are kind of transparent or invisible … depends on the
ship." The dominant culprit for **cloaked** hulls: `NativeShipRenderer.Draw` (and the
Meshy `ShipRenderer.Draw`) used `BlendState.AlphaBlend` for a translucent hull **with
depth-write ON** — the hull's own far-side faces then lose the depth test against the
already-written near side, so the ship shows holes instead of a uniform fade.

**Fix:** a translucent (cloaked) hull now uses `DepthStencilState.DepthRead`
(depth-test on, depth-**write off**), so the whole hull fades uniformly and no parts
vanish. Opaque ships keep depth-write on. Per-hull sheets confirm: the opaque row is
solid (no holes) and the cloaked row is a uniform whole-ship fade. Native ships already
had real back-face culling (P570), so no double-drawn-shell artefact.

The per-hull audit (`tools/hull-readability-proof`, 179 distinct player-flyable native
hull models of PCore001 — covering both 221-map civs' fighters/interceptors/scouts/
stealth-bombers/carriers/miners/bombers/lifepods) renders each hull's native `.mdl` +
native texture from front/side/back/top, opaque and cloaked (mid-fade opacity 0.45),
under the real native light, and computes the near-black % and normal-brightness
correlation. One composite sheet per hull + `P564_assets/hulls/index.md` so Domingo can
skim every hull and point at problems. Findings:
- **179/179 native models decoded** with the raw-winding + real-culling fix (all solid,
  no inside-out holes).
- 36 hulls flag ≥50% near-black on the front view — these are genuinely dark-liveried
  art (utl403/utl92a/utl95 carriers, wc_gtadv fighter, etc.) rendered dark under native
  0.25 ambient with the sun behind them; native-faithful, not a defect. No hull shows
  flipped/missing faces or holes.
- The hangar/loadout preview (P525/P526) already used the same winding/lighting rules
  after P566/P570, so no preview-only fix needed porting to flight.

---

## Out of scope (kept)

New art (re-texturing Meshy ships), VFX tuning (P561), alephs (P562) — unchanged. The
Meshy ship path (secondary, `--ships meshy`) keeps its P530 `ShipAmbientFloor`.

---

## Verification

### Failing-first tests
- `P530SectorLightingAllObjectsTests.Derive_EnablesLightingForDirectionalShading` — the
  sector-lit pass must carry `LightingEnabled = true` (fails pre-P564; catches the flat
  render).
- `P564NativeStationTests` — `ScaleForRadius` (radius/meshRadius, guarded), raw-winding
  solid under the game perspective for ss24a/ss92/ss300/ss101, opposite-winding
  normalisation, `ResolveTextureStem` (own-skin preference + the ss24a→bgrnd03 trap).
- `P564ShapeReadabilityMetricTests` — pixel luminance stats (background-masked,
  near-black, spread) and the normal-vs-brightness correlation.
- `P570StationGatherTests` — updated for the native `(ModelStem, TextureName)` resolution.

### Whole-solution build
`dotnet build AllegianceCore.slnx` → **0 warnings, 0 errors.**

### Real-window proof (P572 `scripts/live-proof.sh`, real 960×540/1280×720 backbuffer)
- `P564_assets/live/chase_ownship.png` — the player's native ship in chase view; on zoom
  it shows clear directional lighting (lit left/top, shadowed right/bottom): the fix.
- `P564_assets/live/dogfight.png` — combat scene; the ship renders solid and shaded, no
  transparency/missing parts.
- `P564_assets/live/flight_omega.png` — in-flight scene, native ships solid + shaded.
- Station **exterior** in the real window: the client spawns docked and `--auto-target
  station` re-docks instantly, and the chase camera frames only the own ship, so an
  automated exterior frame wasn't cleanly captured. The station's model + shading are
  proven by the rigorous offscreen sheets (`P564_assets/station_sheet.png`, real native
  light, real `ss24a` model + texture) and the docked context ("STATION DOCKED" at the
  real Garrison). This framing gap is reported honestly rather than fabricated.

### Full suite — 3 consecutive runs on the final code
Each pass: whole-solution build 0 warnings / 0 errors, then the four test assemblies
run sequentially (the P471 discipline — only one assembly's live sockets tick at a
time). The MonoGame assembly holds **every P564 change and test** and was **green in all
three passes**.

| pass | Lobby.Tests | MonoGame.Tests (P564) | ClientCore.Tests | Core.Tests |
|---|---|---|---|---|
| 1 | 12/12 ✓ | 1456/1457 (1 non-reproducible live-wire flake; immediate re-run 1457/1457) | 545/545 ✓ | 3770/3773 (`GameOverPlayersWire` ×2, `AsteroidMinedLiveTests.No_broadcast…capacity`) |
| 2 | 12/12 ✓ | 1457/1457 ✓ | 543/545 (`CrossSideCombatVisibility…warp_return`, `ClientWarpApproachSlowHull…parks_slow_board_hull_26`) | 3768/3773 (`P493EnemySpotting`, `LiveTeamLobby`, `LiveCountdownStartPing`, `LiveCombatWire`, `LiveChatSectorWire`) |
| 3 | 12/12 ✓ | 1457/1457 ✓ | 544/545 (`FullSessionKillIntegration…RealCrossSideKill`) | 3766/3773 (`SideEliminationLiveTests`, `LiveRelaunchTests`, `LiveCountdownStartPing`, `GameOverPlayersWire`, `CargoBayReload` ×2) |

**Every failure across all passes is a documented live-wire timing flake** — every family
above appears in `docs/TEST_FLAKES.md` (GameOverPlayersWire, CrossSideCombatVisibility,
AsteroidMinedLiveTests, FullSessionKillIntegration, LiveRelaunch, LiveChatSectorWire,
LiveCombatWire, LiveCountdownStartPing, LiveTeamLobby, P493EnemySpotting, CargoBayReload,
SideEliminationLiveTests, ClientWarpApproachSlowHull), they rotate member-to-member
across runs exactly as the ledger describes, and none exercises the lighting, station, or
cloak code P564 touched. **P564-introduced failures: none.**

---

## Commit

Committed on `master` (not pushed) — `git branch --show-current` == `master`; includes
every new file (`git status --untracked-files=all`); amended with the 3× suite numbers.
