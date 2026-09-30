# P441 — BUY_LOADOUT is free and unchecked: charge, gate on funds, gate on tech

Date: 2026-09-29 · Landed on `master` (not pushed; Domingo pushes) · Branch `master`

## Summary

`FM_C_BUY_LOADOUT` → `Server.ApplyLoadout` previously swapped the hull, cleared
the loadout and mounted every requested part with **no price, no funds check
and no tech check** — a player could "buy everything" for free and the client's
fail-open shop filter (`ShopCatalog.FilteredFor`) was the only thing standing
between a player and any piece of equipment. This task prices the purchase the
way native does, gates it on the buyer's personal wallet, and gates hull + parts
on the docked station's tech (`CanBuy`), matching `FedSrv.CPP:6182-6260` and
`CshipIGC::PurchaseShipLoadout` (`shipIGC.h:989-1040`).

Files changed:

- `src/Allegiance.Server/Server.ShipCommands.cs` — `ApplyLoadout` rewritten:
  docked-station tech gate, projection pass, pricing, funds gate, debit/refund +
  side-wide `MONEY_CHANGE`, refused-purchase ack.
- `src/Allegiance.Core/Data/DataPartTypeIGC.cs` — launcher/dispenser parts now
  carry their **real** price (see "Launcher price source").
- New test `tests/Allegiance.Core.Tests/BuyLoadoutPricingLiveTests.cs` — 5 live
  loopback wire tests (Bot + in-process Server).
- Existing tests funded/adapted (list at bottom).

## Pre-fix failing-test evidence

The five new live tests were written first and run against the **unfixed** code
(`d5df96ec` era). All five failed, each for a reason that demonstrates the
missing gates:

| Test | Pre-fix failure |
|---|---|
| `Funded_Paid_Purchase_Charges_Exact_Delta_And_Broadcasts_SideWide` | `buyer never saw the 1 debit MONEY_CHANGE (money=1001012)` — no debit, no `MONEY_CHANGE`. |
| `Unfunded_Buyer_Refused_ShipUnchanged_NoMoneyChange` | `Expected: 1 Actual: 245` — the 0-wallet buy succeeded (hull changed) instead of refusing. |
| `Unsellable_Hull_Refused_ShipUnchanged` | `Expected: 14 Actual: 1` — a tech-gated hull was granted instead of refused. |
| `TechGated_Part_Skipped_Sellable_Part_Still_Mounted` | `mate never saw the sellable-part debit` — no debit/broadcast at all. |
| `Downgrade_Refunds_Buyer` | `buyer never saw the +20000 refund MONEY_CHANGE` — no refund. |

(The `money=1001012` in the first row is the pre-existing P197 payday crediting
the wallet during the long pre-fix pump — see "Cost figures and payday" below.)

## Design decisions (each required by the task, with rationale)

### 1. Cost formula — price the purchase the way native does

```
cost = ShipValue(after) - ShipValue(before)
     = (hull.Price + Σ mounted-part prices) - (oldHull.Price + Σ old-part prices)
```

`ShipValue(ship) = ship.HullType.Price + Σ ship.Parts[].Type.Price`
(the existing `Server.Docking.cs:1313` helper — native `CshipIGC::GetValue`,
`shipIGC.h:396-406`). The cost is computed from the **projected actually-mounted
part set**, so a part that is skipped (tech / hardpoint / not-in-core) is never
charged, and a refusal leaves the ship untouched.

### 2. Launcher price source (the `TryBuildExpendableLauncher` case)

Native `ClauncherTypeIGC::GetPrice()` returns `m_launcherDef->price`
(`launchertypeigc.h:77-80`), where `launcherDef` is the `DataBuyableIGC` base
embedded in the **expendable** type the launcher fires (`igc.h:1939-1945`,
`1947-1958` — `DataExpendableTypeIGC.launcherDef` carries the buyable scalars).
There is no separate price on the compact `DataLauncherTypeIGC` record
(`DataLauncherTypeIGC.cs:19-25` — amount/partId/successor/launchCount/
expendableTypeId/inventoryLineMdl only).

The port decodes that same `launcherDef.price` as the expendable type's `Price`
field — `DataMissileTypeIGC.Price` (`DataMissileTypeIGC.cs:36`, at byte 64, the
start of the embedded `launcherDef`) and the analogous `Price` on
`DataMineTypeIGC` / `DataChaffTypeIGC` / `DataProbeTypeIGC`. Previously the
launcher bridge `DataPartTypeIGC.FromLauncherDef` hard-coded `Price: 0`; it now
threads the real expendable `Price` through every `From<Kind>Launcher` method.
So a mounted launcher part's `Type.Price` (which `ShipValue` sums) is the real
launcher price, not 0. The P441 test proves it: the funded-buyer test's delta
includes the launcher's real price.

### 3. Refused-purchase shape (decided BEFORE mutating)

