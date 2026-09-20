# P268 — Hangar/loadout shop screen (first vertical slice)

Feature: when the local ship is docked, the MonoGame client now shows a real
hangar/loadout screen instead of the old fully-automatic undock. The player can
shop the catalogue (choose a hull, toggle parts), then launch with a real
`BUY_LOADOUT(fLaunch=true)` — and the purchase genuinely takes effect
server-side. This is deliberately the *first slice* of the docking/hangar UI,
not the whole hangar.

## Native content cited (the real shop)

The native docked hub is `WinTrek/hangarscreen.cpp` (308 L): a menu with
Launch / Command / Loadout / Invest / Teleport / Team / Help / Exit buttons
(panes `hangarscreen.cpp:23-30`, handlers `:92-108`). The two that matter here:

- **Launch** (`HangarScreen::OnButtonLaunch`, `hangarscreen.cpp:145-160`) calls
  `trekClient.BuyLoadout(trekClient.GetShip(), true)` — i.e. the client's launch
  IS a `FM_C_BUY_LOADOUT` with `fLaunch=true`. It never sends a separate undock
  message. This is exactly what `ConnectionSession.SendBuyLoadout(hull, true)`
  already encodes (`ConnectionSession.cs:121`).
- **Loadout** (`OnButtonLoadout`, `hangarscreen.cpp:194-199`) switches to the
  loadout view (the shop).

The shop itself is `WinTrek/loadout.cpp` (1514 L), whose `PartListItem`
(`loadout.cpp:23-60`) paints each buyable row with: a **name** (SmallFont at
(11,1), `:299-359`), a **price** `"$ %d"` at (147,1) (`:289`), and a
**category/subtitle** line (e.g. `Build X (count)`) at (11,42) (`:290-357`).
This slice reproduces that name + `$ price` + category tag per item, as a
single-column ASCII list.

Follow-ups explicitly NOT built here (named, kept out):
- Tech-tree **Invest**, team/alliance UI, teleport, and the full
  Launch/Command/Loadout/Invest/Teleport/Team/Help/Exit menu hub — separate tasks.
- **Cargo/trading** UI (the other `loadout.cpp` sibling) — separate task.
- **Turret-boarding** UI — related and now unblocked-adjacent (its own docking
  gating needs this docked state), but not built here.

## What this slice does

- **Dock detection**: the client already knew the ship was docked —
  `ShipEntity.StationId != -1`, latched by the real `DockedNotify(61)` /
  `SHIP_STATUS(90)` wire traffic (`ClientWorld.cs:648-652`, `ShipEntity.cs:51`).
  Previously `Game1` ignored it and force-undocked on `STAGE_STARTED`.
- **Docked screen replaces auto-undock**: `Game1.HandleDockedScreen` is entered
  when the local ship is docked. The old fire-on-Started `SendDocked()` is gone
  (that removal is what makes the docked state reachable at all).
- **Shop catalogue (honest sub-gap closed)**: the MonoGame client had no static
  core (the documented P234/P265 constraint — `MissileModelSource`). The shop
  *needs* hull/part names+prices, which the wire never carries (only ids), so
  this slice makes the catalogue reachable by parsing the same real `PCore001.igc`
  the server boots (`ShopCatalog.TryLoad`, artwork dir). Graceful: a missing core
  yields an empty catalogue so the screen still shows the current ship and
  launch/undock stay usable.
- **Interaction** (`ShopModel`, pure/headless-testable): the flattened buyable
  list is hulls-then-parts with a cursor. Up/Down move it; SPACE picks a hull or
  toggles a part in/out of the pending loadout; L sends the real
  `SendBuyLoadout(effectiveHull, fLaunch:true, chosenParts)`; U sends the plain
  `FM_C_DOCKED(NA)` to undock without buying. A fallback timer auto-launches the
  current loadout after 30 s so an unattended dock never strands the POC player
  (kept: the POC must stay "actually playable" without babysitting).
