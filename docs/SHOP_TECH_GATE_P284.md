# P284 — Gate the hangar shop's hulls & parts by researched tech (weapons/shields included)

Closes the gap Domingo flagged: the docked **hangar/loadout shop** the player
actually browses to buy hulls **and parts (weapons, shields, …)** — the
`ShopCatalog`/`ShopModel` screen introduced in P268 — listed **every** hull and
part in the core regardless of the connecting side's researched tech. P283 wired
`Side.DevelopmentTechs` into the server's *station buy-gate*
(`StationSellsHull`, the mission-start/teleport `bNewHull` decision), but that is
a **separate, narrower** path: the in-hangar shop players actually use had zero
tech-gating. This task makes the shop reflect the side's real tech state, for
**both hulls AND parts**.

## Scope item 1 — where the catalogue comes from (the fix's home)

Researched first: `ShopCatalog.TryLoad` builds the catalogue **entirely
client-side**, by parsing the same `PCore001.igc` the server boots
(`StaticCore.Load(IgcReader.ParseFile(...))`). The server sends **no** catalogue
contents — the wire only ever carries hull/part *ids*, never names or
buyability. So there is no server catalog-population path to filter "what's
sent"; the fix must live **client-side**, filtering the catalogue the client
already builds. (The server remains the authority for what a buy actually
grants via `ApplyLoadout`/`StationSellsHull`; this task makes the shop *display*
match.)

## How the client learns the side's tech (zero server changes needed)

The server already sends the client everything it needs:

- **Initial mask** — the join-sync `CREATE_BUCKETS(191)` carries the joining
  side's real development mask (`TtbmDevelopments`; no `SideId`, sent only to
  the joining peer, so it maps to `MySideId`). The client previously ignored it.
- **Updates** — `SIDE_TECH_CHANGE(75)` broadcasts each side's post-OR mask on
  development completion (side-group, `SideId` carried). The client previously
  ignored it.

P284 adds decode+dispatch for both in `ConnectionSession`, and `ClientWorld`
stores a per-side `DevelopmentTechs` mask
(`DevelopmentTechsBySideId` / `DevelopmentTechsFor(sideId)`) seeded from
CREATE_BUCKETS and updated by SIDE_TECH_CHANGE. No server/wire change was
needed — the exact `BUCKET_DONATE → CompleteDevelopment → SIDE_TECH_CHANGE`
flow P283 tested is what populates the client here.

## The filter — one shared containment, reused from P283

The shop gate is the **exact** P283 composition, not a re-derived one:

```
need  = hull.TtbmRequired | part.TtbmRequired          // byte[50]
buyMask = stationType.TtbmLocal | side.DevelopmentTechs
offer(item) = item.TtbmRequired ⊆ buyMask
```

That `need ⊆ (local | sideDev)` containment P283 wrote inline in
`Server.StationSellsHull` is now exposed once, in `Allegiance.Core`, as
`TechTreeBitMask.RequiredTechsBuyable(need, local, sideDev)` — the shared helper
the MonoGame shop consumes, so the client's filter is the same bitmask math the
server's buy-gate uses. (P283's `StationSellsHull` itself is **untouched**, per
the out-of-scope; the helper is the canonical implementation the new code
routes through.)

`ShopCatalog.FilteredFor(stationLocal, sideDev)` returns a catalogue restricted
to the offered hulls+parts. `Game1.HandleDockedScreen` resolves the **docked
station's** type (`world.Stations[mySe.StationId].StationTypeId →
catalog.StationTypes[typeId].TtbmLocal`) and the local side's mask
(`world.DevelopmentTechsFor(world.MySideId)`), and builds the shop from the
filtered catalogue. If either datum is unresolved (station lookup fails / side
tech not yet observed) it degrades to the **full** catalogue (the P268
behaviour) — a missing datum never blanks the shop.

## Coverage — both hulls AND parts (the flagged gap)

The filter runs identically over `Catalog.Hulls` **and** `Catalog.Parts`, so a
weapon or shield with a real non-zero `TtbmRequired` vanishes from the shop
until its governing development is researched, exactly like hulls.

## Verification

- **Whole-solution build: 0 warnings, 0 errors** (`AllegianceCore.slnx`, Debug).
- **Live before/after proof** (`/tmp/p284verify`, real in-process server + real
  `ConnectionSession` renders the shop + real `Bot` completes devs for real via
  `BUCKET_DONATE → CompleteDevelopment`):
  - Targets (measured from `PCore001.igc`): **hull 25 'Bomber'** requires bits
    {1,16,35} — only bit 35 is missing, supplied by **dev 37 'Bomber'**; **part
    190 'ER Nanite 2'** (a weapon) requires exactly bit **114**, supplied by
    **dev 87 'ER Nanite 2'**. Neither bit is in civ 1's base mask nor any
    garrison's local techs — both are pure dev-gates.
  - BEFORE (devs unresearched): client dev-mask pop=28 (civ base, from real
    `CREATE_BUCKETS`); filtered shop = **6 hulls / 15 parts**, hull 25 absent,
    part 190 absent. `before_shop.png` shows no `Bomber` hull and no `ER Nanite
    2` (only `ER Nanite 1`) in the list.
  - Research: donor funded + `BUCKET_DONATE` to dev 37 (bucket 37, $10000) and
    dev 87 (bucket 82, $5000) **for real** → server logged both completions
    ("+1 new tech bit") and broadcast `SIDE_TECH_CHANGE(75)` with devMask
    29 bits then 30 bits; the client ingested both → its stored mask pop=30.
  - AFTER: filtered shop = **7 hulls / 16 parts** (+1 hull, +1 part — exactly
    the two unlocked items); hull 25 AND part 190 both present.
    `after_shop_hull25.png` shows `> Bomber $500 [HULL]`;
    `after_shop_part190.png` shows `> ER Nanite 2 [Weapon]`.
- **Tests**: new pure/unit tests — `TechTreeBitMaskRequiredBuyableTests` (Core,
  4), `ShopModelTests` +2 (real-core `FilteredFor` exclude/include + degradation),
  `ClientWorldUnitTests` +1 (`DevelopmentTechMask_SeededByCreateBuckets_AndUpdatedBySideTechChange`).
  Full suite run twice (see below).

## Scope boundary / files

- `src/Allegiance.Core/Model/TechTreeBitMask.cs` — shared `RequiredTechsBuyable`
  containment (P283 math exposed once).
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — per-side dev-tech mask
  (CREATE_BUCKETS seed + SIDE_TECH_CHANGE update).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — dispatch for 191/75.
- `src/Allegiance.ClientCore.MonoGame/Rendering/ShopCatalog.cs` — `FilteredFor`.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — build shop from filtered
  catalogue at dock.
- Tests in `Allegiance.Core.Tests`, `Allegiance.ClientCore.MonoGame.Tests`,
  `Allegiance.ClientCore.Tests`.
- **Untouched**: P283's `StationSellsHull`, P189's `CompleteDevelopment`, native
  `~/Development/Allegiance` (read-only).
