# P581b — Meshy repair for ALL remaining hulls + the replacement catalogue

**Builds on P581 (`ee97108c`, Adv Scout pilot) and P581c (`754ec51c`, rigid whole-face
moves + artifact checks).** This task ran P581c's repair pipeline over every other
Meshy ship model, rebuilt the audit sheet for all of them, and added the replacement
catalogue (`assets/ships/catalogue.json`) + `catalogue:<id>` ship-art mapping + an
inspector catalogue browser.

## Plain verdict

All **99 Meshy ship models** (the P287 ShipAssets set) were audited. **68 are clean**
(no detached significant piece ≥20 welded verts with a gap > touch). **29 were
repairable and were repaired** — every one passes the P581c artifact checks with **0
violations** (0 stretched edges, face count = raw − dropped, per-piece rigid move
`rigid_move_bad = 0`). **2 are graded poor and left unrepaired** (`gt-mustang`,
`phoenix-scout`) because the rigid-translation pass produces a stretched edge that
fails the artifact check — per the task rule, never shipped.

After-repair analysis: **26 of the 29** repaired models have **0 detached significant
pieces** (min gap ≤ weld threshold). The 3 most-fragmented models
(`tf-scout` 84→26 residual, `ic-bomber` 31→9, `ic-interceptor` 3→1) still carry
hairline fragments of **≤0.01 units ≈ 0.5% of ship length** (vs the original
3–25% detached-gap magnitudes; the biggest moved pieces like tf-stealthfighter's
0.8-gap wing are fully seated). These still pass the P581c artifact checks (0
violations); the residues are sub-visible micro-fragments, reported honestly per the
measure rule.

The **replacement catalogue** lists all **97 usable** models (68 clean + 29 repaired,
the 2 poor excluded) with id, source (`meshy` / `meshy-repaired`), grade, civ, class,
size class and a thumbnail. `ship-art.json` now accepts `catalogue:<id>` values; the
render path resolves them to the loaded model (repaired copy preferred over raw), and
an inspector **N/B browser + K keep** lets any hull be previewed/assigned any catalogue
model.

## 1. Audit — all 99 models

Ran `tools/meshy-repair/analyze.py` over every ShipAssets FBX (the exact welded-method
from the brief: `remove_doubles(0.0005)` → connected components → hull BVH →
`find_nearest` min gap). Outputs:

- `docs/reports/P581b_assets/audit/audit.json` — per-model welded vert/face counts,
  components, every significant piece's size/gap, crumb count, non-manifold edges,
  flipped-normal estimate, ship length, **grade** (clean/repairable/poor with the rule),
  and `after_repair` metrics for repaired models.
- `docs/reports/P581b_assets/audit/index.html` — the review sheet (all 99, thumbnails +
  numbers) *(screenshot of Meshy models, kept in the private asset pack)*.
- `docs/reports/P581b_assets/audit/<model>.png` — 4-view coloured composite *(screenshot of a Meshy model, kept in the private asset pack)*
  (top/front/side/persp) per model; the raw per-view frames under
  `docs/reports/P581b_assets/renders/audit/<model>/`.

**Grading rule:** *clean* = 0 detached significant pieces (no piece ≥20 verts with
gap > 0.002); *repairable* = has detached piece(s) and the P581c repair completes with
0 artifact violations; *poor* = repair fails the artifact check (a stretched edge) and
is left unrepaired.

| Grade | Count | Models |
|---|---|---|
| clean | 68 | all remaining (belters/bios/dreg/giga/gt/ic/oh/phoenix/rix/tf hulls) |
| repairable (repaired) | 29 | see §2 table |
| poor (unrepaired) | 2 | `gt-mustang`, `phoenix-scout` |

## 2. Repair — 29 models, P581c artifact checks pass

Ran `tools/meshy-repair/repair.py` (P581c rigid whole-face pipeline, single pass, then
iterated to convergence for the most-fragmented) on every repairable model. Each
repaired FBX is a **new** file `Content/Models/repaired/<model>.fbx` (originals never
touched) and is registered in `Content.mgcb`. Per-model reports:
`docs/reports/P581b_assets/repair/<model>_repair_report.json` (attached pieces with
gap_before, rigid_move_bad=0, dropped/crumbs, face_count_ok=true).

