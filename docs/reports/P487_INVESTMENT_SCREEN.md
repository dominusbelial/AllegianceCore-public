# P487 — the investment screen: fund research, drones and stations (native PurchasesPane)

**Landing:** `master` (committed, not pushed). Repo:
`/home/domingo/Development/AllegianceCore`.

## Problem

The whole server-side tech loop existed — prices and tech gates (P441, P468),
research developments and side bonuses (P189, P446/P467/P478), BuildingTechs
(P444), AI constructors/drones from investments (P458, P459) — but **no human
player could invest**. P478's report ("Client display") confirmed the MonoGame
client had no research/investment panel: the docked shop (`ShopCatalog`,
`PartInfoLayout`) lists only hulls and parts. `ConnectionSession.SendBucketDonate`
existed but no UI called it; `CREATE_BUCKETS` was decoded but `BUCKET_STATUS(65)`
progress was **not applied at all** (no `case BucketStatus.Fmid` in the receive
dispatch, no `Apply(BucketStatus)` in `ClientWorld`). This was step S5 of the
economy-parity pass, unscoped until now.

## What native does (read exactly, not guessed)

- **Hangar Invest button** toggles the `ofInvestment` overlay
  (`WinTrek/hangarscreen.cpp:201-206`); the pane is `PurchasesPaneImpl`
  (`WinTrek/loadout.cpp:578+`), listing `pSide->GetBuckets()` with per-bucket
  progress (`:996,:1064`).
- **Invest** (`OnInvest`, `:807-826`): if the mission is in progress and
  `CanInvest(pBucket)`, it puts in `std::min(price − invested, wallet)` via
  `trekClient.AddMoneyToBucket` (→ `BUCKET_DONATE(64)`, `clintlib.cpp:2541-2560`).
- **Partial invest** (`OnPartial`, `:883-914`): 10% of the *total* price, capped
  by the wallet, and stepped down so it never completes the bucket (and 0 when
  only 1cr remains).
- **Show all / Hide completed** (`:829,:876`, `UpdateTechList` `:983-1040`): a
  bucket shows iff `(CanBuy || ShowAll) && !(HideCompleted && Completed)`.
- **CanInvest** (`:1095-1142`): mission in progress; has a side; `CanBuy`;
  drone-type cap (`CountDronesOfType >= nMaxDronesPerTeam` refuses); station
  build-spot/no-builder gate; `money > 0`; not 100% funded.

## Changes

### 1. Client bucket state — `BUCKET_STATUS(65)` wired through

- **`ClientWorld`** now tracks each side's per-bucket funding snapshot
  (`_bucketFundingBySideId`, keyed side → bucket → `BucketFunding` with invested
  money + invested build-time ms). New `Apply(BucketStatus)` stores it; new
  readers `BucketFundingFor(sideId)` / `BucketFundingOf(sideId, bucketId)` feed
  the pane. Registered `case BucketStatus` in the world's dispatcher **and** in
  `ConnectionSession`'s receive dispatch (`case BucketStatus.Fmid`), which was
  previously dropping the message.
- **No double-debit.** Applying bucket status never touches the wallet; the
  pane's Invest only sends `BUCKET_DONATE` — the server's `MONEY_CHANGE(60)` is
  the sole wallet writer (P442). The `BucketStatus_DoesNotTouchTheWallet` test
  pins this: a wallet set by `SET_MONEY` is unchanged after bucket-status Apply.

### 2. One source of truth for bucket ids — `BucketCatalog.Describe`

The wire `BUCKET_STATUS` carries only side + bucketId + money + time, so the
client must correlate each id back to a name/price/kind. A new
`BucketCatalog.Describe(core, initialTechs, civGa)` synthesizes the side's
bucket list with the **exact same id numbering** as the server's
`BucketCatalog.ForSide` — both now enumerate a shared private
`EnumerateDefs` (ship-type id 1, then developments from id 2 skipping obsolete
ones, then reachable drone types, then reachable station types; P478 dev GA
scaling + `c_didTeamMoney` exemption preserved). `Describe` returns
`BucketView`s (id/name/kind/price/build-time/drone-hull-id) for display; the
server path is unchanged. `BucketCatalogDescribeTests` asserts the two agree
**id-for-id** over the real PCore001 for several initial-tech masks.

`ShopCatalog` now also carries `Developments`, `DroneTypes` and the parsed
`StaticCore` (all null-degrading) so the MonoGame host can call `Describe`.

### 3. The pure investment model — `InvestmentPaneModel` (MonoGame-free)

`Rendering/InvestmentPaneModel.cs`: a headless-testable model exactly in the
`ShopModel` discipline. It holds the synthesized bucket defs, the live
`BUCKET_STATUS` funding, the wallet, and the `CanInvest` context; it computes the
native Invest / partial amounts; it applies the Show-all / Hide-completed filter.

**CanInvest fidelity (native `loadout.cpp:1095-1142`):**

