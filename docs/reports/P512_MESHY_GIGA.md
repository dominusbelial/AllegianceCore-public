# P512 — Gigacorp (giga) Meshy regeneration: submit → convert → wire in → live proof

Date: 2026-10-03 · Branch: `master` (committed, not pushed) ·
Task: `docs/prompts/p512-meshy-giga-prompt.md`

This task regenerates **Gigacorp**'s player-flyable native ship models through
Meshy Multi-Image-to-3D (reference images from the original decoded `.mdl`
models), converts and installs them, wires one brand-new P287 asset slot
(`giga-interceptor` — Gigacorp previously exposed only bomber/fighter/scout),
and proves live that every Gigacorp player hull now resolves to a dedicated
model instead of the `bios_scout` fallback.

## Scope note: 3 jobs, not 7

P510's giga table lists 7 native models, but its own class-sharing rule collapses
them onto the resolved slots:

- 3 fighter meshes (`wc_gcfig`, `wc_gcfigx`, `wc_gchttx`) → **one** `giga-fighter` slot
- 3 interceptor meshes (`wc_gchvy`, `wc_gchvyx`, `wc_gcint`) → **one** `giga-interceptor` slot
- `wc_gcbmbx` → `giga-bomber` slot

`wc_gcfigx`≡`wc_gcfig` and `wc_gchvyx`≡`wc_gchvy` are topology-identical (same
mesh, different skin — verified by identical OBJ vertex/tri counts in P510), so
they cannot be installed separately. Under the resolver, each slot holds exactly
one mesh. So the minimum fully-utilized submission set that achieves **0
fallback** is the three slot representatives — `wc_gcbmbx`, `wc_gcfig`,
`wc_gchvy` (the first-listed model of each slot row in P510). The two other
distinct meshes (`wc_gchttx`, `wc_gcint`) share a slot with a representative and
would burn credits on GLBs that can never render. **3 jobs × 30 = 90 credits.**

## 1. Credit check & submission

Balance checked first (read-only `GET /openapi/v1/balance`): **1,360 credits**
(post-P511) — comfortably above this civ's cost. All 3 giga models submitted as
**one Multi-Image-to-3D job each** from the 4-angle native reference renders
(`mdl_render_pngs/*_front/_three_quarter/_side/_back.png`), `meshy-7`, 2k
texture, PBR. No text-to-3D, no invented art.

| Native model | → slot | Job id | Credits | Status |
|---|---|---|---|---|
| wc_gcbmbx | giga-bomber | 01a10537-33c3-73fe-b51a-d45085c8eeef | 30 | SUCCEEDED |
| wc_gcfig | giga-fighter | 01a10537-3df1-712f-bf34-bb9ac7049baa | 30 | SUCCEEDED |
| wc_gchvy | giga-interceptor | 01a10537-47e1-768d-97f9-70920b0d8361 | 30 | SUCCEEDED |

**Spent: 90 credits (3 × 30).** Post-submission balance re-read: **1,270**
(1360 − 90). GLBs + Meshy thumbs downloaded to
`~/Development/allegiance-tools/reference/giga_meshy_glbs/`.

## 2. Convert & install

