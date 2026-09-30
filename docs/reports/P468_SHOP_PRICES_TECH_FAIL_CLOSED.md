# P468 — the shop shows no prices and its tech filter fails open

Date: 2026-09-30 · Landed on `master` (not pushed; Domingo pushes) · Branch `master`

Assumes P442 (client wallet) landed — it has (HEAD `e93f60ef`). The task brief's line
numbers were cited at `d5df96ec`; re-checked against HEAD during the work.

## Summary

Two real defects fixed, plus the client half of shop pricing:

1. **Server: a side switch never re-sent the new side's development mask.** A
   player who joined side 0 then switched to side 1 through the real
   `TryJoinSide` path was never told side 1's `DevelopmentTechs`, so the
   client's `ClientWorld.DevelopmentTechsBySideId` stayed keyed to the OLD side
   and the docked shop offered **everything** (fail-open). Now `TryJoinSide`
   re-sends the joiner its new side's `CREATE_BUCKETS(191)` + `BUCKET_STATUS(65)`
   set, using the same construction `CompleteJoinAndLaunch` uses (factored into a
   shared `SendSideTechToPeer`).
2. **Client: the tech filter failed open.** `ShopCatalog.FilteredFor` returned
   the **full unfiltered catalogue** whenever the side's development mask was
   unknown (which it always was right after a side switch). It now **fails
   closed**: an unknown mask offers only items with an all-clear tech
   requirement (`TtbmRequired` empty) — the set that is correct regardless of
   the unknown mask — never the full catalogue.
3. **Client: prices in the shop.** Each hull / part / launcher row shows its real
   price (the launcher rows now show the real P441 price instead of `$n/a`), and
   the pending-loadout panel gains a **PRICE summary**: new loadout value,
   current ship value, `cost = new − current` (negative = refund) and `MyMoney`,
   with a live affordability verdict. Rows the player can't afford are greyed,
   and confirming (L / Launch) an unaffordable total is **blocked client-side**
   with a visible reason instead of silently sending a `BUY_LOADOUT` the server
   (P441) would refuse. Pricing math lives in the pure, headless-testable
   `ShopModel`; the renderer only draws.

## Pre-fix failing-test evidence

