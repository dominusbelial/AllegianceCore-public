# P514 — Omicron (oh) Meshy regeneration: submit → convert → wire in → live proof

Date: 2026-10-04 · Branch: `master` (committed, not pushed) ·
Task: `docs/prompts/p514-meshy-oh-prompt.md`

This task regenerates **Omicron Hive**'s player-flyable native ship models
through Meshy Multi-Image-to-3D (reference images from the original decoded
`.mdl` models), converts and installs them, wires **7 brand-new P287 asset
slots** (`oh-int`, `oh-interceptor`, `oh-stealthfighter`, `oh-corvette`,
`oh-carrier`, `oh-cruiser`, `oh-devastator` — OH previously exposed only
bomber/fighter/scout), and proves live that every Omicron player hull now
resolves to a dedicated model instead of the `bios_scout` fallback.

Prereqs verified: P510 commit `a2d127ba` present; plan
`docs/reports/P510_MESHY_SUBMISSION_PLAN.md` present; balance read before any
spend.

## Scope note: 10 jobs, not 12

P510's oh table lists 12 native models, but its own class-sharing rule
collapses them onto the resolved slots (follows P512's established approach):

- `faohshafig` + `faohshfig` → **one** `oh-fighter` slot
- `faohshass` + `faohshcrv` → **one** `oh-corvette` slot
- one model each → `oh-scout`, `oh-bomber`, `oh-int`, `oh-interceptor`,
  `oh-stealthfighter`, `oh-carrier`, `oh-cruiser`, `oh-devastator`

Unlike giga's x-variants (topology-identical), the OH same-role pairs are
**genuinely distinct meshes** (afig 1070/1210 vs fig 1030/1156 verts/tris;
ass 3295/2981 vs crv 1417/1442). But under the resolver each slot holds
**exactly one mesh** — the second mesh of a pair can never render, so
submitting it would burn 30 credits on a dead GLB. Minimum fully-utilized
set = the 10 slot representatives (first-listed model of each slot row in
P510). **10 jobs × 30 = 300 credits.**

## 1. Credit check & submission

Balance checked first (read-only `GET /openapi/v1/balance`): **970 credits**
— comfortably above this civ's cost. All 10 OH models submitted as **one
Multi-Image-to-3D job each** from the 4-angle native reference renders
(`mdl_render_pngs/*_front/_three_quarter/_side/_back.png`), `meshy-7`, 2k
texture, PBR. No text-to-3D, no invented art.

| Slot | ← Native model | Job id | Credits | Status |
|---|---|---|---|---|
| oh-bomber (replaced) | faohshbmb | 01a105a9-431e-722f-b377-9091aaf9941b | 30 | SUCCEEDED |
| oh-carrier (NEW) | faohshcar | 01a105b2-cf9e-7668-b7a5-381ef8f27129 | 30 | SUCCEEDED |
| oh-corvette (NEW) | faohshass | 01a105b1-1eac-71d4-ac4d-9975570ac7db | 30 | SUCCEEDED |
| oh-cruiser (NEW) | faohshcrs | 01a105b4-ec6b-71e0-931a-eb9652e3be49 | 30 | SUCCEEDED |
| oh-devastator (NEW) | faohshdev | 01a105b6-ca73-77bd-b2b5-f6696992975a | 30 | SUCCEEDED |
| oh-fighter (replaced) | faohshafig | 01a105a5-76df-74c5-bea0-8118c84eb364 | 30 | SUCCEEDED |
| oh-int (NEW) | faohshint | 01a105aa-dfb7-7321-9d0e-6313beef26f4 | 30 | SUCCEEDED |
| oh-interceptor (NEW) | faohshhint | 01a105ac-a902-7378-b82c-9e3c3cfd20cc | 30 | SUCCEEDED |
| oh-scout (replaced) | faohshsctb | 01a105a7-7c9c-73e1-bc64-b6b6b8888b2d | 30 | SUCCEEDED |
| oh-stealthfighter (NEW) | faohshstl | 01a105ae-c359-7752-8e13-70efe8cdbf16 | 30 | SUCCEEDED |

**Spent: 300 credits (10 × 30).** Post-submission balance re-read: **670**
(970 − 300). GLBs + Meshy thumbs downloaded to
`~/Development/allegiance-tools/reference/oh_meshy_glbs/`.

## 2. Convert & install

Staging (`oh_stage/`) — copied (snap-Blender can't read symlinks) each GLB
renamed to its asset slot (already named by asset); then `glb_to_fbx.py`
(headless Blender 5.2.2) and `extract_meshy_textures.py` (`/usr/bin/python3`,
which has Pillow).

| Slot | ← Source GLB | Decimation | FBX | Texture |
|---|---|---|---|---|
| oh-bomber (replaced) | faohshbmb | 172536→20000 | 912348 B | 1024², 1524313 B |
| oh-carrier (NEW) | faohshcar | 591964→20000 | 988956 B | 1024², 1907569 B |
| oh-corvette (NEW) | faohshass | 373320→20000 | 962668 B | 1024², 1704945 B |
| oh-cruiser (NEW) | faohshcrs | 500258→20000 | 1022844 B | 1024², 1982283 B |
| oh-devastator (NEW) | faohshdev | 285184→20000 | 931196 B | 1024², 1827096 B |
| oh-fighter (replaced) | faohshafig | 698796→20000 | 1062492 B | 1024², 1802708 B |
| oh-int (NEW) | faohshint | 577286→20000 | 959324 B | 1024², 1894929 B |
| oh-interceptor (NEW) | faohshhint | 423756→20000 | 994076 B | 1024², 1908850 B |
| oh-scout (replaced) | faohshsctb | 432160→20000 | 1040844 B | 1024², 1478830 B |
| oh-stealthfighter (NEW) | faohshstl | 1015174→20000 | 1102828 B | 1024², 2097004 B |

All 10 conversions clean: rc=0, no ERROR/Traceback, all hit the 20k tri cap.
Textures: all `oh-*` lines printed `OK` (the other MISS lines are the expected
harmless noise — the extractor exits 1 on those, do not treat as failure).
Installed FBX → `Content/Models/`, PNGs → `Content/Textures/ships/`.

## 3. Wiring (7 new slots)

OH previously had only `oh-bomber/fighter/scout`; the 7 new slots:

1. `Content/Models/MODELS_MANIFEST_P287.json` + `.md` — added
   `oh-carrier/corvette/cruiser/devastator/int/interceptor/stealthfighter`
   (83→**90** ships); refreshed all 10 OH tris/maxDim to the freshly measured
   geometry (below).
2. `Content/Content.mgcb` — 7 new `FbxImporter`/`ModelProcessor` blocks +
   7 new texture blocks (alphabetised into the existing OH sections).
3. `HullModelResolver.ShipAssets` — added the 7 (83→**90**).
4. `HullModelResolver.ClassToken` — added an `oh` case mapping
   `"int" => "int"` so `faohshint` (standard Lt Interceptor) resolves to its
   own `oh-int` slot instead of colliding with `faohshhint` (Hvy Interceptor →
   `oh-interceptor`) via `DefaultToken("int") = "interceptor"`. Everything
   else flows through `DefaultToken`.
5. `tools/extract_meshy_textures.py` `SHIP_ASSETS` — added the 7 to stay in
   sync.
6. `ModelOrientationOverrides.Extents` — added/updated all 10 OH extents to
   the freshly measured values (below).

`RosterPlanner` needed no change (`oh` → `["faohsh"]` prefix and `("oh", 5)`
FactionOrder entry already exist).

Tests updated to the new counts: `HullModelResolverTests` (83→90, +1 new OH
coverage test `All_oh_player_hulls_resolve_dedicated_no_fallback`, count 29),
`RosterPlannerTests` (total 83→90, oh column 3→10).

## 4. Orientation audit (P520 gate)

Re-ran P520's headless audit (`roster-capture orient`) over the compiled
content. **All ten OH assets are decisively X-long → `Ok`; no rotation
override needed** (contrast P511's dreg, which had two Z-long flags):

| asset | x | y | z | long axis | verdict |
|---|---|---|---|---|---|
| oh-bomber | 190.075 | 37.167 | 114.236 | X | Ok |
| oh-carrier | 190.183 | 41.860 | 66.566 | X | Ok |
| oh-corvette | 189.224 | 29.974 | 61.592 | X | Ok |
| oh-cruiser | 190.235 | 66.159 | 76.660 | X | Ok |
| oh-devastator | 190.154 | 27.139 | 63.973 | X | Ok |
| oh-fighter | 190.066 | 39.836 | 104.478 | X | Ok |
| oh-int | 190.292 | 36.940 | 107.609 | X | Ok |
| oh-interceptor | 190.570 | 37.359 | 108.267 | X | Ok |
| oh-scout | 190.385 | 58.341 | 57.163 | X | Ok |
| oh-stealthfighter | 190.202 | 169.093 | 172.450 | X | Ok |

Contact sheet: `P514_MESHY_OH_assets/orient_after.jpg` (all 90 ships) +
`orient_extents.json` (per-asset raw extents).

## 5. Build

- MonoGame content pipeline (MGCB) recompiled the new `.fbx`→`.xnb` +
  `.png`→`.xnb`: **0 warnings, 0 errors** (fresh Debug timestamps on all new
  XNBs prove recompile).
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.

## 6. Live proof

Booted `vizfight roster 8099 oh` (real 9-faction server; the `oh` filter
spawns Omicron's real bots on side 7). Transport on `127.0.0.1:39015`.

- **`roster-capture dump`** → `roster_oh.jpg`: **10/10 distinct JPEG sizes,
  10/10 distinct tri counts** — every OH ship is distinct geometry. Each
  asset resolved to a real native hull in `roster_dump.json` (e.g.
  `oh-fighter` ↔ faohshafig hull 714 "Enh Fighter", `oh-carrier` ↔ faohshcar
  hull 725 "Carrier [SR]").
- **`roster-capture wire`** (real `ConnectionSession` over the wire, side 7,
  civId 5=oh): received **oh-bomber, oh-devastator, oh-fighter, oh-int,
  oh-interceptor, oh-scout, oh-stealthfighter** live (the new slots arrive
  over the real EXPORT/UPDATE path), `roster_net_oh_wire.jpg`. (Bot reseats
  make the received set vary run to run — environmental.)

### Resolver coverage (before → after)

Measured over the real PCore001 via `HullModelResolver` on OH's
player-flyable (`GroupId >= 0`) `faohsh`-prefix hulls:

| | fallback → bios_scout | dedicated model |
|---|---|---|
| **Before (P510)** | **16 / 29** | 13 / 29 |
| **After (P514)** | **0 / 29** | **29 / 29** |

Every Omicron player hull now resolves to a dedicated model (10 distinct
assets: bomber, carrier, corvette, cruiser, devastator, fighter, int,
interceptor, scout, stealthfighter). Per-asset distribution after:
oh-bomber 5, oh-carrier 1, oh-corvette 2, oh-cruiser 1, oh-devastator 2,
oh-fighter 5, oh-int 4, oh-interceptor 2, oh-scout 3, oh-stealthfighter 4.

### Scale check (objective)

Each new model's raw `RenderRadius` (the compiled geometry) maps through the
runtime `ShipRenderScale` to exactly **0.5 × HullType.Length**:

| asset | Length | raw renderR | rendered = 0.5×L |
|---|---|---|---|
| oh-bomber | 40.0 | 113.559 | 20.00 |
| oh-carrier | 300.0 | 95.095 | 150.00 |
| oh-corvette | 75.0 | 94.616 | 37.50 |
| oh-cruiser | 300.0 | 95.798 | 150.00 |
| oh-devastator | 225.0 | 95.079 | 112.50 |
| oh-fighter | 16.2 | 96.794 | 8.10 |
| oh-int | 15.3 | 99.894 | 7.70 |
| oh-interceptor | 15.3 | 97.298 | 7.70 |
| oh-scout | 16.5 | 95.357 | 8.20 |
| oh-stealthfighter | 11.0 | 117.950 | 5.50 |

Proportional auto-normalization (carrier 300→150, fighter 16.2→8.1), no
per-model tuning.

### Dark livery

All ten OH textures are healthy (mean luminance 71.6–104.4, **97.5–100%
non-black**, max 201–255) — none is a missing/blank texture.

## 7. Tests

Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (all numbers
actually measured this session):

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | **1211/1211** | **1211/1211** | **1211/1211** |
| Allegiance.ClientCore.Tests | 436/438 | 438/438 | 437/438 |
| Allegiance.Core.Tests | 3707/3708 | 3706/3708 | 3708/3708 |

My project — **Allegiance.ClientCore.MonoGame**, which hosts the
resolver/roster/orientation/manifest tests (incl. the new OH coverage test) —
was **fully green 1211/1211 on all three runs**. Every failure elsewhere is a
pre-existing, rotating live-wire/load-timing flake documented in
`docs/TEST_FLAKES.md` (none reference any P514 symbol), and the set changes
run to run:

- Run 1: `FullSessionKillIntegration…RealCrossSideKill` (P472 suspect) +
  `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`
  (live-wire, line 51) [ClientCore]; `CargoBayReloadTests.SwapPart_negative_mount…`
  (rotating member, lines 53-54) [Core].
- Run 2: `AutoReloadOnEmpty…RELOAD129` + `LiveTreasurePickupTests.Rescue_ship…`
  (both load-flake, lines 53/247) [Core]; ClientCore fully green.
- Run 3: `SeedThreePeers…DeterministicVisibility` (live-wire, line 38/51)
  [ClientCore]; Core fully green (3708/3708).

## 8. Out of scope (untouched)

- Other civs (tf/phoenix — their own tasks P513/P515). Pushing.

## Files committed

- `Content/Models/MODELS_MANIFEST_P287.json` / `.md` (83→90 ships)
- `Content/Content.mgcb` (7 model + 7 texture blocks)
- `Content/Models/oh-{bomber,carrier,corvette,cruiser,devastator,fighter,int,interceptor,scout,stealthfighter}.fbx`
  (bomber/fighter/scout replaced, 7 new)
- `Content/Textures/ships/oh-{...}.png` (same 10)
- `src/Allegiance.Core/Content/HullModelResolver.cs` (ShipAssets 83→90,
  ClassToken `oh` case)
- `src/Allegiance.Core/Content/ModelOrientationOverrides.cs` (Extents)
- `tools/extract_meshy_textures.py` (SHIP_ASSETS)
- tests: `HullModelResolverTests` (+1 OH coverage, 83→90), `RosterPlannerTests`
  (83→90, oh 3→10)
- this report + `docs/P514_MESHY_OH_assets/` (proof screenshots)

Large binary assets committed directly (no LFS) per the P351–P353/P511–P513
convention (`.glb` source files are untracked; only the converted `.fbx` +
`.png` are committed). `tools/vizfight/roster_dump.json` remains untracked
(build artifact).
