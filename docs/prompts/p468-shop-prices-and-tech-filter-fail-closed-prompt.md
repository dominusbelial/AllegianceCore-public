# P468 — the shop shows no prices and its tech filter fails open

## Why this task exists

This was split out of P442 (client wallet) to fit one session. It assumes
P442 has landed and `ClientWorld.MyMoney` exists; if it hasn't, stop and say
so. Line numbers are at `HEAD` `d5df96ec`; re-check them.

- **No prices anywhere in the docked shop.** `ShopModel` (`Rendering/ShopModel.cs`),
  `ShopLayout` and `DockedScreenRenderer.Draw` (`DockedScreenRenderer.cs:63`)
  list hulls, parts and launchers by name only. Native's loadout screen shows
  each item's price and the running cost of the new loadout against the
  player's money. Its purchase cost is `newShip.GetValue() -
  oldShip.GetValue()`, where `GetValue` = hull price + Σ part prices
  (`FedSrv.CPP:6182-6195`, `shipIGC.h:396-406`). Since P441 the server
  refuses a purchase the player can't afford, so a client that can't show
  cost produces silent refusals.
- **The client tech filter fails open.** `ShopCatalog.FilteredFor` returns
  the **full unfiltered catalogue** whenever the side's development mask is
  unknown (`ShopCatalog.cs:107-109`, "degrades to the full catalogue … so a
  missing datum never blanks the shop"). The mask is unknown in real play:
  - `CREATE_BUCKETS(191)`, the only full-mask source at join, is sent only in
    `CompleteJoinAndLaunch` and only if the ship already has a side
    (`Server.Lobby.cs:~2347-2383`).
  - `TryJoinSide` (`Server.Lobby.cs:1669`) never re-sends it on a side switch.
  - `SIDE_TECH_CHANGE(75)` goes only to the side's own peers
    (`Server.Economy.cs:~738-746`).
  - The client stores the mask keyed by `MySideId` (`ClientWorld.cs:1777-1781`,
    and `:419` `DevelopmentTechsFor`).
  So after a side switch (fixed on the client by P438), `DevelopmentTechsFor(newSide)`
  is null and the player is offered **everything**. The join ordering itself is
  fine: the own-ship EXPORT carrying the side is sent before CREATE_BUCKETS on
  the same connection (`Server.Lobby.cs:~2258-2270` vs `:~2377`).

## Scope

1. **Server: send the new side's development mask on a side change.** In the
   `TryJoinSide` success path, send the joiner a `CREATE_BUCKETS` for its new
   side, using the same construction as `CompleteJoinAndLaunch`. If simpler,
   send a `SIDE_TECH_CHANGE` for that side to that one peer instead. Also send
   the new side's `BUCKET_STATUS`es if CompleteJoinAndLaunch does. Check
   native for what a side-joiner receives (`FedSrv.CPP` / `fsmission.cpp`
   `AddPlayerToSide` → `SendMissionInfo`) and cite it.
2. **Client: fail closed.** When the side's mask is unknown, the docked shop
   offers only items with **no** tech requirement (`TtbmRequired` all-clear),
   or shows a clear "waiting for tech data" state. Pick one, and never offer
   the full catalogue. Update `FilteredFor`'s doc comment and its tests.
3. **Prices in the shop.** Show each hull/part/launcher row's real price, and
   a summary line: new loadout value, current ship value, **cost =
   new − current** (negative = refund) and `MyMoney`. Rows the player can't
   afford (the cost of choosing them would exceed `MyMoney`) are visibly
   greyed. Confirming an unaffordable total is blocked client-side with a
   visible reason. Keep the pure-model/renderer split: pricing math in
   `ShopModel` (unit-testable, no graphics device), drawing in the renderer.
   Use the real launcher/expendable price source that P441 identified
   (see `docs/reports/P441_SERVER_BUY_PRICING.md`).

## Explicitly out of scope

- The wallet itself and the HUD money readout (P442). Server pricing (P441).
  Side BuildingTechs (P444). If P444 has landed, feed the union exactly as it
  does, without changing it.
- Investment/bucket UI.
- Visual redesign of the shop beyond the price column, summary line and
  greying.

## Verification

- Loopback wire test with a real `ConnectionSession`/`ClientWorld` + `Server`.
  Run it **against the unfixed code first** and record the failure: a player
  joins side 0 then switches to side 1 through the real `TryJoinSide` path,
  and afterwards the client knows side 1's development mask. The docked
  shop's offered set equals the fail-closed/tech-correct set, not the full
  catalogue.
- Pure `ShopModel` tests: the cost of a hull swap with parts (including a
  launcher), the refund case, and the affordability flag at the exact boundary
  (`cost == MyMoney` is affordable).
- Existing `ShopCatalog`/`ShopModel` tests updated only where the fail-open
  behaviour is asserted. List each one.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`: 0 warnings,
  0 errors. Bare `dotnet` is a broken snap stub.
- Full suite with `/home/domingo/.dotnet/dotnet test`, **3 consecutive clean
  runs**. Only the pre-existing documented live-wire timing flakes are
  acceptable, and each one seen must be named.

## Deliverables

1. The change plus tests, committed on **`master`**, not pushed (Domingo
   pushes). Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P468_SHOP_PRICES_TECH_FAIL_CLOSED.md` with the
   pre-fix failing-test evidence, what a native side-joiner receives, the
   fail-closed choice, and a screenshot or text description of the price
   column and summary line.