A projection pass resolves every requested part and computes the cost *before*
the ship mutates, so a refusal cannot half-apply a purchase. On any refusal the
ship is left exactly as it was and the server sends an ack that reflects the
**real, unchanged loadout**:

```
HullId = ship.HullType.HullId   (the unchanged hull)
FBoughtEverything = false
FLaunch = false
Parts  = ship.Parts (real mounted parts, unchanged)
```

Native disconnects here via `TrapHackBoot`; the port's established soft-fail
convention is log-and-refuse (a `[loadout] … REFUSED` line), and no
`MONEY_CHANGE` is sent.

### 4. Funds gate

If `cost > live.Wallet.Money`, refuse (shape above). A negative `cost`
(downgrade) refunds, as in native. On success with `cost != 0`:

```
live.Wallet.Money -= cost;                    // refund when cost < 0
BroadcastMoneyChangeSideWide(sideId, -cost, ship.Id);   // dMoney = -cost, sidTo = buyer, sidFrom = NA
```

`BroadcastMoneyChangeSideWide` (`Server.Economy.cs:478`) already sends the
side-wide `MONEY_CHANGE` with `sidFrom = NA` — the exact native shape
(`FedSrv.CPP:6195-6207`). No new sender was written. The actual cost is
recomputed from the really-mounted parts after the apply (equal to the projected
cost unless a 255-part ceiling skip occurs).

### 5. Tech gate

- **Hull:** only when the requested hull *changes* (`hullChanged`) does a docked
  ship's station gate it — matching native's `ploadout->hullID != pht->GetObjectID()`
  guard (`shipIGC.h:996`), below which no hull purchase occurs and no `CanBuy`
  runs. A docked ship whose station can't sell a *different* requested hull
  refuses the whole purchase (`!StationSellsHull(stationType, side, hull)`,
  port of native `CanBuy` abort at `shipIGC.h:999-1000`).
- **Parts:** any requested part whose `RequiredTechsBuyable(TtbmRequired,
  station.TtbmLocal, side.DevelopmentTechs)` fails is **skipped**, making
  `FBoughtEverything = false`; the other parts still mount. (Native
  `shipIGC.h:1034,1042`.)
- `StationSellsHull` / `RequiredTechsBuyable` are the existing P283/P284 helpers
  and use `Side.DevelopmentTechs` exactly as they do today (P444 will widen the
  side-tech term). This is unchanged by P441.

#### Part-reuse decision

Native's "reuse a part the ship already owned" salvage branch
(`shipIGC.h:1040+`) — where a station that can't *sell* a part will instead
re-mount a matching part the ship still owns — is **deliberately NOT mirrored**.
Mirroring it would reintroduce per-part bookkeeping and, because `ApplyLoadout`
clears the loadout first, complicate the value equation for a marginal gameplay
gain. The port simply skips the non-buyable part. This is documented here and in
a comment at the gate; it does not affect the funds/tech behaviour the task
specifies.

### 6. Debug mode

There is **no economy precedent** for `liveSettings.debugMode` skipping a price
or tech rule: its existing uses are the lifepod timer (P378), P379 spawn
topology, P387 auto-start and P388 no-game-over. So **pricing and tech gating
apply in debug mode too** — there is no "free shop" flag, and one is not added
here (a separate decision for Domingo). Until P443 (starting money) lands, a
player's only income is payday — that is expected, not a bug.

### 7. Non-docked ships — documented, existing behaviour kept

Pricing + funds are **unconditional** (a `BUY_LOADOUT` that moves money is always
paid for). The tech gate needs a station, so it only runs when the ship is
docked at a resolvable station type; a non-docked ship has **no station to
consult** and keeps its existing ungated behaviour for hull/parts (no tech
refusal), which is documented here and in the code. Empirically this matters for
testing: PCore001's base-tech garrison sells only **price-0** starter equipment
(all six garrison-sellable hulls and all fifteen garrison-sellable parts are
free), so a *positive-cost* purchase in live tests requires either an undocked
buyer (no tech gate) or a fully-developed side — see below.

## Verification

New live wire tests (`tests/Allegiance.Core.Tests/BuyLoadoutPricingLiveTests.cs`)
run against the fixed code:

1. **Funded paid purchase** (undocked buyer) → wallet falls by exactly
   `ShipValue(new) - ShipValue(old)` (incl. a real paid launcher when one
   exists), and a same-side teammate receives `MONEY_CHANGE{dMoney=-cost,
   sidTo=buyer, sidFrom=NA}`.
2. **Unfunded buyer** → refused: ship unchanged, no `MONEY_CHANGE`.
3a. **Unsellable hull** (docked) → refused, ship unchanged.
3b. **Tech-gated part** (docked) → skipped (`FBoughtEverything=false`), sellable
    part still mounted.
4. **Downgrade** → refund (`dMoney > 0`), wallet increases.

Result tallies (whole-solution build 0 warnings / 0 errors):