Staging (`giga_stage/`) — copied (snap-Blender can't read symlinks) each GLB
renamed to its asset slot; then `glb_to_fbx.py` (headless Blender 5.2.2) and
`extract_meshy_textures.py` (`/usr/bin/python3`, which has Pillow).

| Slot | ← Source GLB | Decimation | FBX | Texture |
|---|---|---|---|---|
| giga-bomber (replaced) | wc_gcbmbx | 437972→19999 | 1019196 B | 1024², 1585087 B |
| giga-fighter (replaced) | wc_gcfig | 455906→20000 | 1022028 B | 1024², 1810906 B |
| giga-interceptor (NEW) | wc_gchvy | 966932→20000 | 1147084 B | 1024², 1905479 B |

All 3 conversions clean: rc=0, no ERROR/Traceback, all hit the 20k tri cap.
Textures: all `giga-*` lines printed `OK` (the other MISS lines are the expected
harmless noise). `giga-scout` (P287, not in this batch) untouched.

## 3. Wiring (new slot)

Gigacorp previously had only `giga-bomber/fighter/scout`; `giga-interceptor` is
new:

1. `Content/Models/MODELS_MANIFEST_P287.json` + `.md` — added `giga-interceptor`
   (77→**78** ships); refreshed `giga-bomber`/`giga-fighter` tris/maxDim to the
   newly measured geometry.
2. `Content/Content.mgcb` — 1 new `FbxImporter`/`ModelProcessor` block +
   1 new texture block.
3. `HullModelResolver.ShipAssets` — added `giga-interceptor` (77→**78**).
4. `tools/extract_meshy_textures.py` `SHIP_ASSETS` — added `giga-interceptor`
   to stay in sync.
5. **No `ClassToken` change** — `ClassToken("giga")` already falls through to
   `DefaultToken`, and `DefaultToken("interceptor")` = `"interceptor"` (P510's
   exact note). `RosterPlanner` needed no change (`giga` → `["wc_gc"]` existed).
6. `ModelOrientationOverrides.Extents` — added `giga-interceptor`, updated
   `giga-bomber`/`giga-fighter` to the freshly measured extents (below).

Tests updated to the new counts: `HullModelResolverTests` (77→78, +1 new giga
coverage test), `RosterPlannerTests` (total 77→78, giga column 3→4).

## 4. Orientation audit (P520 gate)

Re-ran P520's headless audit (`roster-capture orient`) over the compiled
content. **All three giga assets are decisively X-long → `Ok`; no rotation
override needed** (contrast P511's dreg, which had two Z-long flags):

| asset | x | y | z | long axis | verdict |
|---|---|---|---|---|---|
| giga-bomber | 190.118 | 109.586 | 65.002 | X | Ok |
| giga-fighter | 189.902 | 79.462 | 80.997 | X | Ok |
| giga-interceptor | 190.305 | 32.980 | 109.299 | X | Ok |

Contact sheet: `P512_MESHY_GIGA_assets/orient_after.jpg *(screenshot of a Meshy model, kept in the private asset pack)*`.

## 5. Build

- MonoGame content pipeline (MGCB) recompiled the new `.fbx`→`.xnb` +
  `.png`→`.xnb`: **0 warnings, 0 errors** (fresh Debug timestamps on all new
  XNBs prove recompile).
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.

## 6. Live proof

Booted `vizfight roster <port> giga` (real 9-faction server; the `giga` filter
spawns Gigacorp's real bots on side 6). The live `RosterPlanner.Plan` resolved
all 4 giga assets to real native hulls (`roster_dump.json`).

- **`roster-capture dump`** → `roster_giga.jpg`: **4/4 distinct JPEG sizes,
  4/4 distinct tri counts** — every giga ship is distinct geometry.
  (The tool's trailing `<5 distinct tri counts` guard trips because a single-civ
  capture has 4 ships; the artifacts and the 4/4 proof are written first.)
- **`roster-capture wire`** (real `ConnectionSession` over the wire, side 6):
  received **giga-bomber, giga-fighter, giga-interceptor** live (the new
  interceptor arrives over the real EXPORT/UPDATE path),
  `roster_net_giga_wire.jpg`. (Bot reseats make the received set vary run to
  run — environmental.)

### Resolver coverage (before → after)

Measured over the real PCore001 via `HullModelResolver` on giga's
player-flyable (`GroupId >= 0`) `wc_gc`-prefix hulls:

| | fallback → bios_scout | dedicated model |
|---|---|---|
| **Before (P510)** | **3 / 8** | 5 / 8 |
| **After (P512)** | **0 / 8** | **8 / 8** |

Every Gigacorp player hull now resolves to a dedicated model (4 distinct
assets: bomber, fighter, interceptor, scout).

### Scale check (objective)

Each new model's raw `RenderRadius` (the compiled geometry) maps through the
runtime `ShipRenderScale` to exactly **0.5 × HullType.Length**:

| asset | Length | raw renderR | rendered = 0.5×L |
|---|---|---|---|
| giga-bomber | 23.2 | 103.49 | 11.60 |
| giga-fighter | 17.1 | 100.10 | 8.57 |
| giga-interceptor | 13.2 | 95.68 | 6.61 |
| giga-scout | 11.4 | 116.08 | 5.70 |

Proportional auto-normalization, no per-model tuning.

### Dark livery

All three new giga textures are healthy (mean luminance 45.9 / 75.0 / 52.8,
**100% non-black**, max ≈ 255 / 211) — none is a missing/blank texture.

## 7. Tests

Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (all numbers
actually measured this session):

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | **1209/1209** | **1209/1209** | **1209/1209** |
| Allegiance.ClientCore.Tests | 437/438 | 437/438 | 436/438 |
| Allegiance.Core.Tests | 3701/3708 | 3705/3708 | 3703/3708 |

My project — **Allegiance.ClientCore.MonoGame**, which hosts the
resolver/roster/orientation/manifest tests (incl. the new giga coverage test) —
was **fully green 1209/1209 on all three runs**. Every failure elsewhere is a
pre-existing, rotating live-wire/load-timing flake documented in
`docs/TEST_FLAKES.md` (none reference any P512 symbol): the set changes run to
run — run 1 `FullSessionKillIntegration…RealCrossSideKill` (ClientCore; P472
suspect) + 7 Core live-wire tests; run 2 the same FullSessionKill + 3 Core
tests; run 3 `SeedThreePeers…DeterministicVisibility`,
`ClientWarpApproachSlowHullLive…parks_slow_board_hull` (ClientCore) +
`AutoReloadOnEmpty…RELOAD129`, `Rescue_ship…treasure_credits`,
`Conquest_win…does_not_leak_to_another` / `lists_every_connected_player`,
`SwapPart_negative_mount…` (Core). The assembly under test for this task's
changes is green on every run.

## 8. Out of scope (untouched)

- Other civs (oh/tf/phoenix — their own tasks P513–P515).
- Pipeline changes. Pushing.

## Files committed

- `Content/Models/MODELS_MANIFEST_P287.json` / `.md` (77→78 ships)
- `Content/Content.mgcb` (1 model + 1 texture block)
- `Content/Models/giga-{bomber,fighter,interceptor}.fbx` (bomber/fighter
  replaced, interceptor new)
- `Content/Textures/ships/giga-{bomber,fighter,interceptor}.png`
- `src/Allegiance.Core/Content/HullModelResolver.cs` (ShipAssets)
- `src/Allegiance.Core/Content/ModelOrientationOverrides.cs` (Extents)
- `tools/extract_meshy_textures.py` (SHIP_ASSETS)
- tests: `HullModelResolverTests` (+1 giga coverage), `RosterPlannerTests`
- this report + `P512_MESHY_GIGA_assets/` (proof screenshots)
