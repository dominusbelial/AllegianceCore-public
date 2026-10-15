# P441 — BUY_LOADOUT is free and unchecked: charge, gate on funds, gate on tech

## Why this task exists

Domingo: "prices are not there … there is no tech development." A
gameplay-parity pass on 2026-09-29, spot-verified by the coordinator, found
that the port's purchase path ignores both money and tech. Line numbers are at
`HEAD` `d5df96ec`; re-check them, since other tasks may shift lines.

- **Native charges the difference in ship value on every loadout purchase.**
  `FedSrv.CPP:6182-6207`: `cost = -ship.GetValue()` (refund the current ship),
  then `PurchaseShipLoadout(...)`, then `cost += ship.GetValue()` (charge the new
  one). If `cost != 0`: `TrapHackBoot(money >= cost)`, `SetMoney(money - cost)`,
  and a `MONEY_CHANGE` with `dMoney = -cost, sidTo = buyer, sidFrom = NA` goes
  to the buyer's **whole side group**. `GetValue()` = hull price + Σ part
  prices (`shipIGC.h:396-406`). The ack carries
  `fBoughtEverything` and `fLaunch = fLaunch && bBoughtEverything`
  (`FedSrv.CPP:~6247-6260`).
- **Native checks tech inside the purchase.**
  `CshipIGC::PurchaseShipLoadout` (`Igc/shipIGC.h:989-1040`) aborts with no
  change if `!m_station->CanBuy(hull)` (`:999-1000`). For each part it mounts
  only if `m_station->CanBuy(part)` (`:1032`). Otherwise it may **reuse a
  matching part the ship already owned** (`:1040+`) rather than buy one.
  `CanBuy` = `RequiredTechs ⊆ (station LocalTechs | side techs)`.
- **The port does none of this.** `FM_C_BUY_LOADOUT` is dispatched at
  `Server.Dispatch.cs:495-511` → `ApplyLoadout` (`Server.ShipCommands.cs:1855`),
  which swaps the hull, calls `ship.ClearLoadout()` (`:1917`) and mounts every
  requested part. It never reads or writes `live.Wallet.Money` and never checks
  tech. The ack is built at `:1987`.
- The pieces already exist:
  - `ShipValue(ship)` = `HullType.Price + Σ Parts.Type.Price`
    (`Server.Docking.cs:1313-1314`, currently used only for boarding).
  - `StationSellsHull` (`Server.Docking.cs:1347`), the port's `CanBuy` for a
    hull, using `TechTreeBitMask.RequiredTechsBuyable`
    (`Model/TechTreeBitMask.cs:162`) against the docked station type's
    `TtbmLocal` and `Side.DevelopmentTechs` (P283).
  - The per-player wallet `PlayerWallet.Money` (`PlayerWallet.cs:51`).
  - Side-wide `MONEY_CHANGE` helpers `BroadcastMoneyChangeSideWide(sideId, dMoney,
    sidTo)`, which sends `sidFrom = NA` (`Server.Economy.cs:478`).
- **Today the client-side shop filter is the only tech gate**
  (`ShopCatalog.FilteredFor`, `ShopCatalog.cs:107-121`). It fails open, which
  is a separate task (P468). A server that trusts the client is how a player
  "buys everything".

## Scope

1. **Price the purchase the way native does.** In `ApplyLoadout`, compute
   `cost = ShipValue(after) - ShipValue(before)`. Launchers and dispensers that
   resolve through `core.TryBuildExpendableLauncher` must use their real price;
   find where the real launcher/expendable price lives and state it in the
   report. Decide the refused-purchase shape **before mutating** the ship: snapshot
   the old hull and parts, or compute the new value from the request first.
   A refused purchase must leave the ship exactly as it was.
2. **Funds gate.** If `cost > live.Wallet.Money`, refuse the purchase: the ship
   stays unchanged, the ack shows the real unchanged loadout with
   `FBoughtEverything = false` and `FLaunch = false`, and the refusal is
   logged. Native disconnects here (`TrapHackBoot`); the port's soft-fail
   convention is log-and-refuse. A negative `cost` (downgrade) refunds, as in
   native.