- `Allegiance.Core.Tests` — three consecutive full runs of the final code:
  8/9/8 failures, **none attributable to P441** (BuyLoadoutPricingLiveTests passes
  every run). The failures are all pre-existing on pristine `master`
  (verified by stashing P441's changes and re-running): the 5 stable
  conquest/game-over tests (`GameOverPlayersWireTests` ×2,
  `ConquestTerritoryLiveTests` ×3) and `LiveTurretGunnerBindingTests`, plus the
  pre-existing documented live-wire timing flakes that vary run to run
  (observed: `LiveMoneyTests.Board_refund`, `LiveChatSectorWireTests`,
  `WarpBombTests`, `LiveRipcordTests`, all of which pass in isolation).
- `Allegiance.Lobby.Tests` — 12/12 clean.
- `Allegiance.ClientCore.MonoGame.Tests` — 868/868 clean.
- `Allegiance.ClientCore.Tests` — 302/305: `ThreePeerSeededSessionTests`
  (flake, passes in isolation) + `ShipDeleteGoneLiveTests.DockOut` +
  `JoinFlowLiveTests.EntityTable` (both verified pre-existing on pristine
  master, unrelated to P441).

So P441 introduces **zero** new test failures; the baseline already carried the
named pre-existing failures above, which are outside this task's scope.

### Bot tools call sites (out of scope, reported per brief)

`allegiance-mcp` (`tools/allegiance-mcp/BotManager.cs`) and `vizfight`
(`tools/vizfight/Program.cs`), plus the proof consoles
(`tools/hullswap-proof`, `tools/weapon-status-proof`, `tools/serversplit`,
`tools/roster-capture`, `tools/warp-navigate-bot`) all send `BUY_LOADOUT`
(`Bot.SendBuyLoadout`) for their bots. They are console/tool projects with **no
automated tests in the solution**, so no tool test breaks. Operational note:
after P441, any of these bots that buys a **paid** item must be funded in its
wallet (as the live tests now are); they must NOT gain a server-side bypass —
fund the wallet explicitly in the tool.

### Cost figures and payday

P197 payday credits the player wallet every 60 sim-seconds, so the P441 tests
**stop payday** (set `Mission.NextPaydayTime` far-future via reflection) and
capture the wallet value immediately before the send, making the exact
debit/refund wallet assertions deterministic. All prices are read from the live
core at run time, never hard-coded.

### Live harness note

Each P441 live test spins an in-process server + two real bots. Because the two
bots seat in **non-deterministic order** (async logon), each test identifies its
buyer/mate ship by `Bot.ShipAssignment` **after** seating — never by the
spawner's invocation index (an index-based `Pair.Buyer`/`Pair.Mate` caused
~50% flakes where the test operated on the wrong ship).

## Existing tests touched (funded / adapted for P441)

The server rule is **not weakened**; these tests arrange a legal purchase:

- `tests/Allegiance.Core.Tests/BuyLoadoutPricingLiveTests.cs` — new (5 tests).
- `tests/Allegiance.Core.Tests/HullSwapLiveTests.cs` — both tests now spawn the
  ship **undocked** (their subject is swap mechanics, not shop legality) and fund
  the wallet; test 1's in-place proof is now `Assert.Same(ship, s)`.
- `tests/Allegiance.ClientCore.MonoGame.Tests/ShopLauncherBuyLiveTests.cs` —
  fund + develop-all-tech.
- `tests/Allegiance.ClientCore.MonoGame.Tests/ShopLauncherMountNumberingLiveTests.cs` — fund.
- `tests/Allegiance.ClientCore.MonoGame.Tests/DispenserEquipFireLiveTests.cs` — fund + develop-all-tech.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P394ShopPreSeedLiveTests.cs` — reviewed; purchases are price-0/sellable so it passes unmodified (see notes).
- `tests/Allegiance.ClientCore.Tests/HullSwapClientLiveTests.cs` — fund + develop-all-tech (both tests).
- `tests/Allegiance.ClientCore.Tests/FullSessionIntegrationTests.cs` — fund + develop-all-tech.
- `tests/Allegiance.ClientCore.Tests/FullSessionKillIntegrationTests.cs` — fund (buyer is flying/undocked; no tech gate).
- `tests/Allegiance.ClientCore.Tests/DockLoadoutLiveTests.cs` — reviewed: same-hull (cost 0) / price-0 part purchases; passes unmodified.

"develop-all-tech" sets `Side.DevelopmentTechs` to an all-bits mask on every
side — a fully-researched late-game side whose garrison legitimately sells the
advanced hulls/parts the shop tests buy. This simulates tech development
(P283's `StationSells` consults `DevelopmentTechs`) **without touching the server
rule**; it is not a server bypass.

## Out of scope (per brief, unchanged)

- Client wallet display / shop prices / grey-out / fail-open filter (P442, P468).
- Starting money and station income (P443); BuildingTechs from owned stations (P444).
- Successor upgrades and the dock auto-upgrade path.
- `allegiance-mcp` / `vizfight` bot loadouts: if any now fail for lack of money,
  fund their wallet explicitly in that tool — **no server bypass**.
