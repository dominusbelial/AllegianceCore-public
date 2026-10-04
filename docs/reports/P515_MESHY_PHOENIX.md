# P515 — Phoenix (phoenix) Meshy regeneration: submit → convert → wire in → live proof

Date: 2026-10-04 · Branch: `master` (committed, not pushed) ·
Task: `docs/prompts/p515-meshy-phoenix-prompt.md`

Regenerates **Phoenix**'s player-flyable native ship models through Meshy
Multi-Image-to-3D (reference images from the original decoded `.mdl` models),
converts and installs them, wires **9 brand-new P287 asset slots** (Phoenix
previously exposed *zero* — every Phoenix hull drew as the `bios_scout`
fallback), and proves live that every Phoenix player hull now resolves to a
dedicated model.

Prereqs verified: P510 commit `a2d127ba` present; plan
`docs/reports/P510_MESHY_SUBMISSION_PLAN.md` present; balance read before any
spend.

## Scope note: 9 jobs, not 11

P510 lists 11 distinct Phoenix native models, but (following P514's established
minimum-fully-utilized-set approach) its own class-sharing rule collapses them
onto the 9 resolved slots — and under the resolver each slot holds exactly one
mesh, so the second member of a same-role pair could never render:

- `dn_faphshfig` + `dn_faphshvng` → **one** `phoenix-fighter` slot (submitted
  `dn_faphshfig`)
- `dn_faphshstl` + `dn_faphshbmbs` → **one** `phoenix-stealthfighter` slot
  (submitted `dn_faphshstl`)
- one model each → scout, bomber, int, carrier, cruiser, corvette, frigate

**9 jobs × 30 = 270 credits** (submitting the 2 dead GLBs would have burned 60
credits).

## 1. Credit check & submission

Balance checked first (read-only `GET /openapi/v1/balance`): **670 credits** —
comfortably above this civ's cost. All 9 slot representatives submitted as
**one Multi-Image-to-3D job each** from the 4-angle native reference renders
(`mdl_render_pngs/*_front/_three_quarter/_side/_back.png`), `meshy-7`, 2k
texture, PBR. No text-to-3D, no invented art.

| Slot | ← Native model | Job id | Credits | Status |
|---|---|---|---|---|
| phoenix-bomber (NEW) | dn_faphshbmb | 01a105ea-2247-7797-92da-b8c3f755b10c | 30 | SUCCEEDED |
| phoenix-carrier (NEW) | dn_faphshcar | 01a105ea-418a-72e7-8d45-43c240a94c89 | 30 | SUCCEEDED |
| phoenix-corvette (NEW) | dn_faphshdst | 01a105ea-555e-76a3-bb4f-9c25f4a65784 | 30 | SUCCEEDED |
| phoenix-cruiser (NEW) | dn_faphshcrs | 01a105ea-4b03-7753-9bfe-7c34cc851893 | 30 | SUCCEEDED |
| phoenix-fighter (NEW) | dn_faphshfig | 01a105ea-0f12-733e-a02f-f5cb9a477828 | 30 | SUCCEEDED |
| phoenix-frigate (NEW) | dn_faphshfrg | 01a105ea-5f74-761e-aba3-9409c54f3eca | 30 | SUCCEEDED |
| phoenix-int (NEW) | dn_faphshint | 01a105ea-2cc9-7007-bbe3-fcb6dcfa2310 | 30 | SUCCEEDED |
| phoenix-scout (NEW) | dn_faphshsct | 01a105ea-18c9-7759-803b-8aa4b987e513 | 30 | SUCCEEDED |
| phoenix-stealthfighter (NEW) | dn_faphshstl | 01a105ea-3774-7329-bda9-2116a069f263 | 30 | SUCCEEDED |

**Spent: 270 credits (9 × 30).** Post-submission balance re-read: **400**
(670 − 270). GLBs + Meshy thumbs downloaded to
`~/Development/allegiance-tools/reference/phoenix_meshy_glbs/`.

## 2. Convert & install

