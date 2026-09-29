# P434 — real missile launchers in the player-facing shop UI

## Summary

Domingo reported live: *"can't find missiles on the store."* Root cause
confirmed (real, and separate from P433, which only covered `allegiance-mcp`
and `tools/vizfight`'s bot loadout code, not the MonoGame client's own shop
screen): `ShopCatalog.TryLoad` (`src/Allegiance.ClientCore.MonoGame/Rendering/
ShopCatalog.cs`) built its `Parts` list purely from `core.PartTypes.Values`,
which does **not** contain missile launchers — those live in the SEPARATE
`StaticCore.Launchers` table (the compact 24-byte `OT_partType` launcher form),
so a real human player browsing the shop could never see, buy, or equip one.

This task makes a real launcher a real, purchasable, equippable shop row,
reusing P433's resolution logic verbatim rather than re-deriving it.

## The data-model extension (minimal, no parallel UI system)

The existing shop model/view/buy path (`ShopModel`/`ShopLayout`/`Game1.Panes`)
is built around `DataPartTypeIGC` rows addressed by `PartId`. A missile
launcher is *not* a `DataPartTypeIGC` record in the core — but P433 already
provides the one shared bridge from a real launcher `PartId` to the mountable
part the shop, the server's `ApplyLoadout`, and the `FM_CS_FIRE_MISSILE(70)`
handler all expect:

  `StaticCore.TryBuildMissileLauncher(partId, out part)` ->
  `DataPartTypeIGC.FromLauncher(partId, launcher, missile)`

So P434 reuses that bridge instead of inventing a parallel UI row type.
Because the launcher's *display name / part mask / tech mask* live on the
MISSILE type (embedded `LauncherDef`) and its *real carried ammo* on the
launcher record itself (`DataLauncherTypeIGC.Amount`), and the built
`DataPartTypeIGC` shape has no ammo slot, the catalogue carries each launcher
as a small pair type:

  `LauncherOffer(DataPartTypeIGC Part, short Ammo)`

### `ShopCatalog.cs`
- New `Launchers: IReadOnlyList<LauncherOffer>?` (optional param — `Empty` stays
  `new([],[])`).
- `TryLoad` now walks `core.Launchers` (ordered by `PartId`), and for each real
  launcher whose expendable is a genuine MISSILE (via the exact P433
  `TryBuildMissileLauncher` gate) appends `LauncherOffer(part, launcher.Amount)`.
  Chaff/mine/probe dispensers (same `Launchers` table, non-missile expendable)
  fail that gate and are correctly excluded — mirroring P433.
- `FilteredFor` now applies the **same** P284 tech gate it already applies to
  hulls and parts to the launchers: an item shows iff
  `TtbmRequired ⊆ (stationLocal | sideDev)`
  (`TechTreeBitMask.RequiredTechsBuyable`), where `TtbmRequired` on the built
  launcher part is the missile's `LauncherTtbmRequired`. A launcher with an
  unmet tech requirement is filtered OUT exactly like any other gated part —
  never shown unconditionally. Nil requirement (all-clear) stays offered.

### `ShopModel.cs`
- Carries `_launchers` (normalized to empty) and the flat row count becomes
  `hulls + parts + launchers` (rows are laid out hulls, then parts, then
  launchers).
- New `CursorOnLauncher` / `CursorLauncher` accessors; `CursorPart` unchanged
  (a launcher row is not a part row).
- `Activate()` gains a launcher branch: SPACE on a `[LAUNCHER]` row toggles the
  launcher into `ChosenParts` as `ExpandedPartData(realPartId, realAmmo, mount,
  FullyMounted)` — the **exact wire shape** a P433-equipped bot's launcher
  entry carries on `BUY_LOADOUT`. Toggle off is the same remove-if-present key,
  exactly like any other part. (Part toggling refactored into a shared
  `TogglePart(partId, amount)`.)
- `LookupPart` now also resolves launcher `PartId`s through `_launchers`, so the
  existing P335 fit machinery (`ChosenPartsThatDontFitEffectiveHull` ->
  `DataHullTypeIGC.CanMountPart`) treats a bought launcher exactly like any
  other part.

### `ShopLayout.cs`
- Renders launcher rows after hulls and parts: `marker NAME  $n/a NNrds [LAUNCHER]`.
  Display name is the real launcher name (missile `LauncherName` via
  `FromLauncher`); the ammo column reads the launcher's REAL carried ammo
  (`DataLauncherTypeIGC.Amount`). The native data has no launcher price, so the
  price column honestly reads `$n/a` (never a fabricated `$0`).
- `LookupPart` resolves launcher ids for the current-ship / pending-loadout
  lines and the P335 fit warnings.

### `Game1.Panes.cs` / `Game1.Lifecycle.cs`
- Docked log line and boot `shop catalogue:` line now also report the launcher
  count. The buy flow itself is **unchanged**: `SendBuyLoadout(effectiveHull,
  fLaunch, parts: shop.ChosenParts)` already carries launcher entries as
  ordinary `ExpandedPartData`, so the normal launch path needs no special-casing
  for "came from the human shop click" vs an MCP/vizfight bot spawn.

## Confirmed real data (PCore001.igc)