- **Rendering** (`ShopLayout` pure + `DockedScreenRenderer`): a dark docked
  replacement view showing CURRENT SHIP, the SHOP list (scrolling viewport so the
  pending line stays visible with 447 real hulls), and PENDING LOADOUT. Pure-ASCII
  rows (HUD spritefonts bake ASCII only).

## Honest gaps found

1. **Server does NOT hot-swap the physical hull (Phase 2)** — `ApplyLoadout`
   (`Server.cs:9470-9477`) grants the parts but the ship's `_hull` is fixed at
   spawn; the ACK *echoes* the requested hull id while the physical hull is
   unchanged. Consequence: selecting a different hull in the shop is requested
   and acknowledged, but the ship keeps its spawn hull. The **definitive real
   effect of a purchase is the mounted parts** (`Server.cs:9497` grants them; the
   ACK tail = the ship's real mounted parts). Hull hot-swap is a named follow-up.
2. **No per-station tech/bucket gating** — the shop lists every hull/part in the
   core, not just what a given station's tech actually sells. Faithful per-station
   availability is the Invest/tech-tree territory (a named follow-up), matching
   the P225 "mount-count fallback" honesty precedent (the client has no catalog at
   all was itself the P234/P265 constraint).
3. The hull ACK-echo on `ShipEntity.HullId` is a brief echo: the subsequent
   `SHIP_STATUS` during undock rewrites it to the physical hull, so the client's
   final `HullId` correctly reflects the real (unchanged) hull.

## Verification

- Whole-solution build: **0 warnings, 0 errors** (`AllegianceCore.slnx`, Debug).
- **Live proof** (`/tmp/p268verify`, real in-process server + real
  `ConnectionSession`, real dock): ship docked at real station 1051; shop picked
  hull 2 (was hull 1) + real weapon part 3 `PE Gat Gun 1`; sent real
  `BUY_LOADOUT(fLaunch=true)`; server undocked; granted parts=1 and the ACK tail
  contains the shopped weapon → **"the shop's weapon purchase genuinely took
  effect server-side."** (Physical hull stays 1 — the Phase-2 gap above, honest.)
  Offscreen PNGs `dock_pre.png` / `dock_shop.png` show the screen: title
  `STATION DOCKED - HANGAR LOADOUT`, CURRENT SHIP (Adv Miner), SHOP list of real
  hulls/parts/prices, PENDING LOADOUT switching to the chosen hull (Enh Miner).
- **Must-fail proven**: disabling the shop select logic (hull-pick + part-toggle)
  reds `Activate_on_a_hull_row_picks_that_hull`,
  `Activate_toggles_a_part_in_and_out_of_the_loadout` and
  `Live_proof_payload_differs_from_default_and_carries_a_real_weapon`; restored
  → green.
- **Tests**: MonoGame +10 (`ShopModelTests`, `ShopLayoutTests`), all green;
  full MonoGame suite **366/366**, ClientCore **123/123**, Core
  **3116/3117** (run 1) / **3115/3117** (run 2) — the only failures are the
  pre-existing documented flakes `LiveTurretGunnerBindingTests` and
  `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` (both
  machine-state live-network flakes with zero refs to P268 symbols; P268 touches
  no Core/server code). Whole suite run twice as required.

## Scope boundary / files

- `src/Allegiance.ClientCore.MonoGame/Rendering/ShopCatalog.cs` (new) — catalogue.
- `src/Allegiance.ClientCore.MonoGame/Rendering/ShopModel.cs` (new) — shop logic.
- `src/Allegiance.ClientCore.MonoGame/Rendering/ShopLayout.cs` (new) — pure rows.
- `src/Allegiance.ClientCore.MonoGame/Rendering/DockedScreenRenderer.cs` (new).
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — dock detection + input + draw;
  removed the auto-undock.
- `tests/...MonoGame.Tests/ShopModelTests.cs`, `ShopLayoutTests.cs` (new).
- No wire changes; native `~/Development/Allegiance` untouched (read-only).
