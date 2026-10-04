# P511 — Dreghklar (dreg) Meshy regeneration: submit → convert → wire in → live proof

Date: 2026-10-03 · Branch: `master` (committed, not pushed) ·
Task: `docs/prompts/p511-meshy-dreg-prompt.md`

This task regenerates **Dreghklar**'s 6 distinct player-flyable native ship models
through Meshy Multi-Image-to-3D (reference images from the original decoded
`.mdl` models), converts and installs them, wires 5 brand-new P287 asset slots
(Dreghklar previously exposed only 3), and proves live that every Dreghklar
player hull now resolves to a dedicated model instead of the `bios_scout`
fallback.

This is the P511–P515 arc's **dreg** installment (P510 prepared, spent no
credits). Unlike P351–P353 (pure content swaps behind existing slots), dreg
needed new slots + resolver/RosterPlanner/`Content.mgcb`/manifest wiring, per
P510's plan.

---

## 1. Credit check & submission

Balance checked first (read-only `GET /openapi/v1/balance`): **1,540 credits**
— comfortably above this civ's cost. All 6 dreg models submitted as **one
Multi-Image-to-3D job each** from the 4-angle native reference renders
(`mdl_render_pngs/*_front/_three_quarter/_side/_back.png`), `meshy-7`,
2k texture. No text-to-3D, no invented art.

| Native model | Job id | Credits | Status |
|---|---|---|---|
| dn_fadrshfig | 01a104f3-3d92-7473-85fc-bd3aa116101d | 30 | SUCCEEDED |
| dn_fadrshstl | 01a104f3-47af-7126-b68d-59efd49202f4 | 30 | SUCCEEDED |
| dregcar | 01a104f3-5140-711f-bdf0-7ad44de16999 | 30 | SUCCEEDED |
| dregtt | 01a104f3-5c78-73b7-a1e3-98934eaa4edf | 30 | SUCCEEDED |
| drgas | 01a104f3-6689-7052-b2f4-b7404405b63d | 30 | SUCCEEDED |
| drgmf | 01a104f3-6f54-7547-aaac-f55c8838693a | 30 | SUCCEEDED |

**Spent: 180 credits (6 × 30).** Post-submission balance re-read: **1,360**
(1540 − 180). GLBs + Meshy thumbs downloaded to
`~/Development/allegiance-tools/reference/dreg_meshy_glbs/`.

## 2. Convert & install

Staging (`dreg_stage/`) symlinked each GLB renamed to its asset slot
(the converter and texture extractor key off the asset name); then
`glb_to_fbx.py` (headless Blender 5.2.2) and `extract_meshy_textures.py`.

| Slot | ← Source GLB | Decimation | FBX | Texture |
|---|---|---|---|---|
| dreg-fig2 (existing) | dn_fadrshfig | 564144→20000 | 1024476 B | 1024², 1802248 B |
| dreg-stealthfighter (NEW) | dn_fadrshstl | 461580→19999 | 1029708 B | 1024², 1894828 B |
| dreg-carrier (NEW) | dregcar | 378504→20000 | 972428 B | 1024², 1743674 B |
| dreg-trooptransport (NEW) | dregtt | 515430→19999 | 1043068 B | 1024², 1731727 B |
| dreg-corvette (NEW) | drgas | 242868→20000 | 1026732 B | 1024², 1256014 B |
| dreg-frigate (NEW) | drgmf | 307180→20000 | 1104348 B | 1024², 1399996 B |

All 6 conversions clean: rc=0, no ERROR/Traceback, all hit the 20k tri cap
(M=20000, two at 19999 from proportional decimation). Textures: all `dreg-*`
lines printed `OK` (the other MISS lines are the expected harmless noise).

## 3. Wiring (the part P351–P353 didn't need)

Five **new** P287 ship slots added (dreg had only `dreg-bbr2/fig2/scout2`);
`dreg-fig2` existed and was replaced with the native fighter render:

1. `Content/Models/MODELS_MANIFEST_P287.json` + `.md` — 5 new entries
   (carrier/corvette/frigate/stealthfighter/trooptransport), 72→**77** ships.
2. `Content/Content.mgcb` — 5 new `FbxImporter`/`ModelProcessor` blocks + 5 new
   texture blocks.
3. `HullModelResolver.ShipAssets` — 5 new names (72→**77**).
4. `HullModelResolver.ClassToken("dreg")` — replaced `_ => null` with
   `_ => DefaultToken(role)` so dreg now recognises its other roles (carrier,
   tt, sf, ass, frigate) via the shared class vocabulary (keeps the
   fig2/bbr2/scout2 overrides). Per P510's exact instruction.
5. `tools/extract_meshy_textures.py` `SHIP_ASSETS` — 5 new names so it stays in
   sync with the manifest/resolver.
6. `RosterPlanner` needed no change — dreg's `CivModelPrefixes`
   (`["dreg","drg","dn_fadrsh"]`) and `FactionOrder` (dreg=5) already existed.

Tests updated to the new counts: `HullModelResolverTests`
(72→77), `RosterPlannerTests` (total 72→77, dreg column 3→8).

## 4. Orientation audit (P520 gate)

Re-ran P520's headless audit over the new batch
(`roster-capture orient`). Two new dreg meshes measured **decisively Z-long**
(the exact ic-dev/gt-dev yaw signature P520 was built to catch):

| asset | x | y | z | long axis | verdict |
|---|---|---|---|---|---|
| dreg-carrier | 34.5 | 56.5 | 190.6 | Z (ratio 5.5) | **Flagged** → override −90° |
| dreg-corvette | 111.0 | 108.3 | 190.3 | Z (ratio 0.58) | **Flagged** → override −90° |
| dreg-fig2 | 178.0 | 91.4 | 189.9 | near-square | Ambiguous (visual: aligned) |
| dreg-frigate | 188.2 | 58.8 | 109.5 | X | Ok |
| dreg-stealthfighter | 190.2 | 85.1 | 64.8 | X | Ok |
| dreg-trooptransport | 190.4 | 40.8 | 63.2 | X | Ok |