Of the 123 `core.Launchers` records, **56 are genuine missile launchers**
(the rest are chaff/mine/probe dispensers whose expendable is not a missile).
All 56 are offered in the shop with real names + real ammo, e.g.:
`LRM Aleph Res 1 (2)`, `LRM Killer 1 (2)`, `LRM Killer Swarm 1 (10)`,
`XRM Cruise 1 (10)`, `MRM Nerve Gas (1)`, `SRM Seismic (2)`. Launcher `PartId`s
are disjoint from the 117 `core.Parts` ids (verified: 0 collisions).

## Tech & compatibility filtering — confirmed applied

- **Tech:** `FilteredFor` filters `Launchers` with the identical
  `RequiredTechsBuyable(TtbmRequired, stationLocal, sideDev)` containment used
  for hulls/parts; a launcher with an unmet requirement is hidden; a
  nil-requirement launcher stays; the degrade-on-unknown path is unchanged.
  Proven in `ShopLauncherCatalogueTests.FilteredForAppliesTheSameTechGateToLaunchers`.
- **Compatibility:** a bought launcher that does not mount on the effective
  (to-be-launched) hull is flagged in `ChosenPartsThatDontFitEffectiveHull`
  via the real `CanMountPart`, exactly like a non-fitting weapon.
  Proven in `ShopLauncherCatalogueTests.NonMountingLauncherIsFlaggedForTheChosenHullLikeAnyOtherPart`.

## Tests

New `ShopLauncherCatalogueTests` (6, pure-logic, real PCore001.igc):
1. Catalogue carries real missile launchers with real names & ammo (every
   offered id is a real `core.Launchers` record whose expendable is a missile).
2. Launcher part-ids are disjoint from regular part-ids.
3. Moving onto a launcher row + SPACE buys it with its real ammo; SPACE again
   toggles it off.
4. A bought launcher renders as a sensible `[LAUNCHER]` row (name + ammo).
5. `FilteredFor` applies the same tech gate to launchers.
6. A non-mounting launcher is flagged for the chosen hull like any other part.

New `ShopLauncherBuyLiveTests.ShopLauncherPurchase_GrantsTheRealLauncher_Live`
(1, `[Collection("LiveNetwork")]`, the same real-wire method P433/P394 use):
stands up a real in-process server with a docked spawner, connects a real
`ConnectionSession`, replays the HUMAN shop sequence (cursor onto a real
`[LAUNCHER]` row, SPACE `Activate`, L `SendBuyLoadout(fLaunch:true,
parts: ChosenParts)`), and asserts the authoritative BUY_LOADOUT_ACK-granted
loadout carries the real launcher `PartId` with its real `Amount`, fully
mounted.

## Build + full test suites (each run twice)

- Whole solution `AllegianceCore.slnx -c Release`: **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **848/848 pass** both runs
  (incl. the 7 new P434 tests).
- `Allegiance.Lobby.Tests`: 12/12 pass both runs.
- `Allegiance.Core.Tests` / `Allegiance.ClientCore.Tests`: only the
  pre-existing documented live-wire timing flakes, all in `...LiveTests`
  classes involving real server traffic (e.g. LiveRipcord, LiveChatSectorWire,
  LiveTurretGunnerBinding, JoinFlowLive, ShipDeleteGoneLive). Counts varied
  between runs (Core 8→7, ClientCore 2→4), confirming nondeterminism; **none**
  touch shop/catalogue symbols (my changes are confined to the MonoGame client).

## Live verification — real proof

1. **Real MonoGame client ↔ real server.** Built the P434 client (Release),
   connected it to the live server on `127.0.0.1:6073`, and the running client
   logged its real shop catalogue:
   ```
   [monogame] shop catalogue: 447 hulls, 117 parts, 56 missile launchers
   ```
   The actual graphical client now carries the real launcher table at runtime —
   pre-P434 this read `117 parts` and no launchers. Real launcher names/ammo
   were confirmed from the same core (`LRM Killer 1 (2)`, etc.).

2. **Real buy/equip over real wire.** The `ShopLauncherBuyLiveTests` live test
   reproduces the human shop purchase on a real server + real `ConnectionSession`
   and the real BUY_LOADOUT_ACK granted loadout carries the real launcher with
   its real ammo — the same real verification P433 used to prove a bot's
   loadout carries a launcher. (Buying/equipping through the normal
   `BUY_LOADOUT` flow produces the exact real loadout a P433-equipped bot would
   carry — same wire shape, no special-casing.)

> Honest note: driving the *graphical* client's 3D flight to physically dock a
> ship at a station (the ship spawns flying in the dev server, and the
> client's autopilot found no targetable station candidate on this instance) is
> not reliably automatable headlessly here. The two proofs above cover the task's
> stated verification ("a real BUY_LOADOUT ack naming the real part"), and the
> graphical client demonstrably enumerates the launchers in its catalogue; the
> docked-screen launcher rows are additionally pinned by the pure-logic layout
> test (`BoughtLauncherRendersAsASensibleLauncherRow`).

## Deliverables

1. Real launcher support in the MonoGame shop UI — committed on `master`
   (not pushed; Domingo pushes).
2. This report: `docs/reports/P434_SHOP_UI_MISSILE_LAUNCHERS.md`.

## Out of scope (unchanged, per brief)

- Missile lock/firing mechanics (P433) — untouched.
- `allegiance-mcp` / `tools/vizfight` — only read-only reuse of P433's
  `TryBuildMissileLauncher`/`FromLauncher` bridge.
- No new tech-tree rule — the existing `RequiredTechsBuyable` gate is reused
  exactly as-is.
