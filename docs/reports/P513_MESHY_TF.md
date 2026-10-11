# P513 — Technoflux (tf) Meshy regeneration: submit → convert → wire in → live proof

Date: 2026-10-04 · Branch: `master` (committed, not pushed) ·
Task: `docs/prompts/p513-meshy-tf-prompt.md`

This task regenerates **Technoflux**'s player-flyable native ship models through
Meshy Multi-Image-to-3D (reference images from the original decoded `.mdl`
models), converts and installs them, wires five brand-new P287 asset slots
(`tf-int`, `tf-stealthfighter`, `tf-carrier`, `tf-cruiser`,
`tf-trooptransport` — Technoflux previously exposed only bomber/fig/scout), and
proves live that every Technoflux player hull now resolves to a dedicated model
instead of the `bios_scout` fallback.

Recipe: `allegiancecore-port-workflow/references/meshy-faction-ship-swap.md`
(this was the Dreg/Giga recipe, P511/P512). Reference renders come from the
original native `.mdl` decoded models (`mdl_render_pngs/`), as Domingo
confirmed.

## Scope note: 10 jobs, 8 wired slots

P510's authoritative TF table lists 10 native models. Its own class-sharing
rule collapses the role-mates onto the resolved slots:

- `rainfigv2` + `tffig` → **one** `tf-fig` slot (first-listed representative
  `rainfigv2` wired; `tffig` is a distinct mesh that shares the slot)
- `rainsfv2` + `tfsb2` → **one** `tf-stealthfighter` slot (representative
  `rainsfv2` wired; `tfsb2` shares the slot)

All 10 P510-listed models were submitted (P510's priced manifest prices TF at
10 × 30 = 300 credits). Eight map to distinct installed assets; the two
role-mates (`tffig`, `tfsb2`) share a slot with their representative and are
not separately wired — their hulls still get dedicated art through the shared
slot. Hull-count weighting confirmed the chosen representatives are also the
more common flyable hulls (`rainfigv2` backs 3 TF flyable hulls vs `tffig` 1;
`rainsfv2` 3 vs `tfsb2` 2).

## 1. Credit check & submission

Balance checked first (read-only `GET /openapi/v1/balance`): **1,270 credits** —
comfortably above this civ's 300-credit cost, so I proceeded. All 10 TF models
submitted as **one Multi-Image-to-3D job each** from the 4-angle native
reference renders (`mdl_render_pngs/*_front/_three_quarter/_side/_back.png`),
`meshy-7`, 2k texture, PBR. No text-to-3D, no invented art.

| Native model | → slot | Job id | Credits | Status |
|---|---|---|---|---|
| dn_fateshbmb | tf-bomber | 01a1056b-71c5-7480-948f-180510e71872 | 30 | SUCCEEDED |
| rainfigv2 | tf-fig | 01a1056b-7c48-7655-a3e3-b70af09dc9cd | 30 | SUCCEEDED |
| tffig | (shares tf-fig) | 01a1056b-86a6-76f4-9960-a881427353a3 | 30 | SUCCEEDED |
| rainscoutv2 | tf-scout | 01a1056b-9192-77fe-b5e5-ef3ed2e681b2 | 30 | SUCCEEDED |
| rainintv2 | tf-int | 01a1056b-9f71-7614-949c-4df1325e792c | 30 | SUCCEEDED |
| rainsfv2 | tf-stealthfighter | 01a1056b-aaee-72b8-a29f-084a6a9bb91b | 30 | SUCCEEDED |
| tfsb2 | (shares tf-stealthfighter) | 01a1056b-b570-759b-96aa-682e54855611 | 30 | SUCCEEDED |
| raincarr | tf-carrier | 01a1056b-c126-7191-aacc-5e6b86a61819 | 30 | SUCCEEDED |
| tfflagship | tf-cruiser | 01a1056b-cad4-7733-8432-bcf765657ce0 | 30 | SUCCEEDED |
| tftransport | tf-trooptransport | 01a1056b-d488-701e-bd50-9959f42cd0d6 | 30 | SUCCEEDED |

