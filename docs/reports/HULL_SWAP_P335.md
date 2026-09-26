# HULL_SWAP_P335 — wire the hangar shop UI to the real hull swap (Stage 2)

Date: 2026-09-22. Landed on `master` (not pushed — Domingo pushes).
Stage 2 of `~/Documents/Domingo-Llm-Wiki/pages/allegiance-hangar-loadout-plan.md`,
built directly on P334 (Stage 1, the real server-side hull swap in `ApplyLoadout`).

## What was already true when this task started (confirmed, not assumed)

The client wire layer already applied the authoritative ACK: `ClientWorld.Apply(BuyLoadoutAck)`
sets `ShipEntity.HullId = ack.HullId` and `ShipEntity.Loadout = ack.Parts`
(ClientWorld.cs:1066-1072), and the renderer resolves every ship's model from
`ShipEntity.HullId` (Game1.cs UpdateFlying). So the "optimistic ChosenHullId
guess" the brief worried about is NOT used for the flown ship — `ShopModel.ChosenHullId`
only drives the request (`SendBuyLoadout(shop.EffectiveHullId, …)`) and the shop's
pending-loadout display. The ACK wire shape is unchanged by P334 (same
`BuyLoadoutAck` codec); what changed is that its VALUES are now authoritative:
`HullId` echoes the real applied hull, and the `Parts` tail is the real
post-swap mounted set (including P334's CanMount drop of non-fitting parts).

## The client-side change

1. **`Rendering/ShopModel.cs`** — the "Honest limits (Phase 2): the server does
   not yet hot-swap a ship's physical hull… that hull-swap is a named follow-up"
   doc comment was stale after P334. Rewritten to state the real contract. Added
   helpers that expose the now-real swap to the UI, all pure/testable:
   - `EffectiveHull` — the catalogue record for the hull the shop will launch.
   - `WillSwapHull` — true iff a genuinely different hull is chosen (the exact
     condition under which the server applies the CanMount drop policy).
   - `ChosenPartsThatDontFitEffectiveHull` — which chosen parts the server will
     drop on launch, mirroring P334's server-side CanMount gate.
2. **`Rendering/PartInfoLayout.cs`** — the highlighted-part "COMPATIBILITY"
   verdict previously judged the part against the pre-dock CURRENT hull. Once a
   player has chosen a different hull, that's wrong relative to what P334 applies
   (it judges against the new hull and DROPS non-fitters). Now the verdict is
   judged against `EffectiveHullId` and labelled "chosen hull" vs "current hull";
   when a swap is pending and the part won't fit, it explicitly says "dropped on
   launch".
3. **`Rendering/ShopLayout.cs`** — the PENDING LOADOUT now flags each chosen part
   the server will drop for the chosen hull (DROPPED on launch), gated on
   `WillSwapHull` so a same-hull loadout (free-mounted, nothing dropped) never
   warns spuriously.

## Gap found in P334's own work

None functional. The only staleness was client-side documentation (the
`ShopModel` "cosmetic-only" note), which this task fixed. Server-side P334's
swap, re-seed, and CanMount drop all behave as the live proof shows.

## Verification — real live proof (required)

`tools/hullswap-proof/` is a new reusable harness: it boots a real in-process
server, connects a real MonoGame-rendering client (`ConnectionSession` + the same
`HullModelResolver`/`ShipRenderer`/compiled content models the in-game client
uses), docks, opens the real shop (`ShopModel`), picks a genuinely different
hull, launches, and — once the ACK lands — renders the new hull's model to PNG
and logs the server-reported physics. Ran twice with two different target hulls
(not a one-shot fluke). Artifacts in `.verify_p335/` (also
`hullswap_proof.log`):

- Swap A: docked hull #1 'Adv Miner' → shop pick → launch → server
  `BUY_LOADOUT_ACK hull 890`, client `ShipEntity.HullId=890` (authoritative),
  StationId=-1 (undocked). Server physics: hull 890, Mass 50, HitPoints 800,
  Speed 80, Thrust 1000, Energy reseeded 1500 (=new MaxEnergy), Ammo 1440.
  Screenshots: `hull_before_swap1.jpg` (asset bios_scout_upscaled) /
  `hull_after_swap1.jpg` (asset dreg-bbr2) / `composite_swap1.jpg`.
- Swap B: docked hull #1 → launch → `BUY_LOADOUT_ACK hull 13`, client
  `ShipEntity.HullId=13`. Server physics: hull 13, Mass 40, HitPoints 225,
  Speed 180, Thrust 1200, Energy 1500, Ammo 800. Screenshots:
  `hull_before_swap2.jpg` / `hull_after_swap2.jpg` (asset dreg-scout2) /
  `composite_swap2.jpg`.

Both composites were inspected and show clearly DIFFERENT ship silhouettes
(BEFORE angular Adv Miner vs AFTER organic Gunship / blocky green Adv Scout) —
the model rendering follows the real new hull, and the server-physics log shows
genuine per-hull differences (Mass 50 vs 40, Speed 80 vs 180, HitPoints 800 vs
225), i.e. a real, non-cosmetic change.

Plus a deterministic client-side live test, `tests/Allegiance.ClientCore.Tests/
HullSwapClientLiveTests.cs` (the client companion to P334's server-side
HullSwapLiveTests):
- `Hull_swap_updates_the_clients_ship_entity_to_the_real_new_hull_Live` — a real
  docked `ConnectionSession` client launches with a genuinely different hull and
  its `ShipEntity.HullId` lands on the real new hull (ACK-driven), StationId
  reverts (undock). Red if the client ignored the ACK's authoritative hull.
- `Hull_swap_into_an_incompatible_hull_drops_the_part_on_the_clients_loadout_Live` —
  swap into a hull that can't host a requested weapon: the server drops it and
  the client's `ShipEntity.Loadout` reflects the real post-drop set, never a UI
  state promising the dropped part.

## Build / test

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**
  (new `tools/hullswap-proof` added to the solution).
- `Allegiance.ClientCore.MonoGame.Tests`: **616 pass** (baseline 611, +5 new
  ShopModel/ShopLayout/PartInfoLayout tests).
- `Allegiance.ClientCore.Tests`: **164 pass** (baseline ~162, +2 new live tests);
  the only intermittent failures across runs are the pre-existing rotating
  live-network timing flakes (RetryCleanup / ThreePeerSeeded / FullSessionKill,
  etc.) — each passes in isolation, and none touch this change.
- `Allegiance.Lobby.Tests`: **12 pass**.
- `Allegiance.Core.Tests`: untouched by this task (the assembly is
  byte-identical); the usual rotating live-network flakes appear across runs,
  none related to the shop/hull client work.

## Scope boundaries / exclusions (unchanged from the brief)

- No shop-layout redesign / category tabs / 3D hangar preview (Stage 3).
- No default-spawn-hull fix (Stage 4).
- No server-side hull-swap changes beyond P334.
- Native reference (`~/Development/Allegiance`) untouched (read-only).