Fixed with the **per-asset override table**, not the global correction:
`ModelOrientationOverrides` gained `dreg-carrier`/`dreg-corvette` = (−90,0,0)
(Domingo's P520-verified sign) + their measured extents; the audit re-run
reports both **Ok**. `ModelOrientationOverridesTests` override-set invariant
updated to the 6 (was 4). Contact sheets in `P511_MESHY_DREG_assets/`.

## 5. Build

- MonoGame content pipeline (MGCB) recompiled the new `.fbx`→`.xnb` +
  `.png`→`.xnb`: **0 warnings, 0 errors** (fresh Debug timestamps on all new
  XNBs prove recompile).
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.

## 6. Live proof

Booted `vizfight roster <port> dreg` (real 9-faction server; the `dreg` civ
filter spawns Dreghklar's real bots on side 5 via the real `SendBuyLoadout`
path). The live `RosterPlanner.Plan` resolved all 8 dreg assets to real native
hulls (`roster_dump.json`).

- **`roster-capture dump`** → `roster_dreg.jpg`: **8/8 distinct JPEG sizes,
  8/8 distinct tri counts** — every dreg ship is distinct geometry.
- **`roster-capture wire`** (real `ConnectionSession` over the wire, side 5):
  received dreg ships live, rendered `roster_net_dreg_wire.jpg`.

### Resolver coverage (before → after)

Measured over the real PCore001 via `HullModelResolver` on dreg's
player-flyable (`GroupId >= 0`) prefix-family hulls:

| | fallback → bios_scout | dedicated model |
|---|---|---|
| **Before (P510)** | **8 / 9** | 1 / 9 |
| **After (P511)** | **0 / 9** | **9 / 9** |

Every Dreghklar player hull now resolves to a dedicated model (6 distinct
assets: fig2, stealthfighter, carrier, trooptransport, corvette, frigate).

### Scale check (objective)

Each new model's raw `RenderRadius` (the compiled geometry) maps through the
runtime `ShipRenderScale` to exactly **0.5 × HullType.Length**:

| asset | Length | raw renderR | rendered = 0.5×L |
|---|---|---|---|
| dreg-carrier | 300.0 | 96.23 | 150.0 |
| dreg-corvette | 157.0 | 96.99 | 78.5 |
| dreg-frigate | 200.0 | 96.22 | 100.0 |
| dreg-trooptransport | 30.0 | 96.50 | 15.0 |
| dreg-stealthfighter | 18.25 | 95.08 | 9.12 |
| dreg-fig2 | 23.11 | 112.17 | 11.56 |
| dreg-scout2 | 11.4 | 112.36 | 5.7 |
| dreg-bbr2 | 23.21 | 105.69 | 11.60 |

Carrier 300→150 vs scout-tier 11.4→5.7 (75× ratio) — proportional
auto-normalization, no per-model tuning.

### Dark livery

`dreg-corvette` (mean-lum 28.6, opaque) and `dreg-frigate` (22.4) render dark.
This is Dreghklar's real dark-green-on-black livery (confirmed against the
approved Meshy thumbs `drgas_thumb`/`drgmf_thumb` — black hull, green glow),
not a missing texture (a missing texture renders white/unlit, not black).

## 7. Tests

Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (all numbers
actually measured this session):

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | **1208/1208** | **1208/1208** | **1208/1208** |
| Allegiance.ClientCore.Tests | 438/438 | 438/438 | 438/438 |
| Allegiance.Core.Tests | 3707/3708 | 3705/3708 | 3704/3708 |

Every Core.Tests failure is a pre-existing, documented live-wire timing / load
flake in `docs/TEST_FLAKES.md` (none reference any P511 symbol): run 1
`StationsUpdateLiveTests.Visible_station…` (the rotating sub-top-5 list,
line 394); run 2 `LiveCountdownStartPingTests.Multi_mission…` (suspect P472) +
`LiveChatSectorWireTests.Individual_AllSector…` (load-flake); run 3
`AutoReloadOnEmpty…RELOAD129` (load-flake P472), `LiveRipcordTests.Re_target…`
(load-flake), `LiveRelaunchTests.Carrier_without_launch_slots…` (suspect P472),
`ConquestTerritoryLiveTests.Conquest_win…` (rotating). My project
(ClientCore.MonoGame, which hosts the resolver/roster/orientation tests) was
**fully green 1208/1208 on all three runs**, including the 5 new dreg
counts/tests.

## 8. Out of scope (untouched)

- Other civs (tf/oh/giga/phoenix — their own tasks P512–P515).
- Pipeline changes. Pushing.

## Files committed

- `Content/Models/MODELS_MANIFEST_P287.json` / `.md` (72→77 ships)
- `Content/Content.mgcb` (5 model + 5 texture blocks)
- `Content/Models/dreg-{fig2,stealthfighter,carrier,trooptransport,corvette,frigate}.fbx`
- `Content/Textures/ships/dreg-{fig2,stealthfighter,carrier,trooptransport,corvette,frigate}.png`
- `src/Allegiance.Core/Content/HullModelResolver.cs` (ShipAssets + ClassToken)
- `src/Allegiance.Core/Content/ModelOrientationOverrides.cs` (2 new overrides + extents)
- `tools/extract_meshy_textures.py` (SHIP_ASSETS)
- tests: `HullModelResolverTests`, `RosterPlannerTests`, `ModelOrientationOverridesTests`
- this report + `P511_MESHY_DREG_assets/` (proof screenshots)