The new loopback wire test
(`tests/Allegiance.ClientCore.Tests/SideSwitchDevMaskLiveTests.cs`) drives a real
`ConnectionSession`/`ClientWorld` against an in-process server: the client seats
on side 0 (join-sync `CREATE_BUCKETS` seeds side 0's mask), then switches to side
1 via a real `POSITIONREQ` (which the server auto-accepts to the empty side
through `TryJoinSide`, `Server.Dispatch.cs:630`). Written first and run against
the **unfixed** server (the `Server.Lobby.cs` change stashed), it failed:

```
Side_switch_teaches_the_client_the_new_sides_development_mask [FAIL]
  Error Message:
   client never learned side 1's development mask after the side switch (P468 server fix missing)
```

i.e. after the side switch `ClientWorld.DevelopmentTechsFor(side1)` stayed `null`
— exactly the fail-open precondition the brief described. With the fix in place
the same test passes: the client learns side 1's real mask and the computed
docked-shop offered set is the tech-correct (P284) set, strictly smaller than the
full catalogue.

## What a native side-joiner receives (cited)

Native `CFSMission::AddPlayerToSide` (`src/FedSrv/fsmission.cpp:539+`), for a
non-leader player, sends the joiner's OWN connection:

- `SET_BRIEFING_TEXT` (fsmission.cpp:728-733), then
- `SendMissionInfo(pfsPlayer, pside)` (fsmission.cpp:736), which includes:
  - `CREATE_BUCKETS` built from `pside->GetDevelopmentTechs()/GetInitialTechs()`
    (fsmission.cpp:4255-4258),
  - one `BUCKET_STATUS` per bucket (fsmission.cpp:4260-4273),
  - per-side-mate `AUTODONATE` + `SET_MONEY` (fsmission.cpp:4276-4301).

So a genuine side switch **re-runs** the `CREATE_BUCKETS` + `BUCKET_STATUS`
preface to the switcher — the port's `TryJoinSide` now mirrors that. The port
uses the same `CREATE_BUCKETS` (no `SideId`, applies to the client's `MySideId`)
rather than `SIDE_TECH_CHANGE(75)` because it is the faithful `SendMissionInfo`
shape the brief named; ordering is safe because `TryJoinSide` sends it AFTER the
`JOIN_SIDE(125)` broadcast on the same FIFO connection, so the client updates
`MySideId` (P438 `Apply(JoinSide)`) before storing the mask
(`Apply(CreateBuckets)` reads `MySideId`).

## Design decisions

### 1. Server: `TryJoinSide` re-sends the new side's mask on a real switch

The `CREATE_BUCKETS` + per-bucket `BUCKET_STATUS` construction that lived inline
in `CompleteJoinAndLaunch` is factored into a private `SendSideTechToPeer`
(identical bytes) and reused there. In `TryJoinSide`, after the JOIN_SIDE
broadcast, when the ship's side genuinely changed (`oldSideId is { } prev &&
prev != newSideId` — the port's "team lobby" is a null `SideId`, i.e. the very
first seat, whose `CREATE_BUCKETS` `CompleteJoinAndLaunch` already sends), the
joiner's own connection is sent the new side's mask + buckets. This covers every
side-switch entry point (POSITIONREQ auto-accept, leader approval, randomize-teams)
uniformly, and only fires on an actual change.

### 2. Client: `FilteredFor` fails CLOSED

`ShopCatalog.FilteredFor(stationLocal, sideDev)` now returns, when either tech
term is null, a catalogue of only the items whose `TtbmRequired` is all-clear
(the union with an empty station + empty side mask reduces the P284 containment
to `need ⊆ ∅` ⇔ all-clear). The P268 degrade-on-unknown (return `this`, the full
catalogue) is gone. `Game1.Panes.cs` now routes **both** terms through
`FilteredFor` even when the side mask is unknown — previously an unknown mask
left `offerCat` as the unfiltered `_shopCatalog`, which is what actually opened
the shop. Doc comment updated. The fail-closed choice (all-clear items only) was
selected over a "waiting for tech data" blank state because it still serves the
legal starter gear while hiding everything undeveloped.

### 3. Client: pricing in `ShopModel` (pure), drawing in the renderer

`ShopModel` (still MonoGame-free) gains:
- `ValueOf(hullId, parts)` = hull price + Σ part prices — native `CshipIGC::GetValue`
  (`shipIGC.h:396-406`), the same helper the server uses to price a purchase.
- `CurrentShipValue`, `NewLoadoutValue` (effective hull + chosen parts),
  `Cost = New − Current` (negative = refund).
- `MyMoney` (nullable), `AffordabilityKnown`, `CanAffordTotal` (`Cost ≤ MyMoney`;
  the exact boundary is affordable).
- `TryLaunch()` — returns false and sets a visible `BlockReason` when the wallet
  is known and `Cost > MyMoney`; true (never blocked) when the wallet is unknown
  (can't prove it unaffordable). `Activate()` clears a stale `BlockReason`.
- Per-row grey-out: `HullRowGreyed` (net hull-swap cost > money; the effective
  hull is never greyed) and `PartRowGreyed` (unowned part/launcher price > money).

`ShopLayout.Build` shows every item's real price (launchers now render their real
P441 price instead of `$n/a`), marks unaffordable rows `Greyed`, and adds the
PRICE summary block + the `BlockReason` line. `DockedScreenRenderer` styles
`Greyed` rows muted and the block-reason/verdict rows red. `Game1.Panes.cs`
blocks the L launch when `TryLaunch()` is false, printing the reason to the
console and letting the layout show it.

## Text description of the price column and summary line

Each shop row carries a real dollar price in the price column (the `ShopLayout`
format, reproduced from a live dump):

```
SHOP (buyable hulls, parts & launchers)
> Hvy Bomber                   $500    [HULL]      <- cursor, real price
  Hvy Troop Transport          $1000   [HULL]      <- greyed (unaffordable)
  Destroyer                    $3000   [HULL]      <- greyed
  ...
  LRM Aleph Res 1              $1000   2rds [MISSILE]   <- launcher real price + ammo
  MRM Nerve Gas                $1000   1rds [MISSILE]
  ...

PENDING LOADOUT
hull: Hvy Bomber
parts: (none - keeps current loadout)
PRICE
new loadout value: $500
current ship value: $0
cost: $500
money: $100  CANNOT AFFORD
  !! cannot afford: loadout cost $500 > money $100
```

The `$n/a` launcher placeholder is gone — launcher rows show the real P441 price
(which `From<Kind>Launcher` threads from the expendable type's `launcherDef`
price, `launchertypeigc.h:77-80`). Grey-out, the cost/refund line and the
CANNOT-AFFORD verdict + reason are all driven by `ShopModel`'s pure math, proven
by the headless `ShopModelTests`/`ShopLayoutTests`.

## Tests

### Updated (fail-open → fail-closed) — only where fail-open was asserted
- `tests/Allegiance.ClientCore.MonoGame.Tests/ShopModelTests.cs` —
  `Shop_filter_degrades_to_full_catalogue_when_tech_unknown` renamed/replaced by
  `Shop_filter_fails_closed_to_no_tech_items_when_tech_unknown`: the dev-gated
  bomber hull / ER-Nanite part are excluded, and the offered set equals exactly
  the all-clear subset (never the full catalogue).
- `tests/Allegiance.ClientCore.MonoGame.Tests/ShopLauncherCatalogueTests.cs` —
  the degraded-gate assertion changed from "offers everything" to "offers only
  the all-clear launchers".

### New
- `tests/Allegiance.ClientCore.Tests/SideSwitchDevMaskLiveTests.cs` — the
  loopback wire proof (join side 0 → switch to side 1 through the real
  `TryJoinSide` path → client knows side 1's mask; shop offered set is the
  tech-correct set, not the full catalogue).
- `ShopModelTests`: `Cost_of_a_hull_swap_with_parts_including_a_launcher`,
  `Cheaper_hull_swap_yields_a_refund`, `Affordability_flag_at_the_exact_money_boundary`
  (cost == MyMoney is affordable), `Unknown_money_does_not_block_or_grey`,
  `Expensive_unowned_part_is_greyed_but_chosen_and_effective_are_not`.
- `ShopLayoutTests`: `Launcher_row_shows_a_real_price_not_n_a`,
  `Price_summary_lists_new_value_current_value_and_cost`,
  `Unaffordable_item_rows_are_greyed`, `Refused_launch_renders_visible_reason`.

(Pure `ShopModel` tests use real PCore001 prices; PCore001's `Parts` table is
entirely price-0, so the grey/price-positive fixtures use the separate real
`Launchers` table P441 priced.)

## Verification

- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`:
  **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **883/883 clean every run** (all shop,
  catalogue and layout tests, old and new; pristine master is 874, so all 9 new
  P468 tests pass).
- Loopback `SideSwitchDevMaskLiveTests`: passes with the fix; **fails pre-fix**
  (evidence above).
- Full suite `/home/domingo/.dotnet/dotnet test AllegianceCore.slnx`, **3 runs**
  (tallies below) **plus a pristine-master baseline run** (all P468 changes
  stashed) for comparison. Every failure across all four runs is a member of the
  documented pre-existing live-wire / conquest / game-over / integration timing
  flake population — the same rotating set fails on pristine master too (12
  there vs 14/11/13 in the P468 runs; P441's stable core — `ConquestTerritoryLiveTests`
  ×2–3, `GameOverPlayersWireTests` ×2, `LiveTurretGunnerBindingTests`, and the
  ClientCore `EntityTable` + `ShipDeleteGoneLiveTests.DockOut` — appears every run).
  **No failure is attributable to P468**: the P468-touched surface (all 883
  MonoGame tests + the new loopback) passed every run, and the rotating names
  also fail on pristine master.

| Run | MonoGame | ClientCore | Core | Lobby |
|---|---|---|---|---|
| 1 (P468) | 883/883 | 3 fail | 11 fail | 12/12 |
| 2 (P468) | 883/883 | 4 fail | 7 fail | 12/12 |
| 3 (P468) | 883/883 | 3 fail | 10 fail | 12/12 |
| pristine (P468 stashed) | 874/874 | 2 fail | 10 fail | 12/12 |

Rotating pre-existing failures seen across the three P468 runs (each named):
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`,
`...Flags_disabled_capture_broadcasts_nothing_and_does_not_end`,
`...Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over`;
`GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_including_losing_side`,
`...Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`;
`LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`;
`LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`;
`LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission`;
`LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side`;
`LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it`;
`CargoBayReloadTests.Reload_fuel_pack_is_accepted_and_topups_the_fuel_pool`;
`RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads`;
`FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill`;
`SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`;
`TreasureAck`/`Part_pickup` timing; `Board_refused_once_mission_over`;
`Auto_reload_relays_RELOAD129_sector_wide`. ClientCore:
`JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
`ReadyCheckLiveTests.Only_the_mission_owner_can_force_team_ready_and_it_starts_the_mission`,
`FullSession_ThreePeers_...`. (Pristine-master baseline named the same
population, incl. `LiveMoneyTests.Board_refund`, `Part_pickup_decline_...`,
`Re_target_activates_to_requester_only`, `Multi_mission_disconnect_...`,
conquest/game-over ×5, `LiveTurretGunnerBindingTests`, `EntityTable`, `DockOut`.)

## Out of scope (unchanged)

- The wallet itself and the HUD money readout (P442). Server pricing (P441).
  Side BuildingTechs (P444) — P444 has not landed; `BuilderTargetsFor` keeps its
  documented degrade behaviour (the fail-closed scope item is the hull/part/
  launcher offer).
- Investment / bucket UI. Visual redesign beyond the price column, summary line
  and greying.
