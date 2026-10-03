# P510 — Meshy Submission Plan: remaining civs (dreg, tf, oh, giga, phoenix)

Date: 2026-10-03 · Branch: `master` (not pushed) · Status: PREP ONLY — **no Meshy credit spent**

This stage derives the exact regeneration list with the port's own code (never from
memory), produces the 4-angle native `.mdl` reference renders for every model that
lacked them, adds Phoenix's civ mapping (code + tests), and defines the new P287
asset-slot plan that P511–P515 implement. **Nothing was submitted to Meshy.**

---

## 1. Verified data (measured with the port's code, not recalled)

Ran `HullModelResolver` / `RosterPlanner` over the real `PCore001.igc` (loaded via
`ShopCatalog.TryLoad`, `src/Allegiance.ClientCore.Game/Rendering/ShopCatalog.cs`),
restricted to **player-flyable hulls (`GroupId >= 0`)**.

| Measure | Value |
|---|---|
| Total hulls in PCore001 | 447 |
| Player-flyable hulls (`GroupId >= 0`) | **295** |
| Distinct player-flyable native models, remaining civs (dreg+tf+oh+giga+phoenix) | **46** (confirms the brief's 46) |
| Player-flyable hulls falling back to `bios_scout` for each of dreg/tf/oh/giga | **177 / 295** (confirms the brief's 177) |
| Player-flyable hulls falling back for Phoenix | **295 / 295** |
| Meshy balance (read-only `GET /openapi/v1/balance`, 2026-10-03) | **1,540 credits** (correction: the brief quoted 1,290 — 1,540 is what I actually measured) |

---

## 2. Reference renders (completed this stage)

Every one of the 46 native `.mdl` models decodes and renders cleanly. Produced with
the existing pipeline (`mdl_parse.py` → `decode_texture.py` → `textured_render.py`,
4 angles + `_tex`), plus a new `solid_render.py` for Solid-shading mesh sanity checks.

- Fix applied: `mdl_render_scripts/batch_render.sh` `OUT=` pointed at a stale
  2026-09-22 scratchpad; now defaults to `mdl_render_pngs/` (a `mkdir -p` was added).
- New `mdl_render_scripts/solid_render.py`: renders the same 4 angles with a flat
  studio-gray material + key/fill/rim lights so GEOMETRY reads (the
  `feedback_blender_solid_shading_for_mesh_checks` lesson).
- Render dir: `~/Development/allegiance-tools/reference/mdl_render_pngs/`
  (outside the repo — not committed).

| Civ | Models rendered | Missing before | New renders |
|---|---|---|---|
| dreg | 6 | 6 | all |
| tf | 10 | 10 | all |
| oh | 12 | 12 | all |
| giga | 7 | 5 | wc_gcbmbx, wc_gcfig, wc_gchttx, wc_gchvy, wc_gcint |
| phoenix | 11 | 11 | all |
| **Total** | **46** | **44** | **44 new + 2 pre-existing** |

The brief claimed "4 giga models already have reference renders
(wc_gcfigx, wc_gchvyx, wc_gchvy, wc_gcint)"; **on disk only wc_gcfigx and wc_gchvyx
existed** (wc_gchvyx lacked its `_tex`). wc_gchvy/wc_gcint were re-rendered this
stage. All 46 now have the full `_front/_three_quarter/_side/_back/_tex` set plus
`_solid_*` mesh checks.

**Mesh sanity:** all 46 `.mdl` files parsed to `.obj`, all renders non-blank
(>100 KB fronts). Representative Solid-shading renders spot-checked with vision:
Dreghklar/Phoenix fighters and the OH cruiser are coherent, symmetrical craft; the
GigaCorp fighter is a clean industrial fighter. The one soft spot is `tfflagship` —
a very low-poly TF flagship whose surfaces read clipped/merged, characteristic of the
original's low-detail design (not a decode failure). It is still a valid reference.

**Geometry sharing (verified by identical OBJ vertex/tri counts):**
- `wc_gcfig` ≡ `wc_gcfigx` (601 verts / 463 tris) — same mesh, different skin.
- `wc_gchvy` ≡ `wc_gchvyx` (656 / 568) — same mesh, different skin.

So the two `x`-variants share topology with their base. Every other same-role pair is a
**genuinely distinct mesh** (e.g. `wc_gchttx` 455/430 ≠ `wc_gcfig`; `dn_faphshfig` 919/682
≠ `dn_faphshvng` 1759/1302; `tffig` 345/528 ≠ `rainfigv2` 923/875; `faohshint` 1207/1306
≠ `faohshhint` 1254/1360; `dn_faphshstl` 464/326 ≠ `dn_faphshbmbs` 614/424).

---

## 3. The new asset-slot plan (implemented by P511–P515)

Dreg, TF, OH and Giga currently expose only **3 P287 slots** (scout/fighter/bomber);
Phoenix exposes **none**. New slots follow the existing `<civ>-<class>` naming exactly
(the class token = `HullModelResolver`'s per-civ vocabulary). **27 new slots total.**
Same-role models collapse to one slot by the resolver's class-sharing rule (shown
under "→ slot"); each still needs its own Meshy job because they are distinct meshes
(except the two `x`-variants noted above).

### dreg — 5 new slots
| Native model | role | slot |
|---|---|---|
| dn_fadrshfig | fighter | dreg-fig2 *(exists)* |
| dn_fadrshstl | sf | **dreg-stealthfighter** |
| dregcar | carrier | **dreg-carrier** |
| dregtt | tt | **dreg-trooptransport** |
| drgas | ass | **dreg-corvette** |
| drgmf | frigate | **dreg-frigate** |

Resolver: in `ClassToken("dreg")`, replace `_ => null` with `_ => DefaultToken(role)`
(keeps fig2/bbr2/scout2 overrides).

### tf — 5 new slots
| Native model | role | slot |
|---|---|---|
| dn_fateshbmb | bomber | tf-bomber *(exists)* |
| rainfigv2, tffig | fighter | tf-fig *(exists)* |
| rainscoutv2 | scout | tf-scout *(exists)* |
| rainintv2 | int | **tf-int** |
| rainsfv2, tfsb2 | sf | **tf-stealthfighter** |
| raincarr | carrier | **tf-carrier** |
| tfflagship | cruiser | **tf-cruiser** |
| tftransport | tt | **tf-trooptransport** |

Resolver: in `ClassToken("tf")`, replace `_ => null` with `_ => DefaultToken(role)`
and add `"int" => "int"` before the default (matches the `*-int` standard-interceptor
convention of ic-int/gt-int/rix-int).

### oh — 7 new slots
| Native model | role | slot |
|---|---|---|
| faohshafig, faohshfig | fighter | oh-fighter *(exists)* |
| faohshsctb | scout | oh-scout *(exists)* |
| faohshbmb | bomber | oh-bomber *(exists)* |
| faohshint | int | **oh-int** |
| faohshhint | interceptor | **oh-interceptor** |
| faohshstl | sf | **oh-stealthfighter** |
| faohshass, faohshcrv | ass | **oh-corvette** |
| faohshcar | carrier | **oh-carrier** |
| faohshcrs | cruiser | **oh-cruiser** |
| faohshdev | devastator | **oh-devastator** |

Resolver: `ClassToken` has no `oh` case today (falls to `DefaultToken`). Add an `oh`
case mapping `"int" => "int"` (`DefaultToken("int")` = "interceptor", which would
otherwise collide faohshint into the same slot as faohshhint); everything else via
`DefaultToken`.

### giga — 1 new slot
| Native model | role | slot |
|---|---|---|
| wc_gcbmbx | bomber | giga-bomber *(exists)* |
| wc_gcfig, wc_gcfigx, wc_gchttx | fighter | giga-fighter *(exists)* |
| wc_gchvy, wc_gchvyx, wc_gcint | interceptor | **giga-interceptor** |

Resolver: **no change** — `ClassToken("giga")` already defaults to `DefaultToken`,
and `DefaultToken("interceptor")` = "interceptor". Only the asset/manifest/MGCB entries
are needed.

### phoenix — 9 new slots (no phoenix slot exists today)
| Native model | role | slot |
|---|---|---|
| dn_faphshfig, dn_faphshvng | fighter | **phoenix-fighter** |
| dn_faphshsct | scout | **phoenix-scout** |
| dn_faphshbmb | bomber | **phoenix-bomber** |
| dn_faphshint | int | **phoenix-int** |
| dn_faphshstl, dn_faphshbmbs | sf | **phoenix-stealthfighter** |
| dn_faphshcar | carrier | **phoenix-carrier** |
| dn_faphshcrs | cruiser | **phoenix-cruiser** |
| dn_faphshdst | ass | **phoenix-corvette** |
| dn_faphshfrg | frigate | **phoenix-frigate** |

Resolver: `ClassToken` has no `phoenix` case → add one mapping `"int" => "int"`
(`DefaultToken("int")` = "interceptor"); everything else via `DefaultToken`.

### Wiring required per new slot (P511–P515)
Each new slot needs, in `src/Allegiance.ClientCore.MonoGame/Content/`:
1. a `MODELS_MANIFEST_P287.json` + `.md` entry (`asset`, `civ`, `faction`, `class`,
   `kind:"ship"`, real `tris`/`maxDim`);
2. a `Content.mgcb` importer/build entry (MGCB compiles the `.fbx` → `.xnb`);
3. the `HullModelResolver.ShipAssets` entry (embedded set — keeps the resolver
   headless-testable and in sync with the manifest);
4. (per the per-civ notes above) the `ClassToken` extension.

---

## 4. Priced submission manifest

Meshy Multi-Image-to-3D is **30 credits per model** (one job per model from its 4
reference angles). `submit_multi_image` in
`reference/esrgan-work/meshy_pipeline.py` takes exactly the
`_front/_three_quarter/_side/_back` renders.

| Civ | Models | Credits |
|---|---|---|
| dreg | 6 | 180 |
| tf | 10 | 300 |
| oh | 12 | 360 |
| giga | 7 | 210 |
| phoenix | 11 | 330 |
| **Total (46 models)** | | **1,380** |

- Geometry-distinct alternative (skip the two `x`-variants `wc_gcfigx`/`wc_gchvyx`,
  re-skin from their topology-identical bases): **44 models → 1,320 credits**.
- **Measured balance 1,540** → the full 46-model set (1,380) fits with ~160 to spare.
  Even under the brief's stale 1,290 figure, dreg+tf+oh+giga (1,050) fit; only
  phoenix's 330 would exceed it (Domingo has indicated he can top up credits).

---

## 5. Recommended priority order (within the balance)

| Rank | Civ | Models | Credits | Cumulative | Rationale |
|---|---|---|---|---|---|
| 1 | giga | 7 | 210 | 210 | Iconic industrial aesthetic; only **1** new slot → cheapest to wire (P512); small quick win |
| 2 | oh | 12 | 360 | 570 | Distinctive Hive look, widely played (P514) |
| 3 | tf | 10 | 300 | 870 | Technoflux, popular (P513) |
| 4 | dreg | 6 | 180 | 1,050 | Dreghklar, moderate popularity (P511) |
| 5 | phoenix | 11 | 330 | 1,380 | Least-played; all-new slots = most wiring (P515) |

Domingo decides the final order. All four 3-slot civs plus phoenix end with full
dedicated art under this sequence; the first four fit comfortably in even the stale
1,290 line.

---

## 6. Effect on current playtests

The active session (221) runs **IC vs GT** (`civIds [25, 3]`) — both already have
complete native-reference regenerations (P352 GT, P353 IC), so this stage's civs do
**not** change that matchup's visuals. The benefit is for *future* playtests of these
five civs: today they draw mostly as `bios_scout` (177/295 fallback each for
dreg/tf/oh/giga; 295/295 for phoenix). Once P511–P515 land, switching a playtest to
any of these civs yields correct dedicated hulls instead of the fallback.

---

## 7. Phoenix mapping (this stage's code + tests)

- `HullModelResolver.CivKeyById` now includes `[4] = "phoenix"` (was absent → every
  Phoenix ship fell back).
- `RosterPlanner.CivModelPrefixes` now includes `"phoenix" => ["dn_faphsh"]`,
  verified against the real hull data (26 player-flyable Phoenix hulls across the 11
  `dn_faphsh*` models).
- Interim behaviour (documented in code + tests): Phoenix has no `phoenix-*` P287
  slots yet, so `ResolveAsset("phoenix", …)` still returns null → the shared
  `bios_scout` fallback. The civ mapping is the enabling change; slots arrive in P515.
- Tests (`tests/Allegiance.ClientCore.MonoGame.Tests/PhoenixMappingTests.cs`, +10;
  plus the updated `HullModelResolverTests.Civ_key_by_id_matches_real_core_ids`):
  map id 4 → phoenix; prefix family registered; prefix owns real flyable hulls;
  interim-fallback behaviour; the verified 46-model census (per-civ counts + exact
  priced set) as a regression guard. Failing-first: all 5 Phoenix-dependent tests
  went red before the mapping landed, green after.

---

## 8. Out of scope (unchanged)

No Meshy submissions / credit spend (Domingo's decision, later per-civ tasks).
Belters and Rix art (no dedicated native meshes — P350 finding). No GLB→FBX→MGCB
pipeline changes.

## 9. Build / test status

Whole-solution build (`AllegianceCore.slnx`, Debug, `/home/domingo/.dotnet/dotnet build`):
**0 warnings, 0 errors**.

Full suite via `scripts/run-tests.sh` (P471), **3 consecutive runs** (all numbers
actually measured this session):

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | **1204/1204** | **1204/1204** | **1204/1204** |
| Allegiance.ClientCore.Tests | 420/421 | 420/421 | 420/421 |
| Allegiance.Core.Tests | 3705/3708 | 3707/3708 | **3708/3708** |

Every failure is a documented rotating live-wire/load flake in `docs/TEST_FLAKES.md`
(pre-existing; none in the changed project, none referencing this stage's symbols):
FullSessionKillIntegration…RealCrossSideKill (P472 suspect — also fails in isolation,
machine-state), AutoReloadOnEmpty…RELOAD129, SideElimination…gameover (all three
verified **green in isolation**), ConquestTerritory…does_not_leak (green in
isolation), RetriedConnectLiveTests…LeaveNoAccumulatedSockets, NpcShipFoundation…
Ownerless_npc, ReadyCheckLiveTests…starts_the_mission. Core.Tests ran fully green
(3708/3708) on run 3, and my project was green on every run.