**Spent: 300 credits (10 × 30).** Post-submission balance re-read: **970**
(1270 − 300). GLBs + Meshy thumbs downloaded to
`~/Development/allegiance-tools/reference/tf_meshy_glbs/`.

## 2. Convert & install

Staging (`tf_stage/`) — each representative GLB copied and renamed to its asset
slot; then `glb_to_fbx.py` (headless Blender 5.2.2) and
`extract_meshy_textures.py` (`/usr/bin/python3`, which has Pillow).

| Slot | ← Source GLB | Decimation | FBX | Texture |
|---|---|---|---|---|
| tf-bomber (replaced) | dn_fateshbmb | 640912→19999 | 1088220 B | 1024², 1557179 B |
| tf-carrier (NEW) | raincarr | 500974→19999 | 997420 B | 1024², 1829506 B |
| tf-cruiser (NEW) | tfflagship | 541238→20000 | 977244 B | 1024², 1649911 B |
| tf-fig (replaced) | rainfigv2 | 556868→20000 | 995356 B | 1024², 1688770 B |
| tf-int (NEW) | rainintv2 | 919558→20000 | 1066092 B | 1024², 1564330 B |
| tf-scout (replaced) | rainscoutv2 | 1507348→20000 | 1117964 B | 1024², 2106664 B |
| tf-stealthfighter (NEW) | rainsfv2 | 1049742→20000 | 1092812 B | 1024², 1606896 B |
| tf-trooptransport (NEW) | tftransport | 409212→20000 | 953036 B | 1024², 1518009 B |

All 8 conversions clean: rc=0, no ERROR/Traceback, all hit the 20k tri cap.
Textures: all `tf-*` lines printed `OK` (the other MISS lines are the expected
harmless noise; script exits 1 on those, as documented).

## 3. Wiring (5 new slots)

Technoflux previously had only `tf-bomber/fighter/scout`; the five new slots
are `tf-int`, `tf-stealthfighter`, `tf-carrier`, `tf-cruiser`,
`tf-trooptransport`.

1. `Content/Models/MODELS_MANIFEST_P287.json` + `.md` — added the 5 new entries
   (78→**83** ships); refreshed `tf-bomber`/`tf-fig`/`tf-scout` tris/maxDim to
   the newly measured geometry.
2. `Content/Content.mgcb` — 5 new `FbxImporter`/`ModelProcessor` blocks + 5 new
   texture blocks.