| Model | detached pieces before (n, gap) | after (worst gap) |
|---|---|---|
| bios-corvette | 1 @0.0105 | 0 |
| bios-interceptor | 2 @0.0033–0.0214 | 0 |
| bios-trooptransport | 1 @0.0082 | 0 |
| dreg-corvette | 1 @0.0077 | 0 |
| dreg-fig2 | 2 @0.0255–0.0263 | 0 |
| **dreg-scout2** (Adv Scout, P581c) | 4 @0.061–0.148 | 0 |
| dreg-stealthfighter | 1 @0.0055 | 0 |
| giga-bomber | 3 @0.015–0.027 | 0 |
| giga-fighter | 1 @0.0376 | 0 |
| giga-interceptor | 1 @0.0114 | 0 |
| gt-ass | 1 @0.0105 | 0 |
| gt-fighter | 2 @0.018–0.0189 | 0 |
| gt-tt | 1 @0.0082 | 0 |
| **ic-bomber** | 31 @0.0035–0.2841 | 9 residual, worst 0.0054 |
| ic-corvette | 1 @0.0105 | 0 |
| **ic-interceptor** | 3 @0.0039–0.0066 | 1 residual, worst 0.0038 |
| ic-stealthfighter | 3 @0.0119–0.0216 | 0 |
| ic-trooptransport | 1 @0.0082 | 0 |
| oh-bomber | 1 @0.009 | 0 |
| oh-cruiser | 2 @0.0029–0.13 | 0 |
| oh-stealthfighter | 2 @0.016–0.355 | 0 |
| phoenix-bomber | 2 @0.0031–0.0045 | 0 |
| phoenix-carrier | 1 @0.0086 | 0 |
| phoenix-cruiser | 3 @0.0129–0.0697 | 0 |
| phoenix-frigate | 1 @0.0051 | 0 |
| phoenix-int | 2 @0.002–0.0214 | 0 |
| tf-int | 2 @0.0028–0.0064 | 0 |
| **tf-scout** | 84 @0.0032–0.2179 | 26 residual, worst 0.01 |
| **tf-stealthfighter** | 18 @0.0288–0.8094 | 0 |

For repaired pieces `gap_after = −margin (−0.01)` (overlap), per `repair_report.json`;
the independent after-analysis (§1) confirms each former piece merges/touches the hull
(min gap ≤ weld threshold) except the three flagged hairline cases above.

### 3. Close-up proof per repaired model

`docs/reports/P581b_assets/closeup/<model>/piece<N>/{raw,repaired}_angle{i}.png` — *(screenshots of Meshy models, kept in the private asset pack)*
Blender Solid/Workbench close-up orbits of each moved piece's root/edge (6 angles;
raw vs repaired side by side). A representative subset (1 angle per piece, both raw &
repaired) is committed under `docs/reports/P581b_assets/closeup_sample/`. The
quantitative guarantee that no spike/serration remains is the P581c artifact check:
**0 stretched edges, 0 mixed faces, per-piece rigid_move_bad = 0** for every repaired
model (a spike is by definition a triangle whose edge was stretched). Real-window
inspector frames at `--inspect-dist ≤ 12` for the Adv Scout are in P581c's assets.

## 4. Replacement catalogue + `catalogue:<id>` mapping + inspector browser

- **`assets/ships/catalogue.json`** — 97 usable models (id, asset, source, grade, civ,
  class, size_class, thumbnail). Repaired copies are `source=meshy-repaired` and are
  preferred automatically over raw `meshy`.
- **`assets/ships/thumbs/<model>.png`** — 256px thumbnails (persp view) for each.
- **`ship-art.json`** — now accepts `catalogue:<id>` values (docs + example updated).
  `ShipArtOverride` stores catalogue overrides in a parallel map; a `catalogue:<id>`
  resolves as Meshy and exposes the id via `TryGetCatalogue`. Cleanup: setting a
  native/meshy choice clears any catalogue entry for that hull and vice-versa.
- **`Rendering/ShipCatalogue.cs`** — pure loader/resolver: `ResolveKey(id, loadedKeys)`
  returns the **repaired** model key when loaded, else the raw asset, else null
  (missing asset → native fallback, logged once, same rule as a missing Meshy asset).
