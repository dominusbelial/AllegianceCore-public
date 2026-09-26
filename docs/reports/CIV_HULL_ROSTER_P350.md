# P350 — Cross-civ hull mismatch in the P287 roster witness selection (fixed)

Relationship: direct follow-up to P305 (`RosterPlanner.Plan`, the spawn-ready
per-asset roster) and P288 (`HullModelResolver`, the civ+class → P287 model
mapper). Scope boundary is exact: `HullModelResolver.ResolveAsset` (the role →
class → asset derivation) is confirmed correct and UNTOUCHED; this task fixes
only WHICH real PCore001 hull is chosen as the witness for a `(civ, class)`
asset in `RosterPlanner.Plan` (`src/Allegiance.ClientCore.MonoGame/Rendering/RosterPlanner.cs`).

## Root cause (confirmed, not guessed)

`DataHullTypeIGC` carries no `CivilizationId` (`src/Allegiance.Core/Data/DataHullTypeIGC.cs`
— verified: the struct has no civ field, and the native `DataHullTypeIGC`
struct at `src/Igc/igc.h:1767` has none either). So the witness search in
`Plan` (`RosterPlanner.cs:99-104`) resolved each `(civ, class)` asset to simply
the **first** hull in iteration order whose `ModelName` role mapped to that
asset under that civ — with no signal that the chosen hull was actually that
civ's own art. Concretely, Bios's fighter/interceptor resolved to GigaCorp's
`wc_gcfigx`/`wc_gchvyx`, Bios's stealth-fighter to generic `fig17`, and every
civ's bomber to generic `bom05b`.

Because the iteration order differs between the test path (`ShopCatalog.Hulls`,
sorted by `HullId`) and the live `vizfight roster` path (`rCore.HullTypes.Values`,
dictionary order), the witness was even unstable across callers — the same
order-independence the task's "no actual signal" complaint describes.

## The per-civ prefix table (data, from the real PCore001 census)

The fix introduces `RosterPlanner.CivModelPrefixes` — a per-faction list of the
dedicated native `ModelName` prefix families, confirmed hull-by-hull from
`PCore001.igc` (a hull-ID cluster where several classes share the prefix) and
cross-checked against each civ's `TtbmBaseTechs` fingerprint in `OT_civilization`
(the distinguishing tech byte of a family's `TtbmRequired` matches its owning
civ's base-tech byte):

| Civ (id) | Dedicated ModelName prefix family | Confirmed by |
|---|---|---|
| bios (35)   | `quiz` | hull cluster 13-25; quiz fig/int/sf/scout/bbr |
| gt (3)      | `wc_gt` | hull cluster 97-135; ttbm byte `0x20` == GT base-tech |
| ic (25)     | `wc_ic` | hull cluster 404-495; ttbm byte `0x04` == IC |
| giga (18)   | `wc_gc` | hull cluster 212-299; ttbm byte `0x08` == Giga (gc = GigaCorp) |
| oh (5)      | `faohsh` | hull cluster 703-795; ttbm byte `0x80` == Omicron |
| tf (2)      | `tf`, `rain`, `dn_fatesh` | all carry Technoflux's `0x10` tech byte; `rain` block (49-65) is TF, resolving the task's open "rain = ?" question |
| dreg (1)    | `dreg`, `drg`, `dn_fadrsh` | `dregcar`/`dregtt` names; ttbm byte `0x01` == Dreg |
| rix (201)   | *(none)* | ships reuse shared generic `fig`/`bom`/`cap` meshes — no clean dedicated prefix family |
| belters (101)| *(none)* | same — shared generic meshes |

**Couldn't pin down:** Rixian and Belters. Their P287 assets (Rix 12 classes,
Belters 3) are backed by the *shared* generic `fig`/`bom`/`cap` model names
(distinguishable only by a tech-bit stat fingerprint, not by ModelName prefix),
so no per-civ dedicated prefix exists to prefer. Per the task's no-force rule,
their witnesses were left on the historical fallback. This is reported plainly,
not hidden.

## Exactly which classes changed (25), and to what

Only classes where a confirmed civ-dedicated prefix hull resolves to the asset
change; every other class keeps the historical "first hull that resolves"
fallback, unchanged.

