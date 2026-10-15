# P487 — the investment screen: players can fund research, drones and stations (native PurchasesPane)

## Why this task exists

The whole server-side tech loop exists now:
- prices and tech gates (P441, P468);
- research developments and side bonuses (P189, P446/P467/P478);
- BuildingTechs from owned stations (P444);
- AI constructors and drones spawned from investments (P458, P459).

**But no human player can invest.** P478's report
(`docs/reports/P478_SIDE_GA_DEVELOPMENT_COST_TIME.md`, "Client display")
confirmed that the MonoGame client has **no research/investment panel**: the
docked shop (`ShopCatalog`, `PartInfoLayout`) lists only hulls and parts.
`ConnectionSession.SendBucketDonate` exists
(`src/Allegiance.ClientCore/Session/ConnectionSession.cs:280`) but no UI
calls it. `CREATE_BUCKETS` is decoded (`ClientWorld.Apply(CreateBuckets)`,
`ClientWorld.cs:~2330`). Check whether `BUCKET_STATUS(65)` progress is
applied at all. This was step S5 of the 2026-09-29 economy parity pass, and
it was never scoped until now.

**Native** is at `~/Development/Allegiance/src/`:
- The hangar's **Invest** button toggles the investment overlay
  (`WinTrek/hangarscreen.cpp:201-206`, `ofInvestment`).
- The pane is `PurchasesPaneImpl` (`WinTrek/loadout.cpp:578+`). It lists the
  side's buckets (`pSide->GetBuckets()`, `:996`, `:1064`) with each one's
  progress.
  - **Invest** puts in `min(price − invested, wallet)` via
    `trekClient.AddMoneyToBucket` (`:807-827`). That debits locally and
    sends `BUCKET_DONATE` (`clintlib/clintlib.cpp:2541-2560`).
  - There's a **partial** invest on right-click (`OnPartial`, `:883+`).
  - **Show all / Hide completed** filters (`:829`, `:876`).
  - `CanInvest` (`:1095+`): the mission is in progress, the player has a
    side, `pSide->CanBuy(pBucket)`, and the drone-type cap on how many of
    one drone type can exist.
  - Hovering a station-type bucket highlights the matching asteroid class
    (`SetInvestAsteroid`, `:1396-1409`).

## Scope

1. **Client bucket state.** Make sure `ClientWorld` tracks each bucket's
   current invested money and time from `BUCKET_STATUS(65)`, plus
   completion. Add the handler if it's missing. The P442 wallet must not be
   double-debited: the server's MONEY_CHANGE is the source, per P442's
   documented decision.
2. **The investment pane.** Open it from the docked UI with an Invest key or
   button, mirroring the hangar's. Reuse the existing docked-shop rendering
   style and pure-layout discipline (`ShopLayout`/`PartInfoLayout`). It must:
   - list the side's buckets, grouped by kind (development, drone, station
     type), with name, price, invested amount and progress, and remaining
     build time;
   - offer **Invest**, which puts in `min(remaining, wallet)` through
     `SendBucketDonate`, plus native's partial invest;
   - support Show-all / Hide-completed;
   - apply native's `CanInvest` gate, including the drone-type cap. Grey out
     or hide what native does, and say which.
3. **The station-type asteroid hint** (`SetInvestAsteroid`) only if it's
   cheap with the existing renderer. Otherwise make it a named follow-up.

## Explicitly out of scope

- Any server change. The server handlers exist (P198/P441), so if the UI
  exposes a server bug, report it with evidence.
- Successor upgrades (**P488**).
- The commander/sector-map UI.

## Verification

- Every fix has a test that fails on unfixed code first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P487_INVESTMENT_SCREEN.md`.