Staging (`phoenix_stage/`) — copied (snap-Blender can't read symlinks) each GLB
renamed to its asset slot; then `glb_to_fbx.py` (headless Blender 5.2.2) and
`extract_meshy_textures.py` (`/usr/bin/python3`, which has Pillow).

| Slot | ← Source GLB | Decimation | FBX | Texture |
|---|---|---|---|---|
| phoenix-bomber | dn_faphshbmb | 677100→20000 | 1050268 B | 1024², 1366098 B |
| phoenix-carrier | dn_faphshcar | 338390→19999 | 1041852 B | 1024², 1443731 B |
| phoenix-corvette | dn_faphshdst | 360574→20000 | 1046620 B | 1024², 1450867 B |
| phoenix-cruiser | dn_faphshcrs | 605232→20000 | 1067644 B | 1024², 1390540 B |
| phoenix-fighter | dn_faphshfig | 481182→20000 | 989884 B | 1024², 1490101 B |
| phoenix-frigate | dn_faphshfrg | 870128→20000 | 1098764 B | 1024², 1315973 B |
| phoenix-int | dn_faphshint | 803036→19999 | 1138732 B | 1024², 1317342 B |
| phoenix-scout | dn_faphshsct | 1940182→20000 | 1379836 B | 1024², 1602215 B |
| phoenix-stealthfighter | dn_faphshstl | 210090→19999 | 918796 B | 1024², 1299700 B |

All 9 conversions clean: rc=0, no ERROR/Traceback, all hit the 20k tri cap.
Textures: all `phoenix-*` lines printed `OK` (the other MISS lines are the
expected harmless noise — the extractor exits 1 on those, do not treat as
failure). Installed FBX → `Content/Models/`, PNGs → `Content/Textures/ships/`.

## 3. Wiring (9 brand-new slots)

Phoenix previously had **zero** P287 slots; the 9 new ones:

1. `Content/Models/MODELS_MANIFEST_P287.json` + `.md` — added
   `phoenix-bomber/carrier/corvette/cruiser/fighter/frigate/int/scout/stealthfighter`
   (90→**99** ships, `count` 93→102); refreshed all 9 tris/maxDim to the
   freshly measured geometry.
2. `Content/Content.mgcb` — 9 new `FbxImporter`/`ModelProcessor` blocks + 9 new
   texture blocks (alphabetised into the new PHOENIX section, between OH and
   RIX).
3. `HullModelResolver.ShipAssets` — added the 9 (90→**99**).
4. `HullModelResolver.ClassToken` — added a `phoenix` case mapping
   `"int" => "int"` so `dn_faphshint` resolves to its own `phoenix-int` slot
   instead of colliding into `interceptor` via `DefaultToken`. Everything else
   flows through `DefaultToken`.
5. `RosterPlanner.FactionOrder` — added `("phoenix", 4)` so Phoenix gets its own
   roster column.
6. `tools/extract_meshy_textures.py` `SHIP_ASSETS` — added the 9 to stay in
   sync.
7. `ModelOrientationOverrides.Extents` — added all 9 Phoenix extents to the
   freshly measured values (below).

`RosterPlanner.CivModelPrefixes` already had `phoenix → ["dn_faphsh"]` (P510).

Tests updated to the new counts: `HullModelResolverTests` (90→99, +1 new
Phoenix coverage test `All_phoenix_player_hulls_resolve_dedicated_no_fallback`,
count 26), `RosterPlannerTests` (total 90→99, phoenix column 0→9),
`PhoenixMappingTests` (interim-fallback test superseded by a dedicated-slots
test), `ModelOrientationOverridesTests` (override set 6→8, gate relaxed to
accept non-forward near-square corrections).

## 4. Orientation audit (P520 gate)

Re-ran P520's headless audit (`roster-capture orient`) over the compiled
content:

| asset | x | y | z | long axis | verdict | override |
|---|---|---|---|---|---|---|
| phoenix-bomber | 156.726 | 43.868 | 190.263 | Z | Ambiguous | **−90°** |
| phoenix-carrier | 190.225 | 52.080 | 106.368 | X | Ok | — |
| phoenix-corvette | 190.291 | 41.687 | 110.820 | X | Ok | — |
| phoenix-cruiser | 190.517 | 50.888 | 186.890 | X | Ok | — |
| phoenix-fighter | 189.108 | 35.533 | 170.047 | X | Ok | — |
| phoenix-frigate | 190.431 | 40.896 | 154.668 | X | Ok | — |
| phoenix-int | 190.005 | 34.172 | 150.404 | X | Ok | — |
| phoenix-scout | 189.870 | 34.443 | 166.925 | X | Ok | — |
| phoenix-stealthfighter | 160.440 | 40.524 | 190.335 | Z | Ambiguous | **−90°** |

**phoenix-bomber and phoenix-stealthfighter measured raw-Z-long** (long axis
disagrees with the forward-X convention and every sibling Phoenix ship). They
aren't elongated enough for the decisive-axis classifier to hard-flag (near
square, so "Ambiguous"), but a raw-Z-long mesh undergoes the identical
global±override transform as the Domingo-verified `ic-dev` fix (whose "before"
renders crosswise), so both get the same **−90°-about-Y** per-asset override —
added to `ModelOrientationOverrides`, *not* the global correction. Before/after
close-ups in the assets dir confirm the flip. The P520 gate test was extended to
accept overrides for any raw **non-forward** mesh (not only decisively-flagged
ones) — otherwise the near-square rotation case would be unrepresentable.