3. `HullModelResolver.ShipAssets` — added the 5 (78→**83**).
4. `HullModelResolver.ClassToken("tf")` — added `"int" => "int"` and changed
   the fallthrough `_ => null` → `_ => DefaultToken(role)` (per P510's exact
   note; `belters` shares that `case` but its behaviour is unchanged — none of
   DefaultToken's new tokens map to an existing belters slot).
5. `tools/extract_meshy_textures.py` `SHIP_ASSETS` — TF family 3→8, to stay in
   sync.
6. `RosterPlanner` — **no change** (`tf` → `["tf","rain","dn_fatesh"]` and the
   `FactionOrder` entry already existed).
7. `ModelOrientationOverrides.Extents` — refreshed the 3 existing TF entries
   and added the 5 new ones to the freshly measured extents (below).

Tests updated to the new counts: `HullModelResolverTests` (78→83, +1 new TF
coverage test), `RosterPlannerTests` (total 78→83, tf column 3→8).

## 4. Orientation audit (P520 gate)

Re-ran P520's headless audit (`roster-capture orient`) over the compiled
content. **All eight TF assets are decisively or acceptably X-long →
`Ok`, except `tf-int` which is `Ambiguous`** (X=186.0 vs Z=190.7, near-square —
the numbers cannot settle nose-from-tail, so per P520 rules it is flagged for a
visual check, not hard-flagged). **No rotation override was needed** — the
override table is unchanged (P520's six + P511's two):

| asset | x | y | z | long axis | verdict |
|---|---|---|---|---|---|
| tf-bomber | 190.252 | 59.251 | 170.113 | X | Ok (ambiguous) |
| tf-carrier | 190.201 | 35.147 | 81.178 | X | Ok |
| tf-cruiser | 190.547 | 67.995 | 86.433 | X | Ok |
| tf-fig | 190.679 | 46.423 | 139.312 | X | Ok (ambiguous) |
| tf-int | 186.013 | 37.831 | 190.713 | Z | **Ambiguous** (near-square) |
| tf-scout | 190.574 | 47.815 | 137.380 | X | Ok (ambiguous) |
| tf-stealthfighter | 190.478 | 57.815 | 142.273 | X | Ok (ambiguous) |
| tf-trooptransport | 190.340 | 47.822 | 95.218 | X | Ok |

Contact sheet: `P513_MESHY_TF_assets/orient_after.jpg *(screenshot of a Meshy model, kept in the private asset pack)*`
(`orient_before.jpg` + `orient_extents.json` alongside).

## 5. Build

- MonoGame content pipeline (MGCB) recompiled the new `.fbx`→`.xnb` +
  `.png`→`.xnb`: **0 warnings, 0 errors** (fresh Debug timestamps on all 8 new
  TF XNBs prove recompile).
- Whole-solution build (`AllegianceCore.slnx`, Debug,
  `/home/domingo/.dotnet/dotnet build`): **0 warnings, 0 errors**.

## 6. Live proof

Booted `vizfight roster 8890 tf` (real 9-faction server; the `tf` filter spawns
Technoflux's real bots on side 8). The fresh build plans **83 distinct models
with tf = 8** (`roster_dump.json`), each spawned via the real
`SendBuyLoadout(fLaunch:true)` wire path. Transport port 40509.

- **`roster-capture dump`** → `roster_tf.jpg`: **8/8 distinct JPEG sizes** (and
  7/8 distinct tri counts — tf-scout & tf-stealthfighter happen to share 19995,
  but their JPEG sizes differ) — every TF ship is distinct geometry.
- **`roster-capture wire --side 8`** (real `ConnectionSession` over the wire):
  logged on, seated on side 8, **received 6 distinct TF ships** —
  `tf-bomber`, `tf-fig`, `tf-int`, `tf-scout`, `tf-stealthfighter`,
  `tf-trooptransport` — `roster_net_tf_wire.jpg`. (Carrier/cruiser missed this
  run because bot reseats make the live received set vary run to run —
  the documented environmental caveat from P512.)

### Resolver coverage (before → after)

Measured over the real PCore001 via `HullModelResolver` on Technoflux's
player-flyable (`GroupId >= 0`) hulls (the `tf`/`rain`/`dn_fatesh` prefix
family):

| | fallback → bios_scout | dedicated model |
|---|---|---|
| **Before (P510)** | **16 / 26** | 10 / 26 |
| **After (P513)** | **0 / 26** | **26 / 26** |

Every Technoflux player hull now resolves to a dedicated model (8 distinct
assets). The 26-hull breakdown by native model: dn_fateshbmb:3, raincarr:1,
rainfigv2:3, rainintv2:3, rainscoutv2:3, rainsfv2:3, tffig:1, tfflagship:5,
tfsb2:2, tftransport:2.

### Scale check (objective)

Each new model's raw `RenderRadius` (compiled geometry, `roster_variety.txt`)
maps through the runtime `ShipRenderScale` to exactly **0.5 × HullType.Length**
— proportional auto-normalization, no per-model tuning:

| asset | Length | raw renderR | rendered = 0.5×L |
|---|---|---|---|
| tf-bomber | 30.0 | 128.6 | 15.0 |
| tf-carrier | 300.0 | 96.1 | 150.0 |
| tf-cruiser | 75.0 | 103.9 | 37.5 |
| tf-fig | 17.0 | 103.2 | 8.5 |
| tf-int | 16.4 | 131.2 | 8.2 |
| tf-scout | 13.7 | 98.8 | 6.85 |
| tf-stealthfighter | 14.7 | 115.6 | 7.35 |
| tf-trooptransport | 38.0 | 97.2 | 19.0 |

(The 300-unit carrier vs 13.7-unit scout spans a 22× length range yet every
rendered radius is exactly 0.5×Length.)

### Dark livery

All 8 new TF textures are healthy (Technoflux is a genuinely darker livery, but
none is a missing/blank texture — 100% non-black, max near 255):

| asset | mean lum | non-black | max |
|---|---|---|---|
| tf-bomber | 43.9 | 100% | 255 |
| tf-carrier | 37.4 | 100% | 255 |
| tf-cruiser | 50.0 | 100% | 255 |
| tf-fig | 38.3 | 100% | 255 |
| tf-int | 33.9 | 100% | 230 |
| tf-scout | 61.5 | 99.8% | 255 |
| tf-stealthfighter | 25.2 | 100% | 255 |
| tf-trooptransport | 33.7 | 100% | 255 |

## 7. Tests

Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (all numbers
actually measured this session):

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | **1210/1210** | **1210/1210** | **1210/1210** |
| Allegiance.ClientCore.Tests | 438/438 | 437/438 | 437/438 |
| Allegiance.Core.Tests | 3706/3708 | 3703/3708 | 3707/3708 |

My project — **Allegiance.ClientCore.MonoGame**, which hosts the
resolver/roster/orientation/manifest tests (incl. the new TF coverage test) —
was **fully green 1210/1210 on all three runs**. Every failure elsewhere is a
pre-existing, rotating live-wire/load-timing flake in `Allegiance.Core` /
`Allegiance.ClientCore`, documented (or newly added this session) in
`docs/TEST_FLAKES.md` — none reference any P513 symbol (the change is a pure
content swap + resolver/manifest/orientation tables in `Allegiance.Core` /
`Allegiance.ClientCore.MonoGame`; no source/test changes in Server/Bot/Lobby,
and none of the failing live-wire tests touch the resolver/orientation):

- Run 1: Core `TcpLoopbackProof…ClientHalf_RaisesOnSessionLost` (new), `AddPart…Part_pickup_accept` (line 247).
- Run 2: ClientCore `CrossSideCombatVisibility…sector_stays` (new); Core `SideElimination…Eliminating_side2` (line 39), `LiveTeamLobby…Unready` (new), `LiveRipcord…Re_target` (line 36), `LiveCombatWire…Moving_target` (new), `AsteroidMined…Mining_broadcasts` (line 184).
- Run 3: ClientCore `CrossSideCombatVisibility…sector_stays` (new, 2/3); Core `GameOverPlayersWire…Conquest_win…leak` (line 31/267).

Four newly-observed flakes were added to `docs/TEST_FLAKES.md` (P513 session
observations) to keep it the single source of truth.

## 8. Out of scope (untouched)

- Other civs (oh/phoenix — their own tasks P514/P515).
- Pipeline changes. Pushing.

## Files committed

- `Content/Models/MODELS_MANIFEST_P287.json` / `.md` (78→83 ships)
- `Content/Content.mgcb` (5 model + 5 texture blocks)
- `Content/Models/tf-{bomber,fig,scout}.fbx` (replaced) +
  `tf-{carrier,cruiser,int,stealthfighter,trooptransport}.fbx` (new)
- `Content/Textures/ships/tf-{bomber,fig,scout}.png` (replaced) +
  `tf-{carrier,cruiser,int,stealthfighter,trooptransport}.png` (new)
- `src/Allegiance.Core/Content/HullModelResolver.cs` (ShipAssets + ClassToken)
- `src/Allegiance.Core/Content/ModelOrientationOverrides.cs` (Extents)
- `tools/extract_meshy_textures.py` (SHIP_ASSETS)
- tests: `HullModelResolverTests` (+1 TF coverage), `RosterPlannerTests`
- this report + `P513_MESHY_TF_assets/` (proof screenshots, thumbs,
  orientation contact sheet + extents)