3. **Debit and broadcast.** On success with `cost != 0`:
   `live.Wallet.Money -= cost`, then `MONEY_CHANGE { dMoney = -cost, sidTo =
   buyer ship, sidFrom = NA }` to the buyer's side peers. Reuse
   `BroadcastMoneyChangeSideWide`, which matches native's shape. Don't write a
   new sender.
4. **Tech gate.** Hull: refuse the whole purchase if the docked station can't
   sell it (`StationSellsHull`), matching native's abort at `shipIGC.h:999-1000`.
   Parts: skip any part whose `RequiredTechsBuyable` fails against the docked
   station's `TtbmLocal` and the side's techs. Mirror native's "reuse a part the
   ship already owned" branch only if it's small; otherwise just skip, and
   document it either way. Skipped parts make `FBoughtEverything = false`. A
   non-docked ship has no station, and the existing behaviour for that case
   must be documented and kept.
5. **Debug mode.** There is **no economy precedent** for
   `liveSettings.debugMode`. Its existing uses are the lifepod timer (P378),
   the P379 spawn topology, P387 auto-start and P388 no-game-over, and none of
   them skip a price or tech rule. So **pricing and tech gating apply in debug
   mode too.** State this in the report. Don't add a new "free shop" flag here:
   that's a separate decision for Domingo. Note in the report that until P443
   (starting money) lands, a player's only income is payday. That's expected,
   not a bug.

## Explicitly out of scope

- Any client change: wallet display, shop prices, greying out unaffordable
  items, or the fail-open client filter. Those are P442 and P468.
- Starting money and station income (P443). Side BuildingTechs from owned
  stations (P444). This task uses `Side.DevelopmentTechs` exactly as
  `StationSellsHull` does today, and P444 widens it.
- Successor upgrades (`m_station->GetSuccessor`, `shipIGC.h:1004,1033`), and the
  dock auto-upgrade path (`Server.Docking.cs:~1071-1090`).
- `allegiance-mcp`/`vizfight` bot loadouts. If they buy through `BUY_LOADOUT`
  and now fail for lack of money, report it with the exact call site. Fix a
  tool only if its test breaks, by funding its wallet explicitly; don't add a
  bypass in the server.

## Existing tests

Many live tests buy loadouts with a 0-credit wallet. Examples:
`tests/Allegiance.ClientCore.MonoGame.Tests/ShopLauncherBuyLiveTests.cs`,
`DispenserEquipFireLiveTests.cs`, `ShopLauncherMountNumberingLiveTests.cs`,
`P394ShopPreSeedLiveTests.cs`, and
`tests/Allegiance.ClientCore.Tests/ClientWorldDockLoadoutTests.cs`. Fix them
by **funding the wallet explicitly** in setup: the precedent is
`LiveBucketTests.cs:218`, `...Wallet.Money = 100000`. A shared test helper is
fine. Don't weaken the server rule. List every test you touched in the report.

## Verification

- New loopback wire tests with `Bot` + in-process `Server`. Run each
  **against the unfixed code first** and record the failure in the report:
  1. Funded buyer buys a pricier hull → wallet decreases by exactly
     `ShipValue(new) - ShipValue(old)`, and a teammate `Bot` on the same side
     receives `MONEY_CHANGE { dMoney = -cost, sidTo = buyer, sidFrom = NA }`.
  2. Unfunded buyer → ship unchanged, ack `FBoughtEverything = false`, no
     `MONEY_CHANGE`.
  3. A hull the docked station can't sell (tech not met) → refused and ship
     unchanged. A tech-gated part → skipped, `FBoughtEverything = false`, and
     the other parts still mounted.
  4. Downgrade → refund (`dMoney > 0`).
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`: 0 warnings,
  0 errors. Bare `dotnet` is a broken snap stub.
- Full suite with `/home/domingo/.dotnet/dotnet test`, **3 consecutive clean
  runs**. Only the pre-existing documented live-wire timing flakes are
  acceptable, and each one seen must be named.

## Deliverables

1. The fix plus tests, committed on **`master`**, not pushed (Domingo pushes).
   Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P441_SERVER_BUY_PRICING.md` with the pre-fix
   failing-test evidence, the cost formula including the launcher price
   source, the refused-purchase shape, the part-reuse decision, the debug-mode
   decision, and every existing test you funded.