Contact sheet: `P515_MESHY_PHOENIX_assets/orient_before.jpg` (global-only, the
two ships crosswise) + `orient_after.jpg` (fixed) + per-asset
`orient_flag_phoenix-{bomber,stealthfighter}_{before,after}.jpg`;
`orient_extents.json` (full 99-row data).

## 5. Build

- MonoGame content pipeline (MGCB) recompiled the new `.fbx`→`.xnb` +
  `.png`→`.xnb`: **0 warnings, 0 errors** (fresh Debug timestamps on all new
  XNBs prove recompile).
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.

## 6. Live proof

Booted `vizfight roster 8127 phoenix` (real 10-faction server; the `phoenix`
filter spawns Phoenix's real bots). Transport on `127.0.0.1:41193`.

- **`roster-capture dump`** → `roster_phoenix.jpg`: **9/9 distinct JPEG sizes,
  9/9 distinct tri counts** — every Phoenix ship is distinct geometry. Each
  asset resolved to a real native hull in `roster_dump.json` (e.g.
  `phoenix-fighter` ↔ dn_faphshfig hull 190 "Fighter", `phoenix-carrier` ↔
  dn_faphshcar hull 185 "Carrier [SR]").
- **`roster-capture wire`** (real `ConnectionSession` over the wire, side 9,
  civId 4=phoenix): received **phoenix-bomber, phoenix-carrier, phoenix-corvette,
  phoenix-fighter, phoenix-int, phoenix-scout, phoenix-stealthfighter** live
  (the new slots arrive over the real EXPORT/UPDATE path),
  `roster_net_phoenix_wire.jpg`. (Bot reseats make the received set vary run to
  run — cruiser/frigate reseated at capture — environmental, documented.)

### Resolver coverage (before → after)

Measured over the real PCore001 via `HullModelResolver` on Phoenix's
player-flyable (`GroupId >= 0`) `dn_faphsh`-prefix hulls:

| | fallback → bios_scout | dedicated model |
|---|---|---|
| **Before (P510)** | **26 / 26** | 0 / 26 |
| **After (P515)** | **0 / 26** | **26 / 26** |

Every Phoenix player hull now resolves to a dedicated model (9 distinct assets:
bomber, carrier, corvette, cruiser, fighter, frigate, int, scout,
stealthfighter). Verified by `All_phoenix_player_hulls_resolve_dedicated_no_fallback`
(count 26, all dedicated, all `phoenix-*`) and by the live roster dump
(9/9 assets). (P510 reported the 295/295 figure across *all* flyable hulls; the
per-civ metric over Phoenix's own 26 hulls is 0/26 → 26/26.)

### Scale check (objective)

Each new model's raw `RenderRadius` (the compiled geometry) maps through the
runtime `ShipRenderScale` to exactly **0.5 × HullType.Length**:

| asset | Length | raw renderR | rendered = 0.5×L |
|---|---|---|---|
| phoenix-bomber | 34.8 | 95.137 | 17.41 |
| phoenix-carrier | 300.0 | 97.833 | 150.00 |
| phoenix-corvette | 75.0 | 97.266 | 37.50 |
| phoenix-cruiser | 300.0 | 113.567 | 150.00 |
| phoenix-fighter | 20.9 | 103.072 | 10.43 |
| phoenix-frigate | 200.0 | 99.906 | 100.00 |
| phoenix-int | 15.6 | 98.076 | 7.78 |
| phoenix-scout | 21.2 | 96.688 | 10.60 |
| phoenix-stealthfighter | 23.3 | 111.037 | 11.64 |

Proportional auto-normalization (carrier 300→150, fighter 20.9→10.43), no
per-model tuning.

### Dark livery

All nine Phoenix textures are healthy (mean luminance 85.4–126.2, **99.3–100%
non-black**, max 232–255) — none is a missing/blank texture.

## 7. Tests

Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (all numbers
actually measured this session):

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | **1214/1214** | **1214/1214** | 1213/1214 |
| Allegiance.ClientCore.Tests | 438/438 | 438/438 | 438/438 |
| Allegiance.Core.Tests | 3707/3708 | 3706/3708 | 3707/3708 |

*(measured this session)*

My project — **Allegiance.ClientCore.MonoGame**, which hosts the
resolver/roster/orientation/manifest/Phoenix-coverage tests (incl. the new
`All_phoenix_player_hulls_resolve_dedicated_no_fallback`) — was **1214/1214 on
runs 1 and 2**, and 1213/1214 on run 3, the only loss being a live-wire timing
flake (`ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`)
that passes in isolation (verified 1/1). Every failure elsewhere is a
pre-existing, rotating live-wire/load-timing flake documented in
`docs/TEST_FLAKES.md` (none reference any P515 symbol), all passing in
isolation:

- Run 1 Core: `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_…`
  (TEST_FLAKES.md:157).
- Run 2 Core: `StationUnderAttackAlertLiveTests.Second_hit_within_throttle_window…`
  (family, :375) + `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_
  does_not_leak_to_another` (:175).
- Run 3 Core: `AddPartTests.Part_pickup_decline_recreates_treasure_…` (:247/331).

## 8. Out of scope (untouched)

- Other civs (all already landed: P511 dreg, P512 giga, P513 tf, P514 oh).
  Pushing. Pipeline changes.

## Files committed

- `Content/Models/MODELS_MANIFEST_P287.json` / `.md` (90→99 ships, count→102)
- `Content/Content.mgcb` (9 model + 9 texture blocks)
- `Content/Models/phoenix-{bomber,carrier,corvette,cruiser,fighter,frigate,int,scout,stealthfighter}.fbx` (9 new)
- `Content/Textures/ships/phoenix-{...}.png` (same 9)
- `src/Allegiance.Core/Content/HullModelResolver.cs` (ShipAssets 90→99,
  ClassToken `phoenix` case)
- `src/Allegiance.Core/Content/RosterPlanner.cs` (FactionOrder + phoenix)
- `src/Allegiance.Core/Content/ModelOrientationOverrides.cs` (Extents + 2
  overrides)
- `tools/extract_meshy_textures.py` (SHIP_ASSETS)
- tests: `HullModelResolverTests` (+1 Phoenix coverage, 90→99),
  `RosterPlannerTests` (90→99, phoenix 0→9), `PhoenixMappingTests`,
  `ModelOrientationOverridesTests` (6→8)
- this report + `docs/P515_MESHY_PHOENIX_assets/` (proof screenshots)

Large binary assets committed directly (no LFS) per the P351–P353/P511–P514
convention (`.glb` source files are untracked; only the converted `.fbx` +
`.png` are committed). `tools/vizfight/roster_dump.json` remains untracked
(build artifact).
