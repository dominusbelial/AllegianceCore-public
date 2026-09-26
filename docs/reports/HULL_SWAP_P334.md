# HULL_SWAP_P334 — real server-side hull swap (Stage 1 of the hangar/loadout plan)

Date: 2026-09-22. Landed on `master` (not pushed — Domingo pushes).
Stage 1 of `~/Documents/Domingo-Llm-Wiki/pages/allegiance-hangar-loadout-plan.md`.
Queued after P332/P333 client-scale work; P335 (Stage 2, wire the shop UI) is the
next task in line.

## Problem

Picking a different hull in the existing hangar shop UI (`Rendering/ShopModel.cs`,
P268) was cosmetic: the client requested it and the server ACK'd the echoed hullId,
but `Ship._hull` was fixed at spawn and the server kept SIMULATING the old hull — a
real visual/physics divergence (the plan's own doc comment + kimi M-5 finding).
`Ship.ChangeHull` already existed (the port of native `CshipIGC::SetBaseHullType`,
used by the death-respawn/lifepod path), but `ApplyLoadout` never called it on a
hull change — the P268 branch logged "hull hot-swap not implemented (Phase 2)".

## Native's real behavior — IN-PLACE mutation, not retire-and-recreate

Verified directly in the native source, not guessed:

- `BUY_LOADOUT` (`FM_C_BUY_LOADOUT`) is dispatched on the server to
  `pfsPlayer->GetIGCShip()->PurchaseShipLoadout(...)` — `FedSrv.CPP:6189`.
- `PurchaseShipLoadout` (shipIGC.h:989-1108) mutates the **existing** `CshipIGC`
  object **in place**: on a hull change it resolves the target hull, then
  `if (pht != m_myHullType.GetHullType()) SetBaseHullType(pht)` (shipIGC.h:1019-1020).
  There is **no retire-and-recreate** — the same ship keeps its id/side/position/
  score/wallet; only its base hull type is swapped.
- `SetBaseHullType` (shipIGC.cpp:1352-1458) is the in-place mutation: it detaches
  every mounted part first (the caller already moved `m_parts` to `partsOld`,
  shipIGC.h:1007-1018), then re-seats the hull, then the mount loop re-adds loadout
  parts (shipIGC.h:1022-1108).

The port's `Ship.ChangeHull` is the direct port of that `SetBaseHullType`
primitive (Ship.cs:189-234), already invoked by the eject/lifepod seam
(FedSrv.CPP:12052 -> Server.Combat.cs:1234). This task wires it into the loadout
purchase path, matching native's design.

## Hull-derived physical values — enumerated, not guessed

From `Ship.cs` itself (this is the honest inventory): nearly every hull-derived
physical value is a **live getter over `_hull`**, so swapping `_hull` in place makes
them all reflect the new hull automatically with no per-field recomputation:

- `Mass => _hull.Mass` (Ship.cs:157)
- `HitPoints => _fraction * _hull.HitPoints` (Ship.cs:314)
- `HullType => _hull` (Ship.cs:160); `EffectiveSignature` (Ship.cs:293), `RipcordSpeed`
  (Ship.cs:779)
- thrust/turn-rates/torques/speed/max-energy/max-fuel/hardpoints — all read `_hull`
  live in the movement/damage math (Ship.cs:3407-3478, 3999, 4450, 4696-4705; the
  hardpoint layout is `HullType.Hardpoints` / `HullType.CanMountPart`)

The only construction-**seeded** (cached) hull fields are the runtime pools `Energy`
(constructor Ship.cs:152) and `Ammo` (Ship.cs:153) — and `_fraction`. Native
`SetBaseHullType` resets these to empty (`m_fraction = 1.0f`, `m_energy = 0.0f`,
`m_ammo = 0`, plus fuel/ore, shipIGC.cpp:1428-1437). The port has **no passive
energy regeneration** (Energy refills only at dock repair, Ship.RepairAndRefuel:3298,
or the constructor's full seed:152), so literal zeroing would strand a launched
hull-swapped ship unable to fire — a broken state the task forbids. This task instead
RE-SEEDS the two pools to the NEW hull's full values exactly as the constructor does
for a fresh ship (`Energy = hull.MaxEnergy`, `Ammo = hull.MaxAmmo`), the
port-functional equivalent of native's eventual full recharge, plus `SetFraction(1.0f)`
(matching native's full-repair `m_fraction = 1.0f`). These resets live in the purchase
path — NOT inside `ChangeHull`, so the shared primitive keeps its pinned
"swap-only-the-hull-record" contract that the P270 cloak test relies on
(`CloakVisibilityGateLiveTests` tops energy back up after it).

## Mounted-parts policy — native CanMount, cited

Native `PurchaseShipLoadout` only mounts a part the (new) hull can host:
`m_myHullType.CanMount(ppt, mountID)` (shipIGC.h:1030); a part with nowhere to mount
is simply not mounted, and the fewer-slots case is resolved by the clear+rebuild
(every old part is detached before `SetBaseHullType`, shipIGC.h:1007-1018).

The port mirrors this in `ApplyLoadout`: on a hull change, each requested part is
gated by `hull.CanMountPart(partType)` — the P276 faithful port of native `CanMount`
(DataHullTypeIGC.cs:247, hullTypeIGC.h:199-213) — and a part that doesn't fit is
dropped (logged, counted `skipped`, absent from the ACK tail). Two stated port
simplifications:

1. Hull-level (`CanMountPart`) rather than native's per-mount `CanMount(ppt, mountID)`,
   because the POC shop's mount ids are sequential list positions
   (`ShopModel.RenumberMounts`, ShopModel.cs:165-168), not real hardpoint slots
   (same spirit as the treasureack CanMount gate, Server.ShipCommands.cs:1380-1383).
2. The gate is scoped to the **hull-change** path: a same-hull loadout (no layout
   change) keeps the port's existing free-mount behavior, matching the POC shop and
   the pre-existing loadout tests (the spawn hull mounts no weapon in PCore001, so an
   always-on gate would have regressed `DockLoadoutLiveTests.BuyLoadout_WithRealParts_...`).

Native's monetary `CanBuy` gate (shipIGC.h:1000-1001 rejects the whole purchase if the
hull isn't buyable) is **not** ported — this port has no station money/purchase economy
for loadout (it grants parts freely, as it always has). Stated, not silently dropped.

## Implementation

`src/Allegiance.Server/Server.ShipCommands.cs`, `ApplyLoadout`:
- When `buy.HullId != ship.HullType.HullId` (a real hull change): log the swap,
  call `ship.ChangeHull(hull)` (in-place `SetBaseHullType`), then re-seed the
  construction-seeded pools for the new hull (`SetFraction(1.0f)`,
  `Energy = hull.MaxEnergy`, `Ammo = hull.MaxAmmo`). The ACK's `HullId` echo now
  equals the ship's REAL hull — the P268 "echo ≠ physical hull" divergence is gone.
- The mount loop gains the `hullChanged && !hull.CanMountPart(partType)` drop gate.

No changes to `Ship.cs`, `ShopModel.cs`, or native reference (read-only).

## Live proof (server-side, real bot over loopback TCP)

`tests/Allegiance.Core.Tests/HullSwapLiveTests.cs` (reads `Server.Ships` directly —
friend access, unlike the ClientCore harness):

1. `BuyLoadout_with_a_different_hull_swaps_the_server_simulated_physics_live` — a
   docked real bot sends BUY_LOADOUT for a genuinely different hull (highest HullId vs
   the spawn's lowest). Server asserts: HullId changed, and Mass / HitPoints / Speed /
   Thrust / MaxTurnRates / TurnTorques all equal the NEW hull's values AND all differ
   from the before values; Energy reset to 0 (native SetBaseHullType); the SAME Ship
   object was mutated (still docked, not retire-and-recreate). Red before P334.
2. `Hull_change_into_an_incompatible_hull_drops_nonmountable_parts_live` — picks a real
   (part, fit-hull, non-fit-hull) triple from PCore001; a swap onto fit-hull mounts the
   part, then a swap onto the non-fit hull DROPS it (Parts empty, hull swapped, Energy
   reset) — the CanMount policy fires instead of corrupting state.

### Must-fail (wiring is load-bearing, verified by temporarily removing each piece)
- Removing `ship.ChangeHull(hull)` → both tests red.
- Removing the CanMount drop gate → test 2 red (the incompatible part mounts anyway).

## Build / test

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full suite runs below (ran 2x, compared to the P340 baseline of Core ~3067 /
  ClientCore 83 / MonoGame ~275; only the documented timing flakes may interrupt —
  each confirmed green in isolation / environmental, not a regression).

## Scope boundaries / exclusions (unchanged from the task brief)

- No client-side UI change — that's Stage 2 (P335).
- No 3D hangar preview / category-tab redesign / default-spawn-hull fix — Stages 3/4.
- Native reference (`~/Development/Allegiance`) untouched (read-only).
- `Ship.ChangeHull` shared primitive left unchanged (pinned contract preserved).
