# P288 — wire per-ship/civ models into ShipRenderer + missile rendering

Consumes P287's asset manifest (`Content/Models/MODELS_MANIFEST_P287.json`:
72 ship + 3 missile content models). Replaces the single hardcoded
`bios_scout_upscaled` model that every ship previously rendered with a real
per-civ/per-class model lookup, and wires the three P287-imported missile
models into the missile path.

Relationship to P287: P287 imported the 75 Meshy models and produced the
manifest; P288 is the rendering wiring that consumes it. P287 landed on master
as `ac09d3c` before this task started.

## The real civ-lookup field (the task's open research question)

A ship's civilization is obtained through a **real, already-wired side field
chain** — no invention:

1. `ShipEntity.SideId` (`ClientCore/State/ShipEntity.cs:84`) — every ship the
   client tracks carries its real side id (set by `EXPORT(26)`, P260).
2. `ClientWorld.SideRoster` (`ClientCore/State/ClientWorld.cs:113`) — the P281
   `SIDE_ROSTER(211)` snapshot carries **each side's real `CivId`**, populated
   from `Side.Civilization.CivilizationId` (`Server.cs:6214-6218`). So
   `SideRoster.Sides[i].CivId` is the authoritative per-side civilization.
3. `HullModelResolver.CivKeyById` (`Rendering/HullModelResolver.cs`) maps the
   real PCore001 `CivilizationId` to the manifest's civ key
   (35→bios, 3→gt, 201→rix, 25→ic, 1→dreg, 2→tf, 5→oh, 18→giga, 101→belters;
   Phoenix(4) has no P287 art → null → fallback).

So at render time: `ShipEntity.SideId → SideRoster.CivId → civKey`. This is
found, not assumed — the port genuinely carries this on the wire.

## The civ+class → model lookup

Granularity is civ+class, not 1:1 per `HullId`: one native model name backs
many hull-stat variants (e.g. `fig05` → "Scout"/"Hvy Scout"/"Adv Scout"),
and one native mesh is genuinely shared across factions (differing only by
texture), so a single role legitimately backs many civs' assets.

`Rendering/HullModelResolver.cs` (pure, headless-testable):
- `RoleOf(ModelName)` — the native `ModelName`-backed grouping (e.g.
  `fig05`→scout, `bom04`→bomber, `wc_gtint`→int, `cap415`→hod), derived from
  the real PCore001 hull census (179 distinct model names).