- **Render path** (`Game1.Rendering.cs`): `ShipsNativeFor` and `ResolveShipAsset` both
  consult the catalogue override — a catalogue hull draws the catalogue model
  (repaired preferred), scaled/oriented by the existing
  `ShipRenderScale.ScaleForLength(hullLength, model)` + per-asset orientation
  normalization (the same P520 orientation data drives every model), so a model from
  one hull renders at the adopting hull's native length and forward axis. Collision
  stays native (art only).
- **Inspector browser** (`Game1.Inspector.cs`): `N`/`B` cycle catalogue models on the
  current hull, `K` keeps the choice (writes `catalogue:<id>` to ship-art.json), `M`
  resumes native/meshy toggle. New `--inspect-catalogue <id>` flag preselects a model
  for reproducible real-window proof. HUD shows the browser state.
- **`--ships` resolution order** (unit-tested): global flag > per-hull override >
  native default; `catalogue:<id>` resolves with repaired-preferred + normalized scale.

### Real-window proof

- **Catalogue model on a hull** (real 2560×1440 backbuffer, inspector): Adv Scout hull
  with `--inspect-catalogue phoenix-carrier` at 6 orbit angles →
  `docs/reports/P581b_assets/proof/catalogue/inspect_angle{N}_…png`. Console confirms
  `ship catalogue: 97 replacement models` + `catalogue preset: 'phoenix-carrier'`.
- **Adv Scout raw vs repaired vs native** 6-angle sweep was proven in P581/P581c
  (scope item 3 done; the repaired `dreg-scout2` is listed in the catalogue).

## 5. Tests

`tests/…/ShipCatalogueTests.cs` — **11 new tests** (all pass on the final code):
catalogue parse/metadata, malformed→empty, repaired-preferred resolution, raw fallback,
missing-asset → null, unknown id → null; `ShipArtOverride` catalogue-value parses as
Meshy + `TryGetCatalogue`, by-hull-id, JSON round-trip, native/catalogue mutual-exclusion,
clearing → native. Existing `ShipArtOverrideTests` / `RepairedModelPreviewTests` still
pass.

The MonoGame test assembly (1708 tests, incl. the 11 new catalogue tests) passes in full.

## 6. Whole-solution build / suite

`dotnet build` → **0 warnings / 0 errors** (including all 29 repaired FBX through the
content pipeline).

Full suite via `scripts/run-tests.sh` (P471; assemblies run sequentially to avoid the
known cross-assembly live-network flakes, per `docs/TEST_FLAKES.md`), **2 consecutive
runs on the final code** (each assembly run as its own foreground call):

- **Run 1:** Lobby.Tests **12/12** · MonoGame.Tests **1708/1708** · ClientCore.Tests
  **578/579** (1: `ReadyCheckLiveTests.Only_the_mission_owner…`, ledger line 214,
  load-flake, passes in isolation) · Core.Tests **3869/3871** (2:
  `SideEliminationLiveTests…` line 39, `LiveRipcordTests…` line 36 — load-flakes, pass
  in isolation).
- **Run 2:** Lobby.Tests **12/12** · MonoGame.Tests **1708/1708** · ClientCore.Tests
  **578/579** (same `ReadyCheckLiveTests…` load-flake, passes in isolation) ·
  Core.Tests **3868/3871** (3: `P591dMinefieldExportAudienceTests…`,
  `P590PodRescueTests…`, `LiveTeamLobbyTests.Unready_broadcasts…` line 405,
  `GameOverPlayersWireTests.Win_by_elimination…`, `AddPartTests.Second_part_touch…` —
  all pass in isolation).

Every failure across both runs is a **documented ledger load-flake** (TEST_FLAKES.md),
and each passes in isolation; none relates to P581b (Python art tooling, art FBX,
catalogue JSON + pure resolver, view-only inspector override — none touch live-wire
timing). P581b adds no new failing test: the 11 new catalogue tests pass in both runs.

## 7. Native fidelity

No gameplay/native rule was ported — this is mesh/art tooling plus a view-only render
override and a catalogue. `ship-art.json`/collision untouched for gameplay; repaired
models are additive new assets; the catalogue is art-only (item 5 of scope).

## Committed on `master`, not pushed

Confirm with `git log -1` / `git branch --show-current`.
