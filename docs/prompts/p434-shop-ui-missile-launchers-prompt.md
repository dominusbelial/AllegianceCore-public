# P434 — real missile launchers in the player-facing shop UI

## Why this task exists

Domingo, testing live: *"can't find missiles on the store."* Confirmed
real and separate from P433 (which only covers `allegiance-mcp` and
`tools/vizfight`'s bot loadout code, not the MonoGame client's own shop
screen). `src/Allegiance.ClientCore.MonoGame/Rendering/ShopCatalog.cs`'s
`TryLoad` (line 48-68) builds `Parts` purely from `core.PartTypes.Values`
— it never reads `core.Launchers` (`StaticCore.Launchers`, the SEPARATE
123-real-missile-launcher collection confirmed present in `PCore001.igc`
this session). `ShopCatalog` already carries a `MissileTypes` dictionary
(missile *type* flavor data), just never the launcher *parts* a real
player would need to buy and equip to actually carry one.

This task assumes **P433** (real launcher resolution + firing in
`allegiance-mcp`/`vizfight`) has already landed — reuse whatever real
launcher-resolution/compatibility logic it added rather than
re-deriving it from scratch; if it hasn't landed when this is picked
up, stop and say so.

## Scope

1. Extend `ShopCatalog` (`ShopCatalog.cs`) to also carry the real
   launchers from `core.Launchers`, resolved/shaped consistently with
   however P433 already resolves them — investigate whether
   `DataLauncherTypeIGC` can be shown in the shop list directly, or
   whether it needs mapping to something the existing `DataPartTypeIGC`-
   shaped UI code already expects (`ShopModel`/`ShopLayout` currently
   only knows `DataPartTypeIGC` rows — check what's the minimal real
   change, don't invent a parallel UI system for one new row type if
   the existing one can represent it with a small extension).
2. `ShopModel`/`ShopLayout`/`Game1.Panes.cs`'s buy flow: a launcher must
   be a real, purchasable, equippable row exactly like any other part —
   respecting the SAME real hull-mount-compatibility gate P432/P433
   already established (`DataHullTypeIGC.CanMountPart`) and the SAME
   real tech-tree buy-gate `ShopCatalog.FilteredFor` already applies to
   `Parts` (`ShopCatalog.cs`, the `TtbmRequired ⊆ (stationLocal |
   sideDev)` containment) — a launcher with unmet tech requirements
   must be filtered out exactly like any other gated part, not shown
   unconditionally.
3. Buying/equipping a launcher through the normal `BUY_LOADOUT` flow
   must produce the exact real loadout a P433-equipped bot would carry
   — same wire shape, no special-casing for "this came from the human
   player's shop click" vs "this came from an MCP/vizfight bot spawn."
4. Real, minimal UI feedback: the launcher's real name/ammo count (from
   `DataLauncherTypeIGC`) should read sensibly in the shop row, not just
   silently reuse `DataPartTypeIGC`'s field layout if that would show
   wrong/blank values — check what's actually available.

## Explicitly out of scope

- Any change to missile lock/firing behavior itself (P433's scope) —
  this task is specifically "can a real human player buy and equip one
  from the shop," not the firing mechanic.
- Any change to `allegiance-mcp`/`tools/vizfight` beyond reusing
  whatever resolution logic P433 already added (read-only reuse, not a
  rewrite).
- Any new tech-tree rule — reuse the existing gate exactly as-is.

## Verification — real proof, not just build/test

- **Live proof, required**: connect a real MonoGame client to a real
  server, dock, open the shop, and confirm a real launcher appears in
  the list (with sensible name/stats), can be bought, and the resulting
  loadout genuinely carries it (confirm via the same real verification
  P433 used to prove a bot's loadout carries a launcher — e.g. a real
  `BUY_LOADOUT` ack naming the real part, or a real fire attempt
  succeeding once locked).
- A test proving the shop catalogue's launcher list + tech/compat
  filtering in isolation (mirroring `ShopCatalog`'s existing test
  style).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. Real launcher support in the shop UI, committed on `master` (not
   pushed — Domingo pushes).
2. Report doc `docs/reports/P434_SHOP_UI_MISSILE_LAUNCHERS.md`: the
   exact UI/data-model extension made, the tech/compat filtering
   confirmed applied, and the live verification performed.