- `ClassToken(civ, role)` — renames the role to that civ's manifest class
  vocabulary (Bios "fighter", Dreghklar "fig2", Ga'Taraan "fig", …) exactly as
  the manifest names them.
- asset = `"{civ}-{class}"`, returned **iff** that (civ,class) is in the
  embedded P287 ship-asset set (`ShipAssets`, verified to equal the manifest).
  Otherwise null → the caller falls back.

`ShipRenderer.Draw` now takes the model per ship instance
(`Rendering/ShipRenderer.cs`), replacing the single `_model` field; Game1 loads
all 72 ship models + the fallback once (`Game1.cs` LoadContent), resolves each
visible ship's model under the existing Gate lock with the pose-gather
(`Game1.cs` UpdateFlying), and draws the chosen model per ship.

## Fallback

Any hull with no matching civ+class art (the ~370+ hulls P287 didn't generate
art for), or an unresolvable civ (no roster yet / Phoenix), falls back to the
existing `bios_scout_upscaled` model — a placeholder, not a missing render or
a crash. Proven in the live harness: an unmapped Recon Drone hull renders the
fallback ship.

## Missiles — a REAL per-type lookup

Research finding: the port **knows each fired missile's type**. The client's
`MissileEntity.TypeId` (`ClientCore/State/MissileEntity.cs:38`) comes from
`FIRE_MISSILE(70)`'s `MissileTypeId`, and the client now parses the real static
core (`ShopCatalog` → `StaticCore.MissileTypes`), so it can map
`typeId → ModelName` the same way the server does.

Decision: **real per-type lookup**, not just a single upgrade. Of the ~3 dozen
missile `ModelName`s in PCore001 only three were imported by P287, so the
lookup maps those three and falls back to the existing native-`.mdl` demo model
for everything else:
- `mis05`  → types 32, 33, 34, 61, 62, 63 (incl. the P234 demo type 34)
- `mis400` → type 83
- `artifact`→ types 14, 31, 70, 71
- all other types → `MissileModelSource.LoadDemoModel()` (native `mis05.mdl`).

Implemented in `MissileModelSource.ResolveImportedAsset(typeId, missileTypes)`;
Game1 loads the three imported content models and draws them with the same
cull-none FBX / nose-alignment conventions as ships (`Game1.cs`). A missing
core degrades to the demo model (no crash).

## Coverage proof (real output)

`tests/Allegiance.ClientCore.MonoGame.Tests/HullModelResolverTests.cs`. The
⭐ tests load the real PCore001 and assert every one of the 72 imported ship
assets is reachable from at least one real hull through the resolver, with a
per-asset witness (civ + real HullId + native ModelName + display name):

```
test command: dotnet test tests/...MonoGame.Tests.csproj --filter HullModelResolverTests
Passed!  - Failed: 0, Passed: 6, Total: 6
  Embedded_ship_assets_match_P287_manifest ............. ✓ (embedded set == manifest's 72 ships)
  Every_imported_ship_model_is_reachable_from_a_real_hull ✓ (missing = <empty>)
  Each_asset_has_a_real_witness_hull .................... ✓ (72 witnesses)
  Known_hull_classes_resolve_and_unmapped_falls_back .... ✓
  Civ_key_by_id_matches_real_core_ids ................... ✓
  Missile_types_map_to_imported_assets_or_null ......... ✓
```

Witness table (real PCore001 hulls → asset), all 72:
`belters-bomber←#22 quizbbrv2 … rix-tt←#26 dregtt` — full table generated in the
live harness `/tmp/p288verify/.verify_p288/` and in the test witness map.

## Live proof (offscreen harness, /tmp/p288verify)

Real `HullModelResolver` + real `ShipRenderer.Draw` + real compiled content
models, driving real hull/civ data from the real PCore001 core:

```
resolving real hulls to P287 assets via HullModelResolver:
  bios-fighter  civ=bios hull #15 quizfigv2 -> bios-fighter
  ic-fighter    civ=ic   hull #15 quizfigv2 -> ic-fighter
  gt-bomber     civ=gt   hull #22 quizbbrv2 -> gt-bomber
  rix-scout     civ=rix  hull #13 quizscoutv2 -> rix-scout
  [fallback] unmapped hull #230 (utl05 Recon Drone) -> null -> bios_scout_upscaled
wrote bios-fighter.png (19996 tris, r=1.575)   ic-fighter.png (19985 tris, r=1.036)
     gt-bomber.png (19971 tris, r=1.034)        rix-scout.png (19931 tris, r=1.075)
     fallback_unmapped_hull.png  missile_mis05.png  scene_all_ships.png
per-asset PNG file sizes (distinct = real visual variety, not all identical):
  bios-fighter 507010 B   ic-fighter 302382 B   gt-bomber 382949 B   rix-scout 96678 B
```

Four factions' real hulls resolve to four **different** content models
(distinct triangle counts, distinct bounding radii, distinct rendered PNG
byte sizes); the fallback path renders a ship (no crash); the imported
`mis05` missile content model renders. Screenshots in `/tmp/p288verify/
.verify_p288/`.

## Build / test status

- Whole-solution build: **0 warnings, 0 errors** (`AllegianceCore.slnx`).
- `Allegiance.ClientCore.MonoGame.Tests`: **472 pass** (baseline 466 → +6 new).
- `Allegiance.ClientCore.Tests`: **127 pass** (unchanged).
- `Allegiance.Lobby.Tests`: **12 pass** (unchanged).
- `Allegiance.Core.Tests` 3163 total, run twice:
  - run 1: 3160 pass / 3 flake (LiveChatSectorWire, BotFlight abrupt-disconnect,
    +1)
  - run 2: 3159 pass / 4 flake (2×StationsUpdate, LiveWinCondition
    Multi_mission [documented], LiveTurretGunner [documented persistent])
  - Each flake re-runs GREEN in isolation / on the pristine tree
    (verified: `StationsUpdateLiveTests` fails identically on `ac09d3c` with
    my changes stashed; none of these live-wire tests reference the MonoGame
    client that P288 modifies). Pre-existing environmental timing flakes, not
    regressions.

## Out of scope (unchanged)

- No model re-import/regeneration (P287's manifest consumed as-is).
- Asteroid rendering (P286) untouched.
- No new hull classes/civs beyond P287.
- Native source (`~/Development/Allegiance`) untouched.