| native gate | status |
|---|---|
| mission in progress | implemented (world `MissionStage.Started`) |
| has a side | implemented |
| `CanBuy(pBucket)` (≈ not completed) | implemented (`!Completed`) |
| drone-type cap `CountDronesOfType ≥ nMaxDronesPerTeam` | implemented (counts this side's ships by `HullTypeId`, cap 4) |
| station build-spot **and** no-builder | **fed by the host, default open** (named follow-up) |
| `money > 0` | implemented |
| `PercentBought ≥ 100` (already funded) | implemented |

The station-type build-spot / no-builder sub-gate is not modelled client-side
yet: it needs asteroid-capability scanning and a constructor ship's base-station
data, neither of which the client tracks (named follow-up; see below).

**Invest / partial amounts** mirror native exactly: full invest is
`min(price − invested, wallet)` (`OnInvest`); partial is 10% of the total price
capped by the wallet, never completing the bucket and 0 when 1cr remains
(`OnPartial`).

### 4. The layout — `InvestmentLayout` (reuses the docked shop renderer)

`Rendering/InvestmentLayout.cs` emits the same `ShopLayout.Row` stream the
existing `DockedScreenRenderer` already draws — **no new renderer**. Rows are
grouped by kind (DEVELOPMENTS / DRONES & SHIPS / STATIONS) in synthesis order,
each item showing name, price, invested `$X/Y`, progress %, and remaining build
time; completed buckets are `*`-marked, the cursor `>`-marked, uninvestable rows
greyed.

### 5. Dock wiring — `I` opens the investment pane

The docked screen gains an **I INVEST** key (native's Invest button / `ofInvestment`
overlay), banishing the teleport pane the way native's `OnButtonInvest` turns off
the other banishable panes. Pane keys: **SPACE** invest (full), **R** partial,
**S** show-all, **H** hide-completed, **B** back to hangar. The pane model is
rebuilt each frame from live funding/wallet/drone counts while cursor and toggles
persist (so navigation survives the rebuild). The bucket defs are synthesized once
per mission (first dock after the side's techs are known) so ids stay aligned with
the server's fixed list.

## Out of scope (honoured)

No server change. Successor upgrades (P488) and the commander/sector-map UI
untouched.

## Named follow-ups

- **`SetInvestAsteroid`** — native highlights asteroids matching a station-type
  bucket's `BuildAABM` on hover (`loadout.cpp:1396-1409`). Not cheap with the
  existing renderer (needs asteroid-capability matching + a highlight pass), so it
  is a named follow-up per the task's option.
- **Station build-spot / no-builder CanInvest gate** — fed always-open; needs
  client asteroid-capability + constructor-base tracking.
- **Native predecessor-chain list pruning** (`UpdateTechList:1009-1011`) — the
  port has no per-bucket predecessor model; omitted.
- **Mid-mission-join id caveat** — `Describe` uses the side's dev-tech mask from
  `CREATE_BUCKETS` at join, which equals the server's civ-base mask for a
  mission-start join; a join after developments already completed could in
  principle diverge ids (same caveat native itself has). Normal play (join →
  mission starts → dock) is aligned.

## Verification

### Pre-fix failure

Before P487 the investment data path did not exist: `ClientWorld` had no
`BucketFundingOf`/`Apply(BucketStatus)` and `ConnectionSession` had no
`BucketStatus.Fmid` dispatch (verified by grep — the message was dropped
silently), and neither `InvestmentPaneModel` nor `BucketCatalog.Describe`
existed. The new tests therefore exercise brand-new APIs and cannot pass on the
pre-fix tree (the funding/pane/Describe tests are red by construction on unfixed
code).

### New tests

- `tests/Allegiance.Core.Tests/BucketCatalogDescribeTests.cs` (5) — `Describe`
  matches `ForSide` id-for-id; contiguous ids from 1; id 1 is the synthetic ship
  bucket; development→drone→station group order.
- `tests/Allegiance.ClientCore.Tests/ClientWorldBucketFundingTests.cs` (3) —
  per-side/per-bucket money+time store; last-status-wins; wallet untouched by
  bucket status (no double-debit).
- `tests/Allegiance.ClientCore.MonoGame.Tests/InvestmentPaneModelTests.cs` (12) —
  every CanInvest gate; full/partial amounts; filters; group order; TryInvest/
  TryPartial routing.
- `tests/Allegiance.ClientCore.MonoGame.Tests/InvestmentLayoutTests.cs` (5) —
  headers/money/group sections; ASCII escaping; empty hint; greyed rows.

All new tests green in isolation.

### Full-suite runs (P471 runner, 3 consecutive)

Three consecutive `scripts/run-tests.sh` runs (2026-10-01). **P487's tests passed
in all three runs** (never in a failure list): `BucketCatalogDescribeTests` 5/5,
`ClientWorldBucketFundingTests` 3/3, `InvestmentPaneModelTests` 12/12,
`InvestmentLayoutTests` 5/5.

| run | Lobby | MonoGame | ClientCore | Core | failures (all pre-existing live-wire flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1077/1077 | 364/365 | 3664/3667 | `RetriedConnect`, `AutoReloadOnEmpty…RELOAD129`, `GameOverPlayers…conquest_leak`, `LiveStationRescue` |
| 2 | 12/12 | 1077/1077 | 364/365 | 3666/3667 | `FullSessionKillIntegration…RealCrossSideKill`, `InboundFloodSafety…Oversized_packet` |
| 3 | 12/12 | 1077/1077 | 365/365 | 3665/3667 | `LiveTreasurePickup…cash_treasure`, `ShipUpdateBudgetLive…dropped_one_tick` |

Every residual failure is a pre-existing live-socket timing flake that rotates
run-to-run and is unrelated to the bucket/investment path. Cited in
`docs/TEST_FLAKES.md` (the ones already in the ledger, plus four newly-observed
ones added there under "P487 session observations"): see the ledger's P471/P472/
P482 tables and the new P487-observation rows.

### Build

Whole-solution `dotnet build` (via `run-tests.sh`): **0 warnings, 0 errors** —
confirmed on every run's build step.