- **bios**: bomber `bom05b→quizbbrv2` (22), fighter `wc_gcfigx→quizfigv2` (15),
  interceptor `wc_gchvyx→quizintv2` (19), stealthfighter `fig17→quizsfv2` (17).
  (The 8 uncovered Bios classes — carrier/constructor/corvette/cruiser/
  destroyer/devastator/ejectpod/miner/trooptransport — have no quiz-prefix
  alternative and are left alone, exactly as scoped.)
- **gt**: bomber `bom05b→wc_gtpt` (128), con `utl14a→wc_gtcon` (97), fighter
  `wc_gcfigx→wc_gtfig` (121), miner `utl91→wc_gtmnr` (107), pod `utl100→wc_gtpod`
  (113), scout `fig05ops1→wc_gtsct` (114), sf `fig17→wc_gtsf` (131).
- **ic**: bomber `bom05b→wc_icbmb` (404), fighter `wc_gcfigx→wc_icfig` (414),
  int `fig22→wc_icint` (412), interceptor `wc_gchvyx→wc_ichvy` (481),
  stealthfighter `fig17→wc_icsf` (150).
- **oh**: bomber `bom05b→faohshbmb` (704), fighter `wc_gcfigx→faohshafig` (714),
  scout `fig05ops1→faohshsctb` (710).
- **tf**: bomber `bom05b→dn_fateshbmb` (66), fig `wc_gcfigx→rainfigv2` (52),
  scout `fig05ops1→rainscoutv2` (49).
- **giga**: bomber `bom05b→wc_gcbmbx` (299), fighter `wc_gcfigx→wc_gcfig` (214)
  (both are already GigaCorp; picks the lower-id base variant).
- **dreg**: fig2 `wc_gcfigx→dn_fadrshfig` (161).

## Live proof (real, not just "it compiles")

1. **Real `vizfight roster` session** — booted the actual server with the
   corrected build; it replans every `(civ,class)` and launches all 72 bots
   through the real `SendBuyLoadout` path. The regenerated `roster_dump.json`
   shows exactly the 25 changed hulls above — i.e. the in-game model for each
   fixed class demonstrably changed (was `wc_gcfigx`/`bom05b`/…, now the civ's
   own hull).
2. **`roster-capture turnaround` (P348 tool)** — rendered all 72 ships through
   the real `HullModelResolver` + `ShipRenderer.Draw` + compiled P287 content
   path (288 4-angle images). The newly-corrected ships are visibly the right
   civ's model:
   - Bios fighter/interceptor/stealthfighter/bomber all read organic/insectoid
     with the green bioluminescent accents — Bios's signature look (the
     bio-fighter is emphatically NOT the boxy industrial GigaCorp `wc_gcfigx`
     it used to show).
   - Spot-checks: GT fighter renders a distinct angular silver/red four-wing
     military fighter; OH fighter a scaly-saucer with red dome — each clearly
     different from Bios and from each other (no cross-civ blending).
   - The task's earlier-session native-`.mdl` confirmation already established
     the `quiz` family's green-scale/orange native livery; the native
     `decode_texture.py` in `allegiance-tools/reference` only handles the simple
     RGB565 case (these ship `.mdl` textures are paletted), so the native-livery
     renders were not reproduced here — the in-game (content-model) proof above
     is the task-sanctioned alternative.

## Tests

Added to `tests/Allegiance.ClientCore.MonoGame.Tests/RosterPlannerTests.cs`
(+18): the per-civ prefix table as data; Bios fighter/interceptor/
stealthfighter/bomber → own `quiz` hulls; 15 theory cases that each changed
class carries its civ's own prefix; and the invariant that *whenever a civ owns
a dedicated hull for a class, the witness is that civ's own ship* (never a
cross-civ neighbour's), while classes with no own alternative keep the
historical fallback.

No existing test hardcoded an old (now-wrong) hull for a changed class — the
prior `RosterPlannerTests`/`HullModelResolverTests` assert structural invariants
(one entry per asset, per-faction counts, resolvability), which still hold.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Focused `RosterPlannerTests` + `HullModelResolverTests`: **29 passed**.
- Full `Allegiance.Core.Tests` suite: run per the landing workflow (only the
  documented pre-existing live-wire timing flakes are expected; failing-test
  names vary run to run — the known signature).
